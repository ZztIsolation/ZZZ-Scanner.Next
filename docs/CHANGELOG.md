# Changelog

## Unreleased

- Scanner 1.0.43 修复整轮首件可能把旧详情面板当成目标盘的问题。首件现在固定执行“目标格、邻格稳定、返回目标格”的验证回环，验收帧自身必须持续与邻格基线强不同；瞬时点击动画不再被锁存为有效切换。新增 `FIRST_CELL_BASELINE_CAPTURED`、`FIRST_CELL_REFRESH_REQUIRED` 和 `FIRST_CELL_REFRESH_READY` 诊断，协议 v4 和流式字段保持不变。
- 每轮导出后统一记录 `SCAN_TERMINAL` 最终计数、partial、终止码和导出文件。Benchmark 优先使用该终态，兼容 CLI sidecar 和旧进度日志，并可读取 `export.partial.json`；`non_level_15_stop` 在保留 partial 事实的同时按有效全量正常结束统计。
- 发布脚本对已经冻结的 Helper 1.3.1 固定其原始 `SourceRevisionId`，避免后续仅修改 Scanner 提交时 .NET 自动写入新的仓库 SHA 并改变 Helper NativeAOT 字节；候选 Helper 继续要求与既有 1.3.1 大小和 SHA-256 完全一致。
- Scanner 1.0.43 修复 1.0.42 预发布包混入旧版 app-local VC runtime 后在 PP-OCRv5 初始化阶段触发 `0xC0000005` 的发布问题。发布脚本现在只接受同一受控 layout 中不低于 14.44.35211 的运行库，Release 禁止 System32 fallback，并在 FDD 与自包含最终目录内各执行一次真实 OCR runtime smoke 后才允许打包。
- Scanner 1.0.42 将详情面板拆为 4 个必需核心 ROI 和 0-4 组连续副词条 ROI；普通四词条盘继续使用单帧快速路径，较短的合法尾部需稳定确认，半对缺失和中间断层会被拒绝。低等级 S 盘不再因固定 12/12 ROI 门槛失败。
- 官方网页与 WinForms 扫描入口固定为 S 级。保留“遇到非 15 级停止”：触发盘在加入结果前停止，已经完成的盘写入 `export.partial.json`；异常和主动取消也使用不可取消的收尾写入部分文件。
- Scanner WebSocket 新增 `stream-items-v1`：逐件 `scan_item` 保持不变，终态只发送摘要和 `itemCount`，旧客户端仍可获得完整数组；线上的 WebSocket 消息改为紧凑 JSON，本地导出继续格式化。
- Helper 1.3.1 将旧式 Scanner 单消息兼容上限提高到 8 MiB，区分 `scanner_message_too_large` 与 `scanner_transport_failed`，消息泵、进程和停止路径共享唯一终态门禁，避免后续重复追加进程退出或停止超时。
- Scanner 1.0.41 为所有 DXGI/GDI 截图增加统一协调器，串行保护帧获取、捕获源切换和释放；主扫描与输入守卫使用阻塞高优先级入口，DXGI 到 GDI 的降级也在同一临界区内原子完成。
- 列表置顶不再用会受动画干扰的整页图像签名判断。Scanner 直接探测滚动条顶部滑块，初始已在顶部时不滚动；否则每 4 个滚轮刻度复检一次，只有达到最大刻度仍无法确认时才使用显式顶部点击，避免在第一页重复执行 180 次向上滚动。
- Helper 1.3.0 同时监督 Scanner WebSocket 与进程 PID，进程退出会先返回唯一的 `scanner_process_exited` 终态再清理消息泵；扫描活动状态在启动准备前原子置位，Scanner 已不存在时的停止请求也不再静默丢弃。NativeAOT 错误回执改用已注册的 `HelperErrorMessage`，修复“日志已记录退出但网页收不到终态”的序列化失败。
- Scanner WebSocket 进度改为同步转发，取消后不再残留 `Progress<T>` 线程池回调导致未处理的 `TaskCanceledException`。Helper 下载流在移动 `.download` 文件前显式释放，避免更新包已下载完成却因文件锁安装失败。
- Scanner 1.0.41 将仓库预检改为“`驱动仓库` 标题语义 + 灰度网格/详情布局”双重门禁，彻底移除青色拆解按钮和首行 S/A/B 品质颜色对页面身份的影响；标题与结构需连续稳定两帧，仓库数量还需在最多三个独立画面中取得两次一致结果，全部完成前不会点击或滚动。
- 删除扫描期间持续截图的后台仓库监控。点击、滚轮和拖拽前的输入守卫先比较静态标题/计数区域；快速签名不匹配时才执行“标题 OCR + 网格/详情结构”强确认，连续强确认失败后返回 `warehouse_context_lost`。详情面板、选中和滚动动画不再参与退出判断，错误标题也改为“无法确认驱动盘仓库界面”。
- 新增脱敏 `warehouseHeaderDetected/headerScore/gridStructureScore/layoutScore/countConsensusFrames` 诊断；旧版 `anchorScore/gridScore/color_profile_unsupported` 仍由网页和服务端接受，Helper 协议不因本改动升级。
- 新增 `neutral/highlight_clipped/warm_shifted/saturation_shifted/contrast_shifted/unknown` 捕获空间分类。常见 HDR 高光裁切、夜间模式和温和显卡色彩调整自动兼容；黑帧、反色、极端滤镜或无法确认的布局继续保守停止。
- 非中性色彩环境自动关闭 Fast OCR assist，以 PP-OCR 为权威识别；仅在原始识别为空或低置信度且清洗失败时执行一次 P2-P98 亮度归一化重试，重试结果仍经过完整字段和槽位校验。
- 滚动端点改用列表图像签名的连续无位移判断，副属性行改用相对背景亮度与文字边缘判断；`scan_complete` 和所有扫描失败会返回预检状态、色彩分类、脱敏分数、窗口尺寸、DPI、捕获后端和视觉配置。
- 本地日志保留完整 HSV 与判定过程；Web 遥测不包含截图、OCR 文本、仓库数量、原始 RGB、本机路径或异常堆栈。原生回归新增常见色彩变换、HDR 裁切、品质歧义、预检状态机、相对行检测和亮度归一化覆盖。
- 详情面板重试不再在固定 80ms 后立即从邻项点回目标项，而是等待邻项相对目标发生强变化并连续稳定两帧，最多等待 600ms；超时时沿用目标快照作为保守基线并记录 `PANEL_SELECTION_REFRESH_TIMEOUT`。
- 面板最终超时新增首个缺失 ROI、参考/候选亮度差、允许亮度差和文字边缘密度等整数诊断；字段复用同一次行检测采样，只在终态写入，不采集原始颜色、截图或 OCR 文本。

- Scanner 1.0.39 为详情面板超时增加结构化诊断：最后一次 `visibleRois/totalRois`、接受门槛、面板/选择变化状态、稳定帧、三次重试、捕获帧数、窗口客户区、DPI、实际捕获后端和视觉配置 ID 会同时写入 `PANEL_CAPTURE_TIMEOUT` 本地日志并通过现有 `scan_error.details` 发送给本机网页。
- `scan_complete` 新增聚合 `queued`、`completed` 和安全的会话诊断；现有字段保持兼容。网页可以只上传这些脱敏字段，不需要也不会从 Scanner 读取驱动盘数组、截图、OCR 文本、本机目录、完整日志或异常堆栈。
- 回归项目新增结构化面板超时诊断契约，当前 17 项 Helper/Scanner 回归测试全部通过。正式 Windows CI 使用 `-RequireVCRedistLayout` 构建并通过双包体积门禁：FDD `21756850` 字节、SHA-256 `6488a032b22c9cf907ea3637927b3c8df3b9bd7a04162818c3244cec80d57ea0`；自包含包 `84775658` 字节、SHA-256 `cc1552a38536b764373c24821af003b7e85adb097f3611653a86783c2a06b037`。构建报告确认 `vcRuntimeSource=vc-redist-layout`；System32 fallback 产物不得作为正式发布资产。Helper 继续沿用已经发布的 1.2.1，不用本次流水线中同版本的重复构建覆盖。

- Helper 1.2.1 修复 1.1.x 升级死路：下载引导程序先校验 22355 端口确为 ZZZ Scanner Helper，再要求唯一进程的名称、文件版本与健康检查版本一致。用户确认后才关闭该旧进程、等待端口释放、安装和校验固定路径副本、注册协议并启动托管 Helper；未知服务、多候选、版本不匹配、取消、终止失败或端口释放超时均不结束进程。
- 网页在读取 Helper 版本和协议后才允许请求 Scanner manifest。1.1.x 只显示“下载并更新 Helper”和“重新检测”；协议 v3 的旧版本自动调用 `update_helper`，断线期间保持扫描流程并在重连后继续准备 OCR。自动更新失败同时提供重试和手动下载。
- 发布脚本从 Helper 项目版本生成 `launcherMinVersion`，Helper Release tag 与 Scanner tag 解耦，并支持 `-HelperOnly` 只生成 Helper 资产。Helper 1.2.1 为 `8137728` 字节、SHA-256 `d3c88f1f7556e9bab15f7129e253d2c5527b0f5009a84d52a7a0acd354f326ae`；Scanner 1.0.38 两个既有包未重建或替换，哈希保持不变。

- 1.0.38 / Helper 1.2.0 将长期驻留文件收敛为一个托管 Helper 和一个已完成握手的活动 OCR runtime。Helper 首次确认后安装到 `%LOCALAPPDATA%\ZZZScannerNext\helper`，协议注册固定指向托管路径；下载引导副本在哈希一致且托管进程接管后删除。
- Helper 协议升级到 v3，新增带 `requestId` 的 `get_storage_info`、`cleanup_storage` 和 `update_helper`。存储统计区分 Helper、runtime、安装包、扫描产物、日志及可释放空间；清理失败路径写入待清理收据并在下次启动重试。
- scanner manifest 升级到 schema v3，每个包新增逐文件路径、大小和 SHA-256。Helper 在验证 ZIP、临时解压目录和文件清单后删除 ZIP；后续无需保留压缩包也能检查缺失、篡改和意外文件。
- 活动版本只在 scanner 子进程完成 WebSocket 握手后写入原子收据。下载、解压或启动失败均保留旧活动版本；新版本接管后才删除其他版本和包 ID，避免升级失败造成不可用。
- 网页托管扫描把输出根目录迁移到 `%LOCALAPPDATA%\ZZZScannerNext\outputs`。首次清理从历史 `runtime/**/Scans` 中迁移最近一次成功和最近一次失败产物，删除其余历史 runtime、ZIP、`.tmp` 与 `.download`。
- 新增独立 `helper-manifest.json` 和事务式 Helper 自更新：HTTPS、大小及 SHA-256 校验通过后由新进程替换固定文件，启动成功删除备份，启动失败由旧版恢复。Helper 1.1.0 需要一次手动过渡，之后不再重复下载 EXE。
- 1.0.38 最终本地发布门禁通过：FDD `21785760` 字节、SHA-256 `d63b89fbe07fba77fe641daa33b2aa8f5427f4b93fc95003fefe8c9750f7b78d`；自包含包 `84835665` 字节、SHA-256 `39be4d44d2b756db66f36b8308b055495e59ce4174cdb615399ceba293613bf4`；Helper 1.2.0 `8096768` 字节、SHA-256 `249e2fc2a6226e096ba8094cce5f984d7ff1130344431ced0a74155465937d9b`。

