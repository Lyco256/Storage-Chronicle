# Test-Fast.sh

## Role

Runs the fast test matrix on a Unix-like host and explicitly skips Windows-targeted runtime tests outside Windows.

## Public types and responsibilities

The script enumerates `*.Tests.csproj`, excludes the environment-bound privileged project, and propagates each test process result.

## Inputs and outputs

Optional arguments are passed to `dotnet build` and the emitted MTP module test invocation; test results and SDK outputs remain under ignored output locations.

## Dependencies

Requires `dotnet`, the repository test projects, and the host's OS identification.

## Invariants

Windows privileged acceptance is never represented as a portable-host pass; skipped Windows projects are reported explicitly.

## Threading and lifetime

Projects run sequentially so output and failures remain attributable to one project.

## Failure behavior

The first non-zero build or module `dotnet test` result terminates the script with the same failure status. The script uses a repository-relative module path to avoid the .NET 10 MTP absolute-path discovery edge case.

## Tests

Covered by the non-privileged fast lane and its PowerShell counterpart.

## OS constraints

Windows-targeted projects require Windows for runtime acceptance; portable projects can run on future Ubuntu hosts.

## Change-sensitive contracts

Project discovery, privileged exclusion, explicit skip reporting, and exit-code propagation are compatibility-sensitive.
