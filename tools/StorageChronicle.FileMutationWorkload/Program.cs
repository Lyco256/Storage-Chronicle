using System.Globalization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace StorageChronicle.FileMutationWorkload;

/// <summary>Executes real metadata-only file mutations inside a marked disposable TestLab data volume.</summary>
public static class Program
{
    private const string MarkerName = ".storage-chronicle-testlab-marker.json";
    private const string VolumeMarkerName = "StorageChronicleTestVolume.json";
    private static readonly int[] ParallelWorkerIds = [0, 1];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Runs metadata-only operations in a fresh marked root and create-new publishes one run-bound operation oracle.</summary>
    public static int Main(string[] args)
    {
        if (!TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("usage: StorageChronicle.FileMutationWorkload --root <marked-data-root> --oracle <path> --scenario basic|full|burst|parallel|rename-move|delete|short-lived|acl-denied|mft --count <n> --run-id <guid>");
            return 2;
        }

        try
        {
            var root = PrepareRoot(options);
            var oraclePath = ValidateNewOraclePath(root, options);
            var records = new List<OracleRecord>();
            var sequence = 0L;
            switch (options.Scenario)
            {
                case "basic":
                    RunBasic(root, options.Count, records, ref sequence);
                    break;
                case "full":
                    RunFull(root, Math.Min(options.Count, 10_000), records, ref sequence);
                    break;
                case "burst":
                    RunBurst(root, options.Count, records, ref sequence);
                    break;
                case "parallel":
                    RunParallelProcesses(root, options, records, ref sequence);
                    break;
                case "rename-move":
                    RunRenameMove(root, Math.Max(1, options.Count), records, ref sequence);
                    break;
                case "delete":
                    RunDelete(root, Math.Max(1, options.Count), records, ref sequence);
                    break;
                case "short-lived":
                    RunShortLived(root, Math.Max(1, options.Count), records, ref sequence);
                    break;
                case "acl-denied":
                    RunAclDenied(root, records, ref sequence);
                    break;
                case "mft":
                    RunMft(root, options.Count, records, ref sequence);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported scenario: {options.Scenario}");
            }

            WriteOracle(oraclePath, root, options, records);
            Console.WriteLine(JsonSerializer.Serialize(new { options.RunId, options.Scenario, Count = records.Count, Oracle = oraclePath }, JsonOptions));
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"FAIL_CLOSED: {exception.Message}");
            return 1;
        }
    }

    private static string PrepareRoot(WorkloadOptions options)
    {
        var root = Path.GetFullPath(options.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var volumeRoot = Path.GetPathRoot(root)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(root) || string.Equals(root, volumeRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The workload root must not be a volume root.");
        if (string.Equals(volumeRoot, "C:", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The workload refuses the guest system volume C:. Use a marked disposable data VHDX.");
        if (root.Contains("Windows", StringComparison.OrdinalIgnoreCase) || root.Contains("Program Files", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The workload root is protected.");
        if (!Directory.Exists(root)) throw new InvalidDataException("The workload root must already exist on a marked TestLab volume.");
        EnsureNoReparsePoints(root);
        var markerPath = Path.Combine(root, MarkerName);
        var marker = ReadAndValidateMarker(markerPath, options);
        var volumeMarkerPath = Path.Combine(root, VolumeMarkerName);
        var volumeMarker = ReadAndValidateMarker(volumeMarkerPath, options);
        if (!string.Equals(marker.Role, volumeMarker.Role, StringComparison.Ordinal) ||
            !string.Equals(marker.VolumeLabel, volumeMarker.VolumeLabel, StringComparison.Ordinal) ||
            !string.Equals(marker.FileSystem, volumeMarker.FileSystem, StringComparison.Ordinal)) throw new InvalidDataException("The two TestLab markers disagree about the volume role or format.");
        var driveRoot = Path.GetPathRoot(root) ?? throw new InvalidDataException("The workload root has no volume root.");
        var drive = new DriveInfo(driveRoot);
        if (!drive.IsReady || !string.Equals(drive.VolumeLabel, marker.VolumeLabel, StringComparison.OrdinalIgnoreCase) || !string.Equals(drive.DriveFormat, marker.FileSystem, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The mounted volume label or filesystem does not match the TestLab marker.");
        var allowedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MarkerName, VolumeMarkerName };
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            EnsureNoReparsePoints(entry);
            var name = Path.GetFileName(entry);
            if (!allowedEntries.Remove(name)) throw new IOException($"The workload root is not a fresh, run-owned fixture; refusing unknown or duplicate entry: {entry}");
            if (Directory.Exists(entry)) throw new IOException($"The workload root marker is not a regular file: {entry}");
        }
        if (allowedEntries.Count != 0) throw new IOException("The workload root is missing one or more required ownership markers.");
        return root;
    }

    private static string ValidateNewOraclePath(string root, WorkloadOptions options)
    {
        var fullPath = Path.GetFullPath(options.OraclePath);
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent) || !string.Equals(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The workload oracle must be a direct child of the verified run-owned root.");
        var expectedName = "oracle-" + options.RunId + ".json";
        var leafName = Path.GetFileName(fullPath);
        var isWorkerOracle = Enumerable.Range(0, ParallelWorkerIds.Length).Any(index => string.Equals(leafName, $"oracle-{options.RunId}-worker-{index}.json", StringComparison.OrdinalIgnoreCase));
        if (!string.Equals(leafName, expectedName, StringComparison.OrdinalIgnoreCase) && !isWorkerOracle)
            throw new IOException($"The workload oracle must use the run-specific name {expectedName}.");
        EnsureNoReparsePoints(fullPath);
        if (File.Exists(fullPath) || Directory.Exists(fullPath)) throw new IOException("The run-specific workload oracle already exists; refusing to overwrite it.");
        return fullPath;
    }

    private static TestLabMarker ReadAndValidateMarker(string path, WorkloadOptions options)
    {
        EnsureNoReparsePoints(path);
        if (!File.Exists(path)) throw new InvalidDataException($"The required TestLab marker is missing: {path}");
        using var stream = File.OpenRead(path);
        var marker = JsonSerializer.Deserialize<TestLabMarker>(stream) ?? throw new InvalidDataException($"The TestLab marker is invalid: {path}");
        if (!string.Equals(marker.Schema, "StorageChronicle.TestLabDataMarker.v1", StringComparison.Ordinal) || !string.Equals(marker.TestId, options.RunId, StringComparison.Ordinal)) throw new InvalidDataException("The TestLab marker does not match this run.");
        var expected = marker.Role switch
        {
            "Workload" => (Label: "SC_TEST_VOLUME", FileSystem: "NTFS"),
            "Mft" => (Label: "SC_TEST_MFT_VOLUME", FileSystem: "NTFS"),
            "NonNtfs" => (Label: "SC_TEST_NONNTFS_VOLUME", FileSystem: "exFAT"),
            "AclDenied" => (Label: "SC_TEST_VOLUME", FileSystem: "NTFS"),
            _ => throw new InvalidDataException($"The TestLab marker role is unsupported: {marker.Role}")
        };
        if (!string.Equals(marker.VolumeLabel, expected.Label, StringComparison.Ordinal) || !string.Equals(marker.FileSystem, expected.FileSystem, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The TestLab marker label or filesystem is not an approved role value.");
        if (options.Scenario == "mft" && !string.Equals(marker.Role, "Mft", StringComparison.Ordinal)) throw new InvalidDataException("The MFT scenario requires an SC_TEST_MFT_VOLUME marker.");
        if (options.Scenario == "acl-denied" && !string.Equals(marker.Role, "AclDenied", StringComparison.Ordinal)) throw new InvalidDataException("The acl-denied scenario requires an SC_TEST_VOLUME marker with the AclDenied role.");
        return marker;
    }

    private static void RunBasic(string root, int count, ICollection<OracleRecord> records, ref long sequence)
    {
        for (var index = 0; index < count; index++)
        {
            var relative = $"batch-{index % 100:D3}\\file-{index:D8}.dat";
            var path = Path.Combine(root, relative);
            EnsureDirectory(Path.GetDirectoryName(path)!);
            WriteNewFile(path, [0x53, 0x43, 0x01]);
            Add(records, ref sequence, "Create", relative, null);
            SetRunFileLastWriteTimeUtc(path);
            Add(records, ref sequence, "Write", relative, null);
        }
    }

    private static void RunFull(string root, int count, ICollection<OracleRecord> records, ref long sequence)
    {
        var tree = Path.Combine(root, "full-tree");
        var movedTree = Path.Combine(root, "full-tree-moved");
        EnsureDirectory(tree);
        EnsureDirectory(Path.Combine(tree, "nested", "deep"));
        Add(records, ref sequence, "DirectoryCreate", Relative(root, tree), null);
        Add(records, ref sequence, "DirectoryCreate", Relative(root, Path.Combine(tree, "nested", "deep")), null);

        for (var index = 0; index < count; index++)
        {
            var empty = Path.Combine(tree, "nested", $"empty-{index:D6}.dat");
            using (CreateNewFile(empty)) { }
            Add(records, ref sequence, "EmptyCreate", Relative(root, empty), null);

            var file = Path.Combine(tree, "nested", "deep", $"item-{index:D6}.dat");
            WriteNewFile(file, [0x53, 0x43, 0x10]);
            Add(records, ref sequence, "Create", Relative(root, file), null);
            Add(records, ref sequence, "DataWrite", Relative(root, file), null);
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                stream.SetLength(1);
                stream.Flush(flushToDisk: true);
            }
            Add(records, ref sequence, "Truncate", Relative(root, file), null);
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                stream.SetLength(64);
                stream.Flush(flushToDisk: true);
            }
            Add(records, ref sequence, "Extend", Relative(root, file), null);
            SetRunFileLastWriteTimeUtc(file);
            SetRunFileAttributes(file, FileAttributes.ReadOnly);
            Add(records, ref sequence, "MetadataChanged", Relative(root, file), null);
            SetRunFileAttributes(file, FileAttributes.Normal);

            var renamed = file + ".renamed";
            MoveRunFileNoOverwrite(file, renamed);
            Add(records, ref sequence, "Rename", Relative(root, renamed), Relative(root, file));
            var moved = Path.Combine(tree, $"moved-{index:D6}.dat");
            MoveRunFileNoOverwrite(renamed, moved);
            Add(records, ref sequence, "Move", Relative(root, moved), Relative(root, renamed));
        }

        MoveRunDirectoryNoOverwrite(tree, movedTree);
        Add(records, ref sequence, "DirectoryMove", Relative(root, movedTree), Relative(root, tree));

        var replacement = Path.Combine(root, "same-name-replacement.dat");
        WriteNewFile(replacement, [0x53, 0x43, 0x11]);
        Add(records, ref sequence, "Create", Relative(root, replacement), null);
        DeleteRunFile(replacement);
        Add(records, ref sequence, "Delete", Relative(root, replacement), null);
        WriteNewFile(replacement, [0x53, 0x43, 0x12]);
        Add(records, ref sequence, "CreateReplacement", Relative(root, replacement), null);

        AssertExpectedFullTreeForDeletion(movedTree, count);
        EnsureNoReparsePoints(movedTree);
        Directory.Delete(movedTree, recursive: true);
        Add(records, ref sequence, "DirectoryDelete", Relative(root, movedTree), null);
    }

    private static void RunBurst(string root, int count, ICollection<OracleRecord> records, ref long sequence)
    {
        var directory = Path.Combine(root, "burst");
        EnsureDirectory(directory);
        var concurrent = new ConcurrentBag<OracleRecord>();
        var nextSequence = sequence;
        Parallel.For(0, count, index =>
        {
            var path = Path.Combine(directory, $"item-{index:D8}.dat");
            using (CreateNewFile(path)) { }
            concurrent.Add(CreateRecord(Interlocked.Increment(ref nextSequence), "Create", Relative(root, path), null));
            SetRunFileLastWriteTimeUtc(path);
            concurrent.Add(CreateRecord(Interlocked.Increment(ref nextSequence), "MetadataChanged", Relative(root, path), null));
        });

        sequence = nextSequence;
        foreach (var value in concurrent.OrderBy(value => value.Sequence)) records.Add(value);
    }

    private static void RunParallelProcesses(string root, WorkloadOptions options, ICollection<OracleRecord> records, ref long sequence)
    {
        var childRoot = Path.Combine(root, "parallel");
        EnsureDirectory(childRoot);
        var childCount = Math.Max(1, options.Count / 2);
        var childOracles = ParallelWorkerIds.Select(index => Path.Combine(childRoot, $"worker-{index}", $"oracle-{options.RunId}-worker-{index}.json")).ToArray();
        var processes = childOracles.Select((oracle, index) =>
        {
            var processRoot = Path.Combine(childRoot, $"worker-{index}");
            EnsureDirectory(processRoot);
            CopyNewRunFile(Path.Combine(root, MarkerName), Path.Combine(processRoot, MarkerName));
            CopyNewRunFile(Path.Combine(root, VolumeMarkerName), Path.Combine(processRoot, VolumeMarkerName));
            var info = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("The workload process path is unavailable."))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            var childArgs = new[] { "--root", processRoot, "--oracle", oracle, "--scenario", "basic", "--count", childCount.ToString(CultureInfo.InvariantCulture), "--run-id", options.RunId };
            foreach (var argument in childArgs) info.ArgumentList.Add(argument);
            var process = Process.Start(info) ?? throw new InvalidOperationException("A parallel workload child could not be started.");
            return (Process: process, Oracle: oracle);
        }).ToArray();

        foreach (var (child, index) in processes.Select((value, index) => (value, index)))
        {
            child.Process.WaitForExit();
            if (child.Process.ExitCode != 0) throw new IOException($"Parallel workload child failed with exit code {child.Process.ExitCode}: {child.Process.StandardError.ReadToEnd()}");
            EnsureNoReparsePoints(child.Oracle);
            using var document = JsonDocument.Parse(File.ReadAllText(child.Oracle));
            foreach (var operation in document.RootElement.GetProperty("Operations").EnumerateArray())
            {
                var path = operation.GetProperty("RelativePath").GetString() ?? throw new InvalidDataException("Parallel oracle path is missing.");
                var oldPath = operation.TryGetProperty("OldRelativePath", out var old) && old.ValueKind != JsonValueKind.Null ? old.GetString() : null;
                var startedUtc = operation.TryGetProperty("StartedUtc", out var started) && started.TryGetDateTimeOffset(out var parsedStarted)
                    ? parsedStarted
                    : DateTimeOffset.UtcNow;
                var completedUtc = operation.TryGetProperty("CompletedUtc", out var completed) && completed.TryGetDateTimeOffset(out var parsedCompleted)
                    ? parsedCompleted
                    : startedUtc;
                var prefix = $"parallel\\worker-{index}\\";
                records.Add(new OracleRecord(++sequence, operation.GetProperty("Operation").GetString() ?? "Unknown", prefix + path, oldPath is null ? null : prefix + oldPath, startedUtc, completedUtc));
            }
        }
    }

    private static void RunShortLived(string root, int count, ICollection<OracleRecord> records, ref long sequence)
    {
        for (var index = 0; index < count; index++)
        {
            var path = Path.Combine(root, "short-lived", $"item-{index:D8}.tmp");
            EnsureDirectory(Path.GetDirectoryName(path)!);
            using (CreateNewFile(path)) { }
            Add(records, ref sequence, "Create", Relative(root, path), null);
            DeleteRunFile(path);
            Add(records, ref sequence, "Delete", Relative(root, path), null);
        }
    }

    private static void RunAclDenied(string root, ICollection<OracleRecord> records, ref long sequence)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The acl-denied workload requires Windows ACL APIs.");
        var directory = Path.Combine(root, "acl-denied");
        EnsureDirectory(directory);
        var path = Path.Combine(directory, "metadata-only-candidate.dat");
        using (CreateNewFile(path)) { }
        Add(records, ref sequence, "Create", Relative(root, path), null);

        var identity = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current Windows identity has no security identifier.");
        var fileInfo = new FileInfo(path);
        EnsureRegularRunFile(path);
        var security = fileInfo.GetAccessControl();
        var deny = new FileSystemAccessRule(
            identity,
            FileSystemRights.ReadData | FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes | FileSystemRights.ReadPermissions,
            AccessControlType.Deny);
        security.AddAccessRule(deny);
        EnsureNoReparsePoints(path);
        fileInfo.SetAccessControl(security);
        Add(records, ref sequence, "AclDeniedMetadata", Relative(root, path), null);
    }

    private static void RunMft(string root, int count, ICollection<OracleRecord> records, ref long sequence)
    {
        for (var index = 0; index < count; index++)
        {
            var relative = $"mft-{index / 1000:D4}\\entry-{index:D8}.dat";
            var path = Path.Combine(root, relative);
            EnsureDirectory(Path.GetDirectoryName(path)!);
            using (CreateNewFile(path)) { }
            Add(records, ref sequence, "Create", relative, null);
        }
    }

    private static void Add(ICollection<OracleRecord> records, ref long sequence, string operation, string path, string? oldPath) =>
        records.Add(CreateRecord(++sequence, operation, path, oldPath));

    private static OracleRecord CreateRecord(long sequence, string operation, string path, string? oldPath)
    {
        var timestamp = DateTimeOffset.UtcNow;
        return new OracleRecord(sequence, operation, path, oldPath, timestamp, timestamp);
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '\\');

    private static void RunRenameMove(string root, int count, ICollection<OracleRecord> records, ref long sequence)
    {
        var source = Path.Combine(root, "rename-source");
        var destination = Path.Combine(root, "rename-destination");
        EnsureDirectory(source);
        for (var index = 0; index < count; index++)
        {
            var relative = $"rename-source\\item-{index:D6}.dat";
            var path = Path.Combine(root, relative);
            WriteNewFile(path, [0x53, 0x43, 0x02]);
            Add(records, ref sequence, "Create", relative, null);
        }
        MoveRunDirectoryNoOverwrite(source, destination);
        Add(records, ref sequence, "DirectoryMove", "rename-destination", "rename-source");
    }

    private static void RunDelete(string root, int count, ICollection<OracleRecord> records, ref long sequence)
    {
        var directory = Path.Combine(root, "delete-target");
        EnsureDirectory(directory);
        for (var index = 0; index < count; index++)
        {
            var relative = $"delete-target\\item-{index:D6}.dat";
            WriteNewFile(Path.Combine(root, relative), [0x53, 0x43, 0x03]);
            Add(records, ref sequence, "Create", relative, null);
        }
        var expectedFiles = Enumerable.Range(0, count).Select(index => $"item-{index:D6}.dat").ToHashSet(StringComparer.OrdinalIgnoreCase);
        AssertExpectedDirectFilesForDeletion(directory, expectedFiles);
        EnsureNoReparsePoints(directory);
        Directory.Delete(directory, recursive: true);
        Add(records, ref sequence, "DirectoryDelete", "delete-target", null);
    }

    private static void WriteOracle(string path, string root, WorkloadOptions options, IReadOnlyCollection<OracleRecord> records)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The workload oracle must remain directly inside the run-owned root.");
        EnsureNoReparsePoints(fullPath);
        using var process = Process.GetCurrentProcess();
        var processEvidence = new ProcessEvidence(process.Id, process.StartTime.ToUniversalTime(), Environment.ProcessPath ?? "unknown", options.Scenario);
        var processes = new Dictionary<(int Id, DateTime StartTimeUtc), ProcessEvidence>
        {
            [(processEvidence.ProcessId, processEvidence.StartTimeUtc)] = processEvidence
        };
        var parallelRoot = Path.Combine(root, "parallel");
        if (Directory.Exists(parallelRoot))
        {
            for (var index = 0; index < ParallelWorkerIds.Length; index++)
            {
                var childOraclePath = Path.Combine(parallelRoot, $"worker-{index}", $"oracle-{options.RunId}-worker-{index}.json");
                if (!File.Exists(childOraclePath)) continue;
                try
                {
                    EnsureNoReparsePoints(childOraclePath);
                    var child = JsonSerializer.Deserialize<OracleDocument>(File.ReadAllText(childOraclePath), JsonOptions);
                    if (child is null) continue;
                    foreach (var childProcess in child.Processes ?? [child.Process])
                    {
                        processes.TryAdd((childProcess.ProcessId, childProcess.StartTimeUtc), childProcess);
                    }
                }
                catch (JsonException)
                {
                    // A partial child oracle is not evidence of a complete
                    // parallel run; the live correlation gate will reject it.
                }
                catch (IOException)
                {
                    // Keep the parent oracle metadata-only; missing child
                    // process evidence remains an acceptance failure.
                }
            }
        }

        var document = new OracleDocument("StorageChronicle.FileMutationWorkload.v2", options.RunId, options.Scenario, DateTimeOffset.UtcNow, processEvidence, processes.Values.ToArray(), records.Count, records);
        WriteNewFile(fullPath, JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));
    }

    private static FileStream CreateNewFile(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrWhiteSpace(parent)) throw new IOException("A workload file must have a parent directory.");
        EnsureNoReparsePoints(parent);
        return new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
    }

    private static void WriteNewFile(string path, ReadOnlySpan<byte> content)
    {
        using var output = CreateNewFile(path);
        output.Write(content);
        output.Flush(flushToDisk: true);
    }

    private static void EnsureDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent)) throw new IOException("A workload directory must have a parent.");
        EnsureNoReparsePoints(parent);
        if (File.Exists(fullPath)) throw new IOException($"A file already occupies a workload directory path: {fullPath}");
        Directory.CreateDirectory(fullPath);
        EnsureNoReparsePoints(fullPath);
    }

    private static void SetRunFileLastWriteTimeUtc(string path)
    {
        EnsureRegularRunFile(path);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
    }

    private static void SetRunFileAttributes(string path, FileAttributes attributes)
    {
        EnsureRegularRunFile(path);
        File.SetAttributes(path, attributes);
    }

    private static void MoveRunFileNoOverwrite(string source, string destination)
    {
        EnsureRegularRunFile(source);
        EnsureNoReparsePoints(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException($"Refusing to replace an existing workload destination: {destination}");
        File.Move(source, destination);
    }

    private static void MoveRunDirectoryNoOverwrite(string source, string destination)
    {
        EnsureNoReparsePoints(source);
        EnsureNoReparsePoints(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException($"Refusing to replace an existing workload directory destination: {destination}");
        Directory.Move(source, destination);
    }

    private static void DeleteRunFile(string path)
    {
        EnsureRegularRunFile(path);
        File.Delete(path);
    }

    private static void CopyNewRunFile(string source, string destination)
    {
        EnsureRegularRunFile(source);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        using var output = CreateNewFile(destination);
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static void EnsureRegularRunFile(string path)
    {
        EnsureNoReparsePoints(path);
        if (!File.Exists(path) || Directory.Exists(path)) throw new IOException($"A run-owned workload file is missing or is not a regular file: {path}");
    }

    private static void AssertExpectedFullTreeForDeletion(string tree, int count)
    {
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "nested", Path.Combine("nested", "deep") };
        for (var index = 0; index < count; index++)
        {
            expected.Add(Path.Combine("nested", $"empty-{index:D6}.dat"));
            expected.Add($"moved-{index:D6}.dat");
        }

        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(tree);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                EnsureNoReparsePoints(entry);
                var relative = Path.GetRelativePath(tree, entry);
                if (!expected.Contains(relative)) throw new IOException($"The workload subtree contains an unexpected entry; refusing recursive deletion: {entry}");
                if (!observed.Add(relative)) throw new IOException($"The workload subtree contains a duplicate entry; refusing recursive deletion: {entry}");
                if (Directory.Exists(entry)) pending.Push(entry);
                else if (!File.Exists(entry)) throw new IOException($"The workload subtree entry is not a regular file or directory: {entry}");
            }
        }

        if (!observed.SetEquals(expected)) throw new IOException("The workload subtree does not exactly match this run's expected file and directory set; refusing recursive deletion.");
    }

    private static void AssertExpectedDirectFilesForDeletion(string directory, IReadOnlySet<string> expectedFiles)
    {
        EnsureNoReparsePoints(directory);
        if (Directory.EnumerateDirectories(directory).Any()) throw new IOException("The delete scenario contains an unexpected subdirectory; refusing recursive deletion.");
        var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            EnsureRegularRunFile(path);
            if (!observed.Add(Path.GetFileName(path))) throw new IOException("The delete scenario contains duplicate file names; refusing recursive deletion.");
        }
        if (!observed.SetEquals(expectedFiles)) throw new IOException("The delete scenario contents do not exactly match files created by this run; refusing recursive deletion.");
    }

    private static void EnsureNoReparsePoints(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"The workload refuses paths that traverse reparse points: {current}");
            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
            current = parent;
        }
    }

    private static bool TryParse(string[] args, out WorkloadOptions options, out string error)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length) { options = default!; error = $"Invalid argument: {args[index]}"; return false; }
            values[args[index][2..]] = args[++index];
        }
        if (!values.TryGetValue("root", out var root) || !values.TryGetValue("oracle", out var oracle) || !values.TryGetValue("scenario", out var scenario) || !values.TryGetValue("run-id", out var runId) || !values.TryGetValue("count", out var countText) || !int.TryParse(countText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count is < 1 or > 1_000_000) { options = default!; error = "root, oracle, scenario, run-id, and count (1..1,000,000) are required."; return false; }
        if (scenario is not ("basic" or "full" or "burst" or "parallel" or "rename-move" or "delete" or "short-lived" or "acl-denied" or "mft")) { options = default!; error = "scenario must be basic, full, burst, parallel, rename-move, delete, short-lived, acl-denied, or mft."; return false; }
        if (!Guid.TryParseExact(runId, "D", out _)) { options = default!; error = "run-id must be a canonical GUID in D format."; return false; }
        options = new WorkloadOptions(root, oracle, scenario, runId, count);
        error = string.Empty;
        return true;
    }

    private sealed record WorkloadOptions(string Root, string OraclePath, string Scenario, string RunId, int Count);
    private sealed record TestLabMarker(string Schema, string TestId, string Role, string VolumeLabel, string FileSystem, DateTimeOffset CreatedUtc);
    private sealed record OracleDocument(string Schema, string RunId, string Scenario, DateTimeOffset CompletedUtc, ProcessEvidence Process, IReadOnlyCollection<ProcessEvidence> Processes, int RecordCount, IReadOnlyCollection<OracleRecord> Operations);
    private sealed record ProcessEvidence(int ProcessId, DateTime StartTimeUtc, string ExecutablePath, string ScenarioId);
    private sealed record OracleRecord(long Sequence, string Operation, string RelativePath, string? OldRelativePath, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc);
}
