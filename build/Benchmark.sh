#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet run --project "$root/benchmarks/StorageChronicle.Benchmarks/StorageChronicle.Benchmarks.csproj" -c Release -- "$@"
