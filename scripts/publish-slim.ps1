param(
    [string]$Version,
    [string]$OutputRoot,
    [switch]$RequireVCRedistLayout,
    [int]$MaxFddMiB = 25,
    [int]$MaxSelfContainedMiB = 90,
    [int]$MaxHelperMiB = 10
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$project = Get-Content (Join-Path $repoRoot "ZZZ-Scanner.Next.csproj")
    $Version = $project.Project.PropertyGroup |
        ForEach-Object { [string]$_.Version } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1
}

$parsedVersion = $null
if (-not [System.Version]::TryParse($Version, [ref]$parsedVersion) -or $Version.Split('.').Count -ne 3) {
    throw "Version must use major.minor.patch format, for example 1.0.37. Got: $Version"
}

$assemblyVersion = "$($parsedVersion.Major).$($parsedVersion.Minor).$($parsedVersion.Build).0"
[xml]$helperProject = Get-Content (Join-Path $repoRoot "Launcher\ZZZ-Scanner.Helper.csproj")
$helperVersion = $helperProject.Project.PropertyGroup |
    ForEach-Object { [string]$_.Version } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1
$parsedHelperVersion = $null
if (-not [System.Version]::TryParse($helperVersion, [ref]$parsedHelperVersion)) {
    throw "Helper project Version is invalid: $helperVersion"
}
$distRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repoRoot "dist"
}
elseif ([System.IO.Path]::IsPathRooted($OutputRoot)) {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
}

$helperOut = Join-Path $distRoot "publish-helper"
$fddOut = Join-Path $distRoot "publish-scanner-$Version-fdd"
$selfContainedOut = Join-Path $distRoot "publish-scanner-$Version-self-contained"
$fddZip = Join-Path $distRoot "ZZZ-Scanner.Next-win-x64-fdd.zip"
$selfContainedZip = Join-Path $distRoot "ZZZ-Scanner.Next-win-x64-self-contained.zip"
$manifestPath = Join-Path $distRoot "scanner-manifest-$Version.json"
$helperManifestPath = Join-Path $distRoot "helper-manifest.json"
$reportPath = Join-Path $distRoot "publish-report-$Version.json"

function Remove-OutputPath([string]$Path) {
    if (Test-Path $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Invoke-DotnetPublish([string[]]$Arguments, [string]$Label) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE."
    }
}

function Find-VCRedistDirectory {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($env:VCToolsRedistDir)) {
        $candidates += (Join-Path $env:VCToolsRedistDir "x64\Microsoft.VC143.CRT")
    }

    foreach ($visualStudioRoot in @(
        "$env:ProgramFiles\Microsoft Visual Studio\2022",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022"
    )) {
        if (Test-Path $visualStudioRoot) {
            $candidates += Get-ChildItem $visualStudioRoot -Directory -Recurse -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match '\\x64\\Microsoft\.VC14[0-9]\.CRT$' } |
                Sort-Object FullName -Descending |
                Select-Object -ExpandProperty FullName
        }
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        if (Test-Path (Join-Path $candidate "msvcp140.dll")) {
            return @{ Path = $candidate; Source = "vc-redist-layout" }
        }
    }

    if ($RequireVCRedistLayout) {
        throw "VC143 Redistributable layout was not found. Install Visual Studio Build Tools with the C++ runtime or set VCToolsRedistDir."
    }

    $system32 = [Environment]::GetFolderPath([Environment+SpecialFolder]::System)
    if (Test-Path (Join-Path $system32 "msvcp140.dll")) {
        Write-Warning "VC Redist layout was not found; using the installed System32 redistributable for this local build. Release CI must use -RequireVCRedistLayout."
        return @{ Path = $system32; Source = "system32-fallback" }
    }

    throw "Microsoft VC++ x64 runtime files were not found."
}

