# Abstractions 2.0 设计草案

> **状态：草案，尚未实施。** 本文描述 `Sdcb.SimdCodecs.Abstractions` 在 2.0 版本里理想的公共 API，以及每项改动的
> 影响范围和难度。它是设计目标，不是缺口清单（缺口清单见 [`known-gaps.md`](known-gaps.md)），也不代表 1.x 的
> 行为。2.0 发布后，本文删除，或改写成描述现行设计的 `api-design.md`。

## 1. 出发点

1.x 的 Abstractions 是围绕“把一个格式解码成像素”设计的：一次性、整幅、8 位输出、输入全在内存里。对显示和
OCR 这类场景足够，但和 WIC（Windows Imaging Component）这类专业图像框架相比，在以下方面有明显差距：

- 只能输出 8 位像素，10 位 HEIC、16 位 PNG/TIFF 在 `TryDecodeInto` 这条路上精度丢失；
- 没有预乘 alpha 格式，而 WPF、Direct2D、Skia 的表面基本都是预乘的；
- 输入必须整个在内存里，没有 `Stream` 入口；
- 解码不能缩放、不能只解一个区域，也不能取消；
- 元数据（Exif、XMP）和缩略图不在接口里，转码时会丢失拍摄时间、GPS 等信息；
- 没有编码器抽象，门面只能解码。

### 1.x 已经做对、2.0 保留的

- **静态抽象成员 + 显式注册**。codec 不靠反射发现，NativeAOT 和 Mono AOT 都安全。门面按具体方法组绑定委托，
  不在泛型方法里绑定（AGENTS.md 的约束）。2.0 新增的所有静态成员都遵守这一点。
- **`ReadOnlySpan<byte>` 一次性 API 作为热路径**。2.0 不删除它，而是在它之上加一层会话 API（见第 9 节）。
- **错误约定**：不可信输入抛出 `InvalidDataException`，`Try*` 返回 `false`；不会挂起，也不会无界分配。
- **`IDecodedImage` 保真层 + `ColorEncoding` 的 H.273 词汇**。
- **`IImageFrameReader` 按下标随机访问帧**（`TryDecodeFrame(int index, …)`），这一点 1.x 已经有了。

### 不学 WIC 的地方

| WIC 的做法 | 不采用的原因 |
|---|---|
| COM 注册表、运行时发现 codec | 与 AOT 冲突；显式注册更可测 |
| GUID 表示的开放像素格式集合 | 封闭集合可以穷举测试每个“源 × 目标”组合，每条路径都有标量版本对照 |
| apartment 线程模型 | 我们的静态 API 无状态、线程安全，更简单 |
| HRESULT 错误码 | 用 .NET 异常和 `Try*` 约定 |
| 打包格式（RGB10A2 等）、调色板输出 | 属于 GPU 表面格式，不是编解码层的抽象；托管代码里也不适合 SIMD |

## 2. 难度与影响的记号

- **破坏性**：是否改变已发布的公共签名或语义。1.x 的调用方或第三方 `IImageDecoder` 实现需要改代码，就算破坏性。
- **难度**：低（主要是机械改动）、中（需要新内核或新测试矩阵）、高（要改各 codec 的内部结构）。
- **影响范围**：以 2026-11-03 的代码为准。`IImageDecoder` 适配器共 12 个（BMP、GIF、ICO、JBIG2、JPEG、
  JPEG 增益图、JPEG 2000、PNG、PNM、QOI、TIFF、WebP），多帧读取器在 GIF、ICO、TIFF、WebP 中。HEIF 目前还没有
  门面适配器，2.0 可以直接按新接口写。`ImagePixelFormat` 在 `src` 下被引用约 218 处，在测试和基准中涉及 32 个文件。

## 3. 像素格式：`ImagePixelFormat` 枚举 → `PixelFormat` 结构体

**现状。** `ImagePixelFormat` 只有 5 个 8 位布局（`Rgba32`、`Bgra32`、`Rgb24`、`Bgr24`、`Gray8`）。它和
`IDecodedImage` 的 `Channels` + `SampleFormat` 是两套不同的描述方式。`PixelConverter.TryGetLayout` 用
`(uint)format > (uint)ImagePixelFormat.Gray8` 判断合法性，加新枚举值时容易漏改。

**2.0 设计。** 通道布局、样本类型和 alpha 语义三者正交，组合成一个结构体：

