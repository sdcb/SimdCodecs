# TIFF：已知缺口与局限

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。

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
