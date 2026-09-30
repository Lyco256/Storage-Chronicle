# Windows 10 compatibility

The Windows projects target `net10.0-windows10.0.19041.0`, use APIs behind the Windows 10 boundary, and keep USN, filesystem notification, session, and cloud capabilities behind platform projects. The capability suite runs without privileged hardware.

The Windows 10 22H2 x64 acceptance run must execute the privileged test script and installer matrix on that physical OS. `Finalize-Windows10PhysicalAcceptance.ps1` and the final aggregate require the elevated Release privileged manifest from that exact Windows 10 22H2 x64 host, the physical preflight, and all eleven physical installer cases; no VirtualBox/guest Stage A artifact is accepted. Until that run is attached, compatibility is recorded as `boundary verified; acceptance run pending`, never as measured OS compatibility. This corrects the evidence composer only; the privileged runner has not been executed and remains behind Requirement 37's audit, isolation, and explicit UAC gates.
