# Sample assets

The images the `linqpad-samples` (`src/*/linqpad-samples/*.linq`) download from
`https://cv-public.sdcb.ai/simdcodecs/samples/v1/`. `assets.tsv` lists each file, its SHA-256 and
where it came from. The images are not committed and not packed into any package.

## Regenerating

```bash
cd tools/sample-assets
mkdir -p in
cp ../../benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/.cache/corpus/originals/{cam-sony-a7r3,sunrise-fuji}.jpg in/   # from scripts/fetch-corpus.sh
cp /path/to/mandelbrot.webp in/                                                                                  # see assets.tsv
docker build -t simdcodecs-assets .            # in mainland China: --build-arg MIRROR=mirrors.ustc.edu.cn
docker run --rm -v "$PWD:/w" -w /w simdcodecs-assets bash make-assets.sh
```

The output lands in `out/` with a `SHA256SUMS`. ImageMagick, libtiff's `tiffcp`, `cwebp`,
`opj_compress` and `jbig2` run only as black-box binaries inside the image.

## Publishing

Upload to a new version directory (`v2/`, …) rather than overwriting `v1/`, so released samples keep
working, then update the URLs in the samples and the table in `assets.tsv`:

```bash
for f in out/*; do npx wrangler r2 object put "cv-public/simdcodecs/samples/v2/$(basename "$f")" --file "$f" --remote; done
```

## README showcase

The image at the top of the repository README is built from these files after decoding them with this
library, so every pixel in it went through Sdcb.SimdCodecs:

```bash
dotnet run decode-all.cs -- out decoded        # RGBA dumps, one per file
python make-showcase.py decoded showcase.jpg   # needs Pillow
npx wrangler r2 object put cv-public/simdcodecs/readme/showcase-v2.jpg --file showcase.jpg --content-type image/jpeg --remote
```

Upload a new name (`showcase-v2.jpg`, …) rather than overwriting, so caches and older READMEs keep the
image they pointed at, then update the URL in `README.md` and `README.zh-CN.md`.