```csharp
public enum ChannelLayout : byte { Rgba, Bgra, Rgb, Bgr, Gray }
public enum AlphaMode     : byte { Straight, Premultiplied }   // 只对 Rgba / Bgra 有意义

public readonly record struct PixelFormat
{
    public ChannelLayout Layout { get; }
    public SampleFormat  Sample { get; }   // UInt8 或 UInt16；Float32 不在这里
    public AlphaMode     Alpha  { get; }

    public int Channels      { get; }      // 由 Layout 推出
    public int BytesPerPixel { get; }      // Channels × 每样本字节数

    public static PixelFormat Rgba32  { get; }   // 8 位
    public static PixelFormat Bgra32  { get; }
    public static PixelFormat Rgb24   { get; }
    public static PixelFormat Bgr24   { get; }
    public static PixelFormat Gray8   { get; }
    public static PixelFormat Rgba64  { get; }   // 16 位
    public static PixelFormat Bgra64  { get; }
    public static PixelFormat Rgb48   { get; }
    public static PixelFormat Bgr48   { get; }
    public static PixelFormat Gray16  { get; }
    public static PixelFormat Rgba32Premultiplied { get; }
    public static PixelFormat Bgra32Premultiplied { get; }
    public static PixelFormat Rgba64Premultiplied { get; }
    public static PixelFormat Bgra64Premultiplied { get; }
}
```

构造函数拒绝无意义的组合（例如 `Gray` + `Premultiplied`），所以不存在越界值，`TryGetLayout` 里的枚举范围检查
也就不需要了。浮点输出不放进 `PixelFormat`：浮点没有规范的整数映射，仍走 `IDecodedImage.ToFloats()`。

**需要先定下来的数值约定**（定下后冻结，和 `JpegColor` 一样，改动算输出变化）：

| 转换 | 约定 |
|---|---|
| 8 位 → 16 位 | `v × 257`，精确，0→0、255→65535 |
| 16 位 → 8 位 | **保留 1.x 的取高字节（`v >> 8`），已决定。** 它和 `round(v × 255 / 65535)` 最多差 1 个码值，不值得为此改动所有 16 位源的 8 位输出和 golden |
| N 位源（10、12 位）→ 16 位 | `round(v × 65535 / (2^N − 1))`，满量程线性映射，不是左移补零 |
| 预乘 | `round(c × a / max)`；只在输出时预乘，解码器内部和 `IDecodedImage.Pixels` 始终是非预乘的 |
| 反预乘（编码器输入） | `a = 0` 时颜色取 0；否则 `min(max, round(c × max / a))`。编码器收到预乘输入时先反预乘，再按非预乘输入处理 |
| 编码器丢弃 alpha | 不合成，直接丢掉（沿用 1.x `PixelConverter` 的约定）。预乘输入先反预乘再丢 alpha，所以它和对应的非预乘输入编码出相同的结果，而不是隐式合成到黑底上。需要合成到某个底色的，由调用方在编码前自己做 |

**影响范围。** Abstractions（`PixelConverter` 全部重写，新增 16 位和预乘的 SIMD 内核及其标量版本）；12 个
`IImageDecoder` 适配器和 4 个多帧读取器的 `TryDecodeInto` 签名；门面；JPEG、PNG、WebP 写入调用方缓冲区的原生
快速路径（要么补上 16 位和预乘路径，要么对新格式退回通用转换）；所有引用 `ImagePixelFormat` 的测试和基准。

**破坏性：是。难度：中。** 签名替换是机械的；工作量主要在新增的转换内核，以及“源格式 × 目标格式 × 指令集层级
（Vector256 / Vector128 / 标量）”这个测试矩阵。

### 3.1 1.x 各 codec 的 `TryDecodeInto` 输出路径

1.x 没有统一的“先出 RGBA32 再后处理”，各 codec 的做法不同（以 2026-11-03 的代码为准）：

| 类别 | codec | 做法 |
|---|---|---|
| 解码时直接写目标格式 | JPEG | 颜色转换（`JpegColor.YccRow` / `RgbRow` / `CmykRow`）带 `bgr` 开关，直接写 RGBA / BGRA。RGB24 / BGR24 先转成一行 RGBA，再用 `JpegColor.PackRgb24` 打包。Gray8：YCbCr 帧直接拷贝 Y 平面（色度不做 IDCT、上采样和颜色转换）；RGB、CMYK 帧先出 RGBA 行，再用 `PixelConverter` 求亮度 |
| | BMP | 按目标格式选行处理方式（`Copy24`、`Swap24` 等），部分情况才经过 `ConvertRow` |
| | ICO | `DecodeInto` 直接写调用方缓冲区，复用 BMP / PNG 的逐行路径 |
| 逐行或分块：中间行 + `ConvertRow` | PNG | 8 位 RGBA 文件输出 RGBA32：反滤波后的行就是结果。8 位、无调色板、无透明色键：从原始样本行直接 `ConvertRow`。其他：先展开成一行 RGBA 再转换。例外：Adam7 隔行扫描先组装整帧 RGBA，再逐行转换 |
| | QOI、PNM | 每 256 像素一块解出样本，再 `ConvertRow`；QOI 输出 RGBA / BGRA 时直接写 |
| | JBIG2 | 逐行生成灰度，再 `ConvertRow` |
| 整帧解码后再转换（多一整帧分配） | WebP | 只会出 RGBA。RGBA32 直接写；BGRA32 写完后原地按行换通道；RGB24、BGR24、Gray8 先租一整帧 RGBA，再逐行转换 |
| | TIFF、JPEG 2000 | 先 `TryDecode` 出完整的 `IDecodedImage`，再 `PixelConverter.TryConvert` |
| | GIF | 先合成整幅 RGBA 画布（动画本来就需要），再转换 |

