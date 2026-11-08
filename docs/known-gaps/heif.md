# HEIF / HEIC：支持情况与已知缺口

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。表中 ✅ 支持，⚠️ 部分支持（见说明），❌ 不支持。

## 支持情况

### 解码

| 功能 | 状态 | 说明 |
|---|:-:|---|
| HEVC Main、Main 10、Main Still Picture，4:2:0 | ✅ | Y / Cb / Cr 与 ffmpeg 8.1.2 逐字节一致 |
| HEVC 帧内语法全集（WPP、sign data hiding、AMP、transform skip、去块、SAO 等） | ✅ | |
| 网格（`grid`）拼图 | ✅ | 手机拍照的格式 |
| `clap`、`irot`、`imir` | ✅ | 按关联顺序应用，像素按显示方向输出 |
| 10 位输出为 16 位样本 | ✅ | |
| 输出 YCbCr 平面 | ⚠️ | 只给没有 `clap` / `irot` / `imir` 的图 |
| 图像序列（`.heics`） | ❌ | |
| HEVC 范围扩展 profile（Main 12、4:4:4 等） | ❌ | |
| alpha、增益图、深度图等辅助图像 | ❌ | 主图照常按不透明解码 |
| AVIF | ❌ | |

### 色彩与元数据

| 功能 | 状态 | 说明 |
|---|:-:|---|
| nclx `colr` 盒、HEVC VUI | ✅ | 决定矩阵、范围和 `ColorEncoding` |
| ICC（`prof` / `rICC`） | ✅ | 透传，不应用 |
| PQ / HLG | ⚠️ | `TryDecode` 照常解码，`TryDecodeInto` 返回 `false` |
| Exif、XMP item | ✅ | Exif 方向标签已改写为 1，与转正后的像素一致 |
| `thmb` 缩略图 | ✅ | 门面 `TryDecodeThumbnail` |

### 门面与性能

| 功能 | 状态 | 说明 |
|---|:-:|---|
| 需显式注册 | — | HEVC 专利原因，不在元包里 |
| 区域解码只解相交的 tile | ✅ | |
| `TryDecodeInto` 直写调用方缓冲区 | ✅ | 所有 `PixelFormat`，每个 tile 解完立即写入 |
| 原生缩放解码 | ⚠️ | 每个 tile 边转换边做 box 缩小，不产生全尺寸图；网格的 tile 接缝须落在 box 边界上，否则整幅解码后缩小（字节相同） |
| 多核并行（tile 并行、WPP wavefront） | ✅ | |
| 已调优 | ✅ | 8 核到 RGBA 约为 libheif 的 0.20–0.32× |

## 已知缺口与局限

