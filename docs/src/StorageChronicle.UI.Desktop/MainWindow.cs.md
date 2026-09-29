# MainWindow.cs

Hosts the Event Stack and Diff View tabs using the Agent named-pipe projection boundary. Event Stack startup, page-size, sort, and saved-filter preferences use the same Agent client and persisted User Settings service. The shell owns no storage or collector logic; disconnected Agents are shown as a status error and never replaced with fabricated history. Headless startup tests verify shell construction.

The Settings action opens `SettingsDialogWindow` modally using the same Agent pipe client. User preferences and machine configuration are edited through Agent IPC, not by the desktop process touching configuration files. The action is constructed alongside the Agent-health status and the Event Stack/Diff View tabs.
