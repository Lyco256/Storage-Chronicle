# Installer and packaging handoff

The WiX Toolset SDK 6.0.2 project defines one x64 per-machine product containing UI, Agent Service, and Session Agent. The Agent uses LocalSystem and recovery actions; history is not removed by uninstall. `build/package/Build-Installer.ps1` publishes self-contained win-x64 outputs and builds the MSI. Installer manifest tests enforce the no-driver and recovery contract.
