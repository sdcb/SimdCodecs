# JBIG2 oracles

Three independent implementations check `Sdcb.SimdCodecs.Jbig2`, in opposite
directions. None is ever read as a port source — see the licence note at the
bottom.

| Direction | Tool | Where it lives | Needs installing? |
|---|---|---|---|
| **jbig2enc → us** | `jbig2` (jbig2enc, Apache-2.0) | committed `Fixtures/jbig2/*.jb2` (generic) and `sym*.jb2` (symbol mode), driven by `Jbig2EncoderFixtureTests` / `Jbig2SymbolFixtureTests` | **No** — the fixtures are in the repo |
| **us → jbig2dec** | `jbig2dec` (Artifex, AGPL) | `Jbig2Oracle` + `Jbig2OracleTests` | Yes, else the tests skip |
| **libtiff → us** (MMR) | ImageMagick / libtiff, via Magick.NET | `Group4Tiff` + `Jbig2MmrOracleTests` | **No** — Magick.NET is already a package reference |

They cover different things, which is the point of having all three.

- jbig2enc only ever emits **GBTEMPLATE 0 with nominal AT pixels** (asserted by
  `JbigEncFixtures_UseTemplate0WithNominalAtPixels`, so a fixture refresh can't
  quietly change it). Real third-party bytes, but a narrow slice of the format.
- jbig2dec is driven with **our own** streams, so it reaches everything no
  encoder emits: every generic template, moved AT pixels, TPGDON either way,
  refinement regions, halftone regions, and seven of the eight text-region
  reference-corner/transposed combinations. For rungs 4 and 5 it is the *only*
  check there is — jbig2enc has no halftone mode, and its `-r` refinement flag
  writes an empty file.
- libtiff covers the **MMR** path, which neither of the other two touches:
  jbig2enc never emits `MMR = 1`, and the jbig2dec direction needs a stream we
  can produce, which for MMR means borrowing an encoder. CCITT Group 4 *is*
  ITU-T T.6, which is what T.88 §6.2.6 carries, so a Group 4 TIFF strip is a
  JBIG2 MMR region with a different wrapper. `Jbig2MmrOracleTests` then feeds
  those same bytes on through the segment layer and past jbig2dec, so all three
  implementations end up agreeing on one raster.

The MMR oracle is the only one here with no round-trip behind it — encoding
JBIG2 is a non-goal, and the T.4 run-length tables have no self-consistency to
check, so a mistyped entry produces a wrong run length and nothing internal
notices. That was not hypothetical: mislabelling white run 24 as 25 left every
picture-shaped test green, because none of them contains a white run of exactly
24. Coverage there is now per table entry
(`EveryRunLength_DecodesToItself`), not per picture.

## Installing jbig2dec

```bash
apt-get install -y jbig2dec                                        # Linux / CI
bash tests/Sdcb.SimdCodecs.Tests/Oracle/jbig2/fetch-windows.sh    # Windows (Git Bash)
```

Upstream publishes no Windows binary; `fetch-windows.sh` downloads MSYS2's UCRT64
build (jbig2dec + its libpng/zlib DLLs), pinned by file name and SHA-256, into the
git-ignored `dist/`. Installing it into a WSL distro also works.

`Jbig2Oracle` looks for `JBIG2DEC=<path>` first, then `jbig2dec` on PATH, then
`dist/bin/jbig2dec.exe`, then `wsl.exe -- jbig2dec`. `JBIG2DEC` is useful for a
custom build, and for exercising the failure branch below.

### Skip vs. fail

| Environment | jbig2dec present | jbig2dec missing |
|---|---|---|
| local (default) | tests run | **reported skips** (`Assert.Skip`) |
| CI (`REQUIRE_ORACLES=1`) | tests run | **build fails** |

Graceful skipping keeps a clean clone green, and in CI it is a liability — a job
that stops installing its oracle looks exactly like one that runs it. CI's test
step sets `REQUIRE_ORACLES=1` so that silence becomes a red build. The shared
entry point is `OracleGate.RequireOrSkip`; JBIG2 was the first harness on it.

