param(
    [string]$Version,
    [string]$OutputRoot,
    [string]$HelperReleaseTag,
    [string]$VCRedistDirectory,
    [string]$MinimumVCRuntimeVersion = "14.44.35211",
    [string]$OcrRuntimeSmokeFixture,
    [int]$OcrRuntimeSmokeTimeoutSeconds = 120,
    [switch]$HelperOnly,
    [switch]$ScannerOnly,
    [switch]$RequireVCRedistLayout,
    [int]$MaxFddMiB = 25,
    [int]$MaxSelfContainedMiB = 90,
    [int]$MaxHelperMiB = 10
)

$ErrorActionPreference = "Stop"

if ($HelperOnly -and $ScannerOnly) {
    throw "HelperOnly and ScannerOnly cannot be used together."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

[xml]$scannerProject = Get-Content (Join-Path $repoRoot "ZZZ-Scanner.Next.csproj")
$scannerProjectVersion = $scannerProject.Project.PropertyGroup |
    ForEach-Object { [string]$_.Version } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($scannerProjectVersion)) {
    throw "Scanner project Version is missing."
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $scannerProjectVersion
}

$parsedVersion = $null
if (-not [System.Version]::TryParse($Version, [ref]$parsedVersion) -or $Version.Split('.').Count -ne 3) {
    throw "Version must use major.minor.patch format, for example 1.0.37. Got: $Version"
}
if ($Version -ne $scannerProjectVersion) {
    throw "Release version must match ZZZ-Scanner.Next.csproj. Project: $scannerProjectVersion; requested: $Version."
}

$assemblyVersion = "$($parsedVersion.Major).$($parsedVersion.Minor).$($parsedVersion.Build).0"
$minimumVCRuntime = $null
if (-not [System.Version]::TryParse($MinimumVCRuntimeVersion, [ref]$minimumVCRuntime)) {
    throw "MinimumVCRuntimeVersion must be a valid version. Got: $MinimumVCRuntimeVersion"
}
if ($OcrRuntimeSmokeTimeoutSeconds -lt 1 -or $OcrRuntimeSmokeTimeoutSeconds -gt 600) {
    throw "OcrRuntimeSmokeTimeoutSeconds must be between 1 and 600."
}
[xml]$helperProject = Get-Content (Join-Path $repoRoot "Launcher\ZZZ-Scanner.Helper.csproj")
$helperVersion = $helperProject.Project.PropertyGroup |
    ForEach-Object { [string]$_.Version } |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    Select-Object -First 1
$parsedHelperVersion = $null
if (-not [System.Version]::TryParse($helperVersion, [ref]$parsedHelperVersion)) {
    throw "Helper project Version is invalid: $helperVersion"
}
$helperProgram = Get-Content (Join-Path $repoRoot "Launcher\Program.cs") -Raw
$helperVersionMatch = [regex]::Match($helperProgram, 'internal\s+const\s+string\s+HelperVersion\s*=\s*"([^"]+)"')
if (-not $helperVersionMatch.Success) {
    throw "Launcher Program.HelperVersion constant was not found."
}
$helperProgramVersion = $helperVersionMatch.Groups[1].Value
if ($helperVersion -ne $helperProgramVersion) {
    throw "Helper version mismatch. Project: $helperVersion; Program.HelperVersion: $helperProgramVersion."
}

$scannerSourceRevisionId = (& git rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $scannerSourceRevisionId -notmatch '^[0-9a-f]{40}$') {
    throw "Unable to resolve the Scanner source revision."
}
$immutableHelperSourceRevisions = @{
    "1.3.1" = "7f88ad9c0e8fbcf4baf0f9f39d51af4fa85b13f1"
}
$immutableHelperArtifacts = @{
    "1.3.1" = [ordered]@{
        size = 8267264
        sha256 = "01f1b5abbe30ecae7668d6339f82eebe8d1c6f91a6dbbbaa8bba5582948ddd0d"
    }
}
$helperSourceRevisionId = $immutableHelperSourceRevisions[$helperVersion]
if ([string]::IsNullOrWhiteSpace($helperSourceRevisionId)) {
    $helperSourceRevisionId = $scannerSourceRevisionId
}

