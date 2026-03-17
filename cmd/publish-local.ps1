$ErrorActionPreference = "Stop"

$localNuGetRepo = $env:LOCAL_NUGET_REPO
if (-not $localNuGetRepo) {
    Write-Error "LOCAL_NUGET_REPO environment variable is not set. Set it to the path of your local NuGet repository folder."
    exit 1
}

if (-not (Test-Path $localNuGetRepo)) {
    Write-Error "LOCAL_NUGET_REPO path '$localNuGetRepo' does not exist. Create the directory or fix the environment variable."
    exit 1
}

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