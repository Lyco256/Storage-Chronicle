# WindowsVolumeDirectorySessionFactory.cs

## Role

Provides the production factory for Windows media filesystem sessions. It opens the volume identified by the caller's current `VolumeId`; the session validates the volume through an open handle and refuses to access an existing `.StorageChronicle` directory without a valid ownership marker.

## Public types and responsibilities

- `WindowsVolumeDirectorySessionFactory`: implements `IVolumeBoundMediaFileSystemFactory` and returns an `IVolumeBoundMediaFileSystem`.

## Invariants

- A session is associated with the expected volume GUID and validates opened descendants against that identity.
- The production file API is restricted to paths beneath `.StorageChronicle` and creates files without replacing existing names.
- An existing unmarked media directory is rejected without adoption.

## Inputs and outputs

Accepts the expected `VolumeId` and returns an opened disposable filesystem session. It does not accept mount-point paths or create media directories.

## Dependencies

- `StorageChronicle.Contracts` for the filesystem factory contract.
- `StorageChronicle.Domain.Contracts` for `VolumeId`.
- `WindowsVolumeDirectorySession` for handle-relative Windows I/O.

## Failure behavior

Unsupported platforms, malformed identities, missing volumes, identity mismatch, reparse points, and invalid ownership markers fail closed with exceptions. The factory does not accept a mount-point string as proof of volume identity.

## Threading and lifetime

Each call creates an independent session. The consumer owns and must dispose the returned session; its pinned handles remain live until disposal.

## Relevant tests

- `tests/StorageChronicle.Platform.Windows.FileSystem.Tests/VolumeAndMediaTests.cs` exercises handle-relative behavior in newly created temporary fixtures.
- `tests/StorageChronicle.ExternalMedia.Tests/ExternalMediaTests.cs` exercises ownership refusal and no-overwrite publication through a fixture filesystem adapter.

## OS constraints

Windows only. The factory performs no physical-media validation beyond the volume-bound session's handle checks; role classification and physical acceptance are separate gates.

## Change-sensitive contracts

Do not accept a caller-provided mount-point or boolean as a substitute for opening the expected volume identity. Keep session creation fail-closed when the volume or ownership marker cannot be validated.
