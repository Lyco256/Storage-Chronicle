# Test-Coverage.ps1

Runs the architecture, Golden/E2E, and Avalonia headless projects with Microsoft Testing Platform CodeCoverage and emits Cobertura files under `artifacts/quality/coverage`. The script intentionally does not include test projects outside this branch's ownership paths. The .NET 10 MTP runner must be selected in `global.json` by the integration owner; no fallback or fake coverage is generated.
