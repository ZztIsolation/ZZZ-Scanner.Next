# ZZZ Scanner Next

ZZZ Scanner Next 是一个面向《绝区零》驱动盘仓库的 WinForms 扫描与导出工具。它通过窗口前置、鼠标点击、滚轮和屏幕截图完成非侵入式读取，不读取或注入游戏进程。

## 下载使用

网页用户只需下载并运行 NativeAOT `ZZZ-Scanner-Helper.exe`。Helper 会检测系统是否已有
`Microsoft.WindowsDesktop.App 8.x`：已有时下载较小的 framework-dependent 包，没有或检测结果不确定时
自动下载 self-contained 兼容包，不安装 .NET，也不修改系统组件。

手动下载时，GitHub Release 同时提供 `ZZZ-Scanner.Next-win-x64-fdd.zip` 和
`ZZZ-Scanner.Next-win-x64-self-contained.zip`。前者需要 .NET 8 Desktop Runtime，后者不需要预装 .NET。

正式支持 Windows 10 1809（Build 17763）及以上 x64、Windows 11 x64，包括 N 版和 LTSC。
x86、ARM64 和 Windows 7 不在当前支持范围。扫描器默认以普通权限运行；仅当游戏进程权限更高时，
网页才会说明原因并提供“以管理员权限重启扫描器”。首次扫描前请在游戏中打开驱动盘仓库页面。

两种 Release 包都包含 OCR 模型、ONNX Runtime 和其所需的 VC 运行库，普通用户不需要额外安装组件。
当前发布不做代码签名，因此 SmartScreen、杀毒软件或企业策略可能在 Helper 启动前拦截程序；这种系统级拦截
无法由尚未启动的 Helper 自诊断。

网页扫描通过本地 Helper 建立一次性令牌连接。远程 manifest 和扫描器包只允许 HTTPS；
`http://localhost` / `http://127.0.0.1` 仅保留给本地开发。Helper 在启动扫描器前会重新校验缓存 ZIP，
并逐文件核对已安装 runtime，检测到损坏或篡改时自动重新安装。

## 当前能力

- 可视化 GUI：检测窗口、预览详情区、开始/停止扫描、实时结果和日志。
- 非侵入式扫描：只使用窗口前置、点击、滚轮和截图。
- OCR 识别：内置 PP-OCRv5 ONNX 识别模型和字典。
- 数据驱动：驱动盘名称、词条候选、数值范围、扫描点位均在 `Data/*.json`。
- 稳定遍历：默认使用 `OverlapSignaturePage`，按可见页重叠补扫和逻辑行签名去重，兼容偶发一次滚动推进两行的情况。
- 可控范围：支持读取前 N 个、按 S/A/B 品质过滤、临时显示调试截图。

## 从源码构建

需要 Windows 和 .NET 8 SDK。

源码仓库不跟踪较大的 `PP-OCRv5_mobile_rec_infer.onnx` 模型文件。若要从源码运行 OCR，请先从 release 包中复制该文件到 `Resources/models/`。

```powershell
dotnet restore
dotnet run --project Tests\ZZZ-Scanner.Next.RegressionTests.csproj -c Release
.\scripts\publish-slim.ps1 -Version 1.0.37
```

构建产物位于 `dist/publish-scanner-<version>-fdd` 和
`dist/publish-scanner-<version>-self-contained`，同时生成两个 ZIP、NativeAOT Helper、schema v2 manifest
和体积报告。脚本强制 FDD 不超过 25 MiB、自包含包不超过 90 MiB、Helper 不超过 10 MiB，超限会列出
最大文件并终止发布。

`publish-slim.ps1` 会把 `-Version` 同步写入程序集和文件版本，并在压缩前校验生成的 EXE；
省略 `-Version` 时读取项目文件中的版本。可用 `-OutputRoot <dir>` 将验证发布写到独立目录，避免覆盖正式 `dist/`。

## 命令行诊断

```powershell
ZZZ-Scanner.Next.exe --scan-benchmark <scan-dir> [baseline-scan-dir]
```

`--scan-benchmark` 只读取扫描输出目录，不启动 GUI、不申请管理员权限、不操作游戏窗口。

自动压测扫描可以直接运行：

```powershell
ZZZ-Scanner.Next.exe --scan-once --max-items 120
```

`--scan-once` 会用默认 GUI 参数直接扫描当前游戏进程，结束后打印 `output_dir` 和 `export_file`，便于连续做“发布 -> 实扫 -> benchmark”。

Fast OCR 影子数据和可回退辅助识别可用下面的命令做离线验证：

