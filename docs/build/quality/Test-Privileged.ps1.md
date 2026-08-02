# Test-Privileged.ps1

Runs only tests tagged `Category=WindowsPrivileged` from Windows-targeted test projects. It exits successfully without running those tests on non-Windows hosts, and writes per-project xUnit XML results to `artifacts/quality/privileged`.
