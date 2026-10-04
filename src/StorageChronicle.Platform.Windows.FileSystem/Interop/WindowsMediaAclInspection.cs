using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using StorageChronicle.Contracts;

namespace StorageChronicle.Platform.Windows.FileSystem.Interop;

/// <summary>Reads and evaluates Windows security descriptors without changing filesystem security.</summary>
internal static class WindowsMediaAclInspection
{
    private const uint SeFileObject = 1;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint GroupSecurityInformation = 0x00000002;
    private const uint DaclSecurityInformation = 0x00000004;
    private const byte SupportedAccessAceFlags = 0x1F;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint GenericExecute = 0x20000000;
    private const uint GenericAll = 0x10000000;
    private const uint FileWriteData = 0x00000002;
    private const uint FileAppendData = 0x00000004;
    private const uint FileWriteExtendedAttributes = 0x00000010;
    private const uint FileDeleteChild = 0x00000040;
    private const uint FileWriteAttributes = 0x00000100;
    private const uint Delete = 0x00010000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint AccessSystemSecurity = 0x01000000;
    private const uint MaximumAllowed = 0x02000000;
    private const uint GenericWriteMapping = 0x00120116;
    private const uint GenericReadMapping = 0x00120089;
    private const uint GenericExecuteMapping = 0x001200A0;
    private const uint GenericAllMapping = 0x001F01FF;
    private const uint DangerousRights = FileWriteData | FileAppendData | FileWriteExtendedAttributes |
                                         FileDeleteChild | FileWriteAttributes | Delete | WriteDac | WriteOwner |
                                         AccessSystemSecurity | MaximumAllowed | GenericRead | GenericWrite | GenericExecute | GenericAll;
    private const uint KnownFileRights = 0x001F01FF | AccessSystemSecurity | MaximumAllowed |
                                         GenericRead | GenericWrite | GenericExecute | GenericAll;
    private const uint ApprovedFileAppendRights = FileAppendData;
    private const uint ApprovedWriterDirectoryCreateRights = FileWriteData;
    private const uint ApprovedWritersRootAddSubdirectoryRights = FileAppendData;

    private static readonly SecurityIdentifier LocalSystemSid = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier OwnerRightsSid = new("S-1-3-4");

    /// <summary>One inspected object and its raw self-relative security descriptor.</summary>
    internal sealed record Entry(string RelativeName, byte[] Descriptor, bool IsDirectory);

    /// <summary>Result for one descriptor, exposed internally so policy tests need no live volume.</summary>
    internal sealed record Evaluation(bool IsUnknown, byte[]? Descriptor, IReadOnlyList<string> Findings);

    /// <summary>Returns an Unknown result without a partial fingerprint.</summary>
    internal static MediaMirrorAclInspection Unknown(string finding, int directoryCount = 0, int fileCount = 0) =>
        Unknown([finding], directoryCount, fileCount);

    /// <summary>Returns an Unknown result without a partial fingerprint.</summary>
    internal static MediaMirrorAclInspection Unknown(IReadOnlyList<string> findings, int directoryCount = 0, int fileCount = 0) =>
        new(MediaMirrorAclInspectionStatus.Unknown, null, directoryCount, fileCount, findings);

    /// <summary>Validates and canonicalizes the exact approved user's SID.</summary>
    internal static bool TryNormalizeApprovedUserSid(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            var sid = new SecurityIdentifier(value);
            if (Enum.GetValues<WellKnownSidType>().Any(sid.IsWellKnown))
                return false;
            normalized = sid.Value;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Reads the descriptor from an already-open handle and evaluates its DACL policy.</summary>
    internal static Evaluation ReadAndEvaluate(SafeFileHandle handle, string approvedSid, uint approvedUserAllowedRights)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var error = GetSecurityInfo(handle, SeFileObject,
            OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptorPointer);
        if (error != 0)
            return new Evaluation(true, null, [$"SecurityDescriptorReadFailed:{error}"]);

        try
        {
            if (descriptorPointer == IntPtr.Zero)
                return new Evaluation(true, null, ["SecurityDescriptorMissing"]);
            var length = GetSecurityDescriptorLength(descriptorPointer);
            if (length == 0 || length > int.MaxValue)
                return new Evaluation(true, null, ["SecurityDescriptorLengthInvalid"]);
            var descriptor = new byte[checked((int)length)];
            Marshal.Copy(descriptorPointer, descriptor, 0, descriptor.Length);
            return EvaluateDescriptor(descriptor, approvedSid, approvedUserAllowedRights);
        }
        finally
        {
            _ = LocalFree(descriptorPointer);
        }
    }

