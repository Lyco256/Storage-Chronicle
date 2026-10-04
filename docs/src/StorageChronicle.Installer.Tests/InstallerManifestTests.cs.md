# InstallerManifestTests.cs

Checks the MSI source contract for one product, LocalSystem Agent registration, service recovery configuration, and absence of a filesystem driver. It statically verifies legacy VirtualBox installer and Windows 10 Stage A entry points are retired no-I/O stubs, confirms physical-only installer gates, and checks that privileged acceptance forwards real evidence inputs without accepting guest-derived Stage A manifests.
