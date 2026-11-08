# PNG：支持情况与已知缺口

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。表中 ✅ 支持，⚠️ 部分支持（见说明），❌ 不支持。

## 支持情况

### 解码

| 功能 | 状态 | 说明 |
|---|:-:|---|
| 全部颜色类型和位深（灰度、RGB、调色板、带 alpha；1–16 位） | ✅ | 无损，与参照逐字节相等 |
| `tRNS` 透明 | ✅ | |
| Adam7 隔行 | ✅ | |
| 16 位输出 | ✅ | 门面 `TryDecode` 给原始 16 位样本 |
| `TryDecodeInto` 直写调用方缓冲区 | ✅ | |

### 编码

| 功能 | 状态 | 说明 |
|---|:-:|---|
| 灰度、灰度 + alpha、RGB、RGBA，8 / 16 位 | ✅ | |
| 无损缩减（自动选调色板、低位深、灰度） | ✅ | `LosslessReduction` |
| 有损调色板量化（pngquant 的用途：≤256 色，含半透明 tRNS，质量目标，可抖动） | ✅ | `Quantize`；质量刻度是本库自定义的，按 pngquant 2.17 校准过 |
| 多线程分段压缩 | ✅ | `ParallelFragments` |
| Adam7 隔行 | ❌ | |

### 动画（APNG）

| 功能 | 状态 | 说明 |
|---|:-:|---|
| 解码、合成整幅画布 | ✅ | `PngAnimationDecoder`，门面 `TryOpenFrames`，可跳帧 |
| 编码，只写变化矩形 | ✅ | `PngAnimationEncoder`，门面的多帧编码会话 |
| 编码时每帧不同的颜色类型 / 调色板 | ❌ | 全部帧共用一种布局 |
| 编码 Adam7 帧、`DiscardAlpha`、`TransparentColor`、`Quantize` | ❌ | |

### 色彩与元数据（读和写）

| 功能 | 状态 | 说明 |
|---|:-:|---|
| `iCCP`、`sRGB`、`gAMA`、`cHRM` | ✅ | |
| HDR 标记 `cICP`、`mDCV`、`cLLI` | ✅ | PQ / HLG 图 `TryDecodeInto` 返回 `false` |
| `eXIf`、`tEXt`、`zTXt`、`iTXt`、`pHYs` | ✅ | |
| `bKGD`、`sBIT`、`tIME` 等其他辅助块 | ❌ | |

### 门面与性能

| 功能 | 状态 | 说明 |
|---|:-:|---|
| 原生缩放 | ⚠️ | 非隔行图：边解码边按行 box 缩小，不生成全尺寸图，内存约为输出加几行。Adam7 隔行图仍整幅解码后缩小 |
| 区域按需 | ⚠️ | 非隔行图：只解到区域（或缩放 box）需要的最后一行就停，只反滤波到区域最右一列，只展开区域内的列。区域上方的行仍要解压和反滤波（PNG 的行彼此依赖）。Adam7 隔行图仍整幅解码后裁剪 |
| 已调优（编码和解码） | ✅ | 见下方基准链接 |

## 已知缺口与局限

- **APNG 已支持解码和编码**（`PngAnimationDecoder` / `PngAnimationEncoder`，门面的 `TryOpenFrames` 和多帧编码会话），局限如下：
  - 帧是合成后的整幅画布，RGBA（整个动画都不透明时为 RGB），8 位，16 位文件为 16 位；不返回 `bKGD`，也不按 `bKGD` 填充画布。
  - 半透明像素的 `APNG_BLEND_OP_OVER` 规范只给出实数公式，整数舍入是库内自行定义的（四舍五入，alpha 0 / 255 精确）；ffmpeg 8.1.2 的结果在颜色上最多差 2，alpha 完全一致（见 `PngApngOracleTests`）。
  - 默认图不属于动画时（`IDAT` 之前没有 `fcTL`），帧读取器的帧只有 `fdAT` 那些，`TryDecode` 仍返回默认图。
  - 编码器的帧一律以整幅画布给出，所有帧共用一种颜色类型 / 位深 / 调色板（`LosslessReduction` 时按全部帧的并集挑最小的精确布局，这要求保留所有帧的矩形直到 `Encode()`）；不写 Adam7 隔行帧，不支持 `DiscardAlpha` 和 `TransparentColor`；alpha 为 0 的像素颜色不保留。
  - 选择矩形 / dispose / blend 用的是对每个候选做一次快速 deflate 来估算大小，不是穷举最优。
- **编码器不能写 Adam7 隔行图**（解码支持）。
- **不读取 `bKGD`、`sBIT`、`tIME` 等辅助块。** 目前读取的有：`PLTE`、`tRNS`、`gAMA`、`cHRM`、`sRGB`、`iCCP`、 `cICP`、`mDCV`、`cLLI`、`pHYs`、`eXIf`、`tEXt`、`zTXt`、`iTXt`。
- **区域 / 缩放解码在最后需要的一行之后就停止，不校验其下的图像数据。** 非隔行图带 `Region` 或 `ScaleDenominator` 解码时，下方损坏或截断的 zlib 数据不会被读到，解码照常成功；整幅解码则会返回 `false`。各块的 CRC 仍在读块头时全部校验；区域一直到最后一行时，也和整幅解码一样检查数据流的结尾。区域外各列的调色板索引同样不检查。输出与“整幅解码 → box 缩小 → 裁剪 → 转换像素格式”逐字节相同（`PngRegionDecodeTests`）。
- **`TryGetClosestSize` 仍只报存储尺寸**：任何 2 的幂缩放都能用，非隔行图也不生成全尺寸图，但缩放并不比整幅解码更快（box 缩小要处理每一行源数据），所以不把缩放尺寸当作“便宜的原生尺寸”报出。
- **cICP 标为 PQ / HLG 的 HDR 图像，`TryDecodeInto` 返回 `false`**：它们没有正确的 8 位投影，请用 `TryDecode` 拿 16 位原始数据。
- 性能数据和与 libpng / ImageSharp 的逐项对比见 [PNG 基准结果](../../benchmarks/Sdcb.SimdCodecs.Png.Benchmarks/results/)。
