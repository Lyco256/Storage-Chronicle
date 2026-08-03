using System.Text.Json;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Settings;

namespace StorageChronicle.UI.Shared;

/// <summary>Adapts the Agent's versioned settings IPC endpoint to the UI settings gateway.</summary>
/// <remarks>The desktop UI never reads machine or user settings files directly.</remarks>
public sealed class AgentPipeSettingsGateway : IAgentSettingsGateway
{
    private readonly AgentPipeProjectionClient client;
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Initializes a settings gateway over an existing projection-pipe client.</summary>
    public AgentPipeSettingsGateway(AgentPipeProjectionClient client) => this.client = client ?? throw new ArgumentNullException(nameof(client));

    /// <inheritdoc />
    public SettingsLoadResult<MachineSettings> LoadMachineSettings()
    {
        var snapshot = LoadSnapshot();
        return new(snapshot.Machine.Deserialize<MachineSettings>(jsonOptions) ?? throw new InvalidDataException("Agent returned empty machine settings."), snapshot.MachineWarning is not null, false, snapshot.MachineWarning);
    }

    /// <inheritdoc />
    public SettingsLoadResult<UserSettings> LoadUserSettings()
    {
        var snapshot = LoadSnapshot();
        return new(snapshot.User.Deserialize<UserSettings>(jsonOptions) ?? throw new InvalidDataException("Agent returned empty user settings."), snapshot.UserWarning is not null, false, snapshot.UserWarning);
    }

    /// <inheritdoc />
    public async ValueTask<SettingsApplyResult> ApplyMachineSettingsAsync(MachineSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return await ApplyAsync(SettingsScope.Machine, settings, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<SettingsApplyResult> ApplyUserSettingsAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return await ApplyAsync(SettingsScope.User, settings, cancellationToken).ConfigureAwait(false);
    }

    private SettingsSnapshot LoadSnapshot()
    {
        var response = client.SendRequestAsync("SettingsSnapshotRequest", new SettingsSnapshotRequest()).AsTask().GetAwaiter().GetResult();
        return IpcProtocol.Read<SettingsSnapshot>(response);
    }

    private async ValueTask<SettingsApplyResult> ApplyAsync<T>(SettingsScope scope, T settings, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToElement(settings, jsonOptions);
        var response = await client.SendRequestAsync("SettingsUpdateRequest", new SettingsUpdateRequest(scope, json), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<SettingsApplyResult>(response.Payload.GetRawText(), jsonOptions)
            ?? throw new InvalidDataException("Agent returned an empty settings apply result.");
    }
}
