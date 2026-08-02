# Windows NTFS handoff

- 担当要件: `Requirements/13_AGENT_WINDOWS_NTFS_COLLECTOR.md`
- 変更概要: documented USN V2 parser, continuity-preserving reader boundary, Windows 10 capability detection, gap-quality source events.
- 既知の制限: privileged FSCTL calls are supplied by the deployment/native adapter; the parser and collector contract are complete and testable without a driver.
- 共有契約変更要求: none.
