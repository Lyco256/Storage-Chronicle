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

    /// <summary>Runs the requested workload and writes an operation oracle without reading file contents.</summary>
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
            var records = new List<OracleRecord>();
            var sequence = 0L;
            // Establish the oracle document before any mutation begins. The
            // final write below closes the operation list after the real I/O.
            WriteOracle(options.OraclePath, options, records);
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

            WriteOracle(options.OraclePath, options, records);
            Console.WriteLine(JsonSerializer.Serialize(new { options.RunId, options.Scenario, Count = records.Count, Oracle = options.OraclePath }, JsonOptions));
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
        return root;
    }

    private static TestLabMarker ReadAndValidateMarker(string path, WorkloadOptions options)
    {
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
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0x53, 0x43, 0x01]);
            Add(records, ref sequence, "Create", relative, null);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            Add(records, ref sequence, "Write", relative, null);
        }
    }

    private static void RunFull(string root, int count, ICollection<OracleRecord> records, ref long sequence)
    {
        var tree = Path.Combine(root, "full-tree");
        var movedTree = Path.Combine(root, "full-tree-moved");
        Directory.CreateDirectory(tree);
        Directory.CreateDirectory(Path.Combine(tree, "nested", "deep"));
        Add(records, ref sequence, "DirectoryCreate", Relative(root, tree), null);
        Add(records, ref sequence, "DirectoryCreate", Relative(root, Path.Combine(tree, "nested", "deep")), null);

        for (var index = 0; index < count; index++)
        {
            var empty = Path.Combine(tree, "nested", $"empty-{index:D6}.dat");
            using (File.Create(empty)) { }
            Add(records, ref sequence, "EmptyCreate", Relative(root, empty), null);

            var file = Path.Combine(tree, "nested", "deep", $"item-{index:D6}.dat");
            File.WriteAllBytes(file, [0x53, 0x43, 0x10]);
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
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
            File.SetAttributes(file, FileAttributes.ReadOnly);
            Add(records, ref sequence, "MetadataChanged", Relative(root, file), null);
            File.SetAttributes(file, FileAttributes.Normal);

            var renamed = file + ".renamed";
            File.Move(file, renamed);
            Add(records, ref sequence, "Rename", Relative(root, renamed), Relative(root, file));
            var moved = Path.Combine(tree, $"moved-{index:D6}.dat");
            File.Move(renamed, moved);
            Add(records, ref sequence, "Move", Relative(root, moved), Relative(root, renamed));
        }

        Directory.Move(tree, movedTree);
        Add(records, ref sequence, "DirectoryMove", Relative(root, movedTree), Relative(root, tree));

        var replacement = Path.Combine(root, "same-name-replacement.dat");
        File.WriteAllBytes(replacement, [0x53, 0x43, 0x11]);
        Add(records, ref sequence, "Create", Relative(root, replacement), null);
        File.Delete(replacement);
        Add(records, ref sequence, "Delete", Relative(root, replacement), null);
        File.WriteAllBytes(replacement, [0x53, 0x43, 0x12]);
        Add(records, ref sequence, "CreateReplacement", Relative(root, replacement), null);

        Directory.Delete(movedTree, recursive: true);
        Add(records, ref sequence, "DirectoryDelete", Relative(root, movedTree), null);
    }

    private static void RunBurst(string root, int count, ICollection<OracleRecord> records, ref long sequence)
    {
        var directory = Path.Combine(root, "burst");
        Directory.CreateDirectory(directory);
        var concurrent = new ConcurrentBag<OracleRecord>();
        var nextSequence = sequence;
        Parallel.For(0, count, index =>
        {
            var path = Path.Combine(directory, $"item-{index:D8}.dat");
            using (File.Create(path)) { }
            concurrent.Add(CreateRecord(Interlocked.Increment(ref nextSequence), "Create", Relative(root, path), null));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            concurrent.Add(CreateRecord(Interlocked.Increment(ref nextSequence), "MetadataChanged", Relative(root, path), null));
        });

        sequence = nextSequence;
        foreach (var value in concurrent.OrderBy(value => value.Sequence)) records.Add(value);
    }

    private static void RunParallelProcesses(string root, WorkloadOptions options, ICollection<OracleRecord> records, ref long sequence)
    {
        var childRoot = Path.Combine(root, "parallel");
        Directory.CreateDirectory(childRoot);
        var childCount = Math.Max(1, options.Count / 2);
        var childOracles = ParallelWorkerIds.Select(index => Path.Combine(childRoot, $"child-{index}.json")).ToArray();
        var processes = childOracles.Select((oracle, index) =>
        {
            var processRoot = Path.Combine(childRoot, $"worker-{index}");
            Directory.CreateDirectory(processRoot);
            File.Copy(Path.Combine(root, MarkerName), Path.Combine(processRoot, MarkerName), overwrite: true);
            File.Copy(Path.Combine(root, VolumeMarkerName), Path.Combine(processRoot, VolumeMarkerName), overwrite: true);
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

        foreach (var child in processes)
        {
            child.Process.WaitForExit();
            if (child.Process.ExitCode != 0) throw new IOException($"Parallel workload child failed with exit code {child.Process.ExitCode}: {child.Process.StandardError.ReadToEnd()}");
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
                records.Add(new OracleRecord(++sequence, operation.GetProperty("Operation").GetString() ?? "Unknown", path, oldPath, startedUtc, completedUtc));
            }
        }
    }

    private static void RunShortLived(string root, int count, ICollection<OracleRecord> records, ref long sequence)
    {
        for (var index = 0; index < count; index++)
        {
            var path = Path.Combine(root, "short-lived", $"item-{index:D8}.tmp");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (File.Create(path)) { }
            Add(records, ref sequence, "Create", Relative(root, path), null);
            File.Delete(path);
            Add(records, ref sequence, "Delete", Relative(root, path), null);
        }
    }

    private static void RunAclDenied(string root, ICollection<OracleRecord> records, ref long sequence)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The acl-denied workload requires Windows ACL APIs.");
        var directory = Path.Combine(root, "acl-denied");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "metadata-only-candidate.dat");
        using (File.Create(path)) { }
        Add(records, ref sequence, "Create", Relative(root, path), null);

        var identity = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("The current Windows identity has no security identifier.");
        var fileInfo = new FileInfo(path);
        var security = fileInfo.GetAccessControl();
        var deny = new FileSystemAccessRule(
            identity,
            FileSystemRights.ReadData | FileSystemRights.ReadAttributes | FileSystemRights.ReadExtendedAttributes | FileSystemRights.ReadPermissions,
            AccessControlType.Deny);
        security.AddAccessRule(deny);
        fileInfo.SetAccessControl(security);
        Add(records, ref sequence, "AclDeniedMetadata", Relative(root, path), null);
    }

    private static void RunMft(string root, int count, ICollection<OracleRecord> records, ref long sequence)
    {
        for (var index = 0; index < count; index++)
        {
            var relative = $"mft-{index / 1000:D4}\\entry-{index:D8}.dat";
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (File.Create(path)) { }
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
        Directory.CreateDirectory(source);
        for (var index = 0; index < count; index++)
        {
            var relative = $"rename-source\\item-{index:D6}.dat";
            var path = Path.Combine(root, relative);
            File.WriteAllBytes(path, [0x53, 0x43, 0x02]);
            Add(records, ref sequence, "Create", relative, null);
        }
        Directory.Move(source, destination);
        Add(records, ref sequence, "DirectoryMove", "rename-destination", "rename-source");
    }

    private static void RunDelete(string root, int count, ICollection<OracleRecord> records, ref long sequence)
    {
        var directory = Path.Combine(root, "delete-target");
        Directory.CreateDirectory(directory);
        for (var index = 0; index < count; index++)
        {
            var relative = $"delete-target\\item-{index:D6}.dat";
            File.WriteAllBytes(Path.Combine(root, relative), [0x53, 0x43, 0x03]);
            Add(records, ref sequence, "Create", relative, null);
        }
        Directory.Delete(directory, recursive: true);
        Add(records, ref sequence, "DirectoryDelete", "delete-target", null);
    }

    private static void WriteOracle(string path, WorkloadOptions options, IReadOnlyCollection<OracleRecord> records)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent)) throw new InvalidOperationException("The oracle path must have a parent directory.");
        Directory.CreateDirectory(parent);
        using var process = Process.GetCurrentProcess();
        var processEvidence = new ProcessEvidence(process.Id, process.StartTime.ToUniversalTime(), Environment.ProcessPath ?? "unknown");
        var document = new OracleDocument("StorageChronicle.FileMutationWorkload.v2", options.RunId, options.Scenario, DateTimeOffset.UtcNow, processEvidence, records.Count, records);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(document, JsonOptions));
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
        options = new WorkloadOptions(root, oracle, scenario, runId, count);
        error = string.Empty;
        return true;
    }

    private sealed record WorkloadOptions(string Root, string OraclePath, string Scenario, string RunId, int Count);
    private sealed record TestLabMarker(string Schema, string TestId, string Role, string VolumeLabel, string FileSystem, DateTimeOffset CreatedUtc);
    private sealed record OracleDocument(string Schema, string RunId, string Scenario, DateTimeOffset CompletedUtc, ProcessEvidence Process, int RecordCount, IReadOnlyCollection<OracleRecord> Operations);
    private sealed record ProcessEvidence(int ProcessId, DateTime StartTimeUtc, string ExecutablePath);
    private sealed record OracleRecord(long Sequence, string Operation, string RelativePath, string? OldRelativePath, DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc);
}