注：`known-gaps.md` 里“JPEG、PNG、WebP 直接写进调用方缓冲区”的说法，对 WebP 只在 RGBA / BGRA 时成立。

### 3.2 对 2.0 的含义

1. **先走通用转换，结果一定正确。** `IImageDecoder.TryDecodeInto` 的契约要求原生路径产生的字节与
   “`ToRgba8` 再 `PixelConverter.ConvertRow`”完全相同。所以新增的 Rgba64 / Bgra64 / 预乘格式可以先让所有 codec
   走“`TryDecode` + `PixelConverter.TryConvert`”，再按基准逐个补原生路径。2.0 的契约要相应改写为：原生路径的结果
   等于“对 `IDecodedImage.Pixels` 做 `PixelConverter` 转换”，而不是等于 `ToRgba8` 的结果（`ToRgba8` 是 8 位的）。
2. **16 位输出不能经过 8 位 RGBA 中间行。** JPEG 的 RGB24 打包、PNG 的非直通路径、WebP 的非 RGBA 路径，中间行都是
   8 位 RGBA。16 位源（PNG 16 位、TIFF、JPEG 2000、10 位 HEIF）输出 16 位格式时，必须从原始样本直接转换，否则精度
   在中间就丢了。具体要改的是：PNG 的逐行展开器增加 16 位输出；TIFF、JPEG 2000 走通用转换即可（它们的
   `IDecodedImage` 本来就保留 16 位）；HEIF 需要新的 16 位颜色转换内核（第 4 节）。
3. **8 位源输出 16 位格式没有精度问题。** JPEG、WebP、BMP、QOI、GIF 出 Rgba64 只是 `×257`，经过 8 位中间行不损失
   精度，第一阶段可以统一用“先出 8 位，再在 `PixelConverter` 里加宽”的方式实现。
4. **WebP 的整帧中间缓冲是独立的性能项。** 它和 2.0 的 API 无关，3 字节和灰度输出逐行转换的改进可以在 1.x 里先做。

## 4. `IDecodedImage`：16 位输出、真实位深、可释放

**现状。** 只有 `ToRgba8` 和 `ToFloats`。10 位 HEIC 拿不到 16 位 RGBA；源文件的真实位深只在 `ImageInfo.BitDepth`
里，`IDecodedImage` 上没有。

**2.0 设计。**

```csharp
public interface IDecodedImage : IDisposable
{
    int Width { get; }
    int Height { get; }
    int Channels { get; }
    SampleFormat SampleFormat { get; }
    int BitDepth { get; }                    // 新增：真实精度，例如 UInt16 容器里的 10 位
    ColorEncoding ColorEncoding { get; }
    ReadOnlySpan<byte> Pixels { get; }
    ReadOnlySpan<byte> IccProfile { get; }
    ImageMetadata Metadata { get; }          // 新增，见第 8 节

    byte[]   ToRgba8();
    ushort[] ToRgba16();                     // 新增
    float[]  ToFloats();

    bool TryCopyTo(Span<byte> destination, int stride, PixelFormat format);   // 统一的无分配出口
}
```

`ExpandToRgba8` / `ExpandToFloats` 目前只在 `RasterImage` 上，2.0 改为由 `TryCopyTo` 统一提供。实现
`IDisposable` 是为了支持池化内存（第 12 节）。

**影响范围。** Abstractions；所有构造 `RasterImage` 的 codec（传入位深）；HEIF 需要新增 16 位输出路径
（`HeifColor` 增加输出 `ushort` 的版本，系数从 `255/(2^N−1)` 换成 `65535/(2^N−1)`，矩阵结构不变）。

**破坏性：是**（接口增加成员、继承 `IDisposable`）。**难度：低到中。** 新成员大多可以用默认接口实现过渡；
HEIF 的 16 位颜色转换是新内核，需要冻结并写 golden。

## 5. `ImageInfo`：解码前就知道颜色和分辨率

**现状。** `ColorEncoding` 只能在完整解码后从 `IDecodedImage` 拿到。调用方没法在解码前判断“这是 PQ HDR 吗”。
DPI 完全没有。

**2.0 设计。** `ImageInfo` 增加 init-only 属性，未设置时取原来的默认值：

```csharp
public ColorEncoding ColorEncoding { get; init; }   // 默认 AssumedSrgb
public Resolution?   Resolution    { get; init; }   // 像素密度及单位；文件没写时为 null
public bool          HasThumbnail  { get; init; }
```

**影响范围。** 每个 codec 的 `TryReadInfo`：PNG（cICP、gAMA、pHYs）、JPEG（JFIF 密度、Exif 分辨率）、
TIFF（XResolution、ResolutionUnit）、HEIF（`colr`/nclx）、WebP、BMP（biXPelsPerMeter）。

