using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Wasm;

namespace Sdcb.SimdCodecs.Jpeg;

/// <summary>
/// Vector128 operations whose portable form Mono AOT (browser-wasm) does not lower to one
/// instruction: it compiles <c>Vector128.ShiftLeft</c> / <c>ShiftRightLogical</c> to an
/// out-of-line call, and <c>Vector128.Create(span)</c> / <c>CopyTo(span)</c> likewise; its
/// <c>ConvertToSingle</c> / <c>ConvertToInt32Native</c> spill the vector and rebuild it from
/// two 64-bit halves around the one instruction.
/// On wasm these take the <see cref="PackedSimd"/> instruction; everywhere else
/// <see cref="PackedSimd.IsSupported"/> is a JIT-time false and the portable call remains.
/// Both forms are the same IEEE / integer operation (int → float rounds to nearest-even
/// either way; float → int truncates, and callers clamp to range first), so the bits agree.
/// </summary>
internal static class V128
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> ShiftLeft(Vector128<int> v, [ConstantExpected] int n) =>
        PackedSimd.IsSupported ? PackedSimd.ShiftLeft(v, n) : Vector128.ShiftLeft(v, n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<uint> ShiftLeft(Vector128<uint> v, [ConstantExpected] int n) =>
        PackedSimd.IsSupported ? PackedSimd.ShiftLeft(v, n) : Vector128.ShiftLeft(v, n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> ShiftLeft(Vector128<ushort> v, [ConstantExpected] int n) =>
        PackedSimd.IsSupported ? PackedSimd.ShiftLeft(v, n) : Vector128.ShiftLeft(v, n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> ShiftRightLogical(Vector128<int> v, [ConstantExpected] int n) =>
        PackedSimd.IsSupported ? PackedSimd.ShiftRightLogical(v, n) : Vector128.ShiftRightLogical(v, n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<ushort> ShiftRightLogical(Vector128<ushort> v, [ConstantExpected] int n) =>
        PackedSimd.IsSupported ? PackedSimd.ShiftRightLogical(v, n) : Vector128.ShiftRightLogical(v, n);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<float> ConvertToSingle(Vector128<int> v) =>
        PackedSimd.IsSupported ? PackedSimd.ConvertToSingle(v) : Vector128.ConvertToSingle(v);

    /// <summary>Truncating float → int for lanes already in int range (the result is unspecified outside it).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector128<int> ConvertToInt32Native(Vector128<float> v) =>
        PackedSimd.IsSupported ? PackedSimd.ConvertToInt32Saturate(v) : Vector128.ConvertToInt32Native(v);
}
