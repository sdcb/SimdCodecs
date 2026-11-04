# JBIG2：已知缺口与局限

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。

- **独立 `.jb2` 文件只解码第 1 页。** PDF 内嵌的 JBIG2 流没有文件头可供识别，需要直接调用
  `Jbig2Decoder.Decode(embedded, globals, width, height)`，门面不处理。
- **不支持**：
  - EXTTEMPLATE 通用区域；
  - 使用 HENABLESKIP 的半色调区域；
  - MMR 编码的半色调区域和图案字典。
- 未调优。
