# InstallerManifestTests.cs

Checks the MSI source contract for one product, LocalSystem Agent registration, service recovery configuration, and absence of a filesystem driver. It also statically verifies that the Hyper-V installer driver uses explicit VM credentials and PowerShell Direct transfer, and that the host orchestrator requires the clean baseline, explicit `-Apply`, and fail-closed acceptance eligibility.
