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
