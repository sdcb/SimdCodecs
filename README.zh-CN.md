<img src="https://raw.githubusercontent.com/sdcb/SimdCodecs/main/assets/icon.png" width="64" align="right" alt="Sdcb.SimdCodecs icon">

# Sdcb.SimdCodecs [![CI](https://github.com/sdcb/SimdCodecs/actions/workflows/dotnet.yml/badge.svg)](https://github.com/sdcb/SimdCodecs/actions/workflows/dotnet.yml) [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs) [![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

[English](README.md) | **简体中文**

纯托管的 .NET 10 图像编解码库：JPEG、PNG、TIFF、WebP、GIF、BMP、ICO / CUR、QOI、Netpbm、JPEG 2000、JBIG2，外加 EXIF 和 ICC；HEIF / HEIC 解码是需要自行启用的独立包。

- **没有原生依赖。** .NET 能跑的地方都能跑，包括 NativeAOT 和 browser-wasm，各平台输出完全一致。
- **和原生库一样快。** SIMD（AVX2、SSE、AdvSimd）加速，带标量回退：JPEG 解码耗时是 libjpeg-turbo 的 0.82×，PNG 解码照片是 libpng 的 0.88×，WebP 解码是 libwebp 的 0.53–0.88×（见[性能](#性能)）。
- **结果精确。** WebP 解码与 libwebp 逐字节一致，HEIF 的 HEVC 平面与 ffmpeg 一致，PNG 和无损 JPEG 2000 逐位一致；在需要色度上采样的图上，JPEG 解码比 libjpeg-turbo 默认设置的精度高约 1 dB。
- **内存占用低。** 可以按任意像素布局直接解进你自己的缓冲区；JPEG 解码不分配托管内存。

![JPEG、PNG、WebP、TIFF、JPEG 2000 和 JBIG2 图片，全部由 Sdcb.SimdCodecs 解码](https://cv-public.sdcb.ai/simdcodecs/readme/showcase-v1.jpg)

<sub>上图每一格都由本库解码，图片来自 LINQPad 样例素材（[生成方式](tools/sample-assets/README.md)）。</sub>

## 快速上手

```bash
dotnet add package Sdcb.SimdCodecs
```

```csharp
using Sdcb.SimdCodecs;
using Sdcb.SimdCodecs.Abstractions;
using Sdcb.SimdCodecs.Jpeg;

byte[] file = File.ReadAllBytes("photo.webp");   // JPEG、PNG、TIFF、WebP、GIF、BMP、ICO、CUR、QOI、Netpbm、JPEG 2000 或 JBIG2

// 门面类根据文件头识别格式。一次调用即可得到指定布局的像素，并按 EXIF 方向转正；
// 不支持的格式抛 NotSupportedException，文件损坏抛 InvalidDataException，消息带 codec 给出的具体原因。
using PixelBuffer image = ImageCodecs.Decode(file, PixelFormat.Rgba32, new DecodeOptions { ApplyOrientation = true });

// 编码成 JPEG：SIMD 加速，支持 baseline / 优化 Huffman / progressive，4:2:0 或 4:4:4。
// 传入 image.Metadata 可保留 ICC 和 Exif（其中的方向已改写为 1）。
using FileStream output = File.Create("photo.jpg");
ImageCodecs.Encode(image.Pixels, image.Width, image.Height, image.Format, ImageFormat.Jpeg, output,
    stride: image.Stride, options: new JpegEncodeOptions { Quality = 90 }, metadata: image.Metadata);
```

常见用法：

```csharp
// 服务端先看文件头，再决定要不要分配内存。
if (!ImageCodecs.TryReadInfo(file, out ImageInfo info) || (long)info.Width * info.Height > 50_000_000)
    return Results.BadRequest();

// 按任意布局、任意行跨度解进你自己的内存（位图、Mat、上传缓冲区）。
// SKBitmap 和 Mat 的写法见 skiasharp、opencvsharp 两个样例。
ImageCodecs.TryDecodeInto(file, buffer, stride, PixelFormat.Bgra32Premultiplied);

// 缩略图：JPEG 在 DCT 域缩小 1/2–1/8，其他格式用 box 滤波。
using IDecodedImage small = ImageCodecs.Decode(file, new DecodeOptions { ScaleDenominator = 8 });

// HEIF / HEIC（iPhone 照片）：安装 Sdcb.SimdCodecs.Heif，启动时注册一次。
ImageCodecs.Register(HeifImageDecoder.Registration);
```

<details>
<summary>门面的全部入口</summary>

- `ImageCodecs.Decode(file, format, options)` / `TryDecodeInto(file, buffer, stride, format, options)`：任意 `PixelFormat`（RGBA、BGRA、给 WPF / Direct2D / Skia 用的 `Bgra32Premultiplied`、RGB、给 OpenCV 用的 BGR、给 OCR 用的灰度，以及 16 位的 `Rgba64`、`Gray16` 等），任意行跨度。JPEG 和 PNG 原生输出 8 位布局，不经过 RGBA 整帧；彩色 JPEG 的灰度就是它的 Y 平面。
- `ImageCodecs.Decode(file, options)` / `TryDecode(file, out image, options)`：保留原始布局（通道数、8/16 位或浮点采样、真实位深），文件的 ICC、Exif、XMP 在 `image.Metadata` 里。释放图像即归还池化内存。
- `DecodeOptions`：`Region`（裁剪；TIFF 和 HEIF 只读与区域相交的条带或分块，JPEG、PNG、WebP 只转换区域内的行）、`ScaleDenominator`（每个 codec 都接受任意 2 的幂：JPEG 的 1/2–1/8 和 JPEG 2000 的分辨率级别是原生的，其余用 box 滤波；`TryGetClosestSize` 告诉你 codec 原生能做到什么）、`Pool`（像素用的 `ArrayPool<byte>`）、`ApplyOrientation`（应用 EXIF / TIFF 方向，Exif 里的方向改写为 1；缓冲区尺寸用 `GetOutputSize(info)` 算）。所有入口都接受 `CancellationToken`。
- `ImageCodecs.TryReadInfo(file, out info)`：只读文件头：格式、尺寸、存储位深、颜色编码（是不是 PQ / HLG HDR）、分辨率（DPI）、EXIF 方向、帧数或页数、有没有缩略图。
- `ImageCodecs.TryReadMetadata(file, out metadata)` / `TryDecodeThumbnail(file, out thumb)`：取出 Exif、XMP、ICC 原始块供转码透传，以及内嵌的预览图（Exif IFD1、HEIF `thmb`）。没开 `ApplyOrientation` 解出的像素，可以事后用 `PixelTransform.Orient` 转正。
- `ImageDecoder.Open(stream | path | memory)` / `OpenAsync(stream)`：WIC 式的会话：`Format`、`FrameCount`，再 `GetFrame(i).Decode(options)` 或 `.CopyPixels(region, format, buffer, stride)`。文件路径用内存映射；上传这类不可寻址的流会替你缓冲。
- `ImageCodecs.TryOpenFrames(file, out reader)`：按索引、以任意布局逐帧解码：动画 WebP / GIF / APNG 的帧及其时长，多页 TIFF 和 JBIG2 的各页，图标里的每张图。`reader.EnumerateFramesInto(buffer, stride, format)` 用同一块缓冲区按顺序播放。
- `ImageCodecs.Encode(image, format, stream | writer, options)` / `Encode(pixels, width, height, pixelFormat, format, …)` / `CreateEncoder(format, writer)`：每个编码器都接收任意 `PixelFormat` 和任意行跨度，并写入图像的元数据；只能存 8 位的格式会在哪里降精度，用 `GetClosestInputFormat` 可以先看到。options 是该格式自己的类型（`JpegEncodeOptions`、`WebpLossyEncodeOptions` 等；null 表示默认值）；WebP 不传 options 时为有损。编码会话可以按行带分批写入，也可以写多帧动画。
- `ImageCodecs.TryReadPlanarInfo` / `TryDecodePlanes`：按编码原样输出 YCbCr 平面（HEIF），供视频管线和 GPU 上传使用。
- `ImageCodecs.DetectFormat(file)`：还能认出不支持解码的格式（AVIF、JPEG XL 等）；`IsDecodable(format)` 判断能否解码。

门面只覆盖各格式共有的功能。每种格式的包有自己更丰富的 API：TIFF 按块读取、WebP 动画按时间戳定位、Ultra HDR 重建、无损 JPEG（`LosslessJpeg.FromMemory`，SOF3，门面不接受）等。门面和各 codec 做不到的事列在 [docs/known-gaps.md](docs/known-gaps.md)。

</details>

从 1.x 升级：见 [docs/migrating-to-2.0.md](docs/migrating-to-2.0.md)。

## 支持的格式

| 格式 | 包 | 解码 | 编码 | 特性 |
|---|---|:-:|:-:|---|
| JPEG | [`Sdcb.SimdCodecs.Jpeg`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg) | ✅ | ✅ | baseline 和 progressive、任意采样、CMYK / YCCK、1/2–1/8 缩放、无损（SOF3）；编码 baseline / 优化 Huffman / progressive，可写入 EXIF、XMP 和 ICC |
| Ultra HDR JPEG | [`Sdcb.SimdCodecs.Jpeg.GainMap`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg.GainMap) | ✅ | ✅ | 增益图读取、组装和 HDR 重建（Ultra HDR / Adobe） |
| PNG | [`Sdcb.SimdCodecs.Png`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Png) | ✅ | ✅ | 所有位深；有损调色板量化（pngquant 的用途）；APNG；HDR 标记（cICP、mDCV、cLLI） |
| TIFF | [`Sdcb.SimdCodecs.Tiff`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Tiff) | ✅ | ✅ | 多页、BigTIFF、8/16/32 位整数和浮点、条带或分块；JPEG、Deflate、LZW、PackBits、CCITT G3/G4 |
| WebP | [`Sdcb.SimdCodecs.Webp`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Webp) | ✅ | ✅ | 有损、无损、alpha、可跳转的动画；与 libwebp 逐字节一致；编码有损和无损的静态图及动画 |
| GIF | [`Sdcb.SimdCodecs.Gif`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Gif) | ✅ | ✅ | 可跳转的动画；编码精确调色板或带抖动的感知量化调色板 |
| BMP | [`Sdcb.SimdCodecs.Bmp`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Bmp) | ✅ | ✅ | 所有 Windows 和 OS/2 变体、RLE、位域、内嵌 PNG；与 WIC 逐像素一致 |
| ICO / CUR | [`Sdcb.SimdCodecs.Ico`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Ico) | ✅ | ✅ | 图标或光标里的每张图，PNG 或 DIB，热点坐标；多尺寸编码 |
| QOI | [`Sdcb.SimdCodecs.Qoi`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Qoi) | ✅ | ✅ | RGB 和 RGBA |
| Netpbm | [`Sdcb.SimdCodecs.Pnm`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Pnm) | ✅ | ✅ | PBM、PGM、PPM 的 ASCII 和二进制格式（最高 16 位）、PAM、PFM 浮点 |
| JPEG 2000 | [`Sdcb.SimdCodecs.Jpeg2000`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg2000) | ✅ | | Part 1，用于 PDF 的 `/JPXDecode`；JP2 alpha；降分辨率解码；tile 多核解码 |
| JBIG2 | [`Sdcb.SimdCodecs.Jbig2`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jbig2) | ✅ | | T.88，用于 PDF 的 `/JBIG2Decode` |
| HEIF / HEIC | [`Sdcb.SimdCodecs.Heif`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Heif) | ✅ | | 手机照片：HEVC Main / Main 10 / Main Still Picture、网格图、Main 10 输出 16 位、YCbCr 平面；与 ffmpeg 逐字节一致 |
| EXIF | [`Sdcb.SimdCodecs.Exif`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Exif) | ✅ | ✅ | 读取 JPEG、TIFF、PNG、WebP、HEIF 和 AVIF 的 Exif；`ExifWriter` 为编码器生成数据块 |
| ICC | [`Sdcb.SimdCodecs.Color.Icc`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Color.Icc)、[`Sdcb.SimdCodecs.Jpeg.IccInjector`](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg.IccInjector) | ✅ | ✅ | 解码器返回内嵌的配置文件；自带 sRGB v4 配置文件；可把配置文件写进已有的 JPEG |

`Sdcb.SimdCodecs` 是门面包，引用上表中除 HEIF 以外的所有包；`Sdcb.SimdCodecs.Abstractions` 是公共类型（`PixelFormat`、`DecodeOptions`、`IImageDecoder` 等）。所有包统一版本号，同时发布。

**HEIF 是需要自行启用的独立包。** HEVC 有专利负担，MIT 许可不授予任何专利许可，所以门面不引用 `Sdcb.SimdCodecs.Heif`：评估过 HEVC 专利对你的产品的影响之后，安装它并调用一次 `ImageCodecs.Register(HeifImageDecoder.Registration)`。

<details>
<summary>各包的详细特性</summary>

- **Jpeg**：T.81 解码器，支持 baseline、progressive、restart interval、任意采样方式，灰度 / YCbCr / RGB / CMYK / YCCK，1/2–1/8 缩放解码，无损（SOF3，经由 `LosslessJpeg.FromMemory`）。SIMD 编码器支持 baseline、优化 Huffman 或 progressive；4:4:4 / 4:2:0 / 灰度；restart interval；可写入 EXIF（APP1）、XMP 和 ICC 配置文件（APP2）。
- **Png**：所有标准位深（包括 1/2/4 位灰度和调色板），tEXt / zTXt / iTXt 文本块，PNG-3 的 cICP / mDCV / cLLI HDR 标记。`PngWriteOptions.Quantize` 像 pngquant 一样把图片降到最多 256 色（含半透明）：在基准语料上，同等 SSIM 下文件比 pngquant 的小（`benchmarks/Sdcb.SimdCodecs.Quantize.Benchmarks`）。APNG：`PngAnimationDecoder` 支持所有 dispose / blend 操作并可跳帧；`PngAnimationEncoder` 只写变化矩形，并合并不变的帧。
- **Tiff**：多页，经典 TIFF 和 BigTIFF；8/16/32 位整数和浮点；Deflate / LZW（另可读 PackBits 和带 JPEGTables 的 JPEG）；条带或分块；chunky 或 planar；按需读取单个条带或分块，区域解码只读与区域相交的条带或分块；读取 1/2/4 位页面（扩展为 8 位）、CCITT 传真（Group 3 1D/2D、Group 4、Modified Huffman）和浮点预测器；门面把调色板、CMYK、YCbCr 页面呈现为 RGB，额外采样呈现为 alpha。写入用 Deflate。
- **Webp**：RFC 6386 / RFC 9649：VP8 有损、VP8L 无损、ALPH、带跳转的动画、ICC / EXIF / XMP。`WebpAnimationDecoder` 另外提供按时间戳定位和每帧的矩形。`WebpEncoder.EncodeLossless` 编码 VP8L 静态图（含 alpha），压缩力度 0–9；`WebpEncoder.EncodeLossy` 编码 VP8 静态图，质量 0–100、力度 0–6，透明度写进 ALPH 平面；两者都可写入 ICC / Exif / XMP。`WebpAnimationEncoder` 和 `WebpEncoder.EncodeAnimation` 编码动画：有损、无损，或每帧取两者中较小的；输入可以是整帧画布（只写变化的矩形，合并不变的帧），也可以是调用方给定的 ANMF 帧。
- **Gif**：GIF89a：全局 / 局部调色板、隔行、透明、全部 dispose 方式、NETSCAPE2.0 循环次数；合成好的帧，可跳转。编码器：静态图和动画，精确调色板或感知量化（OKLab 盒切分 + k-means，可选边缘自适应的 Floyd–Steinberg 抖动），不透明动画只写变化的矩形。规范没有定死的行为列在包的 README 里。
- **Bmp**：Windows DIB 和 OS/2 位图：core、OS/2 2.x、40 字节、V2–V5 各版头部；1/2/4/8 位调色板和 16/24/32 位；`BI_BITFIELDS` / `BI_ALPHABITFIELDS`；带 delta 的 RLE4 / RLE8、OS/2 RLE24 和 Huffman 1D；`BI_PNG`；自顶向下的行；V5 ICC；packed DIB（剪贴板、图标图像）。编码调色板（精确或量化）、RLE、555 / 565、24 / 32 位。与 WIC 逐像素比对过；文档没有定死的行为列在包的 README 里。
- **Ico**：基于 Bmp 和 Png 的图标和光标：读出每张图，默认取最大、位深最高的一张，带热点坐标；编码多尺寸图标和光标，每张图可选 PNG 或 1/4/8/24/32 位 DIB。
- **Qoi**：按 QOI 规范实现：3 通道和 4 通道图像；会报告色彩空间（线性色彩空间报告为线性传递函数）；编码输入可以是 RGBA、BGRA、RGB、BGR 或灰度。
- **Pnm**：Netpbm：P1–P6 的 ASCII 和二进制格式，maxval 最高 65535（大于 255 时为 16 位样本）；1–4 通道的 PAM（P7）；PFM（`Pf` / `PF`）读出浮点样本；文件里只读第一张图。所有格式都能编码，ASCII 输出每行不超过 70 个字符。解码和编码都与 ImageMagick 完全一致。
- **Jpeg2000**：T.800 Part 1，裸码流（J2K）和 JP2 文件；用 JP2 的通道定义（`cdef`）识别 alpha；按降低的分辨率级别解码；tile、code-block 和小波变换多核并行。无损解码与 libopenjp2 逐位一致。
- **Jbig2**：T.88，支持 PDF 内嵌流（`/JBIG2Decode`，含全局段）和独立文件：通用区域、精细化区域、文本区域、半色调区域，模式字典和符号字典。
- **Exif**：读取 JPEG、TIFF、PNG、WebP、HEIF 和 AVIF；`ExifWriter` 生成可赋给 `JpegEncodeOptions.Exif` 及其他编码器的数据块。
- **Heif**：基于 ISOBMFF item 模型的 ISO/IEC 23008-12 静态图：HEVC 帧内解码（H.265 Main、Main 10、Main Still Picture，4:2:0），`grid` 分块并行解码，应用 `clap` / `irot` / `imir`，ICC 原样传出，报告 nclx / VUI 色彩；Main 10 解码为 16 位样本；PQ / HLG 拒绝 8 位布局。不解码 alpha、增益图、图像序列和范围扩展 profile。详见包的 README。

</details>

## 性能

除注明外均为单线程，AMD Ryzen 7 5800X，.NET 10。数字是相对参考库的耗时，低于 1.00× 表示更快。

| 操作 | 对比对象 | Sdcb.SimdCodecs | ImageSharp 3.1.12 |
|---|---|---:|---:|
| [JPEG 解码到 RGBA，446 个文件](benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/results/2026-10-24-amd-ryzen-7-5800x-full-m5.md) | libjpeg-turbo 3.2.0 | **0.82×**，分配 0 B | 1.77× |
| [PNG 解码到 RGBA，1080p 8 位 RGB / RGBA 照片](benchmarks/Sdcb.SimdCodecs.Png.Benchmarks/results/decode-2026-11-09-amd-ryzen-7-5800x.md) | libpng 1.6.39 | **0.88×** | 0.91× |
| [WebP 解码，1080p 有损 / 有损 + alpha / 无损](benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks/optimization/m4-final-2026-10-25-amd-ryzen-7-5800x.md) | libwebp 1.6.0 | **0.88× / 0.53× / 0.72×** | |
| [HEIF 解码到 RGBA，8 核](benchmarks/Sdcb.SimdCodecs.Heif.Benchmarks/results/2026-11-04-amd-ryzen-7-5800x-m3.md) | libheif 1.23.1 | **0.20–0.32×**，峰值内存 75 MB（libheif 80 MB） | |
| [BMP 解码 / 编码，1080p](benchmarks/Sdcb.SimdCodecs.Bmp.Benchmarks/results/m1-tuned-2026-10-30-amd-ryzen-7-5800x.md) | WIC | **0.20× / 0.05–0.08×** | 1.62× / 0.06× |
| [QOI 解码 / 编码，1080p](benchmarks/Sdcb.SimdCodecs.QoiPnm.Benchmarks/results/tuned-2026-10-30-amd-ryzen-7-5800x.md) | ImageSharp 3.1.12 | **0.29× / 0.49×** | 1.00× |
| [Netpbm 解码 / 编码，1080p](benchmarks/Sdcb.SimdCodecs.QoiPnm.Benchmarks/results/tuned-2026-10-30-amd-ryzen-7-5800x.md) | ImageSharp 3.1.12 | **0.23× / 0.54×** | 1.00× |
| [JPEG 2000 解码，1600×1200 无损 / 有损](benchmarks/Sdcb.SimdCodecs.Jpeg2000.Benchmarks/results/2026-10-30-amd-ryzen-7-5800x-baseline.md) | libopenjp2 2.5.4 | 1.96× / 2.03× | |

每一行都链到完整结果、语料和测量方法。JPEG 2000 是唯一一个单核仍慢于原生参考库的 codec；全核时是开了多线程的 libopenjp2 的 1.15–1.75×（[记录](benchmarks/Sdcb.SimdCodecs.Jpeg2000.Benchmarks/results/2026-11-05-amd-ryzen-7-5800x-parallel.md)）。怎样测量、每次改动遵守的规则见 [benchmarks/README.md](benchmarks/README.md)。

## 从 ImageSharp 迁移

Sdcb.SimdCodecs 只负责读写图像，不做缩放、绘制和滤镜。这些操作可以把图解进 `SKBitmap` 或 OpenCV 的 `Mat` 再做（见 `skiasharp`、`opencvsharp` 样例），也可以继续用 ImageSharp 做处理，只把编解码换成本库。

| ImageSharp | Sdcb.SimdCodecs |
|---|---|
| `Image.Load<Rgba32>(bytes)` | `ImageCodecs.Decode(bytes, PixelFormat.Rgba32)`，返回 `PixelBuffer`（`Pixels`、`Width`、`Height`、`Stride`） |
| `image.CopyPixelDataTo(span)` | `ImageCodecs.TryDecodeInto(bytes, span, 0, PixelFormat.Rgba32)`：不经过中间图像 |
| `image.ProcessPixelRows(…)` / `image[x, y]` | `buffer.Pixels` / `buffer.WritablePixels`，行与行相隔 `buffer.Stride` 字节 |
| `Image.Identify(bytes)` | `ImageCodecs.TryReadInfo(bytes, out ImageInfo info)` |
| `Image.DetectFormat(bytes)` | `ImageCodecs.DetectFormat(bytes)` |
| `Image.LoadAsync(stream)` | `await ImageDecoder.OpenAsync(stream)`，再 `decoder.GetFrame(0).Decode()` |
| `image.Mutate(x => x.AutoOrient())` | `new DecodeOptions { ApplyOrientation = true }`，或事后用 `PixelTransform.Orient` |
| `image.Mutate(x => x.Crop(rect))` | `new DecodeOptions { Region = new PixelRect(x, y, width, height) }`：只转换区域内的像素 |
| `new DecoderOptions { TargetSize = … }` | `new DecodeOptions { ScaleDenominator = 2 / 4 / 8 }`；按目标尺寸选分母用 `ImageCodecs.TryGetClosestSize` |
| `image.SaveAsJpeg(stream, new JpegEncoder { Quality = 90 })` | `ImageCodecs.Encode(image, ImageFormat.Jpeg, stream, new JpegEncodeOptions { Quality = 90 })` |
| `image.SaveAsPng(stream)` | `ImageCodecs.Encode(image, ImageFormat.Png, stream)`；要 pngquant 那样的调色板 PNG，传 `PngWriteOptions { Quantize = new() }` |
| `new WebpEncoder { FileFormat = WebpFileFormatType.Lossless }` | `new WebpLosslessEncodeOptions()`（不传 options 时 WebP 为有损） |
| `image.Metadata.ExifProfile` | `image.Metadata.Exif`（原始块）；用 `Sdcb.SimdCodecs.Exif` 包的 `ExifReader.FromBytes(…)` 解析 |
| `image.Frames[i]`（GIF、WebP、APNG、TIFF 多页） | `ImageDecoder.Open(bytes).GetFrame(i)`，或 `ImageCodecs.TryOpenFrames` |
| `Configuration.MemoryAllocator` | `new DecodeOptions { Pool = ArrayPool<byte>.Shared }` |

表中 `Encode` 几行的 `image` 是 `ImageCodecs.Decode(bytes)` 返回的 `IDecodedImage`；`PixelBuffer` 用 `ImageCodecs.Encode(buffer.Pixels, buffer.Width, buffer.Height, buffer.Format, format, stream, stride: buffer.Stride)` 编码。两者都是 `IDisposable`，和 ImageSharp 的 `Image` 一样。有两点行为不同：不设 `ApplyOrientation` 时像素按存储方向输出；16 位或 HDR 图像转 8 位 `PixelFormat` 只是普通转换，不做色调映射（浮点或 PQ / HLG 图像直接拒绝 8 位布局，不去猜）。

## LINQPad 样例

每个包都在 `linqpad-samples/` 里附带可运行的样例，LINQPad 的 NuGet 浏览器会直接列出它们（源码位于 `src/<包名>/linqpad-samples/`）：

| 包 | 样例 |
|---|---|
| `Sdcb.SimdCodecs` | `quick-start`（一次调用 `Decode` 到指定 `PixelFormat`、`ApplyOrientation`、解码进自己的缓冲区、失败原因）、`all-formats`、`decode-options`（元数据、内嵌缩略图、缩放 / 区域 / 池化解码、16 位和预乘输出、`ImageDecoder` 会话）、`encode`（`ImageCodecs.Encode` 编码到各格式，按行带流式写入和动画的编码会话）、`skiasharp`（直接解进 Skia 原生的预乘布局的 `SKBitmap`）、`opencvsharp`（直接解进 BGR 或灰度 `Mat`） |
| `Sdcb.SimdCodecs.Jpeg` | `encode`（质量、色度采样、progressive、EXIF/XMP）、`scaled-decode` |
| `Sdcb.SimdCodecs.Jpeg.GainMap` | `ultrahdr`（写入并读取 Ultra HDR JPEG） |
| `Sdcb.SimdCodecs.Png` | `read-write`、`quantize`（有损调色板 PNG：体积、质量、抖动） |
| `Sdcb.SimdCodecs.Tiff` | `multi-page`（含 CCITT Group 4）、`float-and-tiles`、`write` |
| `Sdcb.SimdCodecs.Exif` | `read-exif` |
| `Sdcb.SimdCodecs.Webp` | `decode`、`animation` |
| `Sdcb.SimdCodecs.Gif` | `decode`、`encode` |
| `Sdcb.SimdCodecs.Bmp` | `bmp`（写出每种布局，读回并比较） |
| `Sdcb.SimdCodecs.Ico` | `icons`（多尺寸图标和光标，写出后读回） |
| `Sdcb.SimdCodecs.Qoi` | `qoi`（QOI 与 PNG 比较体积和速度，解码进带行填充的 BGRA 缓冲区） |
| `Sdcb.SimdCodecs.Pnm` | `netpbm`（P1–P7 的 ASCII 和二进制格式、手写的 PPM、16 位 PGM、PFM） |
| `Sdcb.SimdCodecs.Heif` | `decode`（手机拍摄的网格图、Main 10、`clap` 裁剪；`ReadInfo` / `Decode` / `DecodeTo`）、`facade`（注册到门面；带行填充的 BGRA、Gray8） |

样例图片从 `https://cv-public.sdcb.ai/simdcodecs/samples/v1/` 下载；它们的生成方式和来源见 [tools/sample-assets](tools/sample-assets/README.md)。HEIF 样例使用 HEIF 测试图片（`…/test-images/heif/`，CC0；见 `tests/Sdcb.SimdCodecs.Tests/Oracle/heif/fetch.sh`）。

## 构建和测试

```bash
dotnet build SimdCodecs.slnx -c Release
dotnet test  SimdCodecs.slnx -c Release
```

依赖外部参考工具（oracle）的测试在工具缺失时会跳过；设置 `REQUIRE_ORACLES=1` 可让缺失的 oracle 直接报错（CI 就是这样运行的）。各 oracle 的获取方式，以及所有贡献都要遵守的 clean-room 规则，见 [`AGENTS.md`](AGENTS.md)。基准测试需要按路径单独构建，见 [`benchmarks/README.md`](benchmarks/README.md)。

<details>
<summary>包的依赖关系</summary>

```
Sdcb.SimdCodecs (facade)
 ├─ Abstractions
 ├─ Png ──────────┬─ Color.Icc ─ Abstractions
 │                └─ Abstractions
 ├─ Tiff ─────────┬─ Png
 │                └─ Abstractions
 ├─ Jpeg ─────────── Abstractions
 ├─ Jpeg.GainMap ─┬─ Jpeg
 │                ├─ Color.Icc
 │                └─ Abstractions
 ├─ Jbig2 ────────── Abstractions
 ├─ Jpeg2000 ─────── Abstractions
 ├─ Webp ─────────── Abstractions
 ├─ Gif ──────────── Abstractions
 ├─ Bmp ──────────┬─ Png
 │                └─ Abstractions
 ├─ Ico ──────────┬─ Bmp
 │                ├─ Png
 │                └─ Abstractions
 ├─ Qoi ──────────── Abstractions
 └─ Pnm ──────────── Abstractions

Exif ─ Tiff        Jpeg.IccInjector (no dependencies)
Heif ─ Abstractions   (not referenced by the facade: registered by the application)
```

MQ 算术解码器（JBIG2、JPEG 2000）、T.4 传真码表（JBIG2、TIFF、BMP）、调色板量化器（GIF、BMP）、流和数组写入器（QOI、Netpbm）以及 EXIF 方向读取器（JPEG、PNG、WebP；它的“方向改写为 1”也用在 Abstractions 和门面里）以源码形式共享（`src/Shared`），编译进用到它们的每个包：没有任何包使用另一个包的内部成员。

</details>

## 交流

![微信群二维码](https://io.starworks.cc:88/cv-public/2026/ocr-wxg-qr.png?1109)

如果微信群满了或二维码过期了，请加入 QQ 群 [C#/.NET计算机视觉技术交流 579060605](https://qm.qq.com/q/bPw5jAK4qk)。

## 许可证

MIT，见 [`LICENSE`](LICENSE)。本项目派生自 SharpAstro/Codecs（以 Unlicense 发布）。
