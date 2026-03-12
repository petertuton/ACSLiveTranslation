// Entry point for esbuild - exposes ACS SDK globals for browser use
import { CallClient, LocalVideoStream, VideoStreamRenderer } from '@azure/communication-calling';
import { AzureCommunicationTokenCredential } from '@azure/communication-common';

window.AzureCommunicationCalling = {
    CallClient,
    LocalVideoStream,
    VideoStreamRenderer
};

window.AzureCommunicationCommon = {
    AzureCommunicationTokenCredential
};
