using StorageChronicle.DocMirrorValidator;
using Xunit;

namespace StorageChronicle.DocMirrorValidator.Tests;

/// <summary>Behavioral tests for the source/document contract and its failure reporting.</summary>
public sealed class DocMirrorValidatorTests : IDisposable
{
    private readonly string repositoryRoot = Path.Combine(Path.GetTempPath(), "storage-chronicle-doc-mirror-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Creates an isolated temporary repository for each test instance.</summary>
    public DocMirrorValidatorTests()
    {
        Directory.CreateDirectory(repositoryRoot);
    }

    /// <summary>Removes the isolated temporary repository after the test completes.</summary>
    public void Dispose()
    {
        if (Directory.Exists(repositoryRoot))
        {
            Directory.Delete(repositoryRoot, recursive: true);
        }
    }

    [Fact]
    public void ValidatesSourceMirrorHeadingsTestPathAndProjectReadme()
    {
        CreateValidRepository();

        var result = DocMirrorValidator.Validate(repositoryRoot);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
    }

    [Fact]
    public void ReportsMissingMirrorAndReadme()
    {
        CreateSource("StorageChronicle.Sample", "Sample.cs");
        CreateTestPath("StorageChronicle.Sample.Tests", "SampleTests.cs");
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "docs", "src"));

        var result = DocMirrorValidator.Validate(repositoryRoot);

