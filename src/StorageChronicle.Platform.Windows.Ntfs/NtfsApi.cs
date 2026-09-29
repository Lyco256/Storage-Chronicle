using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.Ntfs;

/// <summary>Documented native structure sizes used by the FSCTL boundary.</summary>
public static class NtfsApiLayout
{
    /// <summary>USN_JOURNAL_DATA_V0 size in bytes.</summary>
    public const int UsnJournalDataV0Size = 56;
    /// <summary>READ_USN_JOURNAL_DATA_V0 size in bytes.</summary>
    public const int ReadUsnJournalDataV0Size = 44;
    /// <summary>MFT_ENUM_DATA_V0 size in bytes.</summary>
    public const int MftEnumDataV0Size = 24;
}

/// <summary>Detects NTFS API capability on Windows 10 22H2 or newer without requiring Windows 11.</summary>
public sealed class Windows10CapabilityDetector : IPlatformCapabilities
{
    /// <inheritdoc />
    public bool IsSupported(string capability) => capability switch
    {
        "FsctlQueryUsnJournal" or "FsctlReadUsnJournal" or "FsctlEnumUsnData" or "MftEnumeration" => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041),
        _ => false
    };
}

/// <summary>Documented NTFS control codes used by this collector.</summary>
public static class NtfsControlCode
{
    /// <summary>Queries the existing USN journal without creating one.</summary>
    public const uint QueryUsnJournal = 0x000900F4;
    /// <summary>Reads existing USN records and can wait for new records.</summary>
    public const uint ReadUsnJournal = 0x000900BB;
    /// <summary>Enumerates USN/MFT records through the public control API.</summary>
    public const uint EnumUsnData = 0x000900B3;
}

/// <summary>Classifies expected NTFS API failures so one inaccessible volume does not stop the agent.</summary>
public enum NtfsApiStatus { Success, JournalNotCreated, AccessDenied, MediaRemoved, InvalidData, Failure }

/// <summary>Returns status and native error information from one NTFS control call.</summary>
public sealed record NtfsApiCallResult(NtfsApiStatus Status, int BytesReturned, int Win32Error)
{
    /// <summary>Gets whether the native operation succeeded.</summary>
    public bool Succeeded => Status == NtfsApiStatus.Success;
}

/// <summary>Contains the documented USN_JOURNAL_DATA_V0 values.</summary>
public sealed record UsnJournalData(
    ulong JournalId,
    long FirstUsn,
    long NextUsn,
    long LowestValidUsn,
    long MaxUsn,
    uint MaximumSize,
    uint AllocationDelta,
    ushort MinSupportedMajorVersion,
    ushort MaxSupportedMajorVersion);

/// <summary>Input values for READ_USN_JOURNAL_DATA_V0.</summary>
public readonly record struct ReadUsnJournalRequest(
    long StartUsn,
    int ReasonMask,
    bool ReturnOnlyOnClose,
    TimeSpan Timeout,
    ulong BytesToWaitFor,
    ulong JournalId,
    ushort MinMajorVersion,
    ushort MaxMajorVersion);

/// <summary>Input values for MFT_ENUM_DATA_V0 used by FSCTL_ENUM_USN_DATA.</summary>
public readonly record struct EnumUsnDataRequest(ulong StartFileReferenceNumber, long LowUsn, long HighUsn);

/// <summary>Native API boundary for query, read, and public USN/MFT enumeration.</summary>
public interface INtfsApi
{
    /// <summary>Opens a volume handle for the supplied Windows device path.</summary>
    SafeFileHandle OpenVolume(string devicePath);
    /// <summary>Queries an existing journal and never creates or resizes it.</summary>
    NtfsApiCallResult QueryUsnJournal(SafeFileHandle volumeHandle, out UsnJournalData? data);
    /// <summary>Reads a bounded output buffer through FSCTL_READ_USN_JOURNAL.</summary>
    NtfsApiCallResult ReadUsnJournal(SafeFileHandle volumeHandle, ReadUsnJournalRequest request, byte[] outputBuffer, out int bytesReturned);
    /// <summary>Enumerates a bounded output buffer through FSCTL_ENUM_USN_DATA.</summary>
    NtfsApiCallResult EnumerateUsnData(SafeFileHandle volumeHandle, EnumUsnDataRequest request, byte[] outputBuffer, out int bytesReturned);
}

/// <summary>Throws only for an NTFS access or media failure that the caller must report.</summary>
public sealed class NtfsAccessException : IOException
{
    /// <summary>Initializes an NTFS access exception.</summary>
    public NtfsAccessException(NtfsApiStatus status, int win32Error, string message) : base(message) { Status = status; Win32Error = win32Error; }
    /// <summary>Gets the classified status.</summary>
    public NtfsApiStatus Status { get; }
    /// <summary>Gets the native error code.</summary>
    public int Win32Error { get; }
}

