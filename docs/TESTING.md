# Testing

## 2026-07-01 1.0.30 多环境自适配与稳定提速

已执行：

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -o "publish 1.0.30"
dotnet run -c Release -- --scan-benchmark "publish 1.0.29\Scans\2026-07-01-17-45-01-001-p2408-079f"
dotnet run -c Release -- --ocr-fast-eval "Data\ocr_fast_templates.local-1280x720-current.json" "publish 1.0.29\Scans\2026-07-01-17-33-23-050-p467c-6340"
.\publish 1.0.30\ZZZ-Scanner.Next.exe --scan-benchmark "publish 1.0.29\Scans\2026-07-01-17-45-01-001-p2408-079f"
.\publish 1.0.30\ZZZ-Scanner.Next.exe --ocr-fast-eval "Data\ocr_fast_templates.local-1280x720-current.json" "publish 1.0.29\Scans\2026-07-01-17-33-23-050-p467c-6340"
```

结果：

- Release 编译通过，0 warning / 0 error。
- 已发布到 `publish 1.0.30`，旧发布目录未覆盖。
- 新 benchmark 字段可读取旧 1.0.29 scan；旧 v1 `visual_profile.json` 会显示 `profile_id`，新增 detected geometry 字段为空属兼容预期。
- `local-1280x720-current` v5 模板对 1.0.29 shadow 目录离线 eval：`fast_eval.rows=840`、`accepted=733`、`false_accepts=0`。
- 仍需在驱动盘页面打开后执行 1.0.30 的 `--collect-visual-profile`、fast-mode 120 件和全量扫描验收。

## 2026-07-01 1.0.28 全量扫描滚动冲突自愈

已执行：

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -o "publish 1.0.28"
.\publish 1.0.28\ZZZ-Scanner.Next.exe --scan-benchmark "C:\Users\ZZT\AppData\Local\ZZZScannerNext\runtime\1.0.26\Scans\2026-07-01-00-22-11-662-p904c-c955"
.\publish 1.0.28\ZZZ-Scanner.Next.exe --scan-benchmark "C:\Users\ZZT\AppData\Local\ZZZScannerNext\runtime\1.0.27\Scans\2026-06-29-23-29-14-798-p8064-37b4"
.\publish 1.0.28\ZZZ-Scanner.Next.exe --scan-once --max-items 30
.\publish 1.0.28\ZZZ-Scanner.Next.exe --scan-benchmark "publish 1.0.28\Scans\2026-07-01-01-12-25-603-p643c-f748"
npm run test:scanner-bridge
npm run build:pages
```

结果：

- `publish 1.0.28` 已新建，旧 `publish 1.0.27` 未覆盖。
- `dotnet build -c Release` 与 self-contained publish 均为 0 warning / 0 error。
- 已生成 `ZZZ-Scanner.Next-win-x64-1.0.28.zip`，大小 `129864140`，SHA-256 `21cff1df6e080ac0796ebf4a9412d7839040ff91b6740275344bcde1af46aa75`。
- 旧 1.0.26 网页失败目录被识别为全量扫描失败：`overlap_conflict_count=1`、`full_scan_expected=true`、`full_scan_complete=false`、`missing_logical_rows_count=41`、`total_rows=82`。
- 旧 1.0.27 网页失败目录被识别为全量扫描失败：`overlap_conflict_count=1`、`full_scan_expected=true`、`full_scan_complete=false`、`missing_logical_rows_count=73`、`total_rows=78`。
- 普通模式 30 件目录 `publish 1.0.28\Scans\2026-07-01-01-12-25-603-p643c-f748`：`Completed=30`、`Failed=0`、重复导出 0、`IncompleteRoi=0`、`overlap_conflict_count=0`、`overlap_hard_stop_count=0`；`full_scan_expected=false`，因此全量覆盖验收项按预期跳过。
- `zzz_calculator` 的 `npm run test:scanner-bridge` 通过，验证 manifest 版本、hash/size 和 `scan_req.overlapConflictMode="recover"`。
- 实机 `MaxItems=0` 三轮全量验收仍需在驱动盘页面打开后执行；本轮代码与发布包已准备好。

## 2026-06-29 1.0.27 场景化面板下限与滚动 tick 矩阵

