using System.Security.AccessControl;
using System.Security.Principal;
using StorageChronicle.Contracts;
using StorageChronicle.Platform.Windows.FileSystem.Interop;
using Xunit;

namespace StorageChronicle.Platform.Windows.FileSystem.Tests;

public sealed class WindowsMediaAclPolicyTests
{
    private static readonly SecurityIdentifier ApprovedUser = new("S-1-5-21-100-200-300-1001");
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

    [Fact]
    public void SystemAdministratorsAndNarrowApprovedUserCreateAppendRightsAreVerified()
    {
        var descriptor = Descriptor(
            Allow(System, 0x001F01FF),
            Allow(Administrators, 0x001F01FF),
            Allow(ApprovedUser, 0x00000006));

        var directory = WindowsMediaAclInspection.EvaluateDescriptor(descriptor, ApprovedUser.Value, isDirectory: true);
        var file = WindowsMediaAclInspection.EvaluateDescriptor(
            Descriptor(Allow(System, 0x001F01FF), Allow(Administrators, 0x001F01FF), Allow(ApprovedUser, 0x00000004)),
            ApprovedUser.Value, isDirectory: false);

        Assert.False(directory.IsUnknown);
        Assert.Empty(directory.Findings);
        Assert.False(file.IsUnknown);
        Assert.Empty(file.Findings);
    }

