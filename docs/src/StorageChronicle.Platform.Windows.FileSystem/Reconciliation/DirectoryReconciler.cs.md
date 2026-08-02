# DirectoryReconciler

Performs metadata/path-only reconciliation after an explicit confirmation callback. Declining returns a `ContinuityGap` marked `UserDeclined`; confirmed results contain only added/removed/path-change deltas with `Reconciled` quality and do not invent exact change times or processes. It is a user-triggered recovery path, not a periodic polling loop.