- 重写根目录用户手册：`README.md` 提供完整英文说明，新增 `README.zh-CN.md` 提供完整中文说明；两版均覆盖支持系统与不支持范围、Helper 自动选择 FDD/自包含包、手动安装、扫描操作、权限与 UAC、输出文件、结构化故障排查、已知限制、命令行和发布门禁。
- 1.0.37 移除 OpenCvSharp：所有 ROI 使用 `System.Drawing.Rectangle`，PP-OCR 预处理改为基于 `Bitmap.LockBits` 的裁剪、半像素双线性缩放和 BGR/NCHW tensor 写入；保留 PP-OCRv5 模型、ONNX Runtime、Fast OCR、DXGI 与 GDI 回退。
- 1.0.37 发布脚本同时生成 framework-dependent 与 self-contained 两个 x64 包，并生成 schema v2 manifest、SHA-256、展开大小和体积报告；发布门禁为 FDD 25 MiB、自包含 90 MiB、NativeAOT Helper 10 MiB。
- 两种包均随附 ONNX Runtime 所需的 VC143 本地依赖。CI 要求从受控 Visual Studio Redistributable 布局复制；缺少该布局时发布会失败，避免在正式构建中静默遗漏或使用来源不明的 DLL。
- Helper 1.1.0/协议 v2 支持 Win10 Build 17763+ 与 Win11 x64 环境检查、.NET 8 Desktop Runtime 多来源探测、双包自动选择、按包 ID 隔离缓存、空间预检、FDD 一次性自包含回退和滚动诊断日志；schema v1 保持兼容。
- Helper 新增结构化错误与 `repair_scanner`、`restart_scanner_elevated`、`open_log_folder`、`get_diagnostics` 命令。scanner 改为 `asInvoker`，只在目标游戏权限更高时返回 `elevation_required`；用户取消 UAC 会识别为 Windows 错误 1223。
- 1.0.37 最终产物：FDD `21785638` 字节、SHA-256 `6ead4f1401ea057c706b4ec94ab41d66499240f95c8d6a9051fe71027d9e5404`；self-contained `84835543` 字节、SHA-256 `bdef1a3d3d0ecf9917b2618fb46cd04cea6443dbd8b399d9793c6f375a993129`；Helper `7823872` 字节、SHA-256 `8735147fb1d3061ad410ba162cebf841be815834699faa341aae342e422f2186`。ZIP 使用路径排序与固定时间戳，连续两次完整发布 SHA 保持一致。
- 修复 Helper 更新信任边界：移除公网 HTTP/IP 回退，远程 manifest、包地址及重定向必须使用 HTTPS；loopback HTTP 仅用于本地开发。manifest 新增 schema、最低 Helper 版本、版本号、SHA-256、包大小和入口文件校验。
- 修复 manifest 路径逃逸风险：scanner 版本、入口、安装目录、临时目录和缓存包路径均执行根目录包含检查；未知或非法字段会在下载、删除和提权启动前失败。
- Helper 不再仅凭 EXE 存在复用 runtime：缓存 ZIP 必须重新通过大小/SHA-256 校验，已安装文件必须与 ZIP 逐文件一致；损坏、篡改或非输出目录中的额外文件会触发自动修复。
- 修复 legacy scanner WebSocket 可被任意网页连接的问题：无令牌直连必须来自白名单 Origin，CORS 不再返回通配符；WebSocket 消息限制为 256 KiB，并增加跨连接全局扫描互斥。
- 修复浏览器断开后提权 scanner 子进程残留：`BrowserSession` 结束时会释放内部 WebSocket、结束子进程；子进程连接失败路径也会完整清理。
- scanner host 与 Helper 收到 WebSocket Close 后会返回标准关闭确认，避免客户端 `CloseAsync` 悬挂并延迟会话/子进程清理。
- 统一 Fast Mode 默认值：GUI、CLI 和 WebSocket 共用 `ScanModeDefaults`，高速模式使用 `early-one-row + adaptive-early-full-roi + recover`，关闭后恢复 `safe + safe + recheck`。
- 修复发布版本漂移：`publish-slim.ps1 -Version` 会写入程序集/文件版本并校验生成的 EXE；支持从 csproj 读取默认版本和使用 `-OutputRoot` 隔离验证发布。`AppInfo.Version` 改为读取程序集版本。
- profile 查找改为不区分大小写的严格匹配；未知 profile 或空 profile 文件会明确失败，不再静默使用第一个 profile 操作游戏窗口。
- 新增无第三方测试依赖的 `Tests/ZZZ-Scanner.Next.RegressionTests.csproj`，覆盖下载协议、manifest/path traversal、runtime 完整性、实际 WebSocket Origin/token 握手、Fast Mode、profile 和程序集版本回归。
- 1.0.36 槽位安全热修：Fast OCR assist 运行时强制禁用 `name` 字段，`name` 仍保留 shadow/eval/training 但正式导出始终回退 PP-OCR；校准器也不再允许 `name` 自动进入 assist，避免套装名模板误接受导致槽位污染。
- 1.0.36 新增导出前槽位硬校验与 benchmark 验收指标：`slot_out_of_range_count`、`slot_mainstat_violation_count`、`slot_fixed_value_violation_count`、`slot_safety_pass`。旧坏样本 `runtime\1.0.34\Scans\2026-07-02-18-34-09-134-pf38-77d5` 回放会正确判定 `slot_safety_pass=false`，其中 `slot_mainstat_violation_count=18`、`slot_fixed_value_violation_count=19`。
- 1.0.36 模板 `Data\ocr_fast_templates.json` 已把所有 `name` policy 的 `AssistEnabled` 置为 false，保留 8275 个 v6 模板样本；模板 SHA-256 为 `814e28114378756e7c541c0efe6cfa2469e1e723d0498ba8e73edea58266a076`。
- 1.0.36 已发布到 `publish 1.0.36`，并为网页重新生成不含 `Scans` 的分发包 `dist\ZZZ-Scanner.Next-win-x64-1.0.36-web.zip`，大小 `47231570` 字节，SHA-256 为 `d885c0aef6da61cfcbf994ad2b4e712a31efe8bd87631260fe4f87ea8711c63d`；`dotnet build ZZZ-Scanner.Next.csproj -c Release` 通过，0 warning / 0 error。120 件本地实扫 `publish 1.0.36\Scans\2026-07-09-16-07-11-507-p2784-e284` 验收通过：`Completed=120`、`Failed=0`、重复导出 0、`IncompleteRoi=0`、`slot_safety_pass=true`、`profile_route=exact:7`。
- 1.0.35 增加网页 WebSocket `scan_req.processName` 支持：网页端选择“云绝区零”时可传入 `Zenless Zone Zero Cloud`，扫描器会覆盖默认 `ZenlessZoneZero` 进程名，并与 `visualProfileClient=cloud` 一起命中 1.0.34 已内置的云端 exact profile。未传 `processName` 时仍保持本地端默认行为。
- 1.0.35 已用 framework-dependent publish 重新生成 `publish 1.0.35`，排除 `Scans` 后压缩为网页分发包，大小 `47228425` 字节，SHA-256 为 `2a10aa3dc92e50c7ea930d75eda82fef741eff16e8c39f2839240b6fc36b0255`；`dotnet build ZZZ-Scanner.Next.csproj -c Release` 通过，0 warning / 0 error。
- 1.0.34 将“本地三挡 + 云绝区零大窗口 + 云绝区零普通窗口 + 云绝区零全屏”的 v6 Fast OCR 合并模板提升为正式 `Data/ocr_fast_templates.json`，覆盖 `local-1280x720-current`、`local-1600x900-current`、`local-1920x1080-current`、`cloud-1592x896-current`、`cloud-1440x808-current`、`cloud-1920x1080-current`；模板 `Version=6`、`Feature=canonical-ahash-dhash-vhash-edge-16x16-v6`、`templates=8275`、`ProfileFieldPolicies=42`、`FamilyFieldPolicies=14`。正式模板已清空训练用 `SourceImage` 本机路径，SHA-256 为 `481a7d08e02c514bce3188f6cf04a6126404417e3c1788ed940df8f6ad12c26a`。
- 云绝区零全屏实际客户区检测为 `1920x1080`、DPI 96，profile 为 `cloud-1920x1080-current`。该 profile 使用 3 轮 clean shadow 采集训练，均 `Completed=120`、`Failed=0`、重复导出 0、`IncompleteRoi=0`、`overlap_hard_stop_count=0`、`row_scroll_overshot_count=0`。
- 云全屏 v6 校准 `false_accepts=0`；默认启用 `level/mainStat/subStat1/subStat2/subStat3/subStat4`，`name` 因接受率 `85.000%` 继续禁用并回退 PP-OCR。
- 云全屏单独 v6 index 120 件 assist 验收目录 `publish 1.0.34\Scans\2026-07-02-17-15-45-213-pd14-db93`：`completed_per_sec=3.780`、`Completed=120`、`Failed=0`、重复导出 0、`IncompleteRoi=0`、`ocr_backlog_max=1`、`profile_route=exact:7`、`fast_accepted_per_item_avg=5.992`、`ppocr_roi_per_item_avg=6.008`。
- 云全屏发布目录内置模板 120 件验收目录 `publish 1.0.34\Scans\2026-07-02-17-21-34-514-p6c68-5211`：`completed_per_sec=3.728`、`Completed=120`、`Failed=0`、重复导出 0、`IncompleteRoi=0`、`profile_route=exact:7`、`fast_accepted_per_item_avg=5.992`、`ppocr_roi_per_item_avg=6.008`。
- 云全屏默认有效全量验收目录 `publish 1.0.34\Scans\2026-07-02-17-22-33-432-pb00-a556`：使用发布包内置 `Data\ocr_fast_templates.json`，`Completed=466`、`Failed=0`、`completed_per_sec=4.048`、重复导出 0、`IncompleteRoi=0`、`overlap_hard_stop_count=0`、`row_scroll_overshot_count=0`、`profile_route=exact:7`、`fast_accepted_per_item_avg=5.961`、`ppocr_roi_per_item_avg=6.039`、`ocr_backlog_max=1`；第 467 个检测为非 15 级 `云岿如我 S 12/15` 并按网页导入规则停止。
- 云绝区零普通窗口实际客户区检测为 `1440x808`、DPI 96，profile 为 `cloud-1440x808-current`。该 profile 完成 3 轮 `MaxItems=120 --collect-visual-profile` shadow 采集，三轮均 `Completed=120`、`Failed=0`、重复导出 0、`IncompleteRoi=0`、`overlap_hard_stop_count=0`、`row_scroll_overshot_count=0`。
- 云普通窗口 v6 校准 `false_accepts=0`；默认启用 `level/mainStat/subStat1/subStat2/subStat3/subStat4`，`name` 因接受率 `82.778%` 继续禁用并回退 PP-OCR。
- 云普通窗口单独 v6 index 120 件 assist 验收目录 `publish 1.0.34\Scans\2026-07-02-16-44-59-913-p738-b64c`：`completed_per_sec=3.868`、`Completed=120`、`Failed=0`、重复导出 0、`IncompleteRoi=0`、`ocr_backlog_max=1`、`profile_route=exact:7`、`fast_accepted_per_item_avg=5.900`、`ppocr_roi_per_item_avg=6.100`。
- 合并模板云普通窗口内置模板 120 件 smoke 目录 `publish 1.0.34\Scans\2026-07-02-16-48-24-860-p2d00-835f`：`completed_per_sec=3.646`、`Completed=120`、`Failed=0`、重复导出 0、`IncompleteRoi=0`、`profile_route=exact:7`、`fast_accepted_per_item_avg=5.917`、`ppocr_roi_per_item_avg=6.083`，确认发布目录内置模板在 strict 路由下可直接命中云普通窗口。
- 云普通窗口默认有效全量验收目录 `publish 1.0.34\Scans\2026-07-02-16-57-32-843-p3224-e657`：使用发布包内置 `Data\ocr_fast_templates.json`，`Completed=466`、`Failed=0`、`completed_per_sec=4.036`、重复导出 0、`IncompleteRoi=0`、`overlap_hard_stop_count=0`、`row_scroll_overshot_count=0`、`profile_route=exact:7`、`fast_accepted_per_item_avg=5.906`、`ppocr_roi_per_item_avg=6.094`、`ocr_backlog_max=1`；第 467 个检测为非 15 级 `云岿如我 S 12/15` 并按网页导入规则停止。
- 云绝区零大窗口实际客户区检测为 `1592x896`、DPI 96，profile 为 `cloud-1592x896-current`。该 profile 完成 3 轮 `MaxItems=120 --collect-visual-profile` shadow 采集，三轮均 `Completed=120`、`Failed=0`、重复导出 0、`IncompleteRoi=0`、`overlap_hard_stop_count=0`、`row_scroll_overshot_count=0`。
- 云大窗口 v6 校准跨轮 `false_accepts=0`；默认启用 `level/mainStat/subStat1/subStat2/subStat3/subStat4`，`name` 因接受率 `85.556%` 继续禁用并回退 PP-OCR。
- 云大窗口单独 v6 index 120 件 assist 验收目录 `publish 1.0.33\Scans\2026-07-02-15-55-50-424-p64ec-f3d4`：`completed_per_sec=3.864`、`Completed=120`、`Failed=0`、重复导出 0、`IncompleteRoi=0`、`ocr_backlog_max=1`、`profile_route=exact:7`、`fast_accepted_per_item_avg=5.958`、`ppocr_roi_per_item_avg=6.042`。
- 合并模板云大窗口 30 件 smoke 目录 `publish 1.0.33\Scans\2026-07-02-16-00-24-632-p2bac-9906`：`Completed=30`、`Failed=0`、重复导出 0、`IncompleteRoi=0`、`profile_route=exact:7`、`fast_accepted_per_item_avg=6.000`、`ppocr_roi_per_item_avg=6.000`，确认云 profile 走 strict exact 路由且不会误用本地模板。
- 按本轮要求覆盖发布到 `publish 1.0.34`。Release build 与 framework-dependent publish 均通过，0 warning / 0 error；发布包内置模板 hash 与正式 `Data` 一致，exe `FileVersion=1.0.34.0`。
- `scripts/publish-slim.ps1 -Version 1.0.34` 已重新生成网页分发包 `dist\ZZZ-Scanner.Next-win-x64-1.0.34.zip`，大小 `115521420` 字节，SHA-256 为 `7956191c3894b875851e27199545311703d0cc8a1a141568e9101929ab1db7c0`；zip 内包含清理后的 `Data/ocr_fast_templates.json`。
- 1.0.34 云大窗口默认有效全量验收目录 `publish 1.0.34\Scans\2026-07-02-16-12-06-487-p4320-ff38`：使用发布包内置 `Data\ocr_fast_templates.json`，`Completed=466`、`Failed=0`、`completed_per_sec=3.963`、重复导出 0、`IncompleteRoi=0`、`overlap_hard_stop_count=0`、`row_scroll_overshot_count=0`、`quick_accept_count=0`、`profile_route=exact:7`、`fast_accepted_per_item_avg=5.726`、`ppocr_roi_per_item_avg=6.274`、`ocr_backlog_max=2`；第 467 个检测为非 15 级并按网页导入规则停止。
- 1.0.33 将三挡本地分辨率 v6 Fast OCR 候选模板提升为正式 `Data/ocr_fast_templates.json`，覆盖 `local-1280x720-current`、`local-1600x900-current`、`local-1920x1080-current`；模板 `Version=6`、`Feature=canonical-ahash-dhash-vhash-edge-16x16-v6`、`templates=719`、`ProfileFieldPolicies=21`，SHA-256 为 `c5b18d4abcd612a4a406f10fc3214e9746f685f403b81c2303cb018b21890a0a`。
- 三挡分辨率均完成 120 件 assist 与默认有效全量扫描验收。默认有效全量指保留 `StopAtNonLevel15=true`，遇到第一个非 15 级驱动盘停止，符合网页正式导入路径；`--include-non15` 全仓读取仍需要后续 partial ROI 设计，不纳入本轮默认验收。
- 三挡默认有效全量速度：1280x720 `4.096/s`、1600x900 `4.201/s`、1920x1080 `4.052/s`；三轮均 `Failed=0`、重复导出 0、`IncompleteRoi=0`、`overlap_hard_stop_count=0`、`row_scroll_overshot_count=0`、`quick_accept_count=0`、`profile_route=exact:7`。
- `.gitignore` 改为继续忽略实验模板 `Data/ocr_fast_templates*.json`，但允许提交已验证的正式 `Data/ocr_fast_templates.json`。
- 1.0.32 将 Fast OCR assist 默认路由收紧为 `strict`：只有 exact profile 模板参与导出；`family/compatible/auto` 保留为显式实验路径，避免未知分辨率误用相近模板。
- 新增 `--ocr-fast-merge-indexes <output.json> <index1.json> <index2.json> [...]`，用于合并多个 v6 canonical index；合并时保留 profile-specific policies，global/family policy 采用更严格阈值。
- 收紧 `selection_changed_stable_full_roi` 兜底：滚动后首格、retry/fallback/recover 场景或 selection 变化时间无明确正值时，会记录 `PANEL_SELECTION_ONLY_BLOCKED` 并走 stale retry，不再把旧面板入队。
- 新增弱 panel change 与过早 panel change 保护：同排相邻格如果只有弱探针变化，或点击后 25ms 内出现不可信变化，会记录 `PANEL_WEAK_CHANGE_BLOCKED` 并触发 stale retry，避免相邻格旧面板重复入队。
- `--scan-benchmark` 新增 `selection_only_accept_count`、`post_scroll_selection_only_blocked_count`、`weak_panel_change_blocked_count` 和 `fast_exact_profile_accept_count`，用于定位 selection-only/弱变化风险与确认 Fast OCR 是否走 exact profile。
- 1.0.31 引入 Fast OCR v6 模板特征：`--feature v6` 会对 ROI 文字核心做 canonical crop，再生成灰度、横向差分、纵向差分和边缘梯度 hash；v3/v4 index 仍保持读取兼容。
- Fast OCR profile 路由新增 `family` 模式并作为默认：优先 exact profile；没有 exact 时只允许同 `client + aspect bucket + dpi bucket + quality` family 模板；`compatible/auto` 保留为显式探索路径，避免未知环境直接全局 fallback。
- `--ocr-fast-calibrate-visual-profiles` 会写出 `ocr_fast_family_calibration.csv` 和 index 内的 `FamilyFieldPolicies`；profile/family 字段启用仍要求跨轮/跨 profile 零误接受。
- `visual_profile.json` 增加 `ProfileFamilyId`；`scan.log` 新增 `FAST_OCR_PROFILE_FAMILY_ROUTE`；`ocr_fast_assist.csv`、`ocr_fast_shadow.csv` 和 `ocr_fast_eval.csv` 增加 source family、canonical crop 与 feature 耗时字段。
- 如果 `--collect-visual-profile` 的 requested profile 几何与实际窗口客户区不一致，运行时会保留 requested label，但训练/assist 使用 detected profile，并记录 `ProfileGeometryStatus=requested_mismatch_detected_fallback:*`，避免把样本写进错误分辨率模板。
- `--scan-benchmark` 新增 `profile_family_id`、`fast_accept_by_profile_family`、`canonical_crop_fallback_rate` 和 `v6_feature_ms_*`，用于判断 v6 是否真的覆盖当前 family 且没有过多 canonical fallback。
- 1.0.31 不把本轮 smoke 生成的 v6 模板复制到 `Data/ocr_fast_templates.json`；正式模板仍需每个视觉 profile 至少 3 轮 shadow 数据并通过 cross validation 后再进入发布数据。
- 1.0.30 引入多环境视觉 profile：新增 `--collect-visual-profile`、`--visual-profile-client`、`--visual-profile-quality` 和 `--profile-routing strict|family|compatible|auto`；`visual_profile.json` 记录 requested/detected geometry、DPI、capture 后端和路由决策。
- Fast OCR 模板索引支持 profile 路由，优先同 profile 模板，compatible 模式会尝试同客户端/相近宽高比模板，auto 才回退全局；`scan.log` 和 benchmark 新增 `PROFILE_HEALTH_*`、`FAST_OCR_PROFILE_ROUTE`、`profile_detected_geometry`、`profile_route` 和 `health_fallback_count`。
- `--collect-visual-profile` 默认关闭 Fast OCR assist、开启 shadow dataset、保守面板/滚动策略，适合作为每轮采集前的安全协议入口。
- Helper 发布改为 NativeAOT + source-generated JSON，`ZZZ-Scanner-Helper.exe` 从约 67.6MB 降到 7.4MB，并通过本地 HTTP `/` 与 `/token` 探测。
- 新增 `scripts/publish-slim.ps1`，统一生成瘦身 Helper 与 Scanner zip。
- Scanner self-contained 发布后自动移除未使用的 FFmpeg 视频 DLL、PDB、调试/诊断文件、native import lib，以及 `Scans`/`StabilitySuites` 输出目录；`1.0.28` OCR zip 从约 129.9MB 降到 115.2MB。
- 保留同一个 OCR 模型、入口程序和 1.0.28 manifest 协议；未启用 WinForms trimming，避免给用户增加 .NET Runtime 或裁剪兼容风险。

