# TestLab.Common.ps1

This is a legacy dot-source shim for `VirtualBox.Common.ps1`, retained only for historical/mock contract compatibility. It does not itself perform an operation, and no supported acceptance runner imports it. Requirements 26 and 37 retire whole-OS VM execution; lightweight fixtures or file-backed VHDX are a separate physical workflow gated by Requirement 37.

The imported helper library contains legacy VirtualBox control functions; those functions are not an authorized current workflow and must not be invoked for acceptance. No VM, guest, ISO, host preflight, or artifact operation is performed by this shim alone.

Relevant historical requirements: 21–36. Operational VM entry points are inert retired stubs; `build/quality/Test-VirtualBoxTestLab.ps1` uses mocked responses to enforce the current fail-closed boundary. Physical acceptance is governed by Requirement 37.
