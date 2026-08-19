# Probe-WriteAccess.ps1

Minimal non-admin ACL probe used only by the physical installer acceptance driver. It attempts to create and remove a temporary file under an explicitly supplied acceptance probe directory and returns success only when Windows reports `UnauthorizedAccessException`. It never targets a volume root or user data by itself.

Tests: parser validation; the real permission result is recorded by the installer driver.
