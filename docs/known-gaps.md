# 已知缺口与局限

这里记录各编解码器目前**做不到**、**有意不做**和**还没调优**的地方，方便使用前判断是否合适，也作为后续工作的
清单。每一条都对应代码里实际的行为（拒绝时抛出的异常、门面返回 `false` 的情况等）。功能补上、性能调优完成后，
相应条目从这里删掉。

约定：

- **不支持**：遇到这种输入时抛出 `NotSupportedException` / `InvalidDataException`，`Try*` 方法返回 `false`，
  不会静默输出错误的像素。
- **门面不提供**：格式包本身有对应 API，但 `Sdcb.SimdCodecs` 门面（`TryDecode` / `TryDecodeInto` /
  `TryOpenFrames`）不会走到它。
- **未调优**：功能正确、有测试，但还没有基准项目，也没有按“不慢于原生参考库”的标准优化过。

## 总体

### 性能调优进度

AGENTS.md 的目标是每个编解码器都达到或超过对应的原生参考库。目前**已调优**的有：JPEG 编码和解码、WebP 解码、
PNG 编码和解码，各有自己的基准项目。

**未调优**：TIFF、JPEG 2000、JBIG2、GIF（解码和编码）、EXIF。它们的门面路径也比较朴素：`TryDecodeInto`
先完整解码出一张图，再整帧转换成目标像素格式，中间会分配一整帧内存；JPEG、PNG、WebP 则是直接写进调用方的缓冲区。

### 门面与公共接口

- **没有编码器抽象。** 门面只做解码；编码请直接用各格式包（`JpegEncoder`、`PngWriter`、`TiffWriter`、
  `GifEncoder`）。这是有意的决定。
- **门面不提供缩放解码。** JPEG 包有 `JpegScale`（1/2、1/4、1/8 的 DCT 缩放），但门面不会走到它；
  JPEG 2000 的分辨率级别缩减（reduce）还没有实现。
- **输入必须整个在内存里。** 所有入口接收 `ReadOnlySpan<byte>` / `ReadOnlyMemory<byte>`，没有 `Stream`
  入口（TIFF 包的 `ReadLayout` / `ReadSegment` 除外，它们可以从可寻址的流里按块读取）。多帧读取器也会持有
  整个文件。
- **`ColorEncoding` 不反映 ICC。** 只有格式自己的色彩标记（PNG 的 cICP，以及表示线性的 gAMA 1.0）会写进
  `IDecodedImage.ColorEncoding`；只带 ICC 的图片（例如 Display P3 照片）仍报默认的 sRGB。要知道 ICC 声明的
  色彩空间，请读 `IccProfile`。
- **EXIF 方向只报告、不应用。** `ImageInfo.Orientation` 告诉你该怎样旋转或翻转，像素本身始终是存储时的
  方向。
- **浮点图像没有自动的 8 位投影。** 浮点 TIFF、增益图 JPEG 重建出的 HDR 等，`TryDecodeInto` 返回 `false`
  （增益图返回的是 SDR 基础图），`EnumerateFramesInto` 抛出 `NotSupportedException`。HDR 到 8 位需要色调映射，
  这是调用方的决定，不是编解码器的。只为查看的话，`PixelConverter.StretchToRgba8` 把颜色范围线性拉伸到
  0..255（线性光数据如增益图 HDR 再加 `srgbEncode: true`），但不做色调映射。
- **多帧读取器的帧数可能少于文件里的帧数。** `ImageInfo.FrameCount` 报告文件里有多少帧或页，但：
  - APNG：解码还没实现，读取器只给出 PNG 的默认图（1 帧）；
  - JBIG2：独立 `.jb2` 文件只解码第 1 页，读取器只有 1 帧。
- **接口里没有“时间戳 → 帧号”的辅助方法。** WebP 包的 `WebpAnimationDecoder.FrameIndexAt` 有，接口层没有；
  需要时可以按各帧的 `Duration` 自己累加。
- **`IImageFrameReader` 不是线程安全的**（动画需要在调用之间保留画布状态），每个线程各开一个。
- **一次性解码所有帧不在接口里**，是有意的：全部帧的内存是帧数 × 画布大小。需要时对索引循环即可；WebP 包的
  `WebpDecoder.DecodeAnimation` 带总像素上限。

## JPEG

- **只支持 8 位精度。** 12 位 JPEG（SOF 精度为 12）会被拒绝。
- **只支持 Huffman 编码的 SOF0 / SOF1 / SOF2**（baseline、extended、progressive）。算术编码（SOF9–SOF11）和
  分层（hierarchical）JPEG 不支持。
- **无损 JPEG（SOF3）不走门面。** 它由单独的 `LosslessJpeg.FromMemory` API 解码，`JpegDecoder` 和门面会拒绝
  这类文件。
- **EXIF 方向只从 APP1 的 Exif 段读取**，不看 XMP 里的 `tiff:Orientation`。
- **编码器**：只写 8 位；色度采样只有 4:4:4、4:2:0 和灰度（没有 4:2:2）；不写 restart interval；不支持算术
  编码和无损编码。

## Ultra HDR / 增益图 JPEG

