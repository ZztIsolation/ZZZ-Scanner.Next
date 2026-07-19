# ZZZ Scanner Next

[English](README.md) | 简体中文

ZZZ Scanner Next 是一个用于扫描和导出《绝区零》驱动盘仓库的 Windows
工具。它会将游戏窗口切换到前台、点击驱动盘格子、滚动列表、截取可见界面，
并在本机使用 PP-OCRv5 识别文字。它不会读取游戏内存、注入代码、安装游戏插件，
也不会 Hook 游戏进程。

本项目包含：

- WinForms 桌面扫描器；
- 供网页计算器调用的 NativeAOT Helper；
- 从同一份扫描器源码同时生成框架依赖包和自包含包的发布流水线。

## 目录

- [支持环境](#支持环境)
- [选择安装方式](#选择安装方式)
- [推荐方式：计算器和 Helper](#推荐方式计算器和-helper)
- [手动安装](#手动安装)
- [扫描前准备游戏](#扫描前准备游戏)
- [使用桌面扫描器](#使用桌面扫描器)
- [输出文件](#输出文件)
- [权限与 UAC](#权限与-uac)
- [故障排查](#故障排查)
- [已知限制](#已知限制)
- [命令行用法](#命令行用法)
- [从源码构建](#从源码构建)
- [安全与隐私](#安全与隐私)

## 支持环境

| 项目 | 正式支持范围 |
| --- | --- |
| 操作系统 | Windows 10 1809 / Build 17763 及以上，Windows 11 |
| 系统架构 | x64 Windows 和 x64 程序包 |
| Windows 版本 | 普通版、N 版和 LTSC 均属于发布测试目标 |
| 游戏客户端 | 本地 PC 客户端、云·绝区零 |
| 游戏语言 | 简体中文界面 |
| 主要布局 | 当前简体中文驱动盘仓库界面，主要面向 16:9 |
| OCR | 程序内置 PP-OCRv5 ONNX 模型，识别在本机完成 |

本期不支持：

- Windows 7、Windows 8/8.1，以及 Build 17763 以前的 Windows 10；
- 32 位 Windows、x86 进程和 ARM64 Windows；
- 非简体中文的游戏界面；
- 手机端、主机端、Wine/Proton，以及未适配的串流画面布局；
- 替换仓库 UI 的模组，或未来游戏更新后尚未适配的新界面。

通过 Helper 启动时，Helper 会在下载前检查 Windows Build 和系统架构。
手动解压扫描器会绕过这一步预检，但不会让不受支持的系统变得兼容。

### 已验证的视觉配置

严格 Fast OCR 配置目前验证过以下分辨率：

- 本地客户端：<code>1280x720</code>、<code>1600x900</code>、
  <code>1920x1080</code>；
- 云客户端：<code>1440x808</code>、<code>1592x896</code>、
  <code>1920x1080</code>。

其他 16:9 分辨率可能可以工作，因为坐标会按游戏客户区等比例缩放。程序不会
把未知布局强行套用到一个相近的 Fast OCR 配置，而是回退到 PP-OCR，以免用速度
换取错误结果。非 16:9 布局、自定义 UI 缩放、HDR、色彩滤镜以及未来 UI 变更均
不承诺一定可用。

## 选择安装方式

### 方式 A：计算器 + Helper

推荐普通用户使用。Helper 会：

- 检查 Windows 版本、架构、缓存目录写权限和可用磁盘空间；
- 检测电脑上是否已有 .NET 8 Windows Desktop Runtime；
- 只下载适合当前电脑的扫描器包；
- 校验包大小、SHA-256、ZIP 路径、入口程序和安装后的每个应用文件；
- 对中断的下载执行续传，并依次尝试发布清单中的镜像；
- 修复损坏缓存，并把结构化错误和可执行操作返回给计算器。

Helper 本身采用 NativeAOT 自包含发布，所以即使电脑没有安装 .NET 8，
Helper 仍然可以启动。

### 方式 B：手动下载扫描器包

需要独立 GUI 或命令行工具时，可以直接下载以下任一包：

| 程序包 | 适用情况 | .NET 要求 |
| --- | --- | --- |
| <code>ZZZ-Scanner.Next-win-x64-fdd.zip</code> | 已确认安装 x64 <code>Microsoft.WindowsDesktop.App 8.x</code> | 必须已有该运行库 |
| <code>ZZZ-Scanner.Next-win-x64-self-contained.zip</code> | 没有 .NET 8，或者无法确认 | 不需要预装 .NET |

两个包中的扫描器、模型、数据、ONNX Runtime 和本地 VC 运行库完全相同。
自包含包更大，是因为它额外携带了 .NET 桌面运行时。

如果框架依赖包提示缺少 .NET，请改用自包含包。为了使用本扫描器，没有必要
专门安装或修改系统级 .NET。

## 推荐方式：计算器和 Helper

### 1. 下载 Helper

从官方 [GitHub Releases](https://github.com/ZztIsolation/ZZZ-Scanner.Next/releases)
下载 <code>ZZZ-Scanner-Helper.exe</code>。请把它放在长期存在且当前用户可写的
目录中。不要直接从 ZIP 压缩包、邮件附件预览或稍后会被清理的临时目录中运行。

Helper 无需 .NET 8，也不会安装 .NET、VC++ Redistributable 或修改系统级
运行库。

### 2. 校验并首次启动

当前发布的二进制文件尚未进行代码签名。SmartScreen、杀毒软件或企业安全策略
可能在 Helper 启动前直接拦截它。此时因为 Helper 进程根本没有运行，它无法显示
自身诊断信息。

只应放行从官方 Release 下载的文件。可以使用 PowerShell 校验 SHA-256：

~~~powershell
Get-FileHash .\ZZZ-Scanner-Helper.exe -Algorithm SHA256
~~~

将结果与 Release 页面提供的哈希比较。不要全局关闭安全软件，也不要从第三方
“DLL 下载站”补文件。

双击运行 Helper 一次。它会为当前 Windows 用户注册
<code>zzz-scanner://</code> 协议，并且只监听
<code>127.0.0.1:22355</code>。如果之后移动了 Helper EXE，请从新位置再运行
一次，使协议注册指向新路径。同一时间只能有一个 Helper 占用 22355 端口。

如果浏览器或系统询问是否允许网页打开 <code>zzz-scanner://</code>，请在确定
操作来自受支持的计算器页面后允许。

### 3. 从计算器开始扫描

1. 启动游戏并完成登录。
2. 将游戏语言设置为简体中文。
3. 打开仓库，并进入驱动盘列表。
4. 在受支持的计算器中打开扫描器页面。
5. 正确选择本地客户端或云客户端。
6. 点击开始扫描，并允许浏览器打开 <code>zzz-scanner://</code>。
7. 扫描结束前保持游戏可见，不要操作鼠标、滚轮、仓库标签、排序、筛选和窗口。

浏览器会先从 Helper 获取一次性令牌，随后通过受令牌保护的本机 WebSocket
通信。Helper 不会接受任意网站直接发起的扫描请求。

### 4. Helper 如何选择程序包

Helper 会同时检查：

- Windows 注册的 .NET 安装位置；
- .NET 的标准安装目录；
- <code>dotnet --list-runtimes</code> 的结果。

选择规则如下：

- 明确检测到 <code>Microsoft.WindowsDesktop.App 8.x</code>：选择体积更小的
  <code>win-x64-fdd</code>；
- 没有检测到或检测结果不确定：选择
  <code>win-x64-self-contained</code>；
- 下载 FDD 后、启动前发现运行库已经消失：只自动回退一次自包含包。

检测不确定时优先保证能启动，而不是冒险选择小包。Helper 不会弹出 .NET
安装器，也不会改变电脑上已有的 .NET 安装。

当前磁盘预检大约需要：

- FDD：160 MiB；
- 自包含包：358 MiB。

这个数值包含压缩包、完整展开目录和 100 MiB 安全余量。计算器会收到并显示
本次操作实际需要的字节数。空间不足时会在下载前明确提示，而不是下载到一半才
失败。

### 5. 缓存、更新与自动修复

Helper 默认使用以下目录：

~~~text
%LOCALAPPDATA%\ZZZScannerNext\
  helper\
    ZZZ-Scanner-Helper.exe
  packages\
    仅保留下载中的临时文件
  runtime\
    <version>\
      <packageId>\
  outputs\
    最近一次成功与最近一次失败产物
  logs\
    helper-YYYYMMDD.log
~~~

Helper 1.2.1 会安装到固定的当前用户 helper 目录，并把浏览器协议注册到这个
路径。后续 Helper 在该路径内执行事务式自更新。Helper 1.1.x 因为尚无更新协议，
需要最后下载一次：运行 1.2.1 安装器并确认一次接管，安装器会安全关闭唯一验证的
旧 Helper、安装托管副本并自动重启。

schema v3 manifest 列出每个 runtime 文件的大小和 SHA-256，因此 Helper 在安装
并完整校验后即可删除 ZIP，后续复用时仍能逐文件验真。新 runtime 只有完成子进程
WebSocket 握手后才会写入活动版本收据，随后删除所有非活动 runtime。更新过程中
会短暂并存新旧两版，避免新版本无法启动时破坏当前可用版本。

托管扫描产物不再放进版本目录。首次清理会迁移旧
<code>runtime/**/Scans</code>，之后只保留最近一次成功和最近一次失败产物。计算器
设置页会显示精确占用，并可重复执行清理而不卸载当前 Scanner。

当计算器提示缓存损坏时，优先使用“修复扫描器”。这会重新下载当前选中的包并
完整校验，不需要用户猜测应该手动删除哪个 DLL。

## 手动安装

### 1. 完整解压程序包

把整个 ZIP 解压到普通、长期存在且当前用户可写的目录。请不要：

- 直接在压缩包内双击 EXE；
- 只复制 EXE，遗漏 <code>Data</code>、<code>Resources</code> 或 DLL；
- 把不同版本的文件混在同一个目录；
- 用来源不明的 DLL 覆盖程序自带文件；
- 把程序放在需要管理员权限才能写入的受保护目录后，再期待它正常保存结果。

<code>ZZZ-Scanner.Next.exe</code>、<code>Data</code>、
<code>Resources\models</code>、<code>onnxruntime.dll</code> 和随包携带的
VC 运行库必须保持在同一套发布目录结构中。

### 2. 启动桌面界面

运行 <code>ZZZ-Scanner.Next.exe</code>。扫描器默认以当前用户权限启动，
不会无条件请求管理员权限。

桌面版默认设置：

- 进程名：<code>ZenlessZoneZero</code>；
- 读取上限：<code>0</code>，表示不设置明确件数上限；
- 稀有度：勾选 S，默认不勾选 A；
- 仅扫描 15 级驱动盘：开启；
- 扫描前将游戏切到前台：开启；
- 高速 OCR：开启；
- 截图后端：GDI；
- 遍历方式：使用配置默认值，目前为重叠签名扫描；
- 已验证极速模式：默认关闭，需要用户主动选择。

云·绝区零使用以下进程名：

~~~text
Zenless Zone Zero Cloud
~~~

进程名必须填写 Windows 中实际运行的游戏进程，而不是启动器或更新器。

## 使用桌面扫描器

### 1. 检测游戏窗口

点击“检测窗口 / Detect Window”。

- 检测成功表示找到了所填进程及可用的主窗口；
- 检测失败通常表示进程名错误、游戏未启动、本地/云端选错，或游戏窗口尚未就绪；
- 如果游戏以更高完整性级别运行，普通权限扫描器不能控制它，参见
  [权限与 UAC](#权限与-uac)。

检测成功并不代表当前仓库页面、语言和分辨率一定正确，正式扫描前仍需完成下方
准备步骤。

### 2. 开始扫描前的安全检查

点击“开始扫描 / Start Scan”之前：

- 打开驱动盘仓库；
- 保持游戏窗口可见且未最小化；
- 关闭覆盖驱动盘网格或详情面板的悬浮窗和 Overlay；
- 不要临时改变显示缩放、HDR、游戏分辨率、UI 缩放、排序和筛选；
- 确认列表中首个驱动盘已处于正常可选状态；
- 准备在扫描期间停止使用鼠标和键盘。

扫描器会主动控制鼠标和前台窗口。在扫描期间使用电脑，可能选择错误驱动盘、
滚动到错误位置，或者触发安全停止。

需要中止时使用“停止 / Stop”。取消后的输出目录中仍可能保留日志和诊断文件，
这是为了帮助定位停止位置，并不表示导出已经完整。

### 3. 基础设置说明

| 设置 | 含义 | 建议 |
| --- | --- | --- |
| 进程名 | 要查找的 Windows 游戏进程 | 本地为 <code>ZenlessZoneZero</code>；云端为 <code>Zenless Zone Zero Cloud</code> |
| 读取上限 | 最多抓取多少件；0 表示不设明确上限 | 首次试扫用 30，验证用 120，正常完整导入用 0 |
| S / A | GUI 中的稀有度过滤 | 计算器通常只需要 S |
| 仅 15 级 | 遇到第一件非 15 级驱动盘时正常停止 | 普通计算器导入建议保持开启 |
| 切到前台 | 输入和截图前激活游戏 | 通常保持开启 |

请先在游戏中合理排序，让需要导入的 15 级驱动盘出现在低等级驱动盘之前。
开启“仅 15 级”后，扫描器在第一件非 15 级驱动盘处停止属于预期行为，并非
扫描崩溃。对应信息会写入 <code>*.non15.txt</code>。

关闭“仅 15 级”属于实验功能。低等级驱动盘可能拥有更少的副词条行，完整扫描
时可能无法通过 ROI 完整性检查。目前它不是常规计算器导入流程的正式支持路径。

### 4. 高级设置说明

第一次使用时建议保持默认值，先完成 30 件试扫。

- “已验证极速模式”会启用已验证视觉配置、严格布局路由、Fast OCR 辅助、
  单行提前接受、自适应完整 ROI 面板接受，以及可恢复的重叠冲突处理。
  模板不匹配时会安全回退，而不是继续套用错误模板。
- GDI 更保守，适合作为首选兼容后端。DXGI 可能更快，但初始化、取帧或显示器
  匹配失败时会自动回退到 GDI。
- 调试截图和 OCR shadow 数据集可能生成大量本地文件，只应在排错或模型维护时
  开启。
- OCR Worker、Batch、Queue 和 IntraOp 会影响吞吐量。过于激进的配置可能增加
  CPU/内存占用，并扰乱游戏 UI 操作时序。
- 面板等待、滚动等待和实验性时序参数主要用于可复现基准测试，不是适合所有
  电脑的通用加速建议。
- 如果追求速度，应先在同一分辨率完成 30 件和 120 件验证，再比较总耗时、
  OCR P95、失败数、重复数和导出内容，不能只看单次扫描“感觉更快”。

## 扫描前准备游戏

每次扫描前建议按顺序确认：

1. 游戏使用简体中文界面。
2. 已打开仓库中的驱动盘列表。
3. 使用受支持的本地或云端窗口布局。
4. Windows 缩放和游戏分辨率在扫描过程中保持稳定。
5. 仓库网格和详情面板没有被其他窗口、通知或 Overlay 遮挡。
6. 排序与筛选符合预期，15 级驱动盘位于低等级驱动盘之前。
7. 游戏窗口未最小化，桌面未锁定，远程会话没有断开。
8. 在游戏更新、显卡驱动更新、分辨率变更，或本地/云端切换后，先执行 30 件
   试扫。

如果试扫出现失败项、重复项、ROI 不完整、意外的视觉配置回退或数量不正确，
请先检查 <code>scan.log</code>，不要直接进行完整导入。

## 输出文件

每次扫描都会在扫描器 EXE 同级目录下建立独立结果目录：

~~~text
Scans\YYYY-MM-DD-HH-mm-ss-fff-p<process>-<random>\
~~~

| 文件 | 用途 |
| --- | --- |
| <code>export.json</code> | 清洗后的驱动盘记录，用于导入计算器 |
| <code>scan.log</code> | 程序版本、选项、进度、安全事件、回退与错误 |
| <code>ocr_diagnostics.csv</code> | 每个 ROI 的耗时和 OCR 诊断 |
| <code>*.error.txt</code> | 某一件驱动盘失败时的详细原因 |
| <code>*.non15.txt</code> | 导致正常停止的第一件非 15 级驱动盘 |

开启可选诊断模式后，还可能生成 shadow ROI 图片、调试截图、Fast OCR CSV、
资源使用指标和视觉配置元数据。

桌面 GUI 的“打开产物文件夹 / Open Output Folder”会打开最近一次结果目录。
通过 Helper 运行时，结果位于：

~~~text
%LOCALAPPDATA%\ZZZScannerNext\outputs
~~~

分享诊断文件前请先检查内容。截图会包含当时可见的游戏界面，日志可能包含本机
路径和设备相关信息。

## 权限与 UAC

扫描器 Manifest 使用 <code>asInvoker</code>，正常使用不需要管理员权限。
Helper 也不会默认通过 <code>runas</code> 启动扫描器。

Windows 不允许低完整性级别进程控制高完整性级别进程。如果游戏是“以管理员
身份运行”，普通权限扫描器会返回 <code>elevation_required</code>。计算器会
解释原因，并可让 Helper 只对扫描器执行一次 UAC 提权重启。

- 只有在自己刚刚发起扫描时才批准 UAC；
- 用户取消 UAC 会明确返回 <code>uac_cancelled</code>，而不是伪装成连接超时；
- 更推荐让游戏和扫描器都以普通权限运行，不要长期让所有程序以管理员运行；
- 提权只解决 Windows 输入权限不匹配，不会修复错误语言、错误布局或 OCR 问题。

## 故障排查

### Helper 双击后没有启动

依次检查：

1. SmartScreen 是否拦截；
2. Windows 安全中心“保护历史记录”中是否隔离文件；
3. 第三方杀毒软件或企业策略是否阻止未知未签名程序；
4. 系统是否满足 x64 和最低 Windows Build；
5. 是否已有另一个 Helper 占用 22355 端口；
6. 文件是否从完整下载的官方 Release 中取得。

如果安全软件在进程创建前拦截 Helper，程序内部无法生成诊断。请不要通过全局
关闭防护来规避，应核对来源和哈希，并只对确认过的官方文件处理拦截。

### 计算器提示未安装 Helper 或版本过旧

1. 在计算器中点击“下载并更新 Helper”。
2. 运行 Helper 1.2.1，并确认一次接管。
3. 保持扫描抽屉打开；安装器会关闭验证过的旧 Helper、安装托管副本，网页会自动重连。

不要手动结束占用 22355 端口的未知进程。服务身份、版本或候选进程不明确时，
安装器会拒绝接管并显示恢复步骤，不会结束任何进程。

健康运行的 Helper 会在以下本机地址返回自身版本和协议版本：

~~~text
http://127.0.0.1:22355/
~~~

该地址只用于本机健康检查，不应暴露到局域网或公网。

### 提示系统或架构不支持

确认系统是 x64 Windows 10 Build 17763 及以上或 Windows 11。Win7、旧版
Win10、x86 和 ARM64 不在本期承诺范围。兼容包只解决缺少 .NET 的问题，不能
把不受支持的操作系统变成受支持系统。

### 磁盘空间不足或缓存目录不可写

Helper 会在下载前检查压缩包、展开目录和安全余量。请释放系统盘空间，或检查
当前用户是否能写入 <code>%LOCALAPPDATA%\ZZZScannerNext</code>。不要仅删除
一个随机 DLL；如需清理，优先通过计算器执行修复，或者在 Helper 关闭后处理
明确的版本缓存目录。

### 下载失败或镜像不可用

先点击“重试”。Helper 支持断点续传，并会尝试清单中的所有镜像。持续失败通常
来自：

- 电脑离线或网络过滤；
- TLS 代理、证书或企业网关问题；
- Release 文件尚未完整上传；
- 磁盘不足或 LocalAppData 无写权限；
- 安全软件在下载后移除了 ZIP；
- 发布清单 URL、大小或哈希与实际资产不一致。

远程 Manifest、程序包及其重定向必须使用 HTTPS。普通 HTTP 只允许用于本机
回环开发地址。

### 提示程序包损坏或文件缺失

在计算器中选择“修复扫描器”。Helper 会：

1. 清理所选版本和包 ID 的损坏缓存；
2. 重新下载并验证大小和 SHA-256；
3. 在临时目录中安全解压；
4. 逐项验证入口程序和应用文件；
5. 原子替换正式运行目录。

ZIP 中的绝对路径、目录穿越路径和越过受控根目录的文件会被拒绝。

### 提示缺少原生 DLL、0xC0000135 或进程立即退出

先执行“修复扫描器”。正式包已经携带 ONNX Runtime 和实际需要的本地 VC
运行库。如果仍然出现 <code>0xC0000135</code>：

1. 检查杀毒软件是否隔离了随包 DLL；
2. 手动安装时确认完整解压，而不是只复制 EXE；
3. 确认下载的是 x64 包；
4. 打开 Helper 日志，并在反馈时附上 diagnostic ID；
5. 不要从第三方网站单独下载并替换 DLL。

### 找不到游戏

- 扫描前先启动并登录游戏；
- 本地客户端使用 <code>ZenlessZoneZero</code>；
- 云客户端使用 <code>Zenless Zone Zero Cloud</code>；
- 打开驱动盘仓库，使主窗口进入可扫描状态；
- 确认找到的是游戏进程，而不是启动器或更新器；
- 如果游戏以管理员权限运行，请处理权限级别不匹配。

### 扫描器启动后握手超时或子进程提前退出

计算器会区分子进程退出、握手超时、端口占用、原生依赖缺失和 UAC 取消，不应
只显示一条模糊的“连接失败”。先执行页面给出的动作；如提示修复则修复，如提示
提权则确认游戏权限后再决定是否提权。重复失败时使用“复制诊断”并保留
diagnostic ID。

### 扫描数据不正确、漏项、重复或中途停止

确认以下内容：

- 游戏是简体中文界面；
- 分辨率和布局受支持；
- 游戏窗口可见、未最小化且没有遮挡；
- 扫描期间没有用户输入；
- 排序、筛选、显示缩放、HDR 和 UI 缩放没有改变；
- 本地/云端客户端选择正确；
- “仅 15 级”导致的停止是否其实属于预期；
- 游戏更新后是否先完成过 30 件试扫。

检查 <code>scan.log</code> 中的 ROI 完整性、视觉配置路由、OCR 回退、重复项、
重叠冲突和槽位安全事件。游戏 UI 更新后，在试扫通过前应把已有视觉配置视为
可能过期。

### 查看日志和诊断

在计算器中使用“打开日志文件夹”或“复制诊断”。Helper 日志位于：

~~~text
%LOCALAPPDATA%\ZZZScannerNext\logs
~~~

对于浏览器尚未连接时发生的端口占用、协议注册失败等启动错误，Helper 会使用
原生 Windows 对话框显示错误码、diagnostic ID 和日志位置。

反馈问题时建议提供：

- 错误码和 diagnostic ID；
- Windows 版本、Build 和架构；
- 本地或云端客户端；
- 游戏分辨率与 Windows 显示缩放；
- 使用的包 ID 和版本；
- 对应的 <code>scan.log</code>。

不要公开包含个人路径或不希望分享的游戏截图。

## 已知限制

- 扫描依赖可见 UI 的坐标、颜色、面板时序和 OCR 文字。即使进程名没有变化，
  一次游戏更新也可能使扫描失效。
- 当前只维护简体中文词典和驱动盘仓库布局。
- Fast OCR 只覆盖有限的本地/云端分辨率。回退到 PP-OCR 不代表未知布局一定
  安全。
- 完整扫描低等级驱动盘不是当前计算器工作流的正式支持功能。
- GDI 需要可见桌面。窗口最小化、被遮挡、桌面锁定、远程连接断开时，截图可能
  过期或全黑。
- DXGI 依赖 GPU、驱动、显示器与当前会话，失败时可能回退到 GDI。
- HDR、色彩滤镜、Overlay、UI 模组和色彩管理变化可能影响稀有度与稳定性判断。
- 未签名程序可能在自诊断运行前就被 SmartScreen、安全软件或企业策略阻止。
- 没有任何程序能保证每一台 Windows 10/11 电脑都一定可运行。损坏的系统文件、
  严格的企业策略、安全产品、异常硬件和未来游戏变更仍可能阻止使用。
- Helper 可以解决缺少 .NET 8 的常见情况，但不能修复损坏的 Windows 组件、
  被系统禁止的进程创建或被安全软件删除的文件。
- “不读取内存、不注入”不代表获得游戏厂商认可。用户需要自行了解适用规则并
  承担账号相关风险。
- 本项目为社区项目，与 HoYoverse / 米哈游不存在隶属、授权或背书关系。

## 命令行用法

命令行模式主要面向可重复验证和开发。实时扫描与 GUI 一样会控制鼠标和游戏
窗口。

### 执行一次扫描

~~~powershell
.\ZZZ-Scanner.Next.exe --scan-once --max-items 30
.\ZZZ-Scanner.Next.exe --scan-once --max-items 120 --fast-mode --capture-mode dxgi
~~~

| 参数 | 含义 |
| --- | --- |
| <code>--process &lt;name&gt;</code> | 本地或云端游戏进程 |
| <code>--profile &lt;name&gt;</code> | 指定精确扫描配置 |
| <code>--max-items &lt;n&gt;</code> | 最大件数，0 表示不设明确上限 |
| <code>--rarities S,A</code> | 逗号分隔的稀有度过滤 |
| <code>--include-non15</code> | 实验性扫描低等级驱动盘 |
| <code>--no-bring-to-front</code> | 不主动激活游戏 |
| <code>--capture-mode gdi&#124;dxgi</code> | 截图后端 |
| <code>--fast-mode</code> | 启用已验证极速配置和 Fast OCR |
| <code>--adaptive-timing</code> | 强制本次扫描使用自适应时序 |
| <code>--no-adaptive-timing</code> | 禁用自适应时序 |
| <code>--ocr-workers 0..4</code> | OCR Worker 数量，0 表示自动 |
| <code>--ocr-batch 1..16</code> | OCR Batch 大小 |
| <code>--ocr-queue 1..256</code> | OCR 队列容量 |
| <code>--ocr-intra-op 1..8</code> | ONNX IntraOp 线程数 |
| <code>--config &lt;json&gt;</code> | 读取 ScanRunCommand JSON 文件 |

命令行会打印输出路径和件数。退出码含义：

| 退出码 | 含义 |
| --- | --- |
| 0 | 没有失败项 |
| 1 | 扫描失败 |
| 73 | 另一个扫描实例正在持有互斥锁 |
| 130 | 用户取消 |

### 离线基准检查

~~~powershell
.\ZZZ-Scanner.Next.exe --scan-benchmark <scan-directory> [baseline-directory]
~~~

该命令只读取已有输出，不控制游戏。发布验证通常要求：导出重复数为 0、
错误文件数为 0、<code>slot_safety_pass=true</code>、硬重叠停止数为 0，
并且视觉配置路由和件数符合预期。

### 维护者工具

~~~powershell
.\ZZZ-Scanner.Next.exe --capture-stability-suite both --max-items 120 --rounds 5
.\ZZZ-Scanner.Next.exe --scan-stability-suite <suite-directory>
.\ZZZ-Scanner.Next.exe --ocr-shadow-analyze <scan-or-parent> --build-fast-index <index.json>
.\ZZZ-Scanner.Next.exe --ocr-fast-eval <index.json> <shadow-parent>
.\ZZZ-Scanner.Next.exe --ocr-fast-cross-validate <shadow-parent>
.\ZZZ-Scanner.Next.exe --ocr-fast-calibrate <shadow-parent> --output <index.json> --feature v6
.\ZZZ-Scanner.Next.exe --ocr-fast-calibrate-visual-profiles <shadow-parent> --output <index.json> --feature v6
.\ZZZ-Scanner.Next.exe --ocr-fast-merge-indexes <output.json> <index1.json> <index2.json> [...]
~~~

校准必须基于多次干净、可复现的扫描。仅仅更快不足以让某个配置进入发布策略。
请同时阅读[架构说明](docs/ARCHITECTURE.md)和[测试证据](docs/TESTING.md)。

## 从源码构建

要求：

- x64 Windows；
- .NET 8 SDK；
- <code>Resources\models\PP-OCRv5_mobile_rec_infer.onnx</code>。

大型模型文件不跟踪在 Git 中。请从项目官方 Release 获取并校验。

~~~powershell
dotnet restore
dotnet build ZZZ-Scanner.Next.csproj -c Release -p:NuGetAudit=false
dotnet build Launcher\ZZZ-Scanner.Helper.csproj -c Release -p:NuGetAudit=false
dotnet run --project Tests\ZZZ-Scanner.Next.RegressionTests.csproj -c Release -p:NuGetAudit=false
.\scripts\publish-slim.ps1 -Version 1.0.38
~~~

发布输出：

~~~text
dist\ZZZ-Scanner.Next-win-x64-fdd.zip
dist\ZZZ-Scanner.Next-win-x64-self-contained.zip
dist\publish-helper\ZZZ-Scanner-Helper.exe
dist\scanner-manifest-<version>.json
dist\publish-report-<version>.json
~~~

发布门禁要求：

- FDD 不超过 25 MiB；
- 自包含包不超过 90 MiB；
- Helper 不超过 10 MiB；
- 不得包含 OpenCvSharp 和 PDB；
- 两个包的模型和应用内容必须一致；
- ONNX、VC 运行库和其他必要 PE 依赖必须完整；
- ZIP 内路径和时间戳必须确定性生成。

正式 CI 使用 <code>-RequireVCRedistLayout</code>，从受控 Windows 构建机的
VC143 Redistributable 布局复制依赖，并记录 DLL 版本、SHA-256 和许可证信息。
本地构建可以记录 System32 fallback，但这种产物不属于正式 Release。

## 安全与隐私

- OCR 和截图均在本机处理。
- 普通扫描不会把截图上传到本仓库或项目服务器。
- 网页集成只使用本机回环 HTTP/WebSocket、来源白名单和一次性令牌。
- 远程 Manifest、程序包和重定向必须使用 HTTPS。
- ZIP 解压会拒绝绝对路径、目录穿越和受控运行根目录以外的条目。
- 复用缓存前必须通过包级和逐文件完整性校验。
- 扫描器 WebSocket 消息有大小限制，同一时间只允许一次扫描。
- Helper 使用限量滚动日志，不会无限增长。

只有用户明确开启时，程序才会在本机写入调试截图和 OCR shadow 数据集。分享前
请自行检查其中内容。

## 更多文档

- [架构说明](docs/ARCHITECTURE.md)
- [测试与发布证据](docs/TESTING.md)
- [数据来源](docs/DATA_SOURCES.md)
- [更新日志](docs/CHANGELOG.md)
- [YAS 调研记录](docs/yas-study.md)

## 许可证

[MIT](LICENSE)
