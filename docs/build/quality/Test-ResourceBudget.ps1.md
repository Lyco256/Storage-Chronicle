# Test-ResourceBudget.ps1

Invokes the resource monitor for an existing process ID. It records bounded private-memory and normalized CPU samples under `artifacts/quality/resources`, applies the documented 50 MiB and 0.5% defaults, and returns nonzero on an exceeded budget. It never invents a measurement or treats an absent process as a pass.
