# 支持情况与已知缺口

这里记录各编解码器**支持什么**，以及目前**做不到**、**有意不做**和**还没调优**的地方，方便使用前判断是否合适，也作为后续工作的 清单。本页只写跨格式的内容，各格式按功能类别列出的支持表和局限条目在 [`known-gaps/`](known-gaps/) 目录里。每一条都对应代码里实际的行为（拒绝时抛出的异常、门面返回 `false` 的情况等）。功能补上、性能调优完成后， 相应条目从这里删掉。

约定：

- **不支持**：遇到这种输入时抛出 `NotSupportedException` / `InvalidDataException`，`Try*` 方法返回 `false`， 不会静默输出错误的像素。
- **门面不提供**：格式包本身有对应 API，但 `Sdcb.SimdCodecs` 门面（`TryDecode` / `TryDecodeInto` / `TryOpenFrames` / `ImageDecoder` / `Encode`）不会走到它。
- **未调优**：功能正确、有测试，但还没有基准项目，也没有按“不慢于原生参考库”的标准优化过。

## 能力一览

✅ 支持，⚠️ 部分支持（限定条件见各格式页），空白为不支持或不适用。“原生缩放”指不先解出全尺寸图；“区域按需”指只读或只解与区域相交的数据，其余格式的区域解码是整幅解码再裁剪；“直写缓冲区”指 `TryDecodeInto` 不经整帧中间图、直接写进调用方缓冲区。

| 格式 | 解码 | 编码 | 动画 / 多页 | alpha | 16 位 | 浮点 / HDR | 原生缩放 | 区域按需 | 直写缓冲区 | 已调优 |
|---|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|:-:|
| [JPEG](known-gaps/jpeg.md) | ✅ | ✅ | | | | | ✅ | ⚠️ | ✅ | ✅ |
| [Ultra HDR / 增益图](known-gaps/jpeg-gainmap.md) | ✅ | ✅ | | | | ✅ | | | | |
| [PNG](known-gaps/png.md) | ✅ | ✅ | ✅ | ✅ | ✅ | ⚠️ | | | ✅ | ✅ |
| [TIFF](known-gaps/tiff.md) | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ | | ✅ | ⚠️ | |
| [WebP](known-gaps/webp.md) | ✅ | ✅ | ✅ | ✅ | | | | | ✅ | ✅ |
| [GIF](known-gaps/gif.md) | ✅ | ✅ | ✅ | ⚠️ | | | | | | |
| [BMP](known-gaps/bmp.md) | ✅ | ✅ | | ✅ | | | | | ✅ | ✅ |
| [ICO / CUR](known-gaps/ico.md) | ✅ | ✅ | ✅ | ✅ | | | | | | ✅ |
| [QOI](known-gaps/qoi.md) | ✅ | ✅ | | ✅ | | | | | ✅ | ✅ |
| [Netpbm](known-gaps/netpbm.md) | ✅ | ✅ | | ✅ | ✅ | ✅ | | | ✅ | ✅ |
| [JPEG 2000](known-gaps/jpeg2000.md) | ✅ | | | ✅ | ✅ | | ✅ | | | ⚠️ |
| [JBIG2](known-gaps/jbig2.md) | ✅ | | ⚠️ | | | | | | | |
| [HEIF / HEIC](known-gaps/heif.md) | ✅ | | | | ✅ | ⚠️ | | ✅ | ✅ | ✅ |
| [EXIF](known-gaps/exif.md) | ✅ | ✅ | | | | | | | | |

几点补充：PNG 和 HEIF 的“浮点 / HDR”指能读出 PQ / HLG 标记和 16 位原始样本，但不投影到 8 位；GIF 的 alpha 只有 1 位；ICO 的“多页”是目录里的各个尺寸；JBIG2 的多页只能通过包 API 取，门面只给第 1 页；JPEG 2000 有基准项目，单核仍慢于 libopenjp2；EXIF 一行的解码 / 编码指读取和生成 Exif 块。

## 总体

### 性能调优进度

