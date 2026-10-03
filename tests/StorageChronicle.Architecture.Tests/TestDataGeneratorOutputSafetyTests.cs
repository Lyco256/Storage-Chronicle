using StorageChronicle.TestDataGenerator;
using Xunit;

namespace StorageChronicle.Architecture.Tests;

/// <summary>Verifies that generated fixtures are new-only and path validation fails closed.</summary>
public sealed class TestDataGeneratorOutputSafetyTests
{
    [Fact]
    public void OutputCreatesNewFileInExistingParent()
    {
        using var fixture = new TemporaryFixture();
        var output = System.IO.Path.Combine(fixture.Path, "generated.ndjson");

        var result = Program.Main(["--count", "1", "--output", output]);

        Assert.Equal(0, result);
        Assert.True(File.Exists(output));
        Assert.Contains("DataWrite", File.ReadAllText(output), StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingOutputIsRejectedWithoutChangingItsContents()
    {
        using var fixture = new TemporaryFixture();
        var output = System.IO.Path.Combine(fixture.Path, "existing.json");
        const string original = "user-owned sentinel";
        File.WriteAllText(output, original);

        var result = Program.Main(["--count", "1", "--output", output]);

        Assert.Equal(1, result);
        Assert.Equal(original, File.ReadAllText(output));
    }

    [Fact]
    public void MissingParentIsRejectedWithoutCreatingDirectories()
    {
        using var fixture = new TemporaryFixture();
        var missingParent = System.IO.Path.Combine(fixture.Path, "not-created");
        var output = System.IO.Path.Combine(missingParent, "generated.json");

        var result = Program.Main(["--count", "1", "--output", output]);

        Assert.Equal(1, result);
        Assert.False(Directory.Exists(missingParent));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void TraversalPathIsRejectedWithoutCreatingOutput()
    {
        using var fixture = new TemporaryFixture();
        var traversal = System.IO.Path.Combine(fixture.Path, "..", "escaped.json");

        var result = Program.Main(["--count", "1", "--output", traversal]);

        Assert.Equal(1, result);
    }

    [Fact]
    public void ReparsePointParentIsRejected()
    {
        using var fixture = new TemporaryFixture();
        var targetDirectory = Directory.CreateDirectory(System.IO.Path.Combine(fixture.Path, "target")).FullName;
        var linkedDirectory = System.IO.Path.Combine(fixture.Path, "linked");
        try
        {
            Directory.CreateSymbolicLink(linkedDirectory, targetDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        var output = System.IO.Path.Combine(linkedDirectory, "generated.json");
        var result = Program.Main(["--count", "1", "--output", output]);

        Assert.Equal(1, result);
        Assert.False(File.Exists(System.IO.Path.Combine(targetDirectory, "generated.json")));
    }

    private sealed class TemporaryFixture : IDisposable
    {
        private readonly string _path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"StorageChronicle-TestDataGenerator-{Guid.NewGuid():N}");

        public TemporaryFixture() => Directory.CreateDirectory(_path);

        public string Path => _path;

        public void Dispose()
        {
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
        }
    }
}
