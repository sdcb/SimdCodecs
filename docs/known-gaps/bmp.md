# BMP：已知缺口与局限

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。

文档没有定死、由解码器决定的行为（窄通道怎样扩展到 8 位、超出调色板的索引、RLE 跳过的像素、32 位 `BI_RGB` 第四个字节算不算 alpha 等），以及它们和 WIC、ImageMagick、ImageSharp 的异同，列在 [BMP 包的 README](../../src/Sdcb.SimdCodecs.Bmp/README.md) 里。

- **不支持**：`BI_JPEG`（BMP 里嵌 JPEG）、64 位像素、Windows Metafile 用的 CMYK 压缩（11–13）、OS/2 的图标和指针 格式（`IC`、`CI`、`PT`、`CP`）。
- **OS/2 位图数组（`BA`）只解码第一张。** 数组里的各张是同一张图为不同显示设备准备的版本。
- **OS/2 Huffman 1D 和 RLE24 没有外部参照。** WIC 和 ImageMagick 都不读这两种压缩，解码器只用按 T.4 码表构造 的流验证过；白色对应调色板索引 0、行之间不填充，这两点是根据规范的推断，没有真实文件核对。
- **宽于 8 位的通道（10-10-10-2 等）只取高 8 位**，不提供 16 位输出。
- **V4 / V5 的校准色彩空间被忽略**：`LCS_CALIBRATED_RGB` 的端点和伽马、链接的 profile（`PROFILE_LINKED`）都 不读；只返回内嵌的 ICC。
- **从 RGBA 输入编码 32 位时比 ImageSharp 慢**（1920×1080 写进流：约 0.41 ms 对 0.23 ms），差在 RGBA→BGRA 的 重排上；从 BGRA 输入就是直接拷贝（0.23 ms），24 位与 ImageSharp 持平。都比 WIC 快 12 倍以上。
- **编码器不写** RLE24、Huffman 1D、`BI_PNG`、`BI_ALPHABITFIELDS` 和 2 位调色板。调色板量化用的是 GIF 同一个 Wu 量化器，局限也相同（见 [GIF](gif.md)）。
