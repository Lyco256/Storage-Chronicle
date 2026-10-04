# WindowsMediaAclInspection.cs

## Role

Implements the Windows-only, read-only ACL descriptor reader and deterministic policy evaluator used by `WindowsVolumeDirectorySession.InspectProductAcl`. It does not open a volume or traverse a tree itself; the session supplies already-open handles anchored to the verified volume identity.

## Public types and responsibilities

This file declares no public types. Internal `WindowsMediaAclInspection` reads owner, group, and DACL security descriptor data from an existing handle using `GetSecurityInfo`, releases the returned descriptor with `LocalFree`, and checks the filesystem type through the volume-GUID root. Its nested `Entry` couples a volume-relative name, raw self-relative security descriptor, and object kind for fingerprint construction; `Evaluation` distinguishes incomplete/unsupported evidence from an unsafe but parseable DACL.

## Inputs and outputs

Inputs are an already-open `SafeFileHandle`, the exact approved SID, object kind and path-derived approved-user write mask, or synthetic descriptor bytes for deterministic tests. Outputs are a parse classification, stable finding codes, retained descriptor bytes for the complete tree fingerprint, or an `Unknown` result. This helper never takes an individual file path for native access and never reads file data.

## Policy invariants

- Only NTFS is reported as inspectable. Invalid or well-known broad approved-user SIDs are rejected.
- The inspected volume-root parent and `.StorageChronicle` root may not grant the approved user any write-like access. Path scope further narrows grants: only writer-directory creation at `.StorageChronicle\writers`; only file creation one level below a writer directory; and append-only on recognized segment/manifest files, including both bare-GUID segment temps and `manifest-{A|B}-{guid}.tmp` files. Ownership markers, unknown names, and deeper directories receive no approved-user write allowance. Inherit-only ACEs are checked on descendant objects where they apply; inherited append permission reaching `.writer-owner.json` is rejected.
- SYSTEM and Builtin Administrators are accepted as trusted product principals. All other allow ACEs with write, append, delete, `DELETE_CHILD`, `WRITE_DAC`, `WRITE_OWNER`, system-security, maximum-allowed, or generic write-like rights are unsafe.
- Null DACL is unsafe. Missing DACL, absent owner, malformed descriptors, callback/object/custom/other unsupported ACE forms or flags, and unsupported access-mask bits are Unknown. Deny ACEs do not grant rights; descriptors are nevertheless fingerprinted in full.
- An owner implicitly has `WRITE_DAC` unless an applicable `OWNER RIGHTS` ACE exists. The owner is therefore unsafe unless trusted or explicitly constrained; any explicit owner-rights write grant is evaluated like any other unapproved principal.
- Fingerprints are SHA-256 over a version tag, inspected entry counts, relative UTF-16 names, object kinds, and security descriptor bytes. File contents and file-content hashes are never read or included.

## Dependencies and failure behavior

Uses `SafeFileHandle`, Windows `GetSecurityInfo`/`LocalFree`/`GetVolumeInformationW`, and `RawSecurityDescriptor`. Native access errors, unsupported filesystems, and parse uncertainty fail closed; the caller returns `Unknown` without a partial fingerprint. A parseable prohibited grant yields `Unsafe` plus a stable finding.

## Threading and lifetime

This stateless helper owns no persistent handles or mutable state. The passed safe handle remains owned by the caller; the native security descriptor allocation is always released with `LocalFree`.

## OS constraints

Native descriptor and filesystem calls are supported only on Windows; ACL inspection currently accepts NTFS only. Pure descriptor evaluation and fingerprint unit tests use managed synthetic inputs and need no live volume.

## Tests

`tests/StorageChronicle.Platform.Windows.FileSystem.Tests/WindowsMediaAclPolicyTests.cs` builds synthetic self-relative descriptors and tests safe trusted/narrow grants, parent/path-scope denial, broad and excessive grants, inherit-only ACE behavior, owner implicit rights, null/missing DACLs, malformed descriptors, unsupported ACE forms, SID validation, and stable name/count-sensitive fingerprints. These tests do not open a volume, create fixture files, or change ACLs.

## Evidence limits

The supplied SID is syntax-validated and compared exactly, but is not independently authenticated as the current interactive user; that identity binding belongs to the consent caller. The result describes explicit ACE policy and the modeled owner `WRITE_DAC` rule; it is not Windows `AccessCheck`/effective-access evaluation for every token, group, deny ordering, privilege, conditional ACE, object-specific inheritance, or remote policy source. Unsupported ACEs are Unknown to reduce this gap. Handles pin the objects observed during traversal, but concurrent actors may still change ACLs, parent contents, or access between inspection and later media operations; a hash is evidence of the inspected descriptors, not an atomic tree snapshot, a hostile-race guarantee, or proof of future write authorization.

## Change-sensitive contracts

Do not broaden the approved-user masks, skip the volume-root parent, resolve reparse points, treat Unknown as Verified, or include file contents in the fingerprint. Any change to the generic-right mapping, trusted SID set, owner-rights rule, descriptor security-information mask, or fingerprint version requires corresponding tests and mirrored documentation.
