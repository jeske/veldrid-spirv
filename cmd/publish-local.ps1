$ErrorActionPreference = "Stop"

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
    $releaseDll = "build\Release\$rid\$dllName"
    $debugDir   = "build\Debug\$rid"
    $debugDll   = "$debugDir\$dllName"

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
$requiredDll = "build\Debug\win-x64\$dllName"
if (-not (Test-Path $requiredDll)) {
    Write-Error "Required native binary not found at '$requiredDll'. You must build at least win-x64 before publishing."
    exit 1
}

# ── Validate LOCAL_NUGET_REPO ──────────────────────────────────────────────
$localNuGetRepo = $env:LOCAL_NUGET_REPO
if (-not $localNuGetRepo) {
    Write-Error "LOCAL_NUGET_REPO environment variable is not set. Set it to the path of your local NuGet repository folder."
    exit 1
}

if (-not (Test-Path $localNuGetRepo)) {
    Write-Error "LOCAL_NUGET_REPO path '$localNuGetRepo' does not exist. Create the directory or fix the environment variable."
    exit 1
}

# ── Build, pack, publish ───────────────────────────────────────────────────
$projectPath = "src/Veldrid.SPIRV/Veldrid.SPIRV.csproj"
$configuration = "Debug"

Write-Host "Cleaning..." -ForegroundColor Cyan
dotnet clean $projectPath -c $configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Building..." -ForegroundColor Cyan
dotnet build $projectPath -c $configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Packing..." -ForegroundColor Cyan
dotnet pack $projectPath -c $configuration --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$packageOutputDir = "bin\Packages\$configuration"
$nupkgFiles = Get-ChildItem $packageOutputDir -Filter "*.nupkg"

if ($nupkgFiles.Count -eq 0) {
    Write-Error "No .nupkg files found in '$packageOutputDir'. Pack may have failed silently."
    exit 1
}

foreach ($nupkgFile in $nupkgFiles) {
    $destinationPath = Join-Path $localNuGetRepo $nupkgFile.Name
    Copy-Item $nupkgFile.FullName $destinationPath -Force
    Write-Host "Published: $($nupkgFile.Name) -> $localNuGetRepo" -ForegroundColor Green
}