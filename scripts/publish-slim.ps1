param(
    [string]$Version = "1.0.28"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$distRoot = Join-Path $repoRoot "dist"
$helperOut = Join-Path $distRoot "publish-helper"
$scannerOut = Join-Path $distRoot "publish-scanner-$Version"
$zipPath = Join-Path $distRoot "ZZZ-Scanner.Next-win-x64-$Version.zip"

foreach ($path in @($helperOut, $scannerOut)) {
    if (Test-Path $path) {
        Remove-Item -Recurse -Force $path
    }
}
if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}

dotnet publish Launcher\ZZZ-Scanner.Helper.csproj -c Release -r win-x64 --self-contained true -o $helperOut
dotnet publish ZZZ-Scanner.Next.csproj -c Release -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false -o $scannerOut

Compress-Archive -Path (Join-Path $scannerOut "*") -DestinationPath $zipPath -CompressionLevel Optimal

$helperExe = Join-Path $helperOut "ZZZ-Scanner-Helper.exe"
$helperSize = (Get-Item $helperExe).Length
$scannerSize = (Get-Item $zipPath).Length

Write-Host "helper_exe=$helperExe"
Write-Host "helper_size=$helperSize"
Write-Host "scanner_zip=$zipPath"
Write-Host "scanner_zip_size=$scannerSize"
