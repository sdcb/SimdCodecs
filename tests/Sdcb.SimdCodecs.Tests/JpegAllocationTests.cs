using System.Reflection;
using Sdcb.SimdCodecs.Jpeg;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="JpegDecoder.DecodeTo(ReadOnlySpan{byte}, Span{byte}, JpegScale)"/> into a
/// caller buffer allocates nothing on the managed heap once warm: buffers come from
/// <see cref="System.Buffers.ArrayPool{T}"/> and the tables from a per-thread cached
/// decoder state. That state is reset to "freshly constructed" on every rent; the
/// reflection checks below keep the hand-written resets complete as fields are added.
/// </summary>
public sealed class JpegAllocationTests
{
    public static TheoryData<string> Inputs() => new() { "4:2:0", "4:4:4 progressive", "gray", "cmyk", "writer restart", "writer non-interleaved" };

    private static byte[] Build(string name)
    {
        const int w = 97, h = 53;
        var rgb = new byte[w * h * 3];
        for (var i = 0; i < rgb.Length; ++i)
            rgb[i] = (byte)(i * 31 % 253);
        byte Sample(int k, int x, int y) => rgb[(y * w + x) * 3 + Math.Min(k, 2)];
        TestJpegWriter.Component[] ycc = [new(1, 2, 2), new(2, 1, 1), new(3, 1, 1)];
        return name switch
        {
            "4:2:0" => JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 80, "4:2:0"),
            "4:4:4 progressive" => JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 80, "4:4:4", progressive: true),
            "gray" => JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 80, gray: true),
            "cmyk" => JpegDecoderOracleTests.EncodeJpeg(rgb, w, h, 80, cmyk: true),
            "writer restart" => TestJpegWriter.Encode(w, h, ycc, Sample, new() { RestartInterval = 3 }),
            _ => TestJpegWriter.Encode(w, h, ycc, Sample, new() { Interleaved = false }),
        };
    }

    [Theory]
    [MemberData(nameof(Inputs))]
    public void DecodeTo_AllocatesNothing(string name)
    {
        var jpeg = Build(name);
        var info = JpegDecoder.ReadInfo(jpeg);
        var rgba = new byte[info.Width * info.Height * 4];
        var rgb = new byte[info.Width * info.Height * 3];
        for (var i = 0; i < 3; ++i)
        {
            JpegDecoder.DecodeTo(jpeg, rgba);
            JpegDecoder.DecodeTo(jpeg, rgb, info.Width * 3, JpegPixelFormat.Rgb24);
            JpegDecoder.DecodeTo(jpeg, rgba, JpegScale.Quarter);
        }

        // ArrayPool.Shared keeps one array per size in a thread-local slot and the rest in
        // per-core stacks other threads may drain under parallel test load; a rent that
        // misses allocates. The decoder itself allocates nothing, so the best of a few
        // attempts must be exactly zero.
        var best = long.MaxValue;
        for (var attempt = 0; attempt < 8 && best != 0; ++attempt)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            JpegDecoder.DecodeTo(jpeg, rgba);
            JpegDecoder.DecodeTo(jpeg, rgb, info.Width * 3, JpegPixelFormat.Rgb24);
            JpegDecoder.DecodeTo(jpeg, rgba, JpegScale.Quarter);
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        best.ShouldBe(0, $"{name}: managed bytes allocated by three warm decodes");
    }

    [Fact]
    public void CachedState_DecodesLikeFresh()
    {
        // Image A defines tables B never does; B must not see them.
        var a = Build("cmyk");
        var b = Build("4:2:0");
        var freshB = JpegDecoder.Decode(b).Pixels;
        JpegDecoder.Decode(a);
        JpegDecoder.Decode(b).Pixels.ShouldBe(freshB);
    }

    private static readonly Type Core = typeof(JpegDecoder).Assembly.GetType("Sdcb.SimdCodecs.Jpeg.JpegCore", throwOnError: true)!;

    [Fact]
    public void ComponentReset_RestoresEveryField()
    {
        var type = Core.GetNestedType("Component", BindingFlags.NonPublic)!;
        AssertResetRestores(type);
    }

    [Fact]
    public void HuffmanReset_RestoresEveryField()
    {
        var type = typeof(JpegDecoder).Assembly.GetType("Sdcb.SimdCodecs.Jpeg.JpegHuffman", throwOnError: true)!;
        AssertResetRestores(type);
    }

    private static void AssertResetRestores(Type type)
    {
        var fresh = Activator.CreateInstance(type, nonPublic: true)!;
        var dirty = Activator.CreateInstance(type, nonPublic: true)!;
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        fields.Length.ShouldBeGreaterThan(3);
        foreach (var f in fields)
        {
            var t = f.FieldType;
            if (t == typeof(int))
                f.SetValue(dirty, 7);
            else if (t.IsArray && f.IsInitOnly)
                Fill((Array)f.GetValue(dirty)!);
            else if (t.IsArray)
                f.SetValue(dirty, Array.CreateInstance(t.GetElementType()!, 3));
            else if (!t.IsValueType)
                f.SetValue(dirty, Activator.CreateInstance(t, nonPublic: true));
            else
                throw new InvalidOperationException($"{type.Name}.{f.Name}: teach this test about {t.Name}");
        }

        type.GetMethod("Reset")!.Invoke(dirty, null);

        foreach (var f in fields)
        {
            object? expected = f.GetValue(fresh), actual = f.GetValue(dirty);
            if (expected is Array ea)
                ((Array)actual!).Cast<object>().ShouldBe(ea.Cast<object>(), $"{type.Name}.{f.Name}");
            else
                actual.ShouldBe(expected, $"{type.Name}.{f.Name}");
        }
    }

    private static void Fill(Array a)
    {
        for (var i = 0; i < a.Length; ++i)
            a.SetValue(Convert.ChangeType(7, a.GetType().GetElementType()!), i);
    }
}
