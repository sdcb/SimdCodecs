# JBIG2：已知缺口与局限

条目约定（“不支持”“门面不提供”“未调优”）和跨格式的门面限制见 [总览](../known-gaps.md)。

- **门面只解码独立 `.jb2` 文件的第 1 页。** 其他页用 `Jbig2Decoder.DecodeFile(file, pageNumber)`，页数由
  `Jbig2Decoder.TryReadFileInfo(file, out w, out h, out pages)` 给出。PDF 内嵌的 JBIG2 流没有文件头可供识别，
  需要直接调用 `Jbig2Decoder.Decode(embedded, globals, width, height)`，门面不处理。
- **不支持**：
  - EXTTEMPLATE 通用区域；
  - 引用或保留算术上下文的符号字典（符号字典标志的 bit 8、9）；
  - MMR 的 2D 扩展码（含 T.6 非压缩模式）。
- **Huffman 编码只有部分覆盖。** 标准表、自定义表段、未压缩和 MMR 的高度类集合位图、Huffman 文本区域，都有
  `Jbig2ExtendedOracleTests` 用手工组装的码流对照 jbig2dec。Huffman 符号字典里的精细化 / 聚合符号（SDHUFF 与
  SDREFAGG 同时为 1）、Huffman 文本区域里的逐实例精细化，没有黑盒编码器能造流，只有 issue #1 语料里的
  `042_14.jb2` 用到，而 jbig2dec 和本解码器都判它为坏流，所以这两条路径没有经过对照验证。标准表 B.7 的码长
  之和是 15/16（不满），也只在单元测试里检查过。
- issue #1 附件里的 114 个 `.jb2` 文件（来源和许可不明，不进仓库）由 `Issue1Jbig2CorpusTests` 在本地回归：
  设置 `SIMDCODECS_ISSUE1_DIR` 指向解压目录即可。112 个和 jbig2dec 逐页逐像素一致；`042_13`、`042_14` 两个
  jbig2dec 和本解码器都在同一处判为坏流，本解码器抛 `InvalidDataException`。
- 未调优。