function Copy-AppLocalVCRuntime([string]$Destination, [hashtable]$VCRedist) {
    $required = @("msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll")
    $optional = @("msvcp140_2.dll", "msvcp140_atomic_wait.dll", "msvcp140_codecvt_ids.dll", "vcruntime140_threads.dll")
    $copied = @()
    foreach ($name in $required + $optional) {
        $source = Join-Path $VCRedist.Path $name
        if (-not (Test-Path $source)) {
            if ($required -contains $name) {
                throw "Required VC runtime file is missing: $source"
            }
            continue
        }

        $target = Join-Path $Destination $name
        Copy-Item -LiteralPath $source -Destination $target -Force
        $item = Get-Item $target
        $copied += [ordered]@{
            name = $name
            version = $item.VersionInfo.FileVersion
            size = $item.Length
            sha256 = (Get-FileHash -Algorithm SHA256 $target).Hash.ToLowerInvariant()
        }
    }

    $notice = @(
        "Microsoft Visual C++ Runtime - app-local deployment",
        "Source: $($VCRedist.Source)",
        "The runtime files are redistributed under the applicable Microsoft Visual Studio license.",
        "They are refreshed and hashed by every scanner release build."
    ) -join [Environment]::NewLine
    Set-Content -LiteralPath (Join-Path $Destination "VC-RUNTIME-NOTICE.txt") -Value $notice -Encoding UTF8
    return $copied
}

function Assert-PublishPayload([string]$Directory) {
    foreach ($pattern in @("OpenCvSharp*.dll", "OpenCvSharpExtern.dll", "*.pdb")) {
        if (Get-ChildItem $Directory -Filter $pattern -Recurse -File -ErrorAction SilentlyContinue) {
            throw "Forbidden publish payload matched $pattern in $Directory"
        }
    }

    foreach ($required in @(
        "ZZZ-Scanner.Next.exe",
        "onnxruntime.dll",
        "msvcp140.dll",
        "msvcp140_1.dll",
        "vcruntime140.dll",
        "vcruntime140_1.dll",
        "Resources\models\PP-OCRv5_mobile_rec_infer.onnx"
    )) {
        if (-not (Test-Path (Join-Path $Directory $required))) {
            throw "Required publish file is missing: $required"
        }
    }
}

