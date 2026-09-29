# Test-Integration.ps1

Runs the integration, Golden/E2E, and Avalonia headless suites as separate xUnit v3 executables and writes XML results under `artifacts/quality/integration`. It does not collapse privileged Windows tests into the default gate.