## 2026-07-01 1.0.28 全量扫描滚动冲突自愈

- 新增 `OverlapConflictMode=strict|recheck|recover`，CLI 参数为 `--overlap-conflict-mode strict|recheck|recover`。普通模式默认 `recheck`，`--fast-mode` 默认 `recover`。
- 重叠签名扫描遇到 `scrollRows` 与 `signatureRows` 不一致时，不再把弱二行签名直接当成硬失败；现在会记录 `RowAdvanceEvidence`，最多复核 3 帧。弱证据可按滚动验证继续，强二行证据必须通过已扫逻辑行覆盖检查才接受。
- `scan.log` 开头新增 `AppVersion`、assembly/file version、exe 路径、exe 修改时间和 runtime 目录；WebSocket `hello/pong/scan_complete/scan_error` 返回同样的 scanner 诊断信息。
- `--scan-benchmark` 新增 `overlap_conflict_*`、`overlap_hard_stop_count`、`full_scan_expected`、`full_scan_complete`、`missing_logical_rows_count`、`scanned_logical_rows_count` 和 `total_rows`。
- 发布到 `publish 1.0.28`，不覆盖旧发布目录。`dotnet build -c Release`、self-contained publish、`npm run test:scanner-bridge` 与 `npm run build:pages` 均通过。
- 用 1.0.28 benchmark 复查 1.0.26 网页失败目录 `C:\Users\ZZT\AppData\Local\ZZZScannerNext\runtime\1.0.26\Scans\2026-07-01-00-22-11-662-p904c-c955`：识别为 `overlap_conflict_count=1`、`full_scan_expected=true`、`full_scan_complete=false`、`missing_logical_rows_count=41`，可明确定位为全量滚动冲突中止。
- `zzz_calculator` manifest 与 bridge 已同步到 scanner `1.0.28`；网页默认 payload 追加 `overlapConflictMode="recover"`，扫描失败弹窗显示 scanner 版本和 runtime 目录。

