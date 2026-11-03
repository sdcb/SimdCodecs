// Writes the HDR HEIF fixtures (Oracle/heif/README.md): raw 8-bit 4:2:0 Y'CbCr encoded by libheif + x265 as black
// boxes, tagged with an nclx colour profile. Only the shape of the public API is taken from libheif's headers
// (AGENTS.md); heif.dll and libx265.dll come from vcpkg. Usage:
//   nclx-encode <in.yuv> <width> <height> <primaries> <transfer> <matrix> <quality> <out.heic> [nclxbox]
// "nclxbox" also writes an nclx 'colr' box; without it libheif signals the colour in the HEVC VUI only.
using System.Runtime.InteropServices;

unsafe
{
    var (input, w, h) = (args[0], int.Parse(args[1]), int.Parse(args[2]));
    var (cp, tc, mc, q, output) = (ushort.Parse(args[3]), ushort.Parse(args[4]), ushort.Parse(args[5]), int.Parse(args[6]), args[7]);
    var yuv = File.ReadAllBytes(input);
    Console.WriteLine("libheif " + Marshal.PtrToStringUTF8((nint)N.heif_get_version()));

    var ctx = N.heif_context_alloc();
    N.Check(N.heif_context_get_encoder_for_format(ctx, 1 /* HEVC */, out var encoder));
    N.Check(N.heif_encoder_set_lossy_quality(encoder, q));
    N.Check(N.heif_image_create(w, h, 0 /* YCbCr */, 1 /* 420 */, out var image));
    int cw = (w + 1) / 2, ch = (h + 1) / 2;
    var offset = 0;
    for (var c = 0; c < 3; c++)
    {
        int pw = c == 0 ? w : cw, ph = c == 0 ? h : ch;
        N.Check(N.heif_image_add_plane(image, c, pw, ph, 8));
        var plane = N.heif_image_get_plane(image, c, out var stride);
        for (var y = 0; y < ph; y++)
            yuv.AsSpan(offset + y * pw, pw).CopyTo(new Span<byte>(plane + (long)y * stride, pw));
        offset += pw * ph;
    }
    var nclx = N.heif_nclx_color_profile_alloc();
    N.Check(N.heif_nclx_color_profile_set_color_primaries(nclx, cp));
    N.Check(N.heif_nclx_color_profile_set_transfer_characteristics(nclx, tc));
    N.Check(N.heif_nclx_color_profile_set_matrix_coefficients(nclx, mc));
    *(byte*)(nclx + 16) = 0; // full_range_flag: u8 version, three int enums at 4/8/12, then the flag at 16
    N.Check(N.heif_image_set_nclx_color_profile(image, nclx));
    nint options = 0;
    if (args.Length > 8 && args[8] == "nclxbox")
    {
        // heif_encoding_options: output_nclx_profile at 8, macOS_compatibility_workaround_no_nclx_profile at 16.
        options = N.heif_encoding_options_alloc();
        *(nint*)(options + 8) = nclx;
        *(byte*)(options + 16) = 0;
    }
    N.Check(N.heif_context_encode_image(ctx, image, encoder, options, out var handle));
    N.Check(N.heif_context_write_to_file(ctx, output));
    Console.WriteLine($"wrote {output}");
}

static unsafe partial class N
{
    const string Lib = "heif";
    [StructLayout(LayoutKind.Sequential)] public struct Err { public int Code, Sub; public nint Message; }
    public static void Check(Err e) { if (e.Code != 0) throw new Exception(Marshal.PtrToStringUTF8(e.Message)); }
    [LibraryImport(Lib)] public static partial byte* heif_get_version();
    [LibraryImport(Lib)] public static partial nint heif_context_alloc();
    [LibraryImport(Lib)] public static partial Err heif_context_get_encoder_for_format(nint ctx, int format, out nint encoder);
    [LibraryImport(Lib)] public static partial Err heif_encoder_set_lossy_quality(nint encoder, int quality);
    [LibraryImport(Lib)] public static partial Err heif_image_create(int w, int h, int colorspace, int chroma, out nint image);
    [LibraryImport(Lib)] public static partial Err heif_image_add_plane(nint image, int channel, int w, int h, int bitDepth);
    [LibraryImport(Lib)] public static partial byte* heif_image_get_plane(nint image, int channel, out int stride);
    [LibraryImport(Lib)] public static partial nint heif_nclx_color_profile_alloc();
    [LibraryImport(Lib)] public static partial nint heif_encoding_options_alloc();
    [LibraryImport(Lib)] public static partial Err heif_nclx_color_profile_set_color_primaries(nint nclx, ushort v);
    [LibraryImport(Lib)] public static partial Err heif_nclx_color_profile_set_transfer_characteristics(nint nclx, ushort v);
    [LibraryImport(Lib)] public static partial Err heif_nclx_color_profile_set_matrix_coefficients(nint nclx, ushort v);
    [LibraryImport(Lib)] public static partial Err heif_image_set_nclx_color_profile(nint image, nint nclx);
    [LibraryImport(Lib)] public static partial Err heif_context_encode_image(nint ctx, nint image, nint encoder, nint options, out nint handle);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] public static partial Err heif_context_write_to_file(nint ctx, string path);
}
