# New-TestDataVhdx.ps1

This legacy VirtualBox VDI creation/attachment helper is retired under Requirement 37. It always exits with status 2 and performs no host, VM, disk, file, or evidence operations, regardless of `-Apply`. Whole-OS guest testing is out of scope.

Do not use this script to test the permitted lightweight file-backed VHDX workflow. That workflow has separate safety gates in `Requirements/37_PHYSICAL_READ_ONLY_ACCEPTANCE.md`; no creation, attachment, initialization, formatting, or cleanup is authorized until those gates pass and the user approves the exact target.