已执行：

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -o "publish 1.0.27"
.\publish 1.0.27\ZZZ-Scanner.Next.exe --scan-once --max-items 30
.\publish 1.0.27\ZZZ-Scanner.Next.exe --capture-stability-suite dxgi --suite-profile speed-1.0.27 --max-items 30 --rounds 1
.\publish 1.0.27\ZZZ-Scanner.Next.exe --capture-stability-suite dxgi --suite-profile speed-1.0.27 --max-items 120 --rounds 5
.\publish 1.0.27\ZZZ-Scanner.Next.exe --scan-stability-suite "publish 1.0.27\StabilitySuites\capture-dxgi-20260629-213516\dxgi-default"
.\publish 1.0.27\ZZZ-Scanner.Next.exe --scan-stability-suite "publish 1.0.27\StabilitySuites\capture-dxgi-20260629-213516\dxgi-floor110-postscroll"
.\publish 1.0.27\ZZZ-Scanner.Next.exe --scan-stability-suite "publish 1.0.27\StabilitySuites\capture-dxgi-20260629-213516\dxgi-scene105-post110-scroll60"
.\publish 1.0.27\ZZZ-Scanner.Next.exe --scan-stability-suite "publish 1.0.27\StabilitySuites\capture-dxgi-20260629-213516\dxgi-scene100-post110-scroll60"
.\publish 1.0.27\ZZZ-Scanner.Next.exe --scan-stability-suite "publish 1.0.27\StabilitySuites\capture-dxgi-20260629-213516\dxgi-scene105-post110-scroll50"
.\publish 1.0.27\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --panel-floor-mode scene-adaptive --same-row-panel-min-accept-floor 105 --post-scroll-panel-min-accept-floor 110 --post-scroll-panel-accept-mode adaptive-after-scroll --scroll-tick-delay-ms 50 --max-items 120
.\publish 1.0.27\ZZZ-Scanner.Next.exe --scan-stability-suite "publish 1.0.27\StabilitySuites\dxgi-scene105-post110-scroll50-final10-20260629-215153"
.\publish 1.0.27\ZZZ-Scanner.Next.exe --scan-once --adaptive-timing --ocr-workers 1 --ocr-batch 1 --ocr-intra-op 1 --ocr-queue 4 --max-items 30
```

结果：

- Release 编译通过，0 warnings，0 errors。
- 已发布到 `publish 1.0.27`，未覆盖 `publish 1.0.26`。
- 普通模式 30 件目录 `publish 1.0.27\Scans\2026-06-29-21-30-56-768-p3474-58f7`：失败 0、重复 0、`IncompleteRoi=0`、`quick_accept_count=0`、`row_scroll_overshot_count=0`，全部 acceptance pass。
- 30 件 smoke suite `publish 1.0.27\StabilitySuites\capture-dxgi-20260629-213240`：5 个 speed-1.0.27 候选全部 correctness pass。
- 完整 5 轮 suite `publish 1.0.27\StabilitySuites\capture-dxgi-20260629-213516`，共 5 组、25 轮 120 件扫描；全部失败 0、重复 0、`IncompleteRoi=0`、`quick_accept_sum=0`、`row_scroll_overshot_sum=0`。
- DXGI 默认：5/5 轮 correctness pass，`completed_per_sec_min=3.178`、`p10=3.178`、`avg=3.472`，低于 1.0.26 参考。
- DXGI `--panel-min-accept-floor 110 --post-scroll-panel-accept-mode adaptive-after-scroll`：5/5 轮 pass，`completed_per_sec_min=3.605`、`p10=3.605`、`avg=3.628`，低于 1.0.26 同组合的 5 轮 `3.719/s`。
- DXGI `scene-adaptive same-row105 postscroll110 + scroll60`：5/5 轮 pass，`completed_per_sec_min=3.552`、`avg=3.600`。
- DXGI `scene-adaptive same-row100 postscroll110 + scroll60`：5/5 轮 pass，`completed_per_sec_min=3.575`、`avg=3.625`。
- DXGI `scene-adaptive same-row105 postscroll110 + scroll50`：5/5 轮 pass，`completed_per_sec_min=3.615`、`avg=3.641`，是 5 轮矩阵中的最佳新候选。
- 最佳候选 10 轮 suite `publish 1.0.27\StabilitySuites\dxgi-scene105-post110-scroll50-final10-20260629-215153`：10/10 轮 correctness pass，`completed_per_sec_min=3.574`、`p10=3.589`、`avg=3.624`、`p90=3.685`；未达到 `avg>=3.8`、`p10>=3.75`、最低轮 `>=3.70`。
- 最佳候选代表扫描显示 `floor_wait_limited_count=86`、`same_row_panel_floor_ms_avg=105`、`post_scroll_panel_floor_ms_avg=110`、`scroll_tick_delay_ms_avg=50`，说明新诊断能确认场景化下限确实生效，但总速没有提高。
- 慢 OCR 压力目录 `publish 1.0.27\Scans\2026-06-29-21-59-48-046-p7310-fe07`：`adaptiveThrottleMs_avg=194.167ms`、`adaptiveThrottleMs_max=300ms`、`ocr_backlog_max=5`，失败 0、重复 0、`IncompleteRoi=0`；小队列压力下 `backlog_not_saturated=risk` 符合预期。
- 互斥保护强烟测：先启动 120 件 fast DXGI 扫描，扫描中启动第二个 `--scan-once --max-items 1`，未创建第二个扫描目录；首个目录 `publish 1.0.27\Scans\2026-06-29-22-07-32-729-p1c2c-c8cf` 正常完成并全 pass。

结论：

- 1.0.27 的场景化面板下限、滚动 tick override、speed-1.0.27 suite 和 benchmark 诊断可用，稳定性通过。
- 新候选没有达到 3.8/s，也没有超过 1.0.26 最佳稳定候选；fast-mode 默认不升级到 `scene-adaptive`。
- 后续继续提速应转向降低每帧 capture/frame loop 成本或重新评估更安全的截图后端，而不是继续单纯压最低等待。

## 2026-06-29 1.0.26 后端复核与组合提速矩阵

已执行：

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -o "publish 1.0.26"
.\publish 1.0.26\ZZZ-Scanner.Next.exe --scan-once --max-items 30
.\publish 1.0.26\ZZZ-Scanner.Next.exe --capture-stability-suite dxgi --max-items 5 --rounds 1
.\publish 1.0.26\ZZZ-Scanner.Next.exe --capture-stability-suite both --max-items 120 --rounds 5
.\publish 1.0.26\ZZZ-Scanner.Next.exe --scan-stability-suite "publish 1.0.26\StabilitySuites\capture-both-20260629-200949\dxgi-default"
.\publish 1.0.26\ZZZ-Scanner.Next.exe --scan-stability-suite "publish 1.0.26\StabilitySuites\capture-both-20260629-200949\dxgi-floor110"
.\publish 1.0.26\ZZZ-Scanner.Next.exe --scan-stability-suite "publish 1.0.26\StabilitySuites\capture-both-20260629-200949\dxgi-floor110-postscroll"
.\publish 1.0.26\ZZZ-Scanner.Next.exe --scan-stability-suite "publish 1.0.26\StabilitySuites\capture-both-20260629-200949\gdi-default"
.\publish 1.0.26\ZZZ-Scanner.Next.exe --scan-stability-suite "publish 1.0.26\StabilitySuites\capture-both-20260629-200949\gdi-postscroll"
.\publish 1.0.26\ZZZ-Scanner.Next.exe --scan-once --adaptive-timing --ocr-workers 1 --ocr-batch 1 --ocr-intra-op 1 --ocr-queue 4 --max-items 30
```

结果：

- Release 编译通过，0 warnings，0 errors。
- 已发布到 `publish 1.0.26`，未覆盖 `publish 1.0.25`。
- 普通模式 30 件目录 `publish 1.0.26\Scans\2026-06-29-20-08-20-763-p82f8-8577`：失败 0、重复 0、`IncompleteRoi=0`、`quick_accept_count=0`、滚动验收全 pass。
- `--capture-stability-suite dxgi --max-items 5 --rounds 1` 烟测通过，确认命令能自动生成 suite、复制扫描目录并输出 `recommended_candidate`/`reject_reason`。
- 完整 suite 根目录 `publish 1.0.26\StabilitySuites\capture-both-20260629-200949`，共 5 组、25 轮 120 件扫描。
- DXGI 默认：5/5 轮 correctness pass，`completed_per_sec_min=3.388`、`p10=3.388`、`avg=3.599`、失败 0、重复 0、`IncompleteRoi=0`；`recommended_candidate=false`，拒绝原因为 `p10_below_3_65,avg_gain_below_5_percent`。
- DXGI `--panel-min-accept-floor 110`：5/5 轮 correctness pass，`completed_per_sec_min=3.572`、`p10=3.572`、`avg=3.655`，拒绝原因为 `p10_below_3_65,avg_gain_below_5_percent`。
- DXGI `--panel-min-accept-floor 110 --post-scroll-panel-accept-mode adaptive-after-scroll`：5/5 轮 correctness pass，`completed_per_sec_min=3.669`、`p10=3.669`、`avg=3.719`、`p90=3.784`，`post_scroll_first_panel_wait_ms_avg_avg=165.427ms`；这是本轮最佳稳定组合，但平均增益 `3.504%` 未达到 5%，因此 `recommended_candidate=false`。
- GDI 默认：5 轮中 4 轮 pass、1 轮 fail，`export_duplicate_items_sum=2`，即使 `completed_per_sec_avg=3.671` 也拒绝推荐。
- GDI `--post-scroll-panel-accept-mode adaptive-after-scroll`：5/5 轮 correctness pass，`completed_per_sec_min=3.556`、`p10=3.556`、`avg=3.651`，拒绝原因为 `p10_below_3_65,avg_gain_below_5_percent`。
- 慢 OCR 压力目录 `publish 1.0.26\Scans\2026-06-29-20-30-22-188-p8774-eb9d`：`adaptiveThrottleMs_avg=185ms`、`adaptiveThrottleMs_max=300ms`、`ocr_backlog_max=5`，失败 0、重复 0、`IncompleteRoi=0`；小队列压力下 `backlog_not_saturated=risk` 符合预期。
- 互斥保护烟测：先启动 30 件扫描，再立即启动第二个 `--scan-once --max-items 1`，第二个实例未创建新扫描目录；首个目录 `publish 1.0.26\Scans\2026-06-29-20-31-25-162-p7694-cb77` 正常完成并全 pass。

结论：

- 1.0.26 没有把默认策略继续激进化；五轮矩阵没有任何组合同时满足 correctness、p10 和 5% 平均增益门槛。
- GDI 单轮高速在多轮中暴露重复导出风险，不能推荐为默认后端。
- 最有价值的显式提速参数是 DXGI `--panel-min-accept-floor 110 --post-scroll-panel-accept-mode adaptive-after-scroll`，平均 `3.719/s` 且 5/5 轮全 pass；但它仍只是实验候选，不替代默认安全路径。

