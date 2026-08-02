using System.Runtime.InteropServices;

namespace StorageChronicle.Platform.Windows.Session;

/// <summary>Reads CF_HDROP and Preferred DropEffect without reading file contents.</summary>
public sealed class WindowsClipboardReader : IClipboardReader
{
    private const uint ErrorAccessDenied = 5;
    private const uint CfHdrop = 15;

    /// <inheritdoc />
    public ValueTask<ClipboardReadResult> TryReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows())
        {
            return ValueTask.FromResult(new ClipboardReadResult(ClipboardReadStatus.Unsupported, null));
        }

        if (!OpenClipboard(IntPtr.Zero))
        {
            var error = checked((uint)Marshal.GetLastWin32Error());
            return ValueTask.FromResult(new ClipboardReadResult(error == ErrorAccessDenied ? ClipboardReadStatus.Locked : ClipboardReadStatus.Unsupported, null));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsClipboardFormatAvailable(CfHdrop))
            {
                return ValueTask.FromResult(new ClipboardReadResult(ClipboardReadStatus.Empty, null));
            }

            var handle = GetClipboardData(CfHdrop);
            if (handle == IntPtr.Zero)
            {
                return ValueTask.FromResult(new ClipboardReadResult(ClipboardReadStatus.Empty, null));
            }

            var pathCount = DragQueryFile(handle, 0xFFFFFFFF, null, 0);
            var paths = new List<string>(checked((int)pathCount));
            for (uint index = 0; index < pathCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = DragQueryFile(handle, index, null, 0);
                var buffer = new char[checked((int)length + 1)];
                _ = DragQueryFile(handle, index, buffer, checked((uint)buffer.Length));
                paths.Add(new string(buffer, 0, checked((int)length)));
            }

            var generation = checked((long)GetClipboardSequenceNumber());
            var isCut = ReadPreferredDropEffect();
            return ValueTask.FromResult(new ClipboardReadResult(ClipboardReadStatus.Read, new ClipboardSnapshot(generation, paths.ToArray(), isCut)));
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    private static bool ReadPreferredDropEffect()
    {
        var preferredDropEffect = GetPreferredDropEffectFormat();
        if (preferredDropEffect == 0 || !IsClipboardFormatAvailable(preferredDropEffect))
        {
            return false;
        }

        var handle = GetClipboardData(preferredDropEffect);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var locked = GlobalLock(handle);
        if (locked == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return (unchecked((uint)Marshal.ReadInt32(locked)) & 0x2U) != 0;
        }
        finally
        {
            _ = GlobalUnlock(handle);
        }
    }

    private static uint GetPreferredDropEffectFormat() => OperatingSystem.IsWindows() ? RegisterClipboardFormat("Preferred DropEffect") : 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint DragQueryFile(IntPtr drop, uint file, [Out] char[]? fileName, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);
}
