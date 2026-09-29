# Test-Architecture.ps1

Builds the architecture dependency slice and runs the emitted ArchUnitNET/xUnit v3 Microsoft Testing Platform module. The XML result is written below `artifacts/quality/architecture`; direct module invocation keeps the gate usable when .NET 10's MTP `global.json` opt-in is owned by the integration branch.
