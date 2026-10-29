#!/usr/bin/env bash
# Mono AOT (browser-wasm) smoke test: run the NativeAOT smoke test's JPEG decodes and facade calls
# under the JIT and as an AOT-compiled WebAssembly app on Node.js; the output must be identical.
# Mono AOT is also the runtime of iOS and Android.
#
# Usage: bash tests/Sdcb.SimdCodecs.WasmSmoke/run.sh [work-dir]
# Needs the wasm-tools workload (`dotnet workload install wasm-tools`) and Node.js.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
work="${1:-$here/bin/wasm-smoke}"
mkdir -p "$work"

# Line endings are normalised: the JIT writes CRLF on Windows, Node LF.
dotnet run --project "$here/../Sdcb.SimdCodecs.AotSmoke" -c Release | tr -d '\r' > "$work/jit.txt"
lines="$(wc -l < "$work/jit.txt" | tr -d ' ')"
[ "$lines" -gt 0 ] || { echo "no decodes ran" >&2; exit 1; }

dotnet publish "$here" -c Release -o "$work/publish" > "$work/publish.log" \
  || { cat "$work/publish.log"; exit 1; }
cp "$here/main.mjs" "$work/publish/wwwroot/"
(cd "$work/publish/wwwroot" && node main.mjs) | tr -d '\r' > "$work/wasm.txt"
if ! diff -u "$work/jit.txt" "$work/wasm.txt"; then
  echo "Mono AOT (browser-wasm) output differs from the JIT's" >&2
  exit 1
fi

echo "Mono AOT (browser-wasm) output identical to JIT ($lines lines)"
