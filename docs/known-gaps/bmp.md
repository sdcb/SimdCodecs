# BMP：支持情况与已知缺口

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。表中 ✅ 支持，⚠️ 部分支持（见说明），❌ 不支持。

## 支持情况

### 解码

| 功能 | 状态 | 说明 |
|---|:-:|---|
| 各版头部：OS/2 core、OS/2 2.x、INFO、V2 / V3、V4、V5 | ✅ | |
| 1 / 2 / 4 / 8 位调色板 | ✅ | |
| 16 / 24 / 32 位 | ✅ | |
| `BI_BITFIELDS`、`BI_ALPHABITFIELDS`，任意通道掩码 | ✅ | 宽于 8 位的通道只取高 8 位 |
| `BI_RLE4` / `BI_RLE8`（含 delta、提前结束） | ✅ | |
| OS/2 RLE24、Huffman 1D | ✅ | 没有外部解码器可对照 |
| `BI_PNG` | ✅ | |
| alpha | ✅ | |
| 自下而上和自上而下的行 | ✅ | |
| OS/2 位图数组（`BA`） | ⚠️ | 只解第一张 |
| 剪贴板的 packed DIB、图标 / 光标里的 DIB | ✅ | `DecodeDib` |
| `BI_JPEG`、64 位、CMYK 压缩（11–13） | ❌ | |
| OS/2 的图标和指针（`IC`、`CI`、`PT`、`CP`） | ❌ | |

### 编码

| 功能 | 状态 | 说明 |
|---|:-:|---|
| 1 / 4 / 8 位调色板（精确或感知量化，可抖动） | ✅ | |
| `BI_RLE4` / `BI_RLE8` | ✅ | |
| 555、565、24 位、32 位、带 alpha 的 32 位 | ✅ | |
| Core / INFO / V4 / V5 头部，自上而下 | ✅ | |
| 内嵌 ICC（V5） | ✅ | |
| packed DIB、图标图像 | ✅ | `EncodeDib` |
| RLE24、Huffman 1D、`BI_PNG`、`BI_ALPHABITFIELDS`、2 位调色板 | ❌ | |

### 色彩与元数据

| 功能 | 状态 | 说明 |
|---|:-:|---|
| V5 内嵌 ICC | ✅ | |
| V4 / V5 校准色彩空间、链接的 profile | ❌ | |

### 门面与性能

| 功能 | 状态 | 说明 |
|---|:-:|---|
| `TryDecodeInto` 直写调用方缓冲区 | ✅ | SIMD 按行转换，零分配 |
| 原生缩放解码 | ❌ | 可用，整幅解码后 box 缩小 |
| 区域解码 | ⚠️ | 整幅解码再裁剪 |
| 已调优（解码和编码） | ✅ | 1920×1080 解码约为 WIC 的 0.20× |

## 已知缺口与局限

文档没有定死、由解码器决定的行为（窄通道怎样扩展到 8 位、超出调色板的索引、RLE 跳过的像素、32 位 `BI_RGB` 第四个字节算不算 alpha 等），以及它们和 WIC、ImageMagick、ImageSharp 的异同，列在 [BMP 包的 README](../../src/Sdcb.SimdCodecs.Bmp/README.md) 里。

- **不支持**：`BI_JPEG`（BMP 里嵌 JPEG）、64 位像素、Windows Metafile 用的 CMYK 压缩（11–13）、OS/2 的图标和指针 格式（`IC`、`CI`、`PT`、`CP`）。
- **OS/2 位图数组（`BA`）只解码第一张。** 数组里的各张是同一张图为不同显示设备准备的版本。
- **OS/2 Huffman 1D 和 RLE24 没有外部参照。** WIC 和 ImageMagick 都不读这两种压缩，解码器只用按 T.4 码表构造 的流验证过；白色对应调色板索引 0、行之间不填充，这两点是根据规范的推断，没有真实文件核对。
- **宽于 8 位的通道（10-10-10-2 等）只取高 8 位**，不提供 16 位输出。
- **V4 / V5 的校准色彩空间被忽略**：`LCS_CALIBRATED_RGB` 的端点和伽马、链接的 profile（`PROFILE_LINKED`）都 不读；只返回内嵌的 ICC。
- **从 RGBA 输入编码 32 位时比 ImageSharp 慢**（1920×1080 写进流：约 0.41 ms 对 0.23 ms），差在 RGBA→BGRA 的 重排上；从 BGRA 输入就是直接拷贝（0.23 ms），24 位与 ImageSharp 持平。都比 WIC 快 12 倍以上。
- **编码器不写** RLE24、Huffman 1D、`BI_PNG`、`BI_ALPHABITFIELDS` 和 2 位调色板。调色板量化用的是 GIF 同一个量化器，局限也相同（见 [GIF](gif.md)）。