/// <summary>Windows implementation of the constrained NTFS native boundary.</summary>
public sealed class WindowsNtfsApi : INtfsApi
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidFunction = 1;
    private const int ErrorInvalidHandle = 6;
    private const int ErrorNotReady = 21;
    private const int ErrorDeviceNotConnected = 1167;
    private const int ErrorJournalNotActive = 1179;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 1;
    private const uint FileShareWrite = 2;
    private const uint FileShareDelete = 4;
    private const uint OpenExisting = 3;

    /// <inheritdoc />
    public SafeFileHandle OpenVolume(string devicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePath);
        var handle = NativeMethods.CreateFile(devicePath, GenericRead, FileShareRead | FileShareWrite | FileShareDelete, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new NtfsAccessException(MapStatus(error), error, new Win32Exception(error).Message);
        }

        return handle;
    }

    /// <inheritdoc />
    public NtfsApiCallResult QueryUsnJournal(SafeFileHandle volumeHandle, out UsnJournalData? data)
    {
        var output = new byte[56];
        var result = Invoke(volumeHandle, NtfsControlCode.QueryUsnJournal, ReadOnlySpan<byte>.Empty, output, out var bytesReturned);
        data = result.Succeeded && bytesReturned >= output.Length ? ParseJournalData(output) : null;
        return data is null && result.Succeeded ? result with { Status = NtfsApiStatus.InvalidData } : result;
    }

    /// <inheritdoc />
    public NtfsApiCallResult ReadUsnJournal(SafeFileHandle volumeHandle, ReadUsnJournalRequest request, byte[] outputBuffer, out int bytesReturned)
    {
        ArgumentNullException.ThrowIfNull(outputBuffer);
        var input = new byte[44];
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(0, 8), request.StartUsn);
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(8, 4), request.ReasonMask);
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(12, 4), request.ReturnOnlyOnClose ? 1 : 0);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(16, 8), (ulong)Math.Max(0, request.Timeout.TotalMilliseconds) * 10_000);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(24, 8), request.BytesToWaitFor);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32, 8), request.JournalId);
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(40, 2), request.MinMajorVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(42, 2), request.MaxMajorVersion);
        return Invoke(volumeHandle, NtfsControlCode.ReadUsnJournal, input, outputBuffer, out bytesReturned);
    }

    /// <inheritdoc />
    public NtfsApiCallResult EnumerateUsnData(SafeFileHandle volumeHandle, EnumUsnDataRequest request, byte[] outputBuffer, out int bytesReturned)
    {
        ArgumentNullException.ThrowIfNull(outputBuffer);
        var input = new byte[24];
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(0, 8), request.StartFileReferenceNumber);
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(8, 8), request.LowUsn);
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(16, 8), request.HighUsn);
        return Invoke(volumeHandle, NtfsControlCode.EnumUsnData, input, outputBuffer, out bytesReturned);
    }

    private static NtfsApiCallResult Invoke(SafeFileHandle handle, uint controlCode, ReadOnlySpan<byte> input, byte[] output, out int bytesReturned)
    {
        var inputArray = input.ToArray();
        if (NativeMethods.DeviceIoControl(handle, controlCode, inputArray, (uint)inputArray.Length, output, (uint)output.Length, out bytesReturned, IntPtr.Zero))
        {
            return new NtfsApiCallResult(NtfsApiStatus.Success, bytesReturned, 0);
        }

        var error = Marshal.GetLastWin32Error();
        return new NtfsApiCallResult(MapStatus(error), bytesReturned, error);
    }

    private static UsnJournalData ParseJournalData(byte[] output) => new(
        BinaryPrimitives.ReadUInt64LittleEndian(output.AsSpan(0, 8)),
        BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(8, 8)),
        BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(16, 8)),
        BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(24, 8)),
        BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(32, 8)),
        BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(40, 4)),
        BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(44, 4)),
        BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(48, 2)),
        BinaryPrimitives.ReadUInt16LittleEndian(output.AsSpan(50, 2)));

    private static NtfsApiStatus MapStatus(int error) => error switch
    {
        ErrorJournalNotActive => NtfsApiStatus.JournalNotCreated,
        ErrorAccessDenied => NtfsApiStatus.AccessDenied,
        ErrorNotReady or ErrorDeviceNotConnected or ErrorInvalidHandle => NtfsApiStatus.MediaRemoved,
        ErrorInvalidFunction => NtfsApiStatus.Failure,
        _ => NtfsApiStatus.Failure
    };

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(SafeFileHandle device, uint controlCode, byte[] inputBuffer, uint inputBufferSize, byte[] outputBuffer, uint outputBufferSize, out int bytesReturned, IntPtr overlapped);
    }
}
