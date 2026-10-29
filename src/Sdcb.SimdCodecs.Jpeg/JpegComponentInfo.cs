namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// One frame component as the SOF header declares it (ITU-T T.81 B.2.2): its identifier and its
/// horizontal and vertical sampling factors, 1..4. A component's resolution relative to the frame is
/// its factor over the largest factor among the components.
/// </summary>
/// <param name="Id">The component identifier (<c>C</c>), e.g. 1, 2, 3 for JFIF's Y, Cb, Cr.</param>
/// <param name="HorizontalSampling">The horizontal sampling factor (<c>H</c>).</param>
/// <param name="VerticalSampling">The vertical sampling factor (<c>V</c>).</param>
public readonly record struct JpegComponentInfo(int Id, int HorizontalSampling, int VerticalSampling);
