# StorageChronicle.Architecture.Tests.csproj

xUnit v3 architecture gate project. It uses deterministic project-file checks so the gate also runs on machines without privileged Windows APIs. It references `StorageChronicle.TestDataGenerator` so CLI output-safety behavior can be exercised against isolated temporary fixtures.
