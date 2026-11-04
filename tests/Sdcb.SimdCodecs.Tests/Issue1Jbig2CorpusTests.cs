using Sdcb.SimdCodecs.Jbig2;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The JBIG2 files attached to issue #1 (https://github.com/sdcb/SimdCodecs/issues/1),
/// every page of every one checked against jbig2dec.
/// <para>
/// <b>The files are not in the repository and must not be.</b> The reporter took them
/// from a test set whose source and licence they no longer know, and AGENTS.md admits
/// no third-party data of uncertain provenance, not even as a fetch-by-URL fixture.
/// So this test runs only when <c>SIMDCODECS_ISSUE1_DIR</c> names a directory holding
/// them, and skips otherwise (REQUIRE_ORACLES does not change that: this is an optional
/// corpus, not an oracle).
/// </para>
/// <para>
/// <b>After changing the JBIG2 decoder, run it.</b> Download
/// https://github.com/user-attachments/files/34160416/JPX_JBig2.zip (and, for the JPEG 2000
/// side, https://github.com/user-attachments/files/34161061/JPXDecode.zip), extract them
/// somewhere outside the repository, set <c>SIMDCODECS_ISSUE1_DIR</c> to that directory
/// (it is searched recursively for <c>*.jb2</c>), and run
/// <c>--filter "FullyQualifiedName~Issue1Jbig2Corpus"</c> with jbig2dec available.
/// </para>
/// <para>
/// Expected: all 114 files decode to jbig2dec's pages, except two. <c>042_13.jb2</c> and
/// <c>042_14.jb2</c> are the same page as the other <c>042_*</c> files, coded with a
/// refining symbol dictionary (arithmetic and Huffman respectively), and jbig2dec rejects
/// both at the first refined symbol of their second dictionary ("OOB in single
/// refinement/aggregate coded symbol data", "invalid number of symbols in aggregate
/// glyph"). This decoder, written independently from T.88, reads the same fields at the
/// same place and finds them malformed too: the first refinement of the arithmetic one
/// codes an out-of-band IARDX, which §6.5.8.2.2 does not allow. Two independent
/// readings agreeing that a stream is invalid is the best evidence available, so for
/// those two the expectation is a clean <see cref="InvalidDataException"/>. If a later
/// change makes them decode, they must decode to the page the other <c>042_*</c> files
/// give (jbig2dec's output for <c>042_1.jb2</c>).
/// </para>
/// <para>
/// Three files (<c>a.jb2</c>, <c>test.jb2</c>, <c>XeroxWorkCentre5230A_Huffman.jb2</c>)
/// are PDF-embedded streams with no file header; they go through
/// <see cref="Jbig2Decoder.Decode(ReadOnlySpan{byte}, int, int, CancellationToken)"/>
/// with the page size jbig2dec reports for them.
/// </para>
/// </summary>
public sealed class Issue1Jbig2CorpusTests(ITestOutputHelper output)
{
    private static readonly string[] RejectedByBoth = ["042_13", "042_14"];

    [Fact]
    public void EveryFile_DecodesToJbig2decsPages()
    {
        var directory = Environment.GetEnvironmentVariable("SIMDCODECS_ISSUE1_DIR");
        if (string.IsNullOrEmpty(directory))
            Assert.Skip("SIMDCODECS_ISSUE1_DIR is not set; see Issue1Jbig2CorpusTests for where the issue #1 files come from.");
        if (!Directory.Exists(directory))
            Assert.Fail($"SIMDCODECS_ISSUE1_DIR is '{directory}', which is not a directory.");

        Jbig2Oracle.RequireOrSkip();
        var ct = TestContext.Current.CancellationToken;

        var files = Directory.GetFiles(directory, "*.jb2", SearchOption.AllDirectories).Order().ToArray();
        files.ShouldNotBeEmpty($"no .jb2 files under {directory}");

        var failures = new List<string>();
        var byName = files.ToDictionary(f => Path.GetFileNameWithoutExtension(f), StringComparer.OrdinalIgnoreCase);

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(path);
            var data = File.ReadAllBytes(path);
            var isFile = data.AsSpan().StartsWith(Jbig2Decoder.FileSignature);

            try
            {
                if (RejectedByBoth.Contains(name))
                {
                    Jbig2Bitmap[]? ours = null;
                    try { ours = DecodeAll(data, ct); }
                    catch (InvalidDataException) { }

                    if (ours is not null)
                    {
                        // Decodes now: then it has to be the page its siblings give.
                        var reference = Jbig2Oracle.DecodePages(File.ReadAllBytes(byName["042_1"]));
                        Compare(name, ours, reference, failures);
                    }

                    output.WriteLine($"{name}: {(ours is null ? "rejected as invalid, as jbig2dec does" : "decoded")}");
                    continue;
                }

                var pages = Jbig2Oracle.DecodePages(data, embedded: !isFile);
                var decoded = isFile
                    ? DecodeAll(data, ct)
                    : [ToBitmap(Jbig2Decoder.Decode(data, pages[0].Width, pages[0].Height, ct))];

                Compare(name, decoded, pages, failures);
                output.WriteLine($"{name}: {decoded.Length} page(s) match");
            }
            catch (Exception e) when (e is InvalidDataException or NotSupportedException)
            {
                failures.Add($"{name}: {e.GetType().Name}: {e.Message}");
            }
        }

        failures.ShouldBeEmpty(string.Join('\n', failures));
    }

    private static Jbig2Bitmap[] DecodeAll(byte[] data, CancellationToken ct)
    {
        Jbig2Decoder.TryReadFileInfo(data, out _, out _, out var pages).ShouldBeTrue();
        var result = new Jbig2Bitmap[Math.Max(1, pages)];
        for (var p = 0; p < result.Length; p++) result[p] = ToBitmap(Jbig2Decoder.DecodeFile(data, p + 1, ct));
        return result;
    }

    private static Jbig2Bitmap ToBitmap(Jbig2Image image)
    {
        var bitmap = new Jbig2Bitmap(image.Width, image.Height);
        image.Bits.CopyTo(bitmap.Data);
        return bitmap;
    }

    private static void Compare(string name, Jbig2Bitmap[] ours, IReadOnlyList<Jbig2Oracle.Raster> reference, List<string> failures)
    {
        if (ours.Length != reference.Count)
        {
            failures.Add($"{name}: {ours.Length} page(s), jbig2dec {reference.Count}");
            return;
        }

        for (var p = 0; p < ours.Length; p++)
        {
            var (w, h, bits) = (reference[p].Width, reference[p].Height, reference[p].Bits);
            if (ours[p].Width != w || ours[p].Height != h)
            {
                failures.Add($"{name} page {p + 1}: {ours[p].Width}x{ours[p].Height}, jbig2dec {w}x{h}");
                continue;
            }

            var differing = 0;
            for (var i = 0; i < bits.Length; i++) if (bits[i] != ours[p].Data[i]) differing++;
            if (differing != 0) failures.Add($"{name} page {p + 1}: {differing} pixels differ from jbig2dec");
        }
    }
}
