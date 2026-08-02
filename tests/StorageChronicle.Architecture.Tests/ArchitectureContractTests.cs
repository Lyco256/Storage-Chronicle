using System.Xml.Linq;
using Xunit;

namespace StorageChronicle.Architecture.Tests;

public sealed class ArchitectureContractTests
{
    [Fact]
    public void SolutionContainsRequiredBoundaries()
    {
        var root = FindRoot();
        var solution = File.ReadAllText(Path.Combine(root, "StorageChronicle.slnx"));
        foreach (var project in new[] { "Domain", "Contracts", "Application", "Normalization", "State", "Projection", "Storage", "Agent" })
            Assert.Contains($"StorageChronicle.{project}", solution, StringComparison.Ordinal);
    }

    [Fact]
    public void DomainHasNoProjectReferences()
    {
        var project = XDocument.Load(Path.Combine(FindRoot(), "src", "StorageChronicle.Domain", "StorageChronicle.Domain.csproj"));
        Assert.Empty(project.Descendants("ProjectReference"));
    }

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "TOP_CODEX.md"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
