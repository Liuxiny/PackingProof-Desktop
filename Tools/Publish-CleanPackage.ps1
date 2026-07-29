param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "",
    [string]$ZipPath = "",
    [string]$Version = "",
    [string]$SevenZipPath = "",
    [ValidateRange(1, 9)]
    [int]$SevenZipCompressionLevel = 5,
    [ValidateSet("lzma2/normal", "lzma2/max", "lzma2/ultra64")]
    [string]$InstallerCompression = "lzma2/max",
    [switch]$SkipTtsCacheGeneration,
    [switch]$ConfirmManualCoreChecks
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $repoRoot "ExpressPackingMonitoring\ExpressPackingMonitoring.csproj"
$launcherProject = Join-Path $repoRoot "ExpressPackingMonitoring.Launcher\ExpressPackingMonitoring.Launcher.csproj"
$releaseValidationScript = Join-Path $repoRoot "Tools\Test-Release.ps1"
$installerBuildScript = Join-Path $repoRoot "Tools\Build-Installer.ps1"
$ttsCacheBuilderProject = Join-Path $repoRoot "Tools\ExpressPackingMonitoring.TtsCacheBuilder\ExpressPackingMonitoring.TtsCacheBuilder.csproj"

function New-DefaultTtsCache {
    $targetDir = Join-Path $repoRoot "package\tts_cache"
    if ($SkipTtsCacheGeneration) {
        Write-Host "Default TTS cache generation skipped by option."
        return
    }
    if (-not (Test-Path -LiteralPath $ttsCacheBuilderProject -PathType Leaf)) {
        throw "TTS cache builder project not found: $ttsCacheBuilderProject"
    }

    $tempDir = Join-Path $repoRoot "package\.tts_cache_generation"
    if (Test-Path -LiteralPath $tempDir) { Remove-Item -LiteralPath $tempDir -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
    if (Test-Path -LiteralPath $targetDir) {
        Copy-Item -Path (Join-Path $targetDir '*') -Destination $tempDir -Recurse -Force
    }
    try {
        & dotnet run --project $ttsCacheBuilderProject -c $Configuration -- $tempDir
        if ($LASTEXITCODE -ne 0) { throw "Default TTS cache generation failed" }
        $cacheFiles = @(Get-ChildItem -LiteralPath $tempDir -File | Where-Object { $_.Extension -in '.mp3', '.wav' })
        if ($cacheFiles.Count -eq 0) { throw "Default TTS cache generation produced no audio files" }
        if (Test-Path -LiteralPath $targetDir) { Remove-Item -LiteralPath $targetDir -Recurse -Force }
        Move-Item -LiteralPath $tempDir -Destination $targetDir
    }
    finally {
        if (Test-Path -LiteralPath $tempDir) { Remove-Item -LiteralPath $tempDir -Recurse -Force }
    }
}

function Copy-PackageTtsCache {
    param([string]$AppDir)
    $sourceDir = Join-Path $repoRoot "package\tts_cache"
    if (-not (Test-Path -LiteralPath $sourceDir)) { return }
    $targetDir = Join-Path $AppDir "tts_cache"
    if (Test-Path -LiteralPath $targetDir) { Remove-Item -LiteralPath $targetDir -Recurse -Force }
    Copy-Item -LiteralPath $sourceDir -Destination $targetDir -Recurse -Force
}

function Get-NormalizedVersion {
    param([string]$RequestedVersion)
    $value = $RequestedVersion.Trim().TrimStart("vV".ToCharArray())
    if ([string]::IsNullOrWhiteSpace($value)) {
        $tag = (& git -C $repoRoot describe --tags --abbrev=0 2>$null)
        if ($LASTEXITCODE -eq 0) {
            $value = "$tag".Trim().TrimStart("vV".ToCharArray())
        }
    }
    if ([string]::IsNullOrWhiteSpace($value)) { $value = "0.0.0" }
    if ($value -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$') {
        throw "Version must contain 3 or 4 numeric parts: $value"
    }
    return $value
}

function Assert-PathInsideRepository {
    param([string]$PathToCheck, [string]$Name)
    $repoFullPath = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
    $fullPath = [System.IO.Path]::GetFullPath($PathToCheck)
    if (-not $fullPath.StartsWith($repoFullPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name must be inside the repository: $fullPath"
    }
    return $fullPath
}

function Resolve-SevenZip {
    $candidates = @(
        $SevenZipPath,
        (Join-Path $env:ProgramFiles "7-Zip\7z.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "7-Zip\7z.exe")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    $command = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($command) { $candidates += $command.Source }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }
    throw "7-Zip was not found. Install 7-Zip or pass -SevenZipPath."
}

$normalizedVersion = Get-NormalizedVersion $Version
$releaseTag = "v$normalizedVersion"
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $repoRoot "package\ExpressPackingMonitoring_$releaseTag"
}
$outputFullPath = Assert-PathInsideRepository $OutputDir "OutputDir"
$packageRoot = Split-Path -Parent $outputFullPath
if ([string]::IsNullOrWhiteSpace($ZipPath)) {
    $ZipPath = Join-Path $packageRoot "ExpressPackingMonitoring_$releaseTag.zip"
}
$zipFullPath = Assert-PathInsideRepository $ZipPath "ZipPath"
$sevenZipFullPath = [System.IO.Path]::ChangeExtension($zipFullPath, ".7z")
$appPublishDir = Join-Path $outputFullPath "app"
$launcherPublishDir = Join-Path $repoRoot "package\.launcher_publish"

if (-not (Test-Path -LiteralPath $releaseValidationScript -PathType Leaf)) {
    throw "Release validation script not found: $releaseValidationScript"
}
& $releaseValidationScript -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "Release validation failed" }

foreach ($target in @($outputFullPath, $launcherPublishDir)) {
    $verifiedTarget = Assert-PathInsideRepository $target "generated directory"
    if (Test-Path -LiteralPath $verifiedTarget) {
        Remove-Item -LiteralPath $verifiedTarget -Recurse -Force
    }
}
foreach ($target in @($zipFullPath, $sevenZipFullPath)) {
    $verifiedTarget = Assert-PathInsideRepository $target "generated archive"
    if (Test-Path -LiteralPath $verifiedTarget) {
        Remove-Item -LiteralPath $verifiedTarget -Force
    }
}
New-Item -ItemType Directory -Force -Path $appPublishDir, $launcherPublishDir | Out-Null

New-DefaultTtsCache

& dotnet publish $appProject -c $Configuration -r $Runtime --self-contained true `
    -p:Version=$normalizedVersion -p:InformationalVersion=$normalizedVersion -o $appPublishDir
if ($LASTEXITCODE -ne 0) { throw "Main application publish failed" }
$publishedFfmpeg = Join-Path $appPublishDir "tools\ffmpeg.exe"
if (-not (Test-Path -LiteralPath $publishedFfmpeg -PathType Leaf)) {
    $ffmpegCommand = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($null -eq $ffmpegCommand) {
        throw "ffmpeg.exe was not found in the project or system PATH"
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $publishedFfmpeg) | Out-Null
    Copy-Item -LiteralPath $ffmpegCommand.Source -Destination $publishedFfmpeg -Force
}
Copy-PackageTtsCache -AppDir $appPublishDir
if (-not $SkipTtsCacheGeneration) {
    $publishedTtsFiles = @(Get-ChildItem -LiteralPath (Join-Path $appPublishDir "tts_cache") -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -in '.mp3', '.wav' })
    if ($publishedTtsFiles.Count -eq 0) { throw "Clean package validation failed: default TTS cache is empty" }
}

& dotnet publish $launcherProject -c $Configuration -r $Runtime --self-contained true `
    -p:Version=$normalizedVersion -p:InformationalVersion=$normalizedVersion -o $launcherPublishDir
if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed" }

$launcherExe = Join-Path $launcherPublishDir "ExpressPackingMonitoring.exe"
if (-not (Test-Path -LiteralPath $launcherExe -PathType Leaf)) {
    throw "Published launcher is missing: $launcherExe"
}
Copy-Item -LiteralPath $launcherExe -Destination (Join-Path $outputFullPath "ExpressPackingMonitoring.exe") -Force

foreach ($forbiddenName in @("config.json", "videos.db", ".env")) {
    Get-ChildItem -LiteralPath $outputFullPath -Recurse -File -Filter $forbiddenName -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

Compress-Archive -Path (Join-Path $outputFullPath '*') -DestinationPath $zipFullPath -CompressionLevel Optimal
$sevenZipExecutable = Resolve-SevenZip
& $sevenZipExecutable a -t7z "-mx=$SevenZipCompressionLevel" -m0=lzma2 -ms=on $sevenZipFullPath (Join-Path $outputFullPath '*') | Out-Host
if ($LASTEXITCODE -ne 0) { throw "7-Zip package creation failed" }

if (-not (Test-Path -LiteralPath $installerBuildScript -PathType Leaf)) {
    throw "Installer build script not found: $installerBuildScript"
}
& $installerBuildScript -SourceDir $outputFullPath -Version $normalizedVersion `
    -OutputDir $packageRoot -InstallerCompression $InstallerCompression
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }

$setupPath = Join-Path $packageRoot "ExpressPackingMonitoring_Setup_$releaseTag.exe"
$releaseInfoPath = Join-Path $packageRoot "release_info_$releaseTag.txt"
$releaseInfoLines = @(
    "Release upload checklist",
    "Version: $releaseTag",
    "",
    "1. Setup: $(Split-Path -Leaf $setupPath)",
    "2. Portable 7z: $(Split-Path -Leaf $sevenZipFullPath)",
    "3. Compatibility ZIP: $(Split-Path -Leaf $zipFullPath)",
    "",
    "Desktop automatic update and AppPatch artifacts are intentionally disabled.",
    "Users update only by downloading a complete package.",
    "",
    "Setup SHA256: $((Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant())",
    "7z SHA256: $((Get-FileHash -LiteralPath $sevenZipFullPath -Algorithm SHA256).Hash.ToLowerInvariant())",
    "ZIP SHA256: $((Get-FileHash -LiteralPath $zipFullPath -Algorithm SHA256).Hash.ToLowerInvariant())"
)
$releaseInfoLines -join [Environment]::NewLine | Set-Content -LiteralPath $releaseInfoPath -Encoding UTF8

Remove-Item -LiteralPath $launcherPublishDir -Recurse -Force
Write-Host "Clean package created: $outputFullPath"
Write-Host "Installer created: $setupPath"
Write-Host "7z package created: $sevenZipFullPath"
Write-Host "ZIP package created: $zipFullPath"
Write-Host "No update JSON or AppPatch package was generated."
