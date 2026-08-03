using System.Text;
using System.Text.RegularExpressions;

namespace StorageChronicle.DocMirrorValidator;

/// <summary>Describes the repository locations and rules used by the documentation gate.</summary>
public sealed class DocMirrorValidationOptions
{
    /// <summary>Initializes validation options for a repository.</summary>
    /// <param name="repositoryRoot">Absolute or relative path to the repository root.</param>
    public DocMirrorValidationOptions(string repositoryRoot)
    {
        RepositoryRoot = Path.GetFullPath(repositoryRoot);
    }

    /// <summary>Gets the repository root to validate.</summary>
    public string RepositoryRoot { get; }

    /// <summary>Gets the source directory below <see cref="RepositoryRoot"/>.</summary>
    public string SourceDirectoryName { get; init; } = "src";

    /// <summary>Gets the documentation mirror directory below <see cref="RepositoryRoot"/>.</summary>
    public string DocumentationDirectoryName { get; init; } = Path.Combine("docs", "src");

    /// <summary>Gets a value indicating whether direct source projects must have a README.</summary>
    public bool RequireProjectReadmes { get; init; } = true;

    /// <summary>Gets a value indicating whether each source mirror must cite an existing test path.</summary>
    public bool RequireTestReference { get; init; } = true;
}

/// <summary>Represents one actionable documentation validation failure.</summary>
public sealed record DocMirrorIssue(string Code, string Path, string Message);

/// <summary>Contains the complete result of a documentation mirror validation run.</summary>
public sealed class DocMirrorValidationResult
{
    /// <summary>Initializes a result with its ordered issues.</summary>
    /// <param name="issues">Validation issues, ordered for deterministic command-line output.</param>
    public DocMirrorValidationResult(IEnumerable<DocMirrorIssue> issues)
    {
        Issues = issues.ToArray();
    }

    /// <summary>Gets all validation issues.</summary>
    public IReadOnlyList<DocMirrorIssue> Issues { get; }

    /// <summary>Gets a value indicating whether the repository passed every enabled rule.</summary>
    public bool IsValid => Issues.Count == 0;
}

