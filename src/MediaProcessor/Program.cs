using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text.Json;
using MediaProcessor;
using MediaProcessor.Services;

var builder = WebApplication.CreateBuilder(args);

// Register application services
builder.Services.AddSingleton<GroupParticipantRegistry>();
builder.Services.AddSingleton<SpeechTranslationService>();
builder.Services.AddSingleton<TextToSpeechService>();
builder.Services.AddSingleton<CaptionPublisher>();
builder.Services.AddSingleton<CallAutomationHandler>();

// Register the background worker (as singleton so it can be injected into endpoints)
builder.Services.AddSingleton<Worker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Worker>());

var app = builder.Build();

// Warm up TTS synthesizers in the background so the first real request is fast
_ = Task.Run(async () =>
{
    var tts = app.Services.GetRequiredService<TextToSpeechService>();
    var languages = builder.Configuration.GetSection("Speech:TargetLanguages").Get<string[]>() ?? ["en", "fr", "es"];
    await tts.WarmupAsync(languages);
});

// Track active group call connections to prevent duplicate joins
var activeGroupCalls = new ConcurrentDictionary<string, string>();
var registry = app.Services.GetRequiredService<GroupParticipantRegistry>();

// Serialize bot-join per group to prevent race conditions (two clients joining simultaneously)
var joinLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

// Global deduplication: track ACS participant raw IDs that already have an active processing session.
// Prevents duplicate recognizer sessions if ACS opens multiple WebSocket connections.
var activeParticipantSessions = new ConcurrentDictionary<string, (Pipe Pipe, Task ProcessingTask, CancellationTokenSource Cts, string SessionId)>();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "MediaProcessor" }));

// Join a group call and start media streaming for translation
app.MapPost("/api/join", async (HttpContext context, CallAutomationHandler handler, ILogger<Program> logger) =>
{
    using var doc = await JsonDocument.ParseAsync(context.Request.Body);
    var groupId = doc.RootElement.GetProperty("groupId").GetString();
    var sourceLanguage = doc.RootElement.TryGetProperty("sourceLanguage", out var srcProp)
        ? srcProp.GetString() : null;
    var participantId = doc.RootElement.TryGetProperty("participantId", out var pidProp)
        ? pidProp.GetString() : null;
    var acsUserId = doc.RootElement.TryGetProperty("acsUserId", out var uidProp)
        ? uidProp.GetString() : null;

    if (string.IsNullOrWhiteSpace(groupId))
        return Results.BadRequest(new { error = "groupId is required" });

    // Register the participant's language
    if (!string.IsNullOrWhiteSpace(participantId) && !string.IsNullOrWhiteSpace(sourceLanguage))
        registry.AddParticipant(groupId, participantId, sourceLanguage, acsUserId);

    logger.LogInformation("Join request received for group call {GroupId} (source={Source}, participant={Participant}, activeLanguages={Languages})",
        groupId, sourceLanguage ?? "auto", participantId ?? "unknown",
        string.Join(",", registry.GetActiveLanguageCodes(groupId)));

    // Only connect the bot to the call if it isn't already connected for this group.
    // Use a per-group lock to prevent race conditions when two clients join simultaneously.
    string callConnectionId;
    var joinLock = joinLocks.GetOrAdd(groupId, _ => new SemaphoreSlim(1, 1));
    await joinLock.WaitAsync();
    try
    {
        if (activeGroupCalls.TryGetValue(groupId, out var existingConnId))
        {
            logger.LogInformation("Bot already connected to group {GroupId} ({ConnId}), skipping re-join", groupId, existingConnId);
            callConnectionId = existingConnId;
        }
        else
        {
            callConnectionId = await handler.JoinGroupCallAsync(groupId);
            activeGroupCalls[groupId] = callConnectionId;
        }
    }
    finally
    {
        joinLock.Release();
    }

    return Results.Ok(new { callConnectionId, groupId });
});

