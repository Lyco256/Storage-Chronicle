# WindowsDirectoryChangeMonitorFactory

Supplies the monitor with separate metadata/handle and notification native interfaces. This preserves the P/Invoke boundary and enables deterministic synthetic-notification tests without changing shared contracts.
