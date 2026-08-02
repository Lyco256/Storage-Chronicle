using StorageChronicle.Application;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.ExternalMedia;
using StorageChronicle.Platform.Windows.FileSystem.Policy;
using StorageChronicle.Settings;

namespace StorageChronicle.Agent;

/// <summary>Coordinates optional per-media immutable mirror sessions.</summary>
public interface IMediaMirrorSessionCoordinator
{
    /// <summary>Starts a mirror session if validated machine settings enable it.</summary>
    ValueTask RegisterAsync(MediaVolumeDescriptor media, MountSession session, CancellationToken cancellationToken = default);

    /// <summary>Flushes and seals a mirror session before media removal.</summary>
    ValueTask UnregisterAsync(VolumeId volumeId, CancellationToken cancellationToken = default);
}

/// <summary>Bridges the media exclusion contract to the shared Windows filesystem policy.</summary>
public sealed class WindowsMediaExclusionRegistrar : IMediaMonitoringExclusionRegistrar
{
    private readonly WindowsExclusionPolicy policy;

    /// <summary>Initializes the registrar over the policy used by live and snapshot collectors.</summary>
    public WindowsMediaExclusionRegistrar(WindowsExclusionPolicy policy) => this.policy = policy ?? throw new ArgumentNullException(nameof(policy));

    /// <inheritdoc />
    public string Register(string path) => policy.RegisterDynamicRoot(path);
}

/// <summary>Writes bounded canonical metadata batches to a configured external-media mirror.</summary>
public sealed class ExternalMediaMirrorCoordinator : IMediaMirrorSessionCoordinator, ICanonicalEventSink, IAsyncDisposable
{
    private const int SegmentBatchSize = 256;
    private readonly ISettingsStore<MachineSettings> settings;
    private readonly string pcId;
    private readonly IMediaMonitoringExclusionRegistrar exclusionRegistrar;
    private readonly Dictionary<VolumeId, MirrorSession> sessions = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    /// <summary>Initializes a coordinator with machine settings and the shared exclusion policy.</summary>
    public ExternalMediaMirrorCoordinator(ISettingsStore<MachineSettings> settings, string pcId, IMediaMonitoringExclusionRegistrar exclusionRegistrar)
    {
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.pcId = ValidateIdentity(pcId);
        this.exclusionRegistrar = exclusionRegistrar ?? throw new ArgumentNullException(nameof(exclusionRegistrar));
    }

    /// <inheritdoc />
    public async ValueTask RegisterAsync(MediaVolumeDescriptor media, MountSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(session);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (sessions.ContainsKey(media.VolumeId)) return;
            var configured = settings.Load().Settings.MediaMirrors.TryGetValue(media.LogicalMediaId, out var root) ? root : null;
            if (string.IsNullOrWhiteSpace(configured) || media.IsReadOnly) return;
            var configuration = ExternalMediaStore.ValidateMirrorConfiguration(new MediaMirrorConfiguration(true, configured!, media.IsSystemVolume, media.IsBootVolume, media.IsRecoveryVolume, media.IsEfiVolume));
            if (!configuration.IsAllowed) return;

            var store = new ExternalMediaStore(configuration.MediaRoot, pcId);
            store.RegisterMonitoringExclusion(exclusionRegistrar);
            await store.RecoverInterruptedWriteAsync(cancellationToken).ConfigureAwait(false);
            var parent = await store.ReadManifestSlotAsync(cancellationToken).ConfigureAwait(false);
            sessions.Add(media.VolumeId, new MirrorSession(media, session, store, parent?.Sha256));
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask UnregisterAsync(VolumeId volumeId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!sessions.Remove(volumeId, out var session)) return;
            await FlushSessionAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask OnCanonicalAsync(CanonicalEvent value, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (value.VolumeId is not { } volume || !sessions.TryGetValue(volume, out var session)) return;
            session.Pending.Add(value);
            if (session.Pending.Count >= SegmentBatchSize) await FlushSessionAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var session in sessions.Values.ToArray()) await FlushSessionAsync(session, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken = default) => await FlushAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await FlushAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
        gate.Dispose();
    }

    private static async ValueTask FlushSessionAsync(MirrorSession session, CancellationToken cancellationToken)
    {
        if (session.Pending.Count > 0)
        {
            var segment = await session.Store.AppendSegmentAsync(session.Pending.ToArray(), cancellationToken).ConfigureAwait(false);
            session.Segments.Add(segment);
            session.Pending.Clear();
        }

        if (session.Segments.Count == 0) return;
        var manifest = await session.Store.PublishManifestAsync(session.Media.LogicalMediaId, session.ParentManifestSha256, session.MountSession.Id.Value, session.Segments.ToArray(), cancellationToken).ConfigureAwait(false);
        session.ParentManifestSha256 = manifest.Sha256;
        session.Segments.Clear();
    }

    private static string ValidateIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar)) throw new ArgumentException("The identity must be one path component.", nameof(value));
        return value;
    }

    private sealed class MirrorSession(MediaVolumeDescriptor media, MountSession mountSession, ExternalMediaStore store, string? parentManifestSha256)
    {
        public MediaVolumeDescriptor Media { get; } = media;
        public MountSession MountSession { get; } = mountSession;
        public ExternalMediaStore Store { get; } = store;
        public List<CanonicalEvent> Pending { get; } = [];
        public List<MediaSegment> Segments { get; } = [];
        public string? ParentManifestSha256 { get; set; } = parentManifestSha256;
    }
}