// ACS Call Automation webhook — receives call events (IncomingCall, CallConnected, etc.)
// Mapped to both "/" and "/api/callbacks" because ACS may post to the base callback URL directly.
app.MapPost("/", HandleCallAutomationEvent);
app.MapPost("/api/callbacks", HandleCallAutomationEvent);

async Task<IResult> HandleCallAutomationEvent(HttpContext context, CallAutomationHandler handler, Worker worker, ILogger<Program> logger)
{
    var requestBody = await new StreamReader(context.Request.Body).ReadToEndAsync();
    logger.LogInformation("Received callback event: {Body}", requestBody[..Math.Min(requestBody.Length, 500)]);

    // Handle Event Grid subscription validation handshake
    using var doc = JsonDocument.Parse(requestBody);
    if (doc.RootElement.ValueKind == JsonValueKind.Array)
    {
        var firstEvent = doc.RootElement[0];
        if (firstEvent.TryGetProperty("data", out var data) &&
            data.TryGetProperty("validationCode", out var validationCode))
        {
            logger.LogInformation("Event Grid validation request — returning validationCode");
            return Results.Ok(new { validationResponse = validationCode.GetString() });
        }
    }

    // Parse and handle Cloud Events from ACS Call Automation
    var callEvent = Azure.Communication.CallAutomation.CallAutomationEventParser.Parse(requestBody, "application/json");

    if (callEvent is null)
    {
        // May be an Event Grid IncomingCall event (not a Call Automation event)
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            var firstEvent = doc.RootElement[0];

            // Check for Event Grid validation handshake (IncomingCall)
            if (firstEvent.TryGetProperty("eventType", out var eventType) &&
                eventType.GetString() == "Microsoft.Communication.IncomingCall" &&
                firstEvent.TryGetProperty("data", out var eventData) &&
                eventData.TryGetProperty("incomingCallContext", out var callContext))
            {
                logger.LogInformation("IncomingCall event received — answering call");
                var callConnectionId = await handler.AnswerCallAsync(callContext.GetString()!);
                logger.LogInformation("Call answered with CallConnectionId: {CallConnectionId}", callConnectionId);
                return Results.Ok();
            }

            // Handle CallDisconnected — clean up active group calls so rejoin works
            if (firstEvent.TryGetProperty("type", out var typeElement))
            {
                var eventTypeStr = typeElement.GetString();
                if (eventTypeStr == "Microsoft.Communication.CallDisconnected" &&
                    firstEvent.TryGetProperty("data", out var disconnectData) &&
                    disconnectData.TryGetProperty("callConnectionId", out var connIdElement))
                {
                    var disconnectedConnId = connIdElement.GetString();
                    logger.LogInformation("Call disconnected: {CallConnectionId}", disconnectedConnId);
                    var entry = activeGroupCalls.FirstOrDefault(kvp => kvp.Value == disconnectedConnId);
                    if (entry.Key != null)
                    {
                        activeGroupCalls.TryRemove(entry.Key, out _);
                        logger.LogInformation("Removed group call {GroupId} from active calls", entry.Key);
                    }
                    return Results.Ok();
                }

                // Handle ParticipantsUpdated — clean up departed participants from registry
                if (eventTypeStr == "Microsoft.Communication.ParticipantsUpdated" &&
                    firstEvent.TryGetProperty("data", out var puData) &&
                    puData.TryGetProperty("participants", out var participantsArr) &&
                    participantsArr.ValueKind == JsonValueKind.Array)
                {
                    var presentRawIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var p in participantsArr.EnumerateArray())
                    {
                        if (p.TryGetProperty("identifier", out var identifier) &&
                            identifier.TryGetProperty("rawId", out var rawIdProp))
                        {
                            var rawId = rawIdProp.GetString();
                            if (!string.IsNullOrEmpty(rawId))
                                presentRawIds.Add(rawId);
                        }
                    }

                    // Find participants in our session tracker that are no longer in the call
                    foreach (var trackedRawId in activeParticipantSessions.Keys)
                    {
                        if (!presentRawIds.Contains(trackedRawId))
                        {
                            logger.LogInformation("Participant {RawId} left the call — cleaning up session and registry", trackedRawId);
                            registry.RemoveByAcsUserId(trackedRawId);
                            if (activeParticipantSessions.TryRemove(trackedRawId, out var session))
                            {
                                await session.Pipe.Writer.CompleteAsync();
                                await session.Cts.CancelAsync();
                            }
                        }
                    }

                    return Results.Ok();
                }

                logger.LogInformation("Unhandled event type: {EventType}", eventTypeStr);
            }
        }

        return Results.Ok();
    }

    logger.LogInformation("Call Automation event: {EventType}", callEvent.GetType().Name);

    if (callEvent is Azure.Communication.CallAutomation.MediaStreamingStarted streamingStarted)
    {
        logger.LogInformation("Media streaming started for call {CallConnectionId}",
            streamingStarted.CallConnectionId);
    }
    else if (callEvent is Azure.Communication.CallAutomation.MediaStreamingStopped streamingStopped)
    {
        logger.LogInformation("Media streaming stopped for call {CallConnectionId}",
            streamingStopped.CallConnectionId);
    }
    else if (callEvent is Azure.Communication.CallAutomation.CallDisconnected disconnected)
    {
        logger.LogInformation("Call disconnected: {CallConnectionId}", disconnected.CallConnectionId);
        // Remove from active calls so the group can be rejoined
        var entry = activeGroupCalls.FirstOrDefault(kvp => kvp.Value == disconnected.CallConnectionId);
        if (entry.Key != null)
        {
            activeGroupCalls.TryRemove(entry.Key, out _);
            logger.LogInformation("Removed group call {GroupId} from active calls", entry.Key);
        }
    }

    return Results.Ok();
}

