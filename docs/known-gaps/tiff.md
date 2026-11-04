# TIFF：已知缺口与局限

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。

- **读取不支持**：
  - JPEG 压缩；
  - BigTIFF；
  - CCITT 2D 的扩展码（uncompressed mode）。
- **1/2/4 位采样只支持每像素一个无符号采样**（二值图和低位深灰度）；多通道的低位深页面不支持。
- **门面不呈现的页面**：调色板、CMYK、YCbCr、CIELab 这几种 photometric，以及有符号整数和 32 位无符号整数采样，
  门面返回 `false`（`TiffReader` 本身仍能读出原始采样）。
- **写入器只写未压缩和 Deflate（含 ZlibPkzip）**，不写 LZW、PackBits 或 CCITT。
- **Exif 是合并进页面、而不是整块嵌入的**（TIFF 页面的 IFD 就是 Exif 的 IFD0）。写入时 IFD0 的描述性标签进入页面 IFD，
  Exif IFD、Interoperability IFD 和 GPS IFD 作为子 IFD 写出并重定位偏移；读取时按同样的规则重组出一个本机字节序的
  Exif 块。不保留的内容：
  - IFD1（缩略图）；
  - 描述图像数据的标签（尺寸、压缩、strip/tile、采样、YCbCr 等，页面写自己的），以及分辨率（页面的 `DpiX`/`DpiY` 覆盖）；
  - 未知类型的条目、值越界的条目、除上述三个子 IFD 以外的 IFD 指针；
  - MakerNote 原样复制，其内部若有相对 TIFF 头的偏移，换位置后不再有效；
  - 只有 IFD0、没有 Exif IFD 和 GPS IFD 的 Exif 块：IFD0 标签写进页面，但读回时没有 Exif 块。
- **按页访问每次都从文件开头沿 IFD 链走到该页**（只跳指针、不解码），页数很多时随机访问有线性开销。
- 未调优。