AGENTS.md 的目标是每个编解码器都达到或超过对应的原生参考库。目前**已调优**的有：JPEG 编码和解码、WebP 解码、 PNG 编码和解码，各有自己的基准项目。WebP 无损编码也已调优（基准项目的 `encode` 命令），默认力度下体积和速度都不输 libwebp，见 [WebP](known-gaps/webp.md)。

BMP 和 ICO（解码和编码）也已调优，基准项目是 `Sdcb.SimdCodecs.Bmp.Benchmarks`；QOI 和 Netpbm（解码和编码）同样已调优，基准项目是 `Sdcb.SimdCodecs.QoiPnm.Benchmarks`。

HEIF 解码也已调优（M3），基准项目是 `Sdcb.SimdCodecs.Heif.Benchmarks`：单核 HEVC 阶段快于 libde265，端到端快于 libheif，每个 tile 解完直接写进调用方缓冲区，见 [HEIF](known-gaps/heif.md)。

**未调优**：TIFF、JBIG2、GIF（解码和编码）、EXIF。JPEG 2000 已有基准项目：单核约为 libopenjp2 的 2×，tile 内的 code-block、小波、分量变换已多核并行（全核约为开多线程的 libopenjp2 的 1.15–1.75×），单核的算术解码优化尚未开始。JBIG2、GIF、JPEG 2000 的门面路径比较朴素：`TryDecodeInto` 先完整解码出一张图，再整帧转换成目标像素格式，中间会分配一整帧内存；JPEG、PNG、WebP、整数采样的 TIFF 则是直接写进调用方的缓冲区。

### 门面与公共接口

