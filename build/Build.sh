#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
args=(build "$root/StorageChronicle.slnx")
if [[ "${1:-}" == "--no-restore" ]]; then
  args+=(--no-restore)
fi
dotnet "${args[@]}"
