# Testing

## 2026-05-19

环境：

- OS：Windows 10
- SDK：.NET SDK 8.0.319
- Runtime：Microsoft.WindowsDesktop.App 8.0.22

已执行：

```powershell
dotnet build ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release
dotnet build ZZZ-Scanner.sln -c Release
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish
```

结果：

- Release 编译通过。
- 0 errors。
- 0 warnings。
- 输出 EXE：`ZZZ-Scanner.Next/bin/Release/net8.0-windows/ZZZ-Scanner.Next.exe`。
- 发布目录：`ZZZ-Scanner.Next/publish`。
- 发布目录已包含 `Data/*.json`、OCR 模型、字符字典、ONNX Runtime 和 OpenCV native dll。
- 已用 `Get-Process ZenlessZoneZero` 验证测试机上存在游戏窗口，标题为 `绝区零`，可供后续人工点 GUI 扫描。

未自动执行完整游戏扫描：

- EXE 带 `requireAdministrator` manifest，直接运行会触发 UAC。
- 完整扫描会真实点击副屏游戏窗口；当前只完成编译验证和输出检查。

建议人工测试顺序：

1. 用 Release EXE 启动 GUI。
2. 点击“检测窗口”，确认能找到 `ZenlessZoneZero`。
3. 点击“预览详情区”，确认界面中的临时预览是右侧驱动盘详情面板。
4. 设置读取上限为 `3`，只勾选 `S`，执行一次小范围扫描。
5. 检查 `Scans/<time>/export.json` 和 GUI 表格结果。

## 2026-05-19 DPI 修正验证

问题现象：

- 预览图尺寸为 `360 x 414`，而 profile 详情区在 1920x1080 下应为 `450 x 517`。
- 二者比例为 `0.8`，说明副屏或系统缩放让窗口坐标被 DPI 虚拟化。

已执行：

```powershell
dotnet build ZZZ-Scanner.sln -c Release
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish-dpi-fix
```

结果：

- 编译通过，0 errors，0 warnings。
- 旧 `publish` 目录被正在运行的 `ZZZ-Scanner.Next` 进程锁定，未覆盖。
- 修正版发布到 `ZZZ-Scanner.Next/publish-dpi-fix`。
- 修正版包含 PerMonitorV2 DPI 感知和 DWM 物理边界校准。
- 随后关闭旧扫描器实例，已重新发布到 `publish` 和 `publish-dpi-fix` 两个目录。

## 2026-05-19 副属性检测修正验证

问题现象：

- 扫描结果主属性正常，但所有 `副属性` 均为空。

原因：

- 副属性条采样点颜色为 `RGB(22,22,22)`。
- 旧逻辑使用容差 26 判断“是否接近黑色背景”，导致深灰副属性条被当成空区域。

已执行：

```powershell
dotnet build ZZZ-Scanner.sln -c Release
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish-dpi-fix
```

结果：

- 编译通过，0 errors，0 warnings。
- 用 `preview-detail-panel.png` 离线验证，ROI 计数为 `12`，包含 4 条副属性的名称和值。

## 2026-05-19 UI 与速度调整验证

已执行：

```powershell
dotnet build ZZZ-Scanner.sln -c Release
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish-dpi-fix
```

结果：

- 编译通过，0 errors，0 warnings。
- `publish` 和 `publish-dpi-fix` 均已更新。
- 发布前关闭了正在运行的扫描器进程，未操作游戏进程。

## 2026-05-19 OCR 批处理提速验证

观察：

- 用户测试约 600 个驱动盘耗时过长，约 4 分钟级别。
- 同轮扫描中有 1 个错误：`0299.error.txt`，异常为 `IndexOutOfRangeException`，发生在 OCR 结果清洗阶段。

原因判断：

- 旧 OCR 解码在某个 ROI 识别为空时不会返回占位结果，导致后续字段下标错位或清洗阶段越界。
- 旧错误文件没有记录每个 ROI 的 OCR 文本，因此无法反推出第 299 个具体哪个字段识别为空。

已执行：

```powershell
dotnet build ZZZ-Scanner.sln -c Release
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish-dpi-fix
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish
```

结果：

