# WindowsEtwFileIoCollector.cs

This file contains the Windows ETW File I/O collector. It runs a TraceEvent kernel session, translates create/write/delete/rename metadata and process attribution into platform-neutral source facts, and retains read/query/directory-enumeration observations only in the bounded transient correlation channel.

The collector has a bounded queue. An ETW queue overflow or session failure is surfaced as a collector failure so the Agent can record an unverified gap and recover through reconciliation. It never reads file contents or content hashes, emits no synthetic descendant event for directory operations, and keeps Windows ETW APIs isolated from the platform-neutral contracts. Tests exercise translation, filtering, cancellation, overflow, and unavailable-ETW failure paths.
