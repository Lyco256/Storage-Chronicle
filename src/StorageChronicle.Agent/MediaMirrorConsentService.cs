using System.Collections.Concurrent;
using StorageChronicle.Contracts;
using StorageChronicle.Contracts.Runtime;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.ExternalMedia;
using StorageChronicle.Settings;

namespace StorageChronicle.Agent;

/// <summary>Gates external-media import and mirror startup on an explicit, live, PC-local approval.</summary>
public sealed class MediaMirrorConsentService
{
    private const string PendingRootIdentity = "pending-new-root";
    private readonly AgentHealthState health;
    private readonly ISettingsStore<MachineSettings> settings;
    private readonly AgentSettingsService settingsService;
    private readonly IVolumeEnumerator volumes;
    private readonly IVolumeBoundMediaFileSystemFactory fileSystems;
    private readonly string pcIdentity;
    private readonly ConcurrentDictionary<string, PendingDecision> pending = new(StringComparer.Ordinal);

    /// <summary>Initializes the consent gate with Agent-owned persistence and live volume identity boundaries.</summary>
    public MediaMirrorConsentService(
        AgentHealthState health,
        ISettingsStore<MachineSettings> settings,
        AgentSettingsService settingsService,
        IVolumeEnumerator volumes,
        IVolumeBoundMediaFileSystemFactory fileSystems,
        string pcIdentity)
    {
        this.health = health ?? throw new ArgumentNullException(nameof(health));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        this.volumes = volumes ?? throw new ArgumentNullException(nameof(volumes));
        this.fileSystems = fileSystems ?? throw new ArgumentNullException(nameof(fileSystems));
        ArgumentException.ThrowIfNullOrWhiteSpace(pcIdentity);
        this.pcIdentity = pcIdentity;
    }

