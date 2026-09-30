#!/usr/bin/env bash
# Builds the native iroh library (cargo; cached after the first run) and starts the chat sample.
#
#   ./run.sh host
#   ./run.sh join <ticket>
set -euo pipefail
cd "$(dirname "$0")"

if ! command -v cargo >/dev/null 2>&1; then
  echo "cargo not found; install the Rust toolchain first: https://rustup.rs" >&2
  exit 1
fi

dotnet build irohnet/N0.IrohNet.NativeAssets.Linux/N0.IrohNet.NativeAssets.Linux.csproj -f net10.0 -r linux-x64
exec dotnet run --project src/Chat -f net10.0 -r linux-x64 -p:TargetFrameworks=net10.0 -- "$@"
