using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace StorageChronicle.Agent.Tests;

/// <summary>Serializes tests that share the production Agent named-pipe name.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "xUnit collection-definition types intentionally use the Collection suffix.")]
public sealed class AgentPipeServerCollection
{
    /// <summary>The xUnit collection shared by all production named-pipe fixtures.</summary>
    public const string Name = "Agent named-pipe server";
}
