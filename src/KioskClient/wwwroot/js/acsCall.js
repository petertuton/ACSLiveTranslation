// ACS Calling SDK interop
// The ACS Calling SDK is loaded via CDN in the HTML.
// NOTE: For production, prefer installing via npm (@azure/communication-calling,
// @azure/communication-common) and bundling with a JS bundler.

let callAgent = null;
let call = null;
let localVideoStream = null;
let deviceManager = null;
let dotNetRef = null;

window.acsCall = {
    initialize: async function (token, displayName) {
        const Calling = window.AzureCommunicationCalling;
        const Common = window.AzureCommunicationCommon;

        if (!Calling || !Common) {
            console.error('ACS Calling SDK not loaded. Ensure the CDN scripts are included.');
            return false;
        }

        const CallClient = Calling.CallClient;
        const AzureCommunicationTokenCredential = Common.AzureCommunicationTokenCredential;

        try {
            const callClient = new CallClient();
            const tokenCredential = new AzureCommunicationTokenCredential(token);
            callAgent = await callClient.createCallAgent(tokenCredential, { displayName });
            deviceManager = await callClient.getDeviceManager();
            await deviceManager.askDevicePermission({ audio: true, video: true });
            return true;
        } catch (err) {
            console.error('Failed to initialize ACS:', err);
            return false;
        }
    },

    joinCall: async function (groupId, dotNetObjRef) {
        if (!callAgent) return null;

        dotNetRef = dotNetObjRef;

        try {
            const cameras = await deviceManager.getCameras();

            if (cameras.length > 0) {
                localVideoStream = new window.AzureCommunicationCalling.LocalVideoStream(cameras[0]);
            }

            const joinOptions = {
                videoOptions: localVideoStream ? { localVideoStreams: [localVideoStream] } : undefined,
                audioOptions: { muted: false }
            };

            call = callAgent.join({ groupId }, joinOptions);

            // Mute incoming call audio — the user doesn't need to hear the raw
            // call mix (which includes their own voice).  All translated speech
            // comes through Web PubSub TTS instead.
            try {
                await call.muteIncomingAudio();
            } catch (e) {
                console.warn('Could not mute incoming call audio:', e);
            }

            // Handle remote participants
            call.on('remoteParticipantsUpdated', (e) => {
                e.added.forEach(p => {
                    subscribeToRemoteParticipant(p);
                    notifyParticipantAdded(p);
                });
                e.removed.forEach(p => notifyParticipantRemoved(p));
            });

            // Subscribe to existing participants
            call.remoteParticipants.forEach(p => {
                subscribeToRemoteParticipant(p);
                notifyParticipantAdded(p);
            });

            return call.id;
        } catch (err) {
            console.error('Failed to join call:', err);
            return null;
        }
    },

    hangUp: async function () {
        if (call) {
            await call.hangUp();
            call = null;
            localVideoStream = null;
        }
        dotNetRef = null;
    },

    toggleMute: async function () {
        if (call) {
            if (call.isMuted) {
                await call.unmute();
            } else {
                await call.mute();
            }
            return call.isMuted;
        }
        return false;
    },

    toggleVideo: async function () {
        if (call && localVideoStream) {
            const isOn = call.localVideoStreams.length > 0;
            if (isOn) {
                await call.stopVideo(localVideoStream);
            } else {
                await call.startVideo(localVideoStream);
            }
            return !isOn;
        }
        return false;
    },

    /**
     * Plays base64-encoded MP3 audio (TTS) through the browser speakers.
     * Keeps at most one queued clip — newer translations replace stale ones
     * so the audio doesn't fall further and further behind the live speech.
     */
    playAudio: (function () {
        let pending = null;   // at most ONE next clip
        let playing = false;

        async function playClip(base64) {
            playing = true;
            try {
                const binary = atob(base64);
                const bytes = new Uint8Array(binary.length);
                for (let i = 0; i < binary.length; i++) {
                    bytes[i] = binary.charCodeAt(i);
                }
                const blob = new Blob([bytes], { type: 'audio/mpeg' });
                const url = URL.createObjectURL(blob);
                const audio = new Audio(url);
                await new Promise((resolve) => {
                    audio.onended = resolve;
                    audio.onerror = resolve;
                    audio.play().catch(resolve);
                });
                URL.revokeObjectURL(url);
            } catch (err) {
                console.error('Failed to play TTS audio:', err);
            }
            playing = false;
            // If a newer clip arrived while we were playing, play it now
            if (pending) {
                const next = pending;
                pending = null;
                playClip(next);
            }
        }

        return function (base64Audio) {
            if (!playing) {
                playClip(base64Audio);
            } else {
                // Replace any previously queued clip — only the latest matters
                pending = base64Audio;
            }
        };
    })()
};

function subscribeToRemoteParticipant(participant) {
    participant.on('videoStreamsUpdated', (e) => {
        e.added.forEach(stream => renderRemoteStream(stream, participant));
    });
    participant.videoStreams.forEach(stream => renderRemoteStream(stream, participant));
}

async function renderRemoteStream(stream, participant) {
    if (stream.isAvailable) {
        try {
            const renderer = new window.AzureCommunicationCalling.VideoStreamRenderer(stream);
            const view = await renderer.createView({ scalingMode: 'Crop' });
            const container = document.getElementById('remote-videos');
            if (container) {
                const participantId = participant.identifier?.communicationUserId || 'unknown';
                // Remove existing video for this participant
                const existing = document.getElementById('video-' + participantId);
                if (existing) existing.remove();

                const wrapper = document.createElement('div');
                wrapper.className = 'remote-video';
                wrapper.id = 'video-' + participantId;
                wrapper.appendChild(view.target);
                container.appendChild(wrapper);

                // Hide the placeholder when there's at least one remote video
                const placeholder = document.getElementById('remote-placeholder');
                if (placeholder) placeholder.style.display = 'none';
            }
        } catch (err) {
            console.error('Failed to render remote stream:', err);
        }
    }
}

function notifyParticipantAdded(participant) {
    if (dotNetRef) {
        const id = participant.identifier?.communicationUserId || 'unknown';
        const name = participant.displayName || 'Participant';
        dotNetRef.invokeMethodAsync('OnRemoteParticipantAdded', id, name);
    }
}

function notifyParticipantRemoved(participant) {
    if (dotNetRef) {
        const id = participant.identifier?.communicationUserId || 'unknown';
        dotNetRef.invokeMethodAsync('OnRemoteParticipantRemoved', id);

        // Remove the video element for this participant
        const existing = document.getElementById('video-' + id);
        if (existing) existing.remove();

        // Show placeholder again if no remote videos left
        const container = document.getElementById('remote-videos');
        const placeholder = document.getElementById('remote-placeholder');
        if (container && placeholder && container.querySelectorAll('.remote-video').length === 0) {
            placeholder.style.display = '';
        }
    }
}

window.scrollCaptionsToBottom = function () {
    const el = document.getElementById('caption-container');
    if (el) {
        el.scrollTop = el.scrollHeight;
    }
};
