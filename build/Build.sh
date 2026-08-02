#!/usr/bin/env bash
set -euo pipefail
dotnet build "$(dirname "$0")/../StorageChronicle.slnx" "$@"