    [Fact]
    public void BroadPrincipalGenericWriteIsUnsafe()
    {
        var result = WindowsMediaAclInspection.EvaluateDescriptor(
            Descriptor(Allow(Users, unchecked((int)0x40000000))), ApprovedUser.Value, isDirectory: true);

        Assert.False(result.IsUnknown);
        Assert.Contains(result.Findings, finding => finding.StartsWith("UnapprovedPrincipalWriteRights:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x00010000)] // DELETE
    [InlineData(0x00000040)] // FILE_DELETE_CHILD
    public void BroadPrincipalDeleteRightsAreUnsafe(int rights)
    {
        var result = WindowsMediaAclInspection.EvaluateDescriptor(
            Descriptor(Allow(Users, rights)), ApprovedUser.Value, isDirectory: true);

        Assert.False(result.IsUnknown);
        Assert.Contains(result.Findings, finding =>
            finding.StartsWith("UnapprovedPrincipalWriteRights:", StringComparison.Ordinal) &&
            finding.EndsWith(rights.ToString("X8", global::System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("S-1-5-32-545")] // BUILTIN\Users
    [InlineData("S-1-5-11")] // Authenticated Users
    [InlineData("S-1-1-0")] // Everyone
    [InlineData("S-1-5-21-100-200-300-1002")] // Any unapproved SID
    public void BroadOrUnapprovedPrincipalsCannotReceiveWriteRights(string sidValue)
    {
        var sid = new SecurityIdentifier(sidValue);
        var result = WindowsMediaAclInspection.EvaluateDescriptor(
            Descriptor(Allow(sid, 0x00000002)), ApprovedUser.Value, isDirectory: true);

        Assert.False(result.IsUnknown);
        Assert.Contains(result.Findings, finding => finding.StartsWith("UnapprovedPrincipalWriteRights:", StringComparison.Ordinal));
    }

    [Fact]
    public void ApprovedUserCannotCreateAtVolumeRootParent()
    {
        var result = WindowsMediaAclInspection.EvaluateDescriptor(
            Descriptor(Allow(ApprovedUser, 0x00000002)), ApprovedUser.Value, approvedUserAllowedRights: 0);

        Assert.False(result.IsUnknown);
        Assert.Contains(result.Findings, finding => finding.StartsWith("ApprovedUserExcessRights:", StringComparison.Ordinal));
    }

    [Fact]
    public void ApprovedUserDeleteAndWriteDacRightsAreUnsafe()
    {
        var result = WindowsMediaAclInspection.EvaluateDescriptor(
            Descriptor(Allow(ApprovedUser, 0x00050000)), ApprovedUser.Value, isDirectory: true);

        Assert.False(result.IsUnknown);
        Assert.Contains(result.Findings, finding => finding.StartsWith("ApprovedUserExcessRights:", StringComparison.Ordinal));
    }

    [Fact]
    public void ApprovedUserCannotWriteExistingFileData()
    {
        var result = WindowsMediaAclInspection.EvaluateDescriptor(
            Descriptor(Allow(ApprovedUser, 0x00000002)), ApprovedUser.Value, isDirectory: false);

        Assert.False(result.IsUnknown);
        Assert.Contains(result.Findings, finding => finding.StartsWith("ApprovedUserExcessRights:", StringComparison.Ordinal));
    }

    [Fact]
    public void ApprovedUserOwnerWithoutOwnerRightsAceIsUnsafe()
    {
        var result = WindowsMediaAclInspection.EvaluateDescriptor(
            DescriptorWithOwner(ApprovedUser, Allow(ApprovedUser, 0x00000004)), ApprovedUser.Value, isDirectory: false);

        Assert.False(result.IsUnknown);
        Assert.Contains(result.Findings, finding => finding.StartsWith("OwnerImplicitWriteDac:", StringComparison.Ordinal));
    }

    [Fact]
    public void ApprovedUserAppendIsRejectedOnWriterOwnershipMarker()
    {
        var result = WindowsMediaAclInspection.EvaluateDescriptor(
            Descriptor(Allow(ApprovedUser, 0x00000004)), ApprovedUser.Value,
            WindowsMediaAclInspection.ApprovedUserRightsForPath(
                ".StorageChronicle\\writers\\pc-id\\.writer-owner.json", isDirectory: false));

        Assert.False(result.IsUnknown);
        Assert.Contains(result.Findings, finding => finding.StartsWith("ApprovedUserExcessRights:", StringComparison.Ordinal));
    }

    [Fact]
    public void ExplicitOwnerRightsDenyCanConstrainApprovedUserOwner()
    {
        var result = WindowsMediaAclInspection.EvaluateDescriptor(
            DescriptorWithOwner(ApprovedUser,
                new CommonAce(AceFlags.None, AceQualifier.AccessDenied, 0x00040000, OwnerRights, false, null),
                Allow(ApprovedUser, 0x00000004)), ApprovedUser.Value, isDirectory: false);

        Assert.False(result.IsUnknown);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void InheritOnlyRightsAreCheckedOnTheDescendantObjectInsteadOfTheParent()
    {
        var parent = WindowsMediaAclInspection.EvaluateDescriptor(
            Descriptor(new CommonAce(AceFlags.InheritOnly | AceFlags.ObjectInherit,
                AceQualifier.AccessAllowed, 0x00000004, ApprovedUser, false, null)),
            ApprovedUser.Value, approvedUserAllowedRights: 0);
        var childFile = WindowsMediaAclInspection.EvaluateDescriptor(
            Descriptor(Allow(ApprovedUser, 0x00000004)), ApprovedUser.Value, approvedUserAllowedRights: 0x00000004);

        Assert.False(parent.IsUnknown);
        Assert.Empty(parent.Findings);
        Assert.False(childFile.IsUnknown);
        Assert.Empty(childFile.Findings);
    }

    [Fact]
    public void NullDaclIsUnsafe()
    {
        var descriptor = new RawSecurityDescriptor(
            ControlFlags.SelfRelative | ControlFlags.DiscretionaryAclPresent,
            ApprovedUser, null, null, null);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);

        var result = WindowsMediaAclInspection.EvaluateDescriptor(bytes, ApprovedUser.Value, isDirectory: true);

        Assert.False(result.IsUnknown);
        Assert.Equal("NullDacl", Assert.Single(result.Findings));
    }

    [Fact]
    public void MissingDaclIsUnknown()
    {
        var descriptor = new RawSecurityDescriptor(ControlFlags.SelfRelative, ApprovedUser, null, null, null);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);

        var result = WindowsMediaAclInspection.EvaluateDescriptor(bytes, ApprovedUser.Value, isDirectory: true);

        Assert.True(result.IsUnknown);
        Assert.Contains("DaclNotPresent", result.Findings);
    }

    [Fact]
    public void MalformedDescriptorIsUnknown()
    {
        var result = WindowsMediaAclInspection.EvaluateDescriptor([0x01, 0x02, 0x03], ApprovedUser.Value, isDirectory: true);

        Assert.True(result.IsUnknown);
        Assert.Contains("SecurityDescriptorMalformed", result.Findings);
    }

    [Fact]
    public void UnsupportedAceFormIsUnknown()
    {
        var acl = new RawAcl(4, 1);
        acl.InsertAce(0, new CustomAce((AceType)0x11, AceFlags.None, [0x01, 0x02, 0x03, 0x04]));
        var descriptor = new RawSecurityDescriptor(ControlFlags.SelfRelative | ControlFlags.DiscretionaryAclPresent,
            ApprovedUser, null, null, acl);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);

        var result = WindowsMediaAclInspection.EvaluateDescriptor(bytes, ApprovedUser.Value, isDirectory: true);

        Assert.True(result.IsUnknown);
        Assert.Contains("UnsupportedAceForm", result.Findings);
    }

    [Fact]
    public void UnsupportedAccessMaskIsUnknown()
    {
        var result = WindowsMediaAclInspection.EvaluateDescriptor(
            Descriptor(Allow(ApprovedUser, unchecked((int)0x08000000))), ApprovedUser.Value, isDirectory: true);

        Assert.True(result.IsUnknown);
        Assert.Contains("UnsupportedAccessMask", result.Findings);
    }

    [Fact]
    public void UnsupportedAccessAceFlagsAreUnknown()
    {
        var result = WindowsMediaAclInspection.EvaluateDescriptor(
            Descriptor(new CommonAce(AceFlags.SuccessfulAccess, AceQualifier.AccessAllowed,
                0x00000004, ApprovedUser, false, null)), ApprovedUser.Value, isDirectory: false);

        Assert.True(result.IsUnknown);
        Assert.Contains("UnsupportedAceFlags", result.Findings);
    }

    [Fact]
    public void FingerprintIsStableAcrossEnumerationOrderAndIncludesRelativeNames()
    {
        var descriptor = Descriptor(Allow(System, 0x001F01FF));
        var left = new WindowsMediaAclInspection.Entry(".StorageChronicle\\a", descriptor, false);
        var right = new WindowsMediaAclInspection.Entry(".StorageChronicle\\b", descriptor, false);

        var first = WindowsMediaAclInspection.Fingerprint([left, right]);
        var reordered = WindowsMediaAclInspection.Fingerprint([right, left]);
        var renamed = WindowsMediaAclInspection.Fingerprint([left with { RelativeName = ".StorageChronicle\\c" }, right]);
        var differentCount = WindowsMediaAclInspection.Fingerprint([left]);

        Assert.Equal(first, reordered);
        Assert.NotEqual(first, renamed);
        Assert.NotEqual(first, differentCount);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void ApprovedUserWriteMasksAreLimitedToWriterHistoryLocations()
    {
        Assert.Equal(0u, WindowsMediaAclInspection.ApprovedUserRightsForPath("", isDirectory: true));
        Assert.Equal(0u, WindowsMediaAclInspection.ApprovedUserRightsForPath(".StorageChronicle", isDirectory: true));
        Assert.Equal(0x4u, WindowsMediaAclInspection.ApprovedUserRightsForPath(".StorageChronicle\\writers", isDirectory: true));
        Assert.Equal(0x2u, WindowsMediaAclInspection.ApprovedUserRightsForPath(".StorageChronicle\\writers\\pc-id", isDirectory: true));
        Assert.Equal(0x4u, WindowsMediaAclInspection.ApprovedUserRightsForPath(
            ".StorageChronicle\\writers\\pc-id\\0123456789abcdef0123456789abcdef.seg", isDirectory: false));
        Assert.Equal(0x4u, WindowsMediaAclInspection.ApprovedUserRightsForPath(
            ".StorageChronicle\\writers\\pc-id\\manifest-A-0123456789abcdef0123456789abcdef.json", isDirectory: false));
        Assert.Equal(0x4u, WindowsMediaAclInspection.ApprovedUserRightsForPath(
            ".StorageChronicle\\writers\\pc-id\\manifest-A-0123456789abcdef0123456789abcdef.tmp", isDirectory: false));
        Assert.Equal(0x4u, WindowsMediaAclInspection.ApprovedUserRightsForPath(
            ".StorageChronicle\\writers\\pc-id\\0123456789abcdef0123456789abcdef.tmp", isDirectory: false));
        Assert.Equal(0u, WindowsMediaAclInspection.ApprovedUserRightsForPath(
            ".StorageChronicle\\writers\\pc-id\\.writer-owner.json", isDirectory: false));
        Assert.Equal(0u, WindowsMediaAclInspection.ApprovedUserRightsForPath(
            ".StorageChronicle\\writers\\pc-id\\unrecognized.json", isDirectory: false));
        Assert.Equal(0u, WindowsMediaAclInspection.ApprovedUserRightsForPath(
            ".StorageChronicle\\writers\\pc-id\\child", isDirectory: true));
    }

    [Fact]
    public void InvalidOrBroadApprovedSidIsRejectedBeforeInspection()
    {
        Assert.False(WindowsMediaAclInspection.TryNormalizeApprovedUserSid("not-a-sid", out _));
        Assert.False(WindowsMediaAclInspection.TryNormalizeApprovedUserSid(Users.Value, out _));
        Assert.False(WindowsMediaAclInspection.TryNormalizeApprovedUserSid("S-1-3-0", out _)); // CREATOR OWNER
        Assert.False(WindowsMediaAclInspection.TryNormalizeApprovedUserSid("S-1-3-4", out _)); // OWNER RIGHTS
    }

    private static CommonAce Allow(SecurityIdentifier sid, int mask) =>
        new(AceFlags.None, AceQualifier.AccessAllowed, mask, sid, isCallback: false, opaque: null);

    private static byte[] Descriptor(params GenericAce[] aces)
        => DescriptorWithOwner(System, aces);

    private static byte[] DescriptorWithOwner(SecurityIdentifier owner, params GenericAce[] aces)
    {
        var acl = new RawAcl(4, aces.Length);
        foreach (var ace in aces) acl.InsertAce(acl.Count, ace);
        var descriptor = new RawSecurityDescriptor(ControlFlags.SelfRelative | ControlFlags.DiscretionaryAclPresent,
            owner, null, null, acl);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }
}