## 2026-06-29 1.0.25 稳定性复核与滚动后首格实验

已执行：

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -o "publish 1.0.25"
.\publish 1.0.25\ZZZ-Scanner.Next.exe --scan-once --max-items 30
.\publish 1.0.25\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --max-items 120
.\publish 1.0.25\ZZZ-Scanner.Next.exe --scan-stability-suite "publish 1.0.25\StabilitySuites\dxgi-default-20260629-193815"
.\publish 1.0.25\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode gdi --max-items 120
.\publish 1.0.25\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --post-scroll-panel-accept-mode adaptive-after-scroll --max-items 30
.\publish 1.0.25\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --post-scroll-panel-accept-mode adaptive-after-scroll --max-items 120
.\publish 1.0.25\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --panel-min-accept-floor 110 --max-items 30
.\publish 1.0.25\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --panel-min-accept-floor 110 --max-items 120
.\publish 1.0.25\ZZZ-Scanner.Next.exe --scan-stability-suite "publish 1.0.25\StabilitySuites\dxgi-floor110-20260629-194751"
.\publish 1.0.25\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --panel-min-accept-floor 100 --max-items 30
.\publish 1.0.25\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --panel-min-accept-floor 100 --max-items 120
.\publish 1.0.25\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --panel-min-accept-floor 90 --max-items 30
.\publish 1.0.25\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --panel-min-accept-floor 90 --max-items 120
.\publish 1.0.25\ZZZ-Scanner.Next.exe --scan-once --adaptive-timing --ocr-workers 1 --ocr-batch 1 --ocr-intra-op 1 --ocr-queue 4 --max-items 30
```

结果：

- Release 编译通过，0 warnings，0 errors。
- 已发布到 `publish 1.0.25`，未覆盖 `publish 1.0.24`。
- 普通模式 30 件目录 `publish 1.0.25\Scans\2026-06-29-19-32-02-961-p7c4c-395f`：失败 0、重复 0、`IncompleteRoi=0`、滚动验收全 pass。
- DXGI fast 默认三轮 suite `publish 1.0.25\StabilitySuites\dxgi-default-20260629-193815`：3/3 轮 correctness pass，`completed_per_sec_min=3.576`、`completed_per_sec_avg=3.593`、失败 0、重复 0、`IncompleteRoi=0`、`quick_accept_sum=0`、`row_scroll_overshot_sum=0`。
- DXGI fast 默认单轮最高目录 `publish 1.0.25\Scans\2026-06-29-19-38-15-939-p7598-42ef`：`completed_per_sec=3.625`，`panel_wait_ms_avg=159.063ms`，`post_scroll_first_panel_wait_ms_avg=218.873ms`。
- GDI fast 目录 `publish 1.0.25\Scans\2026-06-29-19-41-11-805-p143c-bdf2`：120 件全部 acceptance pass，`completed_per_sec=3.772`，`panel_wait_ms_avg=141.686ms`。
- DXGI `--post-scroll-panel-accept-mode adaptive-after-scroll` 120 件目录 `publish 1.0.25\Scans\2026-06-29-19-42-42-327-p84ec-3b1f`：120 件全部 acceptance pass，`post_scroll_adaptive_accept_count=9`，`post_scroll_first_panel_wait_ms_avg=172.064ms`，但总速 `completed_per_sec=3.634`。
- DXGI `--panel-min-accept-floor 110` suite `publish 1.0.25\StabilitySuites\dxgi-floor110-20260629-194751`：3/3 轮 correctness pass，`completed_per_sec_min=3.628`、`completed_per_sec_avg=3.675`、最快单轮 `3.704/s`，失败 0、重复 0、`IncompleteRoi=0`。
- DXGI `--panel-min-accept-floor 100` 单轮 120 件目录 `publish 1.0.25\Scans\2026-06-29-19-44-51-751-p3480-4e27`：全部 acceptance pass，`completed_per_sec=3.686`。
- DXGI `--panel-min-accept-floor 90` 30 件通过；120 件目录 `publish 1.0.25\Scans\2026-06-29-19-45-37-847-p5a5c-644f` 触发签名一致性保护停止，错误为 `signatureRows=2`，拒绝作为候选。
- 慢 OCR 压力目录 `publish 1.0.25\Scans\2026-06-29-19-49-35-812-p74b4-8766`：`adaptiveThrottleMs_avg=204.167ms`、`adaptiveThrottleMs_max=300ms`、失败 0、重复 0、`IncompleteRoi=0`；`backlog_not_saturated=risk` 符合小队列压力测试预期。
- 互斥保护烟测：先启动 30 件扫描，再立即启动第二个 `--scan-once --max-items 1`，第二个实例未创建新扫描目录，首个目录 `publish 1.0.25\Scans\2026-06-29-19-50-29-775-p304c-6792` 正常完成并全 pass。

结论：

- 1.0.25 默认 DXGI fast 多轮稳定性达标，最低轮 `3.576/s` 高于 `3.5/s`，但平均速度没有明显超过 1.0.24 单轮 `3.656/s`。
- `--panel-min-accept-floor 110` 是当前最有希望的显式提速参数，三轮平均 `3.675/s`，但最低轮未超过 1.0.24，因此不改默认。
- `--post-scroll-panel-accept-mode adaptive-after-scroll` 能明显降低滚动后首格面板等待，但总速收益不足，保留为实验开关。
- 90ms 下限会触发滚动签名保护，不纳入推荐。正式路径仍保持 quick accept 关闭、旧面板变化检查、12 ROI 完整和严格一行滚动验收。

## 2026-06-29 1.0.24 面板等待降帧

已执行：

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -o "publish 1.0.24"
.\publish 1.0.24\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --panel-accept-mode safe --scroll-accept-mode safe --max-items 120
.\publish 1.0.24\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --panel-accept-mode adaptive-early-full-roi --scroll-accept-mode early-one-row --max-items 30
.\publish 1.0.24\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --panel-accept-mode adaptive-early-full-roi --scroll-accept-mode early-one-row --max-items 120
.\publish 1.0.24\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode gdi --panel-accept-mode adaptive-early-full-roi --scroll-accept-mode early-one-row --max-items 120
.\publish 1.0.24\ZZZ-Scanner.Next.exe --scan-once --adaptive-timing --ocr-workers 1 --ocr-batch 1 --ocr-intra-op 1 --ocr-queue 4 --max-items 30
.\publish 1.0.24\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --max-items 120
```

结果：

- Release 编译通过，0 warnings，0 errors。
- 已发布到 `publish 1.0.24`，未覆盖 `publish 1.0.23`。
- DXGI fast safe/safe 基线目录 `publish 1.0.24\Scans\2026-06-29-17-27-26-521-p11ec-44b0`：120 件全部 acceptance pass，但 `completed_per_sec=3.078`，`scroll_ms_avg=377.364ms`，`panel_wait_ms_avg=200.580ms`。
- DXGI fast 新组合早期 120 件目录 `publish 1.0.24\Scans\2026-06-29-17-39-30-366-p71b0-8414`：120 件全部 acceptance pass，`completed_per_sec=3.482`，但第二轮同配置曾落到 `3.360/s`，说明两帧 ROI 完整策略方差偏大。
- 调整为一帧 ROI 完整后，DXGI fast 新组合目录 `publish 1.0.24\Scans\2026-06-29-18-02-16-210-p3c6c-abec`：`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、`quick_accept_count=0`、`row_scroll_overshot_count=0`、关键 acceptance 全 pass；`completed_per_sec=3.621`，`panel_wait_ms_avg=153.684ms`，`panel_frames_after_warmup_avg=8.361`，`scroll_ms_avg=94.636ms`。
- DXGI fast 默认命令最终目录 `publish 1.0.24\Scans\2026-06-29-18-06-52-713-p1c74-dd74`：未显式传入新策略，仅使用 `--fast-mode --capture-mode dxgi`；`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、`quick_accept_count=0`、`row_scroll_overshot_count=0`、`strict_one_way_scroll=pass`、`overlap_rows_complete=pass`；`completed_per_sec=3.656`，`panel_wait_ms_avg=157.749ms`，`scroll_ms_avg=95.636ms`，`ocr_backlog_max=1`。
- GDI fast 新组合目录 `publish 1.0.24\Scans\2026-06-29-17-46-55-703-p2754-8021`：120 件全部 acceptance pass，`completed_per_sec=3.187`。
- 慢 OCR 压力目录 `publish 1.0.24\Scans\2026-06-29-17-48-12-529-p11ec-33a4`：`adaptiveThrottleMs_avg=194.167ms`、`adaptiveThrottleMs_max=300ms`、失败 0、重复 0、`IncompleteRoi=0`；`backlog_not_saturated=risk` 符合小队列压力测试预期。

