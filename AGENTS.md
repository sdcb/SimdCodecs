# AGENTS.md

Decisions already made for this repository. The how-to lives next to the code it is about:
[`tests/Sdcb.SimdCodecs.Tests/Oracle/README.md`](tests/Sdcb.SimdCodecs.Tests/Oracle/README.md) (oracles,
goldens, quality gates) and [`benchmarks/README.md`](benchmarks/README.md) (how to measure, and the
lessons behind these rules).

## Goal

Pure-managed .NET 10 image codecs plus a sniff-and-dispatch facade, where **every encoder and
decoder is world-class** — not a minimal "it works" codec in the stb mould: AVX2/AVX-512 → Vector128
→ scalar, low memory, at or beyond the reference native library (libjpeg-turbo, libpng, libwebp, …)
and ImageSharp. **Measure first**, end to end.

Tuned against that bar so far: JPEG encode and decode, WebP decode, PNG encode and decode (each has a
benchmark project). The other codecs are correct and tested but not yet optimised; bring one up to
the bar the same way — a benchmark project with a native and a managed competitor first, then
changes that it proves.

## Clean-room rules (non-negotiable)

- **Never read, decompile, step into with a debugger, or paraphrase** the source of ImageSharp,
  libjpeg-turbo, libjpeg / IJG, libpng, jxl-oxide, libjxl, jxrlib, OpenEXR, libwebp, libvpx
  (including the decoder source printed in RFC 6386 §20), or any GPL/AGPL code (e.g. jbig2dec).
- They **may** be used as black-box binaries — to benchmark, compare outputs, generate test data —
  through their documented APIs. Interop comes from the published docs and is self-checked against
  the binary (`TurboJpeg.SelfCheck`, `LibPng.SelfCheck`), never from headers.
- Algorithms come only from: ITU-T / ISO specs (T.81, T.83, T.88, T.800, …), the PNG specification,
  the prose and tables of RFC 6386 / RFC 9649, academic papers, CPU vendor manuals, and public-domain
  code (stb_image: the JPEG marker/entropy layer is a port of it). The JPEG sample arithmetic and
  encoder are defined in-repo from T.81, T.871 and the AAN paper.
- The upstream's JPEG XR, JPEG XL and OpenEXR codecs (ports of jxrlib and jxl-oxide) were dropped on
  purpose; do not bring them back.
- Nothing of uncertain provenance enters the repo — code, comments, fixtures or images. Third-party
  test images are Public Domain or CC0, fetched by URL + SHA-256 into git-ignored caches, never
  committed.
- Do not port third-party code of any licence, even small snippets: every file is first-party MIT,
  with no third-party notices to carry.

## Repository decisions

- One package per codec (`src/Sdcb.SimdCodecs.*`, PackageId = assembly = root namespace); packages
  ship in lockstep, versioned from the Git tag. Package versions are central
  (`Directory.Packages.props`).
- No shipped package uses another's internals: `InternalsVisibleTo` names only the test and benchmark
  projects. An internal API has no version contract, so two packages upgraded separately fail at run
  time. Code two packages need is shared as source (`src/Shared`, compiled under each package's
  namespace); code a consumer needs is public in `Abstractions`.
- One test project for everything (xunit v3 + Shouldly); Magick.NET is the test-only black box.
- Benchmarks are in `SimdCodecs.slnx` (compiled by CI, never run by it). The NativeAOT and Mono AOT
  smoke tests (`tests/Sdcb.SimdCodecs.AotSmoke`, `tests/Sdcb.SimdCodecs.WasmSmoke`) are not.
- Sample images are downloaded at run time, never committed (`tools/sample-assets/`).
- Names the compiler cannot check — `InternalsVisibleTo`, embedded resource names
  (`Sdcb.SimdCodecs.Color.Icc.sRGB-v4.icc`), paths in tests and scripts — break silently on rename.

## Build, test, warnings

