namespace StorageChronicle.DocMirrorValidator;

/// <summary>Validates the source-to-document mirror required by the repository contract.</summary>
public static class Program
{
    /// <summary>Checks source files and their markdown mirrors beneath the supplied repository root.</summary>
    public static int Main(string[] args)
    {
        var root = args.Length == 0 ? Directory.GetCurrentDirectory() : Path.GetFullPath(args[0]);
        var sourceRoot = Path.Combine(root, "src");
        var docsRoot = Path.Combine(root, "docs", "src");
        if (!Directory.Exists(sourceRoot) || !Directory.Exists(docsRoot))
        {
            Console.Error.WriteLine("src/ and docs/src/ are required.");
            return 2;
        }

        var failures = new List<string>();
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                     .Where(IsDocumentedSource))
        {
            var relative = Path.GetRelativePath(sourceRoot, source);
            var mirror = Path.Combine(docsRoot, relative + ".md");
            if (!File.Exists(mirror)) failures.Add($"missing mirror: {relative}");
            else if (new FileInfo(mirror).Length == 0) failures.Add($"empty mirror: {relative}");
        }

        if (failures.Count == 0)
        {
            Console.WriteLine("Doc mirror validation passed.");
            return 0;
        }

        foreach (var failure in failures) Console.Error.WriteLine(failure);
        return 1;
    }

    private static bool IsDocumentedSource(string path)
    {
        var extension = Path.GetExtension(path);
        return !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   .Any(part => part is "bin" or "obj") &&
               extension is ".cs" or ".axaml" or ".ps1" or ".sh";
    }
}
