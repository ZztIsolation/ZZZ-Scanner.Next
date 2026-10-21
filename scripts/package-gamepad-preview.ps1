param([string]$OutputRoot, [string]$InstallerPath, [string]$VCRuntimeDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'artifacts\packages' }
if (-not $InstallerPath) { $InstallerPath = Join-Path $repo 'artifacts\gamepad-probe-dependencies\ViGEmBus_1.22.0_x64_x86_arm64.exe' }
if (-not $VCRuntimeDirectory) { $VCRuntimeDirectory = Join-Path $repo 'artifacts\gamepad-probe-dependencies\scanner-1.0.49-fdd' }
$name = 'ZZZ-Scanner.Next-gamepad-preview-20261021-win-x64'
$destination = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) $name
$zip = "$destination.zip"
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $zip)) { throw '输出已存在，请指定新的 OutputRoot。' }
$installerHash = '89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A'
$modelHash = '70CA955EBA4729651DA42E7CBCC69AAF41A601939EE5FFAA7A90EFDFCD13DB6D'
if ((Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash -ne $installerHash) { throw '驱动安装器哈希不符。' }
$signature = Get-AuthenticodeSignature -LiteralPath $InstallerPath
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Nefarius Software Solutions') { throw '驱动安装器签名不符。' }
if ((Get-FileHash -LiteralPath (Join-Path $repo 'Resources\models\PP-OCRv5_mobile_rec_infer.onnx') -Algorithm SHA256).Hash -ne $modelHash) { throw 'OCR 模型哈希不符。' }
$runtimeNames = @('msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','msvcp140_atomic_wait.dll','msvcp140_codecvt_ids.dll','vcruntime140.dll','vcruntime140_1.dll','vcruntime140_threads.dll')
$runtimeFiles = foreach ($file in $runtimeNames) {
    $path = Join-Path $VCRuntimeDirectory $file
    $signed = Get-AuthenticodeSignature -LiteralPath $path
    $version = (Get-Item -LiteralPath $path).VersionInfo.FileVersion
    if ($signed.Status -ne 'Valid' -or $signed.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation' -or $version -ne '14.44.35211.0') { throw "VC runtime 不符：$file" }
    [pscustomobject]@{Name=$file; Version=$version; Sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
}
dotnet publish (Join-Path $repo 'ZZZ-Scanner.Next.csproj') -c Release -r win-x64 --self-contained true '-p:RuntimeFrameworkVersion=8.0.32' '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' '-p:DebugType=None' '-p:DebugSymbols=false' -o $destination
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
foreach ($file in $runtimeNames) { Copy-Item -LiteralPath (Join-Path $VCRuntimeDirectory $file) -Destination $destination }
$drivers = New-Item -ItemType Directory -Path (Join-Path $destination 'Drivers')
$licenses = New-Item -ItemType Directory -Path (Join-Path $destination 'Licenses')
$validation = New-Item -ItemType Directory -Path (Join-Path $destination 'Validation')
Copy-Item -LiteralPath $InstallerPath -Destination $drivers.FullName
Get-ChildItem -LiteralPath (Join-Path $repo 'Experiments\GamepadProbe\ThirdParty') -File | Copy-Item -Destination $licenses.FullName
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $licenses.FullName 'ZZZ-Scanner-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repo 'docs\GAMEPAD_SCANNER.md') -Destination $destination
@'
正式扫描器手柄导航预览版（2026-10-21）

1. 完整解压后启动 ZZZ-Scanner.Next.exe，无需实体手柄或另外安装 .NET。
2. 在“高级选项”中打开 ViGEmBus 检测 / 安装引导。未安装时可运行随包官方安装器；仅此步骤需要管理员确认。
3. 游戏使用 16:9 简体中文界面，打开背包 → 驱动盘，回到整个背包第一个盘。
4. 首次建议设置读取上限 18，使用默认“手柄动态网格”模式。
5. 点击开始扫描，保持游戏前台。切出游戏会停止输入并处理已采集截图。
6. OCR 普通单件失败会记录并继续；失败扫描导出 export.partial.json，详细原因见 scan.log。

本包使用输入层适配，复用正式扫描器的逐件采集、判稳、OCR 队列、清洗、排序和导出。
驱动安装器版本 1.22.0；内核驱动版本 1.21.442.0；客户端 1.21.256。
已有其他版本不自动覆盖。ViGEmBus 已停止维护。
新输入层通过 77 组回归；本机 4K、50 ms 按压实测导出 364 件 15 级盘，OCR 失败 0，整轮 149.68 秒。
默认按压仍为 60 ms；全部等级、其他分辨率和多电脑验收尚未完成。
接口、边界、错误策略和实测记录见 GAMEPAD_SCANNER.md。
'@ | Set-Content -LiteralPath (Join-Path $destination '开始使用.txt') -Encoding utf8
$fixture = Join-Path $validation.FullName 'ocr-equivalence.png'
[IO.File]::WriteAllBytes($fixture, [Convert]::FromBase64String([IO.File]::ReadAllText((Join-Path $repo 'Tests\Fixtures\ocr-equivalence.png.b64'))))
$check = Start-Process -FilePath (Join-Path $destination 'ZZZ-Scanner.Next.exe') -ArgumentList @('--ocr-runtime-smoke', ('"' + $fixture + '"')) -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput (Join-Path $validation.FullName 'ocr-runtime-smoke.json') -RedirectStandardError (Join-Path $validation.FullName 'ocr-runtime-smoke.stderr.txt')
if ($check.ExitCode -ne 0) { throw '发布包真实 ONNX OCR 检查失败。' }
[ordered]@{
    kind='production-gamepad-preview'; entry='ZZZ-Scanner.Next.exe'; builtAt=[DateTimeOffset]::Now.ToString('O')
    sourceRevision=(git -C $repo rev-parse HEAD); sourceHasLocalChanges=[bool](git -C $repo status --porcelain)
    runtime='8.0.32'; platform='win-x64'; installer='1.22.0'; driver='1.21.442.0'; client='1.21.256'
    installerSha256=$installerHash; modelSha256=$modelHash; vcRuntimeFiles=@($runtimeFiles)
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'package-info.json') -Encoding utf8
Get-ChildItem -LiteralPath $destination -Recurse -File | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, [IO.Path]::GetRelativePath($destination, $_.FullName)
} | Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') -Encoding utf8
Compress-Archive -LiteralPath $destination -DestinationPath $zip -CompressionLevel Optimal
"$((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash)  $name.zip" | Set-Content -LiteralPath "$zip.sha256" -Encoding ascii
Write-Output "Package: $zip"
