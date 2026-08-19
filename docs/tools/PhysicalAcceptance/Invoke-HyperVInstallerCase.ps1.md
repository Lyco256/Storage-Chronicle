# Invoke-HyperVInstallerCase.ps1

Runs one real installer acceptance case inside an approved `SC-Test-W11` or `SC-Test-W10` guest through PowerShell Direct. It imports a host DPAPI-protected guest credential, transfers only the MSI payload and driver scripts, requires the guest TestLab marker through the guest physical-case driver, collects guest evidence back into the host case directory, and rewrites evidence paths to host-visible paths. It never treats a VM result as physical-machine acceptance.

## Role

Hyper-V guest driver for the generic eleven-case installer harness.

## Public parameters and responsibilities

The parameters mirror `build/package/Test-Installer.ps1`, with `GuestCredentialReference` for host-side PowerShell Direct authentication and `GuestTestDataRoot` for the marked disposable data volume. The driver owns guest transfer and result collection; the guest `Invoke-RealInstallerCase.ps1` owns MSI/service/session/ACL assertions.

## Invariants

Only the approved TestLab VM is accepted. Guest and host evidence are kept separate, result target identity is forced to `HyperVVm`/`VM`, no host source tree is shared, and missing credentials, VM state, guest result, or evidence fail closed.

## Failure behavior

Missing Hyper-V, non-running VM, invalid credential reference, missing MSI, guest marker, guest case result, or evidence returns `FAILED` and a host log; no pass is synthesized from an exit code.

## Tests and validation

PowerShell parser validation and the generic installer harness precondition tests run on the development host. Actual execution requires the approved Windows 10/11 TestLab VMs and is an environment acceptance gate.