- **重建出的 HDR 不带 ICC。** `IDecodedImage.IccProfile` 有意留空：浮点结果是线性光，底图的 ICC 描述的是
  非线性的显示曲线，套上去会误导色彩管理。需要底图的配置文件时，用 `JpegDecoder.ReadIccProfile(file)` 读取，
  并注明它属于 SDR 底图。原色仍会报告：`ColorEncoding.Primaries` 取自底图的 ICC（`IccProfiles.TryGetPrimaries`
  能识别 BT.709、Display P3、BT.2020），没有 ICC 时按 sRGB 报 BT.709，识别不了时报 `Unspecified`。
- **只支持 SDR 基础图的形式。** 基础图本身是 HDR 的文件（`BaseRenditionIsHDR=true`）不支持 HDR 重建，门面会
  退回去解码它的主图。

## PNG

- **APNG 不解码**：`acTL` 的帧数会报告，但只能解出默认图；**也不能编码 APNG**。
- **编码器不能写 Adam7 隔行图**（解码支持）。
- **不读取 `bKGD`、`sBIT`、`tIME` 等辅助块。** 目前读取的有：`PLTE`、`tRNS`、`gAMA`、`cHRM`、`sRGB`、`iCCP`、
  `cICP`、`mDCV`、`cLLI`、`pHYs`、`eXIf`、`tEXt`、`zTXt`、`iTXt`。
- **cICP 标为 PQ / HLG 的 HDR 图像，`TryDecodeInto` 返回 `false`**：它们没有正确的 8 位投影，请用 `TryDecode`
  拿 16 位原始数据。
- 性能数据和与 libpng / ImageSharp 的逐项对比见
  [PNG 基准结果](../benchmarks/Sdcb.SimdCodecs.Png.Benchmarks/results/)。

## TIFF

- **读取不支持**：
  - JPEG 压缩；
  - BigTIFF；
  - CCITT 2D 的扩展码（uncompressed mode）。
- **1/2/4 位采样只支持每像素一个无符号采样**（二值图和低位深灰度）；多通道的低位深页面不支持。
- **门面不呈现的页面**：调色板、CMYK、YCbCr、CIELab 这几种 photometric，以及有符号整数和 32 位无符号整数采样，
  门面返回 `false`（`TiffReader` 本身仍能读出原始采样）。
- **写入器只写未压缩和 Deflate（含 ZlibPkzip）**，不写 LZW、PackBits 或 CCITT。
- **按页访问每次都从文件开头沿 IFD 链走到该页**（只跳指针、不解码），页数很多时随机访问有线性开销。
- 未调优。

## WebP

- **没有编码器。**
- **保留的 VP8 色彩空间标志不支持**（规范保留值）。
- **性能**：在接近照片内容、不带 alpha 的有损文件上与 libwebp 持平（0.87–1.13×），没有全面领先；其他类别更快，
  见 [WebP 基准结果](../benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks/optimization/m4-final-2026-10-25-amd-ryzen-7-5800x.md)。

## GIF

规范没有定死、由解码器决定的行为（初始画布、超出调色板的索引、截断数据、循环次数的含义等）不算缺口，它们的
处理方式列在 [GIF 包的 README](../src/Sdcb.SimdCodecs.Gif/README.md) 里。

- **没有基准项目，也没有质量门槛**，解码和编码都未调优。LZW 解码是逐码查表的朴素实现。
- **编码器只用一个全局调色板**，整个动画共用；不写每帧的局部调色板。颜色变化大的长动画，画质会受限。
- **量化器的直方图每通道只有 32 级。** 落进同一格的颜色无法再分开。实测例子：一张恰好有 256 种相近颜色、外加
  透明像素的图，只量化出 224 色，而上限是 255 色。
- **最近色查找是逐色遍历调色板**（带缓存），颜色很多的大图编码会比较慢。
- **带透明的动画每帧都写整帧**，并在下一帧前清除，文件比只写变化区域的方式大；不透明动画才做变化矩形的差分。
- **编码器不写隔行 GIF。**
- **帧延时按原值报告**：0 就是 0，不做浏览器那种“当作 100 ms”的改写。

## JPEG 2000

- **只支持 Part 1（T.800），定位是 PDF 里的 `/JPXDecode`。**
- **不支持**：
  - 进度顺序变更（POC）；
  - 打包的包头（PPM、PPT）；
  - 感兴趣区域（RGN / MAXSHIFT）；
  - 在 tile-part 头里覆盖主头参数的标记。
- **分量位深最多 16 位，且所有分量位深必须相同。**
- **门面只呈现 1 或 3 个分量、且不是 sYCC 的图像**；带 alpha、CMYK、sYCC 的图门面返回 `false`，
  `Jpeg2000Decoder.Decode` 仍会返回全部分量。
- **没有分辨率缩减（reduce）解码。**
- 未调优。

## JBIG2

- **独立 `.jb2` 文件只解码第 1 页。** PDF 内嵌的 JBIG2 流没有文件头可供识别，需要直接调用
  `Jbig2Decoder.Decode(embedded, globals, width, height)`，门面不处理。
- **不支持**：
  - EXTTEMPLATE 通用区域；
  - 使用 HENABLESKIP 的半色调区域；
  - MMR 编码的半色调区域和图案字典。
- 未调优。

## EXIF

- **只读取 JPEG、TIFF、PNG 和 WebP 里的 EXIF**（`ExifReader`）；HEIF、AVIF 等容器不读。
- **门面不返回 EXIF。** `ImageInfo` 只带方向；其他字段请把同一份文件交给 `ExifReader.FromBytes`。
