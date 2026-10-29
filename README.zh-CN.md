<img src="https://raw.githubusercontent.com/sdcb/SimdCodecs/main/assets/icon.png" width="64" align="right" alt="Sdcb.SimdCodecs icon">

# Sdcb.SimdCodecs [![CI](https://github.com/sdcb/SimdCodecs/actions/workflows/dotnet.yml/badge.svg)](https://github.com/sdcb/SimdCodecs/actions/workflows/dotnet.yml) [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs) [![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

[English](README.md) | **简体中文**

纯托管的 .NET 10 图像编解码库：JPEG、PNG、TIFF、WebP、GIF、JPEG 2000、JBIG2，外加 EXIF 和 ICC。
没有任何原生依赖，兼容 NativeAOT，用 SIMD（AVX2、SSE、AdvSimd）加速，并带标量回退。
在基准测试语料上，JPEG 解码比 libjpeg-turbo 更快。

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

byte[] file = File.ReadAllBytes("photo.webp");   // JPEG、PNG、TIFF、WebP、GIF、JPEG 2000 或 JBIG2

// 门面类根据文件头识别格式，解码到你自己的缓冲区（R G B A）。
if (!SimdCodecs.TryReadInfo(file, out ImageInfo info)) throw new InvalidDataException("Not a supported image");
byte[] rgba = new byte[info.Width * info.Height * 4];
SimdCodecs.TryDecodeIntoRgba8(file, rgba);

