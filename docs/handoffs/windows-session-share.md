# Windows session/share handoff

- 担当要件: `Requirements/14_AGENT_WINDOWS_SESSION_AND_SHARE.md`
- 変更概要: bounded event-driven clipboard intent, session guard, cloud capability detection, deterministic share snapshot diffs.
- 既知の制限: hidden-window and RegNotify/NetShareEnum native adapters remain thin deployment boundaries; no polling is used by the clipboard contract.
- 共有契約変更要求: none.
