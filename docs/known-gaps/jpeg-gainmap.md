# Ultra HDR / 增益图 JPEG：已知缺口与局限

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。

- **重建出的 HDR 不带 ICC。** `IDecodedImage.Metadata.Icc` 有意留空：浮点结果是线性光，底图的 ICC 描述的是
  非线性的显示曲线，套上去会误导色彩管理。需要底图的配置文件时，用 `JpegDecoder.TryReadIccProfile(file)` 读取，
  并注明它属于 SDR 底图。原色仍会报告：`ColorEncoding.Primaries` 取自底图的 ICC（`IccProfiles.TryGetPrimaries`
  能识别 BT.709、Display P3、BT.2020），没有 ICC 时按 sRGB 报 BT.709，识别不了时报 `Unspecified`。
- **只支持 SDR 基础图的形式。** 基础图本身是 HDR 的文件（`BaseRenditionIsHDR=true`）不支持 HDR 重建，门面会
  退回去解码它的主图。
