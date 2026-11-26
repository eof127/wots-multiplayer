# Runs the fast logic tests (no game needed).
#   powershell -ExecutionPolicy Bypass -File "_MultiplayerMod\tools\Test.ps1"
$ErrorActionPreference = 'Stop'
$modRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
& $dotnet run --project (Join-Path $modRoot 'tests\RulesTests\RulesTests.csproj') -v quiet
if ($LASTEXITCODE -ne 0) { throw "Tests failed ($LASTEXITCODE failures)" }