```powershell
ZZZ-Scanner.Next.exe --ocr-shadow-analyze <scan-dir-or-parent> --build-fast-index <index.json>
ZZZ-Scanner.Next.exe --ocr-fast-eval <index.json> <scan-dir-or-parent>
ZZZ-Scanner.Next.exe --ocr-fast-cross-validate <shadow-parent>
ZZZ-Scanner.Next.exe --ocr-fast-calibrate <shadow-parent> --output <index.json>
ZZZ-Scanner.Next.exe --ocr-fast-calibrate <shadow-parent> --output <index.json> --feature v6
ZZZ-Scanner.Next.exe --ocr-fast-merge-indexes <output.json> <index1.json> <index2.json> [...]
ZZZ-Scanner.Next.exe --ocr-fast-feature-eval <shadow-parent>
ZZZ-Scanner.Next.exe --scan-once --max-items 120 --ocr-fast-assist --ocr-fast-index <index.json>
ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode
ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --capture-mode dxgi
ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --adaptive-timing
ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --no-adaptive-timing
ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --capture-mode dxgi --panel-stability-mode text-core
ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --capture-mode dxgi --scroll-accept-mode early-one-row
ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --capture-mode dxgi --panel-accept-mode adaptive-early-full-roi
ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --capture-mode dxgi --panel-accept-mode safe --scroll-accept-mode safe
ZZZ-Scanner.Next.exe --scan-once --max-items 0 --fast-mode --capture-mode dxgi --overlap-conflict-mode recover
ZZZ-Scanner.Next.exe --scan-once --collect-visual-profile local-1280x720-current --visual-profile-client local --visual-profile-quality current --capture-mode dxgi --max-items 120
ZZZ-Scanner.Next.exe --ocr-fast-calibrate-visual-profiles <shadow-parent> --output <index.json> --feature v6
ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --capture-mode dxgi --profile-routing family
ZZZ-Scanner.Next.exe --capture-stability-suite both --max-items 120 --rounds 5
ZZZ-Scanner.Next.exe --capture-stability-suite dxgi --suite-profile speed-1.0.27 --max-items 120 --rounds 5
ZZZ-Scanner.Next.exe --scan-stability-suite <scan-parent>
```

`--ocr-fast-calibrate` 会基于多轮 shadow 数据生成模板索引，并按字段写入是否启用 assist、最低分数和最低 margin。1.0.31 起推荐 `--feature v6`：先对 ROI 文字核心做 canonical crop，再生成灰度、横向差分、纵向差分和边缘梯度 hash。少于两轮 shadow 数据时会保持 assist 全部禁用。

`--feature v4` 仍可读取/生成旧实验特征；`--ocr-fast-feature-eval` 会对比 v3/v4/v6 的跨轮误接受和接受率。

`--ocr-fast-assist` 默认关闭，只会对通过索引策略的字段名类 ROI 使用快路径；不支持、未启用或低置信度的 ROI 会回退 PP-OCR。1.0.36 起 `name` 不参与正式 assist 导出，始终回退 PP-OCR，避免套装名模板误接受污染槽位。

`--fast-mode` 会启用经过验证的 fast profile 和 Fast OCR assist；启动时会检查 `Data/ocr_fast_templates.json` 是否为 v3+ 且存在已启用字段，不满足时自动回退普通模式并写入日志。

1.0.30 起支持多环境视觉 profile。`--collect-visual-profile <id>` 是安全采集快捷命令，会关闭 Fast OCR assist、开启 `--ocr-shadow-dataset`、使用 safe 面板/滚动策略并写入 `visual_profile.json`；配套参数为 `--visual-profile-client local|cloud|unknown`、`--visual-profile-quality <label>`。扫描目录会记录用户请求标签、实际检测到的窗口几何、DPI、capture 后端、profile family 和 profile 路由。若请求标签的几何与实际客户区不一致，训练/assist 会自动使用 detected profile，并在 `ProfileGeometryStatus` 中记录 fallback 原因。

1.0.34 内置了已验证的本地三挡分辨率与云绝区零大窗口/普通窗口/全屏 Fast OCR v6 模板：`local-1280x720-current`、`local-1600x900-current`、`local-1920x1080-current`、`cloud-1592x896-current`、`cloud-1440x808-current`、`cloud-1920x1080-current`。本地三挡已完成 120 件 assist 与默认有效全量扫描验收；云大窗口、普通窗口、全屏均完成 shadow、120 件 assist 和默认有效全量验收。`--fast-mode` 会用 strict exact profile 路由，未知分辨率或未训练 profile 自动回退 PP-OCR。`family`、`compatible` 和 `auto` 仍保留为显式 eval/shadow 探索路径，`auto` 的全局 fallback 不作为默认 assist 导出路径。`--ocr-fast-merge-indexes` 可把多个 v6 index 合并成候选模板，合并后仍按 profile policy 控制字段启用。1.0.35 起网页 WebSocket `scan_req` 可显式传入 `processName`；云绝区零应传 `Zenless Zone Zero Cloud` 并配合 `visualProfileClient=cloud`。1.0.36 起 benchmark 会输出 `slot_safety_pass` 和槽位违规计数，任何槽位/主词条非法组合都不能作为发布验收通过。