    /// <summary>Returns true only for an exact existing grant or after an explicit approval has been revalidated and saved.</summary>
    public async ValueTask<bool> AuthorizeAsync(MediaVolumeDescriptor media, string configuredRoot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        var savedConsent = FindSavedConsent(media.LogicalMediaId);
        var evidence = Capture(media, configuredRoot, savedConsent?.ApprovedUserSid);
        var saved = ToBinding(savedConsent);
        var evaluation = MediaMirrorConsentPolicy.Evaluate(evidence.Binding, saved);
        if (evaluation.Status == MediaMirrorConsentStatus.AuthorizedByExistingBinding) return true;
        if (evaluation.Status != MediaMirrorConsentStatus.ApprovalRequired) return false;

        var request = new PendingMediaMirrorApproval(
            Guid.NewGuid().ToString("N"), pcIdentity, media.LogicalMediaId, media.VolumeId.Value,
            evidence.MediaRoot, evidence.RootIdentity, media.FileSystem, ToDisclosure(evidence.Binding.AclProtection),
            evidence.RootExists, FutureHistoryAppendRequested: true, DateTimeOffset.UtcNow);
        var decision = new PendingDecision(media, configuredRoot, evidence, new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
        if (!pending.TryAdd(request.RequestId, decision)) return false;
        if (!health.TryAddPendingMediaApproval(request))
        {
            pending.TryRemove(request.RequestId, out _);
            return false;
        }

        try
        {
            return await decision.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (pending.TryRemove(request.RequestId, out var unresolved)) unresolved.Completion.TrySetResult(false);
            health.TryResolveMediaApproval(request.RequestId);
        }
    }

    /// <summary>Checks the saved binding against an identity read from the current pinned product-root handle.</summary>
    public bool HasGrant(MediaVolumeDescriptor media, string configuredRoot, IVolumeBoundMediaFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(fileSystem);
        var saved = ToBinding(FindSavedConsent(media.LogicalMediaId));
        if (saved is null || media.IsReadOnly || !media.IsProtectedRoleClassificationComplete || media.ProtectedRoles != ProtectedVolumeRoles.None) return false;
        try
        {
            var current = Capture(media, configuredRoot, saved.ApprovedUserSid, fileSystem).Binding;
            return MediaMirrorConsentPolicy.Evaluate(current, saved).Status == MediaMirrorConsentStatus.AuthorizedByExistingBinding;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Returns whether the exact current grant also permits reading and importing pre-existing media history.</summary>
    public bool CanReadExistingHistory(MediaVolumeDescriptor media, string configuredRoot)
    {
        ArgumentNullException.ThrowIfNull(media);
        try
        {
            var grants = settings.Load().Settings.MediaMirrorConsents.Where(value =>
                string.Equals(value.PcIdentity, pcIdentity, StringComparison.Ordinal) &&
                string.Equals(value.LogicalMediaId, media.LogicalMediaId, StringComparison.Ordinal)).ToArray();
            if (grants.Length != 1 || !grants[0].ExistingHistoryReadAndImportAllowed) return false;
            using var fileSystem = fileSystems.Open(media.VolumeId);
            return fileSystem.DirectoryExists(".StorageChronicle") && HasGrant(media, configuredRoot, fileSystem);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Handles an authenticated interactive user's explicit approve or cancel decision.</summary>
    public async ValueTask<bool> DecideAsync(MediaMirrorApprovalDecision decision, CancellationToken cancellationToken = default)
        => await DecideAsync(decision, authenticatedUserSid: null, cancellationToken).ConfigureAwait(false);

    /// <summary>Handles an explicit decision using the SID authenticated by the interactive named-pipe session.</summary>
    public async ValueTask<bool> DecideAsync(MediaMirrorApprovalDecision decision, string? authenticatedUserSid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!health.TryGetPendingMediaApproval(decision.RequestId, out _)
            || !pending.TryGetValue(decision.RequestId, out var context)) return false;

        if (!decision.Approve)
        {
            Resolve(decision.RequestId, context, false);
            return true;
        }

        if (string.IsNullOrWhiteSpace(authenticatedUserSid))
        {
            Resolve(decision.RequestId, context, false);
            return false;
        }

        try
        {
            var live = await FindLiveVolumeAsync(context.Media.VolumeId, cancellationToken).ConfigureAwait(false);
            if (live is null || !SameLiveMedia(context.Media, live))
            {
                Resolve(decision.RequestId, context, false);
                return false;
            }

            var current = Capture(live, context.ConfiguredRoot, authenticatedUserSid);
            if (current.RootExists != context.Evidence.RootExists ||
                (current.RootExists && !string.Equals(current.RootIdentity, context.Evidence.RootIdentity, StringComparison.Ordinal)))
            {
                Resolve(decision.RequestId, context, false);
                return false;
            }

            var binding = current.Binding;
            if (!current.RootExists)
            {
                if (current.Binding.FileSystem == MediaConsentFileSystem.Ntfs && current.Binding.AclProtection != MediaConsentAclProtection.NtfsAclVerified)
                {
                    Resolve(decision.RequestId, context, false);
                    return false;
                }

                using (var initializedStore = new ExternalMediaStore(current.MediaRoot, pcIdentity, live.VolumeId, fileSystems.Open(live.VolumeId), createIfMissing: true))
                {
                    // The user has explicitly approved creation and future append; bind the grant to the actual new directory identity.
                }

                var created = Capture(live, context.ConfiguredRoot, authenticatedUserSid);
                if (!created.RootExists || string.Equals(created.RootIdentity, PendingRootIdentity, StringComparison.Ordinal))
                {
                    Resolve(decision.RequestId, context, false);
                    return false;
                }
                binding = created.Binding;
            }

            var accepted = MediaMirrorConsentPolicy.Evaluate(binding, ToBinding(FindSavedConsent(live.LogicalMediaId)), MediaMirrorConsentUserDecision.ExplicitlyAccepted);
            if (accepted.Status != MediaMirrorConsentStatus.AcceptedBindingReadyToPersist || accepted.BindingToPersist is null)
            {
                Resolve(decision.RequestId, context, false);
                return false;
            }
            var persisted = ToSettings(accepted.BindingToPersist, current.RootExists);
            var result = await settingsService.GrantMediaMirrorConsentAsync(persisted, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                Resolve(decision.RequestId, context, false);
                return false;
            }

            Resolve(decision.RequestId, context, true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Resolve(decision.RequestId, context, false);
            return false;
        }
        catch (OperationCanceledException)
        {
            Resolve(decision.RequestId, context, false);
            throw;
        }
    }

    private MediaEvidence Capture(MediaVolumeDescriptor media, string configuredRoot, string? approvedUserSid)
    {
        using var fileSystem = fileSystems.Open(media.VolumeId);
        return Capture(media, configuredRoot, approvedUserSid, fileSystem);
    }

    private MediaEvidence Capture(MediaVolumeDescriptor media, string configuredRoot, string? approvedUserSid, IVolumeBoundMediaFileSystem fileSystem)
    {
        if (media.IsReadOnly || !media.IsProtectedRoleClassificationComplete || media.ProtectedRoles != ProtectedVolumeRoles.None)
            throw new InvalidOperationException("The media is read-only or its protected-volume role is not fully verified.");

        var configuration = ExternalMediaStore.ValidateMirrorConfiguration(new MediaMirrorConfiguration(true, configuredRoot, media.ProtectedRoles, media.IsProtectedRoleClassificationComplete));
        var mediaRoot = ExternalMediaStore.ValidateMediaRoot(configuration.MediaRoot, media.MountPoints);
        if (fileSystem.VolumeId != media.VolumeId) throw new IOException("The media filesystem session is not bound to the enumerated volume identity.");
        var exists = fileSystem.DirectoryExists(".StorageChronicle");
        var identity = exists ? fileSystem.GetOwnedProductDirectoryIdentity() : PendingRootIdentity;
        var fileSystemKind = ToPolicyFileSystem(media.FileSystem);
        var aclProtection = ToPolicyAcl(media.FileSystem);
        string? aclFingerprint = null;
        if (fileSystemKind == MediaConsentFileSystem.Ntfs && !string.IsNullOrWhiteSpace(approvedUserSid))
        {
            var inspection = fileSystem.InspectProductAcl(approvedUserSid);
            if (inspection.Status == MediaMirrorAclInspectionStatus.Verified && inspection.DescriptorFingerprint is { Length: 64 } fingerprint && fingerprint.All(Uri.IsHexDigit))
            {
                aclProtection = MediaConsentAclProtection.NtfsAclVerified;
                aclFingerprint = fingerprint;
            }
        }
        var binding = new MediaMirrorConsentBinding(pcIdentity, media.LogicalMediaId, media.VolumeId.Value, identity,
            fileSystemKind, aclProtection, fileSystemKind == MediaConsentFileSystem.Ntfs ? approvedUserSid : null, aclFingerprint);
        return new MediaEvidence(mediaRoot, exists, identity, binding);
    }

    private MediaMirrorConsentSettings? FindSavedConsent(string logicalMediaId)
    {
        var matches = settings.Load().Settings.MediaMirrorConsents
            .Where(value => string.Equals(value.PcIdentity, pcIdentity, StringComparison.Ordinal) && string.Equals(value.LogicalMediaId, logicalMediaId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1) return null;
        return matches[0];
    }

    private static MediaMirrorConsentBinding? ToBinding(MediaMirrorConsentSettings? consent) => consent is null ? null : new(
        consent.PcIdentity, consent.LogicalMediaId, consent.LiveVolumeIdentity, consent.DedicatedMediaRootIdentity,
        ParseFileSystem(consent.FileSystem), ParseAcl(consent.AclProtection), consent.ApprovedUserSid, consent.AclDescriptorFingerprint);

    private async ValueTask<MediaVolumeDescriptor?> FindLiveVolumeAsync(VolumeId volumeId, CancellationToken cancellationToken)
    {
        var descriptors = await volumes.EnumerateAsync(cancellationToken).ConfigureAwait(false);
        var matches = descriptors.Where(value => value.Id == volumeId && value.IsExternal && value.IsDirectoryReadable).ToArray();
        if (matches.Length != 1) return null;
        var descriptor = matches[0];
        return new MediaVolumeDescriptor(descriptor.Id.Value, descriptor.Id, descriptor.FileSystem, descriptor.IsReadOnly,
            descriptor.SupportsUsn, descriptor.ProtectedRoles, descriptor.IsProtectedRoleClassificationComplete) { MountPoints = descriptor.MountPoints };
    }

    private static bool SameLiveMedia(MediaVolumeDescriptor expected, MediaVolumeDescriptor current) =>
        expected.VolumeId == current.VolumeId &&
        string.Equals(expected.LogicalMediaId, current.LogicalMediaId, StringComparison.Ordinal) &&
        string.Equals(expected.FileSystem, current.FileSystem, StringComparison.OrdinalIgnoreCase) &&
        expected.IsReadOnly == current.IsReadOnly &&
        expected.ProtectedRoles == current.ProtectedRoles &&
        expected.IsProtectedRoleClassificationComplete == current.IsProtectedRoleClassificationComplete &&
        expected.MountPoints.OrderBy(Path.GetFullPath, PathComparer).SequenceEqual(current.MountPoints.OrderBy(Path.GetFullPath, PathComparer), PathComparer);

    private static MediaMirrorConsentSettings ToSettings(MediaMirrorConsentBinding binding, bool existingHistoryReadAndImportAllowed) => new(
        binding.PcIdentity!, binding.LogicalMediaId!, binding.LiveVolumeIdentity!, binding.DedicatedMediaRootIdentity!,
        binding.FileSystem.ToString(), binding.AclProtection.ToString(), DateTimeOffset.UtcNow, existingHistoryReadAndImportAllowed,
        binding.ApprovedUserSid, binding.AclDescriptorFingerprint);

    private static MediaConsentFileSystem ToPolicyFileSystem(string fileSystem) => fileSystem.ToUpperInvariant() switch
    {
        "NTFS" => MediaConsentFileSystem.Ntfs,
        "FAT" => MediaConsentFileSystem.Fat,
        "FAT32" => MediaConsentFileSystem.Fat32,
        "EXFAT" => MediaConsentFileSystem.ExFat,
        "" or "UNKNOWN" => MediaConsentFileSystem.Unknown,
        _ => MediaConsentFileSystem.Other
    };

    private static MediaConsentAclProtection ToPolicyAcl(string fileSystem) => fileSystem.ToUpperInvariant() switch
    {
        "NTFS" => MediaConsentAclProtection.NtfsAclUnavailable,
        "" or "UNKNOWN" => MediaConsentAclProtection.Unknown,
        _ => MediaConsentAclProtection.NotProvidedByFileSystem
    };

    private static MediaConsentFileSystem ParseFileSystem(string value) => Enum.TryParse<MediaConsentFileSystem>(value, out var parsed) ? parsed : MediaConsentFileSystem.Unknown;
    private static MediaConsentAclProtection ParseAcl(string value) => Enum.TryParse<MediaConsentAclProtection>(value, out var parsed) ? parsed : MediaConsentAclProtection.Unknown;
    private static MediaMirrorAclDisclosure ToDisclosure(MediaConsentAclProtection value) => value switch
    {
        MediaConsentAclProtection.NtfsAclVerified => MediaMirrorAclDisclosure.NtfsAclVerified,
        MediaConsentAclProtection.NtfsAclUnavailable => MediaMirrorAclDisclosure.NtfsAclUnavailable,
        MediaConsentAclProtection.NotProvidedByFileSystem => MediaMirrorAclDisclosure.NotProvidedByFileSystem,
        _ => MediaMirrorAclDisclosure.Unknown
    };
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private void Resolve(string requestId, PendingDecision context, bool approved)
    {
        if (pending.TryRemove(requestId, out _)) context.Completion.TrySetResult(approved);
        health.TryResolveMediaApproval(requestId);
    }

    private sealed record MediaEvidence(string MediaRoot, bool RootExists, string RootIdentity, MediaMirrorConsentBinding Binding);
    private sealed record PendingDecision(MediaVolumeDescriptor Media, string ConfiguredRoot, MediaEvidence Evidence, TaskCompletionSource<bool> Completion);
}