**破坏性：否**（纯新增）。**难度：低。** 这些字段多数已经在解析头部时读到，只是没有往外报。

## 6. 解码选项与能力协商：缩放、区域

**现状。** JPEG 包有 `JpegScale`（1/2、1/4、1/8 的 DCT 域缩放），但门面不走它；其他 codec 没有缩放或区域解码。
接口里没有传递这类请求的地方。

**2.0 设计。** 学 WIC 的协商方式：调用方先问“最接近的能做到什么”，解码器如实回答，**不静默回退**。

```csharp
public sealed record DecodeOptions
{
    public int ScaleDenominator { get; init; } = 1;   // 1 / 2 / 4 / 8
    public PixelRect? Region    { get; init; }        // 按存储坐标；null 为整幅
}

// IImageDecoder 新增：给定请求的目标尺寸，返回本 codec 能原生产生的最接近尺寸
static abstract bool TryGetClosestSize(ReadOnlySpan<byte> data, int requestedWidth, int requestedHeight,
                                       out int width, out int height);
```

不支持原生缩放的 codec，`TryGetClosestSize` 返回原尺寸，调用方就知道要自己缩放。区域解码对所有 codec 都提供，
做不到原生区域解码的就先整幅解码再裁剪，语义一致，只是没有性能收益。

**影响范围。** JPEG（接上已有的 `JpegScale`；区域按 MCU 行跳过）；HEIF（grid 只解码与区域相交的 tile，单 item
不加速）；JPEG 2000（分辨率级别缩减 reduce，目前还没实现）；TIFF（按 strip/tile 跳过）；其余 codec 用通用的
“解码后裁剪”。

**破坏性：是**（`TryDecode` / `TryDecodeInto` 增加参数）。**难度：中到高。** 接口本身简单；JPEG 的原生区域解码、
JPEG 2000 的 reduce 都是 codec 内部的实质工作，可以在 2.0 之后逐个补。

## 7. 取消

**现状。** 解码一张 50 MP 的 HEIF 要几百毫秒到几秒，开始后无法中断。

**2.0 设计。** 所有解码和编码入口增加 `CancellationToken ct = default`，被取消时抛出
`OperationCanceledException`。检查点放在粗粒度的位置（JPEG 每个 MCU 行、HEIF 每个 tile 和每个 CTU 行、PNG 每 N
行），不进入内层循环。

**影响范围。** 所有 codec 的主解码循环；HEIF 的 `Parallel.For` 改为使用 `ParallelOptions.CancellationToken`。

**破坏性：是**（签名变化）。**难度：中。** 要把 token 一路传进各 codec 内部；需要基准确认检查点没有带来可测的开销。

## 8. 元数据与缩略图

**现状。** 接口只暴露 ICC 和方向。`Sdcb.SimdCodecs.Exif` 包能从 JPEG、TIFF、PNG、WebP 中解析 Exif，但它和解码接口
是分开的，也没有 XMP；HEIF 的 Exif item 没有接入。转码时元数据会全部丢失。

**2.0 设计。** 接口层只负责提取和透传**原始块**；解析交给 Exif 包（Abstractions 不依赖它）。

```csharp
public sealed class ImageMetadata
{
    public ReadOnlyMemory<byte> Exif { get; }   // TIFF 头开始的 Exif 块，没有则为空
    public ReadOnlyMemory<byte> Xmp  { get; }   // UTF-8 XMP 包
    public ReadOnlyMemory<byte> Icc  { get; }
}

// IImageDecoder 新增
static abstract bool TryReadMetadata(ReadOnlySpan<byte> data, [NotNullWhen(true)] out ImageMetadata? metadata);
static abstract bool TryDecodeThumbnail(ReadOnlySpan<byte> data, [NotNullWhen(true)] out IDecodedImage? thumbnail);
```

各编码器也接受 `ImageMetadata`，写进对应的容器位置，这样“HEIC → JPEG”可以保留拍摄时间和 GPS。

**影响范围。** 提取：JPEG（APP1 Exif / XMP）、PNG（eXIf、iTXt XMP）、WebP（EXIF、XMP 块）、TIFF（标签）、HEIF
（Exif item、`mime` 类型的 XMP item）。缩略图：HEIF（`thmb` 引用）、JPEG（Exif IFD1）、TIFF（缩小尺寸的子文件）。
写入：JPEG、PNG、WebP、TIFF 编码器。

**破坏性：否**（纯新增）。**难度：中。** 每个格式的位置都不同，但都是容器层的工作，不碰像素管线。注意 Exif 里的
方向标签和 HEIF 的 `irot`/`imir` 可能冲突：透传时要么把方向标签改写为 1，要么在文档里说明以哪个为准。

## 9. 输入：`Stream` 与会话 API

**现状。** 所有入口接收 `ReadOnlySpan<byte>` 或 `ReadOnlyMemory<byte>`，只有 TIFF 包的 `ReadLayout` /
`ReadSegment` 能从可寻址流按块读取。

