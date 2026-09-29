#!/usr/bin/env bash
set -euo pipefail
if [[ "${STORAGE_CHRONICLE_RUN_PRIVILEGED_ACCEPTANCE:-0}" != "1" ]]; then
  echo 'Windows privileged acceptance is not executed on Linux by default; set STORAGE_CHRONICLE_RUN_PRIVILEGED_ACCEPTANCE=1 only on a configured Windows acceptance host.'
  exit 0
fi
if command -v pwsh >/dev/null 2>&1; then
  pwsh -NoProfile -File "$(dirname "$0")/Test-WindowsPrivileged.ps1" "$@"
else
  echo 'pwsh is required for Windows privileged acceptance.' >&2
  exit 2
fi