function Get-PeImportedLibraries([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4d -or $bytes[1] -ne 0x5a) {
        throw "Not a valid PE file: $Path"
    }

    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    $hasValidSignature = $peOffset -ge 0 -and
        $peOffset + 24 -le $bytes.Length -and
        $bytes[$peOffset] -eq 0x50 -and
        $bytes[$peOffset + 1] -eq 0x45 -and
        $bytes[$peOffset + 2] -eq 0 -and
        $bytes[$peOffset + 3] -eq 0
    if (-not $hasValidSignature) {
        throw "Invalid PE header: $Path"
    }

    $sectionCount = [BitConverter]::ToUInt16($bytes, $peOffset + 6)
    $optionalSize = [BitConverter]::ToUInt16($bytes, $peOffset + 20)
    $optionalOffset = $peOffset + 24
    if ($optionalOffset + $optionalSize -gt $bytes.Length) {
        throw "Invalid PE optional header: $Path"
    }

    $magic = [BitConverter]::ToUInt16($bytes, $optionalOffset)
    if ($magic -eq 0x10b) {
        $dataDirectoryOffset = $optionalOffset + 96
        $imageBase = [uint64][BitConverter]::ToUInt32($bytes, $optionalOffset + 28)
    }
    elseif ($magic -eq 0x20b) {
        $dataDirectoryOffset = $optionalOffset + 112
        $imageBase = [BitConverter]::ToUInt64($bytes, $optionalOffset + 24)
    }
    else {
        throw "Unsupported PE optional-header magic 0x$($magic.ToString('x')): $Path"
    }

    $sectionOffset = $optionalOffset + $optionalSize
    $sections = @()
    for ($index = 0; $index -lt $sectionCount; $index++) {
        $offset = $sectionOffset + ($index * 40)
        if ($offset + 40 -gt $bytes.Length) {
            throw "Invalid PE section table: $Path"
        }

        $sections += [pscustomobject]@{
            VirtualSize = [BitConverter]::ToUInt32($bytes, $offset + 8)
            VirtualAddress = [BitConverter]::ToUInt32($bytes, $offset + 12)
            RawSize = [BitConverter]::ToUInt32($bytes, $offset + 16)
            RawOffset = [BitConverter]::ToUInt32($bytes, $offset + 20)
        }
    }

    function Convert-RvaToOffset([uint64]$Rva) {
        foreach ($section in $sections) {
            $end = [uint64]$section.VirtualAddress + [Math]::Max([uint64]$section.VirtualSize, [uint64]$section.RawSize)
            if ($Rva -ge $section.VirtualAddress -and $Rva -lt $end) {
                $result = [uint64]$section.RawOffset + ($Rva - [uint64]$section.VirtualAddress)
                if ($result -ge $bytes.Length) {
                    throw "PE RVA points outside the file: $Path"
                }
                return [int]$result
            }
        }

        if ($Rva -lt $sectionOffset) {
            return [int]$Rva
        }
        throw "PE RVA 0x$($Rva.ToString('x')) is not mapped by a section: $Path"
    }

    function Read-AsciiZ([uint64]$Rva) {
        $offset = Convert-RvaToOffset $Rva
        $end = $offset
        while ($end -lt $bytes.Length -and $bytes[$end] -ne 0) {
            $end++
        }
        if ($end -eq $bytes.Length) {
            throw "Unterminated PE import name: $Path"
        }
        return [Text.Encoding]::ASCII.GetString($bytes, $offset, $end - $offset)
    }

    $imports = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($directory in @(
        [pscustomobject]@{ Index = 1; DescriptorSize = 20; NameOffset = 12; Delay = $false },
        [pscustomobject]@{ Index = 13; DescriptorSize = 32; NameOffset = 4; Delay = $true }
    )) {
        $entryOffset = $dataDirectoryOffset + ($directory.Index * 8)
        if ($entryOffset + 8 -gt $optionalOffset + $optionalSize) {
            continue
        }
        $directoryRva = [BitConverter]::ToUInt32($bytes, $entryOffset)
        $directorySize = [BitConverter]::ToUInt32($bytes, $entryOffset + 4)
        if ($directoryRva -eq 0 -or $directorySize -eq 0) {
            continue
        }

        $descriptorOffset = Convert-RvaToOffset $directoryRva
        $maximumOffset = [Math]::Min($bytes.Length, $descriptorOffset + [int]$directorySize)
        while ($descriptorOffset + $directory.DescriptorSize -le $maximumOffset) {
            $allZero = $true
            for ($byteIndex = 0; $byteIndex -lt $directory.DescriptorSize; $byteIndex++) {
                if ($bytes[$descriptorOffset + $byteIndex] -ne 0) {
                    $allZero = $false
                    break
                }
            }
            if ($allZero) {
                break
            }

            $nameAddress = [uint64][BitConverter]::ToUInt32($bytes, $descriptorOffset + $directory.NameOffset)
            if ($directory.Delay) {
                $attributes = [BitConverter]::ToUInt32($bytes, $descriptorOffset)
                if (($attributes -band 1) -eq 0) {
                    if ($nameAddress -lt $imageBase) {
                        throw "Invalid delay-load import address: $Path"
                    }
                    $nameAddress -= $imageBase
                }
            }
            if ($nameAddress -ne 0) {
                [void]$imports.Add((Read-AsciiZ $nameAddress))
            }
            $descriptorOffset += $directory.DescriptorSize
        }
    }
    return @($imports | Sort-Object)
}

function Assert-PeDependencyClosure([string]$Directory) {
    $systemDirectory = [Environment]::GetFolderPath([Environment+SpecialFolder]::System)
    $localNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $peFiles = @(Get-ChildItem $Directory -File -Recurse |
        Where-Object { $_.Extension -in @(".exe", ".dll") })
    foreach ($file in $peFiles) {
        [void]$localNames.Add($file.Name)
    }

    $dependencies = @{}
    $missing = [Collections.Generic.List[string]]::new()
    foreach ($file in $peFiles) {
        foreach ($library in Get-PeImportedLibraries $file.FullName) {
            $requiresAppLocal = $library -match '^(msvcp|vcruntime|concrt|atl|mfc|onnxruntime|directml)'
            $isLocal = $localNames.Contains($library)
            $isApiSet = $library -match '^(api-ms-win-|ext-ms-win-)'
            $isSystem = -not $requiresAppLocal -and ($isApiSet -or (Test-Path (Join-Path $systemDirectory $library)))
            $status = if ($isLocal) { "app-local" } elseif ($isSystem) { "windows-system" } else { "missing" }
            $dependencies[$library.ToLowerInvariant()] = $status
            if ($status -eq "missing") {
                $missing.Add("$($file.Name) -> $library")
            }
        }
    }

    if ($missing.Count -gt 0) {
        throw "PE dependency closure is incomplete in ${Directory}:`n$($missing -join [Environment]::NewLine)"
    }

    return [ordered]@{
        filesScanned = $peFiles.Count
        dependencies = @($dependencies.GetEnumerator() | Sort-Object Name | ForEach-Object {
            [ordered]@{ name = $_.Name; status = $_.Value }
        })
    }
}

