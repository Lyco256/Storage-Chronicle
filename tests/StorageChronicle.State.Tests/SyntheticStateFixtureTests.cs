using Xunit;

namespace StorageChronicle.State.Tests;

/// <summary>Lazy one-million-node input used by performance harnesses without retaining a complete event list.</summary>
public sealed class SyntheticStateFixture
{
    /// <summary>Number of synthetic nodes represented by this fixture.</summary>
    public const int NodeCount = 1_000_000;

    /// <summary>Enumerates deterministic node numbers on demand.</summary>
    public IEnumerable<int> EnumerateNodeNumbers()
    {
        for (var index = 0; index < NodeCount; index++)
        {
            yield return index;
        }
    }
}

public sealed class SyntheticStateFixtureTests
{
    private static readonly int[] FirstNodes = [0, 1, 2];

    [Fact]
    public void MillionNodeFixtureIsLazyAndDeterministic()
    {
        var fixture = new SyntheticStateFixture();

        Assert.Equal(SyntheticStateFixture.NodeCount, fixture.EnumerateNodeNumbers().Count());
        Assert.Equal(FirstNodes, fixture.EnumerateNodeNumbers().Take(3));
    }
}
