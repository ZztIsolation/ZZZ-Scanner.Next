# ZZZ Scanner Next

ZZZ Scanner Next 是旧版命令行扫描器的独立重写版，目标是做成可视化、快速、非侵入式的驱动盘导出工具。

## 当前能力

- WinForms GUI：检测窗口、预览详情区、开始/停止扫描、查看实时结果和日志。
- 默认申请管理员权限：`app.manifest` 使用 `requireAdministrator`，程序入口也会尝试自提升。
- 非侵入式扫描：只使用窗口前置、鼠标点击、滚轮和屏幕截图，不读取或注入游戏进程。
- 快速扫描：扫描线程负责点击和截图，多个 OCR worker 异步消费队列。
- 数据驱动：驱动盘名称、词条候选、词条数值、扫描点位都在 `Data/*.json`。
- 重叠签名稳定遍历：默认使用 `OverlapSignaturePage`，以逻辑行集合记录已扫行，首屏扫描 1/2/3 行，中间优先扫描未读的安全行，若滚动偶发推进两行则允许补扫落到视觉第 2 行的缺口。
- 可设置读取范围：支持只读前 N 个、`0=不限制`、按 S/A/B 品质过滤、临时显示调试截图。

## 入口

- 项目文件：`ZZZ-Scanner.Next.csproj`
- 可执行文件：`bin/Release/net8.0-windows/ZZZ-Scanner.Next.exe`
- GUI 主窗体：`Ui/MainForm.cs`
- 扫描主流程：`Scanning/ScanController.cs`
- OCR：`Ocr/PaddleOcrRecognizer.cs`
- wiki 资料清洗：`Cleaning/DriveDiscCleaner.cs`

## 命令行诊断

- 扫描日志基准：
  `ZZZ-Scanner.Next.exe --scan-benchmark <scan-dir> [baseline-scan-dir]`

`--scan-benchmark` 只读取扫描输出目录，不启动 GUI、不申请管理员权限、不操作游戏窗口。传入 baseline 目录时会额外输出关键速度指标的百分比变化。

- Fast OCR 校准：
  `ZZZ-Scanner.Next.exe --ocr-fast-calibrate <shadow-parent> --output <index.json> --feature v6`

`--ocr-fast-calibrate` 读取多轮 `--ocr-shadow-dataset` 输出，生成模板索引和字段级策略。1.0.31 推荐 `--feature v6`：先做文字核心 canonical crop，再生成灰度、横向差分、纵向差分和边缘梯度 hash。少于两轮 shadow 数据时会生成安全禁用的 index，不会自动启用 assist。

- Fast OCR 特征评估：
  `ZZZ-Scanner.Next.exe --ocr-fast-feature-eval <shadow-parent>`

- 多视觉 profile 校准：
  `ZZZ-Scanner.Next.exe --ocr-fast-calibrate-visual-profiles <shadow-parent> --output <index.json> --feature v6`

- 多 index 合并：
  `ZZZ-Scanner.Next.exe --ocr-fast-merge-indexes <output.json> <index1.json> <index2.json> [...]`

1.0.34 内置已验证的本地三挡分辨率与云绝区零大窗口/普通窗口/全屏 Fast OCR v6 模板：`local-1280x720-current`、`local-1600x900-current`、`local-1920x1080-current`、`cloud-1592x896-current`、`cloud-1440x808-current`、`cloud-1920x1080-current`。`--profile-routing strict` 是默认 assist 路由：只有 exact profile 模板会参与导出，未知分辨率或未训练 profile 自动回退 PP-OCR。`family`、`compatible`、`auto` 仍保留为显式实验，不作为默认全局 fallback。`--ocr-fast-merge-indexes` 只合并 v6 canonical index，保留 profile-specific policies，并对 global/family policy 采用更严格的阈值。1.0.35 起网页 WebSocket `scan_req` 可显式传入 `processName`；云绝区零应传 `Zenless Zone Zero Cloud` 并配合 `visualProfileClient=cloud`。1.0.36 起 `name` 不再参与正式 Fast OCR assist，槽位/主词条非法组合会被导出前校验和 benchmark 拦截。

1.0.32 收紧 `selection_changed_stable_full_roi`：滚动后首格、retry/fallback/recover 场景、以及 selection 变化时间没有明确正值时，不允许只凭选中态变化接受详情面板。同排相邻格的弱 panel change 或点击后 25ms 内的过早 panel change 也不会开启接受门，而是记录 `PANEL_WEAK_CHANGE_BLOCKED` 并触发 stale retry。benchmark 输出 `selection_only_accept_count`、`post_scroll_selection_only_blocked_count`、`weak_panel_change_blocked_count` 和 `fast_exact_profile_accept_count`。

