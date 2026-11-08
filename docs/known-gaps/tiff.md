# TIFF：支持情况与已知缺口

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。表中 ✅ 支持，⚠️ 部分支持（见说明），❌ 不支持。

## 支持情况

### 容器与布局

| 功能 | 状态 | 说明 |
|---|:-:|---|
| 经典 TIFF，两种字节序 | ✅ | |
| BigTIFF | ✅ | 只读，写入器不写 |
| 多页（IFD 链） | ✅ | 门面 `TryOpenFrames` 逐页 |
| 条带和分块 | ✅ | 可只读单个条带 / 分块（`ReadSegment`） |
| PlanarConfiguration 1 和 2 | ✅ | 子采样 YCbCr 和 JPEG YCbCr 不支持分离平面 |

### 采样与颜色

| 功能 | 状态 | 说明 |
|---|:-:|---|
| 8 / 16 / 32 位整数（无符号、有符号） | ✅ | 有符号和 32 位无符号门面不呈现 |
| 16 / 32 位浮点 | ✅ | |
| 1 / 2 / 4 位 | ⚠️ | 只支持每像素一个采样 |
| 灰度（MinIsWhite / MinIsBlack）、RGB | ✅ | |
| 调色板 | ✅ | |
| CMYK | ⚠️ | 简单公式转换，不做 ICC 色彩管理 |
| YCbCr（含子采样） | ⚠️ | 色度复制到整个数据单元，不插值 |
| alpha（ExtraSamples） | ✅ | 只取第一个 alpha；预乘的会还原 |
| CIELab | ❌ | |

### 压缩（读取）

| 功能 | 状态 | 说明 |
|---|:-:|---|
| 不压缩、LZW、Deflate / Adobe Deflate、PackBits | ✅ | |
| predictor 2（水平差分）和 3（浮点） | ✅ | |
| CCITT Modified Huffman、Group 3（1D / 2D）、Group 4 | ✅ | 2D 扩展码（uncompressed mode）不支持 |
| JPEG（Compression 7，含 JPEGTables） | ✅ | |
| 旧式 JPEG（Compression 6） | ❌ | |

### 写入

| 功能 | 状态 | 说明 |
|---|:-:|---|
| 经典 TIFF，条带或分块 | ✅ | |
| 8 / 16 / 32 位整数和浮点 | ✅ | |
| 不压缩、Deflate | ✅ | |
| LZW、PackBits、CCITT、JPEG 压缩 | ❌ | |
| BigTIFF | ❌ | |
| ICC、XMP、Exif | ✅ | Exif 合并进页面 IFD，见下 |

### 元数据

| 功能 | 状态 | 说明 |
|---|:-:|---|
| ICC、XMP | ✅ | |
| Exif / GPS / Interoperability IFD | ⚠️ | 重组为一个 Exif 块，部分内容不保留，见下 |
| 分辨率、方向 | ✅ | |

### 门面与性能

| 功能 | 状态 | 说明 |
|---|:-:|---|
| 区域解码只读相交的条带 / 分块 | ✅ | |
| `TryDecodeInto` 直写调用方缓冲区 | ⚠️ | 整数页面按条带写入；浮点页面不投影到 8 位 |
| 原生缩放解码 | ❌ | 可用，整幅解码后 box 缩小 |
| 已调优 | ❌ | |

## 已知缺口与局限

- **读取不支持**：
  - 旧式 JPEG 压缩（Compression=6，已被 TIFF Technical Note 2 的 Compression=7 取代），给出明确的 `NotSupportedException`；
  - 以分离平面（PlanarConfiguration=2）存储、或带 predictor 的子采样 YCbCr；分离平面的 JPEG YCbCr；
  - CCITT 2D 的扩展码（uncompressed mode）。
- **1/2/4 位采样只支持每像素一个无符号采样**（二值图、低位深灰度和调色板索引）；多通道的低位深页面不支持。
- **门面不呈现的页面**：CIELab photometric，以及有符号整数和 32 位无符号整数采样，门面返回 `false`（`TiffReader` 本身仍能读出原始采样）。调色板、CMYK、YCbCr 和 JPEG 页面按包内定义的算术转换为 RGB（见包的 README）。
- **颜色转换的取舍**：非 JPEG 的子采样 YCbCr 把色度复制到整个数据单元（不插值，不使用 YCbCrPositioning）；不应用 TransferFunction、TransferRange、WhitePoint 和 PrimaryChromaticities；CMYK 用简单的 `(1−C)(1−K)` 公式而不做 ICC 色彩管理；ExtraSamples 只取第一个 alpha，其余额外采样丢弃。
- **写入器只写经典 TIFF**（不写 BigTIFF），只写未压缩和 Deflate（含 ZlibPkzip），不写 LZW、PackBits、CCITT 或 JPEG。
- **Exif 是合并进页面、而不是整块嵌入的**（TIFF 页面的 IFD 就是 Exif 的 IFD0）。写入时 IFD0 的描述性标签进入页面 IFD， Exif IFD、Interoperability IFD 和 GPS IFD 作为子 IFD 写出并重定位偏移；读取时按同样的规则重组出一个本机字节序的经典 TIFF 结构 Exif 块（BigTIFF 页面也一样）。不保留的内容：
  - IFD1（缩略图）；
  - 描述图像数据的标签（尺寸、压缩、strip/tile、采样、YCbCr 等，页面写自己的），以及分辨率（页面的 `DpiX`/`DpiY` 覆盖）；
  - 未知类型的条目（包括 BigTIFF 的 LONG8 等 64 位类型的值）、值越界的条目、除上述三个子 IFD 以外的 IFD 指针；
  - MakerNote 原样复制，其内部若有相对 TIFF 头的偏移，换位置后不再有效；
  - 只有 IFD0、没有 Exif IFD 和 GPS IFD 的 Exif 块：IFD0 标签写进页面，但读回时没有 Exif 块。
- **按页访问每次都从文件开头沿 IFD 链走到该页**（只跳指针、不解码），页数很多时随机访问有线性开销。
- **整页解码（无 Region 的 `TryDecode`）仍先组装整页原始采样再转换**；区域解码和整数页面的 `TryDecodeInto` 才按 strip / tile 读取。
- 未调优。