**为什么要 Stream：** 一张 3 MB 的 HEIC 解出 48 MB 的像素，所以对普通照片来说，文件本身在内存里不是主要开销。
`Stream` 真正解决的是：边下载边解码；BigTIFF 这类放不进内存的文件；调用方手里只有 `Stream`（ASP.NET 请求体、
`File.OpenRead`），现在必须自己拷进 `byte[]`（超过 85 KB 还会进 LOH）；`ReadOnlySpan` 签名不能用在 async 方法里。

**格式差异：** JPEG、PNG、GIF、QOI、PNM、BMP 是顺序格式，可以真正流式解码。HEIF 的 `iloc` 偏移可以指向文件任何
位置，`meta` 和 `mdat` 的先后顺序也不固定；TIFF 的 IFD 散落在各处。这些格式需要可寻址的源，遇到不可寻址的流
只能先缓冲。

**2.0 设计。** 内部抽象一个随机读字节源，再在其上建一个借鉴 WIC 的会话对象：

```csharp
public interface IByteSource : IDisposable
{
    long Length { get; }                          // 未知时为 -1
    bool CanSeek { get; }
    int Read(long offset, Span<byte> destination);
}
// 内置实现：内存（ReadOnlyMemory）、可寻址 Stream、内存映射文件；不可寻址的 Stream 按需缓冲

public sealed class ImageDecoder : IDisposable    // 由门面创建
{
    public static ImageDecoder Open(Stream stream, bool leaveOpen = false);
    public static ImageDecoder Open(ReadOnlyMemory<byte> data);
    public static ImageDecoder Open(string path);  // 内存映射
    public static ValueTask<ImageDecoder> OpenAsync(Stream stream, CancellationToken ct = default);

    public ImageFormat Format { get; }
    public int FrameCount { get; }
    public ImageFrame GetFrame(int index);
}

public sealed class ImageFrame
{
    public ImageInfo Info { get; }
    public ImageMetadata Metadata { get; }
    public TimeSpan? Duration { get; }
    public bool TryGetThumbnail([NotNullWhen(true)] out IDecodedImage? thumbnail);
    public bool TryGetClosestSize(int width, int height, out int closestWidth, out int closestHeight);
    public IDecodedImage Decode(DecodeOptions? options = null, CancellationToken ct = default);
    public void CopyPixels(PixelRect? region, PixelFormat format, Span<byte> destination, int stride,
                           DecodeOptions? options = null, CancellationToken ct = default);
}
```

`IImageFrameReader` 并入 `ImageDecoder` / `ImageFrame`：1.x 的读取器已经支持按下标访问，语义直接沿用（动画帧是
合成后的画布，页面是独立图像）。`ImageDecoder` 会保留解码状态（动画画布），和 1.x 的读取器一样不保证线程安全。

**分两步实施：**

1. **API 先行**：`Open` 先把整个输入读进内存，`CopyPixels` 先整幅解码再裁剪。所有 codec 立即可用，难度低。
2. **逐个 codec 换成真正的增量实现**：JPEG、PNG 逐行解码并直接从流读取；HEIF 按 tile 解码，只读需要的 item；
   TIFF 复用已有的按段读取。

**影响范围。** 第 1 步：Abstractions、门面、`IMultiFrameImageDecoder` 及 4 个多帧读取器。第 2 步：每个 codec 的
输入层。现在所有 codec 内部都假定输入是一个完整的 span，改成从 `IByteSource` 按需读取是结构性改动。

**破坏性：是**（`IImageFrameReader` / `IMultiFrameImageDecoder` 被替换）。**难度：第 1 步低，第 2 步高。**
第 2 步要配合模糊测试：输入层变化正是越界和挂起最容易出现的地方，JPEG 熵解码器改动后按 AGENTS.md 的要求跑
30 万次模糊测试。

## 10. 平面 YCbCr 输出

**现状。** HEIF 有内部的 `DecodeYuv`（只给 oracle 测试用）；JPEG 的 Y 平面只在 `Gray8` 输出时暴露。

**2.0 设计。** 对以 YCbCr 存储的格式，提供不做颜色转换的平面输出：

```csharp
public enum ChromaLocation : byte   // H.273 / H.265 chroma_sample_loc_type 的取值
{
    Left = 0, Center = 1, TopLeft = 2, Top = 3, BottomLeft = 4, Bottom = 5,
}

public readonly record struct PlanarInfo(int Width, int Height,
                                         int ChromaSubsamplingX, int ChromaSubsamplingY, int BitDepth,
                                         MatrixCoefficients Matrix, bool FullRange, ChromaLocation ChromaLocation);

// 解码器可选实现
public interface IPlanarImageDecoder : IImageDecoder
{
    static abstract bool TryReadPlanarInfo(ReadOnlySpan<byte> data, out PlanarInfo info);
    static abstract bool TryDecodePlanes(ReadOnlySpan<byte> data, Span<ushort> y, Span<ushort> cb, Span<ushort> cr,
                                         CancellationToken ct = default);
}
```

