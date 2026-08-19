using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Security.AccessControl;
using System.Security;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Contracts;
using StorageChronicle.Settings;
using StorageChronicle.Storage;

namespace StorageChronicle.Agent;

/// <summary>Handles versioned local IPC requests for the desktop UI and session agent.</summary>
public sealed class NamedPipeAgentServer : BackgroundService
{
    /// <summary>Stable local-only pipe name.</summary>
    public const string PipeName = "StorageChronicle.Agent";
    private readonly IProjectionService projection;
    private readonly AppendOnlyStorageEngine store;
    private readonly AgentSettingsService? settings;
    private readonly AgentHealthState health;
    private readonly IEventNormalizer normalizer;
    private readonly IConfirmedReconciliationRunner? reconciliationRunner;
    private readonly ConcurrentDictionary<string, byte> clipboardDedup = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> clipboardDedupOrder = new();
    private const int ClipboardDedupCapacity = 4096;

    /// <summary>Initializes the named pipe server.</summary>
    public NamedPipeAgentServer(IProjectionService projection, AppendOnlyStorageEngine store, AgentHealthState health, IEventNormalizer normalizer, AgentSettingsService? settings = null, IMonitoringLifecycle? monitoringLifecycle = null, IConfirmedReconciliationRunner? reconciliationRunner = null)
    {
        this.projection = projection ?? throw new ArgumentNullException(nameof(projection));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.health = health ?? throw new ArgumentNullException(nameof(health));
        this.normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        this.settings = settings;
        this.reconciliationRunner = reconciliationRunner;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await using var pipe = CreatePipe();
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                await ServeConnectionAsync(pipe, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // A disconnected client is isolated; the Agent remains available for the next connection.
            }
            catch (UnauthorizedAccessException)
            {
                // A client that cannot be authenticated is isolated; the Agent remains available.
            }
            catch (InvalidOperationException)
            {
                // A client identity or pipe state can become invalid during disconnect.
            }
            catch (Win32Exception)
            {
                // A transient Windows pipe/identity failure must not stop the service host.
            }
        }
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        NamedPipeClientIdentity identity;
        try
        {
            identity = NamedPipeClientIdentity.Read(pipe);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or SecurityException)
        {
            await WriteEnvelopeAsync(pipe, IpcProtocol.Create("Error", new AgentHealth("Rejected", "The Agent could not authenticate the named-pipe client.", store.Status.LastSequence, Array.Empty<VolumeHealth>(), Array.Empty<PendingReconciliationRequest>())), cancellationToken).ConfigureAwait(false);
            return;
        }
        IpcClientHello hello;
        try
        {
            var helloFrame = await ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (helloFrame is null) return;
            var helloEnvelope = new LengthPrefixedJsonCodec().Decode<IpcEnvelope>(helloFrame, IpcProtocol.Major);
            if (!string.Equals(helloEnvelope.MessageType, "ClientHello", StringComparison.Ordinal))
            {
                await WriteEnvelopeAsync(pipe, Rejected("The named-pipe client must authenticate its role before sending requests."), cancellationToken).ConfigureAwait(false);
                return;
            }

            hello = IpcProtocol.Read<IpcClientHello>(helloEnvelope);
            if (!IsAllowedClientRole(hello, identity))
            {
                await WriteEnvelopeAsync(pipe, Rejected("The named-pipe client role is not permitted for its authenticated process or session."), cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or ArgumentException)
        {
            await WriteEnvelopeAsync(pipe, Rejected("The named-pipe client role handshake was invalid."), cancellationToken).ConfigureAwait(false);
            return;
        }

        while (pipe.IsConnected && !cancellationToken.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(pipe, cancellationToken).ConfigureAwait(false);
            if (frame is null) return;
            IpcEnvelope request;
            try
            {
                request = new LengthPrefixedJsonCodec().Decode<IpcEnvelope>(frame, IpcProtocol.Major);
            }
            catch (InvalidDataException)
            {
                await WriteEnvelopeAsync(pipe, IpcProtocol.Create("Error", new AgentHealth("ProtocolError", "Invalid IPC frame or protocol version.", store.Status.LastSequence, Array.Empty<VolumeHealth>(), Array.Empty<PendingReconciliationRequest>())), cancellationToken).ConfigureAwait(false);
                return;
            }

            if ((hello.Role == IpcClientRole.SessionAgent && !string.Equals(request.MessageType, "ClipboardCandidate", StringComparison.Ordinal)) ||
                (hello.Role == IpcClientRole.DesktopUi && string.Equals(request.MessageType, "ClipboardCandidate", StringComparison.Ordinal)))
            {
                await WriteEnvelopeAsync(pipe, Rejected("The requested IPC message is not permitted for the authenticated client role."), cancellationToken).ConfigureAwait(false);
                return;
            }

            var response = await DispatchAsync(request, identity, cancellationToken).ConfigureAwait(false);
            if (response is not null) await WriteEnvelopeAsync(pipe, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask<IpcEnvelope?> DispatchAsync(IpcEnvelope request, NamedPipeClientIdentity identity, CancellationToken cancellationToken)
    {
        if (request.MessageType == "ProjectionPageRequest")
        {
            var value = IpcProtocol.Read<ProjectionPageRequest>(request);
            if (projection is AgentProjectionService agent)
            {
                return IpcProtocol.Create("ProjectionPageResponse", await agent.GetEventStackTreeAsync(value, cancellationToken).ConfigureAwait(false));
            }

            var page = await projection.GetEventStackAsync(value.Mode, value.Page, value.PageSize, cancellationToken).ConfigureAwait(false);
            return IpcProtocol.Create("ProjectionPageResponse", new ProjectionPageResponse(page.Items, page.Page, page.PageSize, page.TotalCount, page.HasMore));
        }

        if (request.MessageType == "DiffProjectionRequest")
        {
            var value = IpcProtocol.Read<DiffProjectionRequest>(request);
            if (projection is AgentProjectionService agent)
            {
                return IpcProtocol.Create("DiffProjectionResponse", await agent.GetDiffProjectionAsync(value, cancellationToken).ConfigureAwait(false));
            }

            var rows = await projection.GetDiffAsync(value.FromUtc, value.ToUtc, value.Mode, cancellationToken).ConfigureAwait(false);
            return IpcProtocol.Create("DiffProjectionResponse", new DiffProjectionResponse(rows));
        }

        if (request.MessageType == "AgentHealthRequest")
        {
            return IpcProtocol.Create("AgentHealth", health.Snapshot(store.Status, markPendingPresented: true));
        }

        if (request.MessageType == "ReconciliationDecision")
        {
            if (!identity.IsCurrentUserSession) return Rejected("Reconciliation decisions must originate from the interactive user session.");
            var value = IpcProtocol.Read<ReconciliationDecision>(request);
            if (!health.TryGetPending(value.RequestId, out var pendingRequest)) return Rejected("The reconciliation request is no longer pending.");
            if (value.Execute)
            {
                health.TryResolve(pendingRequest.RequestId);
                if (reconciliationRunner is null) return Rejected("Confirmed reconciliation is unavailable.");
                var summary = await reconciliationRunner.ExecuteAsync(pendingRequest, cancellationToken).ConfigureAwait(false);
                if (!summary.Completed) return IpcProtocol.Create("AgentHealth", health.Snapshot(store.Status));
                return IpcProtocol.Create("AgentHealth", health.Snapshot(store.Status));
            }

            await AppendDeclinedGapAsync(pendingRequest, cancellationToken).ConfigureAwait(false);
            health.TryResolve(pendingRequest.RequestId);
            return IpcProtocol.Create("AgentHealth", health.Snapshot(store.Status));
        }

        if (request.MessageType == "EventDetailsRequest")
        {
            var value = IpcProtocol.Read<EventDetailsRequest>(request);
            var details = projection is AgentProjectionService agent
                ? await agent.GetDetailsAsync(value.EventId, cancellationToken).ConfigureAwait(false)
                : null;
            return IpcProtocol.Create("EventDetailsResponse", new EventDetailsResponse(details));
        }

        if (request.MessageType == "SettingsSnapshotRequest")
        {
            if (settings is null) return Rejected("Settings endpoint is unavailable.");
            var machine = settings.LoadMachineSettings();
            var user = settings.LoadUserSettings();
            return IpcProtocol.Create("SettingsSnapshot", new SettingsSnapshot(ToJson(machine.Settings), ToJson(user.Settings), machine.Warning, user.Warning));
        }

        if (request.MessageType == "SettingsUpdateRequest")
        {
            if (settings is null) return Rejected("Settings endpoint is unavailable.");
            if (!identity.IsCurrentUserSession) return Rejected("Settings requests must originate from the interactive user session.");
            var value = IpcProtocol.Read<SettingsUpdateRequest>(request);
            if (value.Scope == SettingsScope.Machine && !identity.IsAdministrator) return Rejected("Machine settings require an administrator token.");
            using var authorization = SettingsAuthorizationContext.Enter(identity.IsAdministrator);
            var result = await ApplySettingsAsync(value, cancellationToken).ConfigureAwait(false);
            return IpcProtocol.Create("SettingsApplyResult", ToJson(result));
        }

        if (request.MessageType == "ClipboardCandidate" && !identity.IsCurrentUserSession)
        {
            return IpcProtocol.Create("Error", new AgentHealth("Rejected", "Clipboard candidate came from an unexpected session.", store.Status.LastSequence, Array.Empty<VolumeHealth>(), Array.Empty<PendingReconciliationRequest>()));
        }

        if (request.MessageType == "ClipboardCandidate")
        {
            var candidate = IpcProtocol.Read<ClipboardCandidateRequest>(request);
            if (candidate.Generation <= 0 || candidate.SourceSequence <= 0) return Rejected("Clipboard candidate generation and sequence must be positive.");
            if (candidate.Paths.Count > 256 || candidate.Paths.Any(path => string.IsNullOrWhiteSpace(path) || path.Length > 32_768) || candidate.Paths.Sum(path => (long)path.Length) > 1_048_576) return Rejected("Clipboard candidate exceeded the bounded path contract.");
            var dedupKey = $"{identity.Sid}:{candidate.Generation}:{candidate.SourceSequence}";
            if (!clipboardDedup.TryAdd(dedupKey, 0)) return IpcProtocol.Create("ClipboardAccepted", health.Snapshot(store.Status));
            clipboardDedupOrder.Enqueue(dedupKey);
            while (clipboardDedup.Count > ClipboardDedupCapacity && clipboardDedupOrder.TryDequeue(out var oldest)) clipboardDedup.TryRemove(oldest, out _);
            var properties = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            for (var index = 0; index < candidate.Paths.Count; index++) properties[$"clipboardPath.{index}"] = candidate.Paths[index];
            properties["clipboardGeneration"] = candidate.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture);
            properties["clipboardIntent"] = candidate.IsCut ? "Cut" : "Copy";
            var source = new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.Clipboard, null, null, null, null, null, null, null,
                new EventTime(candidate.ObservedUtc, candidate.ObservedUtc.Offset, candidate.ObservedUtc, DateTimeOffset.UtcNow, new SourceSequence(candidate.SourceSequence), new MountSequence(candidate.SourceSequence)),
                Enum.TryParse<EventQuality>(candidate.Quality, true, out var quality) ? quality : EventQuality.Unknown, null, ProcessAttributionQuality.Unknown, null,
                candidate.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture), properties.ToImmutable());
            var canonical = normalizer.Normalize(source);
            if (canonical is not null)
            {
                await store.AppendSourceAsync(source, cancellationToken).ConfigureAwait(false);
                await store.AppendCanonicalAsync(canonical, cancellationToken).ConfigureAwait(false);
                await store.ApplyAsync(canonical, cancellationToken).ConfigureAwait(false);
            }
            return IpcProtocol.Create("ClipboardAccepted", health.Snapshot(store.Status));
        }

        return Rejected($"Unsupported message type: {request.MessageType}");
    }

    private static bool IsAllowedClientRole(IpcClientHello hello, NamedPipeClientIdentity identity) =>
        hello.SessionId == identity.SessionId &&
        identity.IsCurrentUserSession &&
        hello.Role switch
        {
            IpcClientRole.DesktopUi => !identity.IsSessionAgentProcess,
            IpcClientRole.SessionAgent => identity.IsSessionAgentProcess,
            _ => false
        };

    private async ValueTask<SettingsApplyResult> ApplySettingsAsync(SettingsUpdateRequest request, CancellationToken cancellationToken)
    {
        using var document = request.Settings.ValueKind == JsonValueKind.Undefined
            ? throw new InvalidDataException("Settings payload is empty.")
            : JsonDocument.Parse(request.Settings.GetRawText());
        if (settings is null) throw new InvalidOperationException("Settings endpoint is unavailable.");
        if (request.Scope == SettingsScope.Machine)
        {
            var value = document.RootElement.Deserialize<MachineSettings>() ?? throw new InvalidDataException("Machine settings payload is invalid.");
            return await settings.ApplyMachineSettingsAsync(value, cancellationToken).ConfigureAwait(false);
        }

        var user = document.RootElement.Deserialize<UserSettings>() ?? throw new InvalidDataException("User settings payload is invalid.");
        return await settings.ApplyUserSettingsAsync(user, cancellationToken).ConfigureAwait(false);
    }

    private IpcEnvelope Rejected(string reason) => IpcProtocol.Create("Error", new AgentHealth("Rejected", reason, store.Status.LastSequence, Array.Empty<VolumeHealth>()));

    private async ValueTask AppendDeclinedGapAsync(PendingReconciliationRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var properties = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        properties["reconciliationRequestId"] = request.RequestId;
        properties["reconciliationDecision"] = "Declined";
        properties["userDeclined"] = "true";
        properties["reconciliationReason"] = request.Reason;
        properties["uncertainFromUtc"] = (request.GapStartUtc ?? request.DiscoveredUtc).ToUniversalTime().ToString("O");
        properties["uncertainToUtc"] = now.ToUniversalTime().ToString("O");
        var source = new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.DirectoryReconciliation,
            request.VolumeId, null, null, null, null, CanonicalOperation.UnverifiedGap, null,
            new EventTime(now, now.Offset, null, now, new SourceSequence(Math.Max(1, request.SourceSequence ?? 1)), new MountSequence(Math.Max(1, request.SourceSequence ?? 1))),
            EventQuality.UnverifiedGap, null, ProcessAttributionQuality.Unknown, null, null, properties.ToImmutable());
        var canonical = normalizer.Normalize(source) ?? throw new InvalidDataException("A declined reconciliation gap could not be normalized.");
        await store.AppendSourceAsync(source, cancellationToken).ConfigureAwait(false);
        await store.AppendCanonicalAsync(canonical, cancellationToken).ConfigureAwait(false);
        await store.ApplyAsync(canonical, cancellationToken).ConfigureAwait(false);
        health.Observe(source, createPendingReconciliation: false);
    }

