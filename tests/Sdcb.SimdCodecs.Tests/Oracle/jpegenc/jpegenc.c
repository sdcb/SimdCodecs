// Oracle CLI for the Sdcb.SimdCodecs.Jpeg encoder byte-exactness tests.
//
// Wraps the JPEG writer in stb_image_write.h (public domain / MIT) — the exact
// reference Sdcb.SimdCodecs.Jpeg.JpegEncoder is ported from. Reads a raw interleaved
// 8-bit image and writes a baseline JPEG at the given quality. Subsampling is NOT
// an argument: stbi_write_jpg derives it from quality (<=90 -> 4:2:0), which is
// precisely what JpegEncoder's JpegSubsampling.Auto reproduces.
//
//   jpegenc <width> <height> <channels> <quality> <input.raw> <output.jpg>
//
// Built by ../build.sh (via jpegenc/build.sh); binary lands in ../bin and the
// tests skip gracefully when it is absent.
#define STB_IMAGE_WRITE_IMPLEMENTATION
#include "stb_image_write.h"
#include <stdio.h>
#include <stdlib.h>

int main(int argc, char** argv) {
    if (argc != 7) {
        fprintf(stderr, "usage: jpegenc <width> <height> <channels> <quality> <input.raw> <output.jpg>\n");
        return 2;
    }
    int width = atoi(argv[1]);
    int height = atoi(argv[2]);
    int channels = atoi(argv[3]);
    int quality = atoi(argv[4]);
    const char* inPath = argv[5];
    const char* outPath = argv[6];

    if (width <= 0 || height <= 0 || channels < 1 || channels > 4) {
        fprintf(stderr, "bad geometry\n");
        return 2;
    }

    long need = (long)width * height * channels;
    unsigned char* data = (unsigned char*)malloc((size_t)need);
    if (!data) { fprintf(stderr, "oom\n"); return 3; }

    FILE* f = fopen(inPath, "rb");
    if (!f) { fprintf(stderr, "cannot open %s\n", inPath); free(data); return 3; }
    size_t got = fread(data, 1, (size_t)need, f);
    fclose(f);
    if (got != (size_t)need) { fprintf(stderr, "short read: %zu != %ld\n", got, need); free(data); return 4; }

    int ok = stbi_write_jpg(outPath, width, height, channels, data, quality);
    free(data);
    return ok ? 0 : 5;
}
