# StorageChronicle.Benchmarks.csproj

Benchmark project referencing the projection layer, central BenchmarkDotNet version, and ZstdSharp.Port for a real compression measurement. Unsafe compilation is enabled only to support the source-generated `CreateDirectoryW` interop stub used for collision-safe benchmark fixture creation. No benchmark writes durable event history.
