using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Avif;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// Configurable AVIF fuzz soak (run at <c>AVIF_FUZZ_ITERATIONS=300000</c> after touching the AV1 entropy decoder
/// or ISOBMFF/AVIF container parsers). Every mutated file goes through <see cref="AvifDecoder"/> directly —
/// it must decode or throw <see cref="InvalidDataException"/> — and through the <see cref="ImageCodecs"/> facade,
/// whose <c>Try*</c> entry points must return a value and never throw.
/// </summary>
public sealed class AvifFuzzTests
{
    private static int Iterations => int.TryParse(Environment.GetEnvironmentVariable("AVIF_FUZZ_ITERATIONS"), out var n) ? n : 100;
    private static int Seed => int.TryParse(Environment.GetEnvironmentVariable("AVIF_FUZZ_SEED"), out var n) ? n : 20261127;

    private static readonly (string Name, int Weight)[] SeedFixtures =
    [
        ("container-tiny-1x1.avif", 35),
        ("container-tiny-3x7.avif", 35),
        ("screen-text-1280x720-palette-intrabc.avif", 8),
        ("screen-ui-1920x1080-p1-8b-444-gbr.avif", 3),
        ("screen-ui-1920x1080-p1-10b-444-bt709.avif", 2),
        ("screen-testsrc2-1280x720-lr-wiener-sgr.avif", 5),
        ("color-testsrc2-1280x720-pq-10b-nclx.avif", 3),
        ("container-iovl-overlay-800x600.avif", 3),
        ("container-iden-chain-640x480.avif", 2),
        ("container-grid-alpha-1024x768-prem.avif", 1),
        ("photo-night-1280x720-filmgrain-8b.avif", 1),
        ("color-fuji-1280x720-gainmap-tmap.avif", 1),
        ("photo-mangrove-1600x1200-superres.avif", 1),
    ];

    private static readonly TimeSpan CaseLimit = TimeSpan.FromSeconds(15);

    private static readonly string[] FieldBoxes =
        ["iloc", "ispe", "idat", "av1C", "ipma", "infe", "iinf", "dimg", "iref", "pitm", "clap", "irot", "imir", "colr", "ftyp", "auxC", "pixi", "clli", "a1lx", "a1op"];

    private sealed record Box(string Type, int Start, int HeaderSize, int End)
    {
        public int PayloadStart => Start + HeaderSize;
    }

    private sealed record FuzzSeed(string Name, byte[] Data, List<Box> Boxes, int MdatStart, int MdatEnd);

    [Fact]
    public async Task Mutations_decode_or_fail_cleanly()
    {
        var seeds = SeedFixtures.Select(s => (Seed: LoadSeed(s.Name), s.Weight)).ToList();
        int totalWeight = seeds.Sum(s => s.Weight);
        int iterations = Iterations;
        int workers = iterations >= 1000 ? Math.Max(1, Environment.ProcessorCount) : 1;
        var budget = TimeSpan.FromSeconds(Math.Max(180, iterations / 5));
        string? lastContext = null;

        var run = Task.Run(() =>
        {
            Parallel.For(0, workers, new ParallelOptions { MaxDegreeOfParallelism = workers }, worker =>
            {
                int start = (int)((long)iterations * worker / workers);
                int end = (int)((long)iterations * (worker + 1) / workers);
                var random = new Random(unchecked(Seed * 397 ^ worker));
                for (int i = start; i < end; i++)
                {
                    int pick = random.Next(totalWeight);
                    FuzzSeed seed = seeds[0].Seed;
                    foreach (var s in seeds)
                    {
                        pick -= s.Weight;
                        if (pick < 0) { seed = s.Seed; break; }
                    }
                    byte[] data = Mutate(seed, random, out string description);
                    string ctx = $"AVIF_FUZZ_SEED={Seed} worker={worker} #{i} {seed.Name}: {description}";
                    Volatile.Write(ref lastContext, ctx);
                    DecodeOne(data, ctx, random.Next(5));
                }
            });
        }, TestContext.Current.CancellationToken);

        if (await Task.WhenAny(run, Task.Delay(budget, TestContext.Current.CancellationToken)) != run)
            throw new ShouldAssertException($"AVIF fuzz loop timed out near: {Volatile.Read(ref lastContext)}");
        await run;
    }

