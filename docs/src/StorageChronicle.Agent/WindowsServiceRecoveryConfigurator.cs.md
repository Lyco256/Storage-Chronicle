# WindowsServiceRecoveryConfigurator.cs

`WindowsServiceRecoveryConfigurator` is the Windows-only service-host boundary that applies the durable Service Control Manager recovery actions after the service has been installed. It configures restart delays of 5, 15, and 60 seconds and leaves the service running when configuration is unavailable, such as a developer console launch or insufficient service-manager permissions.

The implementation uses only `advapi32.dll` service APIs and does not start a shell command. It never touches Storage Chronicle history. The installer provides the initial service registration; this startup step installs the distinct three-stage delays once the service exists.

Tests and validation: the installer manifest test checks service registration and recovery metadata, while the Windows integration suite validates the P/Invoke layout and the service-host startup path without requiring a machine-wide service mutation.
