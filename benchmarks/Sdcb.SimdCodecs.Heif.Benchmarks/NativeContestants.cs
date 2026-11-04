using Sdcb.SimdCodecs.Heif;

namespace Sdcb.SimdCodecs.Heif.Benchmarks;

/// <summary>One decoded picture's three planes, tightly packed, as a contestant returns them.</summary>
internal sealed class PlaneSet
{
    public required ushort[] Y { get; init; }
    public required ushort[] Cb { get; init; }
    public required ushort[] Cr { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int BitDepth { get; init; }

    public int ChromaWidth => (Width + 1) / 2;
    public int ChromaHeight => (Height + 1) / 2;
}

/// <summary>
/// The native contestants, in this process: libheif over libde265 at the full pipeline, and libde265
/// alone at the HEVC stage. Both are black boxes reached through their public headers.
/// </summary>
internal static unsafe class NativeContestants
{
    /// <summary>
    /// libheif → RGBA8 on the display canvas (transforms applied), the peer of
    /// <see cref="HeifDecoder.Decode"/>.
    /// </summary>
    /// <param name="maxThreads">
    /// Passed to <c>heif_context_set_max_decoding_threads</c>: 0 decodes grid tiles on the calling
    /// thread, 1 puts them on one background thread, <c>int.MaxValue</c> leaves libheif's default
    /// (which uses every core).
    /// </param>
    public static byte[] DecodeRgba(byte[] data, int maxThreads)
    {
        nint context = NativeHeif.heif_context_alloc();
        if (context == 0) throw new OutOfMemoryException("heif_context_alloc");
        try
        {
            if (maxThreads != int.MaxValue) NativeHeif.heif_context_set_max_decoding_threads(context, maxThreads);
            fixed (byte* p = data)
            {
                NativeHeif.Throw(NativeHeif.heif_context_read_from_memory_without_copy(context, p, (nuint)data.Length, 0), "read");
            }
            NativeHeif.Throw(NativeHeif.heif_context_get_primary_image_handle(context, out nint handle), "handle");
            try
            {
                NativeHeif.Throw(NativeHeif.heif_decode_image(handle, out nint image, NativeHeif.ColorspaceRGB, NativeHeif.ChromaInterleavedRGBA, 0), "decode");
                try
                {
                    int w = NativeHeif.heif_image_get_width(image, NativeHeif.ChannelInterleaved);
                    int h = NativeHeif.heif_image_get_height(image, NativeHeif.ChannelInterleaved);
                    byte* plane = NativeHeif.heif_image_get_plane_readonly(image, NativeHeif.ChannelInterleaved, out int stride);
                    var rgba = new byte[(long)w * h * 4];
                    fixed (byte* d = rgba)
                    {
                        for (var y = 0; y < h; y++)
                        {
                            Buffer.MemoryCopy(plane + (long)y * stride, d + (long)y * w * 4, (long)w * 4, (long)w * 4);
                        }
                    }
                    return rgba;
                }
                finally
                {
                    NativeHeif.heif_image_release(image);
                }
            }
            finally
            {
                NativeHeif.heif_image_handle_release(handle);
            }
        }
        finally
        {
            NativeHeif.heif_context_free(context);
        }
    }

    /// <summary>
    /// libheif → the three planes of the primary image, geometric transforms ignored, the peer of
    /// <see cref="HeifDecoder.DecodeYuv(ReadOnlySpan{byte})"/> (which is also pre-transform).
    /// </summary>
    public static PlaneSet DecodeYuv(byte[] data, int maxThreads)
    {
        nint context = NativeHeif.heif_context_alloc();
        if (context == 0) throw new OutOfMemoryException("heif_context_alloc");
        try
        {
            if (maxThreads != int.MaxValue) NativeHeif.heif_context_set_max_decoding_threads(context, maxThreads);
            fixed (byte* p = data)
            {
                NativeHeif.Throw(NativeHeif.heif_context_read_from_memory_without_copy(context, p, (nuint)data.Length, 0), "read");
            }
            NativeHeif.Throw(NativeHeif.heif_context_get_primary_image_handle(context, out nint handle), "handle");
            try
            {
                nint options = NativeHeif.heif_decoding_options_alloc();
                if (options == 0) throw new OutOfMemoryException("heif_decoding_options_alloc");
                try
                {
                    // libheif 1.23 decoding options: version 0, ignore_transformations is the v1
                    // option at byte offset 1.
                    *(byte*)(options + 1) = 1;
                    NativeHeif.Throw(NativeHeif.heif_decode_image(handle, out nint image, NativeHeif.ColorspaceYCbCr, NativeHeif.Chroma420, options), "decode");
                    try
                    {
                        int w = NativeHeif.heif_image_get_width(image, NativeHeif.ChannelY);
                        int h = NativeHeif.heif_image_get_height(image, NativeHeif.ChannelY);
                        return new PlaneSet
                        {
                            Y = CopyHeifChannel(image, NativeHeif.ChannelY),
                            Cb = CopyHeifChannel(image, NativeHeif.ChannelCb),
                            Cr = CopyHeifChannel(image, NativeHeif.ChannelCr),
                            Width = w,
                            Height = h,
                            BitDepth = NativeHeif.heif_image_get_bits_per_pixel_range(image, NativeHeif.ChannelY),
                        };
                    }
                    finally
                    {
                        NativeHeif.heif_image_release(image);
                    }
                }
                finally
                {
                    NativeHeif.heif_decoding_options_free(options);
                }
            }
            finally
            {
                NativeHeif.heif_image_handle_release(handle);
            }
        }
        finally
        {
            NativeHeif.heif_context_free(context);
        }
    }

