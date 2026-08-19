using System.Globalization;
using System.Text.Json;

namespace StorageChronicle.FileMutationWorkload;

/// <summary>Executes real metadata-only file mutations inside a marked disposable TestLab data volume.</summary>
public static class Program
{
    private const string MarkerName = ".storage-chronicle-testlab-marker.json";
    private const string VolumeMarkerName = "StorageChronicleTestVolume.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Runs the requested workload and writes an operation oracle without reading file contents.</summary>
    public static int Main(string[] args)
    {
        if (!TryParse(args, out var options, out var error))
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("usage: StorageChronicle.FileMutationWorkload --root <marked-data-root> --oracle <path> --scenario basic|rename-move|delete|mft --count <n> --run-id <guid>");
            return 2;
        }

        try
        {
            var root = PrepareRoot(options);
            var records = new List<OracleRecord>();
            var sequence = 0L;
            switch (options.Scenario)
            {
                case "basic":
                    RunBasic(root, options.Count, records, ref sequence);
                    break;
                case "rename-move":
                    RunRenameMove(root, Math.Max(1, options.Count), records, ref sequence);
                    break;
                case "delete":
                    RunDelete(root, Math.Max(1, options.Count), records, ref sequence);
                    break;
                case "mft":
                    RunBasic(root, options.Count, records, ref sequence);
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
            _ => throw new InvalidDataException($"The TestLab marker role is unsupported: {marker.Role}")
        };
        if (!string.Equals(marker.VolumeLabel, expected.Label, StringComparison.Ordinal) || !string.Equals(marker.FileSystem, expected.FileSystem, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The TestLab marker label or filesystem is not an approved role value.");
        if (options.Scenario == "mft" && !string.Equals(marker.Role, "Mft", StringComparison.Ordinal)) throw new InvalidDataException("The MFT scenario requires an SC_TEST_MFT_VOLUME marker.");
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
            records.Add(new OracleRecord(++sequence, "Create", relative, null));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            records.Add(new OracleRecord(++sequence, "Write", relative, null));
        }
    }

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
            records.Add(new OracleRecord(++sequence, "Create", relative, null));
        }
        Directory.Move(source, destination);
        records.Add(new OracleRecord(++sequence, "DirectoryMove", "rename-destination", "rename-source"));
    }

    private static void RunDelete(string root, int count, ICollection<OracleRecord> records, ref long sequence)
    {
        var directory = Path.Combine(root, "delete-target");
        Directory.CreateDirectory(directory);
        for (var index = 0; index < count; index++)
        {
            var relative = $"delete-target\\item-{index:D6}.dat";
            File.WriteAllBytes(Path.Combine(root, relative), [0x53, 0x43, 0x03]);
            records.Add(new OracleRecord(++sequence, "Create", relative, null));
        }
        Directory.Delete(directory, recursive: true);
        records.Add(new OracleRecord(++sequence, "DirectoryDelete", "delete-target", null));
    }

    private static void WriteOracle(string path, WorkloadOptions options, IReadOnlyCollection<OracleRecord> records)
    {
        var fullPath = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parent)) throw new InvalidOperationException("The oracle path must have a parent directory.");
        Directory.CreateDirectory(parent);
        var document = new OracleDocument("StorageChronicle.FileMutationWorkload.v1", options.RunId, options.Scenario, DateTimeOffset.UtcNow, records.Count, records);
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
        if (scenario is not ("basic" or "rename-move" or "delete" or "mft")) { options = default!; error = "scenario must be basic, rename-move, delete, or mft."; return false; }
        options = new WorkloadOptions(root, oracle, scenario, runId, count);
        error = string.Empty;
        return true;
    }

    private sealed record WorkloadOptions(string Root, string OraclePath, string Scenario, string RunId, int Count);
    private sealed record TestLabMarker(string Schema, string TestId, string Role, string VolumeLabel, string FileSystem, DateTimeOffset CreatedUtc);
    private sealed record OracleDocument(string Schema, string RunId, string Scenario, DateTimeOffset CompletedUtc, int RecordCount, IReadOnlyCollection<OracleRecord> Operations);
    private sealed record OracleRecord(long Sequence, string Operation, string RelativePath, string? OldRelativePath);
}
