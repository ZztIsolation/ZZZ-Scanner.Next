param(
    [string]$OutputRoot,
    [string]$InstallerPath,
    [string]$VCRuntimeDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$projectRoot = $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..\..'))
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'artifacts\packages' }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (-not $InstallerPath) { $InstallerPath = Join-Path $repoRoot 'artifacts\gamepad-probe-dependencies\ViGEmBus_1.22.0_x64_x86_arm64.exe' }
if (-not $VCRuntimeDirectory) { $VCRuntimeDirectory = Join-Path $repoRoot 'artifacts\gamepad-probe-dependencies\scanner-1.0.49-fdd' }
$modelPath = Join-Path $repoRoot 'Resources\models\PP-OCRv5_mobile_rec_infer.onnx'
$modelHash = '70CA955EBA4729651DA42E7CBCC69AAF41A601939EE5FFAA7A90EFDFCD13DB6D'
if (-not (Test-Path -LiteralPath $modelPath) -or (Get-FileHash -LiteralPath $modelPath -Algorithm SHA256).Hash -ne $modelHash) {
    throw '缺少固定 OCR 模型或模型哈希不符。请按仓库 CI 下载并校验模型。'
}
$runtimeNames = @('msvcp140.dll','msvcp140_1.dll','msvcp140_2.dll','msvcp140_atomic_wait.dll','msvcp140_codecvt_ids.dll','vcruntime140.dll','vcruntime140_1.dll','vcruntime140_threads.dll')
$runtimeFiles = foreach ($name in $runtimeNames) {
    $runtimePath = Join-Path $VCRuntimeDirectory $name
    $runtimeSignature = Get-AuthenticodeSignature -FilePath $runtimePath
    $runtimeFile = Get-Item -LiteralPath $runtimePath
    if ($runtimeSignature.Status -ne 'Valid' -or $runtimeSignature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation' -or
        $runtimeFile.VersionInfo.FileVersion -ne '14.44.35211.0') { throw "VC runtime signature/version mismatch: $runtimePath" }
    [PSCustomObject]@{ Name=$name; Version=$runtimeFile.VersionInfo.FileVersion; Sha256=(Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash }
}
$installerHash = '89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A'
$packageName = 'ZZZ.GamepadProbe-0.4.0-win-x64'
$destination = Join-Path $OutputRoot $packageName
$zipPath = Join-Path $OutputRoot "$packageName.zip"
if ((Test-Path -LiteralPath $destination) -or (Test-Path -LiteralPath $zipPath)) {
    throw "输出已存在，请指定新的 -OutputRoot：$destination"
}
if (-not (Test-Path -LiteralPath $InstallerPath)) {
    throw '缺少固定驱动安装器。请从官方 1.22.0 发行页下载，再传入 -InstallerPath；脚本不会下载或运行安装器。'
}
if ((Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash -ne $installerHash) {
    throw '驱动安装器 SHA-256 不符。'
}
$signature = Get-AuthenticodeSignature -FilePath $InstallerPath
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Nefarius Software Solutions') {
    throw '驱动安装器数字签名验证失败。'
}

dotnet restore (Join-Path $projectRoot 'GamepadProbe.csproj') --locked-mode
if ($LASTEXITCODE -ne 0) { throw '锁定依赖还原失败。' }
dotnet publish (Join-Path $projectRoot 'GamepadProbe.csproj') -c Release -r win-x64 --self-contained true --no-restore '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' '-p:DebugType=None' '-p:DebugSymbols=false' -o $destination
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
foreach ($name in $runtimeNames) { Copy-Item -LiteralPath (Join-Path $VCRuntimeDirectory $name) -Destination $destination }

$drivers = New-Item -ItemType Directory -Path (Join-Path $destination 'Drivers')
$licenses = New-Item -ItemType Directory -Path (Join-Path $destination 'Licenses')
Copy-Item -LiteralPath $InstallerPath -Destination $drivers.FullName
Get-ChildItem -LiteralPath (Join-Path $projectRoot 'ThirdParty') -File | Copy-Item -Destination $licenses.FullName
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $licenses.FullName 'ZZZ-Scanner-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $destination
Copy-Item -LiteralPath (Join-Path $projectRoot 'QUICKSTART.txt') -Destination (Join-Path $destination '开始使用.txt')

$manifest = [ordered]@{
    toolVersion = '0.4.0'
    platform = 'win-x64'
    selfContained = $true
    dotnetRuntime = '8.0.32'
    installerVersion = '1.22.0'
    driverVersion = '1.21.442.0'
    clientVersion = '1.21.256'
    onnxRuntimeVersion = '1.23.1'
    ocrModelSha256 = $modelHash
    vcRuntimeVersion = '14.44.35211.0'
    vcRuntimeFiles = @($runtimeFiles)
    installerSha256 = $installerHash
    installerSignature = $signature.Status.ToString()
    installerSigner = $signature.SignerCertificate.Subject
    sourceRevision = (git -C $repoRoot rev-parse HEAD)
    sourceHasLocalChanges = [bool](git -C $repoRoot status --porcelain)
    builtAt = [DateTimeOffset]::Now.ToString('O')
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'package-info.json') -Encoding utf8

$checks = New-Item -ItemType Directory -Force -Path (Join-Path $destination 'Validation')
foreach ($check in @('self-test', 'verify-installer', 'ocr-runtime-check')) {
    $reportPath = Join-Path $checks.FullName "$check.txt"
    $test = Start-Process -FilePath (Join-Path $destination 'ZZZ.GamepadProbe.exe') -ArgumentList @("--$check", ('"' + $reportPath + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($test.ExitCode -ne 0) { throw "发布包检查失败：$check；请查看 $reportPath" }
}
$hashLines = Get-ChildItem -LiteralPath $destination -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($destination, $_.FullName)
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash, $relative
}
$hashLines | Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') -Encoding utf8
Compress-Archive -LiteralPath $destination -DestinationPath $zipPath -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
"$zipHash  $packageName.zip" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ascii
Write-Output "Package: $zipPath"
Write-Output "SHA256: $zipHash"
