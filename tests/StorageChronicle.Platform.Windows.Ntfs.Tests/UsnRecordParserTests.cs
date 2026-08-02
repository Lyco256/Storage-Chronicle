using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Microsoft.Win32.SafeHandles;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.Ntfs;
using Xunit;

namespace StorageChronicle.Platform.Windows.Ntfs.Tests;

public sealed class UsnRecordParserTests
{
    [Fact]
    public void DocumentedNativeLayoutsRemainStable()
    {
        Assert.Equal(56, NtfsApiLayout.UsnJournalDataV0Size);
        Assert.Equal(44, NtfsApiLayout.ReadUsnJournalDataV0Size);
        Assert.Equal(24, NtfsApiLayout.MftEnumDataV0Size);
    }

    [Fact]
    public void ParsesReadAndEnumBuffersWithContinuationValues()
    {
        var record = Record(0x0001_0000_0000_0001, 2, 3, UsnReason.FileCreate, "file.txt");
        var readBuffer = Prefix(10L, record);
        var enumBuffer = Prefix(20UL, record);
        var parser = new UsnRecordParser();

        var read = parser.ParseReadBuffer(readBuffer, out var nextUsn);
        var enumerated = parser.ParseEnumBuffer(enumBuffer, out var nextFileReference);

        Assert.Equal(10, nextUsn);
        Assert.Equal((ulong)20, nextFileReference);
        Assert.Equal("file.txt", Assert.Single(read).Name);
        Assert.Equal((ulong)0x0001_0000_0000_0001, (ulong)Assert.Single(enumerated).FileId);
        Assert.Equal((ushort)1, Assert.Single(read).FileReferenceSequence);
    }

    [Fact]
    public void ParsesValidRecordAndRejectsMalformedBounds()
    {
        var bytes = Record(1, 2, 3, UsnReason.FileCreate, "file.txt");

        Assert.Equal("file.txt", Assert.Single(new UsnRecordParser().Parse(bytes)).Name);
        Assert.Throws<InvalidDataException>(() => new UsnRecordParser().Parse(bytes.AsSpan(0, 70)));
        var malformedLength = bytes.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(malformedLength, 4);
        Assert.Throws<InvalidDataException>(() => new UsnRecordParser().Parse(malformedLength));
    }

    [Fact]
    public void RenamePairRequiresSameFileReferenceSequence()
    {
        var pairer = new UsnRenamePairer();
        var fileId = (long)0x0002_0000_0000_0001;
        var oldName = new UsnRecord(fileId, 1, 4, UsnReason.RenameOldName, "old.txt");
        var reusedId = new UsnRecord((long)0x0003_0000_0000_0001, 1, 5, UsnReason.RenameNewName, "new.txt");
        var newName = new UsnRecord(fileId, 1, 6, UsnReason.RenameNewName, "new.txt");

        Assert.Null(pairer.Add(oldName));
        Assert.Null(pairer.Add(reusedId));
        var pair = pairer.Add(newName);

        Assert.NotNull(pair);
        Assert.Equal("old.txt", pair.OldName);
        Assert.Equal("new.txt", pair.NewName);
        Assert.Equal((ushort)2, pair.FileReferenceSequence);
    }

    private static byte[] Prefix(long nextUsn, byte[] record)
    {
        var buffer = new byte[sizeof(long) + record.Length];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, nextUsn);
        record.CopyTo(buffer.AsSpan(sizeof(long)));
        return buffer;
    }

    private static byte[] Prefix(ulong nextFileReference, byte[] record)
    {
        var buffer = new byte[sizeof(ulong) + record.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, nextFileReference);
        record.CopyTo(buffer.AsSpan(sizeof(ulong)));
        return buffer;
    }

    internal static byte[] PrefixForRead(long nextUsn, byte[] record) => Prefix(nextUsn, record);

    internal static byte[] PrefixForEnum(ulong nextFileReference, byte[] record) => Prefix(nextFileReference, record);

    internal static byte[] Record(long fileId, long parentId, long usn, int reason, string name, uint attributes = 0)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var length = 64 + nameBytes.Length;
        var bytes = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, length);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(6), 0);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), fileId);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), parentId);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24), usn);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), reason);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(52), attributes);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(56), (ushort)nameBytes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(58), 64);
        nameBytes.CopyTo(bytes.AsSpan(64));
        return bytes;
    }
}

