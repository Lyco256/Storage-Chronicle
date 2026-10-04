# InstallerAuthorizationProtocol.cs

## Role and public types

Provides protocol and safety primitives shared by the physical installer broker and its non-privileged tests. `InstallerAuthorizationProtocol` creates cryptographic tokens, validates every Hello field against the exact parent-side case/process context, reports the kernel-reported named-pipe client PID, maps only ShellExecute error 1223 to `NOT_EXECUTED`, and compares registry value names using Windows' case-insensitive name semantics. `OneTimeNonce` accepts its configured nonce at most once. `VerifiedPayloadLock` opens the payload and every ancestor directory by handle, rejects reparse points, denies delete sharing on the directory chain and write/delete sharing on the payload, hashes the payload through that same handle, and retains these handles until disposal. `InstallerChildCompletionState` distinguishes a bounded wait timeout from terminal proof and keeps the timed-out case indeterminate after the exact process handle signals.

## Invariants and dependencies

The source intentionally uses syntax and framework APIs supported by Windows PowerShell 5.1's .NET Framework `Add-Type` compiler. The parent uses `NamedPipeServerStreamAcl.Create` and explicit `PipeSecurity`; pipe ACL creation is separate from this helper. The parent sends only the exact versioned Grant after process-ID and Hello validation. No phrase or nonce is authorized from command-line/environment state alone.

The helper uses `RandomNumberGenerator.Create` and Windows `kernel32` APIs for named-pipe client identity and handle-bound file/directory inspection. Calling the PID method for a disconnected pipe or on a non-Windows platform fails; callers must treat that as authorization failure. A nonce mismatch consumes that one-use validator, deliberately preventing retry/replay. The held payload stream shares read access only; ancestor directory handles omit delete sharing. Callers must keep the returned object alive while any child may reopen the path and must wait for terminal child state before disposal. These locks do not survive termination of the parent process and are not a substitute for an ACL-protected staging directory against forced harness termination.

## Relevant tests and limits

`InstallerManifestTests` verifies Hello success/mismatch, nonce replay, UAC-cancel mapping, named-pipe name collision behavior, payload write/file-rename/ancestor-directory-rename denial while handles are held, expected-hash corruption rejection, registry-name collision comparison, timeout recovery state, and orchestration contracts. The physical harness also parses the approved driver timeout function and refuses UAC if it contains a direct `$process.Kill()` or lacks both bounded and terminal waits. This helper does not validate driver-side server PID, authenticate the external hash-manifest signing process, or sandbox MSI effects. UAC and installer execution are not performed by these tests.