## 2026-06-29 1.0.27 场景化面板下限与滚动 tick 矩阵

- 新增 `PanelFloorMode=static|scene-adaptive`，CLI 可用 `--panel-floor-mode static|scene-adaptive`。`scene-adaptive` 只允许同排普通点击降低面板最低等待；warmup、滚动后首格、视觉第 2 行补扫以及 retry/fallback 场景继续保守。
- 新增 `--same-row-panel-min-accept-floor <100..120>`、`--post-scroll-panel-min-accept-floor <100..120>` 和 `--scroll-tick-delay-ms <50..80>`，用于显式测试同排点击、滚动后首格和滚动 tick 等待；90ms 下限继续不进入候选矩阵。
- `--capture-stability-suite` 新增 `--suite-profile speed-1.0.27`，固定跑 DXGI 默认、DXGI `floor110 + postscroll adaptive`、`scene-adaptive same-row105 postscroll110 + scroll60`、`scene-adaptive same-row100 postscroll110 + scroll60`、`scene-adaptive same-row105 postscroll110 + scroll50`。
- `CELL_TIMING` 与 `--scan-benchmark` 新增 `panel_floor_mode`、`same_row_panel_floor_ms`、`post_scroll_panel_floor_ms`、`floor_wait_limited_count/ms`、`panel_accept_elapsed_vs_floor_ms` 和 `scroll_tick_delay_ms`，用于确认是否真的被最低等待限制。
- 发布到 `publish 1.0.27`，不覆盖旧发布目录。`dotnet build -c Release` 与 self-contained publish 通过，0 warning / 0 error。
- 普通模式 30 件目录 `publish 1.0.27\Scans\2026-06-29-21-30-56-768-p3474-58f7`：失败 0、重复 0、`IncompleteRoi=0`、`quick_accept_count=0`、`row_scroll_overshot_count=0`，全部 acceptance pass。
- 30 件 smoke suite `publish 1.0.27\StabilitySuites\capture-dxgi-20260629-213240`：5 个候选全部 correctness pass。
- 完整 5 轮 suite `publish 1.0.27\StabilitySuites\capture-dxgi-20260629-213516`：5 个候选均 5/5 轮 correctness pass，失败 0、重复 0、`IncompleteRoi=0`、`quick_accept_sum=0`、`row_scroll_overshot_sum=0`。最佳 5 轮候选为 `scene105/post110/scroll50`，`completed_per_sec_min=3.615`、`avg=3.641`、`p90=3.674`，未达到 3.8/s 目标。
- 最佳候选 10 轮 suite `publish 1.0.27\StabilitySuites\dxgi-scene105-post110-scroll50-final10-20260629-215153`：10/10 轮 correctness pass，`completed_per_sec_min=3.574`、`p10=3.589`、`avg=3.624`、`p90=3.685`。低于 1.0.26 的最佳稳定候选 `3.719/s`，因此不升级默认 fast-mode。
- 慢 OCR 压力目录 `publish 1.0.27\Scans\2026-06-29-21-59-48-046-p7310-fe07`：`adaptiveThrottleMs_avg=194.167ms`、`adaptiveThrottleMs_max=300ms`，失败 0、重复 0、`IncompleteRoi=0`；小队列压力下自动背压仍生效。
- 互斥保护强烟测：先启动 120 件 fast DXGI 扫描，再在扫描中启动第二个 `--scan-once --max-items 1`，扫描目录数量未增加；首个目录 `publish 1.0.27\Scans\2026-06-29-22-07-32-729-p1c2c-c8cf` 正常完成并全 pass。
- 结论：1.0.27 新增的场景化下限和 scroll tick 诊断稳定可用，但没有带来净提速。默认推荐仍不切换到 scene-adaptive；若要追速度，1.0.26 的 DXGI `--panel-min-accept-floor 110 --post-scroll-panel-accept-mode adaptive-after-scroll` 仍是当前最值得显式复测的安全候选。

## 2026-06-29 1.0.26 后端复核与组合提速矩阵

- 新增 `--capture-stability-suite gdi|dxgi|both --max-items <n> --rounds <n>`，可自动串行跑后端/参数候选并汇总稳定性；`both` 默认覆盖 DXGI 默认、DXGI `--panel-min-accept-floor 110`、DXGI `--panel-min-accept-floor 110 --post-scroll-panel-accept-mode adaptive-after-scroll`、GDI 默认、GDI `--post-scroll-panel-accept-mode adaptive-after-scroll`。
- `--scan-stability-suite` 新增 `recommended_candidate`、`reject_reason` 和 `speed_vs_baseline_percent`。推荐门槛以 1.0.25 DXGI 默认三轮均值 `3.593/s` 为基线，要求 correctness 全通过、`completed_per_sec_p10 >= 3.65` 且平均速度提升至少 5%。
- 发布到 `publish 1.0.26`，不覆盖旧发布目录。`dotnet build -c Release` 通过，0 warning / 0 error。
- 普通模式 30 件目录 `publish 1.0.26\Scans\2026-06-29-20-08-20-763-p82f8-8577`：失败 0、重复 0、`IncompleteRoi=0`、`quick_accept_count=0`、`row_scroll_overshot_count=0`，全部 acceptance pass。
- 完整 5 轮捕获后端 suite 根目录：`publish 1.0.26\StabilitySuites\capture-both-20260629-200949`。DXGI 默认 5/5 轮 correctness pass，`completed_per_sec_min=3.388`、`p10=3.388`、`avg=3.599`，因 p10 和平均增益不足，`recommended_candidate=false`。
- DXGI `--panel-min-accept-floor 110` 5/5 轮 correctness pass，`completed_per_sec_min=3.572`、`p10=3.572`、`avg=3.655`，仍因 p10 和平均增益不足不推荐默认。
- DXGI `--panel-min-accept-floor 110 --post-scroll-panel-accept-mode adaptive-after-scroll` 是本轮最佳稳定组合：5/5 轮 correctness pass，失败 0、重复 0、`IncompleteRoi=0`、`quick_accept_sum=0`、`row_scroll_overshot_sum=0`，`completed_per_sec_min=3.669`、`p10=3.669`、`avg=3.719`、`p90=3.784`。但相对基线平均只提升 `3.504%`，低于 5% 门槛，因此不自动升级为默认。
- GDI 默认 5 轮中 4 轮 correctness pass、1 轮失败，`export_duplicate_items_sum=2`，即使 `completed_per_sec_avg=3.671` 也直接拒绝推荐。GDI `--post-scroll-panel-accept-mode adaptive-after-scroll` 5/5 轮 correctness pass，`completed_per_sec_avg=3.651`，但 p10 和平均增益不足。
- 慢 OCR 压力目录 `publish 1.0.26\Scans\2026-06-29-20-30-22-188-p8774-eb9d`：`adaptiveThrottleMs_avg=185ms`、`adaptiveThrottleMs_max=300ms`、`ocr_backlog_max=5`，失败 0、重复 0、`IncompleteRoi=0`；小队列压力下 `backlog_not_saturated=risk` 符合预期。
- 互斥保护烟测：先启动 30 件扫描，再立即启动第二个 `--scan-once --max-items 1`，第二个实例未创建新扫描目录，首个目录 `publish 1.0.26\Scans\2026-06-29-20-31-25-162-p7694-cb77` 正常完成并全 pass。
- 结论：1.0.26 验证了 GDI 单轮高速不可靠，默认推荐不切到 GDI。若要手动追速度，当前最值得复测的是 DXGI `--panel-min-accept-floor 110 --post-scroll-panel-accept-mode adaptive-after-scroll`；正式默认仍保持 1.0.25/1.0.24 的安全边界，不启用 90ms、不恢复 quick accept、不放宽旧面板和一行滚动验收。

