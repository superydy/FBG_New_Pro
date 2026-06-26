param(
    [string]$Configuration = "Release",
    [string]$SdkRoot = "D:/WorkSpace/DTS/DTS SDK V1.0 20250625"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$nativeDll = Join-Path $repoRoot "native\build\$Configuration\dts_core.dll"
$appProject = Join-Path $repoRoot "managed\DtsMonitor.App\DtsMonitor.App.csproj"
$appProjectXml = [xml](Get-Content -LiteralPath $appProject)
$targetFramework = $appProjectXml.Project.PropertyGroup |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_.TargetFramework) } |
    Select-Object -First 1 -ExpandProperty TargetFramework
if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw "Could not determine TargetFramework from $appProject"
}
$appOut = Join-Path $repoRoot "managed\DtsMonitor.App\bin\$Configuration\$targetFramework"

function Copy-RuntimeFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Source,
        [Parameter(Mandatory = $true)]
        [string]$Destination
    )

    if (!(Test-Path -LiteralPath $Source)) {
        throw "Missing runtime file: $Source"
    }

    try {
        Copy-Item -LiteralPath $Source -Destination $Destination -Force
    }
    catch [System.IO.IOException] {
        throw "Failed to copy '$Source' to '$Destination'. Close HG-FBG and retry. $($_.Exception.Message)"
    }
}

if (!(Test-Path $nativeDll)) {
    throw "Missing $nativeDll. Build native first."
}
if (!(Test-Path $appOut)) {
    throw "Missing $appOut. Build managed first."
}

Copy-RuntimeFile -Source $nativeDll -Destination (Join-Path $appOut "dts_core.dll")
Copy-RuntimeFile -Source (Join-Path $SdkRoot "RC_FBGSystem.dll") -Destination (Join-Path $appOut "RC_FBGSystem.dll")
Copy-RuntimeFile -Source (Join-Path $SdkRoot "opencv_world3412.dll") -Destination (Join-Path $appOut "opencv_world3412.dll")

Write-Host "Runtime files copied to $appOut"
