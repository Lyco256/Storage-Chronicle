# ResourceMonitor/Program.cs

Samples a caller-selected process ID at a bounded interval. It records private bytes, working set, normalized process CPU percentage, threshold results, and the samples as JSON. The portable tool does not infer disk writes; host-specific disk accounting remains an explicit acceptance-host responsibility.
