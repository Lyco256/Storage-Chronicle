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
    [Trait("Capability", "UsnQuery")]
    [Trait("Capability", "UsnRead")]
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
    public async Task ReadDirectoryChangesWReportsCreateRenameAndDelete()
    {
        var scenario = WindowsAcceptanceEnvironment.CreateScenario("rdcw");
        var marker = $"rdcw-{Guid.NewGuid():N}.entry";
        var markerPath = Path.Combine(scenario, marker);
        var renamed = marker + ".renamed";
        var renamedPath = Path.Combine(scenario, renamed);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var monitor = new WindowsDirectoryChangeMonitorFactory().Create(VolumeId.Create("windows-acceptance"), scenario, 64 * 1024);
        var found = new TaskCompletionSource<HashSet<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var consumer = Task.Run(async () =>
            {
                await foreach (var read in monitor.ReadChangesAsync(cancellation.Token))
                {
                    foreach (var notification in read.Notifications)
                    {
                        if (notification.Kind == DirectoryChangeKind.Added && notification.RelativePath.EndsWith(marker, StringComparison.OrdinalIgnoreCase)) operations.Add("create");
                        if (notification.Kind == DirectoryChangeKind.RenamedNewName && notification.RelativePath.EndsWith(renamed, StringComparison.OrdinalIgnoreCase)) operations.Add("rename");
                        if (notification.Kind == DirectoryChangeKind.Removed && notification.RelativePath.EndsWith(renamed, StringComparison.OrdinalIgnoreCase)) operations.Add("delete");
                    }

                    if (operations.Count == 3) { found.TrySetResult(new HashSet<string>(operations, StringComparer.Ordinal)); return; }

                    if (read.MonitorLost)
                    {
                        found.TrySetException(new InvalidOperationException($"ReadDirectoryChangesW reported a gap: {read.Gap?.Reason ?? "unknown"} ({read.NativeErrorCode})."));
                        return;
                    }
                }
            }, CancellationToken.None);

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
            using (File.Create(markerPath)) { }
            File.Move(markerPath, renamedPath);
            File.Delete(renamedPath);
            Assert.True(await WindowsAcceptanceEnvironment.WaitForAsync(found.Task, TimeSpan.FromSeconds(20)), "ReadDirectoryChangesW did not report create, rename, and delete before the timeout.");
            Assert.Equal(["create", "delete", "rename"], (await found.Task).OrderBy(value => value, StringComparer.Ordinal));
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
        var found = new TaskCompletionSource<SourceEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var consumer = Task.Run(async () =>
            {
                await foreach (var value in collector.CollectAsync(cancellation.Token))
                {
                    if (value.Properties.TryGetValue("path", out var path) && path.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        found.TrySetResult(value);
                        return;
                    }
                }
            }, CancellationToken.None);

            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            using (File.Create(markerPath)) { }
            Assert.True(await WindowsAcceptanceEnvironment.WaitForAsync(found.Task, TimeSpan.FromSeconds(30)), "Kernel ETW did not report the test file operation before the timeout.");
            var observed = await found.Task;
            Assert.Equal(ProcessAttributionQuality.Correlated, observed.ProcessQuality);
            Assert.NotNull(observed.ProcessInstanceId);
            Assert.True(observed.Properties.ContainsKey("process.name"), "ETW did not retain the correlated process name.");
            Assert.True(observed.Properties.ContainsKey("process.executable"), "ETW did not retain the correlated process executable.");
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
    [Trait("Capability", "SmbQuery")]
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
    [Trait("Capability", "Smb")]
    public async Task DisposableSmbShareCreateChangeAndRemoveAreObservedBySnapshots()
    {
        var scenario = WindowsAcceptanceEnvironment.CreateScenario("smb");
        var shareName = "SCAcc" + Guid.NewGuid().ToString("N");
        var before = await new NetShareSnapshotReader().ReadAsync(TestContext.Current.CancellationToken);
        try
        {
            await WindowsAcceptanceEnvironment.RunPowerShellAsync(
                "param($name, $path) New-SmbShare -Name $name -Path $path -FullAccess $env:USERNAME -ErrorAction Stop | Out-Null",
                shareName,
                scenario);
            var afterCreate = await new NetShareSnapshotReader().ReadAsync(TestContext.Current.CancellationToken);
            Assert.Contains(new ShareSnapshotDiffer().Diff(before, afterCreate), value => string.Equals(value.Share.Name, shareName, StringComparison.OrdinalIgnoreCase) && value.ChangeKind == "Created");

            await WindowsAcceptanceEnvironment.RunPowerShellAsync(
                "param($name) Set-SmbShare -Name $name -Description 'Storage Chronicle acceptance changed' -Force -ErrorAction Stop | Out-Null",
                shareName);
            var afterChange = await new NetShareSnapshotReader().ReadAsync(TestContext.Current.CancellationToken);
            Assert.Contains(new ShareSnapshotDiffer().Diff(afterCreate, afterChange), value => string.Equals(value.Share.Name, shareName, StringComparison.OrdinalIgnoreCase) && value.ChangeKind == "Changed");

            await WindowsAcceptanceEnvironment.RunPowerShellAsync(
                "param($name) Remove-SmbShare -Name $name -Force -ErrorAction Stop",
                shareName);
            var afterRemove = await new NetShareSnapshotReader().ReadAsync(TestContext.Current.CancellationToken);
            Assert.Contains(new ShareSnapshotDiffer().Diff(afterChange, afterRemove), value => string.Equals(value.Share.Name, shareName, StringComparison.OrdinalIgnoreCase) && value.ChangeKind == "Deleted");
        }
        finally
        {
            try { await WindowsAcceptanceEnvironment.RunPowerShellAsync("param($name) Remove-SmbShare -Name $name -Force -ErrorAction SilentlyContinue", shareName); } catch (InvalidOperationException) { }
            WindowsAcceptanceEnvironment.DeleteScenario(scenario);
        }
    }

    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "ServiceQuery")]
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
    [Trait("Capability", "Service")]
    public async Task DisposableAgentServiceCanBeInstalledStartedStoppedAndConfiguredForRecovery()
    {
        var executable = WindowsAcceptanceEnvironment.AgentExecutable;
        Assert.True(File.Exists(executable), $"The Agent executable does not exist: {executable}");
        var serviceName = $"SCAccAgent{Guid.NewGuid():N}"[..Math.Min(40, 11 + Guid.NewGuid().ToString("N").Length)];
        var binPath = $"\"{executable}\"";
        try
        {
            Assert.Equal(0, RunSc("create", serviceName, "binPath=", binPath, "start=", "demand").ExitCode);
            Assert.Equal(0, RunSc("failure", serviceName, "reset=", "60", "actions=", "restart/5000/restart/15000/" ).ExitCode);
            Assert.Equal(0, RunSc("start", serviceName).ExitCode);
            await WaitForServiceStateAsync(serviceName, "RUNNING");
            var failure = RunSc("qfailure", serviceName);
            Assert.Equal(0, failure.ExitCode);
            Assert.Contains("FAILURE_ACTIONS", failure.Output, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, RunSc("stop", serviceName).ExitCode);
            await WaitForServiceStateAsync(serviceName, "STOPPED");
        }
        finally
        {
            _ = RunSc("stop", serviceName);
            _ = RunSc("delete", serviceName);
        }
    }

    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "BufferGap")]
    public void InitialScanBufferReportsOverflowWithoutDroppingSilently()
    {
        var buffer = new InitialScanNotificationBuffer(1);
        buffer.Begin(100);
        Assert.True(buffer.TryAdd(new DirectoryChangeNotification(101, DirectoryChangeKind.Added, "one.entry", null, DateTimeOffset.UtcNow)));
        Assert.False(buffer.TryAdd(new DirectoryChangeNotification(102, DirectoryChangeKind.Modified, "two.entry", null, DateTimeOffset.UtcNow)));
        Assert.True(buffer.IsOverflowed);
        Assert.Single(buffer.Complete());
    }

    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "VolumeGuid")]
    public async Task DriveLetterFreeAcceptanceRootResolvesToAStableVolumeGuid()
    {
        var root = WindowsAcceptanceEnvironment.RootPath;
        Assert.True(root.StartsWith("\\\\?\\Volume{", StringComparison.OrdinalIgnoreCase), $"A drive-letter-free volume GUID root is required; got {root}");
        var volumes = await new WindowsVolumeEnumerator().EnumerateAsync(TestContext.Current.CancellationToken);
        var volume = Assert.Single(volumes, value => value.MountPoints.Any(mount => IsSameOrUnder(root, mount)) || string.Equals(value.Id.Value, root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
        Assert.True(volume.Id.Value.StartsWith("\\\\?\\VOLUME{", StringComparison.OrdinalIgnoreCase), $"The volume identifier was not a GUID path: {volume.Id.Value}");
        Assert.NotEmpty(volume.MountPoints);
    }

    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "HotAttachDetach")]
    public async Task DisposableVhdxCanBeDetachedAndReattached()
    {
        var imagePath = WindowsAcceptanceEnvironment.VhdxPath;
        Assert.True(File.Exists(imagePath), $"The disposable VHDX does not exist: {imagePath}");
        var result = await WindowsAcceptanceEnvironment.RunPowerShellAsync(
            "param($path) $image = Get-DiskImage -ImagePath $path -ErrorAction Stop; if (-not $image.Attached) { throw 'The disposable VHDX was not attached before the hot attach check.' }; Dismount-DiskImage -ImagePath $path -ErrorAction Stop; Mount-DiskImage -ImagePath $path -ErrorAction Stop; $image = Get-DiskImage -ImagePath $path -ErrorAction Stop; if (-not $image.Attached) { throw 'The disposable VHDX was not attached after the hot attach check.' }; [Console]::WriteLine($image.Attached)",
            imagePath);
        Assert.Equal("True", result, ignoreCase: true);
    }

    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "SessionAgent")]
    public async Task RealSessionAgentSendsOneClipboardCandidateThroughTheLiveAgentPipe()
    {
        Assert.True(Environment.UserInteractive, "An interactive user session is required for Session Agent acceptance.");
        Assert.True(Process.GetCurrentProcess().SessionId > 0, "Session Agent acceptance cannot run in session 0.");
        var executable = WindowsAcceptanceEnvironment.SessionAgentExecutable;
        Assert.True(File.Exists(executable), $"The Session Agent executable does not exist: {executable}");
        var scenario = WindowsAcceptanceEnvironment.CreateScenario("session-agent");
        var clipboardPath = Path.Combine(scenario, "clipboard-candidate.entry");
        using (File.Create(clipboardPath)) { }
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("--pipe-name");
        process.StartInfo.ArgumentList.Add(WindowsAcceptanceEnvironment.AgentPipeName);
        process.StartInfo.ArgumentList.Add("--once");
        try
        {
            Assert.True(process.Start(), "The Session Agent process could not be started.");
            await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await WindowsAcceptanceEnvironment.RunPowerShellAsync("param($path) Set-Clipboard -Path $path -ErrorAction Stop", clipboardPath);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            try { await WindowsAcceptanceEnvironment.RunPowerShellAsync("Set-Clipboard -Value '' -ErrorAction SilentlyContinue"); } catch (InvalidOperationException) { }
            WindowsAcceptanceEnvironment.DeleteScenario(scenario);
        }
    }

    [Fact]
    [Trait("Category", "WindowsPrivileged")]
    [Trait("Capability", "Session")]
    [Trait("Capability", "Clipboard")]
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

    private static async Task WaitForServiceStateAsync(string serviceName, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (!timeout.IsCancellationRequested)
        {
            var query = RunSc("query", serviceName);
            if (query.Output.Contains(expected, StringComparison.OrdinalIgnoreCase)) return;
            await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
        }

        throw new TimeoutException($"The disposable service did not reach state {expected}.");
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
