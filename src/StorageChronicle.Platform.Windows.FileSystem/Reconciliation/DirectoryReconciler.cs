using StorageChronicle.Domain.Contracts;

namespace StorageChronicle.Platform.Windows.FileSystem.Reconciliation;

/// <summary>Describes one metadata-only directory snapshot used for user-confirmed reconciliation.</summary>
public sealed record DirectoryReconciliationEntry(FileId? FileId, string RelativePath, FileKind Kind);

/// <summary>Runs directory reconciliation only after an explicit user confirmation.</summary>
public sealed class DirectoryReconciler
{
    /// <summary>Compares two directory snapshots and returns only path/identity differences.</summary>
    public async ValueTask<DirectoryReconciliationResult> ReconcileAsync(
        VolumeId volumeId,
        IReadOnlyCollection<DirectoryReconciliationEntry> before,
        IReadOnlyCollection<DirectoryReconciliationEntry> after,
        Func<CancellationToken, ValueTask<bool>> confirm,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(confirm);
        var confirmed = await confirm(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!confirmed)
        {
            var now = DateTimeOffset.UtcNow;
            return new DirectoryReconciliationResult(Array.Empty<DirectoryReconciliationDelta>(), new ContinuityGap(volumeId, now, now, "Directory reconciliation was declined by the user", true), false);
        }

        var beforeByIdentity = before.ToDictionary(Identity, StringComparer.OrdinalIgnoreCase);
        var afterByIdentity = after.ToDictionary(Identity, StringComparer.OrdinalIgnoreCase);
        var changes = new List<DirectoryReconciliationDelta>();
        foreach (var (identity, oldEntry) in beforeByIdentity)
        {
            if (!afterByIdentity.TryGetValue(identity, out var newEntry))
            {
                changes.Add(new DirectoryReconciliationDelta(oldEntry.FileId, oldEntry.RelativePath, null, DirectoryChangeKind.Removed, EventQuality.Reconciled));
                continue;
            }

            if (!string.Equals(oldEntry.RelativePath, newEntry.RelativePath, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new DirectoryReconciliationDelta(newEntry.FileId ?? oldEntry.FileId, oldEntry.RelativePath, newEntry.RelativePath, DirectoryChangeKind.RenamedNewName, EventQuality.Reconciled));
            }
        }

        foreach (var (identity, newEntry) in afterByIdentity)
        {
            if (!beforeByIdentity.ContainsKey(identity))
            {
                changes.Add(new DirectoryReconciliationDelta(newEntry.FileId, null, newEntry.RelativePath, DirectoryChangeKind.Added, EventQuality.Reconciled));
            }
        }

        return new DirectoryReconciliationResult(changes, null, true);
    }

    private static string Identity(DirectoryReconciliationEntry entry) => entry.FileId?.Value ?? "path:" + entry.RelativePath;
}
