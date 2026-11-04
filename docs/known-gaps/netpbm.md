# Netpbm（PBM / PGM / PPM / PAM / PFM）：已知缺口与局限

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。

- **只读文件里的第一张图**；后续的图被忽略，门面也不把它们当作多帧。
- **PFM 不能解码成 8 位布局**：浮点样本需要色调策略，`TryDecodeInto` 返回 false，`DecodeTo` 抛 `NotSupportedException`；
  用 `PixelConverter.StretchToRgba8` 得到可看的图。PFM 头里 scale 的绝对值只报告，不应用。
- **PAM 的 DEPTH 只支持 1–4**；更多通道的 PAM 会被拒绝。
- **原始 8 位格式编码到流**基本是内存拷贝，耗时在 0.03–0.2 ms 量级，与 ImageSharp 的相对比值在不同轮次间波动较大（0.5×–1.8×）。
- ImageSharp 3.1.12 读写 16 位 PPM 时按小端（低字节在前），而 Netpbm 规范和 ImageMagick 都是大端；所以 16 位用例不和它计时比较。