public sealed class NtfsJournalReaderTests
{
    [Fact]
    public async Task ReadsOnlyUnconsumedUsnAndPreservesJournalIdentity()
    {
        var api = new FakeNtfsApi
        {
            Journal = new UsnJournalData(7, 1, 10, 1, 100, 4096, 4096, 2, 2),
            ReadBuffers = new Queue<byte[]>(new[] { UsnRecordParserTests.PrefixForRead(10, UsnRecordParserTests.Record(1, 2, 5, UsnReason.FileCreate, "created.txt")) })
        };
        var reader = new UsnJournalReader(api, "\\\\.\\C:", previousState: new UsnJournalState(7, 4));

        var results = await ReadAll(reader.ReadAsync());

        Assert.Equal((long)4, api.LastReadRequest.StartUsn);
        Assert.Equal("created.txt", Assert.Single(results).Record?.Name);
        Assert.Equal(new UsnJournalState(7, 10), reader.LastObservedState);
    }

    [Fact]
    public async Task JournalIdChangeAndTruncationBecomeExplicitGaps()
    {
        var changedApi = new FakeNtfsApi { Journal = new UsnJournalData(8, 1, 10, 1, 100, 4096, 4096, 2, 2) };
        var changed = await ReadAll(new UsnJournalReader(changedApi, "volume", previousState: new UsnJournalState(7, 4)).ReadAsync());
        var truncatedApi = new FakeNtfsApi { Journal = new UsnJournalData(7, 5, 10, 5, 100, 4096, 4096, 2, 2) };
        var truncated = await ReadAll(new UsnJournalReader(truncatedApi, "volume", previousState: new UsnJournalState(7, 4)).ReadAsync());

        Assert.Contains(changed, item => item.GapReason == "JournalIdChanged");
        Assert.Contains(truncated, item => item.GapReason == "JournalTruncated");
        Assert.Equal(0, changedApi.ReadCallCount);
        Assert.Equal(0, truncatedApi.ReadCallCount);
    }

    [Fact]
    public async Task MissingJournalIsReportedWithoutCreatingOne()
    {
        var api = new FakeNtfsApi { QueryResult = new NtfsApiCallResult(NtfsApiStatus.JournalNotCreated, 0, 1179) };

        var result = await ReadAll(new UsnJournalReader(api, "volume").ReadAsync());

        Assert.Contains(result, item => item.GapReason?.StartsWith("JournalNotCreated", StringComparison.Ordinal) == true);
        Assert.Equal(0, api.CreateOrResizeCallCount);
    }

    [Fact]
    public async Task AccessDeniedAndMediaRemovalAreVolumeLocalFailures()
    {
        var denied = new FakeNtfsApi { ReadResult = new NtfsApiCallResult(NtfsApiStatus.AccessDenied, 0, 5), Journal = Journal() };
        var removed = new FakeNtfsApi { ReadResult = new NtfsApiCallResult(NtfsApiStatus.MediaRemoved, 0, 1167), Journal = Journal() };

        var deniedResult = await ReadAll(new UsnJournalReader(denied, "volume").ReadAsync());
        var removedResult = await ReadAll(new UsnJournalReader(removed, "volume").ReadAsync());

        Assert.Contains(deniedResult, item => item.GapReason?.StartsWith("AccessDenied", StringComparison.Ordinal) == true);
        Assert.Contains(removedResult, item => item.GapReason?.StartsWith("MediaRemoved", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task CancellationIsPropagatedBeforeTheNextNativeRead()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var api = new FakeNtfsApi { Journal = Journal() };

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await ReadAll(new UsnJournalReader(api, "volume").ReadAsync(cancellation.Token)));
        Assert.Equal(0, api.ReadCallCount);
    }

    private static UsnJournalData Journal() => new(7, 1, 10, 1, 100, 4096, 4096, 2, 2);

    private static async Task<List<UsnReadResult>> ReadAll(IAsyncEnumerable<UsnReadResult> values)
    {
        var result = new List<UsnReadResult>();
        await foreach (var value in values) result.Add(value);
        return result;
    }
}

public sealed class MftEnumeratorTests
{
    [Fact]
    public async Task EnumeratesPublicUsnDataAndPreservesSequence()
    {
        var api = new FakeNtfsApi
        {
            Journal = NtfsJournalReaderTestsJournal(),
            EnumBuffers = new Queue<byte[]>(new[]
            {
                UsnRecordParserTests.PrefixForEnum(10, UsnRecordParserTests.Record(0x0001_0000_0000_0001, 2, 3, UsnReason.FileCreate, "folder", 0x10)),
                UsnRecordParserTests.PrefixForEnum(0, Array.Empty<byte>())
            })
        };
        var entries = new List<MftEntry>();
        await foreach (var item in new WindowsMftEnumerator(api, "volume").EnumerateAsync()) entries.Add(item);

        var entry = Assert.Single(entries);
        Assert.True(entry.IsDirectory);
        Assert.Equal((ushort)1, entry.FileReferenceSequence);
        Assert.Equal("0001000000000001", entry.ToFileId().Value);
    }

