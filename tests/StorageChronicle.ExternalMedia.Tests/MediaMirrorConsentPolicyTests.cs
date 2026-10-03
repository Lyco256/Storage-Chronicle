using StorageChronicle.ExternalMedia;
using Xunit;

namespace StorageChronicle.ExternalMedia.Tests;

public sealed class MediaMirrorConsentPolicyTests
{
    [Fact]
    public void ExactSavedBindingAuthorizesWithoutCreatingReplacementEvidence()
    {
        var binding = CompleteBinding();

        var result = MediaMirrorConsentPolicy.Evaluate(binding, binding);

        Assert.Equal(MediaMirrorConsentStatus.AuthorizedByExistingBinding, result.Status);
        Assert.Equal(MediaMirrorConsentReason.ExactBindingMatch, result.Reason);
        Assert.Null(result.BindingToPersist);
    }

    [Fact]
    public void MissingOrLegacyBindingRequiresAnExplicitDecision()
    {
        var current = CompleteBinding();

        var result = MediaMirrorConsentPolicy.Evaluate(current, null);

        Assert.Equal(MediaMirrorConsentStatus.ApprovalRequired, result.Status);
        Assert.Equal(MediaMirrorConsentReason.MissingOrLegacyBinding, result.Reason);
        Assert.Null(result.BindingToPersist);
    }

    [Fact]
    public void ExplicitAcceptanceReturnsOnlyCurrentBindingForCanonicalLayerToPersist()
    {
        var current = CompleteBinding();

        var result = MediaMirrorConsentPolicy.Evaluate(current, null, MediaMirrorConsentUserDecision.ExplicitlyAccepted);

        Assert.Equal(MediaMirrorConsentStatus.AcceptedBindingReadyToPersist, result.Status);
        Assert.Equal(MediaMirrorConsentReason.MissingOrLegacyBinding, result.Reason);
        Assert.Same(current, result.BindingToPersist);
    }

    [Theory]
    [InlineData("PC")]
    [InlineData("LogicalMedia")]
    [InlineData("Volume")]
    [InlineData("Root")]
    [InlineData("FileSystem")]
    [InlineData("Protection")]
    public void AnyBindingChangeForcesReapproval(string changedField)
    {
        var saved = CompleteBinding();
        var current = changedField switch
        {
            "PC" => saved with { PcIdentity = "pc-b" },
            "LogicalMedia" => saved with { LogicalMediaId = "media-b" },
            "Volume" => saved with { LiveVolumeIdentity = "volume-b" },
            "Root" => saved with { DedicatedMediaRootIdentity = "root-b" },
            "FileSystem" => saved with { FileSystem = MediaConsentFileSystem.ExFat, AclProtection = MediaConsentAclProtection.NotProvidedByFileSystem },
            "Protection" => saved with { AclProtection = MediaConsentAclProtection.NtfsAclUnavailable },
            _ => throw new ArgumentOutOfRangeException(nameof(changedField))
        };

        var result = MediaMirrorConsentPolicy.Evaluate(current, saved);

        Assert.Equal(MediaMirrorConsentStatus.ApprovalRequired, result.Status);
        Assert.Equal(MediaMirrorConsentReason.BindingChanged, result.Reason);
        Assert.Null(result.BindingToPersist);
    }

    [Fact]
    public void IdentityComparisonIsOrdinalAndDoesNotSilentlyNormalizeCase()
    {
        var saved = CompleteBinding();
        var current = saved with { PcIdentity = "PC-A" };

        var result = MediaMirrorConsentPolicy.Evaluate(current, saved);

        Assert.Equal(MediaMirrorConsentStatus.ApprovalRequired, result.Status);
        Assert.Equal(MediaMirrorConsentReason.BindingChanged, result.Reason);
    }

    [Fact]
    public void AcceptanceAfterBindingChangeReturnsCurrentBindingButDoesNotAuthorizeOldBinding()
    {
        var saved = CompleteBinding();
        var current = saved with { LiveVolumeIdentity = "new-live-volume" };

        var result = MediaMirrorConsentPolicy.Evaluate(current, saved, MediaMirrorConsentUserDecision.ExplicitlyAccepted);

        Assert.Equal(MediaMirrorConsentStatus.AcceptedBindingReadyToPersist, result.Status);
        Assert.Equal(MediaMirrorConsentReason.BindingChanged, result.Reason);
        Assert.Equal(current, result.BindingToPersist);
    }