function Get-TaggedCommit([string]$Tag) {
    $revision = (& git rev-parse --verify "refs/tags/$Tag`^{commit}" 2>$null)
    if ($LASTEXITCODE -ne 0) {
        return $null
    }
    return $revision.Trim()
}

$scannerReleaseTag = "scanner-$Version"
$taggedScannerRevision = Get-TaggedCommit $scannerReleaseTag
if (-not [string]::IsNullOrWhiteSpace($taggedScannerRevision) -and $taggedScannerRevision -ne $scannerSourceRevisionId) {
    throw "Immutable tag $scannerReleaseTag points to $taggedScannerRevision, not current source $scannerSourceRevisionId. Bump the Scanner version before publishing."
}
if ([string]::IsNullOrWhiteSpace($HelperReleaseTag)) {
    $HelperReleaseTag = "helper-$helperVersion"
}
if ($HelperReleaseTag -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') {
    throw "Helper release tag contains unsupported characters: $HelperReleaseTag"
}
$taggedHelperRevision = Get-TaggedCommit $HelperReleaseTag
if (-not [string]::IsNullOrWhiteSpace($taggedHelperRevision) -and $taggedHelperRevision -ne $helperSourceRevisionId) {
    throw "Immutable tag $HelperReleaseTag points to $taggedHelperRevision, not Helper source $helperSourceRevisionId. Bump the Helper version before publishing."
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
$smokeFixturePath = if ([string]::IsNullOrWhiteSpace($OcrRuntimeSmokeFixture)) {
    Join-Path $repoRoot "Tests\Fixtures\ocr-equivalence.png.b64"
}
elseif ([System.IO.Path]::IsPathRooted($OcrRuntimeSmokeFixture)) {
    [System.IO.Path]::GetFullPath($OcrRuntimeSmokeFixture)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OcrRuntimeSmokeFixture))
}

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

function Get-VCRedistCandidateDirectories([string]$Root) {
    if ([string]::IsNullOrWhiteSpace($Root) -or -not (Test-Path -LiteralPath $Root -PathType Container)) {
        return @()
    }

    $fullRoot = [System.IO.Path]::GetFullPath($Root)
    $candidates = @(
        $fullRoot,
        (Join-Path $fullRoot "x64\Microsoft.VC143.CRT")
    )
    foreach ($redistRoot in @($fullRoot, (Join-Path $fullRoot "VC\Redist\MSVC"))) {
        if (-not (Test-Path -LiteralPath $redistRoot -PathType Container)) {
            continue
        }
        $candidates += Get-ChildItem -LiteralPath $redistRoot -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName "x64\Microsoft.VC143.CRT" }
    }
    return @($candidates | Where-Object { Test-Path (Join-Path $_ "msvcp140.dll") } | Select-Object -Unique)
}

function Get-VCRuntimeFileVersion([System.IO.FileInfo]$File) {
    $match = [regex]::Match([string]$File.VersionInfo.FileVersion, '\d+(?:\.\d+){2,3}')
    if (-not $match.Success) {
        throw "VC runtime file has no parseable version: $($File.FullName)"
    }
    return [System.Version]$match.Value
}

function Get-VCRedistLayoutInfo([string]$Path, [string]$Selection) {
    $required = @("msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll", "vcruntime140_1.dll")
    $optional = @("msvcp140_2.dll", "msvcp140_atomic_wait.dll", "msvcp140_codecvt_ids.dll", "vcruntime140_threads.dll")
    $files = @()
    foreach ($name in $required + $optional) {
        $filePath = Join-Path $Path $name
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            if ($required -contains $name) {
                return $null
            }
            continue
        }
        $item = Get-Item -LiteralPath $filePath
        $files += [pscustomobject]@{ Name = $name; Version = Get-VCRuntimeFileVersion $item }
    }

    $runtimeVersion = ($files | Where-Object Name -eq "msvcp140.dll" | Select-Object -First 1).Version
    $lowestFileVersion = ($files | Sort-Object Version | Select-Object -First 1).Version
    return [pscustomobject]@{
        Path = [System.IO.Path]::GetFullPath($Path)
        Source = "vc-redist-layout"
        Selection = $Selection
        RuntimeVersion = $runtimeVersion
        RuntimeVersionText = $runtimeVersion.ToString()
        LowestFileVersion = $lowestFileVersion
        LowestFileVersionText = $lowestFileVersion.ToString()
    }
}

