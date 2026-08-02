using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using StorageChronicle.Contracts;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
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

    /// <summary>Initializes the named pipe server.</summary>
    public NamedPipeAgentServer(IProjectionService projection, AppendOnlyStorageEngine store, AgentSettingsService? settings = null)
    {
        this.projection = projection ?? throw new ArgumentNullException(nameof(projection));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.settings = settings;
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
        }
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var identity = NamedPipeClientIdentity.Read(pipe);
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
                await WriteEnvelopeAsync(pipe, IpcProtocol.Create("Error", new AgentHealth("ProtocolError", "Invalid IPC frame or protocol version.", store.Status.LastSequence, Array.Empty<VolumeHealth>())), cancellationToken).ConfigureAwait(false);
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
            var page = await projection.GetEventStackAsync(value.Mode, value.Page, value.PageSize, cancellationToken).ConfigureAwait(false);
            return IpcProtocol.Create("ProjectionPageResponse", new ProjectionPageResponse(page.Items, page.Page, page.PageSize, page.TotalCount, page.HasMore));
        }

        if (request.MessageType == "DiffProjectionRequest")
        {
            var value = IpcProtocol.Read<DiffProjectionRequest>(request);
            var rows = await projection.GetDiffAsync(value.FromUtc, value.ToUtc, value.Mode, cancellationToken).ConfigureAwait(false);
            return IpcProtocol.Create("DiffProjectionResponse", new DiffProjectionResponse(rows));
        }

        if (request.MessageType == "AgentHealthRequest")
        {
            return IpcProtocol.Create("AgentHealth", new AgentHealth(store.Status.State.ToString(), store.Status.Reason, store.Status.LastSequence, Array.Empty<VolumeHealth>()));
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
            return IpcProtocol.Create("Error", new AgentHealth("Rejected", "Clipboard candidate came from an unexpected session.", store.Status.LastSequence, Array.Empty<VolumeHealth>()));
        }

        return Rejected($"Unsupported message type: {request.MessageType}");
    }

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
    /// <summary>Reads client identity while impersonating the connected pipe client.</summary>
    public static NamedPipeClientIdentity Read(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        string sid = string.Empty;
        var isAdministrator = false;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            sid = identity.User?.Value ?? string.Empty;
            isAdministrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        });
        var processId = 0u;
        _ = NativePipeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out processId);
        var sessionId = -1;
        try { if (processId != 0) sessionId = System.Diagnostics.Process.GetProcessById((int)processId).SessionId; } catch (ArgumentException) { }
        return new NamedPipeClientIdentity(sid, sessionId, sessionId == System.Diagnostics.Process.GetCurrentProcess().SessionId, isAdministrator);
    }
}

internal static partial class NativePipeMethods
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);
}
