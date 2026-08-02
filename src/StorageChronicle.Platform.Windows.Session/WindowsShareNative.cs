using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using StorageChronicle.Contracts;
using StorageChronicle.Platform.Abstractions;

namespace StorageChronicle.Platform.Windows.Session;

/// <summary>Reads all local SMB share rows through NetShareEnum level 2.</summary>
public sealed class NetShareSnapshotReader : IShareSnapshotReader
{
    private const int ErrorSuccess = 0;
    private const int ErrorMoreData = 234;
    private const int ErrorAccessDenied = 5;
    private const int ShareInfoLevel = 2;
    private const int MaxPreferredLength = -1;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ShareDescriptor>> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult<IReadOnlyList<ShareDescriptor>>(Array.Empty<ShareDescriptor>());
        }

        var shares = new List<ShareDescriptor>();
        var resume = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = NetShareEnum(null, ShareInfoLevel, out var buffer, MaxPreferredLength, out var entriesRead, out _, ref resume);
            try
            {
                if (result != ErrorSuccess && result != ErrorMoreData)
                {
                    if (result == ErrorAccessDenied) throw new UnauthorizedAccessException("NetShareEnum access was denied.");
                    throw new Win32Exception(result, "NetShareEnum failed.");
                }

                var size = Marshal.SizeOf<ShareInfo2>();
                for (var index = 0; index < entriesRead; index++)
                {
                    var row = Marshal.PtrToStructure<ShareInfo2>(IntPtr.Add(buffer, index * size));
                    var name = Marshal.PtrToStringUni(row.Name) ?? string.Empty;
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    shares.Add(new ShareDescriptor(
                        name,
                        Marshal.PtrToStringUni(row.Path) ?? string.Empty,
                        MapType(row.Type),
                        Marshal.PtrToStringUni(row.Remark),
                        [ $"AccessMask:0x{row.Permissions:X8}" ]));
                }
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                {
                    _ = NetApiBufferFree(buffer);
                }
            }

            if (result != ErrorMoreData)
            {
                break;
            }
        }

        return ValueTask.FromResult<IReadOnlyList<ShareDescriptor>>(shares.ToArray());
    }

    private static string MapType(uint type) => type switch
    {
        0 => "Disk",
        1 => "Print",
        2 => "Device",
        3 => "IPC",
        _ => $"Unknown:{type}"
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct ShareInfo2
    {
        public IntPtr Name;
        public uint Type;
        public IntPtr Remark;
        public uint Permissions;
        public uint MaximumUses;
        public uint CurrentUses;
        public IntPtr Path;
        public IntPtr Password;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int NetShareEnum(string? serverName, int level, out IntPtr buffer, int preferredMaximumLength, out int entriesRead, out int totalEntries, ref int resumeHandle);

    [DllImport("Netapi32.dll", SetLastError = true)]
    private static extern int NetApiBufferFree(IntPtr buffer);
}

/// <summary>Waits for LanmanServer share registry changes using RegNotifyChangeKeyValue.</summary>
public sealed class RegistryShareChangeNotifier : IShareChangeNotifier
{
    private const int ErrorSuccess = 0;
    private const int RegNotifyChangeName = 1;
    private const int RegNotifyChangeLastSet = 4;
    private const int RegNotifyChangeSecurity = 8;
    private static readonly IntPtr HkeyLocalMachine = new(unchecked((int)0x80000002));
    private readonly IntPtr key;
    private readonly EventWaitHandle signal;
    private bool disposed;

    /// <summary>Opens the LanmanServer share registry key when notification is available.</summary>
    public RegistryShareChangeNotifier()
    {
        if (!OperatingSystem.IsWindows())
        {
            signal = new EventWaitHandle(false, EventResetMode.AutoReset);
            return;
        }

        var result = RegOpenKeyEx(HkeyLocalMachine, "SYSTEM\\CurrentControlSet\\Services\\LanmanServer\\Shares", 0, 0x20019, out key);
        if (result != ErrorSuccess)
        {
            key = IntPtr.Zero;
            signal = new EventWaitHandle(false, EventResetMode.AutoReset);
            return;
        }

        signal = new EventWaitHandle(false, EventResetMode.AutoReset);
    }

    /// <inheritdoc />
    public bool IsAvailable => key != IntPtr.Zero && !disposed;

    /// <inheritdoc />
    public async ValueTask<bool> WaitForChangeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsAvailable)
        {
            return false;
        }

        signal.Reset();
        var result = RegNotifyChangeKeyValue(key, true, RegNotifyChangeName | RegNotifyChangeLastSet | RegNotifyChangeSecurity, signal.SafeWaitHandle, true);
        if (result != ErrorSuccess)
        {
            return false;
        }

        using var registration = cancellationToken.Register(static state => ((EventWaitHandle)state!).Set(), signal);
        await Task.Run(signal.WaitOne, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return true;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return ValueTask.CompletedTask;
        }

        disposed = true;
        signal.Set();
        signal.Dispose();
        if (key != IntPtr.Zero)
        {
            _ = RegCloseKey(key);
        }

        return ValueTask.CompletedTask;
    }

    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegOpenKeyEx(IntPtr key, string subKey, uint options, int desiredAccess, out IntPtr result);

    [DllImport("Advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(IntPtr key, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree, uint notifyFilter, SafeWaitHandle eventHandle, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    [DllImport("Advapi32.dll")]
    private static extern int RegCloseKey(IntPtr key);
}
