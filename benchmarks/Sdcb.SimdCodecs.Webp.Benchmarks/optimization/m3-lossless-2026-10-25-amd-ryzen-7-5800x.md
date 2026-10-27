# M3 — lossless (VP8L) past libwebp, in the caller's buffer (2026-10-25, AMD Ryzen 7 5800X)

## Machine

| | |
|---|---|
| CPU | AMD Ryzen 7 5800X (Zen 3), AVX2, BMI2, no AVX-512 (`Vector512.IsHardwareAccelerated` = false) |
| OS | Windows 11 25H2 (10.0.26200) |
| .NET | runtime 10.0.11, SDK 11.0.100-rc.2, X64 RyuJIT x86-64-v3 |
| libwebp | 1.6.0 from [`build-libwebp.sh`](build-libwebp.sh) (generic x86-64, SSE2/SSE4.1 dispatch) |
| Commits | baseline `640974b` (M2), current `94fc5b3` |

## Result (`ab --ablate`, BenchmarkDotNet medians, one run, ms)

| Input | libwebp | M2 | now | now ÷ M2 | now ÷ libwebp |
|---|---:|---:|---:|---:|---:|
| lossy opaque | 5.418 | 5.295 | 5.169 | 0.98× | 0.95× |
| lossy + alpha | 10.559 | 5.612 | 5.349 | 0.95× | 0.51× |
| lossless | 7.591 | 8.439 | 5.245 | 0.62× | 0.69× |

Lossless stage costs now: VP8L transforms 2.50 ms (of which the predictor 1.72), ARGB→RGBA 0.14 ms; the
rest (~2.6 ms) is entropy decoding.

### The synthetic corpus (`quick --files "*lossless*" --rounds 10`, in-process medians)

| Group | M2 ÷ libwebp | now ÷ libwebp |
|---|---|---|
| `mixed` / `alpha` 1920×1080, 5 option sets each (exact, near-lossless 60, z0, z6, z9) | 1.41× – 2.31× | 0.71× – 0.90× |
| `mixed` 1001×777 and `noisy` 640×480, same option sets | 1.8× – 1.9× (z6; others not timed at M2) | 0.73× – 0.94× |
| `small` 33×17 | — | 0.49× – 0.63× |

Every lossless corpus file now decodes faster than libwebp. (Earlier `quick` runs reported the small files
2–3× slower: 20 warm-up decodes of a 33×17 file finish before tiered compilation promotes anything, so
tier-0 code was timed. `quick` now warms each contestant for at least 300 ms; every `small-33x17` file,
lossy ones included, is 0.49–0.78× of libwebp.)

## Memory

Managed allocation per `DecodeTo` (MemoryDiagnoser): lossless 536 → 320 B, lossy + alpha 537 → 321 B (the
`List<Transform>` per VP8L image is gone), lossy opaque 320 B. `peakmem` (fresh process per decode, after a
warm-up on a tiny file of the same kind, destination already touched; MiB, median of 3):

| Input | libwebp first decode | M2 first decode | now first decode | now steady |
|---|---:|---:|---:|---:|
| lossy opaque | 0.25 | 0.25 | 0.24 | 0.02 |
| lossy + alpha | 2.46 | 3.04 | 3.07 | 0.02 |
| lossless | 8.20 | 8.34 | 0.79 | 0.02 |

A tightly packed RGBA destination has exactly the size of the ARGB image, so the lossless image is
decoded, inverse-transformed and swizzled in the caller's buffer: what remains is the transform and
entropy sub-images and the prefix-code tables. (A padded stride, `Decode` and the ALPH plane still use a
pooled ARGB buffer.)

## What changed (one commit each, numbers in the commit messages)

1. **Table-driven prefix codes, 64-bit bit reader, lazy colour cache** (`60413be`): two-level decoding tables
   (8-bit root, second level up to 2^7 per slot, in one pooled buffer per image) instead of a node tree and an
   8-bit lookup; an LSB-first 64-bit reader refilled by one unaligned load that reads zero bytes past the end
   and reports truncation as soon as it is certain (so a truncated stream still fails with
   `InvalidDataException` and cannot spin on padding); the pixel loop keeps the bit buffer in locals, selects
   the prefix-code group only at meta-block boundaries, decodes only green when red, blue and alpha have a
   single symbol, and fills the colour cache lazily, right before a cache code is read (same insertion order,
   same content). `lossless-1080p` 8.6 → 6.8 ms; corpus z6 0.68–0.72× of before.
2. **Vector serial predictor modes** (`e55927e`): modes 5, 6, 7, 10–13 read the pixel just reconstructed, so
   they are a chain; each pixel is now one vector with the left pixel in a register — floor average as
   `pavgb` minus the carry bit (`uhadd` on Arm), Select as SAD + compare + blend instead of a branch on the
   data, clamps through 16-bit lanes and a saturating pack, ClampHalf's truncating division as
   (d − (d >> 15)) >> 1. Predictor transform on the 1080p corpus 5.0–11.2 → 1.9–2.7 ms.
3. **In-place decode into the caller's buffer** (`94fc5b3`): transforms on `Span<int>`, colour indexing
   expanded in place (rows last to first, right to left: a packed source is never after its destination),
   the transform list an inline array. Memory 8.73 → 0.70 MiB.

Also: the `quick` warm-up floor (`9eb2f3e`), a `NoPredictorTransform` ablation, ablation sets chosen by
content so corpus files get the right columns.

Every step: `verify` 103/103 byte-identical with libwebp 1.6.0, the tests in all three ISA tiers, and a
deliberate break caught (lazy cache one pixel short: 13 failures; floor instead of truncating halving and
`<=` instead of `<` in Select: the predictor equivalence test, now on full 32-bit pixels and a 0/255 pattern).

## Rejected experiments

- **Root tables of 10 bits** instead of 8: 1.06–1.11× slower on the 1080p corpus except z0 (0.96×) — building
  1024-entry tables for every code of every group costs more than the second-level lookups saved.
- **`AggressiveOptimization` on the pixel loop** (no OSR, no dynamic PGO; it is entered a few times per image,
  so it otherwise runs as OSR code until tier 1): 0.91–0.98× on three corpus files, 1.00–1.11× on two others and
  1.04× on `lossless-1080p`. Ambiguous; not kept.
