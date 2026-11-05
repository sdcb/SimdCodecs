# Ultra HDR / 增益图 JPEG：支持情况与已知缺口

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。表中 ✅ 支持，⚠️ 部分支持（见说明），❌ 不支持。

## 支持情况

| 功能 | 状态 | 说明 |
|---|:-:|---|
| 识别 MPF + hdrgm XMP 的增益图（Ultra HDR / Adobe） | ✅ | `JpegGainMap.TryRead` / `TrySplit` |
| 按显示余量（headroom）重建线性光 HDR | ✅ | `GainMapImage.ReconstructHdr`；门面 `TryDecode` 给出浮点图 |
| SDR 基础图 | ✅ | 门面 `TryDecodeInto` 输出的是它 |
| HDR 基础图（`BaseRenditionIsHDR=true`） | ❌ | 门面退回去解码主图 |
| 从 HDR + SDR 计算增益图 | ✅ | `JpegGainMap.Compute` |
| 组装 Ultra HDR 文件 | ✅ | `JpegGainMap.Assemble`，写 MPF 和 hdrgm XMP |
| 去掉增益图元数据 | ✅ | `JpegGainMap.RemoveGainMapMetadata` |
| 报告原色 | ✅ | 取自底图 ICC（BT.709、Display P3、BT.2020） |
| 重建结果带 ICC | ❌ | 有意留空，见下 |
| Exif 缩略图 | ✅ | 与 JPEG 相同 |

## 已知缺口与局限

- **重建出的 HDR 不带 ICC。** `IDecodedImage.Metadata.Icc` 有意留空：浮点结果是线性光，底图的 ICC 描述的是 非线性的显示曲线，套上去会误导色彩管理。需要底图的配置文件时，用 `JpegDecoder.TryReadIccProfile(file)` 读取， 并注明它属于 SDR 底图。原色仍会报告：`ColorEncoding.Primaries` 取自底图的 ICC（`IccProfiles.TryGetPrimaries` 能识别 BT.709、Display P3、BT.2020），没有 ICC 时按 sRGB 报 BT.709，识别不了时报 `Unspecified`。
- **只支持 SDR 基础图的形式。** 基础图本身是 HDR 的文件（`BaseRenditionIsHDR=true`）不支持 HDR 重建，门面会 退回去解码它的主图。