结论：

- 1.0.24 达到本轮 `>=3.5/s` 目标，并超过 1.0.20 DXGI fast 的 `3.406/s` 稳定基线。
- `--fast-mode` 默认切到 `PanelAcceptMode=AdaptiveEarlyFullRoi` 与 `ScrollAcceptMode=EarlyOneRow`；仍可显式用 `--panel-accept-mode safe` 或 `--scroll-accept-mode safe` 回退。
- 正式路径仍不启用 quick accept，未放宽旧面板变化检查、12 ROI 完整、重复导出和一行滚动验收。

## 2026-06-29 1.0.23 中等冒险版采集端优化

已执行：

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -o "publish 1.0.23"
.\publish 1.0.23\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --scroll-accept-mode safe --max-items 120
.\publish 1.0.23\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --scroll-accept-mode early-one-row --max-items 30
.\publish 1.0.23\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --scroll-accept-mode early-one-row --max-items 120
.\publish 1.0.23\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode gdi --scroll-accept-mode early-one-row --max-items 120
.\publish 1.0.23\ZZZ-Scanner.Next.exe --scan-once --adaptive-timing --ocr-workers 1 --ocr-batch 1 --ocr-intra-op 1 --ocr-queue 4 --max-items 30
```

结果：

- Release 编译通过，0 warnings，0 errors。
- 已发布到 `publish 1.0.23`，未覆盖 `publish 1.0.22`。
- DXGI fast safe 基线目录 `publish 1.0.23\Scans\2026-06-29-16-38-46-790-paa8-da59`：120 件全部 acceptance pass，`completed_per_sec=3.052`，`scroll_ms_avg=396.364ms`。
- DXGI fast early-one-row 小样本目录 `publish 1.0.23\Scans\2026-06-29-16-40-12-602-p8448-bf6c`：30 件失败 0、重复 0、`IncompleteRoi=0`、`row_scroll_overshot_count=0`。
- DXGI fast early-one-row 120 件目录 `publish 1.0.23\Scans\2026-06-29-16-41-38-252-p828-3b18`：`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、`row_scroll_overshot_count=0`、关键 acceptance 全 pass；`completed_per_sec=3.119`，`scroll_ms_avg=101.636ms`，`after_scroll_click_ms_avg=724.455ms`，`same_row_panel_wait_ms_avg=211.044ms`，`post_scroll_first_panel_wait_ms_avg=219.845ms`。
- GDI fast early-one-row 目录 `publish 1.0.23\Scans\2026-06-29-16-43-08-726-p7210-f397`：120 件全部 acceptance pass，`completed_per_sec=2.810`。
- 慢 OCR 压力目录 `publish 1.0.23\Scans\2026-06-29-16-44-27-731-p10f0-3edf`：`adaptiveThrottleMs_avg=204.167ms`、`adaptiveThrottleMs_max=300ms`、`ocr_backlog_before_enqueue_max=4`，失败 0、重复 0、`IncompleteRoi=0`；`backlog_not_saturated=risk` 符合小队列压力测试预期。

结论：

- `early-one-row` 成功把滚动等待从 safe 的约 `396ms` 压到约 `102ms`，且本轮未出现 overshoot、重复导出或漏 ROI。
- 总体速度仍只有 `3.119/s`，低于 1.0.20 DXGI fast 的 `3.406/s`，因此 fast-mode 默认继续保持 `safe`；`early-one-row` 只作为显式实验开关保留。
- 新增分场景 benchmark 显示剩余慢点主要在同排/滚动后面板等待，下一轮应继续压面板接受链路，而不是继续压滚动签名等待。

## 2026-06-29 1.0.22 字段级稳定判定实验

已执行：

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -o "publish 1.0.22"
.\publish 1.0.22\ZZZ-Scanner.Next.exe --scan-once --max-items 30
.\publish 1.0.22\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --max-items 30
.\publish 1.0.22\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --max-items 120
.\publish 1.0.22\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode gdi --max-items 120
.\publish 1.0.22\ZZZ-Scanner.Next.exe --scan-once --adaptive-timing --ocr-workers 1 --ocr-batch 1 --ocr-intra-op 1 --ocr-queue 4 --max-items 30
```

结果：

- Release 编译通过，0 warnings，0 errors。
- 已发布到 `publish 1.0.22`，未覆盖 `publish 1.0.21`。
- 普通模式 30 件目录 `publish 1.0.22\Scans\2026-06-29-15-40-57-729-p75c0-1e05`：`Completed=30`、`Failed=0`、`IncompleteRoi=0`、重复导出 0。
- DXGI fast auto 实验目录 `publish 1.0.22\Scans\2026-06-29-15-45-58-399-p5510-e445`：120 件全部 acceptance pass，但 `panel_stable_source_text_core_count=0`，`completed_per_sec=3.098`，低于 1.0.21/1.0.20。
- DXGI fast 默认 panel 最终目录 `publish 1.0.22\Scans\2026-06-29-15-53-48-089-p958-c953`：`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、`quick_accept_count=0`、`panel_stable_source_text_core_count=0`、关键 acceptance 全 pass，`completed_per_sec=3.077`。
- GDI fast 目录 `publish 1.0.22\Scans\2026-06-29-15-55-34-816-p7700-9f03`：120 件全部 acceptance pass，`completed_per_sec=2.775`。
- 慢 OCR 压力目录 `publish 1.0.22\Scans\2026-06-29-15-57-00-519-p83c8-c37c`：`adaptiveThrottleMs_avg=167.5ms`、`adaptiveThrottleMs_max=300ms`、`ocr_backlog_max=4`，失败 0、重复 0、`IncompleteRoi=0`；`backlog_not_saturated=risk` 符合小队列压力测试预期。

结论：

- 1.0.22 的字段级稳定判定链路和诊断可用，但本机实测 text-core/auto 未带来速度收益。
- fast-mode 默认回退 `panel`，`text-core/auto` 只作为显式实验选项保留。
- 推荐稳定版本仍应优先参考 `publish 1.0.20`/`1.0.21` 的 DXGI fast 结果；1.0.22 主要价值是新增诊断与安全实验接口。

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

- 从列表中部启动，程序先回顶，首屏只允许点击视觉第 1、2、3 行，其中视觉第 2 行只允许出现在 `visibleTopLogicalRow=1` 且 `logicalRow=2` 的顶部边界。
- 中间状态的 `CELL_CLICK` 必须固定为视觉第 3 行，不得出现视觉第 1、2、4 行；若代码路径试图点击，必须先写 `EDGE_CLICK_BLOCKED` 并停止。
- 每次向下推进允许多个小 `ROW_SCROLL_TICK`，但只能在 `ROW_SCROLL_VERIFY` 确认推进一行后写 `ROW_SCROLL_DONE`。
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