function Find-VCRedistDirectory {
    $candidateRoots = @()
    if (-not [string]::IsNullOrWhiteSpace($VCRedistDirectory)) {
        $candidateRoots += [pscustomobject]@{ Path = $VCRedistDirectory; Selection = "explicit" }
    }
    else {
        if (-not [string]::IsNullOrWhiteSpace($env:VCToolsRedistDir)) {
            $candidateRoots += [pscustomobject]@{ Path = $env:VCToolsRedistDir; Selection = "environment" }
        }

        $vswherePaths = @(
            (Get-Command vswhere.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
            "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
        ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -Unique
        foreach ($vswherePath in $vswherePaths) {
            $installations = & $vswherePath -all -products * -property installationPath
            if ($LASTEXITCODE -ne 0) {
                throw "vswhere failed with exit code $LASTEXITCODE."
            }
            foreach ($installation in $installations) {
                if (-not [string]::IsNullOrWhiteSpace($installation)) {
                    $candidateRoots += [pscustomobject]@{ Path = $installation.Trim(); Selection = "vswhere" }
                }
            }
        }
    }

    $layouts = @()
    foreach ($root in $candidateRoots) {
        foreach ($candidate in Get-VCRedistCandidateDirectories $root.Path) {
            $info = Get-VCRedistLayoutInfo $candidate $root.Selection
            if ($null -ne $info) {
                $layouts += $info
            }
        }
    }
    $layouts = @($layouts | Sort-Object Path -Unique)
    $eligible = @($layouts | Where-Object { $_.LowestFileVersion -ge $minimumVCRuntime } | Sort-Object RuntimeVersion -Descending)
    if ($eligible.Count -gt 0) {
        return $eligible[0]
    }

    $found = if ($layouts.Count -eq 0) {
        "none"
    }
    else {
        ($layouts | ForEach-Object { "$($_.Path)=$($_.RuntimeVersionText)" }) -join "; "
    }
    $explicitHint = if ([string]::IsNullOrWhiteSpace($VCRedistDirectory)) { "" } else { " Explicit path: $VCRedistDirectory." }
    throw "Release publishing requires one app-local VC runtime layout at version $MinimumVCRuntimeVersion or newer; System32 fallback is forbidden. Found: $found.$explicitHint"
}

function Copy-AppLocalVCRuntime([string]$Destination, [object]$VCRedist) {
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
        "Runtime version: $($VCRedist.RuntimeVersionText)",
        "Minimum required version: $MinimumVCRuntimeVersion",
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

function Set-DeterministicPeTimestamps([string]$Path) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 0x40) {
        throw "PE file is too small: $Path"
    }
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3c)
    if ($peOffset -lt 0 -or $peOffset + 24 -gt $bytes.Length -or
        [BitConverter]::ToUInt32($bytes, $peOffset) -ne 0x00004550) {
        throw "Invalid PE signature: $Path"
    }

    $fixedTimestamp = [BitConverter]::GetBytes([uint32]0)
    [Array]::Copy($fixedTimestamp, 0, $bytes, $peOffset + 8, 4)

    $sectionCount = [BitConverter]::ToUInt16($bytes, $peOffset + 6)
    $optionalSize = [BitConverter]::ToUInt16($bytes, $peOffset + 20)
    $optionalOffset = $peOffset + 24
    $magic = [BitConverter]::ToUInt16($bytes, $optionalOffset)
    $dataDirectoryOffset = switch ($magic) {
        0x10b { $optionalOffset + 96 }
        0x20b { $optionalOffset + 112 }
        default { throw "Unsupported PE optional-header magic 0x$($magic.ToString('x')): $Path" }
    }
    $debugDirectoryEntry = $dataDirectoryOffset + (6 * 8)
    $debugRva = [BitConverter]::ToUInt32($bytes, $debugDirectoryEntry)
    $debugSize = [BitConverter]::ToUInt32($bytes, $debugDirectoryEntry + 4)
    if ($debugRva -ne 0 -and $debugSize -ge 28) {
        $sectionOffset = $optionalOffset + $optionalSize
        $debugOffset = $null
        for ($index = 0; $index -lt $sectionCount; $index++) {
            $entry = $sectionOffset + ($index * 40)
            $virtualSize = [BitConverter]::ToUInt32($bytes, $entry + 8)
            $virtualAddress = [BitConverter]::ToUInt32($bytes, $entry + 12)
            $rawSize = [BitConverter]::ToUInt32($bytes, $entry + 16)
            $rawOffset = [BitConverter]::ToUInt32($bytes, $entry + 20)
            $mappedSize = [Math]::Max($virtualSize, $rawSize)
            if ($debugRva -ge $virtualAddress -and $debugRva -lt $virtualAddress + $mappedSize) {
                $debugOffset = [int]($rawOffset + ($debugRva - $virtualAddress))
                break
            }
        }
        if ($null -eq $debugOffset -or $debugOffset + $debugSize -gt $bytes.Length) {
            throw "PE debug directory is outside mapped sections: $Path"
        }
        for ($offset = $debugOffset; $offset + 28 -le $debugOffset + $debugSize; $offset += 28) {
            [Array]::Copy($fixedTimestamp, 0, $bytes, $offset + 4, 4)
        }
    }

    [System.IO.File]::WriteAllBytes($Path, $bytes)
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
$outputPaths = @($helperOut, $helperManifestPath)
if (-not $HelperOnly) {
    $outputPaths += @($fddOut, $selfContainedOut, $fddZip, $selfContainedZip, $manifestPath, $reportPath)
}

function Invoke-OcrRuntimeSmoke([string]$Directory, [string]$Label) {
    $scannerExe = Join-Path $Directory "ZZZ-Scanner.Next.exe"
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $scannerExe
    $startInfo.WorkingDirectory = $Directory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    [void]$startInfo.ArgumentList.Add("--ocr-runtime-smoke")
    [void]$startInfo.ArgumentList.Add($smokeFixturePath)

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw "$Label OCR runtime smoke could not start."
    }
    try {
        if (-not $process.WaitForExit($OcrRuntimeSmokeTimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "$Label OCR runtime smoke timed out after $OcrRuntimeSmokeTimeoutSeconds seconds."
        }
        $stdout = $process.StandardOutput.ReadToEnd().Trim()
        $stderr = $process.StandardError.ReadToEnd().Trim()
        $exitCode = $process.ExitCode
        if ($exitCode -ne 0) {
            $unsignedExitCode = [BitConverter]::ToUInt32([BitConverter]::GetBytes([int]$exitCode), 0)
            throw "$Label OCR runtime smoke failed with exit code $exitCode (0x$($unsignedExitCode.ToString('X8'))). stdout=$stdout stderr=$stderr"
        }
        if ([string]::IsNullOrWhiteSpace($stdout)) {
            throw "$Label OCR runtime smoke returned no JSON output. stderr=$stderr"
        }
        try {
            $result = $stdout | ConvertFrom-Json
        }
        catch {
            throw "$Label OCR runtime smoke returned invalid JSON: $stdout"
        }
        if ($result.ok -ne $true) {
            throw "$Label OCR runtime smoke reported failure: $stdout"
        }
        Write-Host "$Label OCR runtime smoke passed in $($result.elapsedMs) ms."
        return $result
    }
    finally {
        $process.Dispose()
    }
}
foreach ($path in $outputPaths) {
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

$vcRedist = $null
if (-not $HelperOnly) {
    if (-not (Test-Path -LiteralPath $smokeFixturePath -PathType Leaf)) {
        throw "OCR runtime smoke fixture was not found: $smokeFixturePath"
    }
    $vcRedist = Find-VCRedistDirectory
}

$helperExe = $null
$helperDependencyReport = $null
if (-not $ScannerOnly) {
    $helperPublishArguments = @(
        "publish", "Launcher\ZZZ-Scanner.Helper.csproj", "-c", "Release", "-r", "win-x64",
        "--self-contained", "true", "-o", $helperOut
    )
    if (-not [string]::IsNullOrWhiteSpace($helperSourceRevisionId)) {
        $helperPublishArguments += "-p:SourceRevisionId=$helperSourceRevisionId"
    }
    Invoke-DotnetPublish $helperPublishArguments "Helper publish"
    $helperExe = Join-Path $helperOut "ZZZ-Scanner-Helper.exe"
    Set-DeterministicPeTimestamps $helperExe
}

if ($HelperOnly) {
    $helperDependencyReport = Assert-PeDependencyClosure $helperOut
    $helperExe = Join-Path $helperOut "ZZZ-Scanner-Helper.exe"
    Assert-MaxSize $helperExe $MaxHelperMiB "NativeAOT Helper" $helperOut
    $helperManifest = [ordered]@{
        schemaVersion = 1
        version = $helperVersion
        packageUrls = @(
            "https://download.zzzcaculator.top/downloads/zzz-scanner/helper/$helperVersion/ZZZ-Scanner-Helper.exe",
            "https://zzzcaculator.top/downloads/zzz-scanner/helper/$helperVersion/ZZZ-Scanner-Helper.exe",
            "https://github.com/ZztIsolation/ZZZ-Scanner.Next/releases/download/$HelperReleaseTag/ZZZ-Scanner-Helper.exe"
        )
        sha256 = (Get-FileHash -Algorithm SHA256 $helperExe).Hash.ToLowerInvariant()
        size = (Get-Item $helperExe).Length
    }
    $helperManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $helperManifestPath -Encoding UTF8
    Write-Host "helper_exe=$helperExe"
    Write-Host "helper_size=$((Get-Item $helperExe).Length)"
    Write-Host "helper_manifest=$helperManifestPath"
    return
}

Invoke-DotnetPublish (@(
    "publish", "ZZZ-Scanner.Next.csproj", "-c", "Release", "-r", "win-x64",
    "--self-contained", "false", "-o", $fddOut
) + $commonProperties) "Framework-dependent scanner publish"
Invoke-DotnetPublish (@(
    "publish", "ZZZ-Scanner.Next.csproj", "-c", "Release", "-r", "win-x64",
    "--self-contained", "true", "-o", $selfContainedOut
) + $commonProperties) "Self-contained scanner publish"

$fddVcFiles = Copy-AppLocalVCRuntime $fddOut $vcRedist
$selfContainedVcFiles = Copy-AppLocalVCRuntime $selfContainedOut $vcRedist
if (($fddVcFiles | ConvertTo-Json -Depth 4 -Compress) -ne ($selfContainedVcFiles | ConvertTo-Json -Depth 4 -Compress)) {
    throw "FDD and self-contained packages received different app-local VC runtime files."
}

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
$helperDependencyReport = if ($ScannerOnly) { $null } else { Assert-PeDependencyClosure $helperOut }

$modelRelative = "Resources\models\PP-OCRv5_mobile_rec_infer.onnx"
$fddModelHash = (Get-FileHash -Algorithm SHA256 (Join-Path $fddOut $modelRelative)).Hash
$selfContainedModelHash = (Get-FileHash -Algorithm SHA256 (Join-Path $selfContainedOut $modelRelative)).Hash
if ($fddModelHash -ne $selfContainedModelHash) {
    throw "FDD and self-contained packages contain different OCR models."
}

$fddSmoke = Invoke-OcrRuntimeSmoke $fddOut "Framework-dependent scanner"
$selfContainedSmoke = Invoke-OcrRuntimeSmoke $selfContainedOut "Self-contained scanner"

New-Zip $fddOut $fddZip
New-Zip $selfContainedOut $selfContainedZip
Assert-MaxSize $fddZip $MaxFddMiB "Framework-dependent scanner" $fddOut
Assert-MaxSize $selfContainedZip $MaxSelfContainedMiB "Self-contained scanner" $selfContainedOut
if (-not $ScannerOnly) {
    Assert-MaxSize (Join-Path $helperOut "ZZZ-Scanner-Helper.exe") $MaxHelperMiB "NativeAOT Helper" $helperOut
}

function New-PackageManifest([string]$Id, [string]$Mode, [string]$ZipPath, [string]$PublishDirectory, [object]$Framework) {
    $name = Split-Path -Leaf $ZipPath
    $package = [ordered]@{
        id = $Id
        mode = $Mode
        packageUrls = @(
            "https://download.zzzcaculator.top/downloads/zzz-scanner/$Version/$name",
            "https://zzzcaculator.top/downloads/zzz-scanner/$Version/$name",
            "./$Version/$name",
            "https://github.com/ZztIsolation/ZZZ-Scanner.Next/releases/download/scanner-$Version/$name"
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
    launcherMinVersion = $helperVersion
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

$helperArtifact = if ($ScannerOnly) { $immutableHelperArtifacts[$helperVersion] } else { $null }
if ($ScannerOnly -and $null -eq $helperArtifact) {
    throw "Scanner-only release requires frozen Helper artifact metadata for version $helperVersion."
}
if (-not $ScannerOnly) {
    $helperExe = Join-Path $helperOut "ZZZ-Scanner-Helper.exe"
    $helperManifest = [ordered]@{
        schemaVersion = 1
        version = $helperVersion
        packageUrls = @(
            "https://download.zzzcaculator.top/downloads/zzz-scanner/helper/$helperVersion/ZZZ-Scanner-Helper.exe",
            "https://zzzcaculator.top/downloads/zzz-scanner/helper/$helperVersion/ZZZ-Scanner-Helper.exe",
            "https://github.com/ZztIsolation/ZZZ-Scanner.Next/releases/download/$HelperReleaseTag/ZZZ-Scanner-Helper.exe"
        )
        sha256 = (Get-FileHash -Algorithm SHA256 $helperExe).Hash.ToLowerInvariant()
        size = (Get-Item $helperExe).Length
    }
    $helperManifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $helperManifestPath -Encoding UTF8
}

$report = [ordered]@{
    version = $Version
    scannerSourceRevisionId = $scannerSourceRevisionId
    helperReleaseTag = $HelperReleaseTag
    helperSourceRevisionId = $helperSourceRevisionId
    createdAt = [DateTimeOffset]::Now.ToString("O")
    vcRuntimeSource = $vcRedist.Source
    vcRuntimePath = $vcRedist.Path
    vcRuntimeSelection = $vcRedist.Selection
    vcRuntimeVersion = $vcRedist.RuntimeVersionText
    vcRuntimeLowestFileVersion = $vcRedist.LowestFileVersionText
    vcRuntimeMinimumVersion = $MinimumVCRuntimeVersion
    vcRuntimeLicense = "Microsoft Visual Studio redistributable license; VC-RUNTIME-NOTICE.txt is included in each scanner package."
    vcRuntimeFiles = $fddVcFiles
    ocrRuntimeSmoke = [ordered]@{
        fixture = $smokeFixturePath
        timeoutSeconds = $OcrRuntimeSmokeTimeoutSeconds
        frameworkDependent = $fddSmoke
        selfContained = $selfContainedSmoke
    }
    peDependencies = [ordered]@{
        helper = $helperDependencyReport
        frameworkDependent = $fddDependencyReport
        selfContained = $selfContainedDependencyReport
    }
    modelSha256 = $fddModelHash.ToLowerInvariant()
    helper = [ordered]@{
        built = -not $ScannerOnly
        path = if ($ScannerOnly) { $null } else { $helperExe }
        version = $helperVersion
        size = if ($ScannerOnly) { $helperArtifact.size } else { (Get-Item $helperExe).Length }
        sha256 = if ($ScannerOnly) { $helperArtifact.sha256 } else { $helperManifest.sha256 }
    }
    packages = $manifest.packages
}
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding UTF8

if ($ScannerOnly) {
    Write-Host "helper_built=false"
    Write-Host "frozen_helper_size=$($report.helper.size)"
    Write-Host "frozen_helper_sha256=$($report.helper.sha256)"
}
else {
    Write-Host "helper_exe=$($report.helper.path)"
    Write-Host "helper_size=$($report.helper.size)"
}
Write-Host "fdd_zip=$fddZip"
Write-Host "fdd_zip_size=$($manifest.packages[0].size)"
Write-Host "self_contained_zip=$selfContainedZip"
Write-Host "self_contained_zip_size=$($manifest.packages[1].size)"
Write-Host "manifest=$manifestPath"
if (-not $ScannerOnly) {
    Write-Host "helper_manifest=$helperManifestPath"
}
Write-Host "report=$reportPath"
