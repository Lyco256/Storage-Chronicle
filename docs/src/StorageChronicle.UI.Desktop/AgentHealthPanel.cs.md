# AgentHealthPanel.cs

`AgentHealthPanel` is the desktop state-monitoring surface. It reads only the bounded Agent health IPC response, displays recording state and per-volume continuity, and lets the user execute or decline a selected pending reconciliation request. Recovery is delegated to the Agent; the UI never invokes a manual filesystem reconciliation API.
