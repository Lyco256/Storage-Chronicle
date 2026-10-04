using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using StorageChronicle.Domain.Contracts;
using StorageChronicle.Platform.Windows.FileSystem.Interop;
using StorageChronicle.RealIoOracleValidator;
using StorageChronicle.Storage;
using Xunit;

namespace StorageChronicle.RealIoOracleValidator.Tests;

public sealed class OracleValidatorTests
{
    [Fact]
    public async Task ExactPathAndStateOperationCoveragePasses()
    {
        var root = CreateTempRoot();
        try
        {
            var volume = VolumeId.Create("test-volume");
            var parent = FileId.Create("parent");
            var file = FileId.Create("file");
            await CreateHistoryAsync(root, volume, parent, file, includeWrite: true, deleteMetadata: false, moveFileId: null);

            var resultPath = OutputPath(root);
            var exitCode = await Program.Main(new[] { "--oracle", WriteOracle(root, new[]
            {
                Operation("DirectoryCreate", "folder", null, 1),
                Operation("Create", "folder\\same.txt", null, 2),
                Operation("DataWrite", "folder\\same.txt", null, 3)
            }), "--history", Path.Combine(root, "history"), "--output", resultPath });

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
            Assert.Equal("PASSED", document.RootElement.GetProperty("Status").GetString());
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task SameNameInAnotherDirectoryCannotSatisfyPathCoverage()
    {
        var root = CreateTempRoot();
        try
        {
            var volume = VolumeId.Create("test-volume");
            var parent = FileId.Create("parent");
            var file = FileId.Create("file");
            await CreateHistoryAsync(root, volume, parent, file, includeWrite: true, deleteMetadata: false, moveFileId: null);

            var resultPath = OutputPath(root);
            var exitCode = await Program.Main(new[] { "--oracle", WriteOracle(root, new[]
            {
                Operation("DataWrite", "other\\same.txt", null, 1)
            }), "--history", Path.Combine(root, "history"), "--output", resultPath });

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
            Assert.Equal("FAILED", document.RootElement.GetProperty("Status").GetString());
            Assert.Contains(document.RootElement.GetProperty("FailureReasons").EnumerateArray(), value => value.GetString() == "FinalStateMissing:other\\same.txt");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task RenameRequiresTheCurrentFileIdentity()
    {
        var root = CreateTempRoot();
        try
        {
            var volume = VolumeId.Create("test-volume");
            var parent = FileId.Create("parent");
            var file = FileId.Create("file");
            var wrongFile = FileId.Create("wrong-file");
            await CreateHistoryAsync(root, volume, parent, file, includeWrite: false, deleteMetadata: false, moveFileId: wrongFile);

            var resultPath = OutputPath(root);
            var exitCode = await Program.Main(new[] { "--oracle", WriteOracle(root, new[]
            {
                Operation("Rename", "folder\\renamed.txt", "folder\\same.txt", 1)
            }), "--history", Path.Combine(root, "history"), "--output", resultPath });

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
            Assert.Contains(document.RootElement.GetProperty("FailureReasons").EnumerateArray(), value => value.GetString() == "OracleCoverage.RenameMove");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task DeleteWithoutRetainedMetadataCannotPass()
    {
        var root = CreateTempRoot();
        try
        {
            var volume = VolumeId.Create("test-volume");
            var parent = FileId.Create("parent");
            var file = FileId.Create("file");
            await CreateHistoryAsync(root, volume, parent, file, includeWrite: false, deleteMetadata: true, moveFileId: null);

            var resultPath = OutputPath(root);
            var exitCode = await Program.Main(new[] { "--oracle", WriteOracle(root, new[]
            {
                Operation("Delete", "folder\\same.txt", null, 1)
            }), "--history", Path.Combine(root, "history"), "--output", resultPath });

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
            Assert.Contains(document.RootElement.GetProperty("FailureReasons").EnumerateArray(), value => value.GetString() == "OracleCoverage.Delete");
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task ExistingEvidenceFileIsPreservedAndNeverOverwritten()
    {
        var root = CreateTempRoot();
        try
        {
            var volume = VolumeId.Create("test-volume");
            var parent = FileId.Create("parent");
            var file = FileId.Create("file");
            await CreateHistoryAsync(root, volume, parent, file, includeWrite: true, deleteMetadata: false, moveFileId: null);
            var resultPath = OutputPath(root);
            byte[] existing = [0x53, 0x41, 0x46, 0x45];
            await File.WriteAllBytesAsync(resultPath, existing);

            var exitCode = await Program.Main(new[] { "--oracle", WriteOracle(root, new[] { Operation("Create", "file.txt", null, 1) }), "--history", Path.Combine(root, "history"), "--output", resultPath });

            Assert.Equal(1, exitCode);
            Assert.Equal(existing, await File.ReadAllBytesAsync(resultPath));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task EvidenceOutsideRunOwnedOracleRootIsRejectedWithoutCreatingParent()
    {
        var root = CreateTempRoot();
        try
        {
            var volume = VolumeId.Create("test-volume");
            var parent = FileId.Create("parent");
            var file = FileId.Create("file");
            await CreateHistoryAsync(root, volume, parent, file, includeWrite: true, deleteMetadata: false, moveFileId: null);
            var outputParent = Path.Combine(root, "unapproved-output");
            var outputPath = Path.Combine(outputParent, "evidence.json");

            var exitCode = await Program.Main(new[] { "--oracle", WriteOracle(root, new[] { Operation("Create", "file.txt", null, 1) }), "--history", Path.Combine(root, "history"), "--output", outputPath });

            Assert.Equal(1, exitCode);
            Assert.False(Directory.Exists(outputParent));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task MissingRunMarkerRejectsEvidenceWithoutCreatingOutput()
    {
        var root = CreateTempRoot();
        try
        {
            var volume = VolumeId.Create("test-volume");
            var parent = FileId.Create("parent");
            var file = FileId.Create("file");
            await CreateHistoryAsync(root, volume, parent, file, includeWrite: true, deleteMetadata: false, moveFileId: null);
            var oraclePath = WriteOracle(root, new[] { Operation("Create", "file.txt", null, 1) });
            File.Delete(Path.Combine(root, ".storage-chronicle-testlab-marker.json"));
            var outputPath = OutputPath(root);

            var exitCode = await Program.Main(new[] { "--oracle", oraclePath, "--history", Path.Combine(root, "history"), "--output", outputPath });

            Assert.Equal(1, exitCode);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task EvidenceDirectoryCollisionIsRejectedAndPreserved()
    {
        var root = CreateTempRoot();
        try
        {
            var volume = VolumeId.Create("test-volume");
            var parent = FileId.Create("parent");
            var file = FileId.Create("file");
            await CreateHistoryAsync(root, volume, parent, file, includeWrite: true, deleteMetadata: false, moveFileId: null);
            var outputPath = OutputPath(root);
            Directory.CreateDirectory(outputPath);

            var exitCode = await Program.Main(new[] { "--oracle", WriteOracle(root, new[] { Operation("Create", "file.txt", null, 1) }), "--history", Path.Combine(root, "history"), "--output", outputPath });

            Assert.Equal(1, exitCode);
            Assert.True(Directory.Exists(outputPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outputPath));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public void PinnedFixtureDirectoryCreatesOnlyRelativeNewFilesAndBlocksRootRename()
    {
        var root = CreateTempRoot();
        try
        {
            var moved = root + "-moved";
            using (var pinned = WindowsPinnedRunOwnedDirectory.OpenExisting(root))
            {
                Assert.Equal(Path.GetFullPath(root), pinned.DirectoryPath);
                Assert.Equal(GetVolumeUniqueId(root).ToUpperInvariant(), pinned.VolumeUniqueId.ToUpperInvariant());
                Assert.Throws<IOException>(() => Directory.Move(root, moved));
                Assert.Throws<ArgumentException>(() => pinned.CreateNewDirectChild("..\\escape.json"));
                Assert.Throws<ArgumentException>(() => pinned.CreateNewDirectChild("alternate:stream"));

                using var created = pinned.CreateNewDirectChild("handle-bound-output.json");
                created.Write("fixture-only"u8);
                created.Flush(true);
            }

            Assert.Equal("fixture-only", File.ReadAllText(Path.Combine(root, "handle-bound-output.json")));
            Assert.False(Directory.Exists(moved));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    [Fact]
    public async Task MarkerForDifferentLiveVolumeRejectsEvidenceWithoutCreatingOutput()
    {
        var root = CreateTempRoot();
        try
        {
            var volume = VolumeId.Create("test-volume");
            var parent = FileId.Create("parent");
            var file = FileId.Create("file");
            await CreateHistoryAsync(root, volume, parent, file, includeWrite: true, deleteMetadata: false, moveFileId: null);
            var markerPath = Path.Combine(root, "StorageChronicleTestVolume.json");
            var marker = JsonSerializer.Serialize(new
            {
                Schema = "StorageChronicle.TestLabDataMarker.v1",
                TestId = GetRunGuid(root),
                Role = "Workload",
                FileSystem = "NTFS",
                VolumeUniqueId = "\\\\?\\Volume{00000000-0000-0000-0000-000000000001}\\"
            });
            await File.WriteAllTextAsync(markerPath, marker);
            var outputPath = OutputPath(root);

            var exitCode = await Program.Main(new[]
            {
                "--oracle", WriteOracle(root, new[] { Operation("Create", "file.txt", null, 1) }),
                "--history", Path.Combine(root, "history"), "--output", outputPath
            });

            Assert.Equal(1, exitCode);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            DeleteTempRoot(root);
        }
    }

    private static async Task CreateHistoryAsync(string root, VolumeId volume, FileId parent, FileId file, bool includeWrite, bool deleteMetadata, FileId? moveFileId)
    {
        var history = new AppendOnlyStorageEngine(new StorageEngineOptions(Path.Combine(root, "history")) { FlushInterval = TimeSpan.FromMinutes(10) });
        try
        {
            var now = DateTimeOffset.UtcNow;
            var parentMetadata = Metadata(volume, parent, null, "folder", FileKind.Directory, true);
            var fileMetadata = Metadata(volume, file, parent, "same.txt", FileKind.File, true);
            var parentSource = Source(volume, parent, null, "folder", CanonicalOperation.DirectoryCreate, parentMetadata, now, 1);
            var fileSource = Source(volume, file, parent, "same.txt", CanonicalOperation.Create, fileMetadata, now.AddMilliseconds(1), 2);
            var parentCanonical = Canonical(parentSource, CanonicalOperation.DirectoryCreate, parentMetadata);
            var fileCanonical = Canonical(fileSource, CanonicalOperation.Create, fileMetadata);
            await history.AppendSourceAsync(parentSource);
            await history.AppendCanonicalAsync(parentCanonical);
            await history.ApplyAsync(parentCanonical);
            await history.AppendSourceAsync(fileSource);
            await history.AppendCanonicalAsync(fileCanonical);
            await history.ApplyAsync(fileCanonical);

            if (includeWrite)
            {
                var writeSource = Source(volume, file, parent, "same.txt", CanonicalOperation.DataWrite, fileMetadata, now.AddMilliseconds(2), 3);
                var writeCanonical = Canonical(writeSource, CanonicalOperation.DataWrite, fileMetadata);
                await history.AppendSourceAsync(writeSource);
                await history.AppendCanonicalAsync(writeCanonical);
                await history.ApplyAsync(writeCanonical);
            }

            if (moveFileId is { } movedId)
            {
                var movedMetadata = Metadata(volume, movedId, parent, "renamed.txt", FileKind.File, true);
                var moveSource = Source(volume, movedId, parent, "renamed.txt", CanonicalOperation.Rename, movedMetadata, now.AddMilliseconds(3), 4, "same.txt");
                var moveCanonical = Canonical(moveSource, CanonicalOperation.Rename, movedMetadata);
                await history.AppendSourceAsync(moveSource);
                await history.AppendCanonicalAsync(moveCanonical);
                await history.ApplyAsync(moveCanonical);
            }

            if (deleteMetadata)
            {
                var deleteSource = Source(volume, file, parent, "same.txt", CanonicalOperation.Delete, null, now.AddMilliseconds(4), 5);
                var deleteCanonical = Canonical(deleteSource, CanonicalOperation.Delete, null);
                await history.AppendSourceAsync(deleteSource);
                await history.AppendCanonicalAsync(deleteCanonical);
                await history.ApplyAsync(deleteCanonical);
            }
        }
        finally
        {
            await history.DisposeAsync();
        }
    }

    private static SourceEvent Source(VolumeId volume, FileId file, FileId? parent, string name, CanonicalOperation operation, FileMetadata? metadata, DateTimeOffset time, long sequence, string? oldName = null) =>
        new(EventId.New(), EventSchemaVersion.Current, EventOrigin.LiveUsn, volume, file, parent, name, oldName, operation, metadata, Time(time, sequence), EventQuality.Exact, null, ProcessAttributionQuality.Unknown, null, null, ImmutableDictionary<string, string>.Empty);

    private static CanonicalEvent Canonical(SourceEvent source, CanonicalOperation operation, FileMetadata? metadata) =>
        new(source.EventId, source.SchemaVersion, operation, source.Origin, source.VolumeId, source.FileId, source.ParentFileId, source.Name, source.OldName, metadata, source.Time, source.Quality, source.ProcessInstanceId, source.ProcessQuality, source.MountSessionId, source.OperationCorrelationId, source.Properties);

    private static FileMetadata Metadata(VolumeId volume, FileId file, FileId? parent, string name, FileKind kind, bool exists) =>
        new(volume, file, parent, name, kind, null, null, null, null, null, null, FileAttributes.Normal, null, null, EventQuality.Exact, exists, false);

    private static EventTime Time(DateTimeOffset now, long sequence) => new(now, TimeSpan.Zero, null, now, new SourceSequence(sequence), new MountSequence(sequence));

    private static string WriteOracle(string root, IReadOnlyList<object> operations)
    {
        var path = Path.Combine(root, "oracle.json");
        var payload = new { Schema = "StorageChronicle.FileMutationWorkload.v2", RunId = GetRunGuid(root), Scenario = "full", CompletedUtc = DateTimeOffset.UtcNow, Process = new { ProcessId = 1, StartTimeUtc = DateTime.UtcNow, ExecutablePath = "test" }, RecordCount = operations.Count, Operations = operations };
        File.WriteAllText(path, JsonSerializer.Serialize(payload));
        return path;
    }

    private static string OutputPath(string root) => Path.Combine(root, $"real-io-evidence-{GetRunGuid(root)}.json");

    private static string GetRunGuid(string root) => Guid.ParseExact(Path.GetFileName(root), "N").ToString("D");

    private static object Operation(string operation, string path, string? oldPath, long sequence) => new { Sequence = sequence, Operation = operation, RelativePath = path, OldRelativePath = oldPath, StartedUtc = DateTimeOffset.UtcNow, CompletedUtc = DateTimeOffset.UtcNow };

    private static string CreateTempRoot()
    {
        var runId = Guid.NewGuid().ToString("N");
        var path = Path.Combine(Path.GetTempPath(), "StorageChronicle.RealIoOracleValidator", runId);
        Directory.CreateDirectory(path);
        using var marker = new FileStream(Path.Combine(path, ".test-owner.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(marker, new { Schema = "StorageChronicle.TestFixtureOwner.v1", RunId = runId });
        var workloadRunId = Guid.ParseExact(runId, "N").ToString("D");
        var workloadMarker = new { Schema = "StorageChronicle.TestLabDataMarker.v1", TestId = workloadRunId, Role = "Workload", VolumeLabel = "SC_TEST_VOLUME", FileSystem = "NTFS", VolumeUniqueId = GetVolumeUniqueId(path) };
        File.WriteAllText(Path.Combine(path, ".storage-chronicle-testlab-marker.json"), JsonSerializer.Serialize(workloadMarker));
        File.WriteAllText(Path.Combine(path, "StorageChronicleTestVolume.json"), JsonSerializer.Serialize(workloadMarker));
        return path;
    }

    private static string GetVolumeUniqueId(string path)
    {
        var mountPoint = new char[1024];
        if (!GetVolumePathName(path, mountPoint, (uint)mountPoint.Length))
            throw new IOException($"Could not resolve the test fixture mount point (Win32 {Marshal.GetLastWin32Error()}).");
        var volumeName = new char[1024];
        var root = new string(mountPoint).TrimEnd('\0');
        if (!Path.EndsInDirectorySeparator(root)) root += Path.DirectorySeparatorChar;
        if (!GetVolumeNameForVolumeMountPoint(root, volumeName, (uint)volumeName.Length))
            throw new IOException($"Could not resolve the test fixture volume GUID (Win32 {Marshal.GetLastWin32Error()}).");
        return new string(volumeName).TrimEnd('\0');
    }

    private static void DeleteTempRoot(string path)
    {
        if (!Directory.Exists(path)) return;
        var fullPath = Path.GetFullPath(path);
        var runId = Path.GetFileName(fullPath);
        if (!Guid.TryParseExact(runId, "N", out _) || !string.Equals(Path.GetDirectoryName(fullPath), Path.Combine(Path.GetTempPath(), "StorageChronicle.RealIoOracleValidator"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("The oracle-validator fixture escaped its dedicated temp root.");
        using var marker = JsonDocument.Parse(File.ReadAllText(Path.Combine(fullPath, ".test-owner.json")));
        if (marker.RootElement.GetProperty("Schema").GetString() != "StorageChronicle.TestFixtureOwner.v1" || marker.RootElement.GetProperty("RunId").GetString() != runId)
            throw new IOException("The oracle-validator fixture marker does not match this run.");
        Directory.Delete(fullPath, recursive: true);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumePathNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, [Out] char[] volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, [Out] char[] volumeName, uint bufferLength);
}
