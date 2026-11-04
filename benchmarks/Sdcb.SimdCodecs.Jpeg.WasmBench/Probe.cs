#if WASM_PROBE
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Wasm;

namespace Sdcb.SimdCodecs.Jpeg.WasmBench;

/// <summary>Disassembly probe: which Vector128 / PackedSimd calls Mono AOT lowers to wasm SIMD.</summary>
public static class Probe
{
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<int> ShlConst(Vector128<int> v) => Vector128.ShiftLeft(v, 8);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<int> ShlOp(Vector128<int> v) => v << 8;
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<int> ShlVar(Vector128<int> v, int s) => Vector128.ShiftLeft(v, s);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<int> ShlPacked(Vector128<int> v) => PackedSimd.ShiftLeft(v, 8);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<ushort> ShrU16(Vector128<ushort> v) => Vector128.ShiftRightLogical(v, 4);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<ushort> ShrU16Op(Vector128<ushort> v) => v >>> 4;
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> ShufConst(Vector128<float> v) => Vector128.Shuffle(v, Vector128.Create(2, 0, 3, 1));
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<byte> ShufBytes(Vector128<byte> v) => Vector128.Shuffle(v, Vector128.Create((byte)0, 4, 8, 12, 1, 5, 9, 13, 2, 6, 10, 14, 3, 7, 11, 15));
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> CreateSpan(ReadOnlySpan<float> s) => Vector128.Create(s);
    [MethodImpl(MethodImplOptions.NoInlining)] public static void CopyToSpan(Vector128<float> v, Span<float> s) => v.CopyTo(s);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> LoadU(ref float p) => Vector128.LoadUnsafe(ref p, 4);
    [MethodImpl(MethodImplOptions.NoInlining)] public static void StoreU(Vector128<float> v, ref float p) => v.StoreUnsafe(ref p, 4);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<short> NarrowI32(Vector128<int> a, Vector128<int> b) => Vector128.Narrow(a, b);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<byte> NarrowU16(Vector128<ushort> a, Vector128<ushort> b) => Vector128.Narrow(a, b);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<byte> NarrowSat(Vector128<short> a, Vector128<short> b) => PackedSimd.ConvertNarrowingSaturateUnsigned(a, b);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<int> CvtNative(Vector128<float> v) => Vector128.ConvertToInt32Native(v);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<int> Cvt(Vector128<float> v) => Vector128.ConvertToInt32(v);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> CvtF(Vector128<int> v) => Vector128.ConvertToSingle(v);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> MinMaxNative(Vector128<float> v) => Vector128.MinNative(Vector128.MaxNative(v, Vector128<float>.Zero), Vector128.Create(255f));
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> MinMaxPacked(Vector128<float> v) => PackedSimd.PseudoMin(PackedSimd.PseudoMax(v, Vector128<float>.Zero), Vector128.Create(255f));
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<int> WidenLo(Vector128<short> v) => Vector128.WidenLower(v);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<ushort> WidenLoB(Vector128<byte> v) => Vector128.WidenLower(v);
    [MethodImpl(MethodImplOptions.NoInlining)] public static ulong GetEl(Vector128<ulong> v) => v.GetElement(0);
    [MethodImpl(MethodImplOptions.NoInlining)] public static ulong ToScalar(Vector128<ulong> v) => v.ToScalar();
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<short> CreateShortSpan(ReadOnlySpan<short> s) => Vector128.Create(s);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> MulAdd(Vector128<float> a, Vector128<float> b, Vector128<float> c) => a * b + c;
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> MulScalar(Vector128<float> a) => a * 0.5f;
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<ushort> MulU16(Vector128<ushort> a, Vector128<ushort> b) => a * b;
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<int> MulI32(Vector128<int> a, Vector128<int> b) => a * b;
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<ulong> ScalarUnsafe(ulong x) => Vector128.CreateScalarUnsafe(x);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> Zero() => Vector128<float>.Zero;
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<byte> Swz(Vector128<byte> a, Vector128<byte> i) => PackedSimd.Swizzle(a, i);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<byte> ShufVar(Vector128<byte> a, Vector128<byte> i) => Vector128.Shuffle(a, i);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> Select(Vector128<float> a, Vector128<float> b) => Vector128.ConditionalSelect(Vector128.Create(-1, 0, -1, 0).AsSingle(), a, b);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<int> ShlFold(Vector128<int> v) => PackedSimd.IsSupported ? PackedSimd.ShiftLeft(v, 8) : Vector128.ShiftLeft(v, 8);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> Exchange(Vector128<float> a, Vector128<float> b) =>
        Vector128.ConditionalSelect(Vector128.Create(-1, 0, -1, 0).AsSingle(), a, Vector128.Shuffle(b, Vector128.Create(1, 0, 3, 2)));
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> UnpackLoSel(Vector128<float> a, Vector128<float> b) =>
        Vector128.ConditionalSelect(Vector128.Create(-1, 0, -1, 0).AsSingle(),
            Vector128.Shuffle(a.AsByte(), Vector128.Create((byte)0, 1, 2, 3, 0, 1, 2, 3, 4, 5, 6, 7, 4, 5, 6, 7)).AsSingle(),
            Vector128.Shuffle(b.AsByte(), Vector128.Create((byte)0, 1, 2, 3, 0, 1, 2, 3, 4, 5, 6, 7, 4, 5, 6, 7)).AsSingle());
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> UnpackLoSelF(Vector128<float> a, Vector128<float> b) =>
        Vector128.ConditionalSelect(Vector128.Create(-1, 0, -1, 0).AsSingle(), Vector128.Shuffle(a, Vector128.Create(0, 0, 1, 1)), Vector128.Shuffle(b, Vector128.Create(0, 0, 1, 1)));
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> MoveLH(Vector128<float> a, Vector128<float> b) =>
        Vector128.Create(a.AsUInt64().GetLower(), b.AsUInt64().GetLower()).AsSingle();
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> MoveHL(Vector128<float> a, Vector128<float> b) =>
        Vector128.Create(a.AsUInt64().GetUpper(), b.AsUInt64().GetUpper()).AsSingle();
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> MoveLHSel(Vector128<float> a, Vector128<float> b) =>
        Vector128.ConditionalSelect(Vector128.Create(-1L, 0).AsSingle(), a, Vector128.Shuffle(b.AsUInt64(), Vector128.Create(0UL, 0)).AsSingle());
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<byte> Pack(Vector128<int> a, Vector128<int> b, Vector128<int> c, Vector128<int> d) =>
        PackedSimd.ConvertNarrowingSaturateUnsigned(PackedSimd.ConvertNarrowingSaturateSigned(a, b), PackedSimd.ConvertNarrowingSaturateSigned(c, d));
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> Even(Vector128<float> a, Vector128<float> b) =>
        Vector128.ConditionalSelect(Vector128.Create(-1, 0, -1, 0).AsSingle(), a, PackedSimd.ShiftLeft(b.AsUInt64(), 32).AsSingle());
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> Odd(Vector128<float> a, Vector128<float> b) =>
        Vector128.ConditionalSelect(Vector128.Create(-1, 0, -1, 0).AsSingle(), PackedSimd.ShiftRightLogical(a.AsUInt64(), 32).AsSingle(), b);
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> EvenOr(Vector128<float> a, Vector128<float> b) =>
        ((a.AsUInt64() & Vector128.Create(0xFFFFFFFFUL)) | PackedSimd.ShiftLeft(b.AsUInt64(), 32)).AsSingle();
    [MethodImpl(MethodImplOptions.NoInlining)] public static Vector128<float> EvenV(Vector128<float> a, Vector128<float> b) =>
        Vector128.ConditionalSelect(Vector128.Create(-1, 0, -1, 0).AsSingle(), a, Vector128.ShiftLeft(b.AsUInt64(), 32).AsSingle());
}
#endif
