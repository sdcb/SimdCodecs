# 已知缺口与局限

这里记录各编解码器目前**做不到**、**有意不做**和**还没调优**的地方，方便使用前判断是否合适，也作为后续工作的
清单。本页只写跨格式的内容，各格式的条目在 [`known-gaps/`](known-gaps/) 目录里。每一条都对应代码里实际的行为（拒绝时抛出的异常、门面返回 `false` 的情况等）。功能补上、性能调优完成后，
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
PNG 编码和解码，各有自己的基准项目。WebP 无损编码也已调优（基准项目的 `encode` 命令），默认力度下体积和速度都不输 libwebp，见 [WebP](known-gaps/webp.md)。

BMP 和 ICO（解码和编码）也已调优，基准项目是 `Sdcb.SimdCodecs.Bmp.Benchmarks`；QOI 和 Netpbm（解码和编码）同样已调优，基准项目是 `Sdcb.SimdCodecs.QoiPnm.Benchmarks`。

HEIF 解码也已调优（M3），基准项目是 `Sdcb.SimdCodecs.Heif.Benchmarks`：单核 HEVC 阶段快于 libde265，端到端快于 libheif，每个 tile 解完直接写进调用方缓冲区，见 [HEIF](known-gaps/heif.md)。

**未调优**：TIFF、JBIG2、GIF（解码和编码）、EXIF。JPEG 2000 已建立基准项目并测得初始基线（~2× libopenjp2），算术解码与并发优化尚未开始。它们的门面路径也比较朴素：`TryDecodeInto`
先完整解码出一张图，再整帧转换成目标像素格式，中间会分配一整帧内存；JPEG、PNG、WebP 则是直接写进调用方的缓冲区。

### 门面与公共接口

- **HEIF 要显式注册。** HEVC 有专利负担，`Sdcb.SimdCodecs.Heif` 不在元包里，门面也不引用它；安装后调用一次
  `SimdCodecs.Register(HeifImageDecoder.Registration)`，门面的全部入口才会处理 HEIF。注册前 `DetectFormat`
  仍报 `ImageFormat.Heif`，但 `IsDecodable` 为 `false`、`Try*` 返回 `false`。注册是进程级的：幂等、线程安全、
  不能撤销；注册的编解码器排在内置的之后嗅探。没有反注册、优先级、按名字查找，这是有意保持最小。
- **没有编码器抽象。** 门面只做解码；编码请直接用各格式包（`JpegEncoder`、`PngWriter`、`TiffWriter`、
  `GifEncoder`、`BmpEncoder`、`IcoEncoder`、`QoiEncoder`、`PnmEncoder`、`WebpEncoder`）。这是有意的决定。
- **门面不提供缩放解码。** JPEG 包有 `JpegScale`（1/2、1/4、1/8 的 DCT 缩放），但门面不会走到它；
  JPEG 2000 的分辨率级别缩减（reduce）还没有实现。
- **输入必须整个在内存里。** 所有入口接收 `ReadOnlySpan<byte>` / `ReadOnlyMemory<byte>`，没有 `Stream`
  入口（TIFF 包的 `ReadLayout` / `ReadSegment` 除外，它们可以从可寻址的流里按块读取）。多帧读取器也会持有
  整个文件。
- **`ColorEncoding` 不反映 ICC。** 只有格式自己的色彩标记（PNG 的 cICP，表示线性的 gAMA 1.0，HEIF 的 nclx
  `colr` 盒或 HEVC VUI）会写进 `IDecodedImage.ColorEncoding`；只带 ICC 的图片（例如 Display P3 照片）仍报默认的 sRGB。要知道 ICC 声明的
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

## 各格式

- [JPEG](known-gaps/jpeg.md)
- [Ultra HDR / 增益图 JPEG](known-gaps/jpeg-gainmap.md)
- [PNG](known-gaps/png.md)
- [TIFF](known-gaps/tiff.md)
- [WebP](known-gaps/webp.md)
- [GIF](known-gaps/gif.md)
- [BMP](known-gaps/bmp.md)
- [ICO / CUR](known-gaps/ico.md)
- [QOI](known-gaps/qoi.md)
- [Netpbm（PBM / PGM / PPM / PAM / PFM）](known-gaps/netpbm.md)
- [JPEG 2000](known-gaps/jpeg2000.md)
- [JBIG2](known-gaps/jbig2.md)
- [HEIF / HEIC](known-gaps/heif.md)
- [EXIF](known-gaps/exif.md)
