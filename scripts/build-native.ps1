param(
    [string]$SdkRoot = "D:/WorkSpace/DTS/DTS SDK V1.0 20250625",
    [string]$BuildType = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$nativeDir = Join-Path $repoRoot "native"
$buildDir = Join-Path $nativeDir "build"
$appProject = Join-Path $repoRoot "managed\DtsMonitor.App\DtsMonitor.App.csproj"
$appProjectXml = [xml](Get-Content -LiteralPath $appProject)
$targetFramework = $appProjectXml.Project.PropertyGroup |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_.TargetFramework) } |
    Select-Object -First 1 -ExpandProperty TargetFramework
if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw "Could not determine TargetFramework from $appProject"
}
$appOut = Join-Path $repoRoot "managed\DtsMonitor.App\bin\$BuildType\$targetFramework"
$publishScript = Join-Path $PSScriptRoot "publish-runtime.ps1"

cmake -S $nativeDir -B $buildDir -A x64 "-DDTS_SDK_ROOT=$SdkRoot"
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed." }

cmake --build $buildDir --config $BuildType
if ($LASTEXITCODE -ne 0) { throw "Native build failed." }

if (Test-Path -LiteralPath $appOut) {
    & $publishScript -Configuration $BuildType -SdkRoot $SdkRoot
    Write-Host "Native build completed and runtime synced: $buildDir"
}
else {
    Write-Host "Native build completed: $buildDir"
    Write-Host "Runtime sync skipped because managed output folder does not exist: $appOut"
}
