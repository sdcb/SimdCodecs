# BMP / ICO benchmark (`Sdcb.SimdCodecs.Bmp.Benchmarks`)

BMP and ICO decode and encode, timed against ImageSharp and WIC as black boxes, with sizes, managed allocations
and peak working sets. Run it by path from the repository root; recorded results go in [`results/`](results/).

```bash
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Bmp.Benchmarks -- verify
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Bmp.Benchmarks -- matrix all
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Bmp.Benchmarks -- baseline          # A/B against HEAD
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Bmp.Benchmarks -- peakmem decode
dotnet run -c Release --project benchmarks/Sdcb.SimdCodecs.Bmp.Benchmarks -- bdn decode --cases 'photo-(rgb24|p8)$'
```

## Cases

Every bitmap is `1920×1080` (`--size WxH`), cut from the PNG benchmark's two source images (its `Sources.cs` is
linked, not copied): **`photo`**, a centre crop of a CC0 corpus master when the JPEG benchmark's corpus cache exists
(synthetic otherwise), and **`screen`**, synthetic UI content. Icons are 256×256 and 48×48 cuts of the same.

| Matrix | Cases (per content) | Written by |
|---|---|---|
| decode | `rgb24`, `bgra32` (32-bit with alpha), `rgb16`, `p8`, `p4`, `p1` | ImageSharp, a foreign encoder |
| decode | `rgb24-topdown`, `argb32-v4` (V4 alpha mask), `rgb565`, `rle8`, `rle4`, `ico256-bmp32`, `ico256-png`, `ico48-bmp8` | ours (layouts ImageSharp does not write) |
| encode | `rgb24`, `argb32`, `rgb565`, `p8`, `rle8` from RGBA; `ico-set` (16, 32, 48 as DIB, 256 as PNG) | — |

## Contestants

| Name | Decode | Encode |
|---|---|---|
| `current` | `BmpImageDecoder` / `IcoImageDecoder.TryDecodeInto` to RGBA in a reused buffer | `BmpEncoder` / `IcoEncoder`, from RGBA |
| `current BGR(A)` | the same to BGRA, WIC's layout | the same from the B, G, R(, A) input WIC gets |
| `baseline` | the previous commit's decoder, same process (`baseline [git-ref]`) | the previous commit's encoder |
| ImageSharp 3.1.12 | `Image.Load<Rgba32>` + `CopyPixelDataTo`, `MaxDegreeOfParallelism = 1` (no icons: it has no ICO codec) | `BmpEncoder` with the same bits per pixel, into a reused stream (no RLE) |
| WIC | WPF's `BitmapDecoder` + `FormatConvertedBitmap` to BGRA (Windows only) | WPF's `BmpBitmapEncoder` from B, G, R(, A) into a reused stream (24- and 32-bit) |

**Correctness before timing.** Decode: `current` must give the source back exactly where the file holds it
losslessly, and agree exactly with a competitor where it does not; otherwise the run stops. A competitor that
disagrees with `current` is dropped from that case with a note (WIC is compared on colour alone for a 32-bit
`BI_RGB` file, whose fourth byte it ignores by design). Encode: every output decodes, and the lossless layouts give
the source back. `verify` runs these checks without timing anything.

**Clean-room.** ImageSharp and WIC are black boxes. WIC is reached through WPF's documented
`System.Windows.Media.Imaging` API (the project targets `net10.0-windows` on Windows, `net10.0` elsewhere), so no
COM interface is declared by hand; `Wic.SelfCheck` checks it against known pixels at start-up.
