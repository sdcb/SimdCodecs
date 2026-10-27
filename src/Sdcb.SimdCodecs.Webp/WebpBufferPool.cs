using System.Buffers;

namespace Sdcb.SimdCodecs.Webp;

internal static class WebpBufferPool<T>
{
    public static T[] Rent(int minimumLength) => ArrayPool<T>.Shared.Rent(minimumLength);

    public static void Return(T[] buffer) => ArrayPool<T>.Shared.Return(buffer, clearArray: false);
}
