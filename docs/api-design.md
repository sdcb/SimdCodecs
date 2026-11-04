# Abstractions 2.0：API 设计

本文描述 `Sdcb.SimdCodecs.Abstractions` 与门面在 2.0 的设计和取舍，取代 2.0 之前的设计草案
（`abstractions-2.0-design.md`）。从 1.x 升级见 [migrating-to-2.0.md](migrating-to-2.0.md)；做不到、有意不做的地方见
[known-gaps.md](known-gaps.md)。

## 保留的原则

- **静态抽象成员 + 显式注册。** codec 不靠反射发现，NativeAOT 和 Mono AOT 都安全。每个 codec 用对象初始化器、以自己
  具体类型的方法组构造 `Registration`（解码器的 `ImageDecoderRegistration`、编码器的 `ImageEncoderRegistration`），门面
  只引用这些属性，从不在泛型方法里绑定委托（Mono AOT 会在那里陷入）。wasm 冒烟测试覆盖所有门面入口。
- **`ReadOnlySpan<byte>` 一次性 API 是热路径。** 会话 API（`ImageDecoder`）建在它之上。
- **错误约定。** 不可信输入：`Try*` 返回 `false`（抛出型 API 抛 `InvalidDataException`），不挂起，不无界分配。取消
  抛 `OperationCanceledException`；调用方给的选项做不到（不支持的缩放、越界的区域）抛 `NotSupportedException` /
  `ArgumentException`，不静默回退。
- **`IDecodedImage` 是保真层**：原始通道数和样本类型、主机字节序、非预乘 alpha。

## 像素格式：`PixelFormat`

`ChannelLayout`（Rgba、Bgra、Rgb、Bgr、Gray、GrayAlpha）× 样本宽度（8 / 16 位）× `AlphaMode`（Straight、
Premultiplied，只对 Rgba / Bgra 有意义）。构造函数拒绝无意义的组合，所以集合封闭、每个值都合法；`default` 是
`Rgba32`。浮点不在其中：没有规范的整数映射，走 `IDecodedImage.ToFloats()`。

数值约定已冻结（改动即输出变化）：

| 转换 | 约定 |
|---|---|
| 8 → 16 位 | `v × 257` |
| 16 → 8 位 | `v >> 8`（1.x 的约定，8 位 golden 因此不变） |
| N 位源 → 16 位容器 | 满量程映射，`IDecodedImage.BitDepth` 记录真实位深 |
| 预乘 | `round(c × a / max)`，在目标位深上做 |
| 反预乘 | `a = 0` 时颜色为 0；否则 `min(max, round(c × max / a))`，在源位深上做 |
| 顺序 | 反预乘 → 改位深 → 改布局 → 预乘 |
| 彩色 → 灰度 | BT.601 `(77·R + 150·G + 29·B + 128) >> 8`，在目标位深上做 |
| 丢 alpha | 直接丢，不合成 |

`PixelConverter` 是参考实现：任意格式到任意格式（`ConvertRow`、`Convert`、`ToPacked`），向量内核都有标量版本，
测试对 16 × 16 个格式组合逐字节比对标量参考。

## 解码

`IImageDecoder`：`CanDecode`、`TryReadInfo`、`TryGetClosestSize`、`TryDecode`、`TryDecodeInto`、`TryReadMetadata`、
`TryDecodeThumbnail`。

- **`TryDecodeInto` 的契约**：结果等于 `TryDecode` 的图像（同样的选项）的 `Pixels` 经 `PixelConverter` 转换。原生
  快速路径必须逐字节一致。例外两种，都需在 codec 文档里写明：存储亮度的 codec 输出灰度时给存储的亮度（YCbCr JPEG 的
  Y 平面）；从比 8 位更深的编码样本直接转换到 8 位格式的 codec（HEIF Main 10），只舍入一次，与收窄 16 位图像最多差 1。
- **`DecodeOptions`**：`ScaleDenominator`（1/2/4/8，只有原生能做到的 codec 接受，其他抛 `NotSupportedException`）、
  `Region`（存储坐标；做不到原生区域解码的 codec 整幅解码再裁剪）、`Pool`（像素从池中租用，释放图像即归还）。
  调用方先用 `TryGetClosestSize` 问 codec 原生能做到的最接近尺寸（WIC 的协商方式）。
- **取消**：检查点放在粗粒度位置（JPEG 每个 MCU 行、HEIF 每个 tile 和每 64 行输出、PNG / QOI 等每 64 行、WebP 每个
  宏块行），不进内层循环。
