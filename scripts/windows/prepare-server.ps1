# Get the private server ready to run (Windows version of scripts/prepare-server.sh): client balance data, derived
# world data and a Release build.
#
#   scripts\windows\prepare-server.ps1                  build into server\src\CH.Server\bin\Release
#   scripts\windows\prepare-server.ps1 -Publish DIR     also publish a copy of the server to DIR (the background service)
#
# Derived data is (re)generated when missing or when its script (or the shared chdata.py) changed.
param([string]$Publish)
. (Join-Path $PSScriptRoot 'common.ps1')

$scripts = Join-Path $Repo 'scripts'
function Out-Of-Date([string]$Out, [string[]]$Inputs) {
    $outPath = Join-Path $Extracted $Out
    if (-not (Test-Path $outPath)) { return $true }
    $outTime = (Get-Item $outPath).LastWriteTimeUtc
    foreach ($in in $Inputs) {
        if ((Test-Path $in) -and (Get-Item $in).LastWriteTimeUtc -gt $outTime) { return $true }
    }
    return $false
}
function Script([string]$Name) { Join-Path $scripts $Name }
function Regen([string]$Out, [string]$Name, [string[]]$Also = @()) {
    if (Out-Of-Date $Out (@((Script $Name), (Script 'chdata.py')) + $Also)) {
        Invoke-Python @((Script $Name), $Build) | Out-Null
    }
}
function Extracted([string]$Name) { Join-Path $Extracted $Name }

if (-not (Test-Path (Extracted 'balance-client.json'))) { Invoke-Python @((Script 'extract-balance.py'), $Build) }

# The real ground per zone needs UnityPy in ~/CelticHeroesAnalysis/venv (skipped without it; see docs/windows-host.md).
# NPCs are placed before monster camps, which keep clear of them (docs/world-placement.md).
$venvPy = Join-Path $Analysis 'venv\Scripts\python.exe'
if ((Test-Path $venvPy) -and (Out-Of-Date 'zone-worlds.json' @((Script 'extract-zone-worlds.py')))) {
    & $venvPy (Script 'extract-zone-worlds.py') $Build | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'extract-zone-worlds.py failed' }
}
Regen 'zone-points.json' 'extract-zone-points.py'
$worldInputs = @((Extracted 'zone-worlds.json'), (Script 'worldplace.py'))
Regen 'world.json' 'gen-world.py' $worldInputs
Regen 'zone-population.json' 'gen-zone-population.py' ($worldInputs + (Extracted 'world.json'))
# Gathering and cooking (docs/gathering-notes.md): fishing spots and ground items follow the world and its NPCs.
Regen 'cooking.json' 'gen-cooking.py'
$gatherInputs = $worldInputs + @((Extracted 'world.json'), (Extracted 'cooking.json'))
Regen 'fishing.json' 'gen-fishing.py' $gatherInputs
Regen 'item-spawns.json' 'gen-item-spawns.py' $gatherInputs

$project = Join-Path $Repo 'server\src\CH.Server'
if ($Publish) {
    Invoke-Checked 'dotnet' @('publish', '-c', 'Release', $project, '-o', $Publish, '-v', 'q', '-nologo') | Out-Null
    Set-Content -Path (Join-Path $Publish 'REVISION') -Value (Get-Revision) -Encoding ascii
} else {
    Invoke-Checked 'dotnet' @('build', '-c', 'Release', $project, '-v', 'q', '-nologo') | Out-Null
}
