using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.FileSystem;
using StorageChronicle.Platform.Windows.FileSystem.Monitoring;
using StorageChronicle.Platform.Windows.FileSystem.Volumes;
using StorageChronicle.Platform.Windows.Ntfs;
using StorageChronicle.Platform.Windows.Session;
using Xunit;

namespace StorageChronicle.Platform.Windows.Integration.Tests;

public sealed class WindowsPrivilegedAcceptanceTests
{
    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "Vhdx")]
    public async Task VhdxIsAttachedAndExposesAnNtfsVolume()
    {
        Assert.True(OperatingSystem.IsWindows(), "The Windows acceptance project must run on Windows.");
        var imagePath = WindowsAcceptanceEnvironment.VhdxPath;
        var root = WindowsAcceptanceEnvironment.RootPath;
        Assert.True(File.Exists(imagePath), $"The configured VHDX does not exist: {imagePath}");
        Assert.True(Directory.Exists(root), $"The configured VHDX mount root does not exist: {root}");

        var attached = await WindowsAcceptanceEnvironment.RunPowerShellAsync(
            "param($path) $image = Get-DiskImage -ImagePath $path -ErrorAction Stop; if (-not $image.Attached) { exit 2 }; [Console]::WriteLine($image.Attached)",
            imagePath);
        Assert.Equal("True", attached, ignoreCase: true);
        Assert.Equal("NTFS", GetVolumeFileSystem(root), ignoreCase: true);
    }

    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "Usn")]
    public async Task ExistingUsnJournalCanBeReadWithoutCreatingOrResizingIt()
    {
        var root = WindowsAcceptanceEnvironment.RootPath;
        var device = WindowsAcceptanceEnvironment.DevicePath;
        var api = new WindowsNtfsApi();
        using var handle = api.OpenVolume(device);
        var beforeResult = api.QueryUsnJournal(handle, out var before);
        Assert.True(beforeResult.Succeeded, $"FSCTL_QUERY_USN_JOURNAL failed: {beforeResult.Status} ({beforeResult.Win32Error}).");
        Assert.NotNull(before);

        var scenario = WindowsAcceptanceEnvironment.CreateScenario("usn");
        var marker = Path.Combine(scenario, $"created-{Guid.NewGuid():N}.entry");
        var renamed = marker + ".renamed";
        try
        {
            Directory.CreateDirectory(scenario);
            using (File.Create(marker)) { }
            File.Move(marker, renamed);
            Directory.Delete(scenario, recursive: true);

            var reader = new UsnJournalReader(
                api,
                device,
                new UsnReadOptions { WaitTimeout = TimeSpan.FromSeconds(2) },
                new UsnJournalState(before!.JournalId, before.NextUsn));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var values = await WindowsAcceptanceEnvironment.CollectAsync(reader.ReadAsync(cancellation.Token), cancellation.Token);

            Assert.Contains(values, value => value.Record is not null &&
                (string.Equals(value.Record.Name, Path.GetFileName(marker), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(value.Record.Name, Path.GetFileName(renamed), StringComparison.OrdinalIgnoreCase)));
            Assert.NotNull(reader.LastObservedState);

            using var afterHandle = api.OpenVolume(device);
            var afterResult = api.QueryUsnJournal(afterHandle, out var after);
            Assert.True(afterResult.Succeeded, $"The journal could not be queried after the test: {afterResult.Status} ({afterResult.Win32Error}).");
            Assert.NotNull(after);
            Assert.Equal(before.JournalId, after!.JournalId);
            Assert.Equal(before.MaximumSize, after.MaximumSize);
            Assert.Equal(before.AllocationDelta, after.AllocationDelta);
        }
        finally
        {
            WindowsAcceptanceEnvironment.DeleteScenario(scenario);
        }
    }

    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "Mft")]
    public async Task PublicMftEnumerationFindsARealEntryWithoutReadingContents()
    {
        var scenario = WindowsAcceptanceEnvironment.CreateScenario("mft");
        var marker = Path.Combine(scenario, $"mft-{Guid.NewGuid():N}.entry");
        try
        {
            using (File.Create(marker)) { }
            // Keep the marker open exclusively for write while the MFT is
            // enumerated. The acceptance path must use volume metadata only;
            // a normal content reader cannot open this handle.
            using var contentDenied = new FileStream(marker, FileMode.Open, FileAccess.Write, FileShare.None);
            var enumerator = new WindowsMftEnumerator(new WindowsNtfsApi(), WindowsAcceptanceEnvironment.DevicePath);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            MftEntry? found = null;
            await foreach (var entry in enumerator.EnumerateAsync(cancellation.Token).WithCancellation(cancellation.Token))
            {
                if (string.Equals(entry.Name, Path.GetFileName(marker), StringComparison.OrdinalIgnoreCase))
                {
                    found = entry;
                    break;
                }
            }

            Assert.NotNull(found);
            Assert.False(string.IsNullOrWhiteSpace(found!.Name));
        }
        finally
        {
            WindowsAcceptanceEnvironment.DeleteScenario(scenario);
        }
    }

    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "ReadDirectoryChangesW")]
    public async Task ReadDirectoryChangesWReportsARealFileNameChange()
    {
        var scenario = WindowsAcceptanceEnvironment.CreateScenario("rdcw");
        var marker = $"rdcw-{Guid.NewGuid():N}.entry";
        var markerPath = Path.Combine(scenario, marker);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var monitor = new WindowsDirectoryChangeMonitorFactory().Create(VolumeId.Create("windows-acceptance"), scenario, 64 * 1024);
        var found = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var consumer = Task.Run(async () =>
            {
                await foreach (var read in monitor.ReadChangesAsync(cancellation.Token))
                {
                    if (read.Notifications.Any(value => value.RelativePath.EndsWith(marker, StringComparison.OrdinalIgnoreCase)))
                    {
                        found.TrySetResult(true);
                        return;
                    }

                    if (read.MonitorLost)
                    {
                        found.TrySetException(new InvalidOperationException($"ReadDirectoryChangesW reported a gap: {read.Gap?.Reason ?? "unknown"} ({read.NativeErrorCode})."));
                        return;
                    }
                }
            }, CancellationToken.None);

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
            using (File.Create(markerPath)) { }
            Assert.True(await WindowsAcceptanceEnvironment.WaitForAsync(found.Task, TimeSpan.FromSeconds(20)), "ReadDirectoryChangesW did not report the test file before the timeout.");
            await found.Task;
            cancellation.Cancel();
            await IgnoreCancellationAsync(consumer);
        }
        finally
        {
            cancellation.Cancel();
            WindowsAcceptanceEnvironment.DeleteScenario(scenario);
        }
    }

    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "Etw")]
    public async Task KernelEtwReportsFileIoForARealFileOperation()
    {
        var scenario = WindowsAcceptanceEnvironment.CreateScenario("etw");
        var marker = $"etw-{Guid.NewGuid():N}.entry";
        var markerPath = Path.Combine(scenario, marker);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        await using var collector = new WindowsEtwFileIoCollector($"StorageChronicle.Acceptance.{Environment.ProcessId}.{Guid.NewGuid():N}");
        var found = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var consumer = Task.Run(async () =>
            {
                await foreach (var value in collector.CollectAsync(cancellation.Token))
                {
                    if (value.Properties.TryGetValue("path", out var path) && path.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        found.TrySetResult(true);
                        return;
                    }
                }
            }, CancellationToken.None);

            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            using (File.Create(markerPath)) { }
            Assert.True(await WindowsAcceptanceEnvironment.WaitForAsync(found.Task, TimeSpan.FromSeconds(30)), "Kernel ETW did not report the test file operation before the timeout.");
            await found.Task;
            cancellation.Cancel();
            await IgnoreCancellationAsync(consumer);
        }
        finally
        {
            cancellation.Cancel();
            WindowsAcceptanceEnvironment.DeleteScenario(scenario);
        }
    }

    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "Smb")]
    public async Task LocalSmbSnapshotContainsTheConfiguredShare()
    {
        var reader = new NetShareSnapshotReader();
        var shares = await reader.ReadAsync(TestContext.Current.CancellationToken);
        var share = Assert.Single(shares, value => string.Equals(value.Name, WindowsAcceptanceEnvironment.ShareName, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Disk", share.Type, ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(share.LocalPath));
    }

    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "Service")]
    public void ConfiguredWindowsServiceIsVisibleThroughTheServiceControlManager()
    {
        var serviceName = WindowsAcceptanceEnvironment.ServiceName;
        var query = RunSc("query", serviceName);
        Assert.Equal(0, query.ExitCode);
        Assert.Contains(serviceName, query.Output, StringComparison.OrdinalIgnoreCase);

        var configuration = RunSc("qc", serviceName);
        Assert.Equal(0, configuration.ExitCode);
        Assert.Contains("SERVICE_NAME", configuration.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("BINARY_PATH_NAME", configuration.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "Session")]
    public async Task InteractiveSessionCanReadClipboardMetadataAndCreateItsNotificationWindow()
    {
        Assert.True(Environment.UserInteractive, "An interactive user session is required for the clipboard acceptance check.");
        Assert.True(Process.GetCurrentProcess().SessionId > 0, "Session 0 is not a valid target for the user-session acceptance check.");

        var result = await new WindowsClipboardReader().TryReadAsync(TestContext.Current.CancellationToken);
        Assert.True(result.Status is ClipboardReadStatus.Empty or ClipboardReadStatus.Read, $"Clipboard access was not available: {result.Status}.");
        if (result.Snapshot is not null)
        {
            Assert.True(result.Snapshot.Generation >= 0);
            Assert.All(result.Snapshot.Paths, path => Assert.False(string.IsNullOrWhiteSpace(path)));
        }

        await using var source = new WindowsClipboardNotificationSource();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = source.ReadAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var pending = enumerator.MoveNextAsync().AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        Assert.False(pending.IsFaulted, pending.Exception?.GetBaseException().Message ?? "The clipboard notification window failed to initialize.");
        cancellation.Cancel();
        await IgnoreCancellationAsync(pending);
    }

    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "RemovableMedia")]
    public async Task ConfiguredRemovableVolumeIsEnumeratedAndCanBeUsedForANameOnlyMarker()
    {
        var root = WindowsAcceptanceEnvironment.RemovableRoot;
        var volumes = await new WindowsVolumeEnumerator().EnumerateAsync(TestContext.Current.CancellationToken);
        var volume = Assert.Single(volumes, value => value.IsExternal && value.MountPoints.Any(mount => IsSameOrUnder(root, mount)));
        Assert.True(volume.IsDirectoryReadable, $"The removable volume is not readable: {volume.Id.Value}");

        var marker = Path.Combine(root, $"StorageChronicle.Acceptance.{Guid.NewGuid():N}.entry");
        try
        {
            using (File.Create(marker)) { }
            Assert.True(File.Exists(marker));
        }
        finally
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
    }

    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "RemovableMediaEvent")]
    public async Task DeviceNotificationReportsAnActualMediaChange()
    {
        Assert.True(WindowsAcceptanceEnvironment.WaitForMediaChange, "The media event acceptance test must be explicitly armed by the runner.");
        await using var monitor = new WindowsExternalMediaMonitor();
        Assert.True(string.IsNullOrWhiteSpace(monitor.RegistrationFailure), monitor.RegistrationFailure ?? "Device notification registration failed.");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        await using var enumerator = monitor.ReadChangesAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(cancellation.Token), "The media notification stream ended before a real device change was observed.");
        Assert.True(enumerator.Current.Kind is ExternalMediaChangeKind.Connected or ExternalMediaChangeKind.Disconnected);
    }

    private static string GetVolumeFileSystem(string path)
    {
        var fileSystem = new string('\0', 256);
        var volumeName = new string('\0', 256);
        var serial = 0u;
        var maximumComponentLength = 0u;
        var flags = 0u;
        Assert.True(GetVolumeInformation(path, volumeName, (uint)volumeName.Length, ref serial, ref maximumComponentLength, ref flags, fileSystem, (uint)fileSystem.Length), $"GetVolumeInformation failed for {path}: {Marshal.GetLastWin32Error()}");
        return fileSystem.TrimEnd('\0');
    }

    private static (int ExitCode, string Output) RunSc(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("sc.exe")
        {
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("sc.exe could not be started.");
        var output = new StringBuilder();
        output.Append(process.StandardOutput.ReadToEnd());
        output.Append(process.StandardError.ReadToEnd());
        process.WaitForExit();
        return (process.ExitCode, output.ToString());
    }

    private static bool IsSameOrUnder(string candidate, string root)
    {
        var candidatePath = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(candidatePath, rootPath, StringComparison.OrdinalIgnoreCase) || candidatePath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string rootPathName, string volumeNameBuffer, uint volumeNameSize, ref uint volumeSerialNumber, ref uint maximumComponentLength, ref uint fileSystemFlags, string fileSystemNameBuffer, uint fileSystemNameSize);
}
