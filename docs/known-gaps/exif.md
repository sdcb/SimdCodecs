# EXIF：支持情况与已知缺口

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。表中 ✅ 支持，⚠️ 部分支持（见说明），❌ 不支持。

## 支持情况

| 功能 | 状态 | 说明 |
|---|:-:|---|
| 从 JPEG、TIFF、PNG、WebP 读取 | ✅ | `ExifReader.FromBytes` 自动识别容器 |
| 从 HEIF、AVIF 读取 | ✅ | `ExifReader.FromHeif`，`FromBytes` 按 `ftyp` 品牌识别；只解析 `meta` 盒，不解码图像 |
| 把方向标签改为 1 | ✅ | `ExifWriter.ResetOrientation`，用于自己按 `ImageInfo.Orientation` 旋转像素的调用方 |
| 常用字段：相机、拍摄时间、曝光、GPS、方向等 | ✅ | `ExifMetadata` |
| 原始标签表 | ✅ | 按标签号取原始字节 |
| 生成 Exif 块（软件、描述、作者、时间、注释） | ✅ | `ExifWriter.Build`，可交给 JPEG 编码器（`JpegEncodeOptions.Exif`） |
| 门面直接返回解析后的 EXIF | ❌ | `ImageInfo` 只带方向 |

## 已知缺口与局限

- **只读取 JPEG、TIFF、PNG、WebP、HEIF 和 AVIF 里的 EXIF**（`ExifReader`）；其他容器不读。HEIF / AVIF 只认主图像经 `cdsc` 引用的 `Exif` item，`FromHeif` 返回文件里记录的原始方向（HEIF 解码器交出的块里则已是 1）。
- **门面不返回 EXIF。** `ImageInfo` 只带方向；其他字段请把同一份文件交给 `ExifReader.FromBytes`。
