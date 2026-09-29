using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using System.Reflection;
using Xunit;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using DotNetAssembly = System.Reflection.Assembly;

namespace StorageChronicle.Architecture.Tests;

/// <summary>Architecture gates for the platform-neutral pipeline and its adapter boundaries.</summary>
public sealed class ArchitectureContractTests
{
    private static readonly ArchUnitNET.Domain.Architecture Architecture = new ArchLoader().LoadAssemblies(LoadRequiredAssemblies()).Build();
    private static readonly IObjectProvider<IType> Domain = Types().That().ResideInNamespace("StorageChronicle.Domain.Contracts").As("Domain");
    private static readonly IObjectProvider<IType> Contracts = Types().That().ResideInNamespace("StorageChronicle.Contracts").As("Contracts");
    private static readonly IObjectProvider<IType> Application = Types().That().ResideInNamespace("StorageChronicle.Application").As("Application");
    private static readonly IObjectProvider<IType> Platform = Types().That().ResideInNamespace("StorageChronicle.Platform.Abstractions").As("Platform");
    private static readonly IObjectProvider<IType> Storage = Types().That().ResideInNamespace("StorageChronicle.Storage").As("Storage");

    [Fact]
    public void DomainDoesNotDependOnApplicationPlatformOrStorage()
    {
        IArchRule applicationRule = Types().That().Are(Domain).Should().NotDependOnAny(Application);
        IArchRule platformRule = Types().That().Are(Domain).Should().NotDependOnAny(Platform);
        IArchRule storageRule = Types().That().Are(Domain).Should().NotDependOnAny(Storage);
        applicationRule.Check(Architecture);
        platformRule.Check(Architecture);
        storageRule.Check(Architecture);
    }

    [Fact]
    public void ContractsDoNotDependOnApplicationOrStorage()
    {
        IArchRule applicationRule = Types().That().Are(Contracts).Should().NotDependOnAny(Application);
        IArchRule storageRule = Types().That().Are(Contracts).Should().NotDependOnAny(Storage);
        applicationRule.Check(Architecture);
        storageRule.Check(Architecture);
    }

    [Fact]
    public void StorageAndPlatformDoNotDependOnUiNamespaces()
    {
        var ui = Types().That().ResideInNamespace("StorageChronicle.UI.Shared").As("UI");
        IArchRule storageRule = Types().That().Are(Storage).Should().NotDependOnAny(ui);
        IArchRule platformRule = Types().That().Are(Platform).Should().NotDependOnAny(ui);
        storageRule.Check(Architecture);
        platformRule.Check(Architecture);
    }

    [Fact]
    public void RequiredProjectBoundariesRemainPresent()
    {
        var solution = File.ReadAllText(Path.Combine(FindRoot(), "StorageChronicle.slnx"));
        foreach (var project in new[] { "Domain", "Contracts", "Application", "Normalization", "State", "Projection", "Storage", "Agent" })
        {
            Assert.Contains($"StorageChronicle.{project}", solution, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DomainProjectHasNoProjectReferences()
    {
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(FindRoot(), "src", "StorageChronicle.Domain", "StorageChronicle.Domain.csproj"));
        Assert.Empty(project.Descendants("ProjectReference"));
    }

    private static DotNetAssembly[] LoadRequiredAssemblies()
    {
        var root = FindRoot();
        var paths = new[]
        {
            Path.Combine(root, "src", "StorageChronicle.Domain", "bin", "Debug", "net10.0", "StorageChronicle.Domain.dll"),
            Path.Combine(root, "src", "StorageChronicle.Contracts", "bin", "Debug", "net10.0", "StorageChronicle.Contracts.dll"),
            Path.Combine(root, "src", "StorageChronicle.Application", "bin", "Debug", "net10.0", "StorageChronicle.Application.dll"),
            Path.Combine(root, "src", "StorageChronicle.Normalization", "bin", "Debug", "net10.0", "StorageChronicle.Normalization.dll"),
            Path.Combine(root, "src", "StorageChronicle.State", "bin", "Debug", "net10.0", "StorageChronicle.State.dll"),
            Path.Combine(root, "src", "StorageChronicle.Projection", "bin", "Debug", "net10.0", "StorageChronicle.Projection.dll"),
            Path.Combine(root, "src", "StorageChronicle.Storage", "bin", "Debug", "net10.0", "StorageChronicle.Storage.dll"),
            Path.Combine(root, "src", "StorageChronicle.Platform.Abstractions", "bin", "Debug", "net10.0", "StorageChronicle.Platform.Abstractions.dll"),
            Path.Combine(root, "src", "StorageChronicle.UI.Shared", "bin", "Debug", "net10.0", "StorageChronicle.UI.Shared.dll")
        };
        var missing = paths.Where(path => !File.Exists(path)).ToArray();
        Assert.Empty(missing);
        return paths.Select(DotNetAssembly.LoadFrom).ToArray();
    }

    private static string FindRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "TOP_CODEX.md"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
