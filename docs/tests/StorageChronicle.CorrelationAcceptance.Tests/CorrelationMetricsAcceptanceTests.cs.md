# CorrelationMetricsAcceptanceTests.cs

This acceptance fixture is deterministic evidence for R-00 sections 8 and 9. It loads `Fixtures/r00-process-explorer-correlation.json`, keeps the complete source-fact input separate, and runs the facts through `AgentPipeline` and `EventNormalizer`. The resulting canonical events are projected through `ProjectionService`; the test checks parent-process links, `Explorer操作`, and displayed process quality without changing a shared contract.

The fixture intentionally includes an ETW read observation and three Clipboard candidates. The Agent pipeline must not append those transient correlation observations to the durable source or canonical lists. A strict Explorer paste is counted as correlated only when the existing Normalizer correlation properties identify the source file. A missing candidate, wrong generation, or non-Explorer create is not counted as a successful Explorer source correlation.

The test also maps a fixed Clipboard source through `ClipboardCandidateMessage` to exercise the Session Agent metadata boundary. No clipboard bytes, file contents, or hashes are used. When `STORAGE_CHRONICLE_CORRELATION_REPORT` is set, the test writes the deterministic measurement JSON for the quality script.