All three external oracles (jbig2dec, jpegenc, OpenJPEG) are gated and built,
fetched or installed in CI. An oracle guard that `return`s early counts as a
**pass**, which is exactly the silence `REQUIRE_ORACLES=1` exists to break.

### A note on the symbol-mode fixtures

`sym.jb2` / `sym_tpgd.jb2` come with a committed `.pbm` beside them, which the
others do not. Symbol matching is lossy by default (`-t 0.85`): jbig2enc replaces
near-identical glyphs with a shared bitmap, so the decoded page is deliberately
**not** the source image and there is no grid anyone could write by eye. The
expected raster is jbig2dec's, and committing it is the same licence call as
committing jbig2enc's output — a decoded image is data, not a derivative of the
decoder. Nothing needs installing to run those tests.

## Regenerating the committed fixtures

Only needed when changing the test pattern or refreshing against a newer
jbig2enc:

```bash
apt-get install -y jbig2
bash tests/Sdcb.SimdCodecs.Tests/Oracle/jbig2/make-fixtures.sh
```

The expected pattern is duplicated as an ASCII grid in
`Jbig2EncoderFixtureTests`; keep the two in sync.

## What the oracle actually catches

Worth being precise, because it is not what you might assume. Established by
deliberately breaking the decoder and watching which layer fails:

| Deliberate bug | Round-trip tests | One-hot template tests | jbig2dec oracle |
|---|:---:|:---:|:---:|
| Template reads a different **pixel** | pass | **fail** | **fail** |
| Two template **bits swapped** (same pixels) | pass | **fail** | pass |

The MMR side was measured the same way, with eight deliberate bugs: a mislabelled
white run, a duplicated black makeup code, a wrong extended-makeup value, a wrong
vertical-mode offset, pass mode mapped to vertical, pass mode moving to `b1 + 1`
instead of `b2`, the `b1` parity rule inverted, and `a0` starting at 0 instead of
-1. All eight are caught, but not by any single layer:

| Layer | Catches | Misses |
|---|:---:|---|
| picture patterns (`Patterns`) | 7 / 8 | the mislabelled white run — no picture happens to contain a white run of exactly 24 |
| per-run-length sweep | 6 / 8 | both mode-logic bugs — its rows are all coded in horizontal mode by construction |
| hand vectors + table structure (`Jbig2MmrTests`) | 8 / 8 | — |

The last row looks like it makes the others redundant and does not: it catches
the table bugs *structurally* (a mislabelled run leaves a gap, a duplicated code
is not prefix-free) rather than by decoding, so it would miss any bug that keeps
the tables well-formed but wrong — which is exactly what libtiff is there for.

A context value is only an index into adaptive-state slots that all start
identical, so permuting the bit positions preserves which pixels share a slot and
the coded bytes come out unchanged — a permuted decoder is still conformant.
What conformance really rests on is **which pixels the template reads**, and that
is what jbig2dec catches.

The literal numbering is still pinned to T.88 Figures 4-7 by the one-hot tests,
for one substantive reason: TPGDON's SLTP decision uses **hard-coded** contexts
(`0x9B25` and friends) that name a specific neighbourhood in the spec's
numbering, so permuting the real contexts changes which neighbourhood collides
with that slot.

Note also that the round-trip tests are structurally blind to all of this — the
test encoder forms contexts by calling the shipped decoder's own `Context`, so
both sides share any mistake. That is precisely why this directory exists.

## Licence

This repo is **MIT**, and its clean-room rules keep every file first-party:
third-party source is never a port source, whatever its licence, and AGPL code
cannot go anywhere near it.

- **jbig2dec** — AGPL. Usable **only** as an executable. Never read its source.
- **jbig2enc** — Apache-2.0. Same rule for its source; its *output* is data (a
  rendering of a pattern authored here) and carries no constraint, which is why
  the fixtures can be committed.

Running a program is not linking to it. `Sdcb.SimdCodecs.Jbig2` itself is clean-room
from ITU-T T.88. See [`AGENTS.md`](../../../../AGENTS.md) for the clean-room rules.