## 2026-06-25 1.0.2 提速验收

基准目录：

- baseline：`publish 1.0.1\Scans\2026-06-25-21-39-06`
- 新版本发布目录：`publish 1.0.2`

发布命令：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o "publish 1.0.2"
```

固定实机测试：

- 使用同一背包位置，`读取上限=120`，品质保持与 baseline 一致，优先测试默认 OCR 参数：`OcrBatchSize=1`、`OCR线程=0` 自动 worker。
- 每轮保留 `scan.log`、`ocr_diagnostics.csv`、`resource.csv`、`export.json`。
- 自动实机测试可直接运行：

```powershell
.\publish 1.0.2\ZZZ-Scanner.Next.exe --scan-once --max-items 120
```

- `--scan-once` 结束后会写出 `scan-once-result.json`；若需要测试 OCR 矩阵，可追加 `--ocr-workers`、`--ocr-batch`、`--ocr-queue`、`--ocr-intra-op`。
- 对每轮结果运行：

```powershell
.\publish 1.0.2\ZZZ-Scanner.Next.exe --scan-benchmark "<new-scan-dir>" "publish 1.0.1\Scans\2026-06-25-21-39-06"
```

通过条件：

- `acceptance.no_incomplete_roi=pass`，`acceptance.no_error_files=pass`。
- `acceptance.export_consistency=pass`；如果扫描被手动取消且没有 `export.json`，只作为中途性能样本，不作为完整验收。
- `acceptance.no_unsafe_visual_row2=pass`；`unsafe_visual_row2_clicks` 必须为 0。
- `ocr_total_ms_per_item_avg` 不得高于 baseline；吞吐提升主要看 `queued_per_sec`、`completed_per_sec` 和 `acceptance.backlog_not_saturated`。
- `fallback_rate_percent` 不异常上升，`Probe fullScan=False` 应成为常态；若 `fullScan=True` 频繁出现，需要回查品质采样点。
- `ROW_SCROLL_VERIFY` 仍必须成功，不能用滚动错行换速度；默认单向严格模式下要求 `row_scroll_overshot_count=0`、`row_scroll_recovery_accepted_count=0`。若出现 `ROW_SCROLL_STRICT_STOP` 或 `ROW_SCROLL_OVERSHOT`，本轮只作为风险样本，不能作为默认参数通过依据。
- `CELL_CLICK` / `CELL_TIMING` 中 `visualRow=2` 只允许出现在顶部边界 `logicalRow=2, visibleTopLogicalRow=1, state=Top`；中途出现即判为重复读取风险。

## 2026-06-26 1.0.3 重叠签名扫描验收

发布目录：

- `publish 1.0.3`

发布命令：

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o "publish 1.0.3"
```

正式实机测试：

```powershell
.\publish 1.0.3\ZZZ-Scanner.Next.exe --scan-once --max-items 120
.\publish 1.0.3\ZZZ-Scanner.Next.exe --scan-benchmark "publish 1.0.3\Scans\2026-06-26-18-53-09" "publish 1.0.2\Scans\2026-06-26-17-52-45"
```

结果摘要：

- `Success=True`，`Visited=120`，`Queued=120`，`Completed=120`，`Failed=0`。
- `current.traversal=overlap-signature-page`。
- `queued_per_sec=2.682`，`completed_per_sec=2.682`，`duration_seconds=44.737`。
- `min_visible_rois=12`，`incomplete_roi=0`，`export_items=120`，`export_matches_completed=True`。
- `overlap_viewport_count=12`，`overlap_row_scanned_count=13`，`overlap_scroll_accepted_count=11`。
- `unsafe_visual_row2_clicks=0`，`row_scroll_overshot_count=0`，`row_scroll_recovery_accepted_count=0`，`row_scroll_strict_stop_count=0`。
- `acceptance.no_incomplete_roi=pass`，`acceptance.no_error_files=pass`，`acceptance.export_consistency=pass`，`acceptance.no_unsafe_visual_row2=pass`，`acceptance.overlap_rows_complete=pass`，`acceptance.strict_one_way_scroll=pass`。

额外压力样本：

- 使用临时目录 `publish 1.0.3-stress` 手动放大 `scrollTickDelta=-60` 和 `scrollTickDelta=-120` 各跑一次 `MaxItems=120`，两次均成功导出 120 条。
- 当前游戏状态下压力样本仍没有复现真实“两行越滚”，日志中的 `OVERLAP_SCROLL_ACCEPTED` 仍主要为 `rowsAdvanced=1`。因此 1.0.3 已验证正常单向路径和重叠页覆盖，但“两行越滚后视觉第 2 行补扫”仍需等实机偶发场景再次验证。

## 2026-06-27 1.0.14 Fast OCR 辅助识别验证

离线命令：

```powershell
.\publish 1.0.14\ZZZ-Scanner.Next.exe --ocr-shadow-analyze "<shadow-scan-dir>" --build-fast-index "<index.json>"
.\publish 1.0.14\ZZZ-Scanner.Next.exe --ocr-fast-eval "<index.json>" "<shadow-scan-dir>"
.\publish 1.0.14\ZZZ-Scanner.Next.exe --ocr-fast-cross-validate "<shadow-parent>"
```

实机命令：

```powershell
.\publish 1.0.14\ZZZ-Scanner.Next.exe --scan-once --max-items 30
.\publish 1.0.14\ZZZ-Scanner.Next.exe --scan-once --max-items 30 --ocr-fast-shadow --ocr-fast-index "<index.json>"
.\publish 1.0.14\ZZZ-Scanner.Next.exe --scan-once --max-items 120 --ocr-fast-assist --ocr-fast-index "<index.json>"
```

验收重点：

- `--ocr-fast-eval` 必须写出 `ocr_fast_eval.csv`，并逐字段输出 `false_accepts`、`accept_rate`、`match_rate`。
- `--ocr-fast-assist` 必须写出 `ocr_fast_assist.csv`；`source=fast` 的 ROI 直接进入清洗，`source=ppocr/fallback/rejected` 必须回退 PP-OCR。
- `ocr_diagnostics.csv` 中 `ppocr_roi_count` 应低于原固定 12 ROI；`fast_accepted_count + ppocr_roi_count` 应能解释每个盘的 12 个字段来源。
- 正式验收仍以 `scan-benchmark` 的 `acceptance.no_incomplete_roi`、`acceptance.no_error_files`、`acceptance.export_consistency`、`acceptance.no_export_duplicates` 为准。

## 2026-06-27 1.0.15 Fast OCR 自动校准与 v3 特征

离线命令：

```powershell
.\publish 1.0.15\ZZZ-Scanner.Next.exe --scan-once --max-items 120 --ocr-shadow-dataset
.\publish 1.0.15\ZZZ-Scanner.Next.exe --scan-once --max-items 120 --ocr-shadow-dataset
.\publish 1.0.15\ZZZ-Scanner.Next.exe --scan-once --max-items 120 --ocr-shadow-dataset
.\publish 1.0.15\ZZZ-Scanner.Next.exe --ocr-fast-calibrate "<shadow-parent>" --output "<index.json>"
.\publish 1.0.15\ZZZ-Scanner.Next.exe --ocr-fast-cross-validate "<shadow-parent>"
.\publish 1.0.15\ZZZ-Scanner.Next.exe --ocr-fast-eval "<index.json>" "<shadow-parent>"
```

