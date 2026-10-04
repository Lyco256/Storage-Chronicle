# Handoff: `feat/windows-media-acl`

## Scope and ownership

Implemented the Requirement 08 Windows filesystem portion of handle-bound product-media ACL inspection. Changes are limited to `src/StorageChronicle.Platform.Windows.FileSystem/**`, `tests/StorageChronicle.Platform.Windows.FileSystem.Tests/**`, matching `docs/src/StorageChronicle.Platform.Windows.FileSystem/**` mirrors, and this handoff. The shared contract was already present at branch base `e338a552`; it was not changed here.

## Changes

- Implemented `IVolumeBoundMediaFileSystem.InspectProductAcl` in the Windows volume session using the verified volume identity, a read-control handle to the volume-root parent, the session-pinned owned `.StorageChronicle` root, and handle-relative descendant traversal.
- The scanner reads owner/group/DACL descriptors only; it does not read file contents or alter security. It supports NTFS only. Reparse entries are opened without following them, inspected as entries, and reported unsafe; traversal never enters their targets.
- It rejects broad/unapproved write grants, volume-root and product-root write grants to the approved user, parent `DELETE_CHILD`, excessive approved-user rights, unconstrained non-service owners, and null DACLs. The approved SID can only create writer directories below `.StorageChronicle\writers`, create files one level below a writer directory, and append to recognized segment/manifest files. The filename policy matches both segment GUID temps and the actual `manifest-A/B-{generation}.tmp` publication temps; inherited append permission on `.writer-owner.json` is rejected. Unsupported/inaccessible/incomplete evidence is `Unknown`; parseable prohibited grants are `Unsafe`.
- A complete result fingerprints raw security descriptors, relative names, object kinds, and directory/file counts using SHA-256. The hash is not a file-content hash.
- Added synthetic descriptor tests for safe trusted/narrow grants, volume-root and path-scope user-write rejection, broad/unapproved SID writes, existing-file data writes, real manifest/segment temp naming, writer-marker inherited append rejection, inherit-only ACE behavior, excessive rights, implicit owner `WRITE_DAC`, constrained owner rights, null/missing DACLs, malformed descriptors, unsupported ACE/flag/mask forms, SID validation, and fingerprint stability/counts.
- No Requirements/shared-contract/Agent/settings code, product ACL, physical volume, physical media, VHDX, service, installer, or UAC action was changed or run.

## Validation

Executed in this worktree:

```text
dotnet build tests/StorageChronicle.Platform.Windows.FileSystem.Tests/StorageChronicle.Platform.Windows.FileSystem.Tests.csproj --no-restore
tests/StorageChronicle.Platform.Windows.FileSystem.Tests/bin/Debug/net10.0-windows10.0.19041.0/StorageChronicle.Platform.Windows.FileSystem.Tests.exe --filter-class StorageChronicle.Platform.Windows.FileSystem.Tests.WindowsMediaAclPolicyTests --progress off
build/quality/Test-DocMirror.ps1
```

- Targeted Windows filesystem build: passed, 0 warnings, 0 errors.
- Synthetic ACL policy tests: passed, 22/22 after the manifest-temp matcher and marker inheritance coverage.
- DocMirror: passed.
- The first DocMirror attempt was blocked by missing ignored NuGet assets for its validator; after `dotnet restore tools/StorageChronicle.DocMirrorValidator/StorageChronicle.DocMirrorValidator.csproj`, the required script passed.
- No full suite, privileged acceptance, or live-volume scanner execution was performed, as required by the task scope.

## Evidence limits and known follow-up

- The supplied SID is syntax-validated and compared exactly, but this method does not independently authenticate it as the current interactive user; that identity binding remains the consent caller's responsibility. This evaluates the inspected descriptor policy; it is not Windows `AccessCheck` or a complete effective-access computation over every token/group membership, privilege, conditional ACE, object-specific ACE, or remote policy source. Unsupported ACE forms return `Unknown`.
- `ExternalMediaStore` creates `.writer-owner.json` through its create-only filesystem API while setting up each writer folder. This scanner intentionally grants that marker no approved-user append allowance: if an inheritable history ACE also reaches the marker, inspection is `Unsafe`; no ACL rewrite is attempted to make it pass.
- Handles bind operations to the objects observed during the sequential walk, but the scan is not an atomic tree snapshot. Concurrent actors may alter ACLs or directory contents during/after inspection; the fingerprint cannot prevent hostile races, guarantee future write authorization, or serve as a runtime security boundary.
- Synthetic tests validate deterministic descriptor policy only. Windows native `GetSecurityInfo` behavior and traversal on an isolated NTFS fixture remain untested here. No user's normal media or volume was scanned.

## Commit policy

Commit this work locally on `feat/windows-media-acl` only. No merge or push is part of this handoff.