/// <summary>Validates source-to-document mirrors and the document contract required by the repository.</summary>
public static class DocMirrorValidator
{
    private static readonly HashSet<string> DocumentedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".axaml",
        ".cs",
        ".csproj",
        ".json",
        ".props",
        ".ps1",
        ".sh",
        ".sql",
        ".targets",
        ".wixproj",
        ".wxs"
    };

    private static readonly (string Name, string[] Aliases)[] RequiredHeadings =
    {
        ("Role", ["role", "responsibility", "役割", "責務"]),
        ("Public types and responsibilities", ["public types", "public contract", "public api", "types and responsibilities", "included types", "公開型", "公開契約", "含まれるクラス"]),
        ("Inputs and outputs", ["inputs and outputs", "input and output", "boundary", "inputs", "outputs", "入力", "出力", "境界"]),
        ("Dependencies", ["dependencies", "dependency", "依存"]),
        ("Invariants", ["invariants", "important invariants", "不変条件"]),
        ("Threading and lifetime", ["threading", "thread", "lifetime", "async", "asynchronous", "concurrency", "ライフタイム", "非同期", "スレッド", "並行"]),
        ("Failure behavior", ["failure behavior", "failure and", "failure", "cancellation", "recovery", "失敗", "例外", "キャンセル", "回復"]),
        ("Tests", ["tests", "test", "関連テスト", "テスト"]),
        ("OS constraints", ["os constraints", "os-specific", "platform behavior", "platform constraints", "platform", "windows", "os固有", "os制約", "プラットフォーム"]),
        ("Change-sensitive contracts", ["change-sensitive", "fragile contracts", "contracts", "contract", "change", "変更時", "契約"])
    };

    private static readonly Regex HeadingPattern = new(
        @"^\s*#{2,6}\s+(?<heading>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex TestPathPattern = new(
        @"(?<![A-Za-z0-9_])tests[\\/][^\s`<>\[\](),;:]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Validates the standard repository layout below the supplied root.</summary>
    /// <param name="repositoryRoot">Absolute or relative path to the repository root.</param>
    /// <returns>A deterministic validation result.</returns>
    public static DocMirrorValidationResult Validate(string repositoryRoot)
    {
        return Validate(new DocMirrorValidationOptions(repositoryRoot));
    }

    /// <summary>Validates the repository using explicit source, mirror, and strictness options.</summary>
    /// <param name="options">Validation configuration.</param>
    /// <returns>A deterministic validation result.</returns>
    public static DocMirrorValidationResult Validate(DocMirrorValidationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var issues = new List<DocMirrorIssue>();
        var root = options.RepositoryRoot;
        var sourceRoot = Path.Combine(root, options.SourceDirectoryName);
        var docsRoot = Path.Combine(root, options.DocumentationDirectoryName);
        if (!Directory.Exists(sourceRoot))
        {
            issues.Add(new("configuration", options.SourceDirectoryName, "The source directory does not exist."));
        }

        if (!Directory.Exists(docsRoot))
        {
            issues.Add(new("configuration", options.DocumentationDirectoryName, "The documentation mirror directory does not exist."));
        }

        if (issues.Count > 0)
        {
            return CreateResult(issues);
        }

        var sourceFiles = EnumerateDocumentedSourceFiles(sourceRoot).ToArray();
        var sourceRelativePaths = sourceFiles
            .Select(path => ToPortablePath(Path.GetRelativePath(sourceRoot, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sourceFiles)
        {
            var relative = ToPortablePath(Path.GetRelativePath(sourceRoot, source));
            var mirror = Path.Combine(docsRoot, relative + ".md");
            if (!File.Exists(mirror))
            {
                issues.Add(new("missing-mirror", relative, $"Missing documentation mirror: {relative}.md"));
                continue;
            }

            if (new FileInfo(mirror).Length == 0)
            {
                issues.Add(new("empty-mirror", ToPortablePath(Path.GetRelativePath(root, mirror)), "The documentation mirror is empty."));
                continue;
            }

            ValidateDocument(root, mirror, options, issues);
        }

        ValidateOrphans(root, docsRoot, sourceRelativePaths, options, issues);
        if (options.RequireProjectReadmes)
        {
            ValidateProjectReadmes(root, sourceRoot, docsRoot, issues);
        }

        return CreateResult(issues);
    }

    /// <summary>Runs the command-line validation and returns a process-compatible exit code.</summary>
    /// <param name="args">Optional first argument containing the repository root.</param>
    /// <returns>Zero for success, one for validation failure, or two for invalid layout.</returns>
    public static int RunCommandLine(string[] args)
    {
        var root = args.Length == 0 ? Directory.GetCurrentDirectory() : args[0];
        var result = Validate(root);
        if (result.IsValid)
        {
            Console.WriteLine("Doc mirror validation passed.");
            return 0;
        }

        var hasConfigurationIssue = result.Issues.Any(issue => issue.Code == "configuration");
        foreach (var issue in result.Issues)
        {
            Console.Error.WriteLine($"{issue.Code}: {issue.Path}: {issue.Message}");
        }

        return hasConfigurationIssue ? 2 : 1;
    }

    private static IEnumerable<string> EnumerateDocumentedSourceFiles(string sourceRoot)
    {
        return Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Where(path => !IsGeneratedPath(path) && DocumentedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateDocument(
        string repositoryRoot,
        string documentPath,
        DocMirrorValidationOptions options,
        ICollection<DocMirrorIssue> issues)
    {
        string content;
        try
        {
            content = File.ReadAllText(documentPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            issues.Add(new("unreadable-document", ToPortablePath(Path.GetRelativePath(repositoryRoot, documentPath)), exception.Message));
            return;
        }

        var relative = ToPortablePath(Path.GetRelativePath(repositoryRoot, documentPath));
        ValidateHeadings(relative, content, RequiredHeadings, issues);
        if (options.RequireTestReference)
        {
            ValidateTestReference(repositoryRoot, relative, content, issues);
        }
    }

    private static void ValidateOrphans(
        string repositoryRoot,
        string docsRoot,
        IReadOnlySet<string> sourceRelativePaths,
        DocMirrorValidationOptions options,
        ICollection<DocMirrorIssue> issues)
    {
        foreach (var document in Directory.EnumerateFiles(docsRoot, "*.md", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = ToPortablePath(Path.GetRelativePath(docsRoot, document));
            if (string.Equals(Path.GetFileName(document), "README.md", StringComparison.OrdinalIgnoreCase))
            {
                var directory = Path.GetDirectoryName(relative) ?? string.Empty;
                var sourceDirectory = Path.Combine(repositoryRoot, options.SourceDirectoryName, directory.Replace('/', Path.DirectorySeparatorChar));
                var testDirectory = Path.Combine(repositoryRoot, "tests", directory.Replace('/', Path.DirectorySeparatorChar));
                if (string.IsNullOrEmpty(directory) || (!Directory.Exists(sourceDirectory) && !Directory.Exists(testDirectory)))
                {
                    issues.Add(new("orphan-readme", ToPortablePath(Path.GetRelativePath(repositoryRoot, document)), "README does not describe a source directory."));
                }

                if (Directory.Exists(sourceDirectory))
                {
                    ValidateDocument(repositoryRoot, document, new DocMirrorValidationOptions(repositoryRoot) { RequireProjectReadmes = false }, issues);
                }
                continue;
            }

            if (!relative.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sourceRelative = relative[..^3];
            if (!sourceRelativePaths.Contains(sourceRelative) && !IsAllowedTestMirror(repositoryRoot, sourceRelative))
            {
                issues.Add(new("orphan-mirror", relative, $"No source file exists for {sourceRelative}."));
            }
        }
    }

    private static bool IsAllowedTestMirror(string repositoryRoot, string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        if (!DocumentedExtensions.Contains(extension))
        {
            return false;
        }

        var testPath = Path.Combine(repositoryRoot, "tests", relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(testPath) && !IsGeneratedPath(testPath);
    }

    private static void ValidateProjectReadmes(
        string repositoryRoot,
        string sourceRoot,
        string docsRoot,
        ICollection<DocMirrorIssue> issues)
    {
        foreach (var project in Directory.EnumerateDirectories(sourceRoot)
                     .Where(path => !IsGeneratedPath(path))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var projectName = Path.GetFileName(project);
            var readme = Path.Combine(docsRoot, projectName, "README.md");
            if (!File.Exists(readme))
            {
                issues.Add(new("missing-readme", ToPortablePath(Path.GetRelativePath(repositoryRoot, readme)), "Each source project requires docs/src/<Project>/README.md."));
            }
        }
    }

    private static void ValidateHeadings(
        string document,
        string content,
        IEnumerable<(string Name, string[] Aliases)> requiredHeadings,
        ICollection<DocMirrorIssue> issues)
    {
        var headings = HeadingPattern.Matches(content)
            .Select(match => match.Groups["heading"].Value.Trim().ToLowerInvariant())
            .ToArray();
        foreach (var required in requiredHeadings)
        {
            if (required.Aliases.All(alias => headings.All(heading => !heading.Contains(alias, StringComparison.Ordinal))))
            {
                issues.Add(new("missing-heading", document, $"Required heading is missing: ## {required.Name}"));
            }
        }
    }

    private static void ValidateTestReference(
        string repositoryRoot,
        string document,
        string content,
        ICollection<DocMirrorIssue> issues)
    {
        var references = TestPathPattern.Matches(content)
            .Select(match => match.Value.TrimEnd('.', ';', ',', ')', ']', '>'))
            .Select(ToPortablePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (references.Length == 0)
        {
            issues.Add(new("missing-test-reference", document, "The document must cite at least one path below tests/."));
            return;
        }

        if (references.All(reference => !PathExists(repositoryRoot, reference)))
        {
            issues.Add(new("missing-test-path", document, $"No cited test path exists: {string.Join(", ", references)}"));
        }
    }

    private static bool PathExists(string repositoryRoot, string portablePath)
    {
        var operatingPath = portablePath.Replace('/', Path.DirectorySeparatorChar);
        return File.Exists(Path.Combine(repositoryRoot, operatingPath)) || Directory.Exists(Path.Combine(repositoryRoot, operatingPath));
    }

    private static bool IsGeneratedPath(string path)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part is "bin" or "obj" or "artifacts" or "TestResults");
    }

    private static string ToPortablePath(string path)
    {
        return path.Replace(Path.AltDirectorySeparatorChar, '/').Replace(Path.DirectorySeparatorChar, '/');
    }

    private static DocMirrorValidationResult CreateResult(IEnumerable<DocMirrorIssue> issues)
    {
        return new(issues
            .OrderBy(issue => issue.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(issue => issue.Code, StringComparer.OrdinalIgnoreCase)
            .ThenBy(issue => issue.Message, StringComparer.Ordinal));
    }
}

/// <summary>Console entry point for the repository documentation gate.</summary>
public static class Program
{
    /// <summary>Validates the repository root passed on the command line.</summary>
    /// <param name="args">Optional first argument containing the repository root.</param>
    /// <returns>Zero when validation succeeds; otherwise a non-zero gate exit code.</returns>
    public static int Main(string[] args)
    {
        return DocMirrorValidator.RunCommandLine(args);
    }
}
