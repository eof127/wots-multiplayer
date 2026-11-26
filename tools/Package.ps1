# Builds the all-in-one release zip (M9): _MultiplayerMod\release\out\WotS_Multiplayer_v<version>.zip
#   powershell -ExecutionPolicy Bypass -File "_MultiplayerMod\tools\Package.ps1" [-NoBuild]
# Layout inside the zip:
#   WotS_Multiplayer_v<version>\README.txt, LICENSE.txt, THIRD-PARTY-NOTICES.txt, licenses\
#   WotS_Multiplayer_v<version>\Game folder\  → copied by the player into the game folder:
#       winhttp.dll, doorstop_config.ini, .doorstop_version, BepInEx\core\* (BepInEx 5.4.23.6, unmodified),
#       BepInEx\plugins\WotSMultiplayer\WotSMultiplayer.dll (+ README.txt)
# Nothing from analysis\ (decompiled game code), src\, docs\ or the player's configs/logs/saves goes in.
param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$modRoot  = Split-Path -Parent $PSScriptRoot
$gameRoot = Split-Path -Parent $modRoot
$rel      = Join-Path $modRoot 'release'

$buildInfo = Get-Content (Join-Path $modRoot 'src\WotSMultiplayer\Core\BuildInfo.cs') -Raw
$ver = [regex]::Match($buildInfo, 'Version = "([^"]+)"').Groups[1].Value
if (-not $ver) { throw 'Version not found in BuildInfo.cs' }

if (-not $NoBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') } # (throws while the game runs)

$dll = Join-Path $gameRoot 'BepInEx\plugins\WotSMultiplayer\WotSMultiplayer.dll'
if (-not (Test-Path $dll)) { throw "Plugin not deployed: $dll" }

$name  = "WotS_Multiplayer_v$ver"
$out   = Join-Path $rel 'out'
$stage = Join-Path $out $name
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -Confirm:$false }
$gf = Join-Path $stage 'Game folder'
$plug = Join-Path $gf 'BepInEx\plugins\WotSMultiplayer'
New-Item -ItemType Directory -Force (Join-Path $gf 'BepInEx\core'), $plug, (Join-Path $stage 'licenses') | Out-Null

# BepInEx (unmodified copies of the installed release)
foreach ($f in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version') { Copy-Item -LiteralPath (Join-Path $gameRoot $f) -Destination $gf }
Copy-Item -Path (Join-Path $gameRoot 'BepInEx\core\*') -Destination (Join-Path $gf 'BepInEx\core')
# the mod
Copy-Item -LiteralPath $dll -Destination $plug

# documents (README gets the version filled in; UTF-8 with BOM so Notepad shows the arrows/dashes)
$readme = (Get-Content (Join-Path $rel 'README.txt') -Raw -Encoding UTF8).Replace('{VERSION}', "v$ver")
[IO.File]::WriteAllText((Join-Path $stage 'README.txt'), $readme, (New-Object Text.UTF8Encoding($true)))
[IO.File]::WriteAllText((Join-Path $plug 'README.txt'), $readme, (New-Object Text.UTF8Encoding($true)))
Copy-Item (Join-Path $rel 'LICENSE.txt'), (Join-Path $rel 'THIRD-PARTY-NOTICES.txt') -Destination $stage
Copy-Item (Join-Path $rel 'licenses\*') -Destination (Join-Path $stage 'licenses')

# guard: only the files we meant to ship
$bad = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Extension -in '.cs', '.pdb', '.log', '.cfg', '.md' -or $_.FullName -match '\\analysis\\|\\decompiled\\' }
if ($bad) { throw "Unexpected files in the package: $($bad.FullName -join ', ')" }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $out "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Confirm:$false }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)

$z = [IO.Compression.ZipFile]::OpenRead($zip)
try { $z.Entries | ForEach-Object { '{0,9}  {1}' -f $_.Length, $_.FullName } } finally { $z.Dispose() }
$info = Get-Item $zip
"`n$($info.Name): $([math]::Round($info.Length / 1KB)) KB, SHA256 $((Get-FileHash $zip -Algorithm SHA256).Hash)"
