using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Abstractions;
using StorageChronicle.Platform.Windows.Session;
using StorageChronicle.SessionAgent;
using Xunit;

namespace StorageChronicle.Platform.Windows.Session.Tests;

public sealed class SessionSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 2, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ClipboardGenerationPreservesCopyCutAndSupportsRepeatedPaste()
    {
        var notifications = new FakeNotifications([new ClipboardNotification(Now)]);
        var reader = new FakeClipboardReader([new ClipboardReadResult(ClipboardReadStatus.Read, new ClipboardSnapshot(4, ["C:\\source.txt"], true))]);
        var tracker = new ClipboardIntentTracker(new FixedClock(Now));
        await using var source = new ClipboardEventSource(notifications, reader, tracker, new ClipboardLockRetryOptions(1, TimeSpan.Zero), new FixedClock(Now));

        var value = await FirstAsync(source.ReadAsync(TestContext.Current.CancellationToken));
        var candidate = tracker.ConfirmPaste(4);

        Assert.Equal(EventOrigin.Clipboard, value.Origin);
        Assert.Equal("ConfirmedIntent", value.Properties["clipboardQuality"]);
        Assert.Equal("True", value.Properties["clipboardIsCut"]);
        Assert.Equal("C:\\source.txt", value.Properties["clipboardPath.0"]);
        Assert.NotNull(candidate);
        Assert.True(candidate!.IsCut);
        Assert.NotNull(tracker.ConfirmPaste(4));
    }

    [Fact]
    public async Task SameGenerationCanBeObservedForMultiplePastesUntilCleared()
    {
        var notifications = new FakeNotifications([new ClipboardNotification(Now), new ClipboardNotification(Now.AddSeconds(1))]);
        var reader = new FakeClipboardReader([
            new ClipboardReadResult(ClipboardReadStatus.Read, new ClipboardSnapshot(8, ["C:\\source.txt"], false)),
            new ClipboardReadResult(ClipboardReadStatus.Read, new ClipboardSnapshot(8, ["C:\\source.txt"], false))]);
        var tracker = new ClipboardIntentTracker(new FixedClock(Now));
        await using var source = new ClipboardEventSource(notifications, reader, tracker, new ClipboardLockRetryOptions(1, TimeSpan.Zero), new FixedClock(Now));

        var values = await CollectAsync(source.ReadAsync(TestContext.Current.CancellationToken), 2);

        Assert.Equal(2, values.Count);
        Assert.All(values, value => Assert.Equal("8", value.Properties["clipboardGeneration"]));
        Assert.NotNull(tracker.ConfirmPaste(8));
        tracker.ClearExcept(9);
        Assert.Null(tracker.ConfirmPaste(8));
    }

    [Fact]
    public async Task ClipboardLockUsesBoundedNonBlockingRetry()
    {
        var notifications = new FakeNotifications([new ClipboardNotification(Now)]);
        var reader = new FakeClipboardReader([
            new ClipboardReadResult(ClipboardReadStatus.Locked, null),
            new ClipboardReadResult(ClipboardReadStatus.Locked, null),
            new ClipboardReadResult(ClipboardReadStatus.Read, new ClipboardSnapshot(9, ["C:\\source.txt"], false))]);
        await using var source = new ClipboardEventSource(notifications, reader, retryOptions: new ClipboardLockRetryOptions(3, TimeSpan.Zero), clock: new FixedClock(Now));

        var value = await FirstAsync(source.ReadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(3, reader.Attempts);
        Assert.Equal(EventQuality.Exact, value.Quality);
        Assert.Equal("ConfirmedIntent", value.Properties["clipboardQuality"]);
    }

    [Fact]
    public async Task ExhaustedClipboardLockIsUnknownAndNeverConfirmed()
    {
        var notifications = new FakeNotifications([new ClipboardNotification(Now)]);
        var reader = new FakeClipboardReader([
            new ClipboardReadResult(ClipboardReadStatus.Locked, null),
            new ClipboardReadResult(ClipboardReadStatus.Locked, null)]);
        var tracker = new ClipboardIntentTracker(new FixedClock(Now));
        await using var source = new ClipboardEventSource(notifications, reader, tracker, new ClipboardLockRetryOptions(2, TimeSpan.Zero), new FixedClock(Now));

        var value = await FirstAsync(source.ReadAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EventQuality.Unknown, value.Quality);
        Assert.Equal("SourceUnknown", value.Properties["clipboardQuality"]);
        Assert.Null(tracker.ConfirmPaste(0));
    }

    [Fact]
    public async Task MissingCfHdropIsNotIdentifiedAndDoesNotCreateAPath()
    {
        var notifications = new FakeNotifications([new ClipboardNotification(Now)]);
        var reader = new FakeClipboardReader([new ClipboardReadResult(ClipboardReadStatus.Empty, null)]);
        await using var source = new ClipboardEventSource(notifications, reader, retryOptions: new ClipboardLockRetryOptions(1, TimeSpan.Zero), clock: new FixedClock(Now));

        var value = await FirstAsync(source.ReadAsync(TestContext.Current.CancellationToken));

        Assert.Equal("NotIdentified", value.Properties["clipboardQuality"]);
        Assert.Equal("0", value.Properties["clipboardPathCount"]);
        Assert.False(value.Properties.ContainsKey("clipboardPath.0"));
    }

    [Fact]
    public async Task ClipboardSourceHonorsCancellation()
    {
        var notifications = new BlockingNotifications();
        var reader = new FakeClipboardReader([]);
        await using var source = new ClipboardEventSource(notifications, reader);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ConsumeAsync(source.ReadAsync(cancellation.Token), cancellation.Token));
    }

    [Fact]
    public void SessionGuardRejectsImpersonationAndNonClipboardMessages()
    {
        var guard = new SessionIpcGuard();

        Assert.True(guard.IsAllowed("S-1-5-21", "S-1-5-21", 3, 3, "ClipboardCandidate"));
        Assert.False(guard.IsAllowed("S-1-5-18", "S-1-5-21", 3, 3, "ClipboardCandidate"));
        Assert.False(guard.IsAllowed("S-1-5-21", "S-1-5-21", 3, 3, "Other"));
    }

    [Fact]
    public void CloudPlaceholderTransitionsDistinguishHydrationAndMetadataOnlyChanges()
    {
        Assert.Equal(CloudPlaceholderState.Dehydrated, CloudPlaceholderStateClassifier.Classify(null, CloudPlaceholderState.Dehydrated, false));
        Assert.Equal(CloudPlaceholderState.Hydrated, CloudPlaceholderStateClassifier.Classify(CloudPlaceholderState.Dehydrated, CloudPlaceholderState.Hydrated, false));
        Assert.Equal(CloudPlaceholderState.PlaceholderMetadataChanged, CloudPlaceholderStateClassifier.Classify(CloudPlaceholderState.Hydrated, CloudPlaceholderState.Hydrated, true));
    }

    [Fact]
    public void ShareDiffDetectsPathAndPermissionChangesAsDedicatedShareChanges()
    {
        var differ = new ShareSnapshotDiffer();
        var before = new[] { new ShareDescriptor("docs", "C:\\docs", "Disk", null, ["Users:Read"]) };
        var after = new[] { new ShareDescriptor("docs", "D:\\docs", "Disk", null, ["Users:Write"]) };

        var change = Assert.Single(differ.Diff(before, after));

        Assert.Equal("Changed", change.ChangeKind);
        Assert.Equal("docs", change.Share.Name);
    }

    [Fact]
    public void ShareDiffDoesNotReportEquivalentPermissionListsAsChanges()
    {
        var differ = new ShareSnapshotDiffer();
        var before = new[] { new ShareDescriptor("docs", "C:\\docs", "Disk", "remark", ["Users:Read"]) };
        var after = new[] { new ShareDescriptor("docs", "C:\\docs", "Disk", "remark", ["Users:Read"]) };

        Assert.Empty(differ.Diff(before, after));
    }

    [Fact]
    public async Task RegistryNotificationShareChangesHaveExactQualityAndDedicatedOrigin()
    {
        var before = new[] { new ShareDescriptor("docs", "C:\\docs", "Disk", null, ["AccessMask:0x1"]) };
        var after = new[] { new ShareDescriptor("docs", "C:\\docs", "Disk", "updated", ["AccessMask:0x2"]) };
        var reader = new FakeShareSnapshotReader([before, after]);
        await using var source = new WindowsShareStateSource(reader, new FakeShareNotifier(true, [true]), clock: new FixedClock(Now), fallbackInterval: TimeSpan.Zero);

        var value = await FirstAsync(source.ReadChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EventOrigin.ShareChange, value.Origin);
        Assert.Equal(CanonicalOperation.ShareChanged, value.Hint);
        Assert.Equal(EventQuality.Exact, value.Quality);
        Assert.Equal("RegistryNotification", value.Properties["shareQuality"]);
        Assert.Equal("Changed", value.Properties["shareChangeKind"]);
    }

    [Fact]
    public async Task UnavailableRegistryNotificationUsesThirtySecondFallbackQuality()
    {
        var before = new[] { new ShareDescriptor("docs", "C:\\docs", "Disk", null, ["AccessMask:0x1"]) };
        var after = new[] { new ShareDescriptor("docs", "C:\\docs", "Disk", "updated", ["AccessMask:0x2"]) };
        var reader = new FakeShareSnapshotReader([before, after]);
        await using var source = new WindowsShareStateSource(reader, new FakeShareNotifier(false, []), clock: new FixedClock(Now), fallbackInterval: TimeSpan.Zero);

        var value = await FirstAsync(source.ReadChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(EventQuality.Unknown, value.Quality);
        Assert.Equal("FallbackPolling", value.Properties["shareQuality"]);
    }

    [Fact]
    public async Task CloudPlaceholderBoundaryIsLocalOnlyAndSafeWhenPathDisappears()
    {
        var capability = new CloudPlaceholderCapability();
        var reader = new WindowsCloudPlaceholderReader(new FixedClock(Now));

        var observation = await reader.ReadStateAsync("C:\\StorageChronicle-path-that-does-not-exist", TestContext.Current.CancellationToken);

        Assert.False(capability.IsSupported("CloudApi"));
        Assert.True(observation.State is CloudPlaceholderState.SourceUnknown or CloudPlaceholderState.Unsupported);
        Assert.False(observation.State == CloudPlaceholderState.PlaceholderMetadataChanged);
        Assert.Equal(Now, observation.ObservedUtc);
        Assert.Equal(EventQuality.Unknown, observation.Quality);
    }

    [Fact]
    public void SessionAgentProtocolRoundTripsAndRejectsUnsupportedMajor()
    {
        var payload = new ClipboardCandidateMessage(12, ["C:\\source.txt"], true, "ConfirmedIntent", Now);
        var frame = SessionAgentMessageCodec.Encode("ClipboardCandidate", payload);
        var decoded = SessionAgentMessageCodec.Decode<ClipboardCandidateMessage>(frame);

        Assert.Equal(1, decoded.Major);
        Assert.Equal("ClipboardCandidate", decoded.MessageType);
        Assert.Equal(payload.Generation, decoded.Payload.Generation);
        Assert.Equal(payload.Paths, decoded.Payload.Paths);
        Assert.Equal(payload.IsCut, decoded.Payload.IsCut);
        Assert.Equal(payload.Quality, decoded.Payload.Quality);
        Assert.Equal(payload.ObservedUtc, decoded.Payload.ObservedUtc);
        Assert.Throws<SessionAgentProtocolException>(() => SessionAgentMessageCodec.Decode<ClipboardCandidateMessage>(frame, 2));
    }

    [Fact]
    public void SessionAgentProtocolRejectsCorruptLength()
    {
        var corrupt = new byte[sizeof(int)];
        BitConverter.TryWriteBytes(corrupt, SessionAgentMessageCodec.MaxPayloadBytes + 1);

        Assert.Throws<SessionAgentProtocolException>(() => SessionAgentMessageCodec.Decode<ClipboardCandidateMessage>(corrupt));
    }

    [Fact]
    public async Task SessionAgentPipeStreamsVersionedCandidate()
    {
        var eventValue = CreateClipboardEvent(1, 12, ["C:\\source.txt"], true);
        var output = new MemoryStream();
        var server = new SessionAgentPipeServer(new FakeClipboardEventSource([eventValue]));

        var result = await server.RunAsync(output, TestContext.Current.CancellationToken);
        var decoded = SessionAgentMessageCodec.Decode<ClipboardCandidateMessage>(output.ToArray());

        Assert.False(result.Disconnected);
        Assert.Equal(1, result.SentMessages);
        Assert.Equal(12, decoded.Payload.Generation);
        Assert.True(decoded.Payload.IsCut);
    }

    [Fact]
    public async Task SessionAgentPipeReportsClientDisconnectForRecovery()
    {
        var output = new DisconnectingStream();
        var server = new SessionAgentPipeServer(new FakeClipboardEventSource([CreateClipboardEvent(1, 13, ["C:\\source.txt"], false)]));

        var result = await server.RunAsync(output, TestContext.Current.CancellationToken);

        Assert.True(result.Disconnected);
        Assert.Equal(0, result.SentMessages);
    }

    private static SourceEvent CreateClipboardEvent(long sequence, long generation, IReadOnlyList<string> paths, bool isCut)
    {
        var properties = ImmutableDictionary<string, string>.Empty
            .Add("clipboardGeneration", generation.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Add("clipboardIsCut", isCut.ToString())
            .Add("clipboardQuality", "ConfirmedIntent")
            .Add("clipboardPathCount", paths.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        for (var index = 0; index < paths.Count; index++) properties = properties.Add($"clipboardPath.{index}", paths[index]);
        return new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.Clipboard, null, null, null, null, null, null, null, new EventTime(Now, TimeSpan.Zero, Now, Now, new SourceSequence(sequence), new MountSequence(sequence)), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, generation.ToString(System.Globalization.CultureInfo.InvariantCulture), properties);
    }

    private static async Task<SourceEvent> FirstAsync(IAsyncEnumerable<SourceEvent> values)
    {
        await foreach (var value in values.WithCancellation(TestContext.Current.CancellationToken)) return value;
        throw new InvalidOperationException("The source produced no event.");
    }

    private static async Task<IReadOnlyList<SourceEvent>> CollectAsync(IAsyncEnumerable<SourceEvent> values, int count)
    {
        var result = new List<SourceEvent>();
        await foreach (var value in values.WithCancellation(TestContext.Current.CancellationToken))
        {
            result.Add(value);
            if (result.Count == count) break;
        }

        return result;
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<SourceEvent> values, CancellationToken cancellationToken)
    {
        await foreach (var _ in values.WithCancellation(cancellationToken)) { }
    }

    private sealed class FixedClock(DateTimeOffset value) : ISessionClock
    {
        public DateTimeOffset UtcNow { get; } = value;
    }

    private sealed class FakeClipboardReader(IReadOnlyList<ClipboardReadResult> results) : IClipboardReader
    {
        private int index;
        public int Attempts { get; private set; }

        public ValueTask<ClipboardReadResult> TryReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Attempts++;
            var result = results[Math.Min(index++, results.Count - 1)];
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FakeNotifications(IReadOnlyList<ClipboardNotification> values) : IClipboardNotificationSource
    {
        public async IAsyncEnumerable<ClipboardNotification> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(0, cancellationToken);
                yield return value;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingNotifications : IClipboardNotificationSource
    {
        public async IAsyncEnumerable<ClipboardNotification> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeShareSnapshotReader(IReadOnlyList<IReadOnlyList<ShareDescriptor>> snapshots) : IShareSnapshotReader
    {
        private int index;

        public ValueTask<IReadOnlyList<ShareDescriptor>> ReadAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = snapshots[Math.Min(index++, snapshots.Count - 1)];
            return ValueTask.FromResult(value);
        }
    }

    private sealed class FakeShareNotifier(bool available, IReadOnlyList<bool> results) : IShareChangeNotifier
    {
        private int index;
        public bool IsAvailable { get; } = available;

        public ValueTask<bool> WaitForChangeAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = results.Count == 0 ? false : results[Math.Min(index++, results.Count - 1)];
            return ValueTask.FromResult(value);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeClipboardEventSource(IReadOnlyList<SourceEvent> values) : IClipboardEventSource
    {
        public async IAsyncEnumerable<SourceEvent> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var value in values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(0, cancellationToken);
                yield return value;
            }
        }
    }

    private sealed class DisconnectingStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Disconnected.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Disconnected.");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("Disconnected.");
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException(new IOException("Disconnected."));
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(new IOException("Disconnected."));
    }
}
