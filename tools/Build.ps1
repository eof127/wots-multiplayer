# Builds the WotSMultiplayer plugin and deploys it to <game>\BepInEx\plugins\WotSMultiplayer\.
#   powershell -ExecutionPolicy Bypass -File "_MultiplayerMod\tools\Build.ps1" [-Configuration Release] [-NoDeploy]
param(
    [string]$Configuration = 'Debug',
    [switch]$NoDeploy
)
$ErrorActionPreference = 'Stop'

$modRoot  = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $modRoot
$project  = Join-Path $modRoot 'src\WotSMultiplayer\WotSMultiplayer.csproj'

# .NET 10 SDK is unzipped per-user (not on PATH).
$env:DOTNET_ROOT = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
if (-not (Test-Path $dotnet)) { throw ".NET SDK not found at $dotnet (install the .NET SDK, see README.md: Building from source)" }

& $dotnet build $project -c $Configuration --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }

$outDir = Join-Path $modRoot "src\WotSMultiplayer\bin\$Configuration\net35"
if ($NoDeploy) { Write-Host "Built to $outDir (not deployed)"; return }

$game = Get-Process -Name 'WarOnTheSea' -ErrorAction SilentlyContinue
if ($game) { throw 'War on the Sea is running; close it before deploying (the DLL is locked).' }

$dest = Join-Path $gameRoot 'BepInEx\plugins\WotSMultiplayer'
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $outDir 'WotSMultiplayer.dll') $dest -Force
if (Test-Path (Join-Path $outDir 'WotSMultiplayer.pdb')) { Copy-Item (Join-Path $outDir 'WotSMultiplayer.pdb') $dest -Force }
Write-Host "Deployed to $dest"
