using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Heif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Configurable HEIF fuzz soak (AGENTS.md: run it at <c>HEIF_FUZZ_ITERATIONS=300000</c> after touching the HEVC
/// entropy decoder or the container parser). Every mutated file goes through <see cref="HeifDecoder"/> directly —
/// it must decode or throw <see cref="InvalidDataException"/> — and through the facade with the HEIF codec
/// registered, whose <c>Try*</c> entry points must return a value and never throw. Each case has a time limit, and
/// the loop as a whole a budget, so a hang shows as the case that caused it.
/// </summary>
/// <remarks>
/// Mutations aim at both layers. The container: the size field of a box anywhere in the tree, and fields inside
/// <c>iloc</c>, <c>ispe</c>, <c>idat</c> (a phone's grid header), <c>hvcC</c> (the parameter sets), <c>ipma</c>,
/// <c>iinf</c> / <c>infe</c>, <c>iref</c>, <c>pitm</c>, <c>clap</c>, <c>irot</c>, <c>imir</c> and <c>colr</c>. The HEVC
/// payload: bytes inside <c>mdat</c>, half of them near the start of the slice data (slice header and the first
/// CTUs' CABAC state), plus zeroed runs and cuts. A tenth of the cases are generic (flips, inserts, deletes
/// anywhere). The count scales with <c>HEIF_FUZZ_ITERATIONS</c> (default 4000) and the seed with
/// <c>HEIF_FUZZ_SEED</c>; a failure prints the recipe (fixture, iteration, mutation) that reproduces it.
/// </remarks>
[Collection(FacadeRegistryCollection.Name)]
public sealed class HeifFuzzTests
{
    private static int Iterations => int.TryParse(Environment.GetEnvironmentVariable("HEIF_FUZZ_ITERATIONS"), out var n) ? n : 4000;

    private static int Seed => int.TryParse(Environment.GetEnvironmentVariable("HEIF_FUZZ_SEED"), out var n) ? n : 20261103;

    /// <summary>Seeds and their weights: the small single-item files carry most of the load, the 48-tile phone
    /// grid a few percent (each of its decodes is ~12 MP).</summary>
    private static readonly (string Name, int Weight)[] SeedFixtures =
    [
        ("synth-screen-testsrc2-640x360-pq-vui.heic", 30),
        ("synth-screen-testsrc2-640x360-hlg-nclx.heic", 30),
        ("synth-screen-testsrc2-1280x720-main8.heic", 15),
        ("synth-photo-mangrove-1599x1199-main8.heic", 8),  // clap, odd size
        ("synth-photo-fuji-1600x900-alpha.heic", 8),       // auxiliary alpha item
        ("synth-screen-testsrc2-1280x720-444.heic", 5),    // rejected profile: SPS / range-extension paths
        ("hevc-main-3072x4096-grid.heic", 4),              // grid in idat, 48 tiles, ICC, Exif
    ];

    private static readonly TimeSpan CaseLimit = TimeSpan.FromSeconds(10);

    private sealed record FuzzSeed(string Name, byte[] Data, List<Box> Boxes, int MdatStart, int MdatEnd);

    /// <summary>A box: offset of its size field, header length, end (exclusive), type.</summary>
    private sealed record Box(string Type, int Start, int HeaderSize, int End)
    {
        public int PayloadStart => Start + HeaderSize;
    }

    [Fact]
    public async Task Mutations_decode_or_fail_cleanly()
    {
        var seeds = SeedFixtures.Select(s => (Seed: LoadSeed(s.Name), s.Weight)).ToList();
        var totalWeight = seeds.Sum(s => s.Weight);
        var iterations = Iterations;
        var budget = TimeSpan.FromSeconds(Math.Max(120, iterations / 10));
        var current = "";
        var builtIn = SimdCodecs.RegistrySnapshot;
        SimdCodecs.Register(HeifImageDecoder.Registration);
        try
        {
            var run = Task.Run(() =>
            {
                var random = new Random(Seed);
                for (var i = 0; i < iterations; i++)
                {
                    var pick = random.Next(totalWeight);
                    var seed = seeds.First(s => (pick -= s.Weight) < 0).Seed;
                    var data = Mutate(seed, random, out var description);
                    current = $"HEIF_FUZZ_SEED={Seed} #{i} {seed.Name}: {description}";
                    DecodeOne(data, current, random.Next(5));
                }
            }, TestContext.Current.CancellationToken);
            if (await Task.WhenAny(run, Task.Delay(budget, TestContext.Current.CancellationToken)) != run)
                throw new ShouldAssertException($"The fuzz loop is stuck at {current}");
            await run;
        }
        finally
        {
            SimdCodecs.ExchangeRegistry(builtIn);
        }
    }

    /// <summary>
    /// Cases the soak found, as recipes over a downloaded fixture (the derived files are not committed):
    /// <c>patch</c> writes the hex bytes at the offset, <c>cut</c> keeps the first offset bytes, <c>delete</c>
    /// removes hex-length (a decimal count) bytes at the offset.
    /// </summary>
    [Theory]
    [InlineData("synth-screen-testsrc2-640x360-pq-vui.heic", "patch", 96, "FFFFFFFF")] // iloc extent field read past the box
    [InlineData("synth-screen-testsrc2-640x360-hlg-nclx.heic", "patch", 256, "0502016D")] // hvcC: soak #37002
    public void Regressions_fail_cleanly(string fixture, string operation, int offset, string argument)
    {
        var data = HeifTests.ReadFixture(fixture);
        data = operation switch
        {
            "patch" => Patch(data, offset, Convert.FromHexString(argument)),
            "cut" => data[..offset],
            "delete" => [.. data[..offset], .. data[(offset + int.Parse(argument))..]],
            _ => throw new ArgumentException(operation),
        };
        var builtIn = SimdCodecs.RegistrySnapshot;
        SimdCodecs.Register(HeifImageDecoder.Registration);
        try
        {
            for (var layout = 0; layout < 5; layout++) DecodeOne(data, $"{fixture} {operation} {offset} {argument}", layout);
        }
        finally
        {
            SimdCodecs.ExchangeRegistry(builtIn);
        }

        static byte[] Patch(byte[] data, int offset, byte[] bytes)
        {
            bytes.CopyTo(data, offset);
            return data;
        }
    }

    /// <summary>
    /// The grid geometry is checked from the <c>hvcC</c> parameter sets before any tile is decoded or the canvas
    /// allocated: a grid whose output is one sample wider than its tiles cover fails in the header read.
    /// </summary>
    [Fact]
    public void GridGeometry_is_checked_before_decoding()
    {
        var data = HeifTests.ReadFixture("hevc-main-3072x4096-grid.heic");
        HeifDecoder.ReadInfo(data).Width.ShouldBe(3072);
        // ImageGrid in 'idat': version, flags (bit 0: 32-bit sizes), rows - 1, columns - 1, output width, height.
        var idat = data.AsSpan().IndexOf("idat"u8) + 4;
        (data[idat + 1] & 1).ShouldBe(0);
        (data[idat + 3] + 1).ShouldBe(6, "six columns of 512");
        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(idat + 4)).ShouldBe((ushort)3072);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(idat + 4), 3073);
        var timer = Stopwatch.StartNew();
        Should.Throw<InvalidDataException>(() => HeifDecoder.ReadInfo(data)).Message.ShouldContain("do not cover");
        Should.Throw<InvalidDataException>(() => HeifDecoder.Decode(data));
        timer.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1), "refused before the tiles are decoded");
    }

    /// <summary>Decodes one candidate through <see cref="HeifDecoder"/> and through the facade.</summary>
    internal static void DecodeOne(byte[] data, string context, int layout)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            HeifDecoder.ReadInfo(data);
            using var image = HeifDecoder.Decode(data);
            image.Rgba.Length.ShouldBe(image.Width * image.Height * 4, context);
        }
        catch (InvalidDataException)
        {
        }
        catch (Exception e) when (e is not ShouldAssertException)
        {
            throw new ShouldAssertException($"{context}: HeifDecoder: {e.GetType().Name}: {e.Message}", e);
        }

        timer.Elapsed.ShouldBeLessThan(CaseLimit, $"{context} (HeifDecoder)");
        timer.Restart();
        try
        {
            if (SimdCodecs.TryReadInfo(data, out var info))
            {
                info.Format.ShouldBe(ImageFormat.Heif, context);
                var format = PixelConverterTests.Formats[layout];
                var destination = new byte[(long)info.Width * info.Height * format.BytesPerPixel];
                if (SimdCodecs.TryDecodeInto(data, destination, 0, format) && SimdCodecs.TryDecode(data, out var decoded))
                {
                    (decoded.Width, decoded.Height).ShouldBe((info.Width, info.Height), context);
                    // The scaled path (tiles boxed as they are written, or the fallback), into the same format.
                    var scale = new DecodeOptions { ScaleDenominator = 2 << layout % 3 };
                    var (sw, sh) = scale.GetScaledSize(info.Width, info.Height);
                    if (SimdCodecs.TryDecodeInto(data, destination, 0, format, scale) && SimdCodecs.TryDecode(data, out var scaled, scale))
                    {
                        using (scaled)
                            (scaled.Width, scaled.Height).ShouldBe((sw, sh), context);
                    }
                }
            }
            else
            {
                SimdCodecs.TryDecode(data, out _);
            }
        }
        catch (Exception e) when (e is not ShouldAssertException)
        {
            throw new ShouldAssertException($"{context}: facade: {e.GetType().Name}: {e.Message}", e);
        }

        timer.Elapsed.ShouldBeLessThan(CaseLimit * 2, $"{context} (facade)");
    }

    // ───────────────────────────── mutations ─────────────────────────────

    private static readonly string[] FieldBoxes =
        ["iloc", "ispe", "idat", "hvcC", "ipma", "infe", "iinf", "dimg", "iref", "pitm", "clap", "irot", "imir", "colr", "ftyp", "auxC", "pixi"];

    private static byte[] Mutate(FuzzSeed seed, Random random, out string description)
    {
        var data = seed.Data.ToArray();
        var roll = random.Next(20);
        if (roll < 4)
            return MutateBoxSize(data, seed, random, out description);
        if (roll < 9)
            return MutateField(data, seed, random, out description);
        if (roll < 18 && seed.MdatEnd > seed.MdatStart)
            return MutatePayload(data, seed, random, out description);
        return MutateAnywhere(data, random, out description);
    }

    private static byte[] MutateBoxSize(byte[] data, FuzzSeed seed, Random random, out string description)
    {
        var box = seed.Boxes[random.Next(seed.Boxes.Count)];
        var size = (uint)(box.End - box.Start);
        uint value = random.Next(6) switch
        {
            0 => 0,
            1 => 1,
            2 => uint.MaxValue,
            3 => (uint)random.Next(data.Length + 64),
            4 => size + (uint)random.Next(1, 17),
            _ => size - (uint)Math.Min(size, (uint)random.Next(1, 17)),
        };
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(box.Start), value);
        description = $"'{box.Type}' at {box.Start}: size {size} -> {value}";
        return data;
    }

    private static byte[] MutateField(byte[] data, FuzzSeed seed, Random random, out string description)
    {
        var candidates = seed.Boxes.Where(b => FieldBoxes.Contains(b.Type) && b.End > b.PayloadStart).ToList();
        if (candidates.Count == 0) return MutateAnywhere(data, random, out description);
        var box = candidates[random.Next(candidates.Count)];
        var length = box.End - box.PayloadStart;
        var width = random.Next(3) switch { 0 => 1, 1 => 2, _ => 4 };
        if (width > length) width = 1;
        var at = box.PayloadStart + random.Next(length - width + 1);
        if (box.Type == "ispe" && length >= 12)
        {
            // version/flags, then width and height: hit the dimensions on purpose.
            at = box.PayloadStart + 4 + 4 * random.Next(2);
            width = 4;
        }

        ulong value = random.Next(6) switch
        {
            0 => 0,
            1 => ulong.MaxValue,
            2 => 1UL << (8 * width - 1),
            3 => (ulong)random.NextInt64(),
            4 => (ulong)data.Length,
            _ => ReadField(data, at, width) + (ulong)random.Next(-4, 5),
        };
        WriteField(data, at, width, value);
        description = $"'{box.Type}' field at {at} ({width} bytes) = 0x{ReadField(data, at, width):X}";
        return data;
    }

    private static byte[] MutatePayload(byte[] data, FuzzSeed seed, Random random, out string description)
    {
        var span = seed.MdatEnd - seed.MdatStart;
        // Half near the start of an item's data: NAL headers, slice header, the first CTUs' CABAC state.
        var at = seed.MdatStart + (random.Next(2) == 0 ? random.Next(Math.Min(span, 512)) : random.Next(span));
        switch (random.Next(6))
        {
            case 0:
                data[at] ^= (byte)(1 << random.Next(8));
                description = $"mdat bit flip at {at}";
                return data;
            case 1:
                data[at] = (byte)random.Next(256);
                description = $"mdat byte {at} = 0x{data[at]:X2}";
                return data;
            case 2:
            {
                var n = random.Next(2, 17);
                var where = new List<int>();
                for (var k = 0; k < n; k++)
                {
                    var p = seed.MdatStart + random.Next(span);
                    data[p] ^= (byte)(1 << random.Next(8));
                    where.Add(p);
                }

                description = $"mdat {n} flips at {string.Join(',', where)}";
                return data;
            }
            case 3:
            {
                var run = Math.Min(random.Next(1, 256), seed.MdatEnd - at);
                var fill = random.Next(2) == 0 ? (byte)0 : (byte)0xFF;
                data.AsSpan(at, run).Fill(fill);
                description = $"mdat {run} bytes at {at} = 0x{fill:X2}";
                return data;
            }
            case 4:
                description = $"cut to {at} (inside mdat)";
                return data[..at];
            default:
            {
                var length = Math.Min(random.Next(1, 64), seed.MdatEnd - at);
                description = $"mdat {length} bytes deleted at {at}";
                return [.. data[..at], .. data[(at + length)..]];
            }
        }
    }

    private static byte[] MutateAnywhere(byte[] data, Random random, out string description)
    {
        var at = random.Next(data.Length);
        switch (random.Next(5))
        {
            case 0:
                data[at] ^= (byte)(1 << random.Next(8));
                description = $"bit flip at {at}";
                return data;
            case 1:
                data[at] = (byte)random.Next(256);
                description = $"byte {at} = 0x{data[at]:X2}";
                return data;
            case 2:
                description = $"cut to {at}";
                return data[..at];
            case 3:
            {
                var n = random.Next(1, 64);
                description = $"{n} zero bytes inserted at {at}";
                return [.. data[..at], .. new byte[n], .. data[at..]];
            }
            default:
            {
                var length = Math.Min(random.Next(1, 32), data.Length - at);
                description = $"{length} bytes deleted at {at}";
                return [.. data[..at], .. data[(at + length)..]];
            }
        }
    }

    private static ulong ReadField(byte[] data, int at, int width) => width switch
    {
        1 => data[at],
        2 => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(at)),
        _ => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(at)),
    };

    private static void WriteField(byte[] data, int at, int width, ulong value)
    {
        switch (width)
        {
            case 1: data[at] = (byte)value; break;
            case 2: BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at), (ushort)value); break;
            default: BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at), (uint)value); break;
        }
    }

    // ───────────────────────────── seeds ─────────────────────────────

    private static FuzzSeed LoadSeed(string name)
    {
        var data = HeifTests.ReadFixture(name);
        var boxes = new List<Box>();
        Walk(data, 0, data.Length, boxes, 0);
        var mdat = boxes.FirstOrDefault(b => b.Type == "mdat");
        return new FuzzSeed(name, data, boxes, mdat?.PayloadStart ?? 0, mdat?.End ?? 0);
    }

    /// <summary>The box tree, as far as the mutations need it (ISO/IEC 14496-12 §4.2; HEIF's item boxes).</summary>
    private static void Walk(byte[] data, int start, int end, List<Box> boxes, int depth)
    {
        var p = start;
        while (p + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p));
            var type = Encoding.ASCII.GetString(data, p + 4, 4);
            var header = 8;
            if (size == 1)
            {
                if (p + 16 > end) return;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(p + 8));
                header = 16;
            }
            else if (size == 0)
            {
                size = end - p;
            }

            if (size < header || p + size > end) return;
            var boxEnd = (int)(p + size);
            boxes.Add(new Box(type, p, header, boxEnd));
            var payload = p + header;
            int children = type switch
            {
                "meta" or "iref" => payload + 4,                 // full box
                "iprp" or "ipco" or "dinf" => payload,
                "iinf" when boxEnd > payload => payload + (data[payload] == 0 ? 6 : 8), // full box + entry count
                _ => -1,
            };
            if (children >= 0 && depth < 6) Walk(data, children, boxEnd, boxes, depth + 1);
            p = boxEnd;
        }
    }
}
