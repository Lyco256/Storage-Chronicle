# Test-CorrelationMetrics.ps1

The script builds the acceptance project and runs its emitted Microsoft Testing Platform module so a project-level .NET 10 `dotnet test` zero-test path cannot hide the fixture result.

Runs the dedicated R-00 sections 8–9 acceptance fixture. The test sends fixed, metadata-only source facts through the Agent pipeline, Normalizer, Session Agent clipboard DTO, and Projection. It prints and writes deterministic process attribution counts for `Exact`, `Correlated`, and `Unknown`, plus the Explorer source-correlation numerator, denominator, and rate.

The fixture contains no file contents or content hashes. Clipboard observations and ETW read observations are retained as test input only; the test verifies that they do not become durable file-history rows. Projection assertions verify that source facts, correlation properties, and display labels remain separate.

## Usage

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build\quality\Test-CorrelationMetrics.ps1 -FixtureOnly
```

`-FixtureOnly` is the only mode that may exit zero. It proves the deterministic fixture result but makes no live-machine claim. Without `-FixtureOnly`, the script runs the fixture, records its result, then marks the live Agent/interactive Session Agent measurement `NOT_EXECUTED` and exits `2`. This prevents an unexecuted machine measurement from being reported as a pass.

`-FixturePath` overrides the fixture path and `-OutputPath` selects the JSON report path. The script fails closed with exit `2` when the project, fixture, or report is missing; a test failure or a report that differs from the golden expectations exits nonzero as a failure.

## Expected deterministic output

- Process attribution: `Exact=1`, `Correlated=1`, `Unknown=1` out of `3`; each rate is `0.3333`.
- Explorer candidates: `3`; strict source-correlated events: `1`; uncorrelated events: `2`; rate `0.3333`.
- One non-Explorer application create is explicitly excluded and must not be counted as Explorer correlation.

The JSON report and test log are generated below `artifacts/quality/correlation` and are not source-controlled.