    [Fact]
    public async Task MftAccessDeniedIsExplicit()
    {
        var api = new FakeNtfsApi { EnumResult = new NtfsApiCallResult(NtfsApiStatus.AccessDenied, 0, 5), Journal = NtfsJournalReaderTestsJournal() };

        await Assert.ThrowsAsync<NtfsAccessException>(async () =>
        {
            await foreach (var _ in new WindowsMftEnumerator(api, "volume").EnumerateAsync()) { }
        });
    }

    [Fact]
    public void ReconciliationReturnsCandidatesWithoutCreatingEvents()
    {
        var current = new[] { new MftEntry(1, 2, 9, "new.txt", 0) };
        var saved = new Dictionary<FileId, (FileId? Parent, string Name, long LastUsn)>
        {
            [FileId.Create("0000000000000001")] = (FileId.Create("0000000000000003"), "old.txt", 8)
        };

        var result = NtfsReconciliationComparer.Compare(current, saved);

        Assert.Single(result);
        Assert.Equal("new.txt", result[0].Current.Name);
    }

    private static UsnJournalData NtfsJournalReaderTestsJournal() => new(7, 1, 10, 1, 100, 4096, 4096, 2, 2);
}

public sealed class WindowsNtfsCollectorTests
{
    [Fact]
    public async Task CollectorPairsRenameNamesAndPreservesUnpairedOldNames()
    {
        var fileId = (long)0x0002_0000_0000_0001;
        var reader = new StubReader(
            new UsnReadResult(3, new UsnRecord(fileId, 2, 3, UsnReason.RenameOldName, "old.txt"), false, null),
            new UsnReadResult(4, new UsnRecord(fileId, 2, 4, UsnReason.RenameNewName, "new.txt"), false, null));

        var events = new List<SourceEvent>();
        await foreach (var item in new WindowsNtfsCollector(VolumeId.Create("volume"), reader).CollectAsync()) events.Add(item);

        var result = Assert.Single(events);
        Assert.Equal(CanonicalOperation.Rename, result.Hint);
        Assert.Equal("old.txt", result.OldName);
        Assert.Equal("new.txt", result.Name);
        Assert.Equal(EventQuality.Correlated, result.Quality);
        Assert.NotNull(result.OperationCorrelationId);
    }

    [Fact]
    public async Task CollectorMapsRecordsAndGapsToSourceEvents()
    {
        var reader = new StubReader(new UsnReadResult(3, new UsnRecord(1, 2, 3, UsnReason.FileDelete, "gone.txt"), false, null), new UsnReadResult(4, null, true, "JournalTruncated"));
        var collector = new WindowsNtfsCollector(VolumeId.Create("volume"), reader);
        var events = new List<SourceEvent>();
        await foreach (var item in collector.CollectAsync()) events.Add(item);

        Assert.Equal(CanonicalOperation.Delete, events[0].Hint);
        Assert.Equal(EventQuality.UnverifiedGap, events[1].Quality);
        Assert.Equal(EventOrigin.RecoveredUsn, events[1].Origin);
    }

    [Fact]
    public void CapabilityDetectionTargetsWindows10ApiSurface()
    {
        var detector = new Windows10CapabilityDetector();

        Assert.True(detector.IsSupported("FsctlReadUsnJournal"));
        Assert.False(detector.IsSupported("Windows11OnlyApi"));
    }
}

public sealed class DirectoryMetadataBatcherTests
{
    private static readonly string[] ExpectedParents = ["000000000000000A", "000000000000000B"];

