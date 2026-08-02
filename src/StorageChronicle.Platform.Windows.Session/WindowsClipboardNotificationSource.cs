using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace StorageChronicle.Platform.Windows.Session;

/// <summary>Receives WM_CLIPBOARDUPDATE on a hidden message-only window; it never polls.</summary>
public sealed class WindowsClipboardNotificationSource : IClipboardNotificationSource
{
    private readonly ClipboardMessageWindow window;
    private bool disposed;

    /// <summary>Creates the per-user-session hidden clipboard listener.</summary>
    public WindowsClipboardNotificationSource(ISessionClock? clock = null) => window = new ClipboardMessageWindow(clock ?? new SystemSessionClock());

    /// <inheritdoc />
    public IAsyncEnumerable<ClipboardNotification> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return window.ReadAsync(cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (!disposed)
        {
            disposed = true;
            window.Dispose();
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class ClipboardMessageWindow : IDisposable
{
    private const uint WmClipboardUpdate = 0x031D;
    private const uint WmClose = 0x0010;
    private const uint WmDestroy = 0x0002;
    private const int HwndMessage = -3;
    private const int ErrorClassAlreadyExists = 1410;
    private static readonly ConcurrentDictionary<IntPtr, ClipboardMessageWindow> Windows = new();
    private static readonly NativeWindowProc WindowProcDelegate = WindowProc;
    private static readonly string WindowClassName = $"StorageChronicleClipboardListener-{Environment.ProcessId}";

    private readonly Channel<ClipboardNotification> notifications = Channel.CreateUnbounded<ClipboardNotification>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
    private readonly ISessionClock clock;
    private readonly Thread thread;
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr handle;
    private bool disposed;

    public ClipboardMessageWindow(ISessionClock clock)
    {
        this.clock = clock;
        if (!OperatingSystem.IsWindows())
        {
            notifications.Writer.TryComplete();
            ready.TrySetResult();
            thread = new Thread(static () => { }) { IsBackground = true };
            return;
        }

        thread = new Thread(Pump) { IsBackground = true, Name = "StorageChronicle Clipboard Listener" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public async IAsyncEnumerable<ClipboardNotification> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await foreach (var notification in notifications.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return notification;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        stop.Cancel();
        if (handle != IntPtr.Zero)
        {
            _ = PostMessage(handle, WmClose, IntPtr.Zero, IntPtr.Zero);
        }

        if (OperatingSystem.IsWindows() && !ReferenceEquals(Thread.CurrentThread, thread))
        {
            thread.Join(TimeSpan.FromSeconds(2));
        }

        notifications.Writer.TryComplete();
        stop.Dispose();
    }

    private void Pump()
    {
        try
        {
            RegisterWindowClass();
            handle = CreateWindowEx(0, WindowClassName, WindowClassName, 0, 0, 0, 0, 0, new IntPtr(HwndMessage), IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW for clipboard listener failed.");
            }

            Windows[handle] = this;
            if (!AddClipboardFormatListener(handle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AddClipboardFormatListener failed.");
            }

            ready.TrySetResult();
            while (!stop.IsCancellationRequested)
            {
                var result = GetMessage(out var message, IntPtr.Zero, 0, 0);
                if (result <= 0)
                {
                    break;
                }

                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            ready.TrySetException(exception);
            notifications.Writer.TryComplete(exception);
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                _ = RemoveClipboardFormatListener(handle);
                Windows.TryRemove(handle, out _);
                _ = DestroyWindow(handle);
                handle = IntPtr.Zero;
            }

            notifications.Writer.TryComplete();
        }
    }

    private static void RegisterWindowClass()
    {
        var windowClass = new WindowClassEx
        {
            Size = (uint)Marshal.SizeOf<WindowClassEx>(),
            Procedure = Marshal.GetFunctionPointerForDelegate(WindowProcDelegate),
            Instance = GetModuleHandle(null),
            ClassName = WindowClassName
        };
        if (RegisterClassEx(ref windowClass) == 0)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorClassAlreadyExists)
            {
                throw new Win32Exception(error, "RegisterClassExW for clipboard listener failed.");
            }
        }
    }

    private static IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (Windows.TryGetValue(window, out var owner))
        {
            if (message == WmClipboardUpdate)
            {
                owner.notifications.Writer.TryWrite(new ClipboardNotification(owner.clock.UtcNow));
                return IntPtr.Zero;
            }

            if (message == WmClose)
            {
                _ = DestroyWindow(window);
                return IntPtr.Zero;
            }

            if (message == WmDestroy)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassEx
    {
        public uint Size;
        public uint Style;
        public IntPtr Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
        public IntPtr SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr Window;
        public uint MessageId;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    private delegate IntPtr NativeWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint extendedStyle, string className, string windowName, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out Message message, IntPtr window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