用途：转码时跳过“YCbCr → RGB → YCbCr”的往返（既快，又少一次舍入）；视频管线和 GPU 上传直接使用 YUV 纹理。

**为什么不是 `PixelFormat` 的成员。** `PixelFormat` 的前提是一块缓冲区、交错存储、一个 stride。平面格式有多块缓冲区，
各平面尺寸和 stride 不同（4:2:0 的色度是 `⌈W/2⌉ × ⌈H/2⌉`），布局变体很多（I420、YV12、NV12、NV21、P010、
yuv420p10le …），而且光有平面还解释不了像素，必须同时知道矩阵、范围和色度采样位置。所以它是单独的接口，带自己的
描述结构 `PlanarInfo`。

**边界：**

- **只输出解码出来的原样**：保持 codec 原生的色度采样方式和位深，Y、Cb、Cr 三个平面；8 位以上的样本放在 16 位容器里，
  **低位对齐**（即解码器产生的原始值，10 位的范围是 0..1023），不缩放。
- **`PlanarInfo` 必须完整**：矩阵、范围、色度采样位置都要给出。色度位置取码流里的 `chroma_sample_loc_type`；码流没写时
  按 H.265 的默认值（0，左侧）；JPEG 按 T.871（居中）。少了任何一项，用户自己转出来的颜色会偏。
- **平面格式之间不做转换**。最多额外提供 NV12（8 位）/ P010（16 位，高位对齐）这一种交错色度布局，因为 GPU 纹理和
  硬件编码器几乎只认它；其他变体留给用户或 FFmpeg。
- **HEIF 的几何变换**（`clap` / `irot` / `imir`）：
  - 旋转和镜像对 4:2:0 平面没有问题（旋转 90° 后仍是 4:2:0），按 RGB 路径同样的顺序应用到每个平面。
  - `clap` 的裁剪起点或尺寸是奇数时，色度与亮度无法对齐。这种情况下 `TryDecodePlanes` 返回 `false`，调用方改走 RGB
    路径；不做重采样，也不悄悄输出未裁剪的平面。
  - 4:2:2 旋转 90° 后会变成 4:4:0，无法用同一描述表示。目前 HEIF 只支持 4:2:0，遇不到这个问题；以后支持 4:2:2 时，
    带 90° 旋转的图同样返回 `false`。

**优先级：低。** 大多数用户用不到平面输出。它的价值集中在两处：我们自己的转码路径（例如 HEIC → JPEG 直接交接平面，
不经过 RGB），以及 GPU 和视频管线这类小众但需求明确的用户。建议先作为**内部能力**给转码路径使用，等有用户提出需要时
再公开。注意即使交接平面，源和目标的矩阵、范围或位深不同时（HEIC 的 limited range 对 JFIF 的 full range，10 位对 8 位）
仍要做一次 YCbCr 域内的转换，只是比经过 RGB 简单。

**影响范围。** HEIF（把 `DecodeYuv` 公开即可，HEVC 平面本来就是与 oracle 逐字节一致的契约）；JPEG（上采样之前的
分量平面）；WebP 有损（VP8 的 YUV 4:2:0）。

**破坏性：否。难度：低到中。** HEIF 几乎是现成的。JPEG 要注意：平面输出发生在 `JpegResample` 之前，不改变已冻结的
运算，但需要新增 golden。

## 11. 编码器抽象

**现状。** 编码器各有各的 API（`JpegEncoder`、`PngWriter`、`TiffWriter`、`GifEncoder`、`BmpEncoder`、`IcoEncoder`、
`QoiEncoder`、`PnmEncoder`、`WebpEncoder`），门面不做编码。`known-gaps.md` 里把“没有编码器抽象”记为**有意的决定**；
2.0 推翻这个决定，届时要同步更新 `known-gaps.md`。

**2.0 设计。** 参照 WIC 的帧会话：可以逐行推送像素，也可以写多帧，并且由编码器协商像素格式。

```csharp
public interface IImageEncoder
{
    static abstract ImageFormat Format { get; }

    /// 编码器把 requested 改成它能原生接收的最接近格式（例如 JPEG 把 Rgba64 改成 Rgb24）。
    static abstract PixelFormat GetClosestInputFormat(PixelFormat requested);

    static abstract IImageEncoderSession Create(IBufferWriter<byte> output, EncodeOptions? options = null);
}

public interface IImageEncoderSession : IDisposable
{
    IImageFrameEncoder AddFrame(int width, int height, PixelFormat format, ColorEncoding? color = null,
                                ImageMetadata? metadata = null, TimeSpan? duration = null);
    void Commit(CancellationToken ct = default);
}

public interface IImageFrameEncoder
{
    void WriteRows(ReadOnlySpan<byte> rows, int rowCount, int stride, CancellationToken ct = default);
}

// 门面便利入口
SimdCodecs.Encode(IDecodedImage image, ImageFormat format, Stream destination, EncodeOptions? options = null);
```