- 已验证高速模式：
  `ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode`

`--fast-mode` 会启用 fast profile 和 Fast OCR assist；模板索引不是 v3+ 或没有启用字段时会自动回退普通模式并写日志。

- 截图后端实验：
  `ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --capture-mode dxgi`

`--capture-mode` 支持 `gdi` 和 `dxgi`。默认仍为 GDI；DXGI 初始化失败、取帧超时或尺寸异常时会自动回退 GDI，并在 `scan.log` 记录回退原因。
1.0.20 起内部增加 frame API 和 `captureFrameBackend` 诊断；DXGI raw BGRA 仍是显式实验路径，本机发布候选默认使用已验证的 `bitmap-fallback`。
1.0.21 继续保留 raw/quick accept 诊断，但实测 quick panel accept 与 raw frame 缓存都不能作为默认：前者会重复或变慢，后者会复用旧帧导致重复导出。

- 面板稳定判定实验：
  `ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --capture-mode dxgi --panel-stability-mode text-core`

`--panel-stability-mode` 支持 `panel`、`text-core` 和 `auto`。`text-core` 只把 12 个 OCR ROI 的文字核心区作为稳定探针，不改变“看到变化、稳定、12 ROI 完整”边界。1.0.22 实测 text-core/auto 没有速度收益，因此 fast-mode 默认仍使用 `panel`，text-core/auto 仅保留为显式实验与诊断。

- 滚动接受策略实验：
  `ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --capture-mode dxgi --scroll-accept-mode early-one-row`

`--scroll-accept-mode` 支持 `safe` 和 `early-one-row`。`early-one-row` 在行签名确认当前视口只前进一行时提前接受滚动结果，多行 overshoot 仍会阻断，不做回滚修正。1.0.24 起 fast-mode 默认使用 `early-one-row`；需要保守对照时可显式传入 `--scroll-accept-mode safe`。

- 面板接受策略：
  `ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --capture-mode dxgi --panel-accept-mode adaptive-early-full-roi`

`--panel-accept-mode` 支持 `safe` 和 `adaptive-early-full-roi`。`adaptive-early-full-roi` 只在 warmup 后、非滚动后首格中启用；仍必须看到详情变化、12 ROI 全可见、达到本轮自适应最低等待，并保持 `quick_accept_count=0`。1.0.24 起 fast-mode 默认使用该策略；需要保守对照时可显式传入 `--panel-accept-mode safe`。

- 滚动后首格实验：
  `ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --capture-mode dxgi --post-scroll-panel-accept-mode adaptive-after-scroll`

`--post-scroll-panel-accept-mode` 支持 `safe` 和 `adaptive-after-scroll`。默认仍为 `safe`；实验模式只在滚动后首格尝试使用本轮观测到的 ROI 完整时机提前接受，仍必须看到详情变化、12 ROI 全可见并满足最低等待。

- 面板最低等待实验：
  `ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --capture-mode dxgi --panel-min-accept-floor 110`

`--panel-min-accept-floor` 允许 `90..120`。1.0.25 默认仍为 `120ms`；本机三轮 110ms 全部通过且平均更快，但最低轮未超过 1.0.24，因此它只作为显式复测参数。90ms 曾触发滚动签名一致性保护，不建议使用。

- 场景化面板最低等待实验：
  `ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --capture-mode dxgi --panel-floor-mode scene-adaptive --same-row-panel-min-accept-floor 105 --post-scroll-panel-min-accept-floor 110 --post-scroll-panel-accept-mode adaptive-after-scroll --scroll-tick-delay-ms 50`

`--panel-floor-mode` 支持 `static` 和 `scene-adaptive`。`scene-adaptive` 只让同排普通点击使用更低的最低等待；warmup、滚动后首格、视觉第 2 行补扫和 retry/fallback 场景仍保持保守下限。1.0.27 实测最佳 10 轮候选为上面的 105/110/scroll50 组合，10/10 轮 correctness pass，但 `completed_per_sec_avg=3.624`，没有超过 1.0.26 的最佳稳定候选，因此保留为显式实验。

- 全量扫描滚动冲突自愈：
  `ZZZ-Scanner.Next.exe --scan-once --max-items 0 --fast-mode --capture-mode dxgi --overlap-conflict-mode recover`

