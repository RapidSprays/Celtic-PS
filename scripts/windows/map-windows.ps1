# (Re)build the Windows name map (docs/windows-names.md): pairs the obfuscated Windows Assembly-CSharp with the Mac
# build's readable one by code shape. Windows version of scripts/map-windows.sh: the Windows build is this PC's own
# Steam install (or -GameDir), the Mac build is the snapshot in ~/CelticHeroesAnalysis/snapshots/<build>.
# Writes ~/CelticHeroesAnalysis/extracted/<build>/names-<windows dll sha256>.json (outside git), which CH.Patcher embeds.
#
#   scripts\windows\map-windows.ps1 [-GameDir "C:\...\steamapps\common\Celtic Heroes"]
param([string]$GameDir)
. (Join-Path $PSScriptRoot 'common.ps1')

if (-not $GameDir) {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    $libraries = @()
    if ($steam) {
        $libraries += $steam
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            $libraries += [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"') | ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' }
        }
    }
    $GameDir = $libraries | ForEach-Object { Join-Path $_ 'steamapps\common\Celtic Heroes' } | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $GameDir) { throw 'Celtic Heroes not found in Steam. Install it, or pass -GameDir.' }
$win = Join-Path $GameDir 'CelticHeroes_Data\Managed\Assembly-CSharp.dll'
$mac = Join-Path $Analysis "snapshots\$Build\Contents\Resources\Data\Managed\Assembly-CSharp.dll"
foreach ($f in $win, $mac) { if (-not (Test-Path $f)) { throw "missing $f" } }
$hash = (Get-FileHash -Algorithm SHA256 $win).Hash.ToLowerInvariant()
$out = Join-Path $Extracted "names-$hash.json"
New-Item -ItemType Directory -Force $Extracted | Out-Null
$patcher = Join-Path $Repo 'server\tools\CH.Patcher'
Invoke-Checked 'dotnet' @('run', '--project', $patcher, '--', 'map', '--mac', $mac, '--windows', $win, '--out', $out)
Write-Host "Wrote $out"
& dotnet run --project $patcher -- verify --game-dir $GameDir --names $out | Select-String -Pattern 'site|URLs|names'
