using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Abstractions;

/// <summary>Provides a transient process-exit signal without turning it into a durable file event.</summary>
public interface IProcessLifecycleSink
{
    /// <summary>Records the observed exit time for one uniquely identified process instance.</summary>
    void RecordProcessExit(ProcessInstanceId processInstanceId, DateTimeOffset exitedUtc);

    /// <summary>Gets the transient exit time for one process instance, when observed in this Agent lifetime.</summary>
    bool TryGetProcessExit(ProcessInstanceId processInstanceId, out DateTimeOffset exitedUtc);
}