## 2026-06-29 1.0.25 稳定性复核与滚动后首格实验

- 新增 `--scan-stability-suite <scan-parent>`，可汇总多轮扫描的 `completed_per_sec_min/p10/avg/p90`、正确性通过数、失败数、重复导出、ROI 完整性、overshoot、fallback 和 quick accept。
- 新增 `PostScrollPanelAcceptMode=safe|adaptive-after-scroll`，CLI 可用 `--post-scroll-panel-accept-mode safe|adaptive-after-scroll`；GUI 高级设置和 WebSocket payload 增加同名配置。默认仍为 `safe`，只作为滚动后首格实验开关。
- 新增 `--panel-min-accept-floor <ms>`，范围限制为 `90..120`。fast-mode 默认仍为 `120ms`；`110ms/100ms/90ms` 都必须显式传入，不把单轮高速结果直接写成默认。
- `CELL_TIMING` 新增 `postScrollAcceptMode` 与 `panelMinFloorMs`；`--scan-benchmark` 新增 `post_scroll_adaptive_accept_count`、`post_scroll_safe_accept_count`、`panel_min_floor_ms_*` 和 `before_min_accept_count`。
- `--scan-once` 增加跨进程互斥保护：如果已有扫描在控制游戏窗口，第二个扫描实例会直接拒绝启动，避免两个进程同时点击导致旧面板或重复读取。
- 发布到 `publish 1.0.25`，不覆盖旧发布目录。`dotnet build -c Release` 通过，0 warning / 0 error。
- DXGI fast 默认三轮 stability suite：`publish 1.0.25\StabilitySuites\dxgi-default-20260629-193815`，3/3 轮 correctness pass，`completed_per_sec_min=3.576`、`completed_per_sec_avg=3.593`、失败 0、重复导出 0、`IncompleteRoi=0`、`quick_accept_sum=0`、`row_scroll_overshot_sum=0`。
- DXGI fast `--panel-min-accept-floor 110` 三轮 suite：`publish 1.0.25\StabilitySuites\dxgi-floor110-20260629-194751`，3/3 轮 correctness pass，`completed_per_sec_min=3.628`、`completed_per_sec_avg=3.675`、最快单轮 `3.704/s`。因为最低轮仍低于 1.0.24 单轮 `3.656/s`，暂不改为默认。
- DXGI `--post-scroll-panel-accept-mode adaptive-after-scroll` 120 件目录：`publish 1.0.25\Scans\2026-06-29-19-42-42-327-p84ec-3b1f`，全部 acceptance pass，`post_scroll_first_panel_wait_ms_avg=172.064ms`，但 `completed_per_sec=3.634`，不足以默认启用。
- GDI fast 单轮目录：`publish 1.0.25\Scans\2026-06-29-19-41-11-805-p143c-bdf2`，120 件全部 acceptance pass，`completed_per_sec=3.772`。该结果提示 GDI 在本机当前状态可能更快，但仍需多轮复核后再作为推荐后端。
- `--panel-min-accept-floor 90` 30 件通过，但 120 件目录 `publish 1.0.25\Scans\2026-06-29-19-45-37-847-p5a5c-644f` 在第 7 行附近触发签名一致性保护停止：`signatureRows=2`。90ms 不作为候选。
- 慢 OCR 压力目录：`publish 1.0.25\Scans\2026-06-29-19-49-35-812-p74b4-8766`，`ocr-workers=1`、`ocr-queue=4`、`ocr-intra-op=1` 下 `adaptiveThrottleMs_avg=204.167ms`、`adaptiveThrottleMs_max=300ms`，失败 0、重复 0、`IncompleteRoi=0`；小队列压力下 `backlog_not_saturated=risk` 符合预期。
- 结论：1.0.25 更像“稳定性复核与实验工具版”。默认 fast-mode 保持 1.0.24 的安全组合；若要追速度，可显式复测 `--panel-min-accept-floor 110` 或 GDI fast，但当前默认不接受 90ms 或滚动后首格 adaptive。

## 2026-06-29 1.0.24 面板等待降帧与 fast-mode 默认提速

