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

    [Fact]
    public void FileMutationWorkloadUsesFreshRunBoundCreateNewOutputs()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "tools", "StorageChronicle.FileMutationWorkload", "Program.cs"));
        Assert.Contains("FileMode.CreateNew", source, StringComparison.Ordinal);
        Assert.Contains("EnsureOutsideProtectedRoots(fullRoot)", source, StringComparison.Ordinal);
        Assert.Contains("ValidateNewOraclePath", source, StringComparison.Ordinal);
        Assert.Contains("GetVolumeNameForVolumeMountPoint", source, StringComparison.Ordinal);
        Assert.Contains("marker.VolumeUniqueId", source, StringComparison.Ordinal);
        Assert.Contains("allowedEntries.Remove(name)", source, StringComparison.Ordinal);
        Assert.Contains("oracle-\" + options.RunId + \".json", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllBytes(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllText(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Copy(", source, StringComparison.Ordinal);

        var fullTreeCheck = source.IndexOf("AssertExpectedFullTreeForDeletion(movedTree, count)", StringComparison.Ordinal);
        var firstRecursiveDelete = source.IndexOf("Directory.Delete(movedTree, recursive: true)", StringComparison.Ordinal);
        var directDeleteCheck = source.IndexOf("AssertExpectedDirectFilesForDeletion(directory, expectedFiles)", StringComparison.Ordinal);
        var secondRecursiveDelete = source.IndexOf("Directory.Delete(directory, recursive: true)", StringComparison.Ordinal);
        Assert.True(fullTreeCheck >= 0 && fullTreeCheck < firstRecursiveDelete, "The full-tree deletion must verify the exact run-generated tree first.");
        Assert.True(directDeleteCheck >= 0 && directDeleteCheck < secondRecursiveDelete, "The delete scenario must verify exact run-generated files first.");
    }

    [Fact]
    public void FileMutationWorkloadRejectsSystemUserSynchronizedAndRepositoryRoots()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrWhiteSpace(userProfile));

        var knownProtectedRoots = new[]
        {
            userProfile,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86)
        };
        foreach (var protectedRoot in knownProtectedRoots.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
            Assert.Throws<InvalidOperationException>(() => FileMutationWorkload.Program.EnsureOutsideProtectedRoots(Path.Combine(protectedRoot!, "StorageChronicleFixture")));

        foreach (var variable in new[] { "OneDrive", "OneDriveCommercial", "OneDriveConsumer" })
        {
            var previousValue = Environment.GetEnvironmentVariable(variable);
            var synchronizedRoot = Path.Combine(Path.GetTempPath(), $"StorageChronicle-{variable}-{Guid.NewGuid():N}");
            try
            {
                Environment.SetEnvironmentVariable(variable, synchronizedRoot);
                Assert.Throws<InvalidOperationException>(() => FileMutationWorkload.Program.EnsureOutsideProtectedRoots(Path.Combine(synchronizedRoot, "StorageChronicleFixture")));
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previousValue);
            }
        }

        var repositoryRoot = FindRoot();
        Assert.Throws<InvalidOperationException>(() => FileMutationWorkload.Program.EnsureOutsideProtectedRoots(Path.Combine(repositoryRoot, "artifacts", "StorageChronicleFixture")));

        var userParent = Directory.GetParent(Path.TrimEndingDirectorySeparator(Path.GetFullPath(userProfile)))?.FullName;
        Assert.NotNull(userParent);
        var profileName = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(userProfile)));
        FileMutationWorkload.Program.EnsureOutsideProtectedRoots(Path.Combine(userParent!, profileName + "-StorageChronicleFixture"));
    }

    [Fact]
    public void BenchmarkFixtureDirectoryCreationAndCleanupAreRunBound()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "benchmarks", "StorageChronicle.Benchmarks", "Program.cs"));
        Assert.Contains("CreateDirectoryW(path, 0)", source, StringComparison.Ordinal);
        Assert.Contains("if (error == ErrorAlreadyExists) continue", source, StringComparison.Ordinal);
        Assert.Contains("OwnerMarkerName", source, StringComparison.Ordinal);
        Assert.Contains("Path.GetTempPath()", source, StringComparison.Ordinal);
        Assert.Contains("Path.GetFileName(fullPath), \"data\"", source, StringComparison.Ordinal);
        Assert.Contains("FileAttributes.ReparsePoint", source, StringComparison.Ordinal);

        var markerValidation = source.IndexOf("Refusing to remove a benchmark fixture without its matching run ownership marker", StringComparison.Ordinal);
        var recursiveDelete = source.IndexOf("Directory.Delete(containerPath, recursive: true)", StringComparison.Ordinal);
        Assert.True(markerValidation >= 0 && recursiveDelete > markerValidation, "Recursive cleanup must require matching run ownership evidence first.");
    }

    [Fact]
    public void BenchmarksDoNotChangeHostPowerPlanAndUseStableBuildOutputLayout()
    {
        var root = FindRoot();
        var benchmarkProgram = File.ReadAllText(Path.Combine(root, "benchmarks", "StorageChronicle.Benchmarks", "Program.cs"));
        var mftBenchmarks = File.ReadAllText(Path.Combine(root, "benchmarks", "StorageChronicle.Benchmarks", "MftBenchmarks.cs"));
        var directoryBuildProps = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));

        Assert.Contains("DontEnforcePowerPlan()", benchmarkProgram, StringComparison.Ordinal);
        Assert.Contains("WithUnrollFactor(1)", benchmarkProgram, StringComparison.Ordinal);
        Assert.Contains("Environment.SetEnvironmentVariable(\"UseArtifactsOutput\", \"false\")", benchmarkProgram, StringComparison.Ordinal);
        Assert.Equal(2, benchmarkProgram.Split("writeAuthorization: _ => true", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("InProcessEmitToolchain", benchmarkProgram, StringComparison.Ordinal);
        Assert.DoesNotContain("--inProcess", benchmarkProgram, StringComparison.Ordinal);
        Assert.DoesNotContain("WithPowerPlan(", benchmarkProgram, StringComparison.Ordinal);
        Assert.DoesNotContain("WithPowerPlan(", mftBenchmarks, StringComparison.Ordinal);
        foreach (var source in new[] { benchmarkProgram, mftBenchmarks })
        {
            Assert.DoesNotContain("[InvocationCount(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("[IterationCount(", source, StringComparison.Ordinal);
            Assert.DoesNotContain("[WarmupCount(", source, StringComparison.Ordinal);
        }
        Assert.Contains("<UseArtifactsOutput>false</UseArtifactsOutput>", directoryBuildProps, StringComparison.Ordinal);
    }

    [Fact]
    public void FullBenchmarkMatrixSeparatesTheMillionRecordLane()
    {
        var root = FindRoot();
        var matrix = File.ReadAllText(Path.Combine(root, "build", "quality", "Test-FullBenchmarkMatrix.ps1"));
        var appendSuite = matrix.IndexOf("Name = 'AppendAndCompression'", StringComparison.Ordinal);
        var storageSuite = matrix.IndexOf("Name = 'StorageAppend1M'", StringComparison.Ordinal);

        Assert.True(appendSuite >= 0 && storageSuite > appendSuite, "The 1M Storage suite must remain an explicit matrix lane.");
        var appendBlock = matrix[appendSuite..storageSuite];
        Assert.Contains("'*SegmentAppendAndSqliteIndex100K*'", appendBlock, StringComparison.Ordinal);
        Assert.Contains("'*FlushAndCloseCompressedSegment100K*'", appendBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("'*StorageAppendBenchmarks*'", appendBlock, StringComparison.Ordinal);
        Assert.Contains("$filterPatterns = @($suite.Filter)", matrix, StringComparison.Ordinal);
        Assert.Contains("'--filter') + $filterPatterns", matrix, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplorerScenarioPreparationUsesOnlyBoundNewOutputs()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "tools", "TestEnvironment", "New-ExplorerCorrelationScenario.ps1"));
        Assert.Contains("Get-Volume -FilePath", source, StringComparison.Ordinal);
        Assert.Contains("marker.VolumeUniqueId", source, StringComparison.Ordinal);
        Assert.Contains("Assert-NoReparsePath", source, StringComparison.Ordinal);
        Assert.Contains("Test-AcceptancePathIsProtected -Path $rootFull", source, StringComparison.Ordinal);
        Assert.Contains("Test-AcceptancePathWithinProtectedRoot -Path $rootFull -ProtectedRoot $repositoryRoot", source, StringComparison.Ordinal);
        Assert.Contains("TestLab root must contain exactly the two ownership marker files", source, StringComparison.Ordinal);
        Assert.Contains("Preflight is read-only and prints its result to stdout", source, StringComparison.Ordinal);
        Assert.Contains("OutputPath must be a direct child of the new run-owned scenario directory", source, StringComparison.Ordinal);
        Assert.Contains("FileMode]::CreateNew", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Content", source, StringComparison.Ordinal);
        Assert.DoesNotContain("New-Item -ItemType Directory -Force", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaRootValidationPinsTheExactValidatedDirectoryHandle()
    {
        var source = File.ReadAllText(Path.Combine(FindRoot(), "src", "StorageChronicle.Platform.Windows.FileSystem", "Interop", "WindowsVolumeDirectorySession.cs"));
        var methodStart = source.IndexOf("private void EnsureOwnedMediaRoot()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        var methodEnd = source.IndexOf("private SafeFileHandle OpenDirectoryPath(", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);
        var method = source[methodStart..methodEnd];
        var validation = method.IndexOf("EnsureOwnedProductRoot(productRoot, expectedVolumeGuidPath)", StringComparison.Ordinal);
        var pin = method.IndexOf("DuplicateHandleCore(productRoot", StringComparison.Ordinal);
        Assert.True(validation >= 0 && pin > validation, "The exact validated handle must be duplicated for the session pin.");
        Assert.DoesNotContain("OpenDirectoryCore(volumeRoot, ProductDirectoryName", method, StringComparison.Ordinal);
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
