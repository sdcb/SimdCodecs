#!/usr/bin/env bash
# NativeAOT smoke test for the JPEG decoder: decode the same inputs under the JIT
# and as NativeAOT binaries; the pixel digests must be identical. NativeAOT fixes
# the vector tier at compile time, so this checks that whatever kernel it dispatches
# to is bit-identical to the JIT's.
#
# Usage: bash tests/Sdcb.SimdCodecs.AotSmoke/run.sh [work-dir]
# Needs the platform's NativeAOT prerequisites (clang/zlib on Linux, the MSVC
# linker on Windows).
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
work="${1:-$here/bin/aot-smoke}"
mkdir -p "$work"

dotnet run --project "$here" -c Release > "$work/jit.txt"
lines="$(wc -l < "$work/jit.txt" | tr -d ' ')"
[ "$lines" -gt 0 ] || { echo "no decodes ran" >&2; exit 1; }

# The default NativeAOT instruction set (a conservative baseline: Vector128 only on
# x64), then the build machine's own ("native": AVX2 where available), so both
# vector tiers are covered.
for isa in default native; do
  args=()
  if [ "$isa" = native ]; then args=(-p:IlcInstructionSet=native); fi
  dotnet publish "$here" -c Release -o "$work/publish-$isa" "${args[@]}" > "$work/publish-$isa.log" \
    || { cat "$work/publish-$isa.log"; exit 1; }
  exe="$work/publish-$isa/Sdcb.SimdCodecs.AotSmoke"
  if [ -f "$exe.exe" ]; then exe="$exe.exe"; fi
  "$exe" > "$work/aot-$isa.txt"
  if ! diff -u "$work/jit.txt" "$work/aot-$isa.txt"; then
    echo "NativeAOT ($isa instruction set) decode differs from the JIT decode" >&2
    exit 1
  fi
done

echo "NativeAOT output identical to JIT ($lines decodes; default and native instruction sets)"
