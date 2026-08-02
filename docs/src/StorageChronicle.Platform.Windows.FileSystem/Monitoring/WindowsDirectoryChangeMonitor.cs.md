# WindowsDirectoryChangeMonitor

Runs the event-driven ReadDirectoryChangesW loop for one mounted root with subtree notifications enabled, so normal monitoring does not perform periodic full scans. Native overflow (`ERROR_NOTIFY_ENUM_DIR`), malformed buffers, access denial, invalid handles, path loss, and device removal produce `ContinuityGap` with `UnverifiedGap` handling upstream. Cancellation is a normal shutdown path and does not create a false gap.
