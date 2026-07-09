# Architecture

## Pipeline

1. `MainForm` 读取用户设置并创建 `ScanOptions`。
2. `ScanController` 找到 `ZenlessZoneZero` 主窗口。
3. `GameWindow` 前置窗口、点击驱动盘页签、滚到列表顶部。
4. 扫描线程 OCR 仓库数量，默认使用 `OverlapSignaturePage`：按固定 `4 x 9` 页面模型维护当前可视顶部逻辑行，并用已扫逻辑行集合做去重；首屏扫描视觉第 1、2、3 行，中途优先扫描未读的新行，底部补扫视觉第 4 行。
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

`OverlapSignaturePage` 和 `SafeBandViewport` 依赖 `listGridRect` 和 `panelChangeProbeRect`。游戏在列表中部点击视觉第 1 行会自动向上补位，点击视觉第 4 行会自动向下补位，所以中间状态仍不主动点击这两行。重叠签名模式与旧安全带模式的关键差异是：如果一次滚动被游戏结算为两行，下一屏未读的缺口会落到视觉第 2 行；此时它会按逻辑行集合补扫该行，而不是反向上翻恢复。`CalibratedPage` 保留为高级兼容模式，它还依赖 `rowAlignProbeRect`。

逐行滚动使用较小的滚轮 delta 分步推进；每个小 tick 后会轮询行签名，看到一行位移并且该一行匹配稳定后立即接受，不再先等待整块列表达到固定稳定窗口。`SafeBandViewport` 在默认 `allowScrollRecovery=false` 时如果发现一次推进两行会停止并写出 `ROW_SCROLL_STRICT_STOP`。`OverlapSignaturePage` 会接受已验证的前进距离，保持可视顶部单调递增，并依靠下一屏重叠候选补扫漏出的逻辑行。1.0.23 增加 `ScrollAcceptMode=safe|early-one-row`：`safe` 保持原有确认路径；`early-one-row` 在行签名确认只前进一行时提前接受，多行 overshoot 仍阻断且不回滚。1.0.24 实测该策略与面板 early full ROI 接受组合后超过 1.0.20 DXGI fast 基线，因此 fast-mode 默认使用 `early-one-row`，但仍可显式回退 `safe`。

## OCR Strategy

详情面板文字位置是已知的，所以本系统不跑文字检测模型，只跑识别模型。每个 ROI 会按高度缩放到 48px，并合成 batch tensor，一次送入 `PP-OCRv5_mobile_rec_infer.onnx`。

这比全屏 OCR 快很多，也避免扫描游戏内无关文字。

点击新驱动盘前会记录详情多探针签名；截图前必须看到面板/文本探针变化、连续稳定，以及 12 个 OCR ROI 全部可见，避免把上一张详情面板重复入队。GDI 等待变化阶段会先截 `panelChangeProbeRect`，但签名仍按 12 个 ROI 的相对位置采样，避免整块稀疏采样漏掉文字变化；DXGI 在 warmup 后允许 1 帧稳定，但仍保留变化签名、ROI 完整和最低接受时间。未看到变化时只重试点击，不复用旧面板。1.0.32 起，`selection_changed_stable_full_roi` 不再作为直接接受旧面板的路径；滚动后首格、retry/fallback/recover 场景或 selection 变化时间无明确正值时会记录 `PANEL_SELECTION_ONLY_BLOCKED`，并继续走 stale retry。同排相邻格的弱 panel change 和点击后 25ms 内的过早 change 也会记录 `PANEL_WEAK_CHANGE_BLOCKED`，只有后续可靠面板变化或 retry 成功后才入队。

1.0.22 增加 `PanelStabilityMode=panel|text-core|auto`。`panel` 是默认稳定判定；`text-core` 只用 12 个 OCR ROI 的文字核心区计算稳定帧，核心框为左右各裁 6%、上下各裁 18%；`auto` 会在前 12 件 warmup 中同时记录 panel/text-core 稳定耗时，只有 text-core 明显更快且没有 stale/retry 风险时才选择。无论选择哪种模式，接受条件都仍然是“看到变化、12 ROI 全可见、达到最低等待、稳定帧满足”。本机 1.0.22 实测 text-core 没有提速，因此 fast-mode 默认仍回到 `panel`，text-core/auto 作为显式实验保留。

