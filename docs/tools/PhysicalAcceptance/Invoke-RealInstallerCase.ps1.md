# Invoke-RealInstallerCase.ps1

Real local Windows driver used by `build/package/Test-Installer.ps1`. It invokes `msiexec`, inspects product registration, service LocalSystem/automatic state, service recovery output, self-contained files, Session Agent startup, non-admin UI launch, ACL-denied write behavior, update/rollback, uninstall, and history retention. Each passing assertion points to an evidence file; an exit code alone cannot pass a case.

The driver requires the process to be elevated and requires the inherited marked acceptance-root environment variable. It creates only empty acceptance markers and temporary ACL probe directories; it does not read or hash product/user file contents and does not remove ProgramData history. Tests: parser validation; execution is restricted to a user-approved disposable physical target.
