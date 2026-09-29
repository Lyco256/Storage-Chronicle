using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.Ntfs;

/// <summary>Converts streamed NTFS USN results into source events without stopping for one bad volume.</summary>
public sealed class WindowsNtfsCollector : ISourceEventCollector
{
    private readonly IUsnJournalReader reader;
    private readonly VolumeId volumeId;

    /// <summary>Gets the latest cursor observed by the real reader, when the injected reader exposes one.</summary>
    public UsnJournalState? LastObservedJournalState => (reader as UsnJournalReader)?.LastObservedState;

    /// <summary>Initializes a collector around a reader boundary, which is useful for driver replacement and tests.</summary>
    public WindowsNtfsCollector(VolumeId volumeId, IUsnJournalReader reader)
    {
        this.volumeId = volumeId;
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    /// <summary>Initializes a collector backed by the real query/read FSCTL implementation.</summary>
    public WindowsNtfsCollector(VolumeId volumeId, string devicePath, INtfsApi api, UsnReadOptions? options = null, UsnJournalState? previousState = null)
        : this(volumeId, new UsnJournalReader(api, devicePath, options, previousState)) { }

    /// <inheritdoc />
    public async IAsyncEnumerable<SourceEvent> CollectAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var renamePairer = new UsnRenamePairer();
        await foreach (var item in reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var now = DateTimeOffset.UtcNow;
            if (item.GapReason is not null)
            {
                yield return CreateGap(item, now);
                continue;
            }

            if (item.Record is null) continue;
            var renamePair = renamePairer.Add(item.Record);
            if ((item.Record.Reason & UsnReason.RenameOldName) != 0 && renamePair is null) continue;
            yield return CreateSourceEvent(item, now, renamePair);
        }

        foreach (var oldName in renamePairer.DrainUnpaired())
        {
            var now = DateTimeOffset.UtcNow;
            yield return CreateSourceEvent(new UsnReadResult(oldName.Usn, oldName, true, null), now, null, EventQuality.Unknown);
        }
    }

    private SourceEvent CreateGap(UsnReadResult item, DateTimeOffset now) =>
        new(
            EventId.New(),
            EventSchemaVersion.Current,
            EventOrigin.RecoveredUsn,
            volumeId,
            null,
            null,
            null,
            null,
            CanonicalOperation.UnverifiedGap,
            null,
            CreateTime(item.Sequence, now),
            EventQuality.UnverifiedGap,
            null,
            ProcessAttributionQuality.Unknown,
            null,
            null,
            ImmutableDictionary<string, string>.Empty
                .Add("reconciliationReason", item.GapReason ?? "Unknown")
                .Add("fileSystem", "NTFS"));

    private SourceEvent CreateSourceEvent(UsnReadResult item, DateTimeOffset now, UsnRenamePair? renamePair = null, EventQuality? qualityOverride = null)
    {
        var record = item.Record!;
        var fileId = FileId.Create(unchecked((ulong)record.FileId).ToString("X16", CultureInfo.InvariantCulture));
        var parentId = FileId.Create(unchecked((ulong)record.ParentFileId).ToString("X16", CultureInfo.InvariantCulture));
        var properties = ImmutableDictionary<string, string>.Empty
            .Add("sourceRoute", "NtfsUsn")
            .Add("fileReferenceSequence", record.FileReferenceSequence.ToString(CultureInfo.InvariantCulture))
            .Add("usnReason", record.Reason.ToString(CultureInfo.InvariantCulture));

        return new SourceEvent(
            EventId.New(),
            EventSchemaVersion.Current,
            item.Recovered ? EventOrigin.RecoveredUsn : EventOrigin.LiveUsn,
            volumeId,
            fileId,
            parentId,
            renamePair?.NewName ?? record.Name,
            renamePair?.OldName,
            DetermineOperation(record.Reason),
            null,
            CreateTime(record.Usn, now),
            qualityOverride ?? (renamePair is null && (record.Reason & UsnReason.RenameNewName) != 0 ? EventQuality.Unknown : renamePair is null ? EventQuality.Exact : EventQuality.Correlated),
            null,
            ProcessAttributionQuality.Unknown,
            null,
            renamePair is null ? null : $"{record.FileId:X16}:{renamePair.OldUsn}:{renamePair.NewUsn}",
            properties);
    }

    private static EventTime CreateTime(long sequence, DateTimeOffset now) =>
        new(now, TimeZoneInfo.Local.GetUtcOffset(now), null, now, new SourceSequence(sequence), new MountSequence(sequence));

    private static CanonicalOperation? DetermineOperation(int reason)
    {
        if ((reason & (UsnReason.RenameOldName | UsnReason.RenameNewName)) != 0) return CanonicalOperation.Rename;
        if ((reason & UsnReason.FileDelete) != 0) return CanonicalOperation.Delete;
        if ((reason & UsnReason.FileCreate) != 0) return CanonicalOperation.Create;
        if ((reason & UsnReason.SecurityChange) != 0) return CanonicalOperation.SecurityMetadataChanged;
        if ((reason & UsnReason.DataTruncation) != 0) return CanonicalOperation.Truncate;
        if ((reason & UsnReason.DataExtend) != 0) return CanonicalOperation.Extend;
        if ((reason & UsnReason.DataOverwrite) != 0) return CanonicalOperation.DataWrite;
        if ((reason & UsnReason.BasicInfoChange) != 0) return CanonicalOperation.MetadataChanged;
        return null;
    }
}

/// <summary>Thin replacement boundary for real FSCTL, driver, and deterministic test readers.</summary>
public interface IUsnJournalReader
{
    /// <summary>Streams journal records and explicit continuity/access gaps.</summary>
    IAsyncEnumerable<UsnReadResult> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Represents one journal result or an explicit continuity/access gap.</summary>
public sealed record UsnReadResult(long Sequence, UsnRecord? Record, bool Recovered, string? GapReason);
