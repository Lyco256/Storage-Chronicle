# WindowsPinnedOutputDirectory.cs

## Role

Pins a caller-selected output file's directory chain before the TestDataGenerator creates a new fixture output. This tool-specific Windows guard is separate from product collection and does not itself authorize an evidence root.

## Public and internal types

`WindowsPinnedOutputDirectory` is internal and disposable. `OpenForFile` accepts a fully qualified output path and keeps handles open from the volume root through the output parent until disposal.

## Invariants and dependencies

- File output is restricted to Windows, a local fixed drive-letter volume, and NTFS.
- Each directory handle requests list/read-attributes/synchronize access, allows read/write sharing, and omits delete sharing so the directory cannot be renamed or removed while the guard is held.
- Directories are opened with `FILE_FLAG_OPEN_REPARSE_POINT`; each handle must describe an ordinary directory and its final volume-GUID path must match the expected child of the preceding handle.
- The final target is created with `NtCreateFile(FILE_CREATE)` relative to the pinned parent handle and opened write-only; an existing name is never replaced, and a drive-letter/path re-resolution cannot redirect output. Parent creation is not attempted.
- Native errors, unsupported platforms/filesystems, path mismatch, or reparse directories fail closed; handles acquired before failure are disposed in reverse order.

## Dependencies and failure behavior

Uses Windows `CreateFileW`, `NtCreateFile`, `RtlNtStatusToDosError`, `GetFileInformationByHandleEx`, `GetFinalPathNameByHandleW`, `GetDriveTypeW`, and `GetVolumeInformationW`, plus `SafeFileHandle`. `Program.Main` maps I/O, unsupported-platform, path, access, and Win32 failures to exit code 1. Tests in `TestDataGeneratorOutputSafetyTests` verify a pinned fixture directory cannot be renamed until disposal, while success/collision tests exercise handle-relative creation. No product data is read or changed.
