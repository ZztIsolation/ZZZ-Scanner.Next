# ZZZ Scanner Next

ZZZ Scanner Next 是一个面向《绝区零》驱动盘仓库的 WinForms 扫描与导出工具。它通过窗口前置、鼠标点击、滚轮和屏幕截图完成非侵入式读取，不读取或注入游戏进程。

## 下载使用

从 GitHub Releases 下载最新的 `ZZZ-Scanner.Next-win-x64-framework-dependent.zip`，解压后运行 `ZZZ-Scanner.Next.exe`。

程序默认以管理员权限运行，用于稳定操作游戏窗口。首次扫描前请在游戏中打开驱动盘仓库页面。

Release 包内已经包含 OCR 模型文件，普通用户不需要额外下载模型。若系统提示缺少运行时，请安装 .NET 8 Desktop Runtime。

## 当前能力

- 可视化 GUI：检测窗口、预览详情区、开始/停止扫描、实时结果和日志。
- 非侵入式扫描：只使用窗口前置、点击、滚轮和截图。
- OCR 识别：内置 PP-OCRv5 ONNX 识别模型和字典。
- 数据驱动：驱动盘名称、词条候选、数值范围、扫描点位均在 `Data/*.json`。
- 稳定遍历：默认使用 `SafeBandViewport`，避开容易触发自动补位滚动的边缘行。
- 可控范围：支持读取前 N 个、按 S/A/B 品质过滤、临时显示调试截图。

## 从源码构建

需要 Windows 和 .NET 8 SDK。

源码仓库不跟踪较大的 `PP-OCRv5_mobile_rec_infer.onnx` 模型文件。若要从源码运行 OCR，请先从 release 包中复制该文件到 `Resources/models/`。

```powershell
dotnet restore
dotnet publish -c Release -r win-x64 --self-contained true -o dist\publish
```

构建产物位于 `dist/publish/ZZZ-Scanner.Next.exe`。

## 命令行诊断

```powershell
ZZZ-Scanner.Next.exe --ocr-benchmark <ocr-samples-dir> [workers] [batchSize] [intraOpThreads]
ZZZ-Scanner.Next.exe --scan-benchmark <scan-dir> [baseline-scan-dir]
```

`--scan-benchmark` 只读取扫描输出目录，不启动 GUI、不申请管理员权限、不操作游戏窗口。

## 文档

- [架构说明](docs/ARCHITECTURE.md)
- [测试记录](docs/TESTING.md)
- [数据来源](docs/DATA_SOURCES.md)
- [变更记录](docs/CHANGELOG.md)

## License

MIT
