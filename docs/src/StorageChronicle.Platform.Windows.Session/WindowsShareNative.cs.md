# WindowsShareNative.cs

## Role

Contains the thin native adapters for `NetShareEnum` level 2 and `RegNotifyChangeKeyValue` on the LanmanServer Shares registry key.

## Native boundaries

NetShareEnum maps share name, local path, share type, remark, and permission mask into the shared `ShareDescriptor`. Registry notification uses an asynchronous event handle and re-registers after each notification. No remote enumeration or read-access history is requested.

## Failure and recovery

Access denied and native enumeration errors are preserved for the snapshot caller. Missing registry notification support exposes `IsAvailable=false`, allowing the source to activate the 30-second fallback. Disposal closes the registry key and event handle.

## Tests

The source tests cover injected notifier behavior and fallback quality. Native P/Invoke structure and privileged SMB snapshot behavior belong to the Windows privileged test gate.