// ACS media streaming WebSocket endpoint — receives real-time audio data
// With Unmixed audio, each chunk includes a participantRawId so we can run
// a separate recognizer per speaker with the correct source language.
app.MapGet("/api/mediastream", async (HttpContext context, Worker worker, ILogger<Program> logger) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var wsId = Guid.NewGuid().ToString()[..8];
    logger.LogInformation("[WS-{WsId}] WebSocket media stream connection accepted (Unmixed mode)", wsId);
    var webSocket = await context.WebSockets.AcceptWebSocketAsync();

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    var latestGroup = activeGroupCalls.Keys.LastOrDefault();

    // Track which participant sessions THIS WebSocket connection created (for cleanup)
    var ownedParticipants = new HashSet<string>();
    // Track participants we've already examined and determined to skip (unknown or already active)
    var skippedParticipants = new HashSet<string>();

    var buffer = new byte[65536];
    long audioChunkCount = 0;
    try
    {
        while (webSocket.State == System.Net.WebSockets.WebSocketState.Open)
        {
            var result = await webSocket.ReceiveAsync(buffer, cts.Token);
            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                break;

            if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
            {
                // Handle multi-frame messages
                int totalBytes = result.Count;
                while (!result.EndOfMessage)
                {
                    var continuation = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer, totalBytes, buffer.Length - totalBytes), cts.Token);
                    totalBytes += continuation.Count;
                }

                using var doc = JsonDocument.Parse(buffer.AsMemory(0, totalBytes));
                var root = doc.RootElement;

                if (!root.TryGetProperty("kind", out var kind))
                {
                    logger.LogWarning("[WS-{WsId}] WebSocket text message without 'kind': {Json}",
                        wsId, System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Min(totalBytes, 200)));
                    continue;
                }

                var kindStr = kind.GetString();

                if (kindStr == "AudioData" &&
                    root.TryGetProperty("audioData", out var audioData) &&
                    audioData.TryGetProperty("data", out var data))
                {
                    // Extract participantRawId for unmixed audio
                    var participantRawId = audioData.TryGetProperty("participantRawID", out var pidProp)
                        ? pidProp.GetString() ?? "unknown"
                        : "unknown";

                    // Already determined to skip this participant (unknown or handled by another WS)
                    if (skippedParticipants.Contains(participantRawId))
                        continue;

                    // If we haven't seen this participant yet, try to create a session
                    if (!ownedParticipants.Contains(participantRawId))
                    {
                        // Check if another WebSocket connection already owns this participant
                        if (activeParticipantSessions.ContainsKey(participantRawId))
                        {
                            logger.LogInformation(
                                "[WS-{WsId}] Participant {RawId} already has an active session — skipping (another WS owns it)",
                                wsId, participantRawId);
                            skippedParticipants.Add(participantRawId);
                            continue;
                        }

                        var resolved = registry.ResolveAcsUser(participantRawId);
                        if (resolved == null)
                        {
                            logger.LogWarning("[WS-{WsId}] Audio from unregistered ACS participant {RawId} — skipping", wsId, participantRawId);
                            skippedParticipants.Add(participantRawId);
                            continue;
                        }

                        var (sourceLang, participantId) = resolved.Value;
                        var sessionId = Guid.NewGuid().ToString();
                        var pipe = new Pipe();
                        var participantCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                        var processingTask = worker.ProcessAudioSessionAsync(
                            sessionId, latestGroup, sourceLang, participantId,
                            pipe.Reader.AsStream(), participantCts.Token);

                        // Atomically register the session globally — if another WS beat us, skip
                        if (!activeParticipantSessions.TryAdd(participantRawId, (pipe, processingTask, participantCts, sessionId)))
                        {
                            logger.LogInformation(
                                "[WS-{WsId}] Lost race creating session for {RawId} — another WS owns it",
                                wsId, participantRawId);
                            await participantCts.CancelAsync();
                            await pipe.Writer.CompleteAsync();
                            skippedParticipants.Add(participantRawId);
                            continue;
                        }

                        ownedParticipants.Add(participantRawId);
                        logger.LogInformation(
                            "[WS-{WsId}] Started translation session {SessionId} for ACS participant {RawId} (source={Lang}, participant={Pid})",
                            wsId, sessionId, participantRawId, sourceLang, participantId);
                    }

                    // Write audio to the participant's pipe
                    if (activeParticipantSessions.TryGetValue(participantRawId, out var session))
                    {
                        var pcmBytes = Convert.FromBase64String(data.GetString()!);
                        await session.Pipe.Writer.WriteAsync(pcmBytes, cts.Token);
                        audioChunkCount++;
                        if (audioChunkCount <= 3 || audioChunkCount % 500 == 0)
                        {
                            logger.LogDebug("[WS-{WsId}] Audio chunk #{Count} — {Bytes} PCM bytes from {Participant}",
                                wsId, audioChunkCount, pcmBytes.Length, participantRawId);
                        }
                    }
                }
                else if (kindStr == "AudioMetadata")
                {
                    logger.LogDebug("[WS-{WsId}] Audio metadata: {Json}",
                        wsId, System.Text.Encoding.UTF8.GetString(buffer, 0, totalBytes));
                }
                else
                {
                    logger.LogWarning("[WS-{WsId}] Unknown WebSocket message kind '{Kind}'", wsId, kindStr);
                }
            }
            else if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Binary)
            {
                logger.LogInformation("[WS-{WsId}] Binary WebSocket frame: {Bytes} bytes (unexpected in Unmixed mode)", wsId, result.Count);
            }
        }
    }
    catch (OperationCanceledException) { }
    finally
    {
        // Only clean up sessions THIS WebSocket connection created
        foreach (var rawId in ownedParticipants)
        {
            if (activeParticipantSessions.TryRemove(rawId, out var session))
            {
                await session.Pipe.Writer.CompleteAsync();
                await session.Cts.CancelAsync();
            }
            registry.RemoveByAcsUserId(rawId);
        }

        await cts.CancelAsync();

        // Wait for our processing tasks to finish
        foreach (var rawId in ownedParticipants)
        {
            // The session was already removed above, but we can still await the task
            // since we captured it in the TryRemove
        }
    }

    logger.LogInformation("[WS-{WsId}] WebSocket media stream closed ({Chunks} total audio chunks)", wsId, audioChunkCount);
});

app.UseWebSockets();
app.Run();
