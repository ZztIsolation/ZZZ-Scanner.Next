# YAS Study Notes

参考项目：

- https://github.com/wormtql/yas
- Repository controller: https://github.com/wormtql/yas/blob/main/yas-genshin/src/scanner_controller/repository_layout/controller.rs
- Artifact worker: https://github.com/wormtql/yas/blob/main/yas-genshin/src/scanner/artifact_scanner/artifact_scanner_worker.rs

## 可借鉴点

- 分层：YAS 将窗口信息、列表控制、截图生产、OCR worker 和导出分开，扫描时只让控制器负责“点击/滚动/等待”。
- 自校准滚动：它不是盲目固定翻页，而是用画面采样判断列表是否移动，并记录一行大约需要多少滚轮 tick。
- 切换等待：它会等详情面板真的切换后再把截图交给 worker，减少读到旧面板的概率。
- 重复保护：连续识别到重复物品时主动停下，避免翻页失败后把同一页重复导出。
- 数据布局仓库：不同分辨率使用同一组语义点位，能按比例缩放，而不是在逻辑里写死每个点。
- OCR 模型路线：YAS 的关键价值不是直接可移植的代码，而是“游戏字体 + 有限字符串 + 小输入 + 小词表”的专用识别模型思路。

## 不直接移植的原因

- YAS 核心库标注为 GPL-2.0-or-later，本项目不复制其源码，避免许可证污染。
- YAS 面向原神/星铁/鸣潮，点位、UI 行数、OCR 模型和导出格式都与绝区零不同。
- 本项目继续保留 WinForms GUI、现有 PP-OCRv5 批处理、BiliGame wiki 数据和当前导出结构。
- 绝区零需要独立训练数据、独立词表和独立 ONNX runtime 适配；不能把 YAS 的模型或字典当作即插即用资产。

## 已吸收进 Next 的设计

- `CalibratedPage` 曾作为 YAS 风格默认遍历模式；发现绝区零第 1/4 行点击补位规则后，默认模式已改为 `SafeBandViewport`。
- profile 新增列表区域、行对齐探针、详情切换探针和滚轮校准参数。
- 点击前记录详情探针签名，点击后等待签名变化并稳定。
- OCR 清洗成功后生成去序号 fingerprint，连续重复达到一行时取消扫描。
- `LegacyThirdRow` 保留为兼容模式，可在 GUI 中手动切换。

## 下一步

- 采集 ZZZ 详情面板 OCR 样本，覆盖套装名、等级、主属性、副属性、数值、百分号和空副词条。
- 从 `Data/drive_discs.json` 和 `Data/stat_rules.json` 生成专用词表，避免继续依赖 PP-OCRv5 的 18k 通用字典。
- 训练并接入 `ZZZ_fast_rec.onnx` / `ZZZ_fast_dict.txt`，让 `OcrEngineMode.ZzzFastFieldAware` 从占位 fallback 变成真实引擎。