- 新增 `PanelAcceptMode=safe|adaptive-early-full-roi`，CLI 可用 `--panel-accept-mode safe|adaptive-early-full-roi`；GUI 高级设置增加“面板接受策略”，WebSocket payload 增加 `PanelAcceptMode`。
- `adaptive-early-full-roi` 只在 warmup 后、非滚动后首格中启用；仍必须看到详情变化、12 个 OCR ROI 全可见、达到本轮自适应最低等待，且 `quick_accept_count` 保持 0。它以 ROI 完整帧作为 early full ROI 接受信号，不跳过旧面板保护。
- `--fast-mode` 默认推荐组合切到 `PanelAcceptMode=AdaptiveEarlyFullRoi` 与 `ScrollAcceptMode=EarlyOneRow`；显式传入 `--panel-accept-mode safe` 或 `--scroll-accept-mode safe` 可回退保守路径。
- `CELL_TIMING` 新增 `panelAcceptMode`、`roiCompleteFrames`、`selectedStableFrames` 和 `acceptGateReason`；`--scan-benchmark` 新增 `roi_complete_frames_*`、`selected_stable_frames_*`、`panel_frames_after_warmup_*` 和 `accept_gate_reason_*_count`。
- 发布到 `publish 1.0.24`，不覆盖旧发布目录。`dotnet build -c Release` 通过，0 warning / 0 error。
- DXGI fast 显式新组合目录：`publish 1.0.24\Scans\2026-06-29-18-02-16-210-p3c6c-abec`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、`quick_accept_count=0`、`row_scroll_overshot_count=0`、关键 acceptance 全 pass；`completed_per_sec=3.621`，`panel_wait_ms_avg=153.684ms`，`scroll_ms_avg=94.636ms`。
- DXGI fast 默认命令最终目录：`publish 1.0.24\Scans\2026-06-29-18-06-52-713-p1c74-dd74`，命令为 `--scan-once --fast-mode --capture-mode dxgi --max-items 120`；`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、`strict_one_way_scroll=pass`、`overlap_rows_complete=pass`，`completed_per_sec=3.656`，超过 1.0.20 DXGI fast 的 `3.406/s` 和本轮 `3.5/s` 目标。
- GDI fast 新组合目录：`publish 1.0.24\Scans\2026-06-29-17-46-55-703-p2754-8021`，120 件全部 acceptance pass，`completed_per_sec=3.187`。
- 慢 OCR 压力目录：`publish 1.0.24\Scans\2026-06-29-17-48-12-529-p11ec-33a4`，`ocr-workers=1`、`ocr-queue=4`、`ocr-intra-op=1` 下 `adaptiveThrottleMs_avg=194.167ms`、`adaptiveThrottleMs_max=300ms`，失败 0、重复 0、`IncompleteRoi=0`；小队列压力下 `backlog_not_saturated=risk` 符合预期。
- 结论：1.0.24 是当前推荐高速路径。速度收益来自面板等待降帧与一行滚动早接受的组合，而不是 OCR 或 quick accept；若个别机器出现波动，应优先回退 `--panel-accept-mode safe` 或 `--scroll-accept-mode safe` 做对照。

## 2026-06-29 1.0.23 滚动后首格诊断与一行早接受实验

- 新增 `ScrollAcceptMode=safe|early-one-row`，CLI 可用 `--scroll-accept-mode safe|early-one-row`；GUI 高级设置增加“滚动接受策略”，WebSocket payload 增加 `ScrollAcceptMode`。
- `early-one-row` 在行签名确认当前视口只前进一行时允许提前接受滚动结果；多行 overshoot 仍立即阻断，不做回滚修正，不放宽 `strict_one_way_scroll` 和 `overlap_rows_complete` 验收。
- `CELL_TIMING` 新增 `afterScroll` 与 `postScrollFirstCell`；`--scan-benchmark` 新增 `same_row_panel_wait_ms_*`、`post_scroll_first_panel_wait_ms_*` 和 `post_scroll_first_cell_total_ms_*`，用于分开观察普通同排点击和滚动后首格等待。
- 重叠扫描在滚动验收成功后复用该帧的行签名，避免后续补扫判断再额外捕获一次行签名；面板稳定源默认仍为 `panel`，不启用 1.0.22 的 `text-core` 作为推荐路径。
- 发布到 `publish 1.0.23`，不覆盖旧发布目录。`dotnet build -c Release` 通过，0 warning / 0 error。
- 实机 DXGI fast safe 基线目录：`publish 1.0.23\Scans\2026-06-29-16-38-46-790-paa8-da59`，120 件全部 acceptance pass，`completed_per_sec=3.052`，`scroll_ms_avg=396.364ms`。
- 实机 DXGI fast early-one-row 目录：`publish 1.0.23\Scans\2026-06-29-16-41-38-252-p828-3b18`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、`row_scroll_overshot_count=0`、关键 acceptance 全 pass；`completed_per_sec=3.119`，`scroll_ms_avg=101.636ms`，`same_row_panel_wait_ms_avg=211.044ms`，`post_scroll_first_panel_wait_ms_avg=219.845ms`。
- 实机 GDI fast early-one-row 目录：`publish 1.0.23\Scans\2026-06-29-16-43-08-726-p7210-f397`，120 件全部 acceptance pass，`completed_per_sec=2.810`。
- 慢 OCR 压力目录：`publish 1.0.23\Scans\2026-06-29-16-44-27-731-p10f0-3edf`，`ocr-workers=1`、`ocr-queue=4`、`ocr-intra-op=1` 下 `adaptiveThrottleMs_avg=204.167ms`、`adaptiveThrottleMs_max=300ms`，失败 0、重复 0、`IncompleteRoi=0`，背压仍可自动降速。
- 结论：`early-one-row` 能显著压低滚动等待且本轮未发现重复/漏行，但 120 件速度低于 1.0.20 DXGI fast 的 `3.406/s`，因此 1.0.23 不把它设为 fast-mode 默认，仅作为显式实验与诊断开关保留。下一步瓶颈主要是 `panel_wait_ms` 和滚动后首格面板等待，而不是滚动签名等待本身。

## 2026-06-29 1.0.22 字段级稳定判定实验与默认回退

- 新增 `PanelStabilityMode=panel|text-core|auto`，CLI 可用 `--panel-stability-mode panel|text-core|auto`；GUI 高级设置增加“面板稳定判定”，WebSocket payload 增加 `PanelStabilityMode`。
- `text-core` 稳定判定只观察 12 个 OCR ROI 的文字核心区域，核心框为左右各裁 6%、上下各裁 18%，最小宽高 4px；接受条件仍固定为“看到详情变化 + 12 ROI 全可见 + 达到最低等待 + 稳定帧满足”，不恢复 quick accept。
- `auto` 前 12 件 warmup 记录 panel/text-core 稳定耗时，仅当 text-core 明显更快且没有 stale/retry 风险时才选择；实测本机 text-core 不快，因此默认路径回退 `panel`，`text-core/auto` 保留为显式实验选项。
- `CELL_TIMING` 新增 `panelTextStableMs`、`panelStableSource`、`panelStabilityReason`、`rarityProbeMs` 和 `selectionProbeMs`；`--scan-benchmark` 对应输出 `panel_text_stable_ms_*`、`panel_stable_source_*`、`rarity_probe_ms_*`、`selection_probe_ms_*`。
- 发布到 `publish 1.0.22`，不覆盖旧发布目录。`dotnet build -c Release` 通过，0 warning / 0 error。
- 实机 DXGI fast auto 实验目录：`publish 1.0.22\Scans\2026-06-29-15-45-58-399-p5510-e445`，120 件全部 acceptance pass，但 `panel_stable_source_text_core_count=0`，`completed_per_sec=3.098`，低于 1.0.21 与 1.0.20，不作为推荐提速路径。
- 实机 DXGI fast 默认 panel 验收目录：`publish 1.0.22\Scans\2026-06-29-15-53-48-089-p958-c953`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、关键 acceptance 全 pass；`completed_per_sec=3.077`，`quick_accept_count=0`，`panel_stable_source_text_core_count=0`。
- 慢 OCR 压力目录：`publish 1.0.22\Scans\2026-06-29-15-57-00-519-p83c8-c37c`，`ocr-workers=1`、`ocr-queue=4`、`ocr-intra-op=1` 下 `adaptiveThrottleMs_avg=167.5ms`、`adaptiveThrottleMs_max=300ms`，失败 0、重复 0、`IncompleteRoi=0`，背压仍可自动降速。

## 2026-06-28 1.0.21 采集捷径验证与安全收口

- `--scan-benchmark` 继续扩展采集诊断：保留 `quick_accept_count`、`quick_accept_rate_percent`、`panel_frames_*` 和 `selection_change_ms_*`，用于评估未来的快速面板接受策略。
- `CELL_TIMING` 记录 `quickAccept` 和 `quickRejectReason`；本轮实测后默认禁用 quick panel accept，正式路径仍要求“看到详情变化 + 稳定 + 12 ROI 完整”。
- 验证并拒绝两条不安全/无收益路线：激进 quick accept 曾达到 `3.549/s`，但 120 件出现重复导出 26 件；保守 quick accept 无重复但降到 `2.973/s`。DXGI raw frame 缓存复用 30 件即出现重复导出，已撤回。
- `publish 1.0.21` 保持 DXGI 默认 `captureFrameBackend=bitmap-fallback`，raw BGRA 仍仅能通过 `ZZZ_SCANNER_DXGI_RAW=1` 显式实验。
- 最终 120 件验收目录：`publish 1.0.21\Scans\2026-06-28-20-16-04-105-p7a74-96e8`，`MaxItems=120 --fast-mode --capture-mode dxgi`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、关键 acceptance 全 pass；`quick_accept_count=0`，`completed_per_sec=3.221`。
- 下一波提速方向确定为字段级稳定判定和隐藏截图成本拆解，而不是继续降低全局面板等待或复用旧帧。

## 2026-06-28 1.0.20 DXGI Raw Frame 实验与并发目录修复

- 新增内部 `CapturedFrame` 抽象，`GameWindow.CaptureFrame` 可统一服务 GDI 与 DXGI；面板签名、ROI 可见性、行签名和视口签名已改为优先从 frame 读像素，最终接受的详情面板才转换为 `Bitmap` 入 OCR 队列。
- `CELL_TIMING` 与 `--scan-benchmark` 新增 `frame_capture_ms`、`frame_to_bitmap_ms`、`bitmap_created_count`，并在 `scan.log` 记录 `captureFrameBackend=...`。
- DXGI raw BGRA 路径保留为显式环境变量实验；本机验证出现 Desktop Duplication `ReleaseFrame/Unmap` 与 access lost 稳定性风险，因此默认 `--capture-mode dxgi` 继续使用已验证的 `bitmap-fallback`，不把 raw frame 作为推荐默认。
- 修复扫描目录同秒碰撞：目录名改为毫秒时间戳 + process id + 短随机后缀；`scan-once-result.json` 只写入对应扫描目录，发布根目录不再生成临时 result 文件。
- 面板 probe 签名修复为统一坐标系，并保留“probe 不变但目标格选中态变化”兜底，用于相邻外观极近的详情面板；仍然不允许未看到变化时直接复用旧面板。
- 实机默认模式验证目录：`publish 1.0.20\Scans\2026-06-28-15-57-06-340-p7444-74dc`，`MaxItems=30`，`Completed=30`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、关键 acceptance 全 pass。
- 实机 GDI fast 验证目录：`publish 1.0.20\Scans\2026-06-28-15-48-51-387-p4d10-caf9`，`Completed=120`、重复导出 0、关键 acceptance 全 pass；`completed_per_sec=3.081`，略高于 1.0.19 GDI 的 `3.054/s`。
- 实机 DXGI fast 最终验证目录：`publish 1.0.20\Scans\2026-06-28-15-47-04-018-p75e8-c80c`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、`strict_one_way_scroll=pass`、`overlap_rows_complete=pass`；`completed_per_sec=3.406`，基本持平 1.0.19 DXGI 的 `3.410/s`。
- 慢 OCR 压力验证目录：`publish 1.0.20\Scans\2026-06-28-15-54-58-745-p67bc-f7a6`，关闭 Fast OCR 且 `ocr-workers=1/queue=4/intra-op=1` 后，`adaptiveThrottleMs_avg=205ms`、`adaptiveThrottleMs_max=300ms`，扫描自动降速且失败 0、重复 0。

## 2026-06-28 1.0.19 稳定边界内的采集端提速

- 面板等待继续保留 1.0.18 的安全边界：必须看到详情变化、稳定，并且 12 个 OCR ROI 全部可见；未看到变化仍拒绝复用旧面板。
- GDI 路径在等待变化阶段使用 `panelChangeProbeRect` 轻量截图，但签名仍按 12 个 ROI 位置采样，避免整块稀疏采样漏掉文字变化。
- DXGI 路径 warmup 后将稳定帧要求从 2 帧降到 1 帧；仍保留变化签名、ROI 完整和最低接受时间，用来避免回到 1.0.17 激进版本的旧面板重复问题。
- 单行滚动从“等待列表稳定后再验证”改为“轮询一行位移并等待该一行匹配稳定”，更早接受已经验证的一行滚动，同时继续阻断多行 overshoot。
- 实机 DXGI fast 验证目录：`publish 1.0.19\Scans\2026-06-28-03-02-41`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、关键 acceptance 全 pass；`completed_per_sec=3.410`，相对 1.0.18 DXGI `3.146` 提升约 `8.4%`。
- 1.0.19 仍略低于 1.0.17 DXGI 最终 `3.467/s`，但保留 1.0.18 的旧面板拒绝、12 ROI 完整和 OCR 背压策略；本轮优先选择稳定性。

## 2026-06-28 1.0.18 本轮自适应面板状态机与 OCR 背压

- 新增 `--adaptive-timing` 与 `--no-adaptive-timing`；`--fast-mode` 默认启用本轮自适应，普通模式可手动启用。GUI 增加“本轮自适应等待”，WebSocket payload 增加 `AdaptiveTiming`。
- 面板等待改为更明确的状态机：点击后必须看到 panel probe 相对上一件变化，再等稳定帧，并且 12 个 OCR ROI 全部可见后才接受；未看到变化时只重试点击，不复用旧面板。
- 新增运行内 `AdaptiveTimingState`，前 12 件记录面板变化、稳定、ROI 完整和帧循环耗时；warmup 后只在“变化+稳定+ROI完整”已经满足的前提下收缩安全等待，不写长期机器画像。
- 保留 bounded OCR 队列并新增 `AdaptiveOcrThrottle`：OCR backlog 连续高于队列容量 60% 时，在下一次点击前增加小延迟；低于 25% 后逐步撤销。
- `CELL_TIMING` 增加 `adaptiveThrottleMs`、`ocrBacklogBeforeEnqueue`、`adaptivePanelMinMs`、`adaptivePanelSamples` 和 `adaptivePanelReason`；`--scan-benchmark` 增加 `adaptive_throttle_ms_*`、`ocr_backlog_before_enqueue_*` 和 `adaptive_panel_min_ms_*`。

## 2026-06-28 1.0.17 DXGI 截图后端与采集端诊断

- 新增 `CaptureMode=gdi|dxgi`、CLI `--capture-mode gdi|dxgi`、GUI 高级设置“截图后端”和 WebSocket `CaptureMode` 字段；默认仍使用 GDI，DXGI 仅作为显式实验路径。
- `GameWindow` 改为通过 capture source 截图；GDI 保持原 `CopyFromScreen` 行为，DXGI 使用 Desktop Duplication 按窗口坐标裁剪。DXGI 初始化失败、取帧失败或尺寸异常时自动回退 GDI 并写入 `scan.log`。
- `CELL_TIMING` 增加 `captureMs`、`signatureMs`、`visibleRoiMs`、`frameLoopMs`；滚动日志增加 `ROW_SCROLL_TIMING`，记录 `scroll_tick_wait_ms`、`list_stable_ms`、`row_signature_ms` 和 `post_scroll_viewport_ms`。
- `--scan-benchmark` 新增 `capture_ms_*`、`panel_signature_ms_*`、`visible_roi_ms_*`、`frame_loop_ms_*`、`scroll_list_stable_ms_*`、`row_signature_ms_*` 等采集端分解指标。
- 减少重复截图：行签名和列表视口签名尽量复用同一次截图；同一面板等待循环内记录每帧截图、签名和 ROI 检查成本。旧面板保护仍要求看到变化且 ROI 完整/稳定。
- 实机 GDI fast 验证目录：`publish 1.0.17\Scans\2026-06-28-01-17-31`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0，`completed_per_sec=3.320`，相对 1.0.16 提升约 `5.6%`。
- 实机 DXGI fast 最终验证目录：`publish 1.0.17\Scans\2026-06-28-01-28-45`，`Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、滚动验收全 pass；`completed_per_sec=3.467`，相对 1.0.16 的 `3.144` 提升约 `10.3%`。
- 一轮更激进 DXGI 参数曾达到 `4.213/s`，但出现重复导出，判定为不安全结果并拒绝；最终版本为 DXGI 面板变化接受保留额外安全下限，避免旧面板重复读取。
- 本轮未达到 `+15%` 接受目标；benchmark 显示 OCR 和截图已不是主瓶颈，剩余慢点主要在 `scroll_list_stable_ms_avg=349ms` 与滚动后首格等待。

