#!/usr/bin/env bash
set -euo pipefail
"$(dirname "$0")/Build.sh"
"$(dirname "$0")/Test-Fast.sh"
