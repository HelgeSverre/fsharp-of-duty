#!/bin/sh
# Universal launcher: self-contained .NET cannot ship one lipo'd binary, so the
# .app carries both publishes and this shim picks the one matching the CPU.
# hw.optional.arm64 (not uname -m): uname lies when the shell runs under Rosetta.
dir="$(cd "$(dirname "$0")" && pwd)"
if [ "$(sysctl -n hw.optional.arm64 2>/dev/null)" = "1" ]; then
  exec "$dir/arm64/Ironsight" "$@"
else
  exec "$dir/x64/Ironsight" "$@"
fi