    /// <summary>Evaluates a synthetic or native descriptor using a conservative, deterministic DACL policy.</summary>
    internal static Evaluation EvaluateDescriptor(byte[] descriptorBytes, string approvedSid, uint approvedUserAllowedRights)
    {
        ArgumentNullException.ThrowIfNull(descriptorBytes);
        if (!TryNormalizeApprovedUserSid(approvedSid, out var normalizedSid))
            return new Evaluation(true, null, ["ApprovedUserSidInvalid"]);

        RawSecurityDescriptor descriptor;
        try
        {
            descriptor = new RawSecurityDescriptor(descriptorBytes, 0);
        }
        catch (Exception)
        {
            return new Evaluation(true, null, ["SecurityDescriptorMalformed"]);
        }

        if ((descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0)
            return new Evaluation(true, null, ["DaclNotPresent"]);
        if (descriptor.DiscretionaryAcl is null)
            return new Evaluation(false, descriptorBytes, ["NullDacl"]);
        if (descriptor.Owner is null)
            return new Evaluation(true, null, ["OwnerNotPresent"]);

        var approved = new SecurityIdentifier(normalizedSid);
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var hasOwnerRightsAce = false;
        foreach (GenericAce ace in descriptor.DiscretionaryAcl)
        {
            // Callback, object-specific, compound, audit, and custom ACEs are not interpreted as
            // allow/deny policy here. Treat any unrecognized form as incomplete evidence.
            if (ace is not CommonAce commonAce || commonAce.IsCallback ||
                commonAce.AceQualifier is not (AceQualifier.AccessAllowed or AceQualifier.AccessDenied))
                return new Evaluation(true, null, ["UnsupportedAceForm"]);
            if ((unchecked((byte)commonAce.AceFlags) & ~SupportedAccessAceFlags) != 0)
                return new Evaluation(true, null, ["UnsupportedAceFlags"]);

            if (commonAce.SecurityIdentifier == OwnerRightsSid && (commonAce.AceFlags & AceFlags.InheritOnly) == 0)
                hasOwnerRightsAce = true;

            if (commonAce.AceQualifier == AceQualifier.AccessDenied) continue;
            if ((commonAce.AceFlags & AceFlags.InheritOnly) != 0) continue;

            var mask = MapGenericRights(unchecked((uint)commonAce.AccessMask));
            if ((mask & ~KnownFileRights) != 0)
                return new Evaluation(true, null, ["UnsupportedAccessMask"]);
            var dangerous = mask & DangerousRights;
            if (dangerous == 0) continue;

            var sid = commonAce.SecurityIdentifier;
            if (sid == LocalSystemSid || sid == AdministratorsSid) continue;

            if (sid == approved)
            {
                if ((dangerous & ~approvedUserAllowedRights) != 0)
                    findings.Add($"ApprovedUserExcessRights:{sid.Value}:{dangerous:X8}");
                continue;
            }

            findings.Add($"UnapprovedPrincipalWriteRights:{sid.Value}:{dangerous:X8}");
        }

        // Windows grants an object's owner implicit READ_CONTROL and WRITE_DAC unless an
        // OWNER RIGHTS ACE is present. Inspecting only explicit allow ACEs would miss that
        // effective ACL-management capability, so non-service owners must have an explicit
        // OWNER RIGHTS policy. Any explicit WRITE_DAC grant through that SID is caught above.
        if (descriptor.Owner != LocalSystemSid && descriptor.Owner != AdministratorsSid && !hasOwnerRightsAce)
            findings.Add($"OwnerImplicitWriteDac:{descriptor.Owner.Value}");

        return new Evaluation(false, descriptorBytes, findings.ToArray());
    }

    /// <summary>Evaluates a descriptor with the standard narrow directory-create or file-append mask.</summary>
    internal static Evaluation EvaluateDescriptor(byte[] descriptorBytes, string approvedSid, bool isDirectory) =>
        EvaluateDescriptor(descriptorBytes, approvedSid,
            isDirectory ? FileWriteData | FileAppendData : ApprovedFileAppendRights);

    /// <summary>Returns the exact approved-user rights allowed at one existing product-history location.</summary>
    internal static uint ApprovedUserRightsForPath(string relativeName, bool isDirectory)
    {
        var parts = relativeName.Split('\\', StringSplitOptions.None);
        if (isDirectory && parts.Length == 2 &&
            parts[0].Equals(".StorageChronicle", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("writers", StringComparison.OrdinalIgnoreCase))
            return ApprovedWritersRootAddSubdirectoryRights;

        if (parts.Length == 3 && parts[0].Equals(".StorageChronicle", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("writers", StringComparison.OrdinalIgnoreCase))
        {
            if (isDirectory) return ApprovedWriterDirectoryCreateRights;
            return 0;
        }

        if (!isDirectory && parts.Length == 4 &&
            parts[0].Equals(".StorageChronicle", StringComparison.OrdinalIgnoreCase) &&
            parts[1].Equals("writers", StringComparison.OrdinalIgnoreCase) && IsHistoryFileName(parts[3]))
            return ApprovedFileAppendRights;

        return 0;
    }

    private static bool IsHistoryFileName(string name)
    {
        var extension = Path.GetExtension(name);
        var stem = Path.GetFileNameWithoutExtension(name);
        if (extension.Equals(".seg", StringComparison.OrdinalIgnoreCase))
            return Guid.TryParseExact(stem, "N", out _);
        if (extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(stem, "N", out _))
            return true;

        if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase))
            return false;

        if (name.Equals("manifest-A.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("manifest-B.json", StringComparison.OrdinalIgnoreCase))
            return true;

        var segments = stem.Split('-', StringSplitOptions.None);
        return segments.Length == 3 && segments[0].Equals("manifest", StringComparison.OrdinalIgnoreCase) &&
               segments[1] is "A" or "B" && Guid.TryParseExact(segments[2], "N", out _);
    }

    /// <summary>Computes a stable descriptor-and-relative-name fingerprint, never a file-content hash.</summary>
    internal static string Fingerprint(IReadOnlyCollection<Entry> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("StorageChronicle.MediaAclFingerprint.v1");
            writer.Write(entries.Count);
            writer.Write(entries.Count(static value => value.IsDirectory));
            writer.Write(entries.Count(static value => !value.IsDirectory));
            foreach (var entry in entries.OrderBy(static value => value.RelativeName, StringComparer.Ordinal))
            {
                WriteUtf16(writer, entry.RelativeName);
                writer.Write(entry.IsDirectory);
                writer.Write(entry.Descriptor.Length);
                writer.Write(entry.Descriptor);
            }
        }

        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    /// <summary>Checks the filesystem type through the expected volume-GUID root.</summary>
    internal static bool IsNtfs(string volumeGuidPath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var name = new char[256];
        var fileSystem = new char[64];
        uint serial = 0;
        uint maximumComponentLength = 0;
        uint flags = 0;
        return GetVolumeInformation(volumeGuidPath, name, (uint)name.Length, ref serial,
                   ref maximumComponentLength, ref flags, fileSystem, (uint)fileSystem.Length) &&
               new string(fileSystem).TrimEnd('\0').Equals("NTFS", StringComparison.OrdinalIgnoreCase);
    }

    private static uint MapGenericRights(uint mask)
    {
        if ((mask & GenericAll) != 0) mask = (mask & ~GenericAll) | GenericAllMapping;
        if ((mask & GenericWrite) != 0) mask = (mask & ~GenericWrite) | GenericWriteMapping;
        if ((mask & GenericRead) != 0) mask = (mask & ~GenericRead) | GenericReadMapping;
        if ((mask & GenericExecute) != 0) mask = (mask & ~GenericExecute) | GenericExecuteMapping;
        return mask;
    }

    private static void WriteUtf16(BinaryWriter writer, string value)
    {
        // Hash raw UTF-16 code units to preserve unusual but legal Windows filename sequences.
        var bytes = new byte[checked(value.Length * sizeof(char))];
        for (var index = 0; index < value.Length; index++)
        {
            var codeUnit = value[index];
            bytes[index * sizeof(char)] = (byte)codeUnit;
            bytes[index * sizeof(char) + 1] = (byte)(codeUnit >> 8);
        }
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    [DllImport("Advapi32.dll", SetLastError = false)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, uint objectType, uint securityInformation,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr securityDescriptor);

    [DllImport("Advapi32.dll", SetLastError = false)]
    private static extern uint GetSecurityDescriptorLength(IntPtr securityDescriptor);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeInformationW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(string rootPathName, [Out] char[] volumeNameBuffer,
        uint volumeNameSize, ref uint volumeSerialNumber, ref uint maximumComponentLength,
        ref uint fileSystemFlags, [Out] char[] fileSystemNameBuffer, uint fileSystemNameSize);
}
