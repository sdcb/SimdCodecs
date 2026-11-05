# PNG：已知缺口与局限

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。

- **APNG 已支持解码和编码**（`PngAnimationDecoder` / `PngAnimationEncoder`，门面的 `TryOpenFrames` 和多帧编码会话），局限如下：
  - 帧是合成后的整幅画布，RGBA（整个动画都不透明时为 RGB），8 位，16 位文件为 16 位；不返回 `bKGD`，也不按 `bKGD` 填充画布。
  - 半透明像素的 `APNG_BLEND_OP_OVER` 规范只给出实数公式，整数舍入是库内自行定义的（四舍五入，alpha 0 / 255 精确）；ffmpeg 8.1.2 的结果在颜色上最多差 2，alpha 完全一致（见 `PngApngOracleTests`）。
  - 默认图不属于动画时（`IDAT` 之前没有 `fcTL`），帧读取器的帧只有 `fdAT` 那些，`TryDecode` 仍返回默认图。
  - 编码器的帧一律以整幅画布给出，所有帧共用一种颜色类型 / 位深 / 调色板（`LosslessReduction` 时按全部帧的并集挑最小的精确布局，这要求保留所有帧的矩形直到 `Encode()`）；不写 Adam7 隔行帧，不支持 `DiscardAlpha` 和 `TransparentColor`；alpha 为 0 的像素颜色不保留。
  - 选择矩形 / dispose / blend 用的是对每个候选做一次快速 deflate 来估算大小，不是穷举最优。
- **编码器不能写 Adam7 隔行图**（解码支持）。
- **不读取 `bKGD`、`sBIT`、`tIME` 等辅助块。** 目前读取的有：`PLTE`、`tRNS`、`gAMA`、`cHRM`、`sRGB`、`iCCP`、 `cICP`、`mDCV`、`cLLI`、`pHYs`、`eXIf`、`tEXt`、`zTXt`、`iTXt`。
- **cICP 标为 PQ / HLG 的 HDR 图像，`TryDecodeInto` 返回 `false`**：它们没有正确的 8 位投影，请用 `TryDecode` 拿 16 位原始数据。
- 性能数据和与 libpng / ImageSharp 的逐项对比见 [PNG 基准结果](../../benchmarks/Sdcb.SimdCodecs.Png.Benchmarks/results/)。
