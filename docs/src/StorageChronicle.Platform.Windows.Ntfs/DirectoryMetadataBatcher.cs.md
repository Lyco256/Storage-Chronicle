# DirectoryMetadataBatcher.cs

`DirectoryMetadataBatcher` is the bounded handoff from lightweight public MFT enumeration to standard metadata acquisition. It accepts only reconciliation-selected `MftEntry` candidates, groups each bounded batch by parent directory, and delegates metadata reads to `IDirectoryMetadataProvider`. The provider contract returns `FileMetadata` and explicitly has no content or content-hash operation. The batcher streams results, honors cancellation, and never retains the complete MFT.