## 2026-06-27 1.0.16 Fast Mode 与采集端拆解

- 新增 `--fast-mode`：自动启用 fast scan profile 与 Fast OCR assist；启动时校验 `Data/ocr_fast_templates.json` 必须为 v3+ 且存在已启用字段，否则回退普通模式并写入 `scan.log`。
- 新增独立扫描 profile `ZZZ背包驱动盘-16比9-fast`，普通 profile 保持安全默认；fast profile 当前参数为 `loadPollMs=10`、`panelSettleDelayMs=30`、`panelChangedMinimumAcceptMs=60`、`scrollTickDelayMs=60`。
- `loadPollMs` 的代码下限从 15ms 放宽到 5ms；新增 `panelChangedMinimumAcceptMs` profile 参数，普通模式保持 120ms。
- `--scan-benchmark` 新增 `panel_change_ms`、`panel_roi_ms`、`panel_stable_ms`、`after_scroll_extra_ms` 和 `capture_limited`，用于区分采集端与 OCR 端瓶颈。
- 新增 `--ocr-fast-feature-eval <shadow-parent>`，比较 v3 与候选 v4 特征在 `name/subStat4` 上的跨轮误接受和接受率；`--ocr-fast-calibrate` 可显式传入 `--feature v4`。
- 随包 `ocr_fast_templates.json` 改为 v4 特征：`level/mainStat/subStat1/subStat2/subStat3/subStat4` 启用，`name` 仍因跨轮接受率不足保持禁用。
- 实机 `MaxItems=120 --fast-mode` 验证目录：`publish 1.0.16\Scans\2026-06-27-23-02-38`。结果 `Completed=120`、`Failed=0`、`IncompleteRoi=0`、重复导出 0、滚动验收全 pass；`completed_per_sec=3.144`，相对 1.0.15 assist `2.828` 提升约 `11.2%`。
- 本轮未达到 `3.3/s` 或 `+15%` 目标；benchmark 显示 `capture_limited=true`，剩余瓶颈在 GDI 截图和详情面板探针循环，OCR backlog 已降到 max 1。

## 2026-06-27 1.0.15 Fast OCR 自动校准与 v3 特征

- 新增 `--ocr-fast-calibrate <shadow-parent> --output <index.json>`：读取多轮 shadow 数据，生成字段级 `AssistEnabled`、`MinScore`、`MinMargin` 策略，并写出 `ocr_fast_eval.csv`、`ocr_fast_confusion.csv`、`ocr_fast_calibration.csv`。
- `ocr_fast_templates.json` schema 升到 v3，新增 `ahash-dhash-16x16-v3` 特征；旧 v1/v2 索引继续按原 `ahash-16x16-grayscale-v1` 兼容读取。
- 校准器少于两轮 shadow 数据时会保持 assist 全禁用；字段启用必须满足跨轮零误接受，`name` 还需要接受率达到 95%。
- `--ocr-fast-eval` 和 `--ocr-fast-cross-validate` 会额外输出 `ocr_fast_confusion.csv`，用于定位误接受和常见拒绝标签。
- `--scan-benchmark` 新增 `fast_accepted_per_item`、`fast_rejected_per_item`、`ppocr_roi_per_item`、`fast_match_ms_per_item`，可直接判断 assist 是否减少 PP-OCR ROI。
- 已用 3 轮 `MaxItems=120` shadow 样本校准并实测 assist：启用 `level/mainStat/subStat1/subStat2/subStat3`，120 件扫描失败 0、重复 0，`ppocr_roi_per_item` 从 12 降到 7，`ocr_total_ms_per_item_avg` 从约 927ms 降到约 271ms。

## 2026-06-27 1.0.14 Fast OCR 可回退辅助识别

- Fast OCR 模板索引升级到 v2，记录每个字段的 `minScore`、`minMargin`、模板数和标签数，并保留 v1 索引读取兼容。
- 新增 `--ocr-fast-eval <index.json> <shadow-dir-or-parent>` 与 `--ocr-fast-cross-validate <shadow-parent>`，可输出 `ocr_fast_eval.csv` 并统计字段级接受率、匹配率、误接受数和耗时。
- 新增 `--ocr-fast-assist --ocr-fast-index <index.json>`：`level/mainStat/subStat1..4` 可先走模板快路径，不支持、禁用或未达阈值的 ROI 会回退 PP-OCR；`name` 默认仍只参与 shadow/eval。
- 扫描目录新增 `ocr_fast_assist.csv`，`ocr_diagnostics.csv` 追加 `fast_match_ms`、`fast_accepted_count`、`fast_rejected_count` 和 `ppocr_roi_count`，用于确认 PP-OCR 实际 ROI 是否下降。

## 2026-06-26 1.0.3 重叠签名扫描实验版

- 默认遍历切换为 `OverlapSignaturePage`：按仓库数量计算逻辑行，用已扫逻辑行集合去重；首屏扫描视觉第 1/2/3 行，中间扫描未读的新行，底部补扫视觉第 4 行。
- 滚动仍保持单向向下，不做自动反向上翻恢复；如果游戏偶发把一次滚轮结算成两行，重叠模式会接受前进距离，并在下一屏补扫落到视觉第 2 行的未读逻辑行，避免为了回退而制造重复读取。
- 新增 `OVERLAP_VIEWPORT`、`OVERLAP_ROW_CANDIDATE`、`OVERLAP_ROW_SCANNED`、`OVERLAP_SCROLL_ACCEPTED` 日志，方便回放每一屏扫描了哪些逻辑行。
- `--scan-benchmark` 增加重叠页计数和 `acceptance.overlap_rows_complete`，并允许重叠补扫场景下合理的视觉第 2 行点击，不再把它误报为不安全。
- GUI 遍历模式增加“重叠签名扫描”，默认“按配置”指向新的 profile 默认值；`SafeBandViewport`、`CalibratedPage`、`LegacyThirdRow` 仍保留为高级诊断选项。

## 2026-06-25 1.0.2 扫描提速与基准验收

- 实机验证发现 `OcrBatchSize=4` 会让 PP-OCRv5 单件耗时从约 405ms 退化到约 549ms，并造成 OCR 队列反压；默认批量回退为 `OcrBatchSize=1`。
- 默认 OCR 改为自动最多 2 worker、`OcrQueueCapacity=48`、`OcrIntraOpThreads=3`；`2026-06-26` 实机复测显示 2 worker 能解除队列反压，3 worker 虽更快收尾但出现滚动越行风险，暂不写成默认。
- 扫描 profile 加入显式等待下限：`minPanelSettleDelayMs`、`minPanelUnchangedFallbackMs`、`listStableTimeoutMs`、`minListStableTimeoutMs`、`minScrollTickDelayMs`。默认轮询降到 `loadPollMs=15`，面板盲等降到 `panelSettleDelayMs=40`，逐行滚动等待保留 `scrollTickDelayMs=50`，避免一行滚动验证失败。
- `--scan-once` 会自动执行一次扫描并写出发布目录和扫描目录内的 `scan-once-result.json`，供无人值守 benchmark 判断成功、失败和输出目录。
- 品质检测新增固定点和中心小范围 fast path，只有失败时才回退到原 73x73 区域扫描；日志中的 `Probe` 会写出 `fullScan=True/False`。
- `--scan-benchmark` 增加 `cell_timing_per_sec`、`queued_per_sec`、`completed_per_sec`、`ocr_total_ms_per_item`、`unsafe_visual_row2_clicks`、滚动越行/恢复计数、导出一致性和 acceptance/risk 字段，用于对比 `publish 1.0.1` 与 `publish 1.0.2`。
- 详情面板探针继续复用当前详情截图计算签名，不额外截图；变化检测仍看大面板加关键 ROI，稳定检测改为只看全部 OCR ROI，减少背景动画导致的等待，同时不放宽旧面板保护。`CELL_TIMING` 会追加 `panelFrames/changeMs/fullRoiMs/stableMs/accept`，用于定位慢格子是在等面板变化、ROI 出现还是稳定帧。
- `SafeBandViewport` 收紧点击约束：首屏边界允许视觉第 1、2、3 行，中途必须固定读视觉第 3 行；如果滚动验证发现一次推进多行并会让目标落到视觉第 2 行，会先做一次反向滚轮并验证回到目标顶部，回退失败才停止本轮扫描，避免重复读取。
- 安全带逐行滚动改为更小的 `scrollTickDelta=-30` 分步推进，单 tick 后按 YAS 风格等待 80ms 再验证行签名，降低游戏一次结算两行的概率。
- 默认关闭自动反向恢复：`allowScrollRecovery=false`。如果游戏仍把某个小 tick 结算成两行，扫描写出 `ROW_SCROLL_STRICT_STOP` 并停止，不再自动上翻，避免回滚动作导致重复读取或漏扫。

## 2026-05-19

- 创建新子项目 `ZZZ-Scanner.Next`。
- 新增 WinForms GUI。
- 新增管理员 manifest 和程序入口自提升逻辑。
- 新增非侵入式窗口控制层 `GameWindow`。
- 新增异步扫描/OCR 队列 `ScanController`。
- 复用 PP-OCRv5 ONNX 识别模型，并重写 OCR 输入预处理。
- 将驱动盘名称、词条规则、数值范围整理到 `Data/*.json`。
- 支持读取前 N 个、S/A/B 品质过滤、保存调试截图。
- 新增 docs 目录，记录数据来源、架构、测试和变更。
- 将新项目加入根目录解决方案 `ZZZ-Scanner.sln`。

## 2026-05-19 DPI 修正

- 根据预览图发现副屏缩放导致坐标按 0.8 被虚拟化，详情区截图裁到了左侧格子区域。
- 启用 WinForms `PerMonitorV2` 高 DPI 模式。
- 启动时调用 `SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`，失败时回退到 `SetProcessDPIAware`。
- 新增 DWM 物理窗口边界校准：当 Win32 客户区被 DPI 虚拟化时，按 DWM 实际边界恢复物理像素坐标。
- GUI 日志现在会输出游戏客户区尺寸和 DPI，便于确认坐标是否仍被虚拟化。
- 由于旧 `publish` 目录中的 EXE 正在运行，修正版已发布到 `publish-dpi-fix`。
- 确认 `publish` 与 `publish-dpi-fix` 现均已覆盖为同一份 DPI 修正版。
- GUI 标题更新为 `ZZZ Scanner Next - DPI Fix`，启动日志会显示运行目录，避免误跑旧版本。

