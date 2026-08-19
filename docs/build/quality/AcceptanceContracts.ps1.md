# AcceptanceContracts.ps1

Defines the single shared Windows privileged capability contract used by the producer (`build/Test-Privileged.ps1`), the Windows 10 Stage A composer, and the final acceptance aggregator. The required list is fixed to the sixteen capabilities in `Requirements/25_WINDOWS_PRIVILEGED_CAPABILITY_MATRIX.md`; consumers reject missing, duplicate, or extra capability names instead of trusting an evidence artifact's self-declared subset.

This file contains no test execution or host mutation. It is dot-sourced by the acceptance scripts and is covered by PowerShell parser validation, documentation mirror validation, and the installer/acceptance contract tests.
