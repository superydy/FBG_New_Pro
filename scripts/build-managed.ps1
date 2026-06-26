param(
    [string]$Configuration = "Release",
    [string]$SdkRoot = "D:/WorkSpace/DTS/DTS SDK V1.0 20250625"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$appDir = Join-Path $repoRoot "managed\DtsMonitor.App"
$nativeDll = Join-Path $repoRoot "native\build\$Configuration\dts_core.dll"
$publishScript = Join-Path $PSScriptRoot "publish-runtime.ps1"

Push-Location $appDir
try {
    dotnet restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed." }
    dotnet build -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed." }
}
finally {
    Pop-Location
}

if (Test-Path -LiteralPath $nativeDll) {
    & $publishScript -Configuration $Configuration -SdkRoot $SdkRoot
    Write-Host "Managed build completed and runtime synced: $appDir"
}
else {
    Write-Host "Managed build completed: $appDir"
    Write-Host "Runtime sync skipped because native output does not exist: $nativeDll"
}
