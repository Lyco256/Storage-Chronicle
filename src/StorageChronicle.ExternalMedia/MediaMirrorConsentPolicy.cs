namespace StorageChronicle.ExternalMedia;

/// <summary>Classifies the filesystem reported for the live external-media volume.</summary>
public enum MediaConsentFileSystem
{
    /// <summary>The filesystem could not be determined.</summary>
    Unknown,
    /// <summary>The volume uses NTFS.</summary>
    Ntfs,
    /// <summary>The volume uses FAT.</summary>
    Fat,
    /// <summary>The volume uses FAT32.</summary>
    Fat32,
    /// <summary>The volume uses exFAT.</summary>
    ExFat,
    /// <summary>The volume uses a known filesystem other than NTFS, FAT, FAT32, or exFAT.</summary>
    Other
}

/// <summary>Describes whether ACL protection was established for the dedicated media root.</summary>
public enum MediaConsentAclProtection
{
    /// <summary>The protection classification could not be determined.</summary>
    Unknown,
    /// <summary>The dedicated NTFS media root's ACL was verified by the caller.</summary>
    NtfsAclVerified,
    /// <summary>The filesystem is NTFS, but ACL protection could not be verified.</summary>
    NtfsAclUnavailable,
    /// <summary>The filesystem does not provide the NTFS ACL protection asserted by this policy.</summary>
    NotProvidedByFileSystem
}

/// <summary>
/// Opaque identity evidence used to bind an explicit media-consent decision to one PC, one live volume,
/// and one dedicated media root. This is a policy value, not a settings or serialization model.
/// </summary>
/// <remarks>
/// Identity strings are compared exactly using ordinal comparison; callers must supply stable, canonical
/// identifiers and must not derive identity from a drive-letter or mount-point string alone.
/// </remarks>
public sealed record MediaMirrorConsentBinding(
    string? PcIdentity,
    string? LogicalMediaId,
    string? LiveVolumeIdentity,
    string? DedicatedMediaRootIdentity,
    MediaConsentFileSystem FileSystem,
    MediaConsentAclProtection AclProtection,
    string? ApprovedUserSid = null,
    string? AclDescriptorFingerprint = null);

/// <summary>Indicates whether the user explicitly accepted a consent prompt during this evaluation.</summary>
public enum MediaMirrorConsentUserDecision
{
    /// <summary>No new user decision was made.</summary>
    NotPresented,
    /// <summary>The user explicitly accepted the currently displayed consent scope.</summary>
    ExplicitlyAccepted,
    /// <summary>The user cancelled the consent prompt.</summary>
    Cancelled
}

/// <summary>Outcome of evaluating saved consent against current external-media identity evidence.</summary>
public enum MediaMirrorConsentStatus
{
    /// <summary>An existing saved binding exactly matches all current evidence.</summary>
    AuthorizedByExistingBinding,
    /// <summary>Explicit approval is required; no media import or write is authorized.</summary>
    ApprovalRequired,
    /// <summary>Explicit acceptance was supplied and the returned binding may be persisted by the owner.</summary>
    AcceptedBindingReadyToPersist,
    /// <summary>Current evidence or the user-decision value is invalid or unknown.</summary>
    Refused,
    /// <summary>The consent prompt was cancelled; no media import or write is authorized.</summary>
    Cancelled
}

/// <summary>Explains the consent policy outcome without performing persistence or I/O.</summary>
public enum MediaMirrorConsentReason
{
    /// <summary>All binding fields match an existing complete saved binding.</summary>
    ExactBindingMatch,
    /// <summary>No saved binding exists, including a legacy configuration without consent evidence.</summary>
    MissingOrLegacyBinding,
    /// <summary>The saved binding is incomplete or uses an unrecognized classification.</summary>
    IncompleteSavedBinding,
    /// <summary>At least one saved identity or classification differs from current evidence.</summary>
    BindingChanged,
    /// <summary>Current PC, media, volume, root, filesystem, or protection evidence is unknown or invalid.</summary>
    UnknownCurrentEvidence,
    /// <summary>The supplied user-decision enum value is not recognized.</summary>
    InvalidUserDecision,
    /// <summary>The user cancelled the explicit approval prompt.</summary>
    UserCancelled
}

/// <summary>Immutable result of the pure media-consent binding evaluation.</summary>
/// <param name="Status">The authorization outcome.</param>
/// <param name="Reason">The reason for the outcome.</param>
/// <param name="BindingToPersist">The current binding, present only after explicit acceptance.</param>
public sealed record MediaMirrorConsentEvaluation(
    MediaMirrorConsentStatus Status,
    MediaMirrorConsentReason Reason,
    MediaMirrorConsentBinding? BindingToPersist);

