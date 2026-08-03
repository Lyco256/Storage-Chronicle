# Test-Coverage.ps1

Builds the architecture, Golden/E2E, and Avalonia headless projects, then runs their emitted Microsoft Testing Platform modules with CodeCoverage and emits Cobertura files under `artifacts/quality/coverage`. The script intentionally does not include test projects outside this branch's ownership paths. The .NET 10 MTP runner must be selected in `global.json` by the integration owner; no fallback or fake coverage is generated.
## Role

Collects Microsoft Testing Platform CodeCoverage from the critical non-privileged test matrix and enforces the requirements thresholds.

## Public types and responsibilities

The script exposes the Debug/Release configuration switch and produces a machine-readable coverage summary.

## Inputs and outputs

Inputs are the critical test assemblies and the repository `global.json`; outputs are timestamped Cobertura reports, a summary JSON file, and the exit code.

## Dependencies

Depends on the .NET 10 SDK and `Microsoft.Testing.Extensions.CodeCoverage` supplied through `Directory.Build.targets`.

## Invariants

Coverage is aggregated by unique source package/file/line with hits unioned across tests; a missing report or missing package fails closed.

## Threading and lifetime

Critical projects run synchronously and their output is joined through the foreground SDK invocation before aggregation; no detached test child is treated as completed.

## Failure behavior

Test failure, missing coverage output, missing global runner, or a threshold violation returns a non-zero exit code.

## Tests

The script exercises the test projects under `tests/` and is called by `build/quality/Test-Quality.ps1`.

## OS constraints

Windows privileged acceptance is intentionally excluded; it is executed through `build/Test-WindowsPrivileged.ps1`.

## Change-sensitive contracts

The 80% Domain/State/Projection/Storage gates and 70% UI ViewModel gate are release quality contracts.
