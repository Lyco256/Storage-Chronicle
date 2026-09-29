# DiffProjectionPanel.cs

The desktop Diff View is backed by the Agent's rich Tree/Explorer IPC projection. `AgentDiffProjectionSource` maps bounded wire snapshots to the common `DiffProjection` model, preserving virtual/deleted/unknown-location rows, semantic gutter state, rename history, replay points, and Explorer-open eligibility. The panel exposes Live, Period, Point-in-Time, and Replay mode controls and reports connection failures without touching the file system.
