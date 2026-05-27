#!/usr/bin/env pwsh
# publish-local.ps1 — Build and pack Veldrid.SPIRV package to local NuGet feed
#
# Versioning is timestamp-based (v2) — every build gets a unique version
# automatically via VeldridSpirv.Build.props. No version files to manage.
# The timestamp is captured once here and passed to MSBuild so all projects
# get the exact same version (no inter-project skew).
#
# Usage:
#   ./cmd/publish-local.ps1                    # Debug build + pack + deploy
#   ./cmd/publish-local.ps1 -Release           # Release configuration
#
# Requires: LOCAL_NUGET_REPO environment variable set to local feed path

param(
    [switch]$Release
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSCommandPath)
$projectPath = Join-Path $repoRoot 'src\Veldrid.SPIRV\Veldrid.SPIRV.csproj'
$configuration = if ($Release) { "Release" } else { "Debug" }

Write-Host "=== Veldrid.SPIRV publish-local ($configuration) ===" -ForegroundColor Cyan

if (-not $env:LOCAL_NUGET_REPO) {
    Write-Host "ERROR: LOCAL_NUGET_REPO environment variable not set." -ForegroundColor Red
    Write-Host '$env:LOCAL_NUGET_REPO = "C:\PROJECTS\LocalNuGet"' -ForegroundColor Yellow
    exit 1
}

Write-Host "Local NuGet feed: $env:LOCAL_NUGET_REPO" -ForegroundColor Gray

# ── Locate native DLLs ──────────────────────────────────────────────────────
# The csproj picks up native assets from build\<Configuration>\<rid>\.
# We build the native library in Release, but pack the managed assembly in
# Debug.  To bridge the gap, copy Release native DLLs into the Debug tree
# so the pack step can find them.  If neither Release nor Debug exist,
# fail early — the native library hasn't been built.

$rids = @("win-x64", "win-x86", "win-arm64")
$dllName = "libveldrid-spirv.dll"
$foundAny = $false

foreach ($rid in $rids) {
    $releaseDll = Join-Path $repoRoot "build\Release\$rid\$dllName"
    $debugDir   = Join-Path $repoRoot "build\Debug\$rid"
    $debugDll   = Join-Path $debugDir $dllName

    if (Test-Path $releaseDll) {
        if (-not (Test-Path $debugDir)) {
            New-Item -ItemType Directory -Force -Path $debugDir | Out-Null
        }
        Copy-Item $releaseDll $debugDll -Force
        Write-Host "  Native DLL ($rid): copied Release -> Debug" -ForegroundColor DarkGray
        $foundAny = $true
    } elseif (Test-Path $debugDll) {
        Write-Host "  Native DLL ($rid): using existing Debug build" -ForegroundColor DarkGray
        $foundAny = $true
    } else {
        Write-Host "  Native DLL ($rid): not found (skipped)" -ForegroundColor Yellow
    }
}

if (-not $foundAny) {
    Write-Error "No native binaries found under build\Release or build\Debug for any platform. Build the native library first (see build-native.cmd)."
    exit 1
}

# Require at least win-x64 — that's the primary development platform.
$requiredDll = Join-Path $repoRoot "build\Debug\win-x64\$dllName"
if (-not (Test-Path $requiredDll)) {
    Write-Error "Required native binary not found at '$requiredDll'. You must build at least win-x64 before publishing."
    exit 1
}

# ── Verify native DLL ABI version ──────────────────────────────────────────────
# Load the ABI version expected by managed code from the single-source file.
$abiVersionFile = Join-Path $repoRoot 'NATIVE_ABI_VERSION'
if (-not (Test-Path $abiVersionFile)) {
    Write-Error "NATIVE_ABI_VERSION file not found at '$abiVersionFile'."
    exit 1
}
$expectedAbi = (Get-Content $abiVersionFile -Raw).Trim()
Write-Host "`nVerifying native ABI version (expected: $expectedAbi)..." -ForegroundColor Cyan

$abiCheckProject = Join-Path $repoRoot 'src\Veldrid.SPIRV.AbiCheck\Veldrid.SPIRV.AbiCheck.csproj'
$nativeDllDir = Join-Path $repoRoot "build\Release\win-x64"

dotnet run --project $abiCheckProject -- $nativeDllDir $expectedAbi
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Error "Native ABI version check FAILED. The native DLL does not match the expected ABI version. Rebuild with: build-native.cmd release win-x64"
    exit 1
}
Write-Host ""

# Capture timestamp ONCE so the build and pack get the exact same version
$now = [System.DateTime]::Now
$buildYYMM   = $now.ToString('yyMM')
$buildDDHH   = $now.ToString('ddHH')
$buildmmss   = $now.ToString('mmss')
$buildYYMMDD = $now.ToString('yyMMdd')
$buildHHmmss = $now.ToString('HHmmss')
$versionProps = "/p:_BuildYYMM=$buildYYMM", "/p:_BuildDDHH=$buildDDHH", "/p:_Buildmmss=$buildmmss", "/p:_BuildYYMMDD=$buildYYMMDD", "/p:_BuildHHmmss=$buildHHmmss"

Write-Host "Version stamp: 5.$buildYYMM.$buildDDHH.$buildmmss (pkg: 5.$buildYYMMDD.$buildHHmmss)" -ForegroundColor Gray

# Capture deploy start time so we can identify newly deployed packages
$deployStartTime = Get-Date

# Build
Write-Host "`n[1/3] Cleaning..." -ForegroundColor Green
dotnet clean $projectPath -c $configuration -v quiet
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`n[2/3] Building..." -ForegroundColor Green
dotnet build $projectPath -c $configuration @versionProps
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "`n[3/3] Packing..." -ForegroundColor Green
dotnet pack $projectPath -c $configuration --no-build @versionProps
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Deploy to local NuGet feed
$packageOutputDir = Join-Path $repoRoot "bin\Packages\$configuration"
$nupkgFiles = Get-ChildItem $packageOutputDir -Filter "*.nupkg" -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -ge $deployStartTime }

if ($nupkgFiles.Count -eq 0) {
    Write-Error "No .nupkg files found in '$packageOutputDir'. Pack may have failed silently."
    exit 1
}

foreach ($nupkgFile in $nupkgFiles) {
    $destinationPath = Join-Path $env:LOCAL_NUGET_REPO $nupkgFile.Name
    Copy-Item $nupkgFile.FullName $destinationPath -Force
}

# Show only packages deployed during this run
$deployedPackages = Get-ChildItem "$env:LOCAL_NUGET_REPO\*.nupkg" -ErrorAction SilentlyContinue |
    Where-Object { $_.LastWriteTime -ge $deployStartTime } |
    Sort-Object Name
if ($deployedPackages) {
    Write-Host "`nDeployed packages:" -ForegroundColor Cyan
    foreach ($deployedPackage in $deployedPackages) {
        $sizeKB = [math]::Round($deployedPackage.Length / 1024, 1)
        Write-Host "  $($deployedPackage.Name)  (${sizeKB} KB)" -ForegroundColor Green
    }
} else {
    Write-Host "`nWARNING: No packages were deployed to $env:LOCAL_NUGET_REPO" -ForegroundColor Yellow
}