1.0.32 还收紧了 `selection_changed_stable_full_roi` 兜底：滚动后首格、retry/fallback/recover 场景、以及 selection 变化时间没有明确正值时，不允许只凭选中态变化接受详情面板；日志会记录 `PANEL_SELECTION_ONLY_BLOCKED`。同排相邻格如果只出现弱 panel change，或点击后 25ms 内出现过早 panel change，也会记录 `PANEL_WEAK_CHANGE_BLOCKED` 并触发 stale retry，避免把旧面板入队。benchmark 输出 `selection_only_accept_count`、`post_scroll_selection_only_blocked_count`、`weak_panel_change_blocked_count` 和 `fast_exact_profile_accept_count`。

`--capture-mode gdi|dxgi` 可选择截图后端；默认仍是 GDI。DXGI 是显式实验路径，初始化失败、取帧异常或显示器不匹配时会回退 GDI 并写入 `scan.log`。

1.0.20 起扫描内部增加 frame 诊断，`scan.log` 会记录 `captureFrameBackend`，benchmark 会输出 `frame_capture_ms`、`frame_to_bitmap_ms` 和 `bitmap_created_count`。DXGI raw BGRA 目前只保留为后续实验路径；本机验证发布候选默认仍使用稳定的 `bitmap-fallback`。

1.0.21 验证了更激进的 quick panel accept 和 DXGI raw frame 缓存，但它们分别带来重复导出或速度退化，因此默认不启用。当前推荐仍是 `--fast-mode --capture-mode dxgi` 的稳定路径；benchmark 中的 `quick_accept_count` 应为 0。

`--panel-stability-mode panel|text-core|auto` 可切换面板稳定判定。`text-core` 只观察 12 个 OCR ROI 的文字核心区域，但仍必须看到详情变化、12 ROI 全可见并满足稳定帧；`auto` 会先 warmup 对比两者。1.0.22 本机 120 件实测 text-core 没有提速，默认 fast-mode 仍使用 `panel`，text-core/auto 仅作为显式实验。

`--scroll-accept-mode safe|early-one-row` 可切换滚动接受策略。`early-one-row` 在行签名确认只前进一行时提前接受滚动结果，多行 overshoot 仍会阻断且不做回滚。1.0.24 起 fast-mode 默认使用 `early-one-row`；如需保守对照，可显式传入 `--scroll-accept-mode safe`。

`--panel-accept-mode safe|adaptive-early-full-roi` 可切换面板接受策略。`adaptive-early-full-roi` 只在 warmup 后、非滚动后首格中启用；仍必须看到详情变化、12 个 OCR ROI 全可见、达到本轮自适应最低等待，且不启用 quick accept。1.0.24 起 fast-mode 默认使用该策略；如需保守对照，可显式传入 `--panel-accept-mode safe`。

`--post-scroll-panel-accept-mode safe|adaptive-after-scroll` 可切换滚动后首格面板接受实验策略。1.0.25 默认仍为 `safe`；`adaptive-after-scroll` 可降低首格等待。1.0.26 中它与 DXGI `--panel-min-accept-floor 110` 组合 5 轮全 pass，平均 `3.719/s`，但未达到默认升级的 5% 平均增益门槛，因此仍需显式传入。

`--panel-min-accept-floor <90..120>` 可显式测试面板最低等待下限。默认仍为 `120ms`；本机 `110ms` 多轮通过，1.0.26 中单独使用 5 轮平均 `3.655/s`，与滚动后首格 adaptive 组合 5 轮平均 `3.719/s`。`90ms` 曾触发滚动签名一致性保护，不建议使用。

`--panel-floor-mode static|scene-adaptive` 可切换场景化面板最低等待。`scene-adaptive` 只允许同排普通点击使用更低下限，滚动后首格、warmup、视觉第 2 行补扫和 retry/fallback 场景继续保守；配套参数为 `--same-row-panel-min-accept-floor <100..120>`、`--post-scroll-panel-min-accept-floor <100..120>`。`--scroll-tick-delay-ms <50..80>` 可显式测试滚动 tick 等待。1.0.27 中 5 轮矩阵和最佳候选 10 轮均 correctness 全 pass，但最佳 10 轮平均 `3.624/s`，低于 1.0.26 最佳候选，因此这些参数仍作为显式实验，不替代默认 fast-mode。

