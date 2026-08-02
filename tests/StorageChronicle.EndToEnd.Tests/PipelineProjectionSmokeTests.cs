using System.Collections.Immutable;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Normalization;
using StorageChronicle.Projection;
using Xunit;

namespace StorageChronicle.EndToEnd.Tests;

public sealed class PipelineProjectionSmokeTests
{
    [Fact]
    public void CanonicalEventIsVisibleInAllProjectionModes()
    {
        var volume = VolumeId.Create("volume-e2e");
        var file = FileId.Create("file-e2e");
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var metadata = new FileMetadata(volume, file, null, "e2e.txt", FileKind.File, 4, 4096, now, now, now, now, FileAttributes.Normal, null, null, EventQuality.Exact, true, false);
        var source = new SourceEvent(EventId.New(), EventSchemaVersion.Current, EventOrigin.LiveUsn, volume, file, null, metadata.Name, null, CanonicalOperation.Create, metadata, new EventTime(now, TimeSpan.Zero, null, now, new SourceSequence(1), new MountSequence(1)), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, MountSessionId.Create("mount-e2e"), null, ImmutableDictionary<string, string>.Empty);
        var canonical = new EventNormalizer().Normalize(source);
        Assert.NotNull(canonical);
        var document = new ProjectionDocument([canonical], [source]);
        var projector = new EventStackProjector();
        Assert.Single(projector.GetTreePage(document, new EventStackQuery(EventStackMode.Source, 1, 10), new ProjectionSettings()).Items);
        Assert.Single(projector.GetTreePage(document, new EventStackQuery(EventStackMode.Normalized, 1, 10), new ProjectionSettings()).Items);
        Assert.Single(projector.GetTreePage(document, new EventStackQuery(EventStackMode.Grouped, 1, 10), new ProjectionSettings()).Items);
    }
}
