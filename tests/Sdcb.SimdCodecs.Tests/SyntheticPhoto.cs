namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// A deterministic, photo-like RGB test image generated in code, so the tests that
/// want "natural" content (smooth gradients, textured regions, soft and hard edges)
/// need no committed third-party photograph. Multi-octave value noise supplies
/// the low-frequency structure and the fine texture; a few anti-aliased discs and
/// a hard horizon supply edges.
/// </summary>
internal static class SyntheticPhoto
{
    /// <summary>Packed RGB, 3 bytes per pixel, row-major.</summary>
    public static byte[] Rgb(int width, int height, uint seed = 0x5EED1234u)
    {
        var rgb = new byte[width * height * 3];
        var lattices = new[]
        {
            Lattice(seed + 1, 9), Lattice(seed + 2, 17), Lattice(seed + 3, 33), Lattice(seed + 4, 129),
        };

        (double Cx, double Cy, double R, double Cr, double Cg, double Cb)[] discs =
        [
            (0.30, 0.35, 0.12, 230, 200, 60),
            (0.72, 0.60, 0.18, 40, 90, 170),
            (0.55, 0.20, 0.06, 250, 250, 245),
        ];

        for (var y = 0; y < height; y++)
        {
            var v = (double)y / height;
            for (var x = 0; x < width; x++)
            {
                var u = (double)x / width;

                var n = 0.0;
                var amp = 0.5;
                foreach (var lattice in lattices)
                {
                    n += amp * Sample(lattice, u, v);
                    amp *= 0.5;
                }

                // Sky above a slightly wavy horizon, textured ground below it.
                var horizon = 0.55 + 0.04 * Math.Sin(u * 9.0) + 0.05 * (n - 0.5);
                double r, g, b;
                if (v < horizon)
                {
                    r = 110 + 90 * v + 30 * (n - 0.5);
                    g = 150 + 70 * v + 30 * (n - 0.5);
                    b = 220 - 20 * v + 20 * (n - 0.5);
                }
                else
                {
                    var t = n * 1.6 - 0.3;
                    r = 70 + 90 * t;
                    g = 95 + 80 * t;
                    b = 40 + 50 * t;
                }

                foreach (var (cx, cy, rad, cr, cg, cb) in discs)
                {
                    var dx = (u - cx) * width;
                    var dy = (v - cy) * height;
                    var d = Math.Sqrt(dx * dx + dy * dy) - rad * height;
                    var cover = Math.Clamp(0.5 - d, 0.0, 1.0);
                    if (cover <= 0) continue;
                    var shade = 0.85 + 0.3 * (n - 0.5);
                    r = r * (1 - cover) + cr * shade * cover;
                    g = g * (1 - cover) + cg * shade * cover;
                    b = b * (1 - cover) + cb * shade * cover;
                }

                var i = (y * width + x) * 3;
                rgb[i + 0] = (byte)Math.Clamp((int)Math.Round(r), 0, 255);
                rgb[i + 1] = (byte)Math.Clamp((int)Math.Round(g), 0, 255);
                rgb[i + 2] = (byte)Math.Clamp((int)Math.Round(b), 0, 255);
            }
        }

        return rgb;
    }

    private sealed record Grid(int Size, double[] Values);

    private static Grid Lattice(uint seed, int size)
    {
        var values = new double[(size + 1) * (size + 1)];
        var s = seed * 2654435761u + 1;
        for (var i = 0; i < values.Length; i++)
        {
            s = s * 1664525u + 1013904223u;
            values[i] = (s >> 8) / (double)(1 << 24);
        }

        return new Grid(size, values);
    }

    private static double Sample(Grid grid, double u, double v)
    {
        var fx = u * grid.Size;
        var fy = v * grid.Size;
        var x0 = Math.Min((int)fx, grid.Size - 1);
        var y0 = Math.Min((int)fy, grid.Size - 1);
        var tx = Smooth(fx - x0);
        var ty = Smooth(fy - y0);
        var stride = grid.Size + 1;
        var a = grid.Values[y0 * stride + x0];
        var b = grid.Values[y0 * stride + x0 + 1];
        var c = grid.Values[(y0 + 1) * stride + x0];
        var d = grid.Values[(y0 + 1) * stride + x0 + 1];
        return (a + (b - a) * tx) * (1 - ty) + (c + (d - c) * tx) * ty;
    }

    private static double Smooth(double t) => t * t * (3 - 2 * t);
}
