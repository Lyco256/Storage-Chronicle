using System.Text.Json;
using StorageChronicle.Agent;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Normalization;
using StorageChronicle.Settings;
using StorageChronicle.Storage;
using Xunit;

namespace StorageChronicle.Agent.Tests;

public sealed class AuthenticatedUserSettingsRoutingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SettingsIpcUsesAuthenticatedSidAndIgnoresSpoofedSidAndPathFields()
    {
        var fixtureRoot = AgentTestFixtureOwnership.CreateTempRoot("StorageChronicle.UserSettingsRouting", out var runId);
        try
        {
            const string firstSid = "S-1-5-21-100-200-300-1001";
            const string secondSid = "S-1-5-21-100-200-300-1002";
            var firstProfile = Path.Combine(fixtureRoot, "first", "AppData", "Local");
            var secondProfile = Path.Combine(fixtureRoot, "second", "AppData", "Local");
            var resolver = new WindowsAuthenticatedUserSettingsStoreResolver(new FixedProfileResolver(new Dictionary<string, string>
            {
                [firstSid] = firstProfile,
                [secondSid] = secondProfile
            }));
            var settingsService = new AgentSettingsService(
                new FixedSettingsStore<MachineSettings>(new MachineSettings()),
                new UnscopedUserSettingsStore(),
                new RecordingHistory(),
                new AllowAllAgentSettingsAuthorizer(),
                new NoOpMonitoringLifecycle());
            await using var eventStore = new AppendOnlyStorageEngine(new StorageEngineOptions(Path.Combine(fixtureRoot, "history")) { FlushInterval = TimeSpan.FromMinutes(1) });
            using var server = new NamedPipeAgentServer(new AgentProjectionService(eventStore), eventStore, new AgentHealthState(), new EventNormalizer(), settings: settingsService, userSettingsStoreResolver: resolver);

            var firstIdentity = new NamedPipeClientIdentity(firstSid, 11, true, false);
            var secondIdentity = new NamedPipeClientIdentity(secondSid, 12, true, false);
            var firstUpdate = new UserSettings { EventStackPageSize = 125 };
            var firstApply = await server.DispatchAsync(
                IpcProtocol.Create("SettingsUpdateRequest", new SettingsUpdateRequest(SettingsScope.User, JsonSerializer.SerializeToElement(firstUpdate))),
                firstIdentity,
                TestContext.Current.CancellationToken);
            Assert.Equal("SettingsApplyResult", firstApply!.MessageType);
            Assert.True(firstApply.Payload.Deserialize<SettingsApplyResult>(JsonOptions)!.Succeeded);

            var spoofedPayload = JsonSerializer.SerializeToElement(new
            {
                Scope = SettingsScope.User,
                Settings = JsonSerializer.SerializeToElement(new UserSettings { EventStackPageSize = 175 }),
                Sid = secondSid,
                Path = secondProfile
            });
            var spoofedApply = await server.DispatchAsync(IpcProtocol.Create("SettingsUpdateRequest", spoofedPayload), firstIdentity, TestContext.Current.CancellationToken);
            Assert.Equal("SettingsApplyResult", spoofedApply!.MessageType);
            Assert.True(spoofedApply.Payload.Deserialize<SettingsApplyResult>(JsonOptions)!.Succeeded);

            var firstSnapshot = await ReadUserSettingsAsync(server, firstIdentity);
            var secondSnapshot = await ReadUserSettingsAsync(server, secondIdentity);
            Assert.Equal(175, firstSnapshot.EventStackPageSize);
            Assert.Equal(250, secondSnapshot.EventStackPageSize);
            Assert.True(File.Exists(Path.Combine(firstProfile, "Storage Chronicle", "user-settings.json")));
            Assert.False(File.Exists(Path.Combine(secondProfile, "Storage Chronicle", "user-settings.json")));
        }
        finally
        {
            AgentTestFixtureOwnership.DeleteTempRoot(fixtureRoot, "StorageChronicle.UserSettingsRouting", runId);
        }
    }

    private static async Task<UserSettings> ReadUserSettingsAsync(NamedPipeAgentServer server, NamedPipeClientIdentity identity)
    {
        var response = await server.DispatchAsync(
            IpcProtocol.Create("SettingsSnapshotRequest", new SettingsSnapshotRequest()),
            identity,
            TestContext.Current.CancellationToken);
        Assert.Equal("SettingsSnapshot", response!.MessageType);
        return IpcProtocol.Read<SettingsSnapshot>(response).User.Deserialize<UserSettings>(JsonOptions)!;
    }

    private sealed class FixedProfileResolver(IReadOnlyDictionary<string, string> profiles) : IWindowsUserProfileResolver
    {
        public string ResolveLocalAppData(string authenticatedSid) => profiles[authenticatedSid];
    }

    private sealed class FixedSettingsStore<T>(T settings) : ISettingsStore<T> where T : class
    {
        public SettingsLoadResult<T> Load() => new(settings, false, false, null);
        public void Save(T value) => throw new NotSupportedException();
    }

    private sealed class RecordingHistory : ISettingsChangeHistory
    {
        public ValueTask RecordAsync(SettingsChangeHistoryEvent value, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}

internal sealed class StaticUserSettingsStoreResolver(ISettingsStore<UserSettings> store) : IAuthenticatedUserSettingsStoreResolver
{
    public ISettingsStore<UserSettings> Resolve(string authenticatedSid) => store;
}
