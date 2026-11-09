# AiTeammate PowerShell Build Script
[CmdletBinding()]
param(
    [string]$Sts2Root = $env:STS2_ROOT
)

if (-not $Sts2Root) {
    $Sts2Root = "F:\SteamLibrary\steamapps\common\Slay the Spire 2"
}

$RepoRoot = $PSScriptRoot
$SrcMod = Join-Path $RepoRoot "src\AiTeammateMod.cs"
$SrcFlow = Join-Path $RepoRoot "src\RewardBackendFlow.cs"
$OutDll = Join-Path $RepoRoot "mod\AiTeammate.dll"
$DataDir = Join-Path $Sts2Root "data_sts2_windows_x86_64"
$CscPath = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Building AiTeammate.dll" -ForegroundColor Cyan
Write-Host "Repo Root: $RepoRoot"
Write-Host "StS2 Root: $Sts2Root"
Write-Host "Output:    $OutDll"
Write-Host "==========================================" -ForegroundColor Cyan

if (-not (Test-Path $DataDir)) {
    Write-Error "Could not find Slay the Spire 2 data directory at: $DataDir. Please set STS2_ROOT environment variable."
    exit 1
}

$RefFiles = @(
    "mscorlib.dll",
    "netstandard.dll",
    "System.Private.CoreLib.dll",
    "System.Runtime.dll",
    "System.Collections.dll",
    "System.Console.dll",
    "System.Linq.dll",
    "System.Threading.Tasks.dll",
    "System.Threading.dll",
    "System.Text.RegularExpressions.dll",
    "System.Reflection.Primitives.dll",
    "System.Runtime.InteropServices.dll",
    "GodotSharp.dll",
    "sts2.dll",
    "0Harmony.dll"
)

$ArgsList = @(
    "/nostdlib",
    "/noconfig",
    "/target:library",
    "/nowarn:1685,1701,1702",
    "/utf8output",
    "/codepage:65001",
    "/out:$OutDll"
)

foreach ($ref in $RefFiles) {
    $p = Join-Path $DataDir $ref
    if (Test-Path $p) {
        $ArgsList += "/r:$p"
    } else {
        Write-Warning "Reference missing: $p"
    }
}

# Optional DualRoleAdventure reference if present
$WorkshopDir = "F:\SteamLibrary\steamapps\workshop\content\2868840\3772900244\DualRoleAdventure.dll"
if (Test-Path $WorkshopDir) {
    $ArgsList += "/r:$WorkshopDir"
}

$ArgsList += $SrcMod
$ArgsList += $SrcFlow

& $CscPath $ArgsList
if ($LASTEXITCODE -eq 0) {
    Write-Host "[SUCCESS] Compilation successful -> $OutDll" -ForegroundColor Green
} else {
    Write-Error "[FAIL] Compilation failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}
