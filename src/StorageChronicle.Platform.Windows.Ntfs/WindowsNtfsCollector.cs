using System.Collections.Immutable;
using StorageChronicle.Contracts;
using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.Ntfs;

/// <summary>Converts injected USN reads into source events and reports journal gaps honestly.</summary>
public sealed class WindowsNtfsCollector : ISourceEventCollector
{
    private readonly IUsnJournalReader reader;
    private readonly VolumeId volumeId;
    /// <summary>Initializes an NTFS collector around a thin reader boundary.</summary>
    public WindowsNtfsCollector(VolumeId volumeId, IUsnJournalReader reader) { this.volumeId = volumeId; this.reader = reader ?? throw new ArgumentNullException(nameof(reader)); }
    /// <inheritdoc />
    public async IAsyncEnumerable<SourceEvent> CollectAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var now = DateTimeOffset.UtcNow;
            if (item.GapReason is not null)
            {
                yield return new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.RecoveredUsn, volumeId, null, null, null, null, CanonicalOperation.UnverifiedGap, null, new EventTime(now, TimeZoneInfo.Local.GetUtcOffset(now), null, now, new SourceSequence(item.Sequence), new MountSequence(item.Sequence)), EventQuality.UnverifiedGap, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty.Add("reason", item.GapReason));
                continue;
            }
            if (item.Record is null) continue;
            var record = item.Record;
            yield return new SourceEvent(EventId.New(), EventSchemaVersion.Current, item.Recovered ? EventOrigin.RecoveredUsn : EventOrigin.LiveUsn, volumeId, FileId.Create(record.FileId.ToString(System.Globalization.CultureInfo.InvariantCulture)), FileId.Create(record.ParentFileId.ToString(System.Globalization.CultureInfo.InvariantCulture)), record.Name, null, null, null, new EventTime(now, TimeZoneInfo.Local.GetUtcOffset(now), null, now, new SourceSequence(record.Usn), new MountSequence(item.Sequence)), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty.Add("usnReason", record.Reason.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
    }
}

/// <summary>Thin injectable boundary for FSCTL_QUERY/READ/ENUM_USN_DATA.</summary>
public interface IUsnJournalReader
{
    /// <summary>Reads only records after the persisted USN.</summary>
    IAsyncEnumerable<UsnReadResult> ReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>Represents one journal result or continuity gap.</summary>
public sealed record UsnReadResult(long Sequence, UsnRecord? Record, bool Recovered, string? GapReason);