- **`ImageInfo`** 在解码前给出 `ColorEncoding`（判断是不是 PQ / HLG）、`Resolution`（单位按文件声明）、`HasThumbnail`。
- **`ImageDecoderFallback`** 给不具备原生能力的 codec 一行实现：解码再转换、原尺寸、无元数据、无缩略图。它和
  `DecodeOptions` 的 `Validate` / `Apply`（扩展成员）、`BufferedEncoderSession` 一样放在
  `Sdcb.SimdCodecs.Abstractions.CodecSupport`：只有写 codec 的人需要，用户的 IntelliSense 里看不到。

## 元数据与缩略图

接口层只提取和透传原始块（`ImageMetadata`：Exif 从 TIFF 头开始，XMP 是 UTF-8 包，ICC），解析交给 Exif 包，
Abstractions 不依赖它。提取：JPEG（APP1 Exif / XMP、APP2 ICC）、PNG（eXIf、iTXt XMP、iCCP）、WebP（EXIF、XMP、ICCP）、
TIFF（XMP 标签 700、ICC）、HEIF（`cdsc` 关联的 Exif item 和 `mime` XMP item、`colr` ICC）。缩略图：JPEG Exif IFD1、
HEIF `thmb`。编码器把帧的 `ImageMetadata` 写进各自格式的位置。ICC 只有这一个入口（`IDecodedImage.Metadata.Icc`）。
`ImageMetadata` 是 record，用初始化器构造、`with` 修改，以后加块（IPTC、文本、HDR 静态元数据）不破坏兼容。

## 会话：`ImageDecoder` / `ImageFrame`

借鉴 WIC：`ImageDecoder.Open(Stream | path | memory)`、`OpenAsync`；`Format`、`FrameCount`、`LoopCount`、`Metadata`；
`GetFrame(i)` 给出 `Info`、`Duration`、`Decode(options)`、`CopyPixels(region, format, buffer, stride)`、
`TryGetClosestSize`、`TryGetThumbnail`。输入经过 `IByteSource`（内存、可寻址流、内存映射文件，不可寻址流先缓冲）。
`IByteSource` 放在 Abstractions 而不是门面里，这样 2.x 的 codec 可以直接接收它、只读需要的部分。
2.0 里所有 codec 仍在整份文件上解码；逐 codec 换成增量输入是 2.x 的性能项，不改 API。

## 平面 YCbCr：`IPlanarImageDecoder`

只输出解码出来的原样：codec 原生的色度采样和位深，Y、Cb、Cr 三个紧密排列的平面，8 位以上的样本低位对齐在 16 位
容器里。`PlanarInfo` 必须完整（矩阵、范围、色度采样位置），否则用户转出来的颜色会偏。平面格式之间不做转换。
HEIF：平面是变换前的画布；带 `clap` / `irot` / `imir` 的图没有平面输出（`TryReadPlanarInfo` 返回 `false`）。

## 编码：`IImageEncoder`

`GetClosestInputFormat`、`Create(IBufferWriter<byte>, EncodeOptions?)`；会话 `AddFrame(w, h, format, color, metadata,
duration)` → `WriteRows(rows, count, stride)` → `Commit()`。

- **输入格式**：每个编码器接收每个 `PixelFormat`、任意 stride。`GetClosestInputFormat` 说明它原样存储的格式，让降
  精度对调用方可见（只能存 8 位的格式把 16 位协商成 8 位）；仍传入时由 `PixelConverter` 转换（16→8 `v >> 8`、反预乘、
  丢 alpha 不合成）。
- **验收**：会话的输出与“先 `PixelConverter` 转换，再调用该格式原有的编码 API”逐字节相同；JPEG 的质量契约照旧由
  `encode --gate` 守住，BGRA 与 RGBA 输入编码出相同的字节。
- 2.0 里每个编码器都在会话内缓冲整幅图（`BufferedEncoderSession`），逐行流式是以后的优化，不改契约。
- HEIF 只解码，不提供编码器（HEVC 专利，见 AGENTS.md）。

## 内存

`RasterImage` 只有工厂方法：`Wrap`（接管调用方填好的数组或 `IMemoryOwner`）、`Allocate` / `Rent` / `Create`（创建后
经 `WritablePixels` 填充）、`Crop`。`IDecodedImage : IDisposable`；释放后访问 `Pixels` 抛 `ObjectDisposedException`。
不指定池时行为同 1.x，`Dispose` 可以不调用。

## 兼容性

2.0 之后不再破坏兼容，形状按这个目标定：

- 注册类和 `ImageMetadata` 用 `init` 属性（门面需要的标 `required`），不用位置参数的构造函数：加成员不会让已编译的
  调用方在运行时找不到方法。只有成对出现的平面委托由 `SimdCodecs.Register` 调 `Validate()` 检查。
- 每个包都有 `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt`（`Microsoft.CodeAnalysis.PublicApiAnalyzers`）：
  公开 API 的任何变动都要在这两个文件里出现，否则编译报错；发布一个版本时把 Unshipped 并进 Shipped。
