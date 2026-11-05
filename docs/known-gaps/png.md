# PNG：已知缺口与局限

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。

- **APNG 不解码**：`acTL` 的帧数会报告，但只能解出默认图；**也不能编码 APNG**。
- **编码器不能写 Adam7 隔行图**（解码支持）。
- **不读取 `bKGD`、`sBIT`、`tIME` 等辅助块。** 目前读取的有：`PLTE`、`tRNS`、`gAMA`、`cHRM`、`sRGB`、`iCCP`、 `cICP`、`mDCV`、`cLLI`、`pHYs`、`eXIf`、`tEXt`、`zTXt`、`iTXt`。
- **cICP 标为 PQ / HLG 的 HDR 图像，`TryDecodeInto` 返回 `false`**：它们没有正确的 8 位投影，请用 `TryDecode` 拿 16 位原始数据。
- 性能数据和与 libpng / ImageSharp 的逐项对比见 [PNG 基准结果](../../benchmarks/Sdcb.SimdCodecs.Png.Benchmarks/results/)。