    [Theory]
    [InlineData("screen-ui-1920x1080-p1-8b-444-gbr.avif", 281, 0x46)]
    [InlineData("container-grid-alpha-1024x768-prem.avif", 894, 0x43)]
    public void Regressions_fail_cleanly(string fixture, int offset, byte value)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "avif", fixture);
        if (!File.Exists(path)) Assert.Skip($"{fixture} is not downloaded; run Oracle/avif/fetch.sh.");
        byte[] data = File.ReadAllBytes(path);
        data[offset] = value;
        for (int layout = 0; layout < 5; layout++)
            DecodeOne(data, $"{fixture} byte {offset} = 0x{value:X2}", layout);
    }

    internal static void DecodeOne(byte[] data, string context, int layout)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            AvifDecoder.ReadInfo(data);
            using var image = AvifDecoder.Decode(data);
            image.Rgba.Length.ShouldBe(image.Width * image.Height * 4, context);
        }
        catch (InvalidDataException)
        {
        }
        catch (Exception e) when (e is not ShouldAssertException)
        {
            throw new ShouldAssertException($"{context}: AvifDecoder: {e.GetType().Name}: {e.Message}", e);
        }

        timer.Elapsed.ShouldBeLessThan(CaseLimit, $"{context} (AvifDecoder)");
        timer.Restart();
        try
        {
            if (ImageCodecs.TryReadInfo(data, out var info))
            {
                ImageCodecs.TryReadMetadata(data, out _);
                ImageCodecs.TryDecodeThumbnail(data, out var thumb);
                thumb?.Dispose();
                ImageCodecs.TryReadPlanarInfo(data, out _);

                if ((long)info.Width * info.Height <= 512 * 512)
                {
                    if (ImageCodecs.TryDecode(data, out var decoded))
                    {
                        using (decoded)
                            (decoded.Width, decoded.Height).ShouldBe((info.Width, info.Height), context);
                    }
                    var format = PixelConverterTests.Formats[layout];
                    var dst = new byte[(long)info.Width * info.Height * format.BytesPerPixel];
                    ImageCodecs.TryDecodeInto(data, dst, 0, format);
                }
            }
            else
            {
                if (ImageCodecs.TryDecode(data, out var img))
                    img.Dispose();
            }
        }
        catch (Exception e) when (e is not ShouldAssertException)
        {
            throw new ShouldAssertException($"{context}: facade: {e.GetType().Name}: {e.Message}", e);
        }

        timer.Elapsed.ShouldBeLessThan(CaseLimit * 2, $"{context} (facade)");
    }

    private static byte[] Mutate(FuzzSeed seed, Random random, out string description)
    {
        byte[] data = seed.Data.ToArray();
        int roll = random.Next(20);
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
        uint size = (uint)(box.End - box.Start);
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
        int length = box.End - box.PayloadStart;
        int width = random.Next(3) switch { 0 => 1, 1 => 2, _ => 4 };
        if (width > length) width = 1;
        int at = box.PayloadStart + random.Next(length - width + 1);
        if (box.Type == "ispe" && length >= 12)
        {
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
        int span = seed.MdatEnd - seed.MdatStart;
        // Half near start of mdat (OBU headers, sequence header, frame header, first tile symbols)
        int at = seed.MdatStart + (random.Next(2) == 0 ? random.Next(Math.Min(span, 256)) : random.Next(span));
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
                int run = Math.Min(random.Next(1, 32), seed.MdatEnd - at);
                data.AsSpan(at, run).Clear();
                description = $"mdat zero {run} bytes at {at}";
                return data;
            case 3:
                int run2 = Math.Min(random.Next(1, 16), seed.MdatEnd - at);
                for (int k = 0; k < run2; k++) data[at + k] = (byte)random.Next(256);
                description = $"mdat random {run2} bytes at {at}";
                return data;
            case 4:
                description = $"truncate at {at}";
                return data[..at];
            default:
                int del = Math.Min(random.Next(1, 16), data.Length - at);
                description = $"delete {del} bytes at {at}";
                return [.. data[..at], .. data[(at + del)..]];
        }
    }

    private static byte[] MutateAnywhere(byte[] data, Random random, out string description)
    {
        int at = random.Next(data.Length);
        switch (random.Next(4))
        {
            case 0:
                data[at] ^= (byte)(1 << random.Next(8));
                description = $"flip bit at {at}";
                return data;
            case 1:
                int len = random.Next(Math.Min(data.Length, 256));
                description = $"truncate to {len}";
                return data[..len];
            case 2:
                int del = Math.Min(random.Next(1, 24), data.Length - at);
                description = $"delete {del} at {at}";
                return [.. data[..at], .. data[(at + del)..]];
            default:
                int ins = random.Next(1, 16);
                byte[] extra = new byte[ins];
                random.NextBytes(extra);
                description = $"insert {ins} at {at}";
                return [.. data[..at], .. extra, .. data[at..]];
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
        if (width == 1) data[at] = (byte)value;
        else if (width == 2) BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(at), (ushort)value);
        else BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at), (uint)value);
    }

    private static FuzzSeed LoadSeed(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "avif", name);
        if (!File.Exists(path))
            Assert.Skip($"{name} is not downloaded; run Oracle/avif/fetch.sh.");
        byte[] data = File.ReadAllBytes(path);
        var boxes = new List<Box>();
        int mdatStart = 0, mdatEnd = 0;
        ScanBoxes(data, 0, data.Length, boxes, ref mdatStart, ref mdatEnd);
        return new FuzzSeed(name, data, boxes, mdatStart, mdatEnd);
    }

    private static void ScanBoxes(byte[] data, int start, int end, List<Box> boxes, ref int mdatStart, ref int mdatEnd)
    {
        int pos = start;
        while (pos + 8 <= end)
        {
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            int hdr = 8;
            long size = size32;
            if (size == 1 && pos + 16 <= end)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8));
                hdr = 16;
            }
            else if (size == 0)
            {
                size = end - pos;
            }
            if (size < hdr || pos + size > end) break;
            int boxEnd = (int)(pos + size);
            string type = Encoding.ASCII.GetString(data, pos + 4, 4);
            boxes.Add(new Box(type, pos, hdr, boxEnd));
            if (type == "mdat" && mdatEnd == 0)
            {
                mdatStart = pos + hdr;
                mdatEnd = boxEnd;
            }
            if (type is "meta" or "iprp" or "ipco" or "iinf" or "iref" or "grpl")
            {
                int sub = pos + hdr;
                if (type is "meta" or "iref") sub += 4;
                else if (type == "iinf" && sub < boxEnd) sub += data[sub] == 0 ? 6 : 8;
                if (sub < boxEnd)
                    ScanBoxes(data, sub, boxEnd, boxes, ref mdatStart, ref mdatEnd);
            }
            pos = boxEnd;
        }
    }
}
