using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StorageChronicle.Platform.Windows.FileSystem.Interop;

namespace StorageChronicle.Platform.Windows.FileSystem.Snapshot;

/// <summary>Reads standard metadata through the production Windows metadata boundary.</summary>
public sealed class WindowsFileMetadataReader
{
    private readonly IWindowsFileMetadataNative native;

    /// <summary>Initializes a production metadata reader.</summary>
    public WindowsFileMetadataReader() : this(new Interop.WindowsNativeApi()) { }

    /// <summary>Initializes a metadata reader around an injectable native boundary.</summary>
    public WindowsFileMetadataReader(IWindowsFileMetadataNative native)
    {
        this.native = native ?? throw new ArgumentNullException(nameof(native));
    }

    /// <summary>Reads identity and standard metadata without opening file contents.</summary>
    public NativeFileMetadataRecord Read(string path, string? parentPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return native.ReadMetadata(path, parentPath);
    }

    /// <summary>Opens a directory metadata handle for an optional I/O priority hint.</summary>
    public SafeFileHandle OpenDirectoryHandle(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return native.OpenDirectory(path);
    }

    /// <summary>Opens a metadata-only handle for a candidate file or directory.</summary>
    public SafeFileHandle OpenMetadataHandle(string path, bool directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return native.OpenMetadata(path, directory);
    }
}

/// <summary>Reports one scoped SeBackupPrivilege attempt without changing the process token permanently.</summary>
public sealed record ReconciliationPrivilegeResult(bool Enabled, int? Win32Error, string? FailureReason);

/// <summary>Enables the metadata-only backup privilege on an impersonated thread for one bounded scope.</summary>
public sealed class WindowsSeBackupPrivilegeScope : IDisposable
{
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenImpersonate = 0x0004;
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenAllRequired = TokenDuplicate | TokenQuery | TokenImpersonate | TokenAdjustPrivileges;
    private const uint SecurityImpersonation = 2;
    private const uint TokenImpersonation = 2;
    private const uint SePrivilegeEnabled = 0x00000002;
    private const int ErrorNotAllAssigned = 1300;
    private IntPtr token;
    private bool impersonating;
    private int disposed;

    /// <summary>Gets the result of the bounded privilege attempt.</summary>
    public ReconciliationPrivilegeResult Result { get; }

    private WindowsSeBackupPrivilegeScope(ReconciliationPrivilegeResult result, IntPtr token, bool impersonating)
    {
        Result = result;
        this.token = token;
        this.impersonating = impersonating;
    }