```bash
dotnet build SimdCodecs.slnx -c Release
REQUIRE_ORACLES=1 dotnet test SimdCodecs.slnx -c Release            # what CI runs
DOTNET_EnableAVX2=0        dotnet test tests/Sdcb.SimdCodecs.Tests -c Release --filter "FullyQualifiedName~Jpeg|FullyQualifiedName~Png|FullyQualifiedName~Bmp|FullyQualifiedName~Ico"
DOTNET_EnableHWIntrinsic=0 dotnet test tests/Sdcb.SimdCodecs.Tests -c Release --filter "FullyQualifiedName~Jpeg|FullyQualifiedName~Png|FullyQualifiedName~Bmp|FullyQualifiedName~Ico"
bash tests/Sdcb.SimdCodecs.AotSmoke/run.sh                          # NativeAOT output == JIT output
bash tests/Sdcb.SimdCodecs.WasmSmoke/run.sh                         # Mono AOT (browser-wasm) output == JIT output```

- The facade binds each codec's static members to delegates by concrete method group, never inside
  a generic method: Mono AOT (browser-wasm, iOS, Android) traps on a delegate over a type
  parameter's static abstract member. The wasm smoke test covers every facade entry point; run it
  locally after touching the facade or a codec's `IImageDecoder` adapter.
- **No new warnings.** The build stays at its current set (pre-existing XML-doc warnings only); fix
  what you introduce, never suppress it.
- **A skipped oracle is not a passing oracle.** Oracle tests skip when their tool is missing; read the
  skip messages, or run with `REQUIRE_ORACLES=1`.
- Every vector kernel has a scalar twin and must produce identical output on every tier
  (Vector256 / Vector128 / scalar, x64 and arm64); CI forces each tier.
- After touching the JPEG entropy decoder, soak the fuzzer:
  `JPEG_FUZZ_ITERATIONS=300000 … --filter "FullyQualifiedName~JpegFuzz"`. The BMP decoder's soak is
  `BMP_FUZZ_ITERATIONS=300000 … --filter "FullyQualifiedName~BmpRobustness"`.

## Output contracts

- **Never regenerate a golden to make a test pass.** A golden (`Fixtures/*-golden.tsv`) moves only for
  an intentional, reviewed output change, with the diff as part of the review. Pinned oracle versions
  (OpenJPEG, libjpeg-turbo, libwebp) are contract too.
- **The JPEG decoder's arithmetic is frozen** (`JpegIdct`, `JpegResample`, `JpegColor`): every tier and
  fast path reproduces its operation sequence bit for bit — no FMA, no reassociation. Changing it
  requires passing `accuracy --gate` first and lands as its own commit with the gate table.
- **The JPEG encoder's contract is quality, not bytes:** `encode --gate` must pass, and optimised /
  progressive streams decode to exactly the baseline's pixels.
- **WebP decode is byte-exact against libwebp 1.6.0.** PNG decode is lossless: exact equality, never
  a tolerance.

Gate thresholds and the oracle set: [`Oracle/README.md`](tests/Sdcb.SimdCodecs.Tests/Oracle/README.md).

## Validation

- Exact assertions over tolerances; foreign bytes (Magick.NET, hand-assembled streams, OpenJPEG,
  jbig2enc) over round trips through our own encoder.
- When a test should catch a deliberate bug, break the code and confirm that it does.
- Untrusted input fails with `InvalidDataException` (or `false` from `Try*`) — never a hang,
  unbounded allocation, `OverflowException` or `OutOfMemoryException`.
- `Unsafe` / `MemoryMarshal` access in hot loops only where one up-front check proves every index in
  range, with the proof in a comment beside it. Fuzz tests are the safety net, not the proof.
- `JpegDecoder.DecodeTo` into a caller buffer allocates nothing (`JpegAllocationTests`); its per-thread
  state cache is reset on every rent — new fields go into the `Reset` methods.

## Performance work

Read [`benchmarks/README.md`](benchmarks/README.md) before optimising anything. The rules:

- Only an end-to-end number decides; a kernel's isolated speed-up is not the codec's.
- Compare A and B in the same run against a freshly built baseline (`baseline` + `ab` / `matrix`);
  a number from an earlier session is not a baseline. Record the machine with every result.
- Attribute time by ablation (switch a stage off, see what disappears), not by a sampling profiler.
- Check correctness before timing; a benchmark refuses to time wrong output.
- Vector kernels are `MethodImplOptions.NoInlining | AggressiveOptimization`.
