# EXIF：已知缺口与局限

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。

- **只读取 JPEG、TIFF、PNG 和 WebP 里的 EXIF**（`ExifReader`）；HEIF、AVIF 等容器不读。
- **门面不返回 EXIF。** `ImageInfo` 只带方向；其他字段请把同一份文件交给 `ExifReader.FromBytes`。