    private static JsonElement ToJson<T>(T value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static JsonElement ToJson(SettingsApplyResult value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static async ValueTask<byte[]?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        var headerRead = await ReadAtMostAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0) return null;
        if (headerRead != header.Length) throw new InvalidDataException("IPC frame header is truncated.");
        var length = BitConverter.ToInt32(header, 0);
        if (length is < 0 or > LengthPrefixedJsonCodec.MaximumPayloadBytes) throw new InvalidDataException("IPC payload length is invalid.");
        var frame = new byte[sizeof(int) + length];
        header.CopyTo(frame, 0);
        if (length > 0 && await ReadAtMostAsync(stream, frame.AsMemory(sizeof(int), length), cancellationToken).ConfigureAwait(false) != length) throw new InvalidDataException("IPC frame payload is truncated.");
        return frame;
    }

    private static async ValueTask WriteEnvelopeAsync(Stream stream, IpcEnvelope envelope, CancellationToken cancellationToken)
    {
        var frame = new LengthPrefixedJsonCodec().Encode(envelope, IpcProtocol.Major, IpcProtocol.Minor);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<int> ReadAtMostAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 16, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }
}

/// <summary>Captures the authenticated SID and Windows session of a named-pipe client.</summary>
public sealed record NamedPipeClientIdentity(string Sid, int SessionId, bool IsCurrentUserSession, bool IsAdministrator)
{
    /// <summary>Indicates that the authenticated client process is the published Session Agent executable.</summary>
    public bool IsSessionAgentProcess { get; init; }

