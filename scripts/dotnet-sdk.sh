#!/usr/bin/env bash

require_repo_dotnet_sdk() {
  local repo_root="${1:?repo root is required}"
  local global_json="$repo_root/global.json"
  local required_sdk
  local actual_sdk

  if [[ ! -f "$global_json" ]]; then
    echo "global.json was not found: $global_json" >&2
    return 2
  fi

  required_sdk="$(sed -nE 's/^[[:space:]]*"version"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' "$global_json" | head -n 1)"
  if [[ -z "$required_sdk" ]]; then
    echo "Could not read .NET SDK version from $global_json" >&2
    return 2
  fi

  if ! command -v dotnet >/dev/null 2>&1; then
    echo "dotnet was not found. Install .NET SDK $required_sdk as specified by global.json." >&2
    return 2
  fi

  if ! actual_sdk="$(cd "$repo_root" && dotnet --version 2>/dev/null)"; then
    echo "dotnet could not select SDK $required_sdk. Install that exact SDK version." >&2
    return 2
  fi

  if [[ "$actual_sdk" != "$required_sdk" ]]; then
    echo "Kkindle uses one .NET SDK version: $required_sdk from global.json; found $actual_sdk." >&2
    return 2
  fi
}
