using System.Collections.Immutable;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.ExternalMedia;
using Xunit;

namespace StorageChronicle.ExternalMedia.Tests;

public sealed class ExternalMediaTests
{
    [Fact]
    public async Task SegmentAndManifestSurviveRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "StorageChronicle.Media.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ExternalMediaStore(root, "pc-a");
            var segment = await store.AppendSegmentAsync([Event()]);
            var manifest = await store.PublishManifestAsync("media", null, "mount", [segment]);
            Assert.Equal("1", manifest.FormatVersion);
            Assert.Equal(segment.FileName, (await store.ReadManifestSlotAsync())!.Segments[0].FileName);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SameParentCreatesBranch()
    {
        var manifests = new[] { Manifest("a"), Manifest("b") };
        Assert.StartsWith("branch-", ExternalMediaStore.DetectBranch(manifests).Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ManifestRejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "StorageChronicle.Media.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ExternalMediaStore(root, "pc-a");
            await File.WriteAllTextAsync(Path.Combine(root, ".StorageChronicle", "writers", "pc-a", "manifest-A.json"), "{\"FormatVersion\":\"1\",\"LogicalMediaId\":\"m\",\"WriterPcId\":\"p\",\"MountSessionId\":\"s\",\"Segments\":[{\"FileName\":\"..\\\\bad\"}]}" );
            Assert.Null(await store.ReadManifestSlotAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static CanonicalEvent Event() { var now = DateTimeOffset.UtcNow; return new(EventId.New(), EventSchemaVersion.Current, CanonicalOperation.Create, EventOrigin.LiveUsn, VolumeId.Create("v"), FileId.Create("f"), null, "f", null, null, new EventTime(now, TimeSpan.Zero, null, now, new SourceSequence(1), new MountSequence(1)), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty); }
    private static MediaManifest Manifest(string writer) => new("1", EventSchemaVersion.Current, "m", writer, "s", "parent", Array.Empty<MediaSegment>(), DateTimeOffset.UtcNow);
}
