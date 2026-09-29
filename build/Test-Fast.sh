#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
extra=("$@")
while IFS= read -r project; do
  if [[ "$(uname -s)" != MINGW* && "$(uname -s)" != MSYS* && "$(uname -s)" != CYGWIN* ]] && grep -q 'net10.0-windows' "$project"; then
    echo "Skipping Windows-targeted test project on non-Windows host: $project"
    continue
  fi
  dotnet build "$project" --nologo "${extra[@]}"
  assembly_dir="$(dirname "$project")/bin/Debug"
  assembly_name="$(basename "$project" .csproj).dll"
  assembly="$(find "$assembly_dir" -type f -name "$assembly_name" -print -quit)"
  if [[ -z "$assembly" ]]; then
    echo "Test assembly was not produced: $project" >&2
    exit 6
  fi
  relative_assembly="${assembly#"$root/"}"
  dotnet test "$relative_assembly" "${extra[@]}"
done < <(find "$root/tests" -type f -name '*.Tests.csproj' ! -name 'StorageChronicle.Platform.Windows.Integration.Tests.csproj' | sort)
