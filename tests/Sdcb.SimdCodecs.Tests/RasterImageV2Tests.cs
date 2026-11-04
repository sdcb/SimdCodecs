using System.Buffers;
using Sdcb.SimdCodecs.Abstractions;
using Shouldly;

namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// <see cref="RasterImage"/> in 2.0: the five ways to make one (wrap an array, wrap an owner, allocate, rent, create from an
/// optional pool), the stored bit depth, metadata and colour encoding, disposal (to a pool, to an owner, twice, and access
/// afterwards) and <see cref="RasterImage.Crop"/>.
/// </summary>
public sealed class RasterImageV2Tests
{
    private sealed class CountingOwner(byte[] array) : IMemoryOwner<byte>
    {
        public int Disposals { get; private set; }

        public Memory<byte> Memory => array;

        public void Dispose() => Disposals++;
    }

    private static byte[] Counting(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7 + 1))];

    // ---- Wrap / Allocate / Rent / Create ------------------------------------------------------------------------

    [Fact]
    public void WrapUsesTheArrayWithoutCopying()
    {
        var array = Counting(2 * 3 * 3);
        using var image = RasterImage.Wrap(2, 3, 3, SampleFormat.UInt8, array);
        (image.Width, image.Height, image.Channels, image.SampleFormat).ShouldBe((2, 3, 3, SampleFormat.UInt8));
        image.Pixels.ToArray().ShouldBe(array);
        image.WritablePixels[0] = 99;
        array[0].ShouldBe((byte)99);
        image.BitDepth.ShouldBe(8);
        image.ColorEncoding.ShouldBe(ColorEncoding.AssumedSrgb);
        image.Metadata.ShouldBeSameAs(ImageMetadata.Empty);
        image.IccProfile.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void WrapOfALargerArrayExposesOnlyTheImage()
    {
        var array = Counting(100);
        using var image = RasterImage.Wrap(3, 2, 1, SampleFormat.UInt16, array);
        image.Pixels.Length.ShouldBe(12);
        image.Pixels.ToArray().ShouldBe(array[..12]);
    }

    [Fact]
    public void WrapRefusesABufferThatIsTooSmallOrASizeThatIsWrong()
    {
        Should.Throw<ArgumentException>(() => RasterImage.Wrap(2, 3, 3, SampleFormat.UInt8, new byte[17]));
        Should.Throw<ArgumentException>(() => RasterImage.Wrap(2, 3, 1, SampleFormat.UInt16, new byte[11]));
        Should.Throw<ArgumentException>(() => RasterImage.Wrap(2, 3, 1, SampleFormat.Float32, new byte[23]));
        Should.Throw<ArgumentNullException>(() => RasterImage.Wrap(2, 3, 3, SampleFormat.UInt8, (byte[])null!));
        Should.Throw<ArgumentNullException>(() => RasterImage.Wrap(2, 3, 3, SampleFormat.UInt8, (IMemoryOwner<byte>)null!));
        foreach (var (w, h) in new[] { (0, 1), (1, 0), (-1, 1), (1, -1) })
            Should.Throw<ArgumentOutOfRangeException>(() => RasterImage.Allocate(w, h, 1, SampleFormat.UInt8));
        foreach (var channels in new[] { 0, 5, -1 })
            Should.Throw<ArgumentOutOfRangeException>(() => RasterImage.Allocate(1, 1, channels, SampleFormat.UInt8));
        Should.Throw<OverflowException>(() => RasterImage.Allocate(int.MaxValue, int.MaxValue, 4, SampleFormat.UInt16));
    }

    [Fact]
    public void WrapWithAnOwnerDisposesTheOwnerOnce()
    {
        var owner = new CountingOwner(Counting(64));
        var image = RasterImage.Wrap(4, 2, 2, SampleFormat.UInt8, owner);
        image.Pixels.ToArray().ShouldBe(Counting(16));
        owner.Disposals.ShouldBe(0);
        image.Dispose();
        owner.Disposals.ShouldBe(1);
        image.Dispose();
        owner.Disposals.ShouldBe(1);
        Should.Throw<ObjectDisposedException>(() => _ = image.Pixels.Length);
        Should.Throw<ObjectDisposedException>(() => _ = image.WritablePixels.Length);
    }

    [Fact]
    public void WrapWithAnOwnerThatIsTooSmallIsRefusedAndNotDisposed()
    {
        var owner = new CountingOwner(new byte[7]);
        Should.Throw<ArgumentException>(() => RasterImage.Wrap(4, 2, 1, SampleFormat.UInt8, owner));
        owner.Disposals.ShouldBe(0); // the caller still owns it
    }

    [Fact]
    public void AllocateIsZeroedAndOwnedByTheGarbageCollector()
    {
        var image = RasterImage.Allocate(5, 4, 3, SampleFormat.UInt16);
        image.Pixels.Length.ShouldBe(5 * 4 * 3 * 2);
        image.Pixels.ToArray().ShouldAllBe(b => b == 0);
        image.Dispose();
        Should.Throw<ObjectDisposedException>(() => _ = image.Pixels.Length);
        image.Dispose();
    }

    [Fact]
    public void RentReturnsTheArrayToItsPoolOnceOnDispose()
    {
        var pool = new CountingArrayPool();
        var image = RasterImage.Rent(7, 5, 4, SampleFormat.UInt8, pool);
        pool.Rents.ShouldBe(1);
        pool.Returns.ShouldBe(0);
        image.Pixels.Length.ShouldBe(7 * 5 * 4); // the pool handed out more
        pool.Rented[0].Length.ShouldBeGreaterThan(7 * 5 * 4);
        image.WritablePixels.Fill(0x5A);

        image.Dispose();
        pool.Returns.ShouldBe(1);
        pool.Returned[0].ShouldBeSameAs(pool.Rented[0]);
        image.Dispose();
        pool.Returns.ShouldBe(1);
        Should.Throw<ObjectDisposedException>(() => _ = image.Pixels.Length);
    }

    [Fact]
    public void RentWithoutAPoolUsesTheSharedPool()
    {
        using var image = RasterImage.Rent(8, 8, 1, SampleFormat.UInt8);
        image.Pixels.Length.ShouldBe(64);
        image.WritablePixels.Fill(7);
        image.Pixels.ToArray().ShouldAllBe(b => b == 7);
    }

    [Fact]
    public void CreateRentsFromAPoolAndAllocatesWithoutOne()
    {
        var pool = new CountingArrayPool();
        var rented = RasterImage.Create(3, 3, 3, SampleFormat.UInt8, pool);
        pool.Rents.ShouldBe(1);
        rented.Dispose();
        pool.Returns.ShouldBe(1);

        var allocated = RasterImage.Create(3, 3, 3, SampleFormat.UInt8, null);
        allocated.Pixels.ToArray().ShouldAllBe(b => b == 0);
        allocated.Dispose();
        pool.Rents.ShouldBe(1);
        pool.Returns.ShouldBe(1);
    }

    [Fact]
    public void ADisposedImageThrowsFromEveryPixelAccessor()
    {
        var image = RasterImage.Wrap(2, 2, 4, SampleFormat.UInt8, Counting(16));
        image.Dispose();
        Should.Throw<ObjectDisposedException>(() => _ = image.Pixels.Length);
        Should.Throw<ObjectDisposedException>(() => _ = image.WritablePixels.Length);
        Should.Throw<ObjectDisposedException>(() => image.ToRgba8());
        Should.Throw<ObjectDisposedException>(() => image.ToRgba16());
        Should.Throw<ObjectDisposedException>(() => image.ToFloats());
        Should.Throw<ObjectDisposedException>(() => image.TryCopyTo(new byte[64], 0, PixelFormat.Rgba32));
        // The shape and metadata stay readable.
        (image.Width, image.Height, image.Channels).ShouldBe((2, 2, 4));
        image.Metadata.ShouldNotBeNull();
    }

    [Fact]
    public void DisposeIsSafeFromSeveralThreadsAtOnce()
    {
        var pool = new CountingArrayPool();
        var image = RasterImage.Rent(16, 16, 4, SampleFormat.UInt8, pool);
        Parallel.For(0, 64, _ => image.Dispose());
        pool.Returns.ShouldBe(1);
    }

    // ---- bit depth ---------------------------------------------------------------------------------------------

    [Fact]
    public void BitDepthDefaultsToTheSampleWidthAndReportsWhatWasStored()
    {
        RasterImage.Allocate(1, 1, 1, SampleFormat.UInt8).BitDepth.ShouldBe(8);
        RasterImage.Allocate(1, 1, 1, SampleFormat.UInt16).BitDepth.ShouldBe(16);
        RasterImage.Allocate(1, 1, 1, SampleFormat.Float32).BitDepth.ShouldBe(32);

        RasterImage.Wrap(1, 1, 1, SampleFormat.UInt16, new byte[2], bitDepth: 10).BitDepth.ShouldBe(10);
        RasterImage.Wrap(1, 1, 1, SampleFormat.UInt16, new CountingOwner(new byte[2]), bitDepth: 12).BitDepth.ShouldBe(12);
        RasterImage.Wrap(1, 1, 1, SampleFormat.UInt8, new byte[1], bitDepth: 1).BitDepth.ShouldBe(1);
        RasterImage.Wrap(1, 1, 1, SampleFormat.UInt8, new byte[1], bitDepth: 8).BitDepth.ShouldBe(8);
        RasterImage.Allocate(1, 1, 1, SampleFormat.UInt16, bitDepth: 14).BitDepth.ShouldBe(14);
        RasterImage.Rent(1, 1, 1, SampleFormat.UInt16, new CountingArrayPool(), bitDepth: 9).BitDepth.ShouldBe(9);
        RasterImage.Create(1, 1, 1, SampleFormat.UInt16, null, bitDepth: 11).BitDepth.ShouldBe(11);
        RasterImage.Create(1, 1, 1, SampleFormat.UInt16, new CountingArrayPool(), bitDepth: 11).BitDepth.ShouldBe(11);
        RasterImage.Allocate(1, 1, 1, SampleFormat.Float32, bitDepth: 16).BitDepth.ShouldBe(16);
    }

    [Fact]
    public void ABitDepthWiderThanTheSampleIsRefused()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => RasterImage.Wrap(1, 1, 1, SampleFormat.UInt8, new byte[1], bitDepth: 9));
        Should.Throw<ArgumentOutOfRangeException>(() => RasterImage.Wrap(1, 1, 1, SampleFormat.UInt16, new byte[2], bitDepth: 17));
        Should.Throw<ArgumentOutOfRangeException>(() => RasterImage.Wrap(1, 1, 1, SampleFormat.Float32, new byte[4], bitDepth: 33));
        Should.Throw<ArgumentOutOfRangeException>(() => RasterImage.Wrap(1, 1, 1, SampleFormat.UInt8, new byte[1], bitDepth: -1));
    }

    [Fact]
    public void ABitDepthDoesNotChangeTheSamples()
    {
        using var image = RasterImage.Wrap(1, 1, 1, SampleFormat.UInt16, [0xFF, 0x03], bitDepth: 10); // 1023
        image.ToRgba16().ShouldBe([1023, 1023, 1023, 65535]);
        image.ToRgba8().ShouldBe([3, 3, 3, 255]); // the high byte: no rescale from 10 bits
    }

    // ---- metadata and colour -----------------------------------------------------------------------------------

    [Fact]
    public void MetadataAndColourEncodingAreCarried()
    {
        var encoding = new ColorEncoding { Primaries = ColorPrimaries.BT2020, Transfer = TransferFunction.Pq };
        var metadata = new ImageMetadata(exif: new byte[] { 1, 2, 3 }, xmp: new byte[] { 4, 5 });
        using var image = RasterImage.Wrap(1, 1, 3, SampleFormat.UInt8, new byte[3], colorEncoding: encoding, metadata: metadata);
        image.ColorEncoding.ShouldBe(encoding);
        image.Metadata.ShouldBeSameAs(metadata);
        image.Metadata.Exif.ToArray().ShouldBe([1, 2, 3]);
        image.IccProfile.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void AnIccProfileArgumentFillsTheMetadataAndTheMetadatasOwnWins()
    {
        byte[] icc = [9, 8, 7, 6];
        using (var image = RasterImage.Allocate(1, 1, 3, SampleFormat.UInt8, iccProfile: icc))
        {
            image.IccProfile.ToArray().ShouldBe(icc);
            image.Metadata.Icc.ToArray().ShouldBe(icc);
        }

        using (var withMetadata = RasterImage.Allocate(1, 1, 3, SampleFormat.UInt8, iccProfile: icc,
            metadata: new ImageMetadata(exif: new byte[] { 1, 1, 1, 1, 1, 1, 1, 1 })))
        {
            withMetadata.Metadata.Icc.ToArray().ShouldBe(icc);
            withMetadata.Metadata.Exif.Length.ShouldBe(8);
        }

        byte[] own = [1, 2];
        using (var both = RasterImage.Allocate(1, 1, 3, SampleFormat.UInt8, iccProfile: icc, metadata: new ImageMetadata(icc: own)))
            both.IccProfile.ToArray().ShouldBe(own);

        using (var empty = RasterImage.Allocate(1, 1, 3, SampleFormat.UInt8, iccProfile: []))
            empty.Metadata.ShouldBeSameAs(ImageMetadata.Empty);
    }

    // ---- Crop --------------------------------------------------------------------------------------------------

    [Fact]
    public void CropCopiesTheRegionAndKeepsTheDescription()
    {
        var encoding = new ColorEncoding { Primaries = ColorPrimaries.BT2020, Transfer = TransferFunction.Hlg };
        var metadata = new ImageMetadata(exif: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, icc: new byte[] { 1, 2, 3 });
        var pixels = Counting(6 * 5 * 2 * 3);
        using var source = RasterImage.Wrap(6, 5, 3, SampleFormat.UInt16, pixels, colorEncoding: encoding, bitDepth: 12, metadata: metadata);

        using var crop = RasterImage.Crop(source, new PixelRect(2, 1, 3, 2));
        (crop.Width, crop.Height, crop.Channels, crop.SampleFormat, crop.BitDepth).ShouldBe((3, 2, 3, SampleFormat.UInt16, 12));
        crop.ColorEncoding.ShouldBe(encoding);
        crop.Metadata.Exif.ToArray().ShouldBe(metadata.Exif.ToArray());
        crop.IccProfile.ToArray().ShouldBe([1, 2, 3]);

        var expected = new List<byte>();
        for (var y = 1; y < 3; y++)
            expected.AddRange(pixels.AsSpan((y * 6 + 2) * 6, 3 * 6).ToArray());
        crop.Pixels.ToArray().ShouldBe(expected.ToArray());

        // A copy: writing the crop leaves the source alone.
        crop.WritablePixels.Fill(0);
        source.Pixels.ToArray().ShouldBe(pixels[..(6 * 5 * 6)]);
    }

    [Fact]
    public void CropOfAFullDepthImageDoesNotInventABitDepth()
    {
        using var source = RasterImage.Wrap(2, 2, 1, SampleFormat.UInt16, Counting(8));
        using var crop = RasterImage.Crop(source, new PixelRect(0, 0, 1, 1));
        crop.BitDepth.ShouldBe(16);
    }

    [Fact]
    public void CropRentsFromThePoolAndTheWholeImageIsACopy()
    {
        var pool = new CountingArrayPool();
        using var source = RasterImage.Wrap(4, 3, 4, SampleFormat.UInt8, Counting(48));
        var whole = RasterImage.Crop(source, new PixelRect(0, 0, 4, 3), pool);
        pool.Rents.ShouldBe(1);
        whole.Pixels.ToArray().ShouldBe(source.Pixels.ToArray());
        whole.Dispose();
        pool.Returns.ShouldBe(1);
    }

    [Fact]
    public void CropOfAnyDecodedImageWorks()
    {
        // Crop takes the interface: here an image a decoder made.
        var rgba = new byte[8 * 6 * 4];
        new Random(1).NextBytes(rgba);
        SimdCodecs.TryDecode(Qoi.QoiEncoder.Encode(rgba, 8, 6, PixelFormat.Rgba32), out var decoded, cancellationToken: TestContext.Current.CancellationToken).ShouldBeTrue();
        using (decoded)
        using (var crop = RasterImage.Crop(decoded, new PixelRect(1, 1, 2, 2)))
        {
            for (var y = 0; y < 2; y++)
                crop.Pixels.Slice(y * 8, 8).ToArray().ShouldBe(rgba.AsSpan(((1 + y) * 8 + 1) * 4, 8).ToArray());
        }
    }

    [Fact]
    public void CropRefusesARegionOutsideTheImage()
    {
        using var source = RasterImage.Allocate(4, 3, 1, SampleFormat.UInt8);
        foreach (var region in new PixelRect[] { new(3, 0, 2, 1), new(0, 2, 1, 2), new(-1, 0, 1, 1), new(0, 0, 0, 1), new(0, 0, 1, 0), new(int.MaxValue, 0, 1, 1), new(1, 1, int.MaxValue, 1) })
            Should.Throw<ArgumentException>(() => RasterImage.Crop(source, region), region.ToString());
        Should.Throw<ArgumentNullException>(() => RasterImage.Crop(null!, new PixelRect(0, 0, 1, 1)));
    }

    // ---- conversions -------------------------------------------------------------------------------------------

    [Fact]
    public void TryCopyToIsTheDocumentedConversionForEveryFormat()
    {
        var pixels = new byte[5 * 3 * 4];
        new Random(2).NextBytes(pixels);
        using var image = RasterImage.Wrap(5, 3, 4, SampleFormat.UInt8, pixels);
        foreach (var format in PixelFormat.All)
        {
            var destination = new byte[5 * 3 * format.BytesPerPixel];
            image.TryCopyTo(destination, 0, format).ShouldBeTrue(format.ToString());
            destination.ShouldBe(PixelConverterTests.ReferenceConvert(pixels, PixelFormat.Rgba32, format, 15), format.ToString());
            image.TryCopyTo(new byte[destination.Length - 1], 0, format).ShouldBeFalse(format.ToString());
        }
    }

    [Fact]
    public void FloatImagesHaveNoIntegerProjection()
    {
        using var image = RasterImage.Allocate(2, 2, 3, SampleFormat.Float32);
        Should.Throw<NotSupportedException>(() => image.ToRgba8());
        Should.Throw<NotSupportedException>(() => image.ToRgba16());
        image.ToFloats().Length.ShouldBe(16);
    }
}
