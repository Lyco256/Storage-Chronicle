# Test-Ui.sh

## Role

Builds every UI test project on a Unix-like host and runs its emitted Microsoft Testing Platform module through `dotnet test`, matching the PowerShell entry point.

## Inputs and outputs

Additional arguments are forwarded to each discovered test project.

## Failure behavior

The first failing UI test stops the script with its exit code.

## Tests

Headless Event Stack, Diff View, settings, and desktop shell tests are the relevant consumers.
