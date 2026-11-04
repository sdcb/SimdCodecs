# HEIF / HEIC：已知缺口与局限

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。

- **只解码 HEVC 静态图**（Main、Main 10、Main Still Picture，4:2:0），含网格（`grid`）拼图和 `clap` / `irot` /
  `imir`。**不支持**：图像序列（`.heics`，主品牌 `msf1` 等，`CanDecode` 认得但不解码）、HEVC 范围扩展
  profile（Main 10 Intra、Main 12、4:4:4 等）、AVIF。
- **不解码辅助图像**：alpha（彩色主图照常解码，按不透明输出，`Channels` 为 3）、HDR 增益图、深度图。
- **Main 10 的 8 位输出与 16 位图像收窄后最多差 1。** 2.0 起 Main 10 的 `TryDecode` 给出 16 位样本（`BitDepth`
  10）；`TryDecodeInto` 输出 8 位格式时仍从编码样本直接做冻结的 8 位转换（只舍入一次），所以与把 16 位图像
  `>> 8` 收窄的结果可能差 1 个码值。这是 `IImageDecoder.TryDecodeInto` 契约里写明的例外。
- **Exif 方向原样透传。** `TryReadMetadata` 的 Exif 块里的方向标签不改写，像素却已按 `irot` / `imir` 转正；
  转码到别的格式时调用方需要把方向改写为 1。
- **平面输出只给没有变换的图。** 带 `clap` / `irot` / `imir` 的图 `TryReadPlanarInfo` 返回 `false`，走 RGB 路径。
- **`Gray8` 是存储的亮度。** `TryDecodeInto(Gray8)` 取 Y 平面，按与 RGB 路径相同的系数做范围映射（limited →
  full、10 → 8 位），不是 RGB 的 BT.601 亮度，可能差一两个码值；identity 矩阵（GBR）的图退回 RGB 求亮度。
- **PQ / HLG 不给 8 位投影。** nclx（或 VUI）的传递函数是 PQ（16）或 HLG（18）时，`TryDecodeInto` 返回
  `false`，`TryDecode` 照常解码并在 `ColorEncoding` 里如实报告，与 PNG 的 cICP 处理一致。
- **只有 ICC、没有 nclx 时 `ColorEncoding` 报默认的 sRGB**：`colr` 盒优先于 VUI（手机照片常是 Display P3 ICC
  配 BT.601 的 VUI），而 `ColorEncoding` 不从 ICC 推导。
- **已调优（M3）。** 单核 HEVC 阶段是 libde265 的 0.68–0.87×；8 核端到端到 RGBA 是 libheif 的 0.20–0.32×（网格
  手机照片和单 item 图都是：单 item 的 WPP 图按 CTB 行做 wavefront 并行），Magick.NET 的 0.09–0.14×。每个 tile
  解完立即按目标像素格式和 stride 写进调用方缓冲区（所有 `PixelFormat`，含 `clap` / `irot` / `imir`），没有
  整帧中间缓冲。冷进程首次解码的峰值工作集增量：12.6 MP 网格 75 MB（输出 48 MB，libheif 80 MB），未达 ≤ 64 MB
  的目标，余量是 JIT 和 16 个并行 tile 各自的工作集；50 MP 网格 230 MB（libheif 373 MB）。见
  [HEIF M3 记录](../../benchmarks/Sdcb.SimdCodecs.Heif.Benchmarks/results/2026-11-04-amd-ryzen-7-5800x-m3.md)。
- **专利**：HEVC 有专利负担，本包是 MIT，不授予任何专利许可。
