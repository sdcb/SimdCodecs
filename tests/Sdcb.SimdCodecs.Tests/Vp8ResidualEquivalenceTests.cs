using Sdcb.SimdCodecs.Webp;

namespace Sdcb.SimdCodecs.Tests;

public sealed class Vp8ResidualEquivalenceTests
{
    [Fact]
    public void AddResidualVectorMatchesScalar()
    {
        const int stride = 20;
        var random = new Random(0xD44);
        foreach (int x in new[] { 1, 5 })
        {
            foreach (int y in new[] { 1, 3 })
            {
                for (int pattern = 0; pattern < 3; pattern++)
                {
                    byte[] plane = new byte[stride * 12];
                    short[] residual = new short[16];
                    for (int i = 0; i < plane.Length; i++)
                    {
                        plane[i] = (byte)random.Next(256);
                    }

                    for (int i = 0; i < residual.Length; i++)
                    {
                        residual[i] = pattern switch
                        {
                            1 => (short)(random.Next(2) == 0 ? -255 : 255),
                            2 => (short)(random.Next(512) - 256),
                            _ => (short)(random.Next(41) - 20)
                        };
                    }

                    byte[] scalar = (byte[])plane.Clone();
                    byte[] vector = (byte[])plane.Clone();
                    Vp8Decoder.AddResidualScalar(ref scalar[y * stride + x], stride, residual);
                    Vp8Decoder.AddResidualVector(ref vector[y * stride + x], stride, residual);
                    Assert.Equal(scalar, vector);
                }
            }
        }
    }
}
