using Sdcb.SimdCodecs.Abstractions;

namespace Sdcb.SimdCodecs.Tiff;

/// <summary>Options for <see cref="TiffImageEncoder"/>.</summary>
public sealed record TiffEncodeOptions : EncodeOptions
{
    /// <summary>
    /// The template for every page: compression, layout (strips or tiles), resolution, artist and software.
    /// The adapter overrides what the frame decides — samples per pixel, bits per sample, photometric, extra samples and
    /// sample format — and the ICC profile when the frame has one. Null uses <see cref="TiffPageOptions.Default"/>.
    /// </summary>
    public TiffPageOptions? Page { get; init; }
}