    /// <summary>
    /// libde265 on one tile: the same NAL units our decoder is given, pushed one by one. Worker
    /// threads are not started — one context, one thread — which is what a single tile costs.
    /// </summary>
    public static PlaneSet DecodeTile(Tile tile)
    {
        nint decoder = NativeDe265.de265_new_decoder();
        if (decoder == 0) throw new OutOfMemoryException("de265_new_decoder");
        try
        {
            foreach (var nal in tile.NalUnits)
            {
                fixed (byte* p = nal)
                {
                    int error = NativeDe265.de265_push_NAL(decoder, p, nal.Length, 0, null);
                    if (NativeDe265.IsError(error)) throw new InvalidOperationException("de265_push_NAL: " + NativeDe265.Describe(error));
                }
            }
            int code = NativeDe265.de265_flush_data(decoder);
            if (NativeDe265.IsError(code)) throw new InvalidOperationException("de265_flush_data: " + NativeDe265.Describe(code));

            while (true)
            {
                code = NativeDe265.de265_decode(decoder, out int more);
                if (code == NativeDe265.ErrorImageBufferFull) continue;
                if (NativeDe265.IsError(code) && code != NativeDe265.ErrorWaitingForInputData)
                {
                    throw new InvalidOperationException("de265_decode: " + NativeDe265.Describe(code));
                }
                nint image = NativeDe265.de265_get_next_picture(decoder);
                if (image != 0)
                {
                    return new PlaneSet
                    {
                        Y = CopyDe265Channel(image, NativeDe265.ChannelY),
                        Cb = CopyDe265Channel(image, NativeDe265.ChannelCb),
                        Cr = CopyDe265Channel(image, NativeDe265.ChannelCr),
                        Width = NativeDe265.de265_get_image_width(image, NativeDe265.ChannelY),
                        Height = NativeDe265.de265_get_image_height(image, NativeDe265.ChannelY),
                        BitDepth = NativeDe265.de265_get_bits_per_pixel(image, NativeDe265.ChannelY),
                    };
                }
                if (more == 0 || code == NativeDe265.ErrorWaitingForInputData) break;
            }
            throw new InvalidOperationException("libde265 produced no picture.");
        }
        finally
        {
            NativeDe265.de265_free_decoder(decoder);
        }
    }

    private static ushort[] CopyHeifChannel(nint image, int channel)
    {
        int w = NativeHeif.heif_image_get_width(image, channel);
        int h = NativeHeif.heif_image_get_height(image, channel);
        byte* plane = NativeHeif.heif_image_get_plane_readonly(image, channel, out int stride);
        return CopyPlane(plane, stride, w, h, NativeHeif.heif_image_get_bits_per_pixel(image, channel));
    }

    private static ushort[] CopyDe265Channel(nint image, int channel)
    {
        int w = NativeDe265.de265_get_image_width(image, channel);
        int h = NativeDe265.de265_get_image_height(image, channel);
        byte* plane = NativeDe265.de265_get_image_plane(image, channel, out int stride);
        return CopyPlane(plane, stride, w, h, NativeDe265.de265_get_bits_per_pixel(image, channel));
    }

    private static ushort[] CopyPlane(byte* plane, int stride, int w, int h, int bitDepth)
    {
        var result = new ushort[(long)w * h];
        if (bitDepth <= 8)
        {
            for (var y = 0; y < h; y++)
            {
                var row = plane + (long)y * stride;
                for (var x = 0; x < w; x++) result[(long)y * w + x] = row[x];
            }
        }
        else
        {
            for (var y = 0; y < h; y++)
            {
                var row = (ushort*)(plane + (long)y * stride);
                for (var x = 0; x < w; x++) result[(long)y * w + x] = row[x];
            }
        }
        return result;
    }
}