实机命令：

```powershell
.\publish 1.0.15\ZZZ-Scanner.Next.exe --scan-once --max-items 30
.\publish 1.0.15\ZZZ-Scanner.Next.exe --scan-once --max-items 30 --ocr-fast-shadow --ocr-fast-index "<index.json>"
.\publish 1.0.15\ZZZ-Scanner.Next.exe --scan-once --max-items 120 --ocr-fast-assist --ocr-fast-index "<index.json>"
.\publish 1.0.15\ZZZ-Scanner.Next.exe --scan-benchmark "<assist-scan-dir>" "<default-scan-dir>"
```

验收重点：

- `ocr_fast_templates.json` 新索引应为 `Version=3`、`Feature=ahash-dhash-16x16-v3`；旧 v1/v2 index 必须仍可被 `--ocr-fast-eval` 读取。
- `--ocr-fast-calibrate` 必须输出 `ocr_fast_eval.csv`、`ocr_fast_confusion.csv`、`ocr_fast_calibration.csv`；少于两轮 shadow 数据时必须保持所有字段 `AssistEnabled=false`。
- 字段进入 assist 的门槛是 cross-run `false_accepts=0`；`name` 还需要 `accept_rate>=0.95`，否则保持禁用。
- `--scan-benchmark` 必须输出 `fast_accepted_per_item`、`fast_rejected_per_item`、`ppocr_roi_per_item`、`fast_match_ms_per_item`。
- 正式推荐 assist 前，`MaxItems=120` 需满足 `Failed=0`、`IncompleteRoi=0`、重复导出 0、导出数量与日志一致，并且 `ppocr_roi_per_item` 平均降到 5-6 左右或 OCR 单件耗时下降至少 30%。

已完成的工程烟测：

- `dotnet build -c Release`：0 warning / 0 error。
- 用 `publish 1.0.12\Scans\2026-06-27-19-36-56` 的 5 件 shadow 样本构建 v3 index：`templates=23`、`fields=7`、`labels=18`。
- 单轮 shadow 校准会生成安全禁用 index，所有字段 `calibrated_assist=false`，符合“少于两轮不启用”的规则。
- 复制同一小样本做双折烟测时，`--ocr-fast-cross-validate` 与 `--ocr-fast-calibrate` 均能完成并写出三类 CSV；该结果只证明命令链路可用，不作为真实准确率依据。

真实 120 件验证：

- 采集 3 轮 `MaxItems=120 --ocr-shadow-dataset`：`2026-06-27-21-42-55`、`2026-06-27-21-44-25`、`2026-06-27-21-45-53`，三轮均 `Completed=120`、`Failed=0`。
- 校准索引：`Version=3`、`Feature=ahash-dhash-16x16-v3`；启用 `level/mainStat/subStat1/subStat2/subStat3`，`name` 因 `accept_rate=0.275` 禁用，`subStat4` 因 `accept_rate=0.008333` 禁用。
- assist 实机验证目录：`publish 1.0.15\Scans\2026-06-27-21-53-23`，对比 baseline `2026-06-27-21-45-53`。
- assist 结果：`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、`export_matches_completed=True`。
- 性能结果：`completed_per_sec` 从 `1.944` 升到 `2.828`；`ocr_total_ms_per_item_avg` 从 `926.617ms` 降到 `271.143ms`，下降 `70.738%`；`ppocr_roi_per_item_avg` 从 `12.000` 降到 `7.000`；`fast_accepted_per_item_avg=5.000`；OCR backlog max 从 `32` 降到 `2`。
- 普通默认验证目录：`publish 1.0.15\Scans\2026-06-27-21-55-11`，`MaxItems=30`，`Completed=30`、`Failed=0`、`ppocr_roi_per_item_avg=12.000`、关键 acceptance 均 pass。

## 2026-06-27 1.0.16 Fast Mode 与采集端拆解

离线命令：

```powershell
.\publish 1.0.16\ZZZ-Scanner.Next.exe --ocr-fast-calibrate "tmp-fast-1.0.15\real-shadow-120" --output "tmp-fast-1.0.16\calibrated\ocr_fast_templates.json"
.\publish 1.0.16\ZZZ-Scanner.Next.exe --ocr-fast-cross-validate "tmp-fast-1.0.15\real-shadow-120"
.\publish 1.0.16\ZZZ-Scanner.Next.exe --ocr-fast-feature-eval "tmp-fast-1.0.15\real-shadow-120"
.\publish 1.0.16\ZZZ-Scanner.Next.exe --ocr-fast-calibrate "tmp-fast-1.0.15\real-shadow-120" --output "tmp-fast-1.0.16\calibrated-v4\ocr_fast_templates.json" --feature v4
```

实机命令：

```powershell
.\publish 1.0.16\ZZZ-Scanner.Next.exe --scan-once --max-items 30
.\publish 1.0.16\ZZZ-Scanner.Next.exe --scan-once --max-items 30 --fast-mode
.\publish 1.0.16\ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode
.\publish 1.0.16\ZZZ-Scanner.Next.exe --scan-benchmark "publish 1.0.16\Scans\2026-06-27-23-02-38" "publish 1.0.15\Scans\2026-06-27-21-53-23"
```

已完成验证：

- `dotnet build -c Release`：0 warning / 0 error。
- `publish 1.0.16` 已新建并包含 `ZZZ背包驱动盘-16比9-fast`，旧 `publish 1.0.15` 未覆盖。
- 普通默认验证目录：`publish 1.0.16\Scans\2026-06-27-22-43-27`，`MaxItems=30`，`Completed=30`、`Failed=0`、`IncompleteRoi=0`、重复导出 0。
- v3 fast profile 验证目录：`publish 1.0.16\Scans\2026-06-27-22-45-38`，`MaxItems=120 --fast-mode`，`Completed=120`、`Failed=0`、关键 acceptance 全 pass，`completed_per_sec=3.133`。
- v4 feature eval：`name` 为 `false_accepts=0`、`accept_rate=0.875`，低于 0.95，继续禁用；`subStat4` 为 `false_accepts=0`、`accept_rate=1.000`，允许进入 assist。
- 最终 v4 fast 验证目录：`publish 1.0.16\Scans\2026-06-27-23-02-38`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、`strict_one_way_scroll=pass`、`overlap_rows_complete=pass`。
- 最终性能：`completed_per_sec=3.144`，相对 1.0.15 assist `2.828` 提升 `11.155%`；`ppocr_roi_per_item_avg=6.000`，`ocr_total_ms_per_item_avg=102.069ms`，`ocr_backlog_max=1`。
- 未达到 `3.3/s` 或 `+15%` 的目标；`capture_limited=true`，后续提速应优先做截图/面板探针优化，而不是继续压 OCR。

## 2026-06-28 1.0.17 DXGI 截图后端与采集端诊断

构建与离线命令：

```powershell
dotnet build -c Release
.\publish 1.0.17\ZZZ-Scanner.Next.exe --ocr-fast-cross-validate "tmp-fast-1.0.15\real-shadow-120"
.\publish 1.0.17\ZZZ-Scanner.Next.exe --ocr-fast-feature-eval "tmp-fast-1.0.15\real-shadow-120"
```

实机命令：

```powershell
.\publish 1.0.17\ZZZ-Scanner.Next.exe --scan-once --max-items 30
.\publish 1.0.17\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode gdi --max-items 120
.\publish 1.0.17\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --max-items 30
.\publish 1.0.17\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --max-items 120
.\publish 1.0.17\ZZZ-Scanner.Next.exe --scan-benchmark "publish 1.0.17\Scans\2026-06-28-01-28-45" "publish 1.0.16\Scans\2026-06-27-23-02-38"
```

已完成验证：

- `dotnet build -c Release`：0 warning / 0 error。
- `publish 1.0.17` 已新建；旧 `publish 1.0.16` 未覆盖。
- Fast OCR 离线复核：v4 `name` 为 `false_accepts=0`、`accept_rate=0.875`，继续禁用；`subStat4` 为 `false_accepts=0`、`accept_rate=1.000`，继续允许 assist。
- 普通默认验证目录：`publish 1.0.17\Scans\2026-06-28-01-16-36`，`MaxItems=30`，`Completed=30`、`Failed=0`、`IncompleteRoi=0`、重复导出 0。
- GDI fast 验证目录：`publish 1.0.17\Scans\2026-06-28-01-17-31`，`MaxItems=120 --fast-mode --capture-mode gdi`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、关键 acceptance 全 pass。
- GDI fast 性能：`completed_per_sec=3.320`，相对 1.0.16 的 `3.144` 提升 `5.617%`；`after_scroll_click_ms_avg=811.545`，`scroll_ms_avg=424.364`，`capture_ms_avg=57.915`。
- DXGI 30 件验证目录：`publish 1.0.17\Scans\2026-06-28-01-24-23`，`Completed=30`、`Failed=0`、`IncompleteRoi=0`、重复导出 0，且未发生 GDI fallback。
- DXGI 最终 120 件验证目录：`publish 1.0.17\Scans\2026-06-28-01-28-45`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、`strict_one_way_scroll=pass`、`overlap_rows_complete=pass`，且未发生 GDI fallback。
- DXGI 最终性能：`completed_per_sec=3.467`，相对 1.0.16 提升 `10.286%`；`capture_cell_timing_per_sec=3.483`，`panel_wait_ms_avg=158.184`，`after_scroll_click_ms_avg=771.455`，`scroll_ms_avg=430.909`。
- 采集端分解：`capture_ms_avg=21.177`、`frame_loop_ms_avg=23.492`、`scroll_list_stable_ms_avg=349.227`、`row_signature_ms_avg=11.082`；OCR 仍健康，`ocr_total_ms_per_item_avg=96.177`、`ocr_backlog_max=1`、`ppocr_roi_per_item_avg=6.000`。
- 一轮更激进的 DXGI 120 件扫描目录 `publish 1.0.17\Scans\2026-06-28-01-25-25` 达到 `completed_per_sec=4.213`，但出现重复导出，`acceptance.no_export_duplicates=fail`，因此不作为验收结果。最终版本给 DXGI 面板接受增加安全下限后重复消失。
- 本轮未达到 `completed_per_sec>=3.6` 或 `+15%` 接受目标；DXGI 保持显式实验选项，不默认绑定到 `--fast-mode`。

## 2026-06-28 1.0.18 本轮自适应面板状态机与 OCR 背压

构建与发布命令：

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -o "publish 1.0.18"
```

