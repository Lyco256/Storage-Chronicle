# Remove-TestDataVhdx.ps1

This legacy VirtualBox VDI cleanup helper is retired under Requirement 37. It always exits with status 2 and performs no host, VM, disk, file, or evidence operations, regardless of `-Apply`. Parameters remain only so old callers receive a clear fail-closed result. Do not use this helper for cleanup. Preserve ambiguous test data; a future file-backed VHDX cleanup must be separately audited and prove run ownership and exact disk/volume identity immediately before any mutation.