## 2026-05-19 副属性检测修正

- 修复副属性全为空的问题。
- 原因：副属性条背景是 `RGB(22,22,22)`，旧逻辑用容差 26 判断“是否接近黑色背景”，把副属性条误判为空背景。
- 新逻辑改为检测采样点是否接近属性条背景色，并将行存在性容差限制为 10，避免黑底和深灰条混淆。
- 用正确预览图离线验证，详情面板 ROI 数量从 4 个恢复为 12 个。

## 2026-05-19 UI 与速度调整

- 在 UI 中明确标注读取上限 `0=不限制`，实际扫描逻辑保持 0 为无限制。
- 将“保存调试截图”改为“临时显示调试截图”，扫描中只在界面显示最新详情图，不再保存 `*.panel.png`。
- “预览详情区”改为在界面中临时显示，不再保存 `preview-detail-panel.png`。
- 识别失败时只写入错误文本，不再保存错误截图。
- 默认扫描节奏提速：点击等待 90ms -> 55ms，加载轮询 50ms -> 35ms，翻页等待 420ms -> 320ms。

## 2026-05-19 OCR 批处理提速与错误诊断

- OCR 从单个驱动盘一次 ONNX 调用改为最多 8 个驱动盘合批识别。
- OCR 前处理通道复制从逐像素 `Mat.At<float>` 改为连续内存块复制。
- OCR 解码现在会为每个 ROI 保留一个结果，即使该区域识别为空，也不会造成后续字段错位。
- 普通扫描日志改为每 25 个盘输出一次，减少 UI 文本刷新开销。
- 默认扫描节奏再次提速：点击等待 55ms -> 35ms，加载轮询 35ms -> 25ms，翻页等待 320ms -> 260ms。
- 失败日志现在包含序号、品质、ROI 数、每个 OCR 字段文本和异常堆栈，便于追踪具体失败字段。

## 2026-05-19 回顶与停止热键

- 扫描开始前不再只点击滚动条顶部，而是先将鼠标移动到列表区域，连续向上滚轮 45 次，再点击滚动条顶部兜底，避免用户列表不在顶部导致漏扫。
- `scan_profiles.json` 新增 `listWheelArea`、`resetToTopWheelTicks`、`resetToTopWheelDelayMs`。
- 新增全局停止热键 `Ctrl+Shift+C`，扫描器不在前台时也能取消扫描。
- 停止按钮文案更新为 `停止（Ctrl+Shift+C）`，启动日志会提示热键。

## 2026-05-19 稳定读取修正

- 修复快速扫描时大量副属性为空的问题。
- 不再用副属性行检测结果裁剪 OCR 数量；每个驱动盘固定 OCR 全部 12 个文本区域，空区域由清洗阶段跳过。
- 截图前新增详情面板稳定等待：至少看到第一条副属性名称和值，并且区域数量连续稳定后才截图。
- 如果已经看到全部 12 个文本区域，连续两帧稳定即可继续；如果只看到部分副属性，则要求连续四帧稳定，避免刚加载一半就截图。
- `clickDelayMs` 从 35ms 回调到 60ms，配合批量 OCR 保持总体速度，同时优先保证详情面板刷新完成。
- OCR 批处理异常现在会按批次内每个驱动盘分别记 error，不会让消费者线程直接退出。

## 2026-05-19 遍历漏扫修正（已废弃方案）

- 找到 600 多个驱动盘只扫到 200 多就结束的原因：游戏滚轮一次会移动约 2-3 行，但旧算法每滚一次只扫描第 3 行，导致大量行被跳过，并提前滚到底。
- 重写列表遍历为“按可视页扫描”：每页捕获 4 行视觉指纹，与上一页比较重叠行，只扫描滚动后新出现的行。
- 如果滚动后没有发现新行，会记录 `未发现新行` 并结束，避免卡在底部重复扫描。
- 扫描日志现在会输出 `扫描第 N 页：新行 X-4`，方便判断滚轮步幅和重叠行识别情况。

## 2026-05-19 稳定遍历二次修正（已废弃方案）

- 复测发现行指纹方案会把外观完全相同的多行驱动盘误判为“已经扫过”，导致 207/252 条附近正常结束。
- 新增每次扫描目录下的 `scan.log`，记录回顶、数量 OCR、滚动定位、品质探测和结束原因。
- 扫描开始后先 OCR 顶部 `驱动仓库 [ 当前/上限 ]`，用当前数量计算总行数和滚动条步进。
- 列表遍历改为拖拽右侧滚动条到精确行位置，不再依赖游戏滚轮步幅。
- 回顶改为慢速滚轮循环，并用滚动条顶部颜色确认已到顶；避免滚轮事件过快被游戏丢弃。
- 品质探测由单点 RGB 改为小区域色相/RGB 混合判断，避免暗黄、阴影和选中态把 S 级卡片误判为空。
- 低等级驱动盘遇到“套装效果”时停止读取副属性，不再把它当作副属性写 error。

## 2026-05-19 原项目遍历机制迁移

- 对照旧项目 `ZZZ-Scanner/Helpers/GameHelper.cs` 后确认：旧项目并不是首屏 4 行后持续扫描第 4 行，而是“第 1、2、4 行正常扫描，第 3 行通过翻页扫描”。
- Next 正式遍历改为旧项目同款稳定策略：先扫描可视第 1、2、3 行；第 3 行后如果未到底，发送 `MouseWheel(-120)`，等待 500ms，再继续扫描可视第 3 行；滚动条到底后才扫描最终可视第 4 行。
- 移除正式扫描路径中的滚动条拖拽和 `delta=-1` 逐行方案，避免第 4 行附近横向扫描时列表继续移动。
- 清理源码中已废弃的滚动条精确定位和行指纹辅助函数，避免后续维护误用旧方案。
- `scan.log` 现在记录 `Traversal: legacy third-row mode`、`Pass ... visual row ...` 和 `Scroll: legacy third-row wheel ... delta=-120`，用于确认实际遍历顺序。

## 2026-05-26 YAS 风格稳定扫描重构

- 只参考 YAS 的架构和策略，不复制 GPL-2.0-or-later 源码。
- 新增默认 `CalibratedPage` 遍历：按仓库数量计算 `4 x 9` 页面、尾行列数和下一页起始可视行。
- 保留 `LegacyThirdRow` 兼容模式，并在 GUI 中增加遍历模式选择。
- profile 新增 `listGridRect`、`rowAlignProbeRect`、`panelChangeProbeRect`、`scrollTickDelta`、`scrollMaxTicksPerRow`、`calibrationRows`。
- 滚动改为签名驱动：检测列表区域是否移动，等待稳定，并记录平均一行滚轮 tick。
- 详情截图前新增 panel checksum：点击前记录签名，点击后等待签名变化和稳定，避免读旧详情。
- OCR 清洗成功后生成去序号 fingerprint；连续重复达到一行时触发保护并取消扫描。
- 新增 `docs/yas-study.md` 记录参考项目经验、未直接移植原因和已吸收的设计。

## 2026-05-26 安全带扫描重构

- 根据实机发现，驱动盘列表存在边缘补位滚动规则：点击视觉第 1 行会向上补位，点击视觉第 4 行会向下补位。
- 新默认遍历模式改为 `SafeBandViewport`，只在中间区域点击，顶部第 1 行和底部第 4 行仅在边界状态下允许。
- 扫描逻辑改为维护当前可视顶部逻辑行，逐行滚动并做一行位移验证，不再使用整页 4 行连续 fast scroll 作为默认方案。
- `scan.log` 新增 `VIEWPORT_STATE`、`EDGE_CLICK_BLOCKED`、`ROW_SCROLL_START`、`ROW_SCROLL_TICK`、`ROW_SCROLL_VERIFY`、`ROW_SCROLL_DONE`、`ROW_SCROLL_FAIL`，便于定位每一次滚动和点击。

## 2026-05-26 后台 OCR 并行提速

- OCR 从单 worker 改为多 worker；每个 worker 独立持有 ONNX session，截图生产线程可以继续向后扫描。
- 队列容量从 48 扩大为默认 192，减少 OCR 追不上时对点击/截图线程的阻塞。
- 新增 GUI 设置 `OCR线程（0=自动）`；自动模式在 6 核以上使用 2 个 worker，12 核以上使用 3 个 worker。
- 多 worker 结果按序号归并后再更新 GUI、导出和重复保护，避免乱序识别影响稳定性。

## 2026-05-26 点击节奏与结果表格优化

- 品质探测改为点击前执行；未被用户选择的品质不再点击详情面板，过滤扫描更快，也避免无意义的面板等待。
- 移除每个格子点击后的固定 `clickDelayMs` 等待，改为直接进入详情面板稳定检测，精度仍由 panel checksum 和 ROI 稳定条件保证。
- 品质探测失败重试等待从 90ms 降为 25ms。
- GUI 结果表格新增结果后自动选中并滚动到最新行，便于观察当前识别到的盘。

## 2026-06-08 稳健提速

- 详情面板变更检测从单个背景探针改为多探针签名，覆盖面板探针、名称、等级和主属性 ROI，减少背景未变化导致的空等。
- `panelUnchangedFallbackMs` 从 180ms 调整为 110ms；fallback 仍要求文本 ROI 稳定且可读，不放宽副属性可见性判断。
- 扫描线程改为将 `Bitmap` 直接入队，`Bitmap` 到 `Mat` 的转换移动到 OCR worker，降低采集线程阻塞。
- `scan.log` 新增 `CELL_TIMING` 事件，`ocr_diagnostics.csv` 新增 `bitmap_to_mat_ms` 列，用于继续定位速度瓶颈。

## 2026-06-08 扫描基准工具

- 新增命令行 `--scan-benchmark <scan-dir> [baseline-scan-dir]`，可直接汇总扫描目录中的点击、滚动、面板等待、OCR、资源和错误指标。
- 支持旧扫描日志；缺少 `CELL_TIMING` 或 `bitmap_to_mat_ms` 时输出 `N/A`，仍可计算点击间隔和 OCR 总耗时。
- 传入 baseline 目录时输出关键指标百分比差异，便于比较每轮调参是否真的变快。