实机命令：

```powershell
.\publish 1.0.18\ZZZ-Scanner.Next.exe --scan-once --max-items 30
.\publish 1.0.18\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode gdi --max-items 120
.\publish 1.0.18\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --max-items 30
.\publish 1.0.18\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --max-items 120
.\publish 1.0.18\ZZZ-Scanner.Next.exe --scan-once --adaptive-timing --capture-mode gdi --max-items 30 --ocr-workers 1 --ocr-batch 1 --ocr-intra-op 1 --ocr-queue 4
.\publish 1.0.18\ZZZ-Scanner.Next.exe --scan-benchmark "publish 1.0.18\Scans\2026-06-28-02-24-55" "publish 1.0.17\Scans\2026-06-28-01-28-45"
```

已完成验证：

- `dotnet build -c Release`：0 warning / 0 error。
- `publish 1.0.18` 已新建；旧 `publish 1.0.17` 和更早版本未覆盖。
- 普通默认验证目录：`publish 1.0.18\Scans\2026-06-28-02-21-18`，`MaxItems=30`，`Completed=30`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、关键 acceptance 全 pass；`completed_per_sec=1.823`。
- GDI fast 验证目录：`publish 1.0.18\Scans\2026-06-28-02-22-28`，`MaxItems=120 --fast-mode --capture-mode gdi`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、关键 acceptance 全 pass；`completed_per_sec=3.014`，低于 1.0.17 GDI 的 `3.320`。
- DXGI 30 件验证目录：`publish 1.0.18\Scans\2026-06-28-02-23-59`，`Completed=30`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、关键 acceptance 全 pass，且未发生 GDI fallback；`completed_per_sec=2.587`。
- DXGI 120 件验证目录：`publish 1.0.18\Scans\2026-06-28-02-24-55`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、`strict_one_way_scroll=pass`、`overlap_rows_complete=pass`，且未发生 GDI fallback。
- DXGI 120 件性能：`completed_per_sec=3.146`，低于 1.0.17 DXGI baseline `3.467`；`panel_wait_ms_avg=195.508`、`capture_ms_avg=26.088`、`ocr_backlog_max=1`、`adaptive_throttle_ms_avg=0`、`ocr_backlog_before_enqueue_avg=0`。
- 慢 OCR/小队列背压验证目录：`publish 1.0.18\Scans\2026-06-28-02-30-40`，命令追加 `--adaptive-timing --ocr-workers 1 --ocr-batch 1 --ocr-intra-op 1 --ocr-queue 4`，`Completed=30`、`Failed=0`、`IncompleteRoi=0`、重复导出 0。
- 背压验证结果：`ocr_backlog_before_enqueue_max=4`、`ocr_backlog_max=5`，日志出现 `ADAPTIVE_OCR_THROTTLE`，采集前延迟从 `25ms` 逐步升到 `300ms`；`adaptive_throttle_ms_avg=204.167`、`adaptive_throttle_ms_max=300`。在这个刻意压小队列的场景下 `backlog_not_saturated=risk` 属于预期压力信号，但扫描仍未崩溃、未漏 ROI、未重复导出。
- 1.0.18 是稳定性与跨设备适配性加固版：面板接受从“时间到/可读”收紧为“看到变化 + 至少稳定一帧或 DXGI 两帧 + 12 个 ROI 全部可见”，且未看到变化时不再 fallback 复用旧面板。这个安全边界会牺牲一部分极限速度，但能降低旧面板重复和慢机器抢跑风险。

## 2026-06-28 1.0.19 稳定边界内的采集端提速