输出用 `IBufferWriter<byte>`：`PipeWriter.Create(stream)` 本身就是一个 `IBufferWriter<byte>`，所以 `Stream` 只需要
一个便利重载。`EncodeOptions` 是基类，各格式派生自己的选项（质量、压缩级别、无损/有损等）。HEIF 仍然只解码，
不提供编码器（HEVC 专利，见 AGENTS.md）。

**各编码器的流式难度不同：**

| 编码器 | 逐行写入 |
|---|---|
| PNG、BMP、QOI、PNM | 天然逐行，直接支持 |
| TIFF | 已有按 strip 写入的结构，直接支持 |
| JPEG | 需要缓冲一个 MCU 行（8 或 16 行）；渐进式和 Huffman 优化需要缓冲全部系数 |
| GIF | 需要整帧做调色板量化，内部缓冲一帧 |
| WebP 无损 | 全局变换和颜色缓存需要整幅图，内部缓冲 |
| WebP 有损 | 按宏块行，需要缓冲 16 行 |

不能逐行的编码器在会话内部缓冲，对调用方的契约不变，只是没有内存收益。

**输入格式。** 分契约、实施、验收三层。

*契约。* 每个编码器至少接收 5 种 8 位布局：`Rgba32`、`Bgra32`、`Rgb24`、`Bgr24`、`Gray8`，以及
`Rgba32Premultiplied`、`Bgra32Premultiplied`（按第 3 节的反预乘约定处理）。对这些格式，`GetClosestInputFormat`
原样返回；只有编码器存不下的精度（例如 JPEG 收到 16 位格式）才会改成别的格式。这条要求的目的是让 UI 框架的
帧缓冲直接进编码器，调用方不用自己转换：Avalonia、WPF、WinUI、System.Drawing、Direct2D 的位图大多是 BGRA，
而且往往是预乘的。`stride` 由 `WriteRows` 带进来，不要求调用方把帧缓冲先拷成紧密排列。输入格式决定输出的颜色类型：
`Gray8` 写成灰度（PNG 灰度、JPEG 单分量），不先展开成 RGBA。

*实施。* M0 先在适配器里逐行用 `PixelConverter` 把输入转成编码器内部的格式（同类转换：灰度仍是灰度，有 alpha
仍有 alpha，不一律转成 RGBA）。`WriteRows` 本来就是逐行的，只需一行临时缓冲，不需要整帧拷贝。为此 2.0 的
`PixelConverter` 要能以 `PixelFormat` 描述源格式（BGR 顺序、预乘），并带反预乘内核；1.x 的 `ConvertRow` 源格式
只有灰度 / 灰度+alpha / RGB / RGBA。

最终每个编码器都直接读原始格式，不再经过 `PixelConverter`。知道原始格式有三个好处，只有原生路径拿得到：

1. 转换并进编码器的第一个阶段，省掉一遍读写和临时行。例如 JPEG 的 RGB→YCbCr（`JpegEncoderKernels.ConvertRow`
   及 4:2:0 的下采样路径）按偏移直接从 BGRA 取 R 和 B，解码端的 `JpegColor` 已经用 `bgr` 开关做同样的事；PNG
   在滤波时顺手换通道。
2. 精度更高。有损编码器可以在 float 颜色转换里直接反预乘（`c × max / a`），省掉先反预乘到 8 位的那次舍入，
   低 alpha 区域的颜色更准。
3. 跳过不需要的工作。例如 `Gray8` 输入给 JPEG 直接作为 Y 分量，没有色度；alpha 被丢弃且输入是非预乘的，alpha
   字节完全不读。

*验收。*

- **无损编码器**（PNG、BMP、QOI、PNM、TIFF、WebP 无损、GIF）：原生路径的输出必须与“先用
  `PixelConverter` 转换，再编码”**逐字节相同**。`PixelConverter` 是参考实现，测试直接比较两者，与解码端
  `TryDecodeInto` 的契约相同。所以第 3 节的反预乘舍入要精确定义，不能各编码器自行其是。
- **有损编码器**（JPEG、WebP 有损）：契约是质量而不是字节，可以合并转换、在 float 里反预乘，以 `encode --gate`
  （及 WebP 对应的质量门限）为准；同一输入的 BGRA 与 RGBA 版本必须编码出相同的字节。

1.x 的 `JpegEncoder.Encode` 只接收紧密排列的 RGB(A)。在 2.0 之前，BGRA 帧缓冲要先逐行用
`PixelConverter.ConvertRow` 换成 RGBA：R/B 互换是对称的，把 BGRA 当作 RGBA 转成 `Bgra32` 得到的就是 RGBA。

**影响范围。** 9 个编码器各写一个适配器；门面；Mono AOT 冒烟测试要覆盖每个编码入口（AGENTS.md：触及门面后必须
跑 wasm 冒烟）。JPEG 编码的契约是质量而不是字节，适配器输出必须通过 `encode --gate`，渐进式和优化流仍要解码出与
基线完全相同的像素。

