namespace Sdcb.SimdCodecs.Webp;

internal sealed class Vp8Yuv : IDisposable
{
    private readonly bool _pooled;

    public Vp8Yuv(int width, int height, byte[] y, byte[] u, byte[] v, bool pooled = false)
    {
        Width = width;
        Height = height;
        Y = y;
        U = u;
        V = v;
        _pooled = pooled;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Y { get; }
    public byte[] U { get; }
    public byte[] V { get; }

    public void Dispose()
    {
        if (_pooled)
        {
            WebpBufferPool<byte>.Return(Y);
            WebpBufferPool<byte>.Return(U);
            WebpBufferPool<byte>.Return(V);
        }
    }
}