    [Fact]
    public async Task ReadsOnlySelectedCandidatesInBoundedDirectoryBatches()
    {
        var provider = new FakeMetadataProvider();
        var candidates = ToAsync(new[]
        {
            new MftEntry(1, 10, 1, "one.txt", 0),
            new MftEntry(2, 10, 2, "two.txt", 0),
            new MftEntry(3, 11, 3, "three.txt", 0)
        });

        var result = new List<FileMetadata>();
        await foreach (var item in new DirectoryMetadataBatcher(provider, 2).ReadAsync(candidates)) result.Add(item);

        Assert.Equal(3, result.Count);
        Assert.Equal(ExpectedParents, provider.Parents);
        Assert.All(provider.Batches, batch => Assert.InRange(batch, 1, 2));
    }

    private static async IAsyncEnumerable<MftEntry> ToAsync(IEnumerable<MftEntry> entries)
    {
        foreach (var entry in entries)
        {
            yield return entry;
            await Task.Yield();
        }
    }
}

internal sealed class FakeMetadataProvider : IDirectoryMetadataProvider
{
    public List<string> Parents { get; } = new();
    public List<int> Batches { get; } = new();

    public ValueTask<IReadOnlyList<FileMetadata>> ReadDirectoryBatchAsync(FileId parentFileId, IReadOnlyList<MftEntry> entries, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Parents.Add(parentFileId.Value);
        Batches.Add(entries.Count);
        return ValueTask.FromResult<IReadOnlyList<FileMetadata>>(entries.Select(entry => new FileMetadata(
            VolumeId.Create("volume"),
            entry.ToFileId(),
            parentFileId,
            entry.Name,
            entry.IsDirectory ? FileKind.Directory : FileKind.File,
            null,
            null,
            null,
            null,
            null,
            null,
            FileAttributes.None,
            null,
            null,
            EventQuality.Exact,
            true,
            false)).ToArray());
    }
}

internal sealed class StubReader(params UsnReadResult[] values) : IUsnJournalReader
{
    public async IAsyncEnumerable<UsnReadResult> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var value in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
            await Task.Yield();
        }
    }
}

internal sealed class FakeNtfsApi : INtfsApi
{
    public UsnJournalData? Journal { get; init; }
    public NtfsApiCallResult QueryResult { get; init; } = new(NtfsApiStatus.Success, 56, 0);
    public NtfsApiCallResult ReadResult { get; init; } = new(NtfsApiStatus.Success, 0, 0);
    public NtfsApiCallResult EnumResult { get; init; } = new(NtfsApiStatus.Success, 0, 0);
    public Queue<byte[]> ReadBuffers { get; init; } = new();
    public Queue<byte[]> EnumBuffers { get; init; } = new();
    public int ReadCallCount { get; private set; }
    public int CreateOrResizeCallCount { get; private set; }
    public ReadUsnJournalRequest LastReadRequest { get; private set; }

    public SafeFileHandle OpenVolume(string devicePath) => new(new IntPtr(1), ownsHandle: false);

    public NtfsApiCallResult QueryUsnJournal(SafeFileHandle volumeHandle, out UsnJournalData? data)
    {
        data = Journal;
        return QueryResult;
    }

    public NtfsApiCallResult ReadUsnJournal(SafeFileHandle volumeHandle, ReadUsnJournalRequest request, byte[] outputBuffer, out int bytesReturned)
    {
        LastReadRequest = request;
        ReadCallCount++;
        return Fill(ReadResult, ReadBuffers, outputBuffer, out bytesReturned);
    }

    public NtfsApiCallResult EnumerateUsnData(SafeFileHandle volumeHandle, EnumUsnDataRequest request, byte[] outputBuffer, out int bytesReturned) =>
        Fill(EnumResult, EnumBuffers, outputBuffer, out bytesReturned);

    private static NtfsApiCallResult Fill(NtfsApiCallResult result, Queue<byte[]> buffers, byte[] outputBuffer, out int bytesReturned)
    {
        if (!result.Succeeded || !buffers.TryDequeue(out var source))
        {
            bytesReturned = result.BytesReturned;
            return result;
        }

        source.CopyTo(outputBuffer, 0);
        bytesReturned = source.Length;
        return result with { BytesReturned = bytesReturned };
    }
}
