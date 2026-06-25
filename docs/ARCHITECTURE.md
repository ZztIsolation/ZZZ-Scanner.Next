# Architecture

## Pipeline

1. `MainForm` 读取用户设置并创建 `ScanOptions`。
2. `ScanController` 找到 `ZenlessZoneZero` 主窗口。
3. `GameWindow` 前置窗口、点击驱动盘页签、滚到列表顶部。
4. 扫描线程 OCR 仓库数量，默认使用 `SafeBandViewport`：按固定 `4 x 9` 页面模型维护当前可视顶部逻辑行，只点击安全的视觉第 2、3 行；顶部第 1 行和底部第 4 行作为边界特例。
5. 扫描线程按 profile 中的相对坐标逐格点击。
6. 点击后用小区域色相/RGB 混合判断格子品质，按用户选择过滤 S/A/B。
7. 截取右侧详情面板，按 profile 中的 ROI 裁剪名称、等级、主副词条。
8. 截图任务进入较大的 `BlockingCollection`，扫描线程可继续向后采集。
9. 多个 OCR worker 各自持有 ONNX Runtime session，并行执行 PP-OCRv5 识别。
10. OCR 结果按序号归并后进入 `DriveDiscCleaner`/导出流程，避免多线程乱序影响重复保护。
11. `DriveDiscCleaner` 用 wiki 数据做编辑距离纠错和数值合法化。
12. 重复保护器对去序号结果做 fingerprint，连续重复达到一行时取消扫描并写入日志。
13. GUI 实时显示结果，结束后写入 `Scans/<time>/export.json`。

## Non-Invasive Boundary

本系统不读取内存、不挂钩、不注入 DLL。窗口交互仅限 Win32 API：

- `SetForegroundWindow`
- `SetCursorPos`
- `mouse_event`
- `Graphics.CopyFromScreen`

## Resolution Strategy

`scan_profiles.json` 中保留 1920x1080 点位，但运行时会转换成比例坐标，再乘以当前游戏客户区尺寸。这样同一 UI 比例下可跨分辨率运行。

为了减少颜色波动导致的误判，profile 中有 `colorTolerance`。如果用户显示器、HDR 或游戏滤镜导致颜色变化，优先调整这个值。

`SafeBandViewport` 依赖 `listGridRect` 和 `panelChangeProbeRect`。游戏在列表中部点击视觉第 1 行会自动向上补位，点击视觉第 4 行会自动向下补位，所以默认遍历不会在中间状态点击这两行。`CalibratedPage` 保留为高级兼容模式，它还依赖 `rowAlignProbeRect`。

逐行滚动只发送一个滚轮事件；滚动后会用行签名验证“旧视觉第 3 行移动到新视觉第 2 行”等一行位移关系，成功后才更新可视顶部逻辑行。

## OCR Strategy

详情面板文字位置是已知的，所以本系统不跑文字检测模型，只跑识别模型。每个 ROI 会按高度缩放到 48px，并合成 batch tensor，一次送入 `PP-OCRv5_mobile_rec_infer.onnx`。

这比全屏 OCR 快很多，也避免扫描游戏内无关文字。

点击新驱动盘前会记录详情多探针签名；截图前必须看到面板/文本探针变化、连续稳定，以及现有 ROI 可见性稳定，避免把上一张详情面板重复入队。

默认 OCR worker 数为自动：6 核以上使用 2 个，12 核以上使用 3 个；GUI 中可改为 1-4。多 worker 只影响截图后的识别，不改变点击、等待、滚动逻辑。
