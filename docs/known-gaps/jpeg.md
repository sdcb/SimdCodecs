# JPEG：已知缺口与局限

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。

- **只支持 8 位精度。** 12 位 JPEG（SOF 精度为 12）会被拒绝。
- **只支持 Huffman 编码的 SOF0 / SOF1 / SOF2**（baseline、extended、progressive）。算术编码（SOF9–SOF11）和
  分层（hierarchical）JPEG 不支持。
- **无损 JPEG（SOF3）不走门面。** 它由单独的 `LosslessJpeg.FromMemory` API 解码，`JpegDecoder` 和门面会拒绝
  这类文件。
- **EXIF 方向只从 APP1 的 Exif 段读取**，不看 XMP 里的 `tiff:Orientation`。
- **编码器**：只写 8 位；色度采样只有 4:4:4、4:2:0 和灰度（没有 4:2:2）；不写 restart interval；不支持算术
  编码和无损编码。
