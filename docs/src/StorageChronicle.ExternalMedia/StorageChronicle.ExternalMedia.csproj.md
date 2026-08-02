# StorageChronicle.ExternalMedia.csproj

Builds the platform-neutral external-media module for `net10.0`. It references only Domain, Contracts, and `System.IO.Hashing`; Windows collector APIs are intentionally absent. The project treats compiler and nullable warnings as errors through repository-wide settings. Its focused build and test commands are recorded in `docs/handoffs/external-media.md`.
