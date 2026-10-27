# Sdcb.SimdCodecs

[English](README.md) | **简体中文**

[![CI](https://github.com/sdcb/SimdCodecs/actions/workflows/dotnet.yml/badge.svg)](https://github.com/sdcb/SimdCodecs/actions/workflows/dotnet.yml)
[![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

纯托管的 .NET 10 图像编解码库：JPEG、PNG、TIFF、WebP、JPEG 2000、JBIG2，外加 EXIF 和 ICC。
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

byte[] file = File.ReadAllBytes("photo.webp");   // JPEG、PNG、TIFF、WebP、JPEG 2000 或 JBIG2

// 门面类根据文件头识别格式，解码到你自己的缓冲区（R G B A）。
if (!SimdCodecs.TryReadInfo(file, out ImageInfo info)) throw new InvalidDataException("Not a supported image");
byte[] rgba = new byte[info.Width * info.Height * 4];
SimdCodecs.TryDecodeIntoRgba8(file, rgba);

// 编码成 JPEG：SIMD 加速，支持 baseline / 优化 Huffman / progressive，4:2:0 或 4:4:4。
byte[] jpeg = JpegEncoder.Encode(rgba, info.Width, info.Height, channels: 4, new JpegEncodeOptions { Quality = 90 });
File.WriteAllBytes("photo.jpg", jpeg);
```

`SimdCodecs.TryDecode` 则保留原始布局（通道数、8/16 位或浮点采样、ICC 配置文件）。每种格式也有
独立的包，API 更丰富：JPEG 缩放解码、多页 TIFF、WebP 动画、Ultra HDR 等。

## 支持的格式

| 格式 | 解码 | 编码 | 特性 |
|---|:-:|:-:|---|
| JPEG | ✅ | ✅ | 解码 baseline、progressive、无损，任意采样，CMYK / YCCK，1/2–1/8 缩放解码，零分配的 `DecodeTo`；编码 baseline / 优化 Huffman / progressive，可写入 EXIF 和 XMP |
| Ultra HDR JPEG | ✅ | ✅ | 增益图（gain map）读取、组装和 HDR 重建（Ultra HDR / Adobe） |
| PNG | ✅ | ✅ | 全部位深（1–16 位、调色板）；文本块；HDR 标记（cICP、mDCV、cLLI） |
| TIFF | ✅ | ✅ | 多页；8/16/32 位整数和浮点；条带或分块；读取 Deflate、LZW、PackBits、CCITT G3/G4，可只读单个分块；写入 Deflate |
| WebP | ✅ | — | 有损、无损、alpha；动画及跳转；输出与 libwebp 逐字节一致 |
| JPEG 2000 | ✅ | — | Part 1（T.800），用于 PDF 的 `/JPXDecode` |
| JBIG2 | ✅ | — | T.88，用于 PDF 的 `/JBIG2Decode` |
| EXIF | ✅ | ✅ | 读取 JPEG、TIFF、PNG 中的 EXIF；`ExifWriter` 生成可交给 JPEG 编码器的数据块 |
| ICC 配置文件 | ✅ | ✅ | 解码器返回内嵌的配置文件；内置 sRGB v4；可把配置文件写入现有 JPEG |

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
| `Sdcb.SimdCodecs.Jpeg2000` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Jpeg2000.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jpeg2000) | JPEG 2000 解码器 |
| `Sdcb.SimdCodecs.Jbig2` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Jbig2.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Jbig2) | JBIG2 解码器 |
| `Sdcb.SimdCodecs.Exif` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Exif.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Exif) | EXIF 读写 |
| `Sdcb.SimdCodecs.Color.Icc` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Color.Icc.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Color.Icc) | sRGB v4 ICC 配置文件、`cicp` 标签注入 |
| `Sdcb.SimdCodecs.Abstractions` | [![NuGet](https://img.shields.io/nuget/v/Sdcb.SimdCodecs.Abstractions.svg)](https://www.nuget.org/packages/Sdcb.SimdCodecs.Abstractions) | 公共类型：`IImageDecoder`、`RasterImage`、H.273 色彩描述 |

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
- **Webp**：RFC 6386 / RFC 9649：VP8 有损、VP8L 无损、ALPH、带跳转的动画、ICC / EXIF / XMP。门面
  返回静态图或动画的第 0 帧；完整时间轴请用 `WebpDecoder.DecodeAnimation` 或 `WebpAnimationDecoder`。
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
 │                └─ Abstractions (shares the internal T.4 fax code tables)
 ├─ Jpeg ─────────── Abstractions
 ├─ Jpeg.GainMap ─┬─ Jpeg
 │                └─ Abstractions
 ├─ Jbig2 ────────── Abstractions (shares the internal MQ decoder and T.4 code tables)
 ├─ Jpeg2000 ─────── Abstractions (shares the internal MQ decoder)
 └─ Webp ─────────── Abstractions

Exif ─ Tiff        Jpeg.IccInjector (no dependencies)
```

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

## 许可证

MIT，见 [`LICENSE`](LICENSE)。本项目派生自 SharpAstro/Codecs（以 Unlicense 发布）。