function Get-ExpandedSize([string]$Directory) {
    return [long](Get-ChildItem $Directory -File -Recurse | Measure-Object Length -Sum).Sum
}

function New-Zip([string]$Source, [string]$Destination) {
    Remove-OutputPath $Destination
    $archive = [System.IO.Compression.ZipFile]::Open($Destination, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        $fixedTimestamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        foreach ($file in Get-ChildItem $Source -File -Recurse | Sort-Object FullName) {
            $relativePath = [System.IO.Path]::GetRelativePath($Source, $file.FullName).Replace('\', '/')
            $entry = $archive.CreateEntry($relativePath, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $fixedTimestamp
            $input = $file.OpenRead()
            $output = $entry.Open()
            try {
                $input.CopyTo($output)
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-MaxSize([string]$Path, [int]$MaxMiB, [string]$Label, [string]$PublishDirectory) {
    $size = (Get-Item $Path).Length
    $maximum = [long]$MaxMiB * 1024 * 1024
    if ($size -gt $maximum) {
        Write-Host "$Label exceeded $MaxMiB MiB. Largest files:"
        Get-ChildItem $PublishDirectory -File -Recurse |
            Sort-Object Length -Descending |
            Select-Object -First 20 FullName, Length |
            Format-Table -AutoSize
        throw "$Label size gate failed: $size bytes > $maximum bytes."
    }
}

New-Item -ItemType Directory -Force -Path $distRoot | Out-Null
foreach ($path in @($helperOut, $fddOut, $selfContainedOut, $fddZip, $selfContainedZip, $manifestPath, $helperManifestPath, $reportPath)) {
    Remove-OutputPath $path
}

$commonProperties = @(
    "-p:Version=$Version",
    "-p:AssemblyVersion=$assemblyVersion",
    "-p:FileVersion=$assemblyVersion",
    "-p:DebugType=none",
    "-p:DebugSymbols=false",
    "-p:PublishReadyToRun=false",
    "-p:SatelliteResourceLanguages=zh-Hans"
)

Invoke-DotnetPublish @(
    "publish", "Launcher\ZZZ-Scanner.Helper.csproj", "-c", "Release", "-r", "win-x64",
    "--self-contained", "true", "-o", $helperOut
) "Helper publish"
Invoke-DotnetPublish (@(
    "publish", "ZZZ-Scanner.Next.csproj", "-c", "Release", "-r", "win-x64",
    "--self-contained", "false", "-o", $fddOut
) + $commonProperties) "Framework-dependent scanner publish"
Invoke-DotnetPublish (@(
    "publish", "ZZZ-Scanner.Next.csproj", "-c", "Release", "-r", "win-x64",
    "--self-contained", "true", "-o", $selfContainedOut
) + $commonProperties) "Self-contained scanner publish"

$vcRedist = Find-VCRedistDirectory
$fddVcFiles = Copy-AppLocalVCRuntime $fddOut $vcRedist
$selfContainedVcFiles = Copy-AppLocalVCRuntime $selfContainedOut $vcRedist

foreach ($directory in @($fddOut, $selfContainedOut)) {
    $scannerExe = Join-Path $directory "ZZZ-Scanner.Next.exe"
    $publishedVersion = (Get-Item $scannerExe).VersionInfo.FileVersion
    if ([System.Version]$publishedVersion -ne [System.Version]$assemblyVersion) {
        throw "Published scanner version mismatch. Expected $assemblyVersion, got $publishedVersion."
    }
    Assert-PublishPayload $directory
}

$fddDependencyReport = Assert-PeDependencyClosure $fddOut
$selfContainedDependencyReport = Assert-PeDependencyClosure $selfContainedOut
$helperDependencyReport = Assert-PeDependencyClosure $helperOut

$modelRelative = "Resources\models\PP-OCRv5_mobile_rec_infer.onnx"
$fddModelHash = (Get-FileHash -Algorithm SHA256 (Join-Path $fddOut $modelRelative)).Hash
$selfContainedModelHash = (Get-FileHash -Algorithm SHA256 (Join-Path $selfContainedOut $modelRelative)).Hash
if ($fddModelHash -ne $selfContainedModelHash) {
    throw "FDD and self-contained packages contain different OCR models."
}

New-Zip $fddOut $fddZip
New-Zip $selfContainedOut $selfContainedZip
Assert-MaxSize $fddZip $MaxFddMiB "Framework-dependent scanner" $fddOut
Assert-MaxSize $selfContainedZip $MaxSelfContainedMiB "Self-contained scanner" $selfContainedOut
Assert-MaxSize (Join-Path $helperOut "ZZZ-Scanner-Helper.exe") $MaxHelperMiB "NativeAOT Helper" $helperOut

function New-PackageManifest([string]$Id, [string]$Mode, [string]$ZipPath, [string]$PublishDirectory, [object]$Framework) {
    $name = Split-Path -Leaf $ZipPath
    $package = [ordered]@{
        id = $Id
        mode = $Mode
        packageUrls = @(
            "./$Version/$name",
            "https://github.com/ZztIsolation/zzz_calculator/releases/download/scanner-$Version/$name"
        )
        sha256 = (Get-FileHash -Algorithm SHA256 $ZipPath).Hash.ToLowerInvariant()
        size = (Get-Item $ZipPath).Length
        expandedSize = Get-ExpandedSize $PublishDirectory
        entry = "ZZZ-Scanner.Next.exe"
        files = @(Get-ChildItem $PublishDirectory -File -Recurse | Sort-Object FullName | ForEach-Object {
            [ordered]@{
                path = [System.IO.Path]::GetRelativePath($PublishDirectory, $_.FullName).Replace('\', '/')
                size = $_.Length
                sha256 = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant()
            }
        })
    }
    if ($null -ne $Framework) {
        $package.framework = $Framework
    }
    return $package
}

$manifest = [ordered]@{
    schemaVersion = 3
    launcherMinVersion = "1.2.0"
    scannerVersion = $Version
    support = [ordered]@{
        os = "windows"
        architectures = @("x64")
        minWindowsBuild = 17763
    }
    packages = @(
        (New-PackageManifest "win-x64-fdd" "framework-dependent" $fddZip $fddOut ([ordered]@{
            name = "Microsoft.WindowsDesktop.App"
            major = 8
            minVersion = "8.0.0"
        })),
        (New-PackageManifest "win-x64-self-contained" "self-contained" $selfContainedZip $selfContainedOut $null)
    )
}
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

$helperExe = Join-Path $helperOut "ZZZ-Scanner-Helper.exe"
$helperManifest = [ordered]@{
    schemaVersion = 1
    version = $helperVersion
    packageUrls = @(
        "https://github.com/ZztIsolation/zzz_calculator/releases/download/scanner-$Version/ZZZ-Scanner-Helper.exe"
    )
    sha256 = (Get-FileHash -Algorithm SHA256 $helperExe).Hash.ToLowerInvariant()
    size = (Get-Item $helperExe).Length
}
$helperManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $helperManifestPath -Encoding UTF8

$report = [ordered]@{
    version = $Version
    createdAt = [DateTimeOffset]::Now.ToString("O")
    vcRuntimeSource = $vcRedist.Source
    vcRuntimePath = $vcRedist.Path
    vcRuntimeLicense = "Microsoft Visual Studio redistributable license; VC-RUNTIME-NOTICE.txt is included in each scanner package."
    vcRuntimeFiles = $fddVcFiles
    peDependencies = [ordered]@{
        helper = $helperDependencyReport
        frameworkDependent = $fddDependencyReport
        selfContained = $selfContainedDependencyReport
    }
    modelSha256 = $fddModelHash.ToLowerInvariant()
    helper = [ordered]@{
        path = $helperExe
        version = $helperVersion
        size = (Get-Item $helperExe).Length
        sha256 = $helperManifest.sha256
    }
    packages = $manifest.packages
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host "helper_exe=$($report.helper.path)"
Write-Host "helper_size=$($report.helper.size)"
Write-Host "fdd_zip=$fddZip"
Write-Host "fdd_zip_size=$($manifest.packages[0].size)"
Write-Host "self_contained_zip=$selfContainedZip"
Write-Host "self_contained_zip_size=$($manifest.packages[1].size)"
Write-Host "manifest=$manifestPath"
Write-Host "helper_manifest=$helperManifestPath"
Write-Host "report=$reportPath"
