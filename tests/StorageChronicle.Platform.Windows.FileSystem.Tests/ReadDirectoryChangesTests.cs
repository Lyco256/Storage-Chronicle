using System.Buffers.Binary;
using System.Text;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.FileSystem;
using StorageChronicle.Platform.Windows.FileSystem.Monitoring;
using StorageChronicle.Platform.Windows.FileSystem.Interop;
using Xunit;

namespace StorageChronicle.Platform.Windows.FileSystem.Tests;

public sealed class ReadDirectoryChangesTests
{
    [Fact]
    [Trait("Category", "WindowsApi")]
    public async Task MonitorReportsGapWhenFinalRootComponentIsReparsePoint()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("This test requires Windows directory-handle semantics.");
        var root = Directory.CreateTempSubdirectory("storage-chronicle-reparse-root");
        var target = Path.Combine(root.FullName, "target");
        var link = Path.Combine(root.FullName, "link");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.Combine(target, "must-not-be-watched"));
        try
        {
            try
            {
                Directory.CreateSymbolicLink(link, target);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Assert.Skip($"The host cannot create an unprivileged directory symbolic link: {exception.GetType().Name}.");
            }

            var monitor = new WindowsDirectoryChangeMonitorFactory().Create(VolumeId.Create("test-volume"), link, 4096);
            var reads = await ReadAllAsync(monitor.ReadChangesAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

            Assert.Contains(reads, read => read.Gap is not null && read.MonitorLost);
            Assert.DoesNotContain(reads.SelectMany(read => read.Notifications), notification => notification.RelativePath.Contains("must-not-be-watched", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            root.Delete(true);
        }
    }

    [Fact]
    public async Task MonitorRejectsConfiguredRootReparseComponentBeforeStartingNativeReads()
    {
        var fileNative = new FakeFileNative(path =>
        {
            var isReparseRoot = path.EndsWith("\\junction", StringComparison.OrdinalIgnoreCase);
            return new NativeFileMetadataRecord(FileId.Create(path), null, Path.GetFileName(path),
                isReparseRoot ? FileKind.Junction : FileKind.Directory, null, null, null, null, null, null,
                isReparseRoot ? FileAttributes.Directory | FileAttributes.ReparsePoint : FileAttributes.Directory,
                isReparseRoot ? "Junction" : null, true, false);
        });
        var monitor = new WindowsDirectoryChangeMonitor(VolumeId.Create("V"), "C:\\volume\\junction\\nested", fileNative,
            new FakeChangeNative(new NativeDirectoryChangeReadResult(Array.Empty<byte>(), 0, 1022)),
            volumeRootPath: "C:\\volume", rootComponents: ["junction", "nested"]);

        var reads = await ReadAllAsync(monitor.ReadChangesAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        Assert.Contains(reads, read => read.MonitorLost && read.Gap is not null);
        Assert.Collection(fileNative.OpenedChildren, child => Assert.Equal("junction", child.Name));
    }

    [Fact]
    [Trait("Category", "WindowsApi")]
    public async Task NativeDirectoryReadCancelsPromptlyOnTemporaryDirectory()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("This test requires Windows overlapped directory notifications.");
        var directory = Directory.CreateTempSubdirectory("storage-chronicle-cancel-read");
        try
        {
            var monitor = new WindowsDirectoryChangeMonitorFactory().Create(VolumeId.Create("temporary-test-volume"), directory.FullName, 4096);
            using var cancellation = new CancellationTokenSource();
            var read = ReadAllAsync(monitor.ReadChangesAsync(cancellation.Token), cancellation.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public void ParserPairsRenameAndPreservesSequence()
    {
        var buffer = BuildBuffer((4, "old.txt"), (5, "new.txt"), (1, "created.txt"));
        var result = ReadDirectoryChangesBufferParser.Parse(buffer, buffer.Length, 10, DateTimeOffset.UtcNow);

        Assert.False(result.IsMalformed);
        Assert.Equal(2, result.Notifications.Count);
        Assert.Equal(10, result.Notifications[0].Sequence);
        Assert.Equal("old.txt", result.Notifications[0].OldRelativePath);
        Assert.Equal(DirectoryChangeKind.RenamedNewName, result.Notifications[0].Kind);
        Assert.Equal(11, result.Notifications[1].Sequence);
    }

    [Fact]
    public void ParserRejectsMalformedBufferInsteadOfReturningContinuousEvents()
    {
        var buffer = BuildBuffer((1, "x"));
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), buffer.Length);

        var result = ReadDirectoryChangesBufferParser.Parse(buffer, buffer.Length, 0, DateTimeOffset.UtcNow);

        Assert.True(result.IsMalformed);
        Assert.Empty(result.Notifications);
    }

    [Fact]
    public async Task MonitorReportsBufferOverflowAsContinuityGap()
    {
        var fileNative = new FakeFileNative();
        var monitor = new WindowsDirectoryChangeMonitor(VolumeId.Create("\\\\?\\Volume{A}"), "C:\\", fileNative, new FakeChangeNative(new NativeDirectoryChangeReadResult(Array.Empty<byte>(), 0, 1022)));

        var reads = await ReadAllAsync(monitor.ReadChangesAsync(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        var read = Assert.Single(reads);
        Assert.True(read.MonitorLost);
        Assert.Equal(EventQuality.UnverifiedGap, read.Gap is null ? EventQuality.Exact : EventQuality.UnverifiedGap);
        Assert.Contains("buffer", read.Gap!.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InitialScanBufferMarksOverflowAndKeepsEarlierSequenceOrder()
    {
        var buffer = new InitialScanNotificationBuffer(1);
        buffer.Begin(42);
        var first = new DirectoryChangeNotification(42, DirectoryChangeKind.Added, "first.txt", null, DateTimeOffset.UtcNow);
        var second = new DirectoryChangeNotification(43, DirectoryChangeKind.Added, "second.txt", null, DateTimeOffset.UtcNow);

        Assert.True(buffer.TryAdd(first));
        Assert.False(buffer.TryAdd(second));
        var completed = buffer.Complete();

        Assert.True(buffer.IsOverflowed);
        Assert.Equal(42, buffer.BoundarySequence);
        Assert.Equal("first.txt", Assert.Single(completed).RelativePath);
    }

    private static async Task<List<DirectoryChangeRead>> ReadAllAsync(IAsyncEnumerable<DirectoryChangeRead> source, CancellationToken cancellationToken)
    {
        var result = new List<DirectoryChangeRead>();
        await foreach (var item in source.WithCancellation(cancellationToken)) result.Add(item);
        return result;
    }

    private static byte[] BuildBuffer(params (int Action, string Name)[] records)
    {
        var bytes = new List<byte>();
        for (var index = 0; index < records.Length; index++)
        {
            var nameBytes = Encoding.Unicode.GetBytes(records[index].Name);
            var recordLength = index == records.Length - 1 ? 12 + nameBytes.Length : ((12 + nameBytes.Length + 3) / 4) * 4;
            var record = new byte[recordLength];
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(0, 4), index == records.Length - 1 ? 0 : recordLength);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4, 4), records[index].Action);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8, 4), nameBytes.Length);
            nameBytes.CopyTo(record, 12);
            bytes.AddRange(record);
        }

        return bytes.ToArray();
    }
}
