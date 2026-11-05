# JPEG：支持情况与已知缺口

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。表中 ✅ 支持，⚠️ 部分支持（见说明），❌ 不支持。

## 支持情况

### 解码

| 功能 | 状态 | 说明 |
|---|:-:|---|
| baseline / extended（SOF0 / SOF1） | ✅ | |
| progressive（SOF2） | ✅ | |
| restart interval | ✅ | |
| 任意色度采样布局 | ✅ | |
| 灰度、YCbCr、RGB 标记、Adobe CMYK / YCCK | ✅ | |
| 12 位精度 | ❌ | |
| 算术编码（SOF9–SOF11） | ❌ | |
| 分层（hierarchical）JPEG | ❌ | |
| 无损 JPEG（SOF3） | ⚠️ | 只有独立的 `LosslessJpeg.FromMemory` API，`JpegDecoder` 和门面拒绝 |
| 外部提供的表（TIFF 的 JPEGTables）和指定色彩变换 | ✅ | `JpegDecodeOptions.Tables` / `ColorTransform` |
| 输出 RGBA / BGRA / RGB，任意行跨度 | ✅ | `DecodeTo` 写进调用方缓冲区，零分配 |

### 编码

| 功能 | 状态 | 说明 |
|---|:-:|---|
| baseline | ✅ | |
| 优化 Huffman 表 | ✅ | |
| progressive | ✅ | 与 baseline 解码出完全相同的像素 |
| 色度采样 4:4:4、4:2:0、灰度 | ✅ | |
| 色度采样 4:2:2 | ❌ | |
| 写 restart interval | ❌ | |
| 12 位、算术编码、无损编码 | ❌ | |
| 写入 Exif、XMP、ICC | ✅ | ICC 超过一段时分多个 APP2 |

### 元数据

| 功能 | 状态 | 说明 |
|---|:-:|---|
| 读取 Exif、XMP、ICC | ✅ | |
| 扩展 XMP（跨多个 APP1 段） | ❌ | 不拼接 |
| Exif 内嵌缩略图 | ✅ | 门面的 `TryDecodeThumbnail` |
| Exif 方向 | ✅ | 只读 APP1 的 Exif 段，不看 XMP 的 `tiff:Orientation` |
| 增益图（Ultra HDR / Adobe） | ✅ | 另一个包，见 [Ultra HDR / 增益图 JPEG](jpeg-gainmap.md) |

### 门面与性能

| 功能 | 状态 | 说明 |
|---|:-:|---|
| 原生缩放解码 1/2、1/4、1/8 | ✅ | DCT 域缩放，不生成全分辨率平面；1/4 的缩减 IDCT 还是标量 |
| 区域解码 | ⚠️ | 只保留 MCU 行条带和区域本身，但每个 MCU 仍要熵解码和 IDCT |
| `TryDecodeInto` 直写调用方缓冲区 | ✅ | |
| 已调优 | ✅ | 446 个文件解码到 RGBA 约为 libjpeg-turbo 的 0.82×；编码有质量门槛 `encode --gate` |

## 已知缺口与局限

- **只支持 8 位精度。** 12 位 JPEG（SOF 精度为 12）会被拒绝。
- **只支持 Huffman 编码的 SOF0 / SOF1 / SOF2**（baseline、extended、progressive）。算术编码（SOF9–SOF11）和 分层（hierarchical）JPEG 不支持。
- **无损 JPEG（SOF3）不走门面。** 它由单独的 `LosslessJpeg.FromMemory` API 解码，`JpegDecoder` 和门面会拒绝 这类文件。
- **EXIF 方向只从 APP1 的 Exif 段读取**，不看 XMP 里的 `tiff:Orientation`。
- **编码器**：只写 8 位；色度采样只有 4:4:4、4:2:0 和灰度（没有 4:2:2）；不写 restart interval；不支持算术 编码和无损编码。
- **1/4 缩放解码的缩减 IDCT 还是标量实现**（性能项，输出正确）。`ScaleDenominator = 4` 走 2×2 缩减 IDCT （`JpegIdct.IdctReduced`，b = 2），没有向量版本；1/2（b = 4）已有 Vector128 版本，1/8 只取 DC。测得 （photo.jpg，1600×1067 4:2:0 q90，Ryzen 7 5800X，.NET 10.0.11）：1/4 解码约 2.9 ms，其中 IDCT 约 1.0 ms（消融 `SkipIdct` 后约 1.9 ms），因此 1/4 反而比 1/2（约 2.5 ms）略慢。向量化必须照搬标量版的运算顺序，输出逐字节不变。