1.0.24 增加 `PanelAcceptMode=safe|adaptive-early-full-roi`。`safe` 保持原面板接受路径；`adaptive-early-full-roi` 只在 warmup 后、非滚动后首格中启用，接受条件仍是“看到详情变化、12 ROI 全可见、达到自适应最低等待”，并且不启用 quick accept。它用 ROI 完整帧作为早接受信号，避免等待整面板动画完全稳定；滚动后首格继续走保守路径。若机器上出现 UI 波动，可显式回退 `--panel-accept-mode safe`。

1.0.25 增加 `PostScrollPanelAcceptMode=safe|adaptive-after-scroll` 和 `--panel-min-accept-floor`，用于显式复测滚动后首格和最低等待下限；1.0.26 用多轮 suite 复核后，DXGI `110ms + adaptive-after-scroll` 是当前最稳的显式提速候选，但平均增益未达到默认升级门槛。GDI 单轮可能更快，但 1.0.26 五轮中出现重复导出，因此不作为推荐后端。

默认 OCR 使用自动 worker 并行、`OcrBatchSize=1`、`OcrQueueCapacity=48` 和 `OcrIntraOpThreads=3`；自动模式最多启用 2 个 worker。实机日志表明 batch 合大后单件耗时会退化，3 worker 虽能缩短收尾但更容易诱发滚动/面板等待波动，所以不作为默认。GUI 中仍可手动调整 worker、batch、队列容量和 IntraOp 线程；多 worker 只影响截图后的识别，不改变点击、等待、滚动逻辑。

Fast OCR 是字段名类 ROI 的可回退快路径。`--ocr-shadow-dataset` 保存经 PP-OCR 清洗后的 ROI 样本，`--ocr-fast-calibrate` 用多轮 shadow 数据生成模板索引：v3 包含 16x16 灰度 aHash 和横向差分 hash，v4 追加纵向差分 hash，v6 会先对 ROI 文字核心做 canonical crop，再生成灰度、横向差分、纵向差分和边缘梯度 hash。字段策略记录 `AssistEnabled`、`MinScore` 和 `MinMargin`。Assist 模式只替换通过字段策略的 `level/mainStat/subStat1..4`，数值字段和 `name` 仍交给 PP-OCR；未启用、未达阈值或不支持的 ROI 必须回退 PP-OCR。1.0.36 起 `name` 即使在模板策略中残留启用也会被运行时强制禁用，避免套装名模板误接受污染槽位。

1.0.31 起 Fast OCR 增加 profile family 路由。`visual_profile.json` 会记录 `ProfileFamilyId`，由 `client + aspect bucket + dpi bucket + quality` 组成。1.0.32 起默认 `--profile-routing strict` 只允许 exact profile 模板参与 assist 导出；`family`、`compatible` 和 `auto` 仍可用于离线评估或显式实验，但不会作为默认 assist 路径。1.0.34 将完成本地三挡分辨率与云绝区零大窗口/普通窗口/全屏验收的 v6 模板内置到 `Data/ocr_fast_templates.json`，覆盖 `local-1280x720-current`、`local-1600x900-current`、`local-1920x1080-current`、`cloud-1592x896-current`、`cloud-1440x808-current` 和 `cloud-1920x1080-current`。`--ocr-fast-calibrate-visual-profiles --feature v6` 会写出 `ProfileFieldPolicies` 与 `FamilyFieldPolicies`，任一字段只有跨轮/跨 profile `false_accepts=0` 并达到接受率门槛才启用。`--ocr-fast-merge-indexes` 可合并多个 v6 canonical index，保留 profile-specific policies，并对 global/family policy 采用更严格阈值。`--fast-mode` 会在索引通过 v3+ 与启用字段校验后启用 fast profile 和 assist，否则回退普通模式。