- **只解码 HEVC 静态图**（Main、Main 10、Main Still Picture，4:2:0），含网格（`grid`）拼图和 `clap` / `irot` / `imir`。**不支持**：图像序列（`.heics`，主品牌 `msf1` 等，`CanDecode` 认得但不解码）、HEVC 范围扩展 profile（Main 10 Intra、Main 12、4:4:4 等）、AVIF。
- **不解码辅助图像**：alpha（彩色主图照常解码，按不透明输出，`Channels` 为 3）、HDR 增益图、深度图。
- **Main 10 的 8 位输出与 16 位图像收窄后最多差 1。** 2.0 起 Main 10 的 `TryDecode` 给出 16 位样本（`BitDepth` 10）；`TryDecodeInto` 输出 8 位格式时仍从编码样本直接做冻结的 8 位转换（只舍入一次），所以与把 16 位图像 `>> 8` 收窄的结果可能差 1 个码值。这是 `IImageDecoder.TryDecodeInto` 契约里写明的例外。
- **Exif 方向标签已改写为 1。** 像素已按 `irot` / `imir` 转正，所以 `TryReadMetadata` 和解码结果 `Metadata.Exif` 里的方向标签（SHORT、IFD0）在交出的副本中被改为 1，转码时不会转两次。文件里记录的原始方向可用 `ExifReader.FromHeif` 读到。
- **平面输出只给没有变换的图。** 带 `clap` / `irot` / `imir` 的图 `TryReadPlanarInfo` 返回 `false`，走 RGB 路径。
- **`Gray8` 是存储的亮度。** `TryDecodeInto(Gray8)` 取 Y 平面，按与 RGB 路径相同的系数做范围映射（limited → full、10 → 8 位），不是 RGB 的 BT.601 亮度，可能差一两个码值；identity 矩阵（GBR）的图退回 RGB 求亮度。
- **PQ / HLG 不给 8 位投影。** nclx（或 VUI）的传递函数是 PQ（16）或 HLG（18）时，`TryDecodeInto` 返回 `false`，`TryDecode` 照常解码并在 `ColorEncoding` 里如实报告，与 PNG 的 cICP 处理一致。
- **只有 ICC、没有 nclx 时，`ColorEncoding` 只取 ICC 的原色**：`colr` 盒优先于 VUI（手机照片常是 Display P3 ICC 配 BT.601 的 VUI），所以此时不看 VUI 的原色；ICC 声明的原色是 BT.709、Display P3 或 BT.2020 时，报 sRGB 加上这组原色（手机照片报 `DisplayP3`），其他情况报默认的 sRGB。YCbCr 矩阵仍取自 VUI，像素不变。有 nclx 盒时 nclx 优先于 ICC。
- **已调优（M3）。** 单核 HEVC 阶段是 libde265 的 0.68–0.87×；8 核端到端到 RGBA 是 libheif 的 0.20–0.32×（网格 手机照片和单 item 图都是：单 item 的 WPP 图按 CTB 行做 wavefront 并行），Magick.NET 的 0.09–0.14×。每个 tile 解完立即按目标像素格式和 stride 写进调用方缓冲区（所有 `PixelFormat`，含 `clap` / `irot` / `imir`），没有 整帧中间缓冲。冷进程首次解码的峰值工作集增量：12.6 MP 网格 75 MB（输出 48 MB，libheif 80 MB），未达 ≤ 64 MB 的目标，余量是 JIT 和 16 个并行 tile 各自的工作集；50 MP 网格 230 MB（libheif 373 MB）。见 [HEIF M3 记录](../../benchmarks/Sdcb.SimdCodecs.Heif.Benchmarks/results/2026-11-04-amd-ryzen-7-5800x-m3.md)。
- **缩放解码在 tile 内完成，但仍要解码每个像素。** HEVC 没有降分辨率解码，所以 `ScaleDenominator` > 1 时每个 tile 照常解码，转换成 Rgb24（Main 10 为 Rgb48）后立即按冻结的 box 滤波缩小，写进缩小后的图（`TryDecodeInto` 再用 `PixelConverter` 转成目标格式），没有全尺寸中间图，与“整幅解码再 box 缩小”逐字节相同。条件是每个与输出相交的 tile 在显示坐标下的矩形都从 box 边界开始、在 box 边界或图像边缘结束：单 item 图总是满足；网格要求 tile 接缝经 `clap` / `irot` / `imir` 映射后落在缩放因子的整数倍上（手机照片的 512 像素 tile、尺寸是因子的倍数、无变换或只有旋转 / 镜像时满足，因子最大到 512）。不满足时（例如 `clap` 从奇数列开始裁剪）退回整幅解码后缩小。`TryGetClosestSize` 仍报告原尺寸。50 MP 网格冷进程峰值增量：d2 82 MB、d8 47 MB（原来 218 MB、183 MB）；12.6 MP 网格 d2 38 MB、d8 30 MB（原来 72、61 MB）；8 核耗时是原来的 0.63–0.96×。见 [缩放记录](../../benchmarks/Sdcb.SimdCodecs.Heif.Benchmarks/results/2026-11-08-amd-ryzen-7-5800x-scaled.md)。
- **专利**：HEVC 有专利负担，本包是 MIT，不授予任何专利许可。
