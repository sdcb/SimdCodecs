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
- **按页访问每次都从文件开头沿 IFD 链走到该页**（只跳指针、不解码），页数很多时随机访问有线性开销。
- 未调优。
