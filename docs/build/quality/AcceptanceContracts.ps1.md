# AcceptanceContracts.ps1

Defines the shared acceptance contracts used by the producer (`build/Test-Privileged.ps1`), installer/Windows 10 composers, and the final acceptance aggregator. The Windows privileged list is fixed to the sixteen capabilities in `Requirements/25_WINDOWS_PRIVILEGED_CAPABILITY_MATRIX.md`; installer case IDs and Windows 10 Stage A check names are also centralized. Consumers reject missing, duplicate, or extra names instead of trusting an evidence artifact's self-declared subset.

This file contains no test execution or host mutation. It is dot-sourced by the acceptance scripts and is covered by PowerShell parser validation, documentation mirror validation, and the installer/acceptance contract tests.
