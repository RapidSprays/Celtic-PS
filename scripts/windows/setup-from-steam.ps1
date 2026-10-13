# Build ~/CelticHeroesAnalysis on this PC from Steam alone, without the Mac (docs/windows-host.md). Does the jobs of the
# Mac's pin-client.sh and snapshot.sh against the Mac depot downloaded with Windows Steam, then everything after them.
#
#   1. In Steam's console (steam://open/console):   download_depot 2829820 2829822
#   2. scripts\windows\setup-from-steam.ps1 [-DepotDir DIR] [-Decompile]
#
# Steps (each skipped when already done, so it can be re-run):
#   - verifies every file of the Mac depot against inventories/sha256.txt (the verified build);
#   - pinned client:  client/<build>/CelticHeroes.app + steam_appid.txt (what the data scripts and CH.ClientMods read);
#   - snapshot:       snapshots/<build>/ code and config, read-only (what the name map and decompiling read);
#   - Python venv with UnityPy (zone ground extraction);
#   - Windows name map (map-windows.ps1) and server data + build (prepare-server.ps1);
#   - with -Decompile: decompiled/<build>/Assembly-CSharp with ilspycmd (the readable reference for research).
param([string]$DepotDir, [switch]$Decompile)
. (Join-Path $PSScriptRoot 'common.ps1')

if (-not $DepotDir) {
    $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction SilentlyContinue).SteamPath
    if ($steam) { $DepotDir = Join-Path $steam 'steamapps\content\app_2829820\depot_2829822' }
}
$depotApp = Join-Path $DepotDir 'CelticHeroes.app'
$client = Join-Path $Analysis "client\$Build"
$clientApp = Join-Path $client 'CelticHeroes.app'
$snapshot = Join-Path $Analysis "snapshots\$Build"

# Hash-checks every inventoried file under $App. Returns the inventory (relative path -> sha256).
function Test-Inventory([string]$App) {
    $inventory = @{}
    foreach ($line in Get-Content (Join-Path $Repo 'inventories\sha256.txt')) { $inventory[$line.Substring(66)] = $line.Substring(0, 64) }
    $bad = 0; $n = 0
    foreach ($entry in $inventory.GetEnumerator()) {
        $path = Join-Path $App ($entry.Key -replace '/', '\')
        $n++
        if ($n % 2000 -eq 0) { Write-Host "  checked $n of $($inventory.Count) files..." }
        if (-not (Test-Path -LiteralPath $path)) { $bad++; if ($bad -le 5) { Write-Host "  missing: $($entry.Key)" }; continue }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) {
            $bad++; if ($bad -le 5) { Write-Host "  differs: $($entry.Key)" }
        }
    }
    if ($bad) { throw "$bad of $($inventory.Count) files are missing or differ from build $Build in $App" }
    Write-Host "  all $($inventory.Count) files match build $Build."
}

function Copy-Tree([string]$From, [string]$To, [string[]]$Extra = @()) {
    & robocopy $From $To /E /NFL /NDL /NJH /NJS /NP @Extra | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "copy from $From failed (robocopy $LASTEXITCODE)" }
}

# 1. Pinned client.
if (Test-Path $clientApp) {
    Write-Host "Pinned client already at $clientApp"
} else {
    if (-not (Test-Path $depotApp)) {
        throw "Mac depot not found at $DepotDir. In Steam's console (steam://open/console) run: download_depot 2829820 2829822, wait for 'Depot download complete', then run this again (or pass -DepotDir)."
    }
    Write-Host "Verifying the Mac depot at $depotApp (a few minutes)..."
    Test-Inventory $depotApp
    Write-Host "Pinning it to $clientApp ..."
    Copy-Tree $depotApp $clientApp
    Set-Content -Path (Join-Path $client 'steam_appid.txt') -Value '2829820' -Encoding ascii
}

# 2. Snapshot (code and config, not the 2.5 GB of asset bundles), read-only like the Mac's.
if (Test-Path $snapshot) {
    Write-Host "Snapshot already at $snapshot"
} else {
    Write-Host "Taking the snapshot at $snapshot ..."
    $data = 'Contents\Resources\Data'
    Copy-Tree (Join-Path $clientApp "$data\Managed") (Join-Path $snapshot "$data\Managed")
    Copy-Tree (Join-Path $clientApp "$data\StreamingAssets") (Join-Path $snapshot "$data\StreamingAssets") @('/XD', 'aa', 'AndroidAssets')
    foreach ($f in "$data\boot.config", "$data\app.info", "$data\RuntimeInitializeOnLoads.json", "$data\ScriptingAssemblies.json", 'Contents\Info.plist') {
        $src = Join-Path $clientApp $f
        if (Test-Path $src) { Copy-Item $src (Join-Path $snapshot $f) }
    }
    Get-ChildItem $snapshot -Recurse -File | ForEach-Object { $_.IsReadOnly = $true }
}

# 3. Python environment for UnityPy (never in the repo).
$venvPy = Join-Path $Analysis 'venv\Scripts\python.exe'
if (-not (Test-Path $venvPy)) {
    Write-Host 'Creating the Python environment...'
    Invoke-Python @('-m', 'venv', (Join-Path $Analysis 'venv'))
}
$ErrorActionPreference = 'Continue' # a failed import writes to stderr, which 5.1 would treat as fatal
& $venvPy -c 'import UnityPy' 2>$null
$hasUnityPy = $LASTEXITCODE -eq 0
$ErrorActionPreference = 'Stop'
if (-not $hasUnityPy) {
    Write-Host 'Installing UnityPy...'
    Invoke-Checked $venvPy @('-m', 'pip', 'install', '-q', '--disable-pip-version-check', 'UnityPy')
}

# 4. Windows name map, then the server's data and build.
if (-not (Get-ChildItem (Join-Path $Extracted 'names-*.json') -ErrorAction SilentlyContinue)) {
    Write-Host 'Building the Windows name map...'
    & (Join-Path $PSScriptRoot 'map-windows.ps1')
}
Write-Host 'Extracting the server data and building the server...'
& (Join-Path $PSScriptRoot 'prepare-server.ps1')

# 5. Optional: decompile the readable Mac code for research (docs/client-architecture.md).
if ($Decompile) {
    $out = Join-Path $Analysis "decompiled\$Build\Assembly-CSharp"
    if (Test-Path $out) {
        Write-Host "Decompiled code already at $out"
    } else {
        # Pinned: newer ilspycmd releases need a newer .NET SDK than the server's.
        if (-not (Get-Command ilspycmd -ErrorAction SilentlyContinue)) { Invoke-Checked 'dotnet' @('tool', 'install', '-g', 'ilspycmd', '--version', '9.1.0.7988') }
        $managed = Join-Path $snapshot 'Contents\Resources\Data\Managed'
        Invoke-Checked 'ilspycmd' @('-p', '-r', $managed, '-o', $out, (Join-Path $managed 'Assembly-CSharp.dll'))
    }
}
Write-Host "Done. Next: scripts\windows\server-service.ps1 install (docs/windows-host.md)."
