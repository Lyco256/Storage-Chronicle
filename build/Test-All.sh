#!/usr/bin/env bash
set -euo pipefail
"$(dirname "$0")/Build.sh"
"$(dirname "$0")/Test-Fast.sh"
"$(dirname "$0")/Test-Ui.sh"
if [[ "${RUN_PRIVILEGED:-0}" == "1" || "${1:-}" == "--run-privileged" ]]; then
  STORAGE_CHRONICLE_RUN_PRIVILEGED_ACCEPTANCE=1 \
    "$(dirname "$0")/Test-WindowsPrivileged.sh"
else
  echo "Privileged Windows acceptance is isolated; set RUN_PRIVILEGED=1 or pass --run-privileged."
fi
