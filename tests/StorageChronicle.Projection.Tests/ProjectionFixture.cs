using System.Collections.Immutable;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Projection;

namespace StorageChronicle.Projection.Tests;

internal static class ProjectionFixture
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static CanonicalEvent Event(
        int seconds,
        CanonicalOperation operation,
        string? path = null,
        string? fileId = null,
        string? processId = "p1",
        ProcessAttributionQuality processQuality = ProcessAttributionQuality.Exact,
        EventOrigin origin = EventOrigin.LiveUsn,
        string? volume = "volume-1",
        string? mount = "mount-1",
        FileKind kind = FileKind.File,
        long? size = null,
        params (string Key, string Value)[] properties)
    {
        var eventId = EventId.New();
        var propertyBuilder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (path is not null) propertyBuilder[ProjectionPropertyNames.Path] = path;
        if (processId is not null) propertyBuilder[ProjectionPropertyNames.ProcessName] = processId == "explorer" ? "explorer.exe" : "worker.exe";
        foreach (var property in properties) propertyBuilder[property.Key] = property.Value;
        VolumeId? volumeId = volume is null ? null : VolumeId.Create(volume);
        FileId? file = fileId is null ? null : FileId.Create(fileId);
        ProcessInstanceId? process = processId is null ? null : ProcessInstanceId.Create(processId);
        MountSessionId? mountSession = mount is null ? null : MountSessionId.Create(mount);
        var metadata = file is null || volumeId is null ? null : new FileMetadata(
            volumeId.Value,
            file.Value,
            null,
            Path.GetFileName(path ?? string.Empty),
            kind,
            size,
            size,
            null,
            null,
            null,
            null,
            FileAttributes.Normal,
            null,
            operation == CanonicalOperation.CloudStateChanged ? "Online" : null,
            EventQuality.Exact,
            !ProjectionOperationRulesForTests.IsDelete(operation),
            operation == CanonicalOperation.Recycle);
        var time = new EventTime(Epoch.AddSeconds(seconds), TimeSpan.Zero, null, Epoch.AddSeconds(seconds), new SourceSequence(seconds + 1), new MountSequence(seconds + 1));
        return new CanonicalEvent(eventId, EventSchemaVersion.Current, operation, origin, volumeId, file, null, Path.GetFileName(path ?? string.Empty), null, metadata, time, EventQuality.Exact, process, processQuality, mountSession, null, propertyBuilder.ToImmutable());
    }

    public static SourceEvent SourceFor(CanonicalEvent value, string? normalizedId = null) => new(
        value.EventId,
        value.SchemaVersion,
        value.Origin,
        value.VolumeId,
        value.FileId,
        value.ParentFileId,
        value.Name,
        value.OldName,
        value.Operation,
        value.Metadata,
        value.Time,
        value.Quality,
        value.ProcessInstanceId,
        value.ProcessQuality,
        value.MountSessionId,
        value.OperationCorrelationId,
        normalizedId is null ? value.Properties : value.Properties.SetItem(ProjectionPropertyNames.NormalizedEventId, normalizedId));

    public static ProjectionDocument Document(params CanonicalEvent[] events) => new(events, events.Select(value => SourceFor(value)));

    public static ProjectionDocument LargeDocument(int count)
    {
        var events = Enumerable.Range(0, count).Select(index => Event(index, CanonicalOperation.DataWrite, $"/root/file-{index}.dat", $"file-{index}", $"process-{index}")).ToArray();
        return Document(events);
    }

    private static class ProjectionOperationRulesForTests
    {
        public static bool IsDelete(CanonicalOperation operation) => operation is CanonicalOperation.Delete or CanonicalOperation.Recycle;
    }
}