// 编码成 JPEG：SIMD 加速，支持 baseline / 优化 Huffman / progressive，4:2:0 或 4:4:4。
byte[] jpeg = JpegEncoder.Encode(rgba, info.Width, info.Height, channels: 4, new JpegEncodeOptions { Quality = 90 });
File.WriteAllBytes("photo.jpg", jpeg);
```

`SimdCodecs.TryDecodeInto(file, buffer, stride, format)` 可以按任意行跨度（stride）直接写出 BGRA
（Windows / WPF / Skia 表面）、RGB、BGR（OpenCV）或灰度（OCR）；JPEG 和 PNG 原生输出这些布局，中间不经过
RGBA 整帧（彩色 JPEG 的灰度就是它的 Y 平面，色度直接跳过）。`SimdCodecs.TryDecode` 则保留原始布局（通道数，
灰度 JPEG 为单通道；8/16 位或浮点采样；ICC 配置文件）。`TryReadInfo` 只读文件头，还会给出存储位深、EXIF
方向（只报告，不会旋转像素）和帧数或页数。`SimdCodecs.TryOpenFrames(file, out reader)` 按索引、以任意布局逐帧
解码：动画 WebP 和 GIF 是合成好的帧及其时长，多页 TIFF 是各页（其他图片就是一帧）；
`reader.EnumerateFramesInto(buffer, stride, format)` 用同一块缓冲区按顺序播放。每种格式也有独立的包，
API 更丰富：JPEG 缩放解码、TIFF 按块读取、WebP 动画按时间戳定位、Ultra HDR 等。

## 支持的格式

| 格式 | 解码 | 编码 | 特性 |
|---|:-:|:-:|---|
| JPEG | ✅ | ✅ | 解码 baseline 和 progressive DCT JPEG、任意采样、CMYK / YCCK，支持 1/2–1/8 缩放和零分配 `DecodeTo`；SOF3 无损 JPEG 由独立的 `LosslessJpeg.FromMemory` API 支持；编码 baseline / 优化 Huffman / progressive，可写入 EXIF 和 XMP |
| Ultra HDR JPEG | ✅ | ✅ | 增益图（gain map）读取、组装和 HDR 重建（Ultra HDR / Adobe） |
| PNG | ✅ | ✅ | 全部位深（1–16 位、调色板）；文本块；HDR 标记（cICP、mDCV、cLLI） |
| TIFF | ✅ | ✅ | 多页；8/16/32 位整数和浮点；条带或分块；读取 Deflate、LZW、PackBits、CCITT G3/G4，可只读单个分块；写入 Deflate |
| WebP | ✅ | — | 有损、无损、alpha；动画及跳转；输出与 libwebp 逐字节一致 |
| GIF | ✅ | ✅ | 87a / 89a：隔行、透明、全部 dispose 方式；动画及跳转；编码静态图和动画（精确调色板，或 Wu 量化 + 抖动） |
| JPEG 2000 | ✅ | — | Part 1（T.800），用于 PDF 的 `/JPXDecode` |
| JBIG2 | ✅ | — | T.88，用于 PDF 的 `/JBIG2Decode` |
| EXIF | ✅ | ✅ | 读取 JPEG、TIFF、PNG 中的 EXIF；`ExifWriter` 生成可交给 JPEG 编码器的数据块 |
| ICC 配置文件 | ✅ | ✅ | 解码器返回内嵌的配置文件；内置 sRGB v4；可把配置文件写入现有 JPEG |

上表描述的是各格式包本身的能力。`Sdcb.SimdCodecs` 门面有意提供更窄的识别和显示契约；例如独立的 `LosslessJpeg.FromMemory` API 可以直接处理 SOF3，而 `JpegDecoder` 和门面会拒绝这类文件。其他门面限制见 [docs/known-gaps.md](docs/known-gaps.md)。

## 性能

单线程，AMD Ryzen 7 5800X，.NET 10。时间以原生库为基准（越小越快）：

| JPEG 解码到 RGBA，446 个文件 | 时间 | 每次解码的托管内存分配 |
|---|---:|---:|
| libjpeg-turbo 3.2.0（原生） | 1.00× | — |
| **Sdcb.SimdCodecs** | **0.82×** | **0 B** |
| ImageSharp 3.1.12 | 1.77× | 约 16 KiB，另加它自己分配的图像 |

| WebP 解码到 RGBA，1920×1080 | 有损 | 有损 + alpha | 无损 |
|---|---:|---:|---:|
| libwebp 1.6.0（原生） | 1.00× | 1.00× | 1.00× |
| **Sdcb.SimdCodecs** | **0.88×** | **0.53×** | **0.72×** |

JPEG 解码的精度也不低于 libjpeg-turbo 的默认设置（对色度做插值时，相对精确参考值的 RGB PSNR 高约
1 dB），并且在 AVX2、SSE、AdvSimd、标量和 NativeAOT 下输出逐位一致。WebP 输出与 libwebp 逐字节一致。
完整数据、语料和测量方法见
[JPEG](benchmarks/Sdcb.SimdCodecs.Jpeg.Benchmarks/results/2026-10-24-amd-ryzen-7-5800x-full-m5.md) 和
[WebP](benchmarks/Sdcb.SimdCodecs.Webp.Benchmarks/optimization/m4-final-2026-10-25-amd-ryzen-7-5800x.md)
（在接近照片内容、不带 alpha 的有损文件上，WebP 与 libwebp 持平：0.87–1.13×）。

## 包列表

所有包统一版本号，同时发布。

| 包 | NuGet | 内容 |
|---|---|---|
| `Sdcb.SimdCodecs` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs) | 门面：识别格式并分派给各格式的解码器 |
| `Sdcb.SimdCodecs.Jpeg` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Jpeg.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg) | JPEG 解码器和编码器 |
| `Sdcb.SimdCodecs.Jpeg.GainMap` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Jpeg.GainMap.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg.GainMap) | Ultra HDR / Adobe 增益图 |
| `Sdcb.SimdCodecs.Jpeg.IccInjector` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Jpeg.IccInjector.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg.IccInjector) | 把 ICC 配置文件写入 JPEG |
| `Sdcb.SimdCodecs.Png` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Png.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Png) | PNG 解码器和编码器 |
| `Sdcb.SimdCodecs.Tiff` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Tiff.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Tiff) | TIFF 读写 |
| `Sdcb.SimdCodecs.Webp` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Webp.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Webp) | WebP 解码器（含动画） |
| `Sdcb.SimdCodecs.Gif` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Gif.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Gif) | GIF 解码器和编码器（含动画） |
| `Sdcb.SimdCodecs.Jpeg2000` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Jpeg2000.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg2000) | JPEG 2000 解码器 |
| `Sdcb.SimdCodecs.Jbig2` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Jbig2.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jbig2) | JBIG2 解码器 |
| `Sdcb.SimdCodecs.Exif` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Exif.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Exif) | EXIF 读写 |
| `Sdcb.SimdCodecs.Color.Icc` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Color.Icc.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Color.Icc) | sRGB v4 ICC 配置文件、`cicp` 标签注入 |
| `Sdcb.SimdCodecs.Abstractions` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Abstractions.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Abstractions) | 公共类型：`IImageDecoder`、`RasterImage`、`ImagePixelFormat` / `PixelConverter`、`IImageFrameReader`（帧与页）、H.273 色彩描述 |

<details>
<summary>各包的详细特性</summary>

- **Jpeg**：T.81 解码器，支持 baseline、progressive、restart interval、任意采样方式，灰度 / YCbCr /
  RGB / CMYK / YCCK，1/2–1/8 缩放解码，无损（SOF3）。SIMD 编码器支持 baseline、优化 Huffman 或
  progressive；4:4:4 / 4:2:0 / 灰度；可写入 EXIF（APP1）和 XMP。
- **Png**：所有标准位深（包括 1/2/4 位灰度和调色板），tEXt / zTXt / iTXt 文本块，PNG-3 的 cICP /
  mDCV / cLLI HDR 标记。
- **Tiff**：多页；8/16/32 位整数和浮点；Deflate / LZW（另可读 PackBits）；条带或分块；chunky 或
  planar；按需读取单个条带或分块；读取 1/2/4 位页面（扩展为 8 位）、CCITT 传真（Group 3 1D/2D、
  Group 4、Modified Huffman）和浮点预测器。
- **Webp**：RFC 6386 / RFC 9649：VP8 有损、VP8L 无损、ALPH、带跳转的动画、ICC / EXIF / XMP。门面的
  `TryDecode` 返回静态图或动画的第 0 帧，`TryOpenFrames` 返回所有帧；`WebpAnimationDecoder` 另外提供
  按时间戳定位和每帧的矩形。
- **Gif**：GIF89a：全局 / 局部调色板、隔行、透明、全部 dispose 方式、NETSCAPE2.0 循环次数；合成好的帧，
  可跳转。规范没有定死的行为，按浏览器的做法处理，列在包的 README 里。编码器：静态图和动画，精确调色板或
  Wu 量化（可选 Floyd–Steinberg 抖动），不透明动画只写变化的矩形。
- **Exif**：读取 JPEG、TIFF 和 PNG；`ExifWriter` 生成可赋给 `JpegEncodeOptions.Exif` 的数据块。

</details>

<details>
<summary>依赖关系</summary>

```
Sdcb.SimdCodecs (facade)
 ├─ Abstractions
 ├─ Png ──────────┬─ Color.Icc ─ Abstractions
 │                └─ Abstractions
 ├─ Tiff ─────────┬─ Png
 │                └─ Abstractions
 ├─ Jpeg ─────────── Abstractions
 ├─ Jpeg.GainMap ─┬─ Jpeg
 │                └─ Abstractions
 ├─ Jbig2 ────────── Abstractions
 ├─ Jpeg2000 ─────── Abstractions
 ├─ Webp ─────────── Abstractions
 └─ Gif ──────────── Abstractions

