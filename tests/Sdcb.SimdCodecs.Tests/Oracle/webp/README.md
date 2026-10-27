# WebP oracles

The live WebP comparisons run libwebp's `dwebp` (still images) and `anim_dump`
(animations) as black-box oracles. `WebpOracle` resolves each one from `DWEBP` /
`ANIM_DUMP`, then this directory's `bin/`, then `PATH`. The tests are gated
through `OracleGate`: they skip when a tool is missing, and fail when
`REQUIRE_ORACLES=1`. CI installs both tools with `apt-get install webp`.
Without either tool, the committed digests in `Fixtures/webp/*-golden.tsv` still
run.

`build.sh` downloads the official libwebp 1.6.0 release tarball and builds
`anim_dump` with CMake/Make into `bin/`, for anyone who wants the exact version
the digests were made with. The binary is only a black-box oracle. The downloaded
sources are not read, copied, or linked into Sdcb.SimdCodecs.Webp. Downloads, build
products, and the oracle executable stay in this directory's ignored `src/`,
`build/`, and `bin/` directories.