    [Fact]
    public void IncompleteLegacyBindingIsNeverTreatedAsConsent()
    {
        var current = CompleteBinding();
        var legacy = current with { DedicatedMediaRootIdentity = null };

        var result = MediaMirrorConsentPolicy.Evaluate(current, legacy);

        Assert.Equal(MediaMirrorConsentStatus.ApprovalRequired, result.Status);
        Assert.Equal(MediaMirrorConsentReason.IncompleteSavedBinding, result.Reason);
        Assert.Null(result.BindingToPersist);
    }

    [Theory]
    [InlineData(MediaConsentFileSystem.Fat)]
    [InlineData(MediaConsentFileSystem.Fat32)]
    [InlineData(MediaConsentFileSystem.ExFat)]
    [InlineData(MediaConsentFileSystem.Other)]
    public void NonNtfsFilesystemCannotBeClassifiedAsAclProtected(MediaConsentFileSystem fileSystem)
    {
        var invalid = CompleteBinding() with
        {
            FileSystem = fileSystem,
            AclProtection = MediaConsentAclProtection.NtfsAclVerified
        };

        var result = MediaMirrorConsentPolicy.Evaluate(invalid, null, MediaMirrorConsentUserDecision.ExplicitlyAccepted);

        Assert.Equal(MediaMirrorConsentStatus.Refused, result.Status);
        Assert.Equal(MediaMirrorConsentReason.UnknownCurrentEvidence, result.Reason);
        Assert.Null(result.BindingToPersist);
    }

    [Fact]
    public void NtfsAclUnavailableIsDistinctAndCanBeBoundWithoutClaimingVerification()
    {
        var binding = CompleteBinding() with { AclProtection = MediaConsentAclProtection.NtfsAclUnavailable };

        var result = MediaMirrorConsentPolicy.Evaluate(binding, binding);

        Assert.Equal(MediaMirrorConsentStatus.AuthorizedByExistingBinding, result.Status);
    }

    [Fact]
    public void UnknownOrMissingCurrentIdentityCannotBeApprovedEvenWithExplicitAcceptance()
    {
        var invalid = CompleteBinding() with { LiveVolumeIdentity = " " };

        var result = MediaMirrorConsentPolicy.Evaluate(invalid, null, MediaMirrorConsentUserDecision.ExplicitlyAccepted);

        Assert.Equal(MediaMirrorConsentStatus.Refused, result.Status);
        Assert.Equal(MediaMirrorConsentReason.UnknownCurrentEvidence, result.Reason);
        Assert.Null(result.BindingToPersist);
    }

    [Fact]
    public void UnknownFilesystemAndUnknownAclClassificationAreFailClosed()
    {
        var unknownFileSystem = CompleteBinding() with { FileSystem = MediaConsentFileSystem.Unknown };
        var unknownProtection = CompleteBinding() with { AclProtection = MediaConsentAclProtection.Unknown };

        Assert.Equal(MediaMirrorConsentStatus.Refused,
            MediaMirrorConsentPolicy.Evaluate(unknownFileSystem, null, MediaMirrorConsentUserDecision.ExplicitlyAccepted).Status);
        Assert.Equal(MediaMirrorConsentStatus.Refused,
            MediaMirrorConsentPolicy.Evaluate(unknownProtection, null, MediaMirrorConsentUserDecision.ExplicitlyAccepted).Status);
    }

    [Fact]
    public void CancellationNeverAuthorizesOrReturnsBindingToPersist()
    {
        var binding = CompleteBinding();

        var result = MediaMirrorConsentPolicy.Evaluate(binding, binding, MediaMirrorConsentUserDecision.Cancelled);

        Assert.Equal(MediaMirrorConsentStatus.Cancelled, result.Status);
        Assert.Equal(MediaMirrorConsentReason.UserCancelled, result.Reason);
        Assert.Null(result.BindingToPersist);
    }

    [Fact]
    public void InvalidUserDecisionIsRefused()
    {
        var result = MediaMirrorConsentPolicy.Evaluate(CompleteBinding(), null, (MediaMirrorConsentUserDecision)99);

        Assert.Equal(MediaMirrorConsentStatus.Refused, result.Status);
        Assert.Equal(MediaMirrorConsentReason.InvalidUserDecision, result.Reason);
        Assert.Null(result.BindingToPersist);
    }

    private static MediaMirrorConsentBinding CompleteBinding() => new(
        "pc-a",
        "logical-media-1",
        "volume-guid-1",
        "owned-root-id-1",
        MediaConsentFileSystem.Ntfs,
        MediaConsentAclProtection.NtfsAclVerified);
}
