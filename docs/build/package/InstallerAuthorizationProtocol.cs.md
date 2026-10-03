# InstallerAuthorizationProtocol.cs

## Role and public types

Provides the small protocol primitives shared by the physical installer broker and its non-privileged tests. `InstallerAuthorizationProtocol` creates cryptographic tokens, validates every Hello field against the exact parent-side case/process context, reports the kernel-reported named-pipe client PID, and maps only ShellExecute error 1223 to `NOT_EXECUTED`. `OneTimeNonce` accepts its configured nonce at most once.

## Invariants and dependencies

The source intentionally uses syntax and framework APIs supported by Windows PowerShell 5.1's .NET Framework `Add-Type` compiler. The parent uses `NamedPipeServerStreamAcl.Create` and explicit `PipeSecurity`; pipe ACL creation is separate from this helper. The parent sends only the exact versioned Grant after process-ID and Hello validation. No phrase or nonce is authorized from command-line/environment state alone.

The helper uses `RandomNumberGenerator.Create` for tokens and a Windows `kernel32!GetNamedPipeClientProcessId` P/Invoke for actual client identity. Calling the PID method for a disconnected pipe or on a non-Windows platform fails; callers must treat that as authorization failure. A nonce mismatch consumes that one-use validator, deliberately preventing retry/replay.

## Relevant tests and limits

`InstallerManifestTests` verifies Hello success/mismatch, nonce replay, UAC-cancel mapping, named-pipe name collision behavior, and orchestration ordering. This helper does not validate driver-side server PID, authenticate the external hash-manifest signing process, sandbox MSI effects, or close the MSI path hash-to-use race. UAC and installer execution are not performed by these tests.