Exif ─ Tiff        Jpeg.IccInjector (no dependencies)
```

MQ 算术解码器（JBIG2、JPEG 2000）、T.4 传真码表（JBIG2、TIFF）和 EXIF 方向读取（JPEG、PNG、WebP）以源码形式
共享（`src/Shared`），编译进每个用到它们的包：没有哪个包使用另一个包的内部成员。

</details>

## LINQPad 样例

每个包都在 `linqpad-samples/` 里附带可运行的样例，LINQPad 的 NuGet 浏览器会直接列出它们
（源码位于 `src/<包名>/linqpad-samples/`）：

| 包 | 样例 |
|---|---|
| `Sdcb.SimdCodecs` | `quick-start`、`all-formats`、`skiasharp`、`opencvsharp` |
| `Sdcb.SimdCodecs.Jpeg` | `encode`（质量、采样、progressive、EXIF/XMP）、`scaled-decode` |
| `Sdcb.SimdCodecs.Jpeg.GainMap` | `ultrahdr`（写入并读取 Ultra HDR JPEG） |
| `Sdcb.SimdCodecs.Png` | `read-write` |
| `Sdcb.SimdCodecs.Tiff` | `multi-page`（含 CCITT Group 4）、`float-and-tiles`、`write` |
| `Sdcb.SimdCodecs.Exif` | `read-exif` |
| `Sdcb.SimdCodecs.Webp` | `decode`、`animation` |
| `Sdcb.SimdCodecs.Gif` | `decode`、`encode` |

样例图片从 `https://cv-public.sdcb.ai/simdcodecs/samples/v1/` 下载，它们的生成方式和来源见
[tools/sample-assets](tools/sample-assets/README.md)。

## 构建和测试

```bash
dotnet build SimdCodecs.slnx -c Release
dotnet test  SimdCodecs.slnx -c Release
```

依赖外部参考工具（oracle）的测试在工具缺失时会跳过；设置 `REQUIRE_ORACLES=1` 可让缺失的 oracle 直接
报错（CI 就是这样运行的）。各 oracle 的获取方式，以及所有贡献都要遵守的 clean-room 规则，见
[`AGENTS.md`](AGENTS.md)。基准测试需要按路径单独构建，见 [`benchmarks/README.md`](benchmarks/README.md)。
发版流程见 [`RELEASING.md`](RELEASING.md)。

## 交流

![微信群二维码](https://io.starworks.cc:88/cv-public/2026/ocr-wxg-qr.png?1027)

如果微信群满了或二维码过期了，请加入 QQ 群 [C#/.NET计算机视觉技术交流 579060605](https://qm.qq.com/q/bPw5jAK4qk)。

## 许可证

MIT，见 [`LICENSE`](LICENSE)。本项目派生自 SharpAstro/Codecs（以 Unlicense 发布）。