**破坏性：否**（纯新增；现有编码器 API 保留）。**难度：高。** 接口本身不复杂；难点是给每个编码器加上逐行输入，
同时保证输出字节或质量门限不变，并且基准不回退。

## 12. 内存：池化与释放

**现状。** `RasterImage` 只持有 `byte[]`，每次解码分配一整帧，批处理时 GC 压力大。

**2.0 设计。**

```csharp
public sealed class RasterImage : IDecodedImage
{
    public static RasterImage Allocate(int width, int height, int channels, SampleFormat format, ...);
    public static RasterImage Rent(int width, int height, int channels, SampleFormat format,
                                   ArrayPool<byte>? pool = null, ...);
    public static RasterImage Wrap(int width, int height, int channels, SampleFormat format,
                                   IMemoryOwner<byte> owner, ...);
    public void Dispose();   // 归还池或交给 GC；之后访问 Pixels 抛出 ObjectDisposedException
}
```

门面的 `DecodeOptions` 可以指定一个池；未指定时行为同 1.x（新分配，`Dispose` 可以不调用）。

**影响范围。** Abstractions；所有构造 `RasterImage` 的 codec；`HeifImage`（已经有一个空实现的 `Dispose`）。

**破坏性：是**（构造函数换成工厂方法；`IDecodedImage` 继承 `IDisposable`）。**难度：中。** 代码量不大，主要风险是
归还池之后仍然访问：所有内部持有 `Pixels` span 的地方都要审查，测试里加“释放后访问”的断言。

## 13. 汇总

| # | 项 | 破坏性 | 难度 | 主要影响 |
|---|---|:---:|:---:|---|
| 3 | `PixelFormat` 结构体（16 位、预乘） | 是 | 中 | Abstractions、12 个适配器、4 个多帧读取器、门面、JPEG/PNG/WebP 快速路径、测试 |
| 4 | `IDecodedImage` 16 位输出、`BitDepth`、`IDisposable` | 是 | 低–中 | Abstractions、HEIF 颜色转换 |
| 5 | `ImageInfo` 增加颜色与分辨率 | 否 | 低 | 各 codec 的 `TryReadInfo` |
| 6 | 解码选项与尺寸协商 | 是 | 中–高 | JPEG、HEIF、JPEG 2000、TIFF |
| 7 | `CancellationToken` | 是 | 中 | 所有 codec 的主循环 |
| 8 | 元数据透传与缩略图 | 否 | 中 | JPEG、PNG、WebP、TIFF、HEIF 的容器层，各编码器 |
| 9 | `Stream` 输入与会话 API | 是 | 低（第 1 步）/ 高（第 2 步） | 门面、多帧读取器、每个 codec 的输入层 |
| 10 | 平面 YCbCr 输出（低优先级，可先作内部能力） | 否 | 低–中 | HEIF、JPEG、WebP 有损 |
| 11 | 编码器抽象 | 否 | 高 | 9 个编码器、门面、wasm 冒烟测试 |
| 12 | 池化内存 | 是 | 中 | Abstractions、所有构造 `RasterImage` 的 codec |

## 14. 建议的实施顺序

1. **先定数值约定**（第 3 节的表格）。16 位 → 8 位已决定保留 `v >> 8`，所以现有 8 位输出的 golden 不变；
   新增的 16 位输出和预乘输出需要新的 golden。
2. **一次性完成破坏性改动**：第 3、4、6、7、12 节的签名变化，以及第 9 节第 1 步。破坏性改动只做一次，比 2.x 里
   逐步破坏好。这一步先不追求新功能的性能，新格式可以先走通用转换。
3. **纯新增的功能**：第 5、8、10 节，可以并行推进。
4. **编码器抽象**（第 11 节）：先做天然逐行的 PNG、BMP、QOI、PNM、TIFF，再做 JPEG、WebP、GIF。
5. **性能项**：第 9 节第 2 步（真正的流式输入）、第 6 节的原生区域解码和缩放，按基准逐个 codec 推进。这些不改变
   API，可以放在 2.x 的小版本里。

## 15. 验收要求

每一步都沿用 AGENTS.md 的规则：

- 8 位输出路径的 golden 不因 API 调整而改变（16 → 8 位沿用 `v >> 8`）。
- 每个新的向量内核都有标量版本，在 Vector256、Vector128、标量三个层级以及 x64、arm64 上输出一致。
- 触及门面或任一 codec 的适配器后，跑 NativeAOT 和 Mono AOT（wasm）冒烟测试；新的静态成员按具体方法组绑定委托。
- 输入层改动后跑对应格式的模糊测试（JPEG、BMP、QOI/PNM 的 30 万次浸泡）。
- 性能相关的改动按 `benchmarks/README.md` 做同一次运行内的 A/B，只以端到端数字为准。
- 编译保持零新增警告。