- 编译通过，0 errors，0 warnings。
- `publish` 和 `publish-dpi-fix` 均已更新。
- 发布目录的 `scan_profiles.json` 已包含更快的 `clickDelayMs=35`、`loadPollMs=25`、`wheelDelayMs=260`。

## 2026-05-19 回顶与停止热键验证

已执行：

```powershell
dotnet build ZZZ-Scanner.sln -c Release
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish-dpi-fix
```

结果：

- 编译通过，0 errors，0 warnings。
- 发布目录的 `scan_profiles.json` 已包含 `resetToTopWheelTicks=45` 和 `listWheelArea`。
- 未发现正在运行的扫描器进程，发布目录可正常覆盖。

## 2026-05-19 稳定读取修正验证

观察：

- 最新测试中 `export.json` 有 142 条结果，其中 29 条副属性为空。
- `0056.error.txt` 与 `0084.error.txt` 显示 `Rois: 4`，4 个 OCR 区域均低置信度识别为 `1`。

原因判断：

- 快速点击后详情面板尚未稳定，程序只截取了名称、等级、主属性名称和值 4 个区域。
- 副属性 ROI 数量仍依赖颜色检测，加载不稳定或采样误差时会丢失副属性。

已执行：

```powershell
dotnet build ZZZ-Scanner.sln -c Release
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish-dpi-fix
```

结果：

- 编译通过，0 errors，0 warnings。
- `publish` 和 `publish-dpi-fix` 均已更新。
- 发布目录的 `scan_profiles.json` 已回调为 `clickDelayMs=60`，并保留批量 OCR 提速。

## 2026-05-19 遍历漏扫修正验证（已废弃方案）

观察：

- 最近两轮扫描无 error，但分别只输出 207、252 条。
- 最后一条分别停在套装的 3 号位附近，数量接近 9 的倍数，说明不是 OCR 中断，而是列表遍历过早结束。

原因判断：

- 当前游戏中一次滚轮不是移动 1 行，而是约 2-3 行。
- 旧算法假设滚轮只移动 1 行，因此只扫描第 3 行，导致每页只读到一部分新内容。

已执行：

```powershell
dotnet build ZZZ-Scanner.sln -c Release
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish-dpi-fix
```

结果：

- 编译通过，0 errors，0 warnings。
- `publish` 和 `publish-dpi-fix` 均已更新。
- 遍历逻辑改为可视页行指纹重叠检测。

## 2026-05-19 稳定遍历二次修正验证（已废弃方案）

观察：

- 行指纹重叠检测在大量同套装、同槽位、同等级、同锁定状态的驱动盘上会误判重复行。
- 实测游戏滚轮事件不按 delta 比例移动，`-120` 和 `-15` 都可能按页滚动，不能作为逐行遍历依据。
- 回顶滚轮发得过快时会被游戏丢弃，列表会从中段或末段开始扫描。

已执行：

```powershell
dotnet build ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish
```

直接 GUI 自动化测试：

- `读取上限=45`：导出 45 条，0 error。
- `scan.log` 记录 `Reset reached top`，并 OCR 到 `驱动仓库【614/3000】 => 614`。
- 前 5 条重新回到截图中的 `流光咏叹` 开头，确认回顶有效。
- `读取上限=270`：导出 270 条，0 error。
- 270 条测试越过原先常停的 207/252 区间，日志到达 `Batch 28` 后因读取上限正常结束。

## 2026-05-19 原项目遍历机制迁移验证

观察：

- 用户实测发现第 4 行之后滚动条拖拽会导致横向扫描过程中列表继续移动。
- 方向键和 WASD 不会移动驱动盘选中框，不能用键盘导航代替鼠标。
- 对照旧项目 `ZZZ-Scanner/Helpers/GameHelper.cs` 后确认，旧项目通过固定扫描可视第 3 行来翻页，滚动参数为 `MouseWheel(-120)`，等待 500ms。

已执行：

```powershell
dotnet build ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish
```

直接 GUI 自动化测试：

- `读取上限=54`：导出 54 条，0 error。
- 日志显示 `Traversal: legacy third-row mode`。
- 实际顺序为可视第 1 行、可视第 2 行、可视第 3 行，然后多次继续扫描可视第 3 行；未再在滚动过程中扫描第 4 行。
- 日志包含 `Scroll: legacy third-row wheel after pass 3, delta=-120`。
- 测试输出目录：`publish\Scans\2026-05-20-00-05-27`。

