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

# The JIT reference runs while the (much longer) AOT publish does. It is built first: two
# concurrent builds of the shared codec projects race on their obj folders. Line endings are
# normalised: the JIT writes CRLF on Windows, Node LF.
dotnet build "$here/../Sdcb.SimdCodecs.AotSmoke" -c Release > "$work/jit-build.log" \
  || { cat "$work/jit-build.log"; exit 1; }
dotnet run --project "$here/../Sdcb.SimdCodecs.AotSmoke" -c Release --no-build > "$work/jit-raw.txt" &
jit=$!
dotnet publish "$here" -c Release -o "$work/publish" > "$work/publish.log" \
  || { cat "$work/publish.log"; exit 1; }
wait "$jit" || { echo "JIT run failed" >&2; exit 1; }
tr -d '\r' < "$work/jit-raw.txt" > "$work/jit.txt"
lines="$(wc -l < "$work/jit.txt" | tr -d ' ')"
[ "$lines" -gt 0 ] || { echo "no decodes ran" >&2; exit 1; }
cp "$here/main.mjs" "$work/publish/wwwroot/"
(cd "$work/publish/wwwroot" && node main.mjs) | tr -d '\r' > "$work/wasm.txt"
if ! diff -u "$work/jit.txt" "$work/wasm.txt"; then
  echo "Mono AOT (browser-wasm) output differs from the JIT's" >&2
  exit 1
fi

echo "Mono AOT (browser-wasm) output identical to JIT ($lines lines)"