    /// <summary>Attempts to enable SeBackupPrivilege only for the current reconciliation thread.</summary>
    public static WindowsSeBackupPrivilegeScope Enter()
    {
        if (!OperatingSystem.IsWindows()) return new WindowsSeBackupPrivilegeScope(new(false, null, "Windows is required."), IntPtr.Zero, false);

        var processToken = IntPtr.Zero;
        var duplicate = IntPtr.Zero;
        var transferred = false;
        try
        {
            if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), TokenAllRequired, out processToken))
            {
                return Failed(Marshal.GetLastWin32Error(), "OpenProcessToken failed.");
            }

            if (!NativeMethods.DuplicateTokenEx(processToken, TokenAllRequired, IntPtr.Zero, SecurityImpersonation, TokenImpersonation, out duplicate))
            {
                return Failed(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed.");
            }

            var luid = default(Luid);
            if (!NativeMethods.LookupPrivilegeValue(null, "SeBackupPrivilege", ref luid))
            {
                return Failed(Marshal.GetLastWin32Error(), "LookupPrivilegeValue failed.");
            }

            var privileges = new TokenPrivileges(1, new LuidAndAttributes(luid, SePrivilegeEnabled));
            if (!NativeMethods.AdjustTokenPrivileges(duplicate, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                return Failed(Marshal.GetLastWin32Error(), "AdjustTokenPrivileges failed.");
            }

            var adjustmentError = Marshal.GetLastWin32Error();
            if (adjustmentError == ErrorNotAllAssigned)
            {
                return Failed(adjustmentError, "SeBackupPrivilege is not present in the token.");
            }

            if (!NativeMethods.SetThreadToken(IntPtr.Zero, duplicate))
            {
                return Failed(Marshal.GetLastWin32Error(), "SetThreadToken failed.");
            }

            NativeMethods.CloseHandle(processToken);
            processToken = IntPtr.Zero;
            transferred = true;
            return new WindowsSeBackupPrivilegeScope(new(true, null, null), duplicate, true);
        }
        catch (Win32Exception exception)
        {
            return Failed(exception.NativeErrorCode, exception.Message);
        }
        finally
        {
            if (processToken != IntPtr.Zero) NativeMethods.CloseHandle(processToken);
            if (duplicate != IntPtr.Zero && !transferred) NativeMethods.CloseHandle(duplicate);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (impersonating)
        {
            _ = NativeMethods.RevertToSelf();
            impersonating = false;
        }

        if (token != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(token);
            token = IntPtr.Zero;
        }
    }

    private static WindowsSeBackupPrivilegeScope Failed(int error, string reason) => new(new(false, error, reason), IntPtr.Zero, false);

    private static class NativeMethods
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool DuplicateTokenEx(IntPtr existingToken, uint desiredAccess, IntPtr tokenAttributes, uint impersonationLevel, uint tokenType, out IntPtr newToken);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool LookupPrivilegeValue(string? systemName, string name, ref Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges, ref TokenPrivileges newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool SetThreadToken(IntPtr thread, IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool RevertToSelf();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);

    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes(Luid luid, uint attributes)
    {
        public Luid Luid = luid;
        public uint Attributes = attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges(uint count, LuidAndAttributes privilege)
    {
        public uint PrivilegeCount = count;
        public LuidAndAttributes Privileges = privilege;
    }
}

/// <summary>Reports whether a reconciliation worker entered Windows background I/O mode.</summary>
public sealed record ReconciliationPriorityResult(bool BackgroundModeEnabled, int? BackgroundStartError, int? BackgroundEndError, int IoHintAttempts, int IoHintSuccesses, int IoHintFailures);

/// <summary>Scopes worker and optional file-handle I/O priority without dropping events.</summary>
public sealed class WindowsReconciliationPriorityScope : IDisposable
{
    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;
    private const int FileIoPriorityHintInfo = 43;
    private const int FileIoPriorityHintLow = 1;
    private int disposed;
    private int ioAttempts;
    private int ioSuccesses;
    private int ioFailures;
    private int? endError;
    private readonly ReconciliationPriorityResult initialResult;

    /// <summary>Gets the result accumulated by this scope.</summary>
    public ReconciliationPriorityResult Result => initialResult with { BackgroundEndError = endError, IoHintAttempts = ioAttempts, IoHintSuccesses = ioSuccesses, IoHintFailures = ioFailures };

    private WindowsReconciliationPriorityScope(bool background, int? startError)
    {
        initialResult = new(background, startError, null, 0, 0, 0);
    }

    /// <summary>Enters thread background mode when running on Windows.</summary>
    public static WindowsReconciliationPriorityScope Enter()
    {
        if (!OperatingSystem.IsWindows()) return new(false, null);
        var enabled = NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(), ThreadModeBackgroundBegin);
        return new WindowsReconciliationPriorityScope(enabled, enabled ? null : Marshal.GetLastWin32Error());
    }

    /// <summary>Attempts to set a low file I/O priority hint when a suitable handle is available.</summary>
    public bool TrySetLowFileIoPriority(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        Interlocked.Increment(ref ioAttempts);
        var hint = new FileIoPriorityHint { PriorityHint = FileIoPriorityHintLow };
        var success = OperatingSystem.IsWindows() && NativeMethods.SetFileInformationByHandle(handle, FileIoPriorityHintInfo, ref hint, Marshal.SizeOf<FileIoPriorityHint>());
        if (success) Interlocked.Increment(ref ioSuccesses);
        else Interlocked.Increment(ref ioFailures);
        return success;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        if (Result.BackgroundModeEnabled && !NativeMethods.SetThreadPriority(NativeMethods.GetCurrentThread(), ThreadModeBackgroundEnd)) endError = Marshal.GetLastWin32Error();
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetThreadPriority(IntPtr thread, int priority);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(SafeFileHandle file, int fileInformationClass, ref FileIoPriorityHint fileInformation, int bufferSize);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIoPriorityHint
    {
        public int PriorityHint;
    }
}