`--overlap-conflict-mode strict|recheck|recover` 可切换重叠页滚动签名冲突处理。普通模式默认 `recheck`，`--fast-mode` 默认 `recover`。1.0.28 起遇到 `scrollRows=1`、`signatureRows=2` 这类弱冲突时，会先连续复核列表签名；弱证据不会直接覆盖滚动验证结果，只有强二行且已扫逻辑行集合能证明不会漏行时才接受二行推进，否则安全停止。benchmark 会输出 `overlap_conflict_count`、`overlap_conflict_recovered_count`、`overlap_ambiguous_accept_count`、`overlap_hard_stop_count`、`full_scan_complete` 和 `missing_logical_rows_count`。

`--adaptive-timing` 会启用本轮自适应面板等待和 OCR 背压限速；`--fast-mode` 默认启用，`--no-adaptive-timing` 可用于对照复测。该状态只存在于本次扫描内，不保存机器画像。1.0.24 的 DXGI fast 默认命令验收为 `MaxItems=120`、`completed_per_sec=3.656`、重复导出 0、`IncompleteRoi=0`、`quick_accept_count=0`、滚动验收全 pass；1.0.20 的 DXGI fast 基线为 `3.406/s`。

1.0.26 新增 `--capture-stability-suite gdi|dxgi|both --max-items <n> --rounds <n>`，用于自动串行跑后端/参数候选；1.0.27 增加 `--suite-profile speed-1.0.27`，固定跑 DXGI 默认、floor110+postscroll、scene-adaptive 105/100 和 scroll 60/50 的候选矩阵。同版 benchmark 输出 `panel_floor_mode`、`same_row_panel_floor_ms`、`post_scroll_panel_floor_ms`、`floor_wait_limited_count/ms`、`panel_accept_elapsed_vs_floor_ms` 和 `scroll_tick_delay_ms`。1.0.26 的 `--scan-stability-suite` 输出 `recommended_candidate`、`reject_reason` 和 `speed_vs_baseline_percent`；五轮实测中 GDI 默认出现重复导出，拒绝作为推荐；DXGI `--panel-min-accept-floor 110 --post-scroll-panel-accept-mode adaptive-after-scroll` 5/5 轮全 pass、平均 `3.719/s`，但平均增益低于 5%，仍作为显式实验候选。1.0.25 新增 `--scan-stability-suite <scan-parent>` 用于跨轮汇总速度分布和 correctness；同版 benchmark 输出 `post_scroll_adaptive_accept_count`、`panel_min_floor_ms` 和 `before_min_accept_count`。1.0.24 起 benchmark 输出 `roi_complete_frames`、`selected_stable_frames`、`panel_frames_after_warmup` 和 `accept_gate_reason_*_count`；1.0.23 起输出 `same_row_panel_wait_ms`、`post_scroll_first_panel_wait_ms` 和 `post_scroll_first_cell_total_ms`，用于拆分普通同排点击与滚动后首格等待；1.0.22 起输出 `panel_text_stable_ms`、`panel_stable_source_*`、`rarity_probe_ms` 和 `selection_probe_ms`；1.0.21 起输出 `quick_accept_count`、`quick_accept_rate_percent`、`panel_frames` 和 `selection_change_ms`；1.0.20 起输出 `frame_capture_ms`、`frame_to_bitmap_ms` 和 `bitmap_created_count`；1.0.18 起输出 `adaptive_throttle_ms`、`ocr_backlog_before_enqueue` 和 `adaptive_panel_min_ms`；1.0.17 起输出 `capture_ms`、`panel_signature_ms`、`visible_roi_ms`、`frame_loop_ms`、`scroll_list_stable_ms` 和 `row_signature_ms`；1.0.16 起输出 `panel_change_ms`、`panel_roi_ms`、`panel_stable_ms`、`after_scroll_extra_ms` 和 `capture_limited`；1.0.15 起输出 `fast_accepted_per_item`、`fast_rejected_per_item`、`ppocr_roi_per_item` 和 `fast_match_ms_per_item`。1.0.3 起 benchmark 还会输出重叠页扫描计数，用于确认补扫行、滚动接受和导出一致性。

## 文档

- [架构说明](docs/ARCHITECTURE.md)
- [测试记录](docs/TESTING.md)
- [数据来源](docs/DATA_SOURCES.md)
- [变更记录](docs/CHANGELOG.md)

## License

MIT
