# StorageChronicle.wxs

Defines one MSI feature containing UI, Agent, and Session Agent. The Agent is LocalSystem with restart recovery enabled by MSI; the Agent host applies the distinct 5/15/60-second delays through the Windows Service Control Manager after installation. Session Agent starts at user logon, standard data directories are created below CommonAppDataFolder, and history/config components are permanent so uninstall never deletes history. No filesystem driver is installed.
