# ResourceMonitor/Program.cs

Samples one or more caller-selected process IDs at a bounded interval. On supported Windows 10/11 builds it records the combined `PROCESS_MEMORY_COUNTERS_EX2.PrivateWorkingSetSize`, working set, normalized process CPU percentage, and Windows process-I/O write bytes, plus threshold results and samples as JSON. Queue depth is exposed by the Agent health IPC surface rather than fabricated from thread counts. Passing both the Agent and Session Agent PIDs measures the product-level 50 MiB/0.5% budget without fabricating a missing process.
