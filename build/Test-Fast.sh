#!/usr/bin/env bash
set -euo pipefail
dotnet test "$(dirname "$0")/../StorageChronicle.slnx" "$@"
