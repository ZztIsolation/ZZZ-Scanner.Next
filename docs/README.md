# ZZZ Scanner Next

ZZZ Scanner Next 是旧版命令行扫描器的独立重写版，目标是做成可视化、快速、非侵入式的驱动盘导出工具。

## 当前能力

- WinForms GUI：检测窗口、预览详情区、开始/停止扫描、查看实时结果和日志。
- 默认申请管理员权限：`app.manifest` 使用 `requireAdministrator`，程序入口也会尝试自提升。
- 非侵入式扫描：只使用窗口前置、鼠标点击、滚轮和屏幕截图，不读取或注入游戏进程。
- 快速扫描：扫描线程负责点击和截图，默认 1 个 OCR worker / 1 个 ONNX session 消费队列，ORT 在 session 内并行推理。
- 数据驱动：驱动盘名称、词条候选、词条数值、扫描点位都在 `Data/*.json`。
- 安全带稳定遍历：默认使用 `SafeBandViewport`，避开会触发游戏自动滚动的视觉第 1/4 行，并用详情面板签名和重复保护降低漏扫/误停概率。
- 可设置读取范围：支持只读前 N 个、`0=不限制`、按 S/A/B 品质过滤、临时显示调试截图。

## 入口

- 项目文件：`ZZZ-Scanner.Next.csproj`
- 可执行文件：`bin/Release/net8.0-windows/ZZZ-Scanner.Next.exe`
- GUI 主窗体：`Ui/MainForm.cs`
- 扫描主流程：`Scanning/ScanController.cs`
- OCR：`Ocr/PaddleOcrRecognizer.cs`
- wiki 资料清洗：`Cleaning/DriveDiscCleaner.cs`

## 命令行诊断

- OCR 样本基准：
  `ZZZ-Scanner.Next.exe --ocr-benchmark <ocr-samples-dir> [workers] [batchSize] [intraOpThreads]`
- 扫描日志基准：
  `ZZZ-Scanner.Next.exe --scan-benchmark <scan-dir> [baseline-scan-dir]`

`--ocr-benchmark` 读取 `ocr-samples` 目录，可比较 worker 数、跨盘 batch 和 intra-op 线程数。`--scan-benchmark` 只读取扫描输出目录，不启动 GUI、不申请管理员权限、不操作游戏窗口；传入 baseline 目录时会额外输出关键速度指标的百分比变化。
新版扫描日志包含 `fastAccept`、`probeChangeScore` 和 `stableFrames`，可用于判断高置信快读是否命中以及是否需要回退。

后续 OCR 优化优先走 ZZZ 专用小模型路线，详见 `OCR_MODEL_ROADMAP.md`。

## 重要限制

本版已经把旧版绝对像素点改成了按游戏客户区比例缩放，并给颜色判断加入容差；OCR 输入也会统一缩放到模型高度。因此它比旧版更抗分辨率变化。

但它仍然依赖背包页面整体布局。若游戏 UI 比例、语言或详情面板布局大改，需要维护 `Data/scan_profiles.json` 中的点位和矩形。
