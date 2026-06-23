# OCR 专用模型路线

## 目标

当前版本使用 PP-OCRv5 mobile 通用识别模型，稳定但对《绝区零》驱动盘详情面板偏重。2026-06-23 的单 session 调整已把 PP-OCRv5 平均 inference 降到约 229ms/盘；下一步主线是训练 ZZZ 专用小模型，把识别任务限定在游戏字体、固定 ROI 和有限词表内。

目标指标：

- 平均 OCR inference 低于 100ms/盘。
- P90 OCR inference 低于 150ms/盘。
- 字段清洗后导出错误率不高于当前 PP-OCRv5。
- 副属性空值、错位、连续重复误停不得回归。

## Phase 1 数据准备

- 使用高级设置中的 `OCR样本保存上限` 采集 `ocr-samples`，目标至少 3,000-5,000 个驱动盘。
- 也可以使用命令行 `ZZZ-Scanner.Next.exe --collect-ocr-samples [sampleLimit] [maxItems] [raritiesCsv]` 采集；该模式默认关闭“遇到非15级时停止”。
- 样本需要覆盖套装名、等级、主属性、副属性、百分比、整数、小数和空副词条。
- 从 `Data/drive_discs.json` 和 `Data/stat_rules.json` 生成 ZZZ 字段候选集和训练词表。
- 保留 PP-OCRv5 识别结果、清洗后字段和人工抽查结论，作为训练/验证标签来源。

## Phase 2 训练实验

- 参考 YAS 的思路，但不复制 YAS 源码或模型：使用 ZZZ 字体、ZZZ 字段集合、小输入和小词表训练识别模型。
- 优先尝试灰度或轻量输入、固定高度、固定字段 ROI 的 CRNN/SVTR 类结构。
- 产物命名固定为 `Resources/models/ZZZ_fast_rec.onnx` 和 `Resources/models/ZZZ_fast_dict.txt`。
- 建立同一批 `ocr-samples` 上的 PP-OCRv5 vs ZZZ-fast 对比报告。

## Phase 3 运行时接入

- 将 `OcrEngineMode.ZzzFastFieldAware` 接入真实 runtime，不再总是 fallback 到 PP-OCRv5。
- `Auto` 模式优先尝试 ZZZ-fast；模型缺失、加载失败、字段置信度异常或清洗失败时回退 PP-OCRv5。
- 扩展 `--ocr-benchmark` 支持选择 OCR engine，确保同一批样本可以直接比较吞吐和错误。
- 保持导出 JSON schema 不变，避免影响下游使用。

## Phase 4 验收

- 自动基准：对固定样本集运行 PP-OCRv5 和 ZZZ-fast，比较平均、P90、最大 inference、decode 和总耗时。
- 实机小扫：`MaxItems=54`，0 error，副属性不整体为空，结果序号连续。
- 实机长扫：完整 S 级扫描无新增 backlog throttle，无连续重复误停。
- 回退验证：移走 `ZZZ_fast_rec.onnx` 后 `Auto` 能正常回到 PP-OCRv5。

## 短期 A/B 基线

专用模型前，可以继续记录这些基线，但它们不是主路线：

- `--ocr-benchmark <samples> 1 1 6`、`1 2 6`、`1 4 6` 比较跨盘 batch。
- `--ocr-benchmark <samples> 1 1 8` 比较 intra-op 线程上限。
- DirectML 可单独做实验，但必须和游戏运行时 GPU 占用一起评估。
