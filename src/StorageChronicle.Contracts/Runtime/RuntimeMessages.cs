using System.Text.Json;
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

/// <summary>Requests a diff projection over a bounded time range.</summary>
public sealed record DiffProjectionRequest(DateTimeOffset? FromUtc, DateTimeOffset ToUtc, DiffMode Mode);

/// <summary>Returns one bounded Event Stack page.</summary>
public sealed record ProjectionPageResponse(
    IReadOnlyList<EventStackRow> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasMore);

/// <summary>Returns one bounded Diff View page.</summary>
public sealed record DiffProjectionResponse(IReadOnlyList<DiffEntry> Items);

/// <summary>Requests the current non-user-editable agent state.</summary>
public sealed record AgentHealthRequest;

/// <summary>Requests a persisted settings snapshot through the Agent.</summary>
public sealed record SettingsSnapshotRequest;

/// <summary>Identifies a settings scope in an IPC request.</summary>
public enum SettingsScope { Machine, User }

/// <summary>Requests a validated settings update. The Agent maps this DTO to its internal settings model.</summary>
public sealed record SettingsUpdateRequest(SettingsScope Scope, JsonElement Settings);

/// <summary>Returns both settings scopes and recovery warnings without exposing machine paths through UI file I/O.</summary>
public sealed record SettingsSnapshot(JsonElement Machine, JsonElement User, string? MachineWarning, string? UserWarning);