构建与发布命令：

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -o "publish 1.0.19"
```

实机命令：

```powershell
.\publish 1.0.19\ZZZ-Scanner.Next.exe --scan-once --max-items 30
.\publish 1.0.19\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode gdi --max-items 120
.\publish 1.0.19\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --max-items 120
.\publish 1.0.19\ZZZ-Scanner.Next.exe --scan-benchmark "publish 1.0.19\Scans\2026-06-28-03-02-41" "publish 1.0.18\Scans\2026-06-28-02-24-55"
```

已完成验证：

- `dotnet build -c Release`：0 warning / 0 error。
- `publish 1.0.19` 已新建；旧 `publish 1.0.18` 和更早版本未覆盖。
- 普通默认验证目录：`publish 1.0.19\Scans\2026-06-28-03-04-15`，`MaxItems=30`，`Completed=30`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、关键 acceptance 全 pass；`completed_per_sec=1.821`。
- GDI fast 验证目录：`publish 1.0.19\Scans\2026-06-28-03-05-08`，`MaxItems=120 --fast-mode --capture-mode gdi`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、关键 acceptance 全 pass；`completed_per_sec=3.054`，相对 1.0.18 GDI `3.014` 小幅提升。
- DXGI fast 最终验证目录：`publish 1.0.19\Scans\2026-06-28-03-02-41`，`MaxItems=120 --fast-mode --capture-mode dxgi`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、`strict_one_way_scroll=pass`、`overlap_rows_complete=pass`，且未发生 GDI fallback。
- DXGI fast 最终性能：`completed_per_sec=3.410`，相对 1.0.18 DXGI `3.146` 提升约 `8.4%`；`panel_wait_ms_avg=162.678`、`scroll_ms_avg=339.182`、`after_scroll_click_ms_avg=686.455`、`ocr_backlog_max=1`。
- 对比 1.0.17 DXGI baseline `3.467/s`，1.0.19 仍略慢；原因是继续保留 1.0.18 的旧面板拒绝、12 ROI 完整和 OCR 背压策略。1.0.17 中曾出现 `4.213/s` 但重复导出的激进路径仍不可接受。
- 本轮尝试过两个回退实验：DXGI 取消 probe-only 后 30 件速度降到 `2.684/s`；warmup 后把固定 settle 从 30ms 降到 10ms 后 30 件速度降到 `2.758/s`。两者均未进入最终发布候选。

## 2026-06-28 1.0.20 DXGI Raw Frame 实验与并发目录修复

构建与发布命令：

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -o "publish 1.0.20"
```

实机命令：

```powershell
.\publish 1.0.20\ZZZ-Scanner.Next.exe --scan-once --max-items 30
.\publish 1.0.20\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode gdi --max-items 120
.\publish 1.0.20\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --max-items 120
.\publish 1.0.20\ZZZ-Scanner.Next.exe --scan-once --capture-mode dxgi --adaptive-timing --low-speed-ocr --ocr-workers 1 --ocr-batch 1 --ocr-intra-op 1 --ocr-queue 4 --max-items 30
.\publish 1.0.20\ZZZ-Scanner.Next.exe --scan-benchmark "publish 1.0.20\Scans\2026-06-28-15-47-04-018-p75e8-c80c" "publish 1.0.19\Scans\2026-06-28-03-02-41"
```

已完成验证：

- `dotnet build -c Release`：0 warning / 0 error。
- `publish 1.0.20` 已新建；旧 `publish 1.0.19` 和更早版本未覆盖。
- 根目录 `publish 1.0.20\scan-once-result.json` 不存在；`scan-once-result.json` 只写入各自扫描目录。扫描目录名已包含毫秒、process id 和短随机后缀，例如 `2026-06-28-15-47-04-018-p75e8-c80c`。
- 普通默认验证目录：`publish 1.0.20\Scans\2026-06-28-15-57-06-340-p7444-74dc`，`MaxItems=30`，`Completed=30`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、关键 acceptance 全 pass；`completed_per_sec=2.249`。
- GDI fast 验证目录：`publish 1.0.20\Scans\2026-06-28-15-48-51-387-p4d10-caf9`，`MaxItems=120 --fast-mode --capture-mode gdi`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、关键 acceptance 全 pass；`completed_per_sec=3.081`，相对 1.0.19 GDI `3.054/s` 小幅提升。
- DXGI fast 最终验证目录：`publish 1.0.20\Scans\2026-06-28-15-47-04-018-p75e8-c80c`，`MaxItems=120 --fast-mode --capture-mode dxgi`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、`strict_one_way_scroll=pass`、`overlap_rows_complete=pass`。
- DXGI fast 最终性能：`completed_per_sec=3.406`，基本持平 1.0.19 DXGI `3.410/s`；`frame_to_bitmap_ms_avg=0.002`、`bitmap_created_count_avg=1.000`、`ocr_total_ms_per_item_avg=86.273`、`ocr_backlog_max=1`。
- DXGI raw BGRA 路径已实现但未作为默认启用。本机 raw frame 验证曾触发 Desktop Duplication `ReleaseFrame/Unmap` 与 access lost 类异常，因此 1.0.20 默认 DXGI 记录为 `captureFrameBackend=bitmap-fallback`；raw 路径仅保留给后续显式实验。
- 慢 OCR 压力验证目录：`publish 1.0.20\Scans\2026-06-28-15-54-58-745-p67bc-f7a6`，命令关闭 Fast OCR 并使用 `--low-speed-ocr --ocr-workers 1 --ocr-batch 1 --ocr-intra-op 1 --ocr-queue 4`。结果 `Completed=30`、`Failed=0`、`IncompleteRoi=0`、重复导出 0。
- 背压验证结果：`ocr_backlog_before_enqueue_max=5`、`ocr_backlog_max=6`，`adaptiveThrottleMs_avg=205ms`、`adaptiveThrottleMs_max=300ms`，采集自动降速；`backlog_not_saturated=risk` 是刻意压小队列下的压力信号，正确性验收仍通过。

## 2026-06-28 1.0.21 采集捷径验证与安全收口

构建与发布命令：

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -o "publish 1.0.21"
```

实机命令：

```powershell
.\publish 1.0.21\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --max-items 30
.\publish 1.0.21\ZZZ-Scanner.Next.exe --scan-once --fast-mode --capture-mode dxgi --max-items 120
.\publish 1.0.21\ZZZ-Scanner.Next.exe --scan-benchmark "publish 1.0.21\Scans\2026-06-28-20-16-04-105-p7a74-96e8" "publish 1.0.20\Scans\2026-06-28-15-47-04-018-p75e8-c80c"
```

已完成验证：

- `dotnet build -c Release`：0 warning / 0 error。
- `publish 1.0.21` 已新建；旧 `publish 1.0.20` 和更早版本未覆盖。
- 30 件 smoke 目录：`publish 1.0.21\Scans\2026-06-28-20-10-44-479-p515c-4cfc`，`Completed=30`、`Failed=0`、`IncompleteRoi=0`、重复导出 0，关键 acceptance 全 pass。
- 最终 120 件验收目录：`publish 1.0.21\Scans\2026-06-28-20-16-04-105-p7a74-96e8`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0，关键 acceptance 全 pass。
- 最终 120 件性能：`completed_per_sec=3.221`，低于 1.0.20 DXGI fast `3.406/s`；`panel_wait_ms_avg=187.946`、`scroll_ms_avg=244.727`、`ocr_total_ms_per_item_avg=94.097`、`quick_accept_count=0`。
- 验收日志记录 `captureFrameBackend=bitmap-fallback`，benchmark 记录 `quick_accept_count=0`，确认不安全实验未作为默认路径启用。

失败实验记录：

- 激进 quick panel accept：目录 `publish 1.0.21\Scans\2026-06-28-19-36-55-111-p6974-a56c`，`completed_per_sec=3.549`，但 `export_duplicate_groups=13`、`export_duplicate_items=26`，拒绝作为默认。
- 保守 quick panel accept：目录 `publish 1.0.21\Scans\2026-06-28-19-49-27-448-p54f4-659e`，重复为 0，但 `completed_per_sec=2.973`，低于 1.0.20 DXGI fast `3.406/s`，拒绝作为默认。
- DXGI raw frame 缓存复用：目录 `publish 1.0.21\Scans\2026-06-28-20-02-06-763-p3e48-ebeb`，30 件即出现重复导出 2 件，已撤回。
- 单点品质采样替代 73x73 小截图：目录 `publish 1.0.21\Scans\2026-06-28-20-08-46-723-p84c8-c519`，验收通过但速度退化，已撤回。

结论：

- 1.0.21 不作为速度提升发布，作为采集端诊断和安全边界收口发布。
- 下一波提速应优先做字段级稳定判定，避免用旧帧复用或跳过稳定帧来换速度。
