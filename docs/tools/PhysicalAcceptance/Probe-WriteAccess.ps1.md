# Probe-WriteAccess.ps1

## Role and contract

Minimal write-denial probe invoked by the physical installer permission case. It tries to create one GUID-named file under the exact supplied, run-owned fixture directory using `FileMode.CreateNew`.

Exit code 0 means the OS reported access denied (`UnauthorizedAccessException`, `SecurityException`, or Win32 error 5); 1 means the write succeeded and the permission test failed; 2 means another error prevented a trustworthy conclusion. It deletes only the exact file successfully created by this process, and reports cleanup failure as 2. It never targets a volume root or discovers/chooses a path on its own.

## Dependencies, failure, tests

Depends on Windows ACL behavior and an explicit fixture path supplied by the installer driver. Parser and static behavior are checked with the installer tests; real ACL verification remains unexecuted and must be included in the independent process-attributed runtime audit.
