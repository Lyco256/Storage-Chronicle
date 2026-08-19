# Program.cs

This console workload performs real file-system mutations inside a disposable, marker-identified TestLab data volume. It supports basic create/write, directory move, directory delete, and MFT-scale create scenarios. It writes only a metadata operation oracle (run ID, operation, relative paths, and sequence); it never reads or hashes workload file contents.

The root must already exist, must not be a volume root, the guest system volume `C:`, Windows, or Program Files. Both marker JSON files with schema `StorageChronicle.TestLabDataMarker.v1`, the requested run ID, approved role, label, and filesystem are required before mutation; the workload never creates a marker on an unmarked path. It also verifies the mounted volume label and filesystem. Directory moves/deletes are recorded as one parent operation, leaving descendant reconstruction to Storage Chronicle.
