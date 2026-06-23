# Testing

本文档记录当前推荐的发布验证流程。历史调参和实机问题只保留摘要，详细流水不再作为公开文档维护。

## 环境

- OS：Windows 10/11
- SDK：.NET SDK 8
- Runtime：Microsoft.WindowsDesktop.App 8
- 游戏设置：简体中文、细体、16:9 画面比例

## 发布验证

推荐从项目目录执行：

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

验收点：

- `dotnet build -c Release` 通过，0 errors。
- `publish/ZZZ-Scanner.Next.exe` 存在。
- `publish/Data/*.json` 存在。
- `publish/Resources/models/characterDict.txt` 存在。
- `publish/Resources/models/PP-OCRv5_mobile_rec_infer.onnx` 存在。
- 发布目录包含 ONNX Runtime 和 OpenCV native dll。
- 发布目录中不应包含历史扫描输出 `Scans/`。

## 人工扫描验收

完整扫描会真实前置游戏窗口、移动鼠标、点击和滚轮，因此需要人工执行。

1. 启动 `publish/ZZZ-Scanner.Next.exe`，接受管理员权限请求。
2. 在游戏中打开背包/仓库的驱动盘页面。
3. 点击“检测窗口”，确认能找到 `ZenlessZoneZero`。
4. 点击“预览详情区”，确认预览是右侧驱动盘详情面板。
5. 设置读取上限为 `3`，只勾选 `S`，执行一次小范围扫描。
6. 检查 `Scans/<time>/export.json`、GUI 表格、`scan.log` 和 `ocr_diagnostics.csv`。
7. 设置读取上限为 `54`，确认默认安全带扫描可连续输出且无 error。
8. 扫描中按 `Ctrl+Shift+C`，确认 GUI 能停止并写出已有结果。

## 回归重点

- DPI：副屏缩放或系统缩放下，详情区预览尺寸应符合 profile 比例，日志会输出窗口客户区、DPI 和坐标倍率。
- OCR：副属性不应整体为空；失败日志应包含序号、品质、ROI 数、OCR 文本和异常堆栈。
- 遍历：默认 `SafeBandViewport` 不应在中间状态点击视觉第 1/4 行；`visibleTopLogicalRow` 应单调推进。
- 性能：`CELL_TIMING` 和 `ocr_diagnostics.csv` 可用于比较面板等待、OCR 耗时和 backlog。
- 取消：人工取消后允许 OCR worker 短暂收尾，导出序号应保持连续。

## 历史问题摘要

- 曾因 DPI 虚拟化导致截图区域按 0.8 缩放，已通过 PerMonitorV2 和 DWM 物理边界校准修复。
- 曾因副属性背景色接近黑色而误判为空，现固定裁剪 12 个文本 ROI 并由清洗阶段跳过空值。
- 曾因游戏滚轮步幅不稳定导致漏扫，默认遍历已改为安全带扫描并进行一行位移验证。
- 曾因 OCR 批处理和并行导致结果乱序风险，现按序号归并后再更新 GUI、导出和重复保护。