    /// <summary>Reads client identity while impersonating the connected pipe client.</summary>
    public static NamedPipeClientIdentity Read(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        string sid = string.Empty;
        var isAdministrator = false;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                sid = identity.User?.Value ?? string.Empty;
                isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            });
        }
        catch (SecurityException)
        {
            // Unknown identity remains non-administrative and cannot use mutating endpoints.
        }
        catch (InvalidOperationException)
        {
            // The pipe may be disconnected between connect and impersonation.
        }
        catch (IOException)
        {
            // Keep the identity empty; session-sensitive endpoints will be rejected.
        }
        catch (Win32Exception)
        {
            // Keep the identity empty; session-sensitive endpoints will be rejected.
        }
        var processId = 0u;
        _ = NativePipeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out processId);
        var sessionId = -1;
        var isSessionAgentProcess = false;
        try
        {
            if (processId != 0)
            {
                using var process = System.Diagnostics.Process.GetProcessById((int)processId);
                sessionId = process.SessionId;
                isSessionAgentProcess = string.Equals(Path.GetFileName(process.MainModule?.FileName), "StorageChronicle.SessionAgent.exe", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        var activeSession = NativePipeMethods.WTSGetActiveConsoleSessionId();
        if (activeSession == uint.MaxValue) activeSession = (uint)System.Diagnostics.Process.GetCurrentProcess().SessionId;
        return new NamedPipeClientIdentity(sid, sessionId, sessionId >= 0 && sessionId == (int)activeSession, isAdministrator)
        {
            IsSessionAgentProcess = isSessionAgentProcess
        };
    }
}

internal static partial class NativePipeMethods
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern uint WTSGetActiveConsoleSessionId();
}