1.0.18 起，`--fast-mode` 默认启用本轮自适应等待。它只在内存中记录当前扫描的面板变化、稳定帧、ROI 完整和 OCR backlog，不写长期机器 profile。面板接受必须满足“看到变化、稳定、12 个 ROI 全部可见”；OCR backlog 高时通过 bounded queue 和小幅点击前延迟降速。1.0.19 在这个边界内优化采集等待，主要减少 DXGI warmup 后稳定帧和滚动稳定等待，不把未验证的固定短延迟写成默认。1.0.20 增加内部 frame API 和 `captureFrameBackend` 诊断；DXGI raw BGRA 只保留为显式实验路径，默认 DXGI 发布候选仍使用稳定的 bitmap fallback。1.0.21 记录 quick accept 诊断，但默认关闭：实测跳过完整稳定等待会导致重复导出或速度退化。1.0.22 记录字段级稳定诊断，但 text-core/auto 未达默认推荐门槛。1.0.23 记录滚动后首格诊断并加入 `early-one-row`，但默认仍保持 `safe`。1.0.24 在面板接受中加入 `adaptive-early-full-roi`，并把 fast-mode 默认切到 `adaptive-early-full-roi + early-one-row`；最终 DXGI 120 件为 `3.656/s` 且全部 correctness acceptance pass。1.0.26 没有进一步放宽默认安全边界，而是增加 `--capture-stability-suite` 证明候选是否足够稳定。

## Performance Benchmark

命令行 `--scan-benchmark <scan-dir> [baseline-scan-dir]` 会汇总点击间隔、面板等待、滚动、OCR、资源占用和验收风险。1.0.26 起 `--scan-stability-suite` 输出 `recommended_candidate`、`reject_reason` 和 `speed_vs_baseline_percent`，`--capture-stability-suite` 会自动串行跑 GDI/DXGI 候选并汇总；1.0.24 起输出 `roi_complete_frames`、`selected_stable_frames`、`panel_frames_after_warmup` 和 `accept_gate_reason_*_count`，用于判断面板接受卡在变化、ROI 完整、稳定帧还是最低等待；1.0.23 起输出 `same_row_panel_wait_ms`、`post_scroll_first_panel_wait_ms` 和 `post_scroll_first_cell_total_ms`，用于拆分同排普通点击与滚动后首格等待；1.0.22 起输出 `panel_text_stable_ms`、`panel_stable_source_*`、`rarity_probe_ms` 和 `selection_probe_ms`，用于判断字段级稳定是否真的减少等待，以及新增探针成本是否抵消收益；1.0.21 起输出 `quick_accept_count`、`quick_accept_rate_percent`、`panel_frames` 和 `selection_change_ms`，用于评估快速面板接受实验是否真的减少等待且不引入重复；1.0.18 起输出 `adaptive_throttle_ms`、`ocr_backlog_before_enqueue` 和 `adaptive_panel_min_ms`，用于判断本轮自适应是否介入；1.0.16 起输出 `panel_change_ms`、`panel_roi_ms`、`panel_stable_ms`、`after_scroll_extra_ms` 和 `capture_limited`，用于判断瓶颈在采集端还是 OCR 端。1.0.15 起输出 `fast_accepted_per_item`、`fast_rejected_per_item`、`ppocr_roi_per_item` 和 `fast_match_ms_per_item`，用于判断 assist 是否真的减少 PP-OCR ROI。1.0.3 起输出 `overlap_viewport_count`、`overlap_row_scanned_count`、`overlap_scroll_accepted_count` 和 `acceptance.overlap_rows_complete`，用于判断重叠页扫描是否覆盖全部导出。`unsafe_visual_row2_clicks` 仍会排除重叠模式下合理的缺口补扫，继续用于发现非预期的视觉第 2 行点击。