## 2026-05-26 YAS 风格稳定扫描重构验证

已执行：

```powershell
dotnet build ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release
dotnet build ZZZ-Scanner.sln -c Release
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish-dpi-fix
```

当前结果：

- 编译通过，0 errors，0 warnings。
- 解决方案编译通过，0 errors，0 warnings。
- `publish` 和 `publish-dpi-fix` 均已更新。
- 发布目录的 `scan_profiles.json` 已包含 `CalibratedPage`、`listGridRect`、`rowAlignProbeRect`、`panelChangeProbeRect` 和滚动校准参数。
- 初次静态验证时未检测到 `ZenlessZoneZero` 进程；随后游戏窗口恢复可用，实机结果见下一节。

## 2026-05-26 YAS 风格稳定扫描重构实机验证

已执行：

```powershell
dotnet build ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish
dotnet publish ZZZ-Scanner.Next\ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained false -o ZZZ-Scanner.Next\publish-dpi-fix
```

直接 GUI 自动化测试：

- `读取上限=54`，遍历模式 `CalibratedPage`。
- 导出 54 条，0 error。
- `scan.log` 记录 `Traversal: calibrated-page`。
- 前两次翻页日志显示 `Scroll one row: ticks=1`，平均 tick 保持在 `1.00`。
- 详情面板 checksum 在部分条目上没有跨过阈值，因此走了 `accepted stable panel as fallback`，但没有影响导出结果。
- 首条和末条分别是 `流光咏叹`、`月光骑士颂`。
- 测试输出目录：`publish\Scans\2026-05-26-17-51-07`。

待游戏窗口可用后的实机验收：

- 从列表中部开始，程序必须先回顶；前几条结果应回到顶部驱动盘。
- `读取上限=54`：导出 54 条，0 error，`scan.log` 显示 `Traversal: safe-band viewport`、`VIEWPORT_STATE` 和 `ROW_SCROLL_*` 事件。
- `读取上限=270`：越过历史 200 多条提前停止区间，无乱行、无连续重复熔断。
- `读取上限=0` 且 S/A/B 全选：访问数量应到达仓库 OCR 数量或计算尾行末尾。
- 只选 S：`Visited` 仍遍历全仓库，导出只包含 S。
- 扫描中按 `Ctrl+Shift+C`：GUI 正常取消，队列收尾，写出已有结果。

## 2026-05-26 安全带扫描验收

重点验证绝区零边缘补位规则：

- 从列表中部启动，程序先回顶，首屏只允许点击视觉第 1、2、3 行。
- 中间状态不得出现视觉第 1 行或第 4 行的 `CELL_CLICK`；若代码路径试图点击，必须先写 `EDGE_CLICK_BLOCKED` 并停止。
- 每次向下推进只允许一个 `ROW_SCROLL_TICK`，随后必须有 `ROW_SCROLL_VERIFY` 和 `ROW_SCROLL_DONE`。
- 到达底部后允许点击视觉第 4 行，以读取最后一行和尾行列数。
- `MaxItems=270` 或更高时，日志中的 `visibleTopLogicalRow` 应单调递增，不得出现前后反复跳动。

## 2026-05-26 OCR 并行提速验收

- GUI 中 `OCR线程（0=自动）` 默认为 0；日志首行应显示 `OcrWorkers`、`OcrBatchSize`、`OcrQueueCapacity`。
- `MaxItems=54`：导出 54 条，0 error，日志显示多个 `OCR worker ... started/stopped`。
- `MaxItems=270`：截图采集结束后 GUI 可显示“后台 OCR 正在收尾”，最终 `export.json` 序号仍连续递增。
- 将 OCR 线程手动设为 1：结果应与自动模式一致，用于排查多线程环境问题。

## 2026-05-26 点击节奏与 GUI 表格验收

- 扫描日志中 `Probe` 应出现在 `CELL_CLICK` 之前；过滤掉的品质不应出现对应 `CELL_CLICK`。
- 全品质扫描时应继续等待详情面板稳定，不应新增副属性缺失。
- GUI 表格新增结果后应自动滚到最后一行，当前选中行应为最新识别结果。