- **HEIF 要显式注册。** HEVC 有专利负担，`Sdcb.SimdCodecs.Heif` 不在元包里，门面也不引用它；安装后调用一次 `SimdCodecs.Register(HeifImageDecoder.Registration)`，门面的全部入口才会处理 HEIF。注册前 `DetectFormat` 仍报 `ImageFormat.Heif`，但 `IsDecodable` 为 `false`、`Try*` 返回 `false`。注册是进程级的：幂等、线程安全、 不能撤销；注册的编解码器排在内置的之后嗅探。没有反注册、优先级、按名字查找，这是有意保持最小。
- **编码器抽象（2.0 起）的流式程度不一。** `IImageEncoder` 的会话可以逐行推送像素，但目前每个编码器都在会话里 缓冲整幅图，`Commit` 时一次编码（`BufferedEncoderSession`）：契约不变，只是没有内存收益。PNG、BMP、QOI、PNM 天然逐行，以后可以改成真正流式。
- **缩放解码只有 JPEG 和 JPEG 2000 是原生的。** JPEG 支持 `DecodeOptions.ScaleDenominator` 2、4、8（DCT 域缩放）；JPEG 2000 支持不超过 2 的分解级数次方的任意 2 的幂（略去高分辨率级别，与 `opj_decompress -r` 一致），但 SIZ 原点不为 0 的图只能按原尺寸解码。其他 codec 抛出 `NotSupportedException`，`TryGetClosestSize` 报告原尺寸。
- **区域解码大多是“整幅解码再裁剪”。** `DecodeOptions.Region` 对所有 codec 都可用，但除了 HEIF（只解码与区域相交 的网格 tile，只转换区域内的像素）、TIFF（只读与区域相交的条带或分块）和 JPEG 以外都是整幅解码后裁剪，语义一致，没有性能收益。JPEG 只持有 MCU 行条带和 区域本身，只对区域内的行做上采样和色彩转换，所以超过 2 GB 的大图也能取区域；但每个 MCU 仍要熵解码和 IDCT（baseline 码流没有可跳转的索引），耗时接近整幅解码。按 restart 间隔跳过熵解码留给 2.x。
- **整幅输出受单个数组 2 GB 上限约束。** 输出光栅超过 `Array.MaxLength` 字节时，`TryDecode` 返回 false， `ImageDecoder` 的 `Decode` 抛出 `InvalidDataException` 并提示改用缩放或区域解码；`RasterImage` 的工厂方法抛出 `ArgumentOutOfRangeException`。JPEG progressive 或分多次扫描的帧要整帧保存系数，总样本数上限为 `int.MaxValue`， 缩放解码也不例外。
- **`Stream` 输入会先读进内存。** `ImageDecoder.Open(Stream)` 对可寻址的流按需读取的 `IByteSource` 已经就位，但 2.0 里所有 codec 仍在整份文件上解码：可寻址的流一次读入，不可寻址的流先缓冲，文件路径用内存映射（不复制）。 逐 codec 换成真正的增量输入是 2.x 的性能项。
- **`ColorEncoding` 只从 ICC 取原色。** 格式自己的色彩标记优先（PNG 的 cICP，表示线性的 gAMA 1.0，HEIF 的 nclx `colr` 盒；HEIF 没有 `colr` 盒时用 HEVC VUI）。格式没有自己的标记、但带有 ICC 配置文件，并且该文件声明的原色是 BT.709、Display P3 或 BT.2020 时，`ImageInfo.ColorEncoding` 和 `IDecodedImage.ColorEncoding` 报 sRGB 加上这组原色（例如 Display P3 照片报 `Primaries = DisplayP3`）；传递函数和范围不从 ICC 推导，仍是 sRGB 和全范围。ICC 声明的是其他原色（Adobe RGB、ProPhoto、相机或显示器自己的）、是 LUT 配置文件、或者格式损坏时，行为不变，报默认的 sRGB。识别方法与 `IccProfiles.TryGetPrimaries` 相同。覆盖 JPEG、PNG（含帧读取器）、WebP（含帧读取器）、TIFF、HEIF、JPEG 2000（JP2 的受限 ICC）和 BMP V5；像素不受影响。
- **EXIF 方向只报告、不应用。** `ImageInfo.Orientation` 告诉你该怎样旋转或翻转，像素本身始终是存储时的 方向。
- **浮点图像没有自动的整数投影。** 浮点 TIFF、增益图 JPEG 重建出的 HDR 等，`TryDecodeInto` 返回 `false` （增益图返回的是 SDR 基础图），`EnumerateFramesInto` 抛出 `NotSupportedException`。HDR 到 8 位需要色调映射， 这是调用方的决定，不是编解码器的。只为查看的话，`PixelConverter.StretchToRgba8` 把颜色范围线性拉伸到 0..255（线性光数据如增益图 HDR 再加 `srgbEncode: true`），但不做色调映射。
- **多帧读取器的帧数可能少于文件里的帧数。** `ImageInfo.FrameCount` 报告文件里有多少帧或页，但：
  - JBIG2：门面的读取器只给出独立 `.jb2` 文件的第 1 页（1 帧）；其他页用 `Jbig2Decoder.DecodeFile(file, pageNumber)`。
- **接口里没有“时间戳 → 帧号”的辅助方法。** WebP 包的 `WebpAnimationDecoder.FrameIndexAt` 有，接口层没有； 需要时可以按各帧的 `Duration` 自己累加。
- **`IImageFrameReader` 和 `ImageDecoder` 会话都不是线程安全的**（动画需要在调用之间保留画布状态），每个线程各开一个。
- **元数据只透传、不解析。** `ImageMetadata` 是原始的 Exif / XMP / ICC 块；解析 Exif 用 `Sdcb.SimdCodecs.Exif`。TIFF 页面自己的 IFD 就是 Exif 的 IFD0，所以读出的 Exif 块是重组的（见 [TIFF](known-gaps/tiff.md)），字节布局与源文件不同； JPEG 的扩展 XMP（跨多个 APP1 段）没有拼接。HEIF 的像素已按 `irot` / `imir` 转正，所以它交出的 Exif 块里方向标签已改写为 1；自己按 `ImageInfo.Orientation` 旋转像素的调用方，可以用 `ExifWriter.ResetOrientation` 得到方向为 1 的副本。
- **一次性解码所有帧不在接口里**，是有意的：全部帧的内存是帧数 × 画布大小。需要时对索引循环即可；WebP 包的 `WebpDecoder.DecodeAnimation` 带总像素上限。

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
