using System.Buffers;
using ImageMagick;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Tests.Streaming;
using Sdcb.SimdCodecs.Tiff;
using Shouldly;
using MagickExifTag = ImageMagick.ExifTag;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="TiffImageEncoder.Create(Stream, EncodeOptions?, bool)"/>, the streaming TIFF session over a seekable stream:
/// its file equals the buffered session's byte for byte for every input format, compression, layout and page count, it
/// decodes (with our reader and with libtiff through Magick.NET) to exactly the stored pixels, bytes reach the stream
/// before Commit, and its memory is a band, not the image.
/// </summary>
public sealed class TiffStreamingEncodeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Photo-like samples (smooth gradients plus a little noise), so deflate has work to do and a strip is not a
    /// run of one value.</summary>
    private static byte[] Photo(PixelFormat format, int width, int height, int seed)
    {
        var bytes = new byte[width * height * format.BytesPerPixel];
        FillPhotoRows(bytes, format, width, 0, height, seed);
        return bytes;
    }

    private static void FillPhotoRows(Span<byte> rows, PixelFormat format, int width, int y0, int count, int seed)
    {
        var bpp = format.BytesPerPixel;
        var rowBytes = width * bpp;
        for (var y = 0; y < count; y++)
        {
            var row = rows.Slice(y * rowBytes, rowBytes);
            var yy = y0 + y;
            for (var i = 0; i < rowBytes; i++)
            {
                var x = i / bpp;
                var c = i % bpp;
                var noise = (uint)((x * 73856093) ^ (yy * 19349663) ^ (c * 83492791) ^ seed) * 2654435761u >> 29;
                row[i] = (byte)((x * (c + 1) + yy * (3 - c % 3)) / 4 + noise);
            }
        }
    }

    /// <summary>Feeds <paramref name="pixels"/> to <paramref name="frame"/> in random bands of 1..17 rows at a padded stride.</summary>
    private static void Push(IImageFrameEncoder frame, byte[] pixels, int width, int height, PixelFormat format, int seed)
    {
        var rowBytes = width * format.BytesPerPixel;
        var stride = rowBytes + 3;
        var random = new Random(seed);
        var y = 0;
        while (y < height)
        {
            var n = Math.Min(height - y, random.Next(1, 18));
            var band = new byte[stride * n];
            for (var r = 0; r < n; r++)
                pixels.AsSpan((y + r) * rowBytes, rowBytes).CopyTo(band.AsSpan(r * stride));
            // Half the bands packed (the session's one-piece path), half padded (its row-at-a-time path).
            if (random.Next(2) == 0)
            {
                var packed = new byte[rowBytes * n];
                pixels.AsSpan(y * rowBytes, rowBytes * n).CopyTo(packed);
                frame.WriteRows(packed, n, 0, Ct);
            }
            else
            {
                frame.WriteRows(band, n, stride, Ct);
            }
            y += n;
        }
    }

    private sealed record Page(byte[] Pixels, int Width, int Height, PixelFormat Format, ImageMetadata? Metadata = null);

    private static byte[] Buffered(TiffEncodeOptions? options, params Page[] pages)
    {
        var output = new ArrayBufferWriter<byte>();
        using (var session = TiffImageEncoder.Create(output, options))
        {
            session.IsStreaming.ShouldBeFalse();
            foreach (var p in pages)
                Push(session.AddFrame(p.Width, p.Height, p.Format, null, p.Metadata), p.Pixels, p.Width, p.Height, p.Format, 7);
            session.Commit(Ct);
        }
        return output.WrittenSpan.ToArray();
    }

    private static byte[] Streamed(TiffEncodeOptions? options, params Page[] pages)
    {
        using var stream = new MemoryStream();
        using (var session = TiffImageEncoder.Create(stream, options, leaveOpen: true))
        {
            session.IsStreaming.ShouldBeTrue();
            var seed = 11;
            foreach (var p in pages)
                Push(session.AddFrame(p.Width, p.Height, p.Format, null, p.Metadata), p.Pixels, p.Width, p.Height, p.Format, seed++);
            session.Commit(Ct);
        }
        return stream.ToArray();
    }

    private static void ShouldBeSameFile(byte[] actual, byte[] expected, string what)
    {
        if (actual.AsSpan().SequenceEqual(expected)) return;
        var at = actual.AsSpan().CommonPrefixLength(expected);
        throw new ShouldAssertException($"{what}: streamed file ({actual.Length} B) differs from the buffered one ({expected.Length} B) at byte {at}.");
    }

    /// <summary>Our reader's pixels of page <paramref name="index"/>, which must be <paramref name="expected"/>.</summary>
    private static void DecodesTo(byte[] file, int index, byte[] expected, PixelFormat native, string what)
    {
        TiffImageDecoder.TryOpenFrames(file, out var reader).ShouldBeTrue(what);
        using (reader)
        {
            reader!.TryDecodeFrame(index, out var image, cancellationToken: Ct).ShouldBeTrue(what);
            using (image)
            {
                image!.Channels.ShouldBe(native.Channels, what);
                image.SampleFormat.ShouldBe(native.Sample, what);
                image.Pixels.SequenceEqual(expected).ShouldBeTrue($"{what}: decoded pixels differ");
            }
        }
    }

    public static TheoryData<TiffCompression, TiffLayout, int> Layouts() => new()
    {
        { TiffCompression.Uncompressed, TiffLayout.Strip, 0 },
        { TiffCompression.Uncompressed, TiffLayout.Strip, 1 },
        { TiffCompression.Uncompressed, TiffLayout.Strip, 5 },
        { TiffCompression.Uncompressed, TiffLayout.Tiled, 0 },
        { TiffCompression.Deflate, TiffLayout.Strip, 0 },
        { TiffCompression.Deflate, TiffLayout.Strip, 1 },
        { TiffCompression.Deflate, TiffLayout.Strip, 7 },
        { TiffCompression.Deflate, TiffLayout.Tiled, 0 },
        { TiffCompression.ZlibPkzip, TiffLayout.Strip, 4 },
        { TiffCompression.ZlibPkzip, TiffLayout.Tiled, 0 },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Every_format_streams_the_buffered_file(TiffCompression compression, TiffLayout layout, int rowsPerStrip)
    {
        var options = new TiffEncodeOptions
        {
            Page = new TiffPageOptions { Compression = compression, Layout = layout, RowsPerStrip = rowsPerStrip, TileWidth = 16, TileHeight = 32 },
        };
        foreach (var format in PixelFormat.All)
        {
            const int w = 37, h = 41; // odd: partial strips, partial tiles on the right and bottom, odd segment lengths
            var pixels = Photo(format, w, h, 1);
            var page = new Page(pixels, w, h, format);
            var streamed = Streamed(options, page);
            ShouldBeSameFile(streamed, Buffered(options, page), format.ToString());
            var native = TiffImageEncoder.GetClosestInputFormat(format);
            DecodesTo(streamed, 0, PixelConverter.ToPacked(pixels, 0, format, native, w, h), native, format.ToString());
        }
    }

    /// <summary>Strips and tiles longer than the deflater's input slice, fed in bands that straddle its boundaries.</summary>
    [Theory]
    [InlineData(TiffLayout.Strip, 0)]
    [InlineData(TiffLayout.Strip, 61)]
    [InlineData(TiffLayout.Tiled, 0)]
    public void Segments_longer_than_a_deflate_slice_stream_the_buffered_file(TiffLayout layout, int rowsPerStrip)
    {
        var options = new TiffEncodeOptions
        {
            Page = new TiffPageOptions { Layout = layout, RowsPerStrip = rowsPerStrip, TileWidth = 160, TileHeight = 128 },
        };
        foreach (var format in new[] { PixelFormat.Rgba64, PixelFormat.Bgr24, PixelFormat.Gray8 })
        {
            const int w = 333, h = 257;
            var page = new Page(Photo(format, w, h, 2), w, h, format);
            ShouldBeSameFile(Streamed(options, page), Buffered(options, page), $"{format} {layout}");
        }
    }

    [Fact]
    public void Multi_page_with_metadata_streams_the_buffered_file_and_libtiff_reads_every_page()
    {
        var exif = new ExifProfile();
        exif.SetValue(MagickExifTag.Make, "TestCam");
        exif.SetValue(MagickExifTag.DateTimeOriginal, "2026:11:23 10:20:30");
        exif.SetValue(MagickExifTag.GPSLatitudeRef, "N");
        var icc = new byte[300];
        new Random(3).NextBytes(icc);
        var metadata = new ImageMetadata { Icc = icc, Xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'/>"u8.ToArray(), Exif = exif.ToByteArray()! };

        var pages = new[]
        {
            new Page(Photo(PixelFormat.Rgb24, 51, 33, 4), 51, 33, PixelFormat.Rgb24, metadata),
            new Page(Photo(PixelFormat.Gray16, 20, 70, 5), 20, 70, PixelFormat.Gray16),
            new Page(Photo(PixelFormat.Bgra32, 64, 9, 6), 64, 9, PixelFormat.Bgra32, new ImageMetadata { Xmp = "<x/>"u8.ToArray() }),
        };
        var options = new TiffEncodeOptions { Page = new TiffPageOptions { RowsPerStrip = 8, Software = "streaming test" } };
        var streamed = Streamed(options, pages);
        ShouldBeSameFile(streamed, Buffered(options, pages), "three pages");

        using var collection = new MagickImageCollection(streamed);
        collection.Count.ShouldBe(pages.Length);
        for (var i = 0; i < pages.Length; i++)
        {
            var p = pages[i];
            var native = TiffImageEncoder.GetClosestInputFormat(p.Format);
            var expected = PixelConverter.ToPacked(p.Pixels, 0, p.Format, native, p.Width, p.Height);
            DecodesTo(streamed, i, expected, native, $"page {i}");
            MagickPixels(collection[i], native).ShouldBe(expected, $"page {i} through libtiff");
        }
        collection[0].GetAttribute("tiff:make").ShouldBe("TestCam"); // libtiff reports IFD0's Make as tiff:make
        collection[0].GetColorProfile()!.ToByteArray().ShouldBe(icc);
    }

    /// <summary>libtiff's (Magick.NET's) reading of a page, packed in <paramref name="native"/> layout.</summary>
    private static byte[] MagickPixels(IMagickImage<float> image, PixelFormat native)
    {
        var map = native.Layout switch
        {
            ChannelLayout.Rgba => "RGBA",
            ChannelLayout.Rgb => "RGB",
            ChannelLayout.GrayAlpha => "RA",
            _ => "R",
        };
        using var px = image.GetPixelsUnsafe();
        if (native.Sample == SampleFormat.UInt8)
            return px.ToByteArray(map)!;
        var shorts = px.ToShortArray(map)!;
        var bytes = new byte[shorts.Length * 2];
        Buffer.BlockCopy(shorts, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    [Fact]
    public void Bytes_reach_the_stream_before_commit()
    {
        const int w = 300, h = 200;
        var pixels = Photo(PixelFormat.Rgb24, w, h, 8);
        foreach (var (compression, rowsPerStrip) in new[] { (TiffCompression.Deflate, 16), (TiffCompression.Uncompressed, 16), (TiffCompression.Deflate, 0) })
        {
            var options = new TiffEncodeOptions { Page = new TiffPageOptions { Compression = compression, RowsPerStrip = rowsPerStrip } };
            using var stream = new MemoryStream();
            using var session = TiffImageEncoder.Create(stream, options, leaveOpen: true);
            var frame = session.AddFrame(w, h, PixelFormat.Rgb24);
            var half = h / 2;
            frame.WriteRows(pixels.AsSpan(0, half * w * 3), half, 0, Ct);
            // Six strips are complete by now; a default strip (the whole page) still streams, as the deflater has had
            // 90 KB of input. Either way more than the 8-byte header is out.
            stream.Length.ShouldBeGreaterThan(compression == TiffCompression.Uncompressed ? 8 + 6 * 16 * w * 3 : 8,
                $"{compression} / {rowsPerStrip} rows per strip");
            frame.WriteRows(pixels.AsSpan(half * w * 3), h - half, 0, Ct);
            session.Commit(Ct);
            ShouldBeSameFile(stream.ToArray(), Buffered(options, new Page(pixels, w, h, PixelFormat.Rgb24)), $"{compression}");
        }
    }

    /// <summary>
    /// End to end over a file: an 8192 × 8192 RGB page (192 MiB of pixels) fed in 64-row bands to a <see cref="FileStream"/>
    /// allocates a fraction of one strip-free band, the file equals the buffered encode, and our reader decodes it to the
    /// pixels.
    /// </summary>
    [Fact]
    public void Large_page_streams_to_a_file_in_bounded_memory()
    {
        const int w = 8192, h = 8192, band = 64;
        var format = PixelFormat.Rgb24;
        var rowBytes = w * 3;
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SimdCodecs-TiffStreamingEncode-" + Guid.NewGuid().ToString("N")));
        try
        {
            var path = Path.Combine(dir.FullName, "large.tif");
            var rows = new byte[rowBytes * band];
            void Encode()
            {
                using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16);
                using var session = TiffImageEncoder.Create(file);
                var frame = session.AddFrame(w, h, format);
                for (var y = 0; y < h; y += band)
                {
                    FillPhotoRows(rows, format, w, y, band, 9);
                    frame.WriteRows(rows, band, 0, Ct);
                }
                session.Commit(Ct);
            }
            // One band is 1.5 MiB (the caller's); the session holds a 64 KiB deflate slice.
            StreamingAssert.AllocatesLessThan(1 << 20, Encode, "streaming an 8192x8192 page");

            var streamed = File.ReadAllBytes(path);
            var pixels = Photo(format, w, h, 9);
            ShouldBeSameFile(streamed, Buffered(null, new Page(pixels, w, h, format)), "8192x8192");
            DecodesTo(streamed, 0, pixels, format, "8192x8192");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>A large synthetic photo streamed to a file decodes, with our reader and with libtiff, to the exact pixels.</summary>
    [Fact]
    public void Photo_streamed_to_a_file_decodes_exactly_with_libtiff()
    {
        const int w = 2400, h = 1600;
        var format = PixelFormat.Bgra32;
        var pixels = Photo(format, w, h, 10);
        var native = TiffImageEncoder.GetClosestInputFormat(format);
        var expected = PixelConverter.ToPacked(pixels, 0, format, native, w, h);
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "SimdCodecs-TiffStreamingEncode-" + Guid.NewGuid().ToString("N")));
        try
        {
            var path = Path.Combine(dir.FullName, "photo.tif");
            var options = new TiffEncodeOptions { Page = new TiffPageOptions { RowsPerStrip = 32 } };
            using (var file = File.Create(path))
            using (var session = TiffImageEncoder.Create(file, options))
            {
                Push(session.AddFrame(w, h, format), pixels, w, h, format, 12);
                session.Commit(Ct);
            }
            var streamed = File.ReadAllBytes(path);
            ShouldBeSameFile(streamed, Buffered(options, new Page(pixels, w, h, format)), "photo");
            DecodesTo(streamed, 0, expected, native, "photo");
            using var magick = new MagickImage(path);
            MagickPixels(magick, native).ShouldBe(expected);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void File_starts_at_the_streams_position()
    {
        const int w = 40, h = 30;
        var page = new Page(Photo(PixelFormat.Rgb24, w, h, 13), w, h, PixelFormat.Rgb24);
        var options = new TiffEncodeOptions { Page = new TiffPageOptions { RowsPerStrip = 7 } };
        using var stream = new MemoryStream();
        stream.Write("prefix-"u8);
        using (var session = TiffImageEncoder.Create(stream, options, leaveOpen: true))
        {
            Push(session.AddFrame(w, h, page.Format), page.Pixels, w, h, page.Format, 1);
            Push(session.AddFrame(w, h, page.Format), page.Pixels, w, h, page.Format, 2);
            session.Commit(Ct);
        }
        var all = stream.ToArray();
        all.AsSpan(0, 7).SequenceEqual("prefix-"u8).ShouldBeTrue();
        ShouldBeSameFile(all[7..], Buffered(options, page, page), "after a prefix");
    }

    [Fact]
    public void Disposing_closes_the_stream_unless_left_open()
    {
        var closed = new MemoryStream();
        TiffImageEncoder.Create(closed).Dispose();
        closed.CanWrite.ShouldBeFalse();
        var open = new MemoryStream();
        TiffImageEncoder.Create(open, leaveOpen: true).Dispose();
        open.CanWrite.ShouldBeTrue();
    }

    [Fact]
    public void Non_seekable_or_read_only_stream_is_refused()
    {
        var e = Should.Throw<ArgumentException>(() => TiffImageEncoder.Create(new NonSeekableStream()));
        e.Message.ShouldContain("IBufferWriter");
        Should.Throw<ArgumentException>(() => TiffImageEncoder.Create(new MemoryStream([], writable: false)));
        Should.Throw<ArgumentNullException>(() => TiffImageEncoder.Create((Stream)null!));
        Should.Throw<ArgumentException>(() => TiffImageEncoder.Create(new MemoryStream(), new Sdcb.SimdCodecs.Jpeg.JpegEncodeOptions()));
    }

    [Fact]
    public void A_page_the_writer_cannot_encode_fails_at_AddFrame_before_a_byte_is_written()
    {
        using var stream = new MemoryStream();
        using (var session = TiffImageEncoder.Create(stream, new TiffEncodeOptions { Page = new TiffPageOptions { Compression = TiffCompression.Lzw } }, leaveOpen: true))
            Should.Throw<NotSupportedException>(() => session.AddFrame(4, 4, PixelFormat.Rgb24));
        using (var session = TiffImageEncoder.Create(stream, new TiffEncodeOptions { Page = new TiffPageOptions { Exif = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 } } }, leaveOpen: true))
            Should.Throw<InvalidDataException>(() => session.AddFrame(4, 4, PixelFormat.Rgb24));
        stream.Length.ShouldBe(0);
    }

    [Fact]
    public void Cancellation_stops_the_session()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var stream = new MemoryStream();
        using var session = TiffImageEncoder.Create(stream, leaveOpen: true);
        var frame = session.AddFrame(8, 8, PixelFormat.Rgb24);
        var rows = new byte[8 * 8 * 3];
        Should.Throw<OperationCanceledException>(() => frame.WriteRows(rows, 8, 0, cts.Token));
        frame.WriteRows(rows, 8, 0, Ct);
        Should.Throw<OperationCanceledException>(() => session.Commit(cts.Token));
    }

    [Fact]
    public void A_write_that_fails_partway_ends_the_session()
    {
        const int w = 64, h = 64;
        var rows = Photo(PixelFormat.Rgb24, w, h, 14);
        var options = new TiffEncodeOptions { Page = new TiffPageOptions { Compression = TiffCompression.Uncompressed, RowsPerStrip = 4 } };
        using var stream = new FailingStream(failAfter: 8 + 3 * 4 * w * 3); // fails inside the fourth strip of the first band
        using var session = TiffImageEncoder.Create(stream, options, leaveOpen: true);
        var frame = session.AddFrame(w, h, PixelFormat.Rgb24);
        Should.Throw<IOException>(() => frame.WriteRows(rows, 32, 0, Ct));
        frame.RowsWritten.ShouldBe(0);
        Should.Throw<InvalidOperationException>(() => frame.WriteRows(rows, 32, 0, Ct));
    }

    private sealed class FailingStream(long failAfter) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Check(buffer.Length);
            base.Write(buffer);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Check(count);
            base.Write(buffer, offset, count);
        }

        public override void WriteByte(byte value)
        {
            Check(1);
            base.WriteByte(value);
        }

        private void Check(int count)
        {
            if (Position + count > failAfter)
                throw new IOException("disk full");
        }
    }

    private sealed class NonSeekableStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }
}