/// <summary>Evaluates whether saved consent exactly binds current external-media evidence.</summary>
/// <remarks>
/// This policy is deterministic and side-effect free. The caller is responsible for acquiring and validating
/// identities, displaying the complete scope to the user, persisting <see cref="MediaMirrorConsentEvaluation.BindingToPersist"/>
/// through the canonical Settings/Agent layer, and preventing import or writes for every outcome except
/// <see cref="MediaMirrorConsentStatus.AuthorizedByExistingBinding"/> and a newly accepted binding after it
/// has been durably persisted. This API never performs filesystem or settings I/O.
/// </remarks>
public static class MediaMirrorConsentPolicy
{
    /// <summary>Evaluates current evidence, optional saved consent, and an explicit user decision.</summary>
    /// <param name="current">Evidence freshly obtained for the currently connected media.</param>
    /// <param name="savedBinding">The complete prior consent binding, or <see langword="null"/> for absent/legacy consent.</param>
    /// <param name="userDecision">A decision from the consent UI for this evaluation, if one was shown.</param>
    /// <returns>A fail-closed outcome. A replacement binding is returned only after explicit acceptance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> is <see langword="null"/>.</exception>
    public static MediaMirrorConsentEvaluation Evaluate(
        MediaMirrorConsentBinding current,
        MediaMirrorConsentBinding? savedBinding,
        MediaMirrorConsentUserDecision userDecision = MediaMirrorConsentUserDecision.NotPresented)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (userDecision == MediaMirrorConsentUserDecision.Cancelled)
            return new(MediaMirrorConsentStatus.Cancelled, MediaMirrorConsentReason.UserCancelled, null);

        if (userDecision is not MediaMirrorConsentUserDecision.NotPresented and not MediaMirrorConsentUserDecision.ExplicitlyAccepted)
            return new(MediaMirrorConsentStatus.Refused, MediaMirrorConsentReason.InvalidUserDecision, null);

        if (!HasCompleteCurrentEvidence(current))
            return new(MediaMirrorConsentStatus.Refused, MediaMirrorConsentReason.UnknownCurrentEvidence, null);

        if (savedBinding is not null && IsGrantEligible(current) && IsGrantEligible(savedBinding))
        {
            if (BindingsMatch(current, savedBinding))
                return new(MediaMirrorConsentStatus.AuthorizedByExistingBinding, MediaMirrorConsentReason.ExactBindingMatch, null);
        }

        var reason = savedBinding is null
            ? MediaMirrorConsentReason.MissingOrLegacyBinding
            : IsGrantEligible(savedBinding)
                ? MediaMirrorConsentReason.BindingChanged
                : MediaMirrorConsentReason.IncompleteSavedBinding;

        if (userDecision == MediaMirrorConsentUserDecision.ExplicitlyAccepted)
        {
            if (!IsGrantEligible(current))
                return new(MediaMirrorConsentStatus.Refused, MediaMirrorConsentReason.UnknownCurrentEvidence, null);
            return new(MediaMirrorConsentStatus.AcceptedBindingReadyToPersist, reason, current);
        }

        return new(MediaMirrorConsentStatus.ApprovalRequired, reason, null);
    }

    private static bool HasCompleteCurrentEvidence(MediaMirrorConsentBinding binding)
    {
        if (string.IsNullOrWhiteSpace(binding.PcIdentity)
            || string.IsNullOrWhiteSpace(binding.LogicalMediaId)
            || string.IsNullOrWhiteSpace(binding.LiveVolumeIdentity)
            || string.IsNullOrWhiteSpace(binding.DedicatedMediaRootIdentity))
            return false;

        return binding.FileSystem switch
        {
            MediaConsentFileSystem.Ntfs => binding.AclProtection is MediaConsentAclProtection.NtfsAclVerified or MediaConsentAclProtection.NtfsAclUnavailable,
            MediaConsentFileSystem.Fat or MediaConsentFileSystem.Fat32 or MediaConsentFileSystem.ExFat or MediaConsentFileSystem.Other
                => binding.AclProtection == MediaConsentAclProtection.NotProvidedByFileSystem,
            _ => false
        };
    }

    private static bool IsGrantEligible(MediaMirrorConsentBinding binding)
    {
        if (!HasCompleteCurrentEvidence(binding)) return false;
        return binding.FileSystem switch
        {
            MediaConsentFileSystem.Ntfs => binding.AclProtection == MediaConsentAclProtection.NtfsAclVerified
                && IsSid(binding.ApprovedUserSid)
                && IsSha256(binding.AclDescriptorFingerprint),
            MediaConsentFileSystem.Fat or MediaConsentFileSystem.Fat32 or MediaConsentFileSystem.ExFat or MediaConsentFileSystem.Other
                => binding.AclProtection == MediaConsentAclProtection.NotProvidedByFileSystem
                    && string.IsNullOrWhiteSpace(binding.AclDescriptorFingerprint),
            _ => false
        };
    }

    private static bool IsSid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 184 || !value.StartsWith("S-1-", StringComparison.Ordinal)) return false;
        var parts = value.Split('-');
        return parts.Length >= 4 && parts[0] == "S" && parts[1] == "1" && parts.Skip(2).All(part => ulong.TryParse(part, out _));
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool BindingsMatch(MediaMirrorConsentBinding current, MediaMirrorConsentBinding saved) =>
        string.Equals(current.PcIdentity, saved.PcIdentity, StringComparison.Ordinal)
        && string.Equals(current.LogicalMediaId, saved.LogicalMediaId, StringComparison.Ordinal)
        && string.Equals(current.LiveVolumeIdentity, saved.LiveVolumeIdentity, StringComparison.Ordinal)
        && string.Equals(current.DedicatedMediaRootIdentity, saved.DedicatedMediaRootIdentity, StringComparison.Ordinal)
        && current.FileSystem == saved.FileSystem
        && current.AclProtection == saved.AclProtection
        && string.Equals(current.ApprovedUserSid, saved.ApprovedUserSid, StringComparison.Ordinal)
        && string.Equals(current.AclDescriptorFingerprint, saved.AclDescriptorFingerprint, StringComparison.OrdinalIgnoreCase);
}