        Assert.Contains(result.Issues, issue => issue.Code == "missing-mirror");
        Assert.Contains(result.Issues, issue => issue.Code == "missing-readme");
    }

    [Fact]
    public void ReportsOrphanMirror()
    {
        CreateValidRepository();
        Write("docs/src/StorageChronicle.Sample/Removed.cs.md", "# Removed");

        var result = DocMirrorValidator.Validate(repositoryRoot);

        Assert.Contains(result.Issues, issue => issue.Code == "orphan-mirror");
    }

    [Fact]
    public void AllowsExistingLegacyTestMirrorWithoutMakingTestsTheSourceOfTruth()
    {
        CreateValidRepository();
        Write("docs/src/StorageChronicle.Sample.Tests/SampleTests.cs.md", "# Legacy test mirror");

        var result = DocMirrorValidator.Validate(repositoryRoot);

        Assert.DoesNotContain(result.Issues, issue => issue.Code == "orphan-mirror");
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
    }

    [Fact]
    public void ReportsEmptyMirror()
    {
        CreateValidRepository();
        File.WriteAllText(Path.Combine(repositoryRoot, "docs/src/StorageChronicle.Sample/Sample.cs.md"), string.Empty);

        var result = DocMirrorValidator.Validate(repositoryRoot);

        Assert.Contains(result.Issues, issue => issue.Code == "empty-mirror");
    }

    [Fact]
    public void ReportsEveryMissingRequiredHeading()
    {
        CreateSource("StorageChronicle.Sample", "Sample.cs");
        CreateTestPath("StorageChronicle.Sample.Tests", "SampleTests.cs");
        Write("docs/src/StorageChronicle.Sample/README.md", ReadmeText("tests/StorageChronicle.Sample.Tests/SampleTests.cs"));
        Write("docs/src/StorageChronicle.Sample/Sample.cs.md", "# Sample\n\n`tests/StorageChronicle.Sample.Tests/SampleTests.cs`");

        var result = DocMirrorValidator.Validate(repositoryRoot);

        Assert.True(result.Issues.Count(issue => issue.Code == "missing-heading") >= 9);
    }

    [Fact]
    public void RequiresAnExistingTestPathRatherThanOnlyATestsHeading()
    {
        CreateSource("StorageChronicle.Sample", "Sample.cs");
        Write("docs/src/StorageChronicle.Sample/README.md", ReadmeText("tests/StorageChronicle.Sample.Tests/SampleTests.cs"));
        Write("docs/src/StorageChronicle.Sample/Sample.cs.md", DocumentText("tests/StorageChronicle.Sample.Tests/MissingTests.cs"));

        var result = DocMirrorValidator.Validate(repositoryRoot);

        Assert.Contains(result.Issues, issue => issue.Code == "missing-test-path");
    }

    [Fact]
    public void IncludesApplicationSpecificSourceExtensionsAndIgnoresGeneratedFiles()
    {
        CreateValidRepository();
        CreateSource("StorageChronicle.Sample", "Schema.sql");
        CreateSource("StorageChronicle.Sample", "Generated.cs", "bin");
        Write("docs/src/StorageChronicle.Sample/Schema.sql.md", DocumentText("tests/StorageChronicle.Sample.Tests/SampleTests.cs"));

        var result = DocMirrorValidator.Validate(repositoryRoot);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
    }

    [Fact]
    public void ReturnsConfigurationFailureWhenRootsAreMissing()
    {
        var result = DocMirrorValidator.Validate(repositoryRoot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Code == "configuration");
        Assert.Equal(2, DocMirrorValidator.RunCommandLine([repositoryRoot]));
    }

    [Fact]
    public void CanDisableTestReferenceAndProjectReadmeRulesForFocusedUse()
    {
        CreateSource("StorageChronicle.Sample", "Sample.cs");
        Write("docs/src/StorageChronicle.Sample/Sample.cs.md", "# Sample");

        var result = DocMirrorValidator.Validate(new DocMirrorValidationOptions(repositoryRoot)
        {
            RequireProjectReadmes = false,
            RequireTestReference = false
        });

        Assert.True(result.Issues.All(issue => issue.Code == "missing-heading"), string.Join(Environment.NewLine, result.Issues));
    }

    private void CreateValidRepository()
    {
        CreateSource("StorageChronicle.Sample", "Sample.cs");
        CreateTestPath("StorageChronicle.Sample.Tests", "SampleTests.cs");
        Write("docs/src/StorageChronicle.Sample/README.md", ReadmeText("tests/StorageChronicle.Sample.Tests/SampleTests.cs"));
        Write("docs/src/StorageChronicle.Sample/Sample.cs.md", DocumentText("tests/StorageChronicle.Sample.Tests/SampleTests.cs"));
    }

    private void CreateSource(string project, string fileName, params string[] subdirectories)
    {
        var relative = Path.Combine(["src", project, .. subdirectories, fileName]);
        Write(relative, "namespace Sample; public sealed class SampleType { }");
    }

    private void CreateTestPath(string project, string fileName)
    {
        Write(Path.Combine("tests", project, fileName), "namespace SampleTests; public sealed class SampleTests { }");
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(repositoryRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static string DocumentText(string testPath)
    {
        return string.Join(Environment.NewLine, new[]
        {
            "# Sample",
            "",
            "## Role",
            "Describes the sample source boundary.",
            "",
            "## Public types and responsibilities",
            "Documents the sample type and its public responsibility.",
            "",
            "## Inputs and outputs",
            "The source accepts its configured input and produces canonical output.",
            "",
            "## Dependencies",
            "Depends on the platform-neutral repository contract.",
            "",
            "## Invariants",
            "The source does not read file contents.",
            "",
            "## Threading and lifetime",
            "The caller owns lifetime and cancellation.",
            "",
            "## Failure behavior",
            "Failures are reported without weakening the gate.",
            "",
            "## Tests",
            $"`{testPath}`",
            "",
            "## OS constraints",
            "No OS-specific behavior is assumed.",
            "",
            "## Change-sensitive contracts",
            "The mirror path and headings are stable gate contracts."
        });
    }

    private static string ReadmeText(string testPath)
    {
        return string.Join(Environment.NewLine, new[]
        {
            "# Sample project",
            "",
            "## Role",
            "Owns the sample project boundary.",
            "",
            "## Public types and responsibilities",
            "Exposes the sample project contract.",
            "",
            "## Inputs and outputs",
            "Consumes source inputs and exposes project output.",
            "",
            "## Dependencies",
            "Keeps dependencies within the sample boundary.",
            "",
            "## Invariants",
            "The project remains independently documented.",
            "",
            "## Threading and lifetime",
            "No long-lived process is owned here.",
            "",
            "## Failure behavior",
            "Build and test failures remain visible.",
            "",
            "## Tests",
            $"`{testPath}`",
            "",
            "## OS constraints",
            "The project is platform neutral.",
            "",
            "## Change-sensitive contracts",
            "Project references and mirror layout are stable contracts."
        });
    }
}
