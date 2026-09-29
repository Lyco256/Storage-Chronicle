#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
while IFS= read -r project; do
  dotnet build "$project" --nologo "$@"
  assembly_dir="$(dirname "$project")/bin/Debug"
  assembly_name="$(basename "$project" .csproj).dll"
  assembly="$(find "$assembly_dir" -type f -name "$assembly_name" -print -quit)"
  if [[ -z "$assembly" ]]; then
    echo "Test assembly was not produced: $project" >&2
    exit 6
  fi
  relative_assembly="${assembly#"$root/"}"
  dotnet test "$relative_assembly" "$@"
done < <(find "$root/tests" -type f -name '*UI*.Tests.csproj' -o -name '*Ui*.Tests.csproj' | sort)
