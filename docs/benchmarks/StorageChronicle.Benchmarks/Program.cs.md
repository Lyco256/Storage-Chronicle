# Benchmarks/Program.cs

Runs opt-in BenchmarkDotNet measurements for one-million MFT-like identities and one-hundred-thousand grouped/Event Stack/diff/append/Zstandard/SQLite/media workloads. The fixture is deterministic and metadata-only; it is not a substitute for release hardware acceptance measurements. `MemoryDiagnoser` is enabled and JSON/Markdown output is produced by `build/quality/Test-Performance.ps1`.