`--overlap-conflict-mode` 支持 `strict`、`recheck` 和 `recover`。普通模式默认 `recheck`，`--fast-mode` 默认 `recover`。1.0.28 起滚动后若出现 `scrollRows=1` 但签名估计 `signatureRows=2` 的冲突，会先连续复核列表签名；弱二行证据按一行继续并记录诊断，强二行只有在已扫逻辑行集合证明不会漏中间行时才接受，否则安全停止。

- 稳定性汇总：
  `ZZZ-Scanner.Next.exe --scan-stability-suite "publish 1.0.25\StabilitySuites\dxgi-default-20260629-193815"`

`--scan-stability-suite` 会汇总多轮扫描的速度分布、正确性通过数、失败/重复/ROI/overshoot/fallback/quick accept 总数，适合判断一个参数是否足够稳定。

- 后端稳定性矩阵：
  `ZZZ-Scanner.Next.exe --capture-stability-suite both --max-items 120 --rounds 5`

`--capture-stability-suite` 会自动串行执行 DXGI/GDI 以及 110ms 下限、滚动后首格 adaptive 的候选组合，并把每组扫描目录复制到 `StabilitySuites\capture-*` 下，再输出稳定性汇总。1.0.26 起 `--scan-stability-suite` 同时输出 `recommended_candidate`、`reject_reason` 和 `speed_vs_baseline_percent`。

1.0.27 起可传入 `--suite-profile speed-1.0.27`，固定跑 DXGI 默认、floor110+postscroll、scene-adaptive 105/100 与 scroll 60/50 的候选矩阵。

- 本轮自适应：
  `ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --adaptive-timing`

`--adaptive-timing` 启用本次扫描内的面板状态机自适应和 OCR backlog 限速；`--fast-mode` 默认启用，`--no-adaptive-timing` 可关闭做对照。不会写入长期机器 profile。1.0.19 起 fast-mode 在不放宽旧面板拒绝和 12 ROI 完整检查的前提下，缩短 DXGI warmup 后稳定帧要求，并把逐行滚动改为一行匹配稳定后早接受。

1.0.20 的实机 DXGI fast 验收目录为 `publish 1.0.20\Scans\2026-06-28-15-47-04-018-p75e8-c80c`：`MaxItems=120`、重复导出 0、`IncompleteRoi=0`、关键滚动验收全 pass，`completed_per_sec=3.406`。1.0.24 默认 DXGI fast 目录 `publish 1.0.24\Scans\2026-06-29-18-06-52-713-p1c74-dd74`，120 件全部 acceptance pass，`quick_accept_count=0`，`completed_per_sec=3.656`。1.0.25 默认 DXGI fast 三轮 suite `publish 1.0.25\StabilitySuites\dxgi-default-20260629-193815`，3/3 轮 correctness pass，`completed_per_sec_min=3.576`、`completed_per_sec_avg=3.593`；`--panel-min-accept-floor 110` 三轮 suite `publish 1.0.25\StabilitySuites\dxgi-floor110-20260629-194751`，3/3 轮 pass，`completed_per_sec_avg=3.675`，但最低轮未超过 1.0.24，因此保持显式实验。1.0.26 五轮 suite `publish 1.0.26\StabilitySuites\capture-both-20260629-200949` 中，DXGI `--panel-min-accept-floor 110 --post-scroll-panel-accept-mode adaptive-after-scroll` 5/5 轮 pass，`completed_per_sec_min=3.669`、`completed_per_sec_avg=3.719`，是当前最稳的显式提速候选；GDI 默认 5 轮出现 `export_duplicate_items_sum=2`，拒绝作为推荐后端。1.0.27 的 scene-adaptive 最终 10 轮 suite `publish 1.0.27\StabilitySuites\dxgi-scene105-post110-scroll50-final10-20260629-215153`，10/10 轮 pass，`completed_per_sec_min=3.574`、`avg=3.624`，未达到 3.8/s 或超过 1.0.26 候选，因此不升级默认。

## 重要限制

本版已经把旧版绝对像素点改成了按游戏客户区比例缩放，并给颜色判断加入容差；OCR 输入也会统一缩放到模型高度。因此它比旧版更抗分辨率变化。

但它仍然依赖背包页面整体布局。若游戏 UI 比例、语言或详情面板布局大改，需要维护 `Data/scan_profiles.json` 中的点位和矩形。
