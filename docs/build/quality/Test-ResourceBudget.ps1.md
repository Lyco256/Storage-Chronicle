# Test-ResourceBudget.ps1

Invokes the resource monitor for one or more existing process IDs (`-ProcessId 1234,5678`). It records bounded private working-set, working-set, CPU, and Windows process-I/O write samples under `artifacts/quality/resources`, applies the documented 50 MiB and 0.5% defaults, and returns nonzero on an exceeded budget. Queue depth is obtained from the Agent health IPC surface using cancellable asynchronous named-pipe I/O; unsupported stream timeout properties are not used, and the script never invents a measurement or treats an absent process as a pass.
