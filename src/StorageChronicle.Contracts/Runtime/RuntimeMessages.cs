using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Contracts.Runtime;

/// <summary>Requests a page of a UI projection.</summary>
public sealed record ProjectionPageRequest(EventStackMode Mode, int Page, int PageSize, string? Filter);

/// <summary>Reports a pending reconciliation decision to the UI.</summary>
public sealed record ReconciliationDecision(string RequestId, bool Execute);

/// <summary>Reports non-user-editable agent health state.</summary>
public sealed record AgentHealth(string State, string? Reason, long LastSequence, IReadOnlyList<VolumeHealth> Volumes);

/// <summary>Reports continuity for one volume.</summary>
public sealed record VolumeHealth(VolumeId VolumeId, MonitoringContinuity Continuity, string? GapReason);
