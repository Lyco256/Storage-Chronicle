# Program.cs

Creates the LocalSystem-compatible Windows Service host. It composes durable append storage, settings persistence/history, projection IPC, NTFS USN/MFT, scoped directory monitoring, session clipboard, and share collectors, and applies the service recovery action delays at startup. NTFS is delegated away from the directory collector only for whole-volume scopes; configured NTFS subdirectories use the scoped directory collector to avoid leaking unmonitored events. File contents and clipboard contents are never retained.
