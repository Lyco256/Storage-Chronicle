# Program.cs

This console workload performs real file-system mutations inside a disposable, marker-identified TestLab data volume. It supports basic create/write, directory move, directory delete, and MFT-scale create scenarios. It writes only a metadata operation oracle (run ID, operation, relative paths, and sequence); it never reads or hashes workload file contents.

The root must not be a volume root, the guest system volume `C:`, Windows, or Program Files. A marker JSON with schema `StorageChronicle.TestLabDataMarker.v1` and the requested run ID is required or created before mutation. The workload maintains both the execution marker `.storage-chronicle-testlab-marker.json` and the TestLab volume marker `StorageChronicleTestVolume.json`; existing mismatched markers fail closed. Directory moves/deletes are recorded as one parent operation, leaving descendant reconstruction to Storage Chronicle.
