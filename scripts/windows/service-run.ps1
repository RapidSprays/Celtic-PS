# The background service's main loop (started at logon by Task Scheduler; see server-service.ps1). Windows version of
# scripts/service-run.sh. Not meant to be run by hand.
#
# Keeps the private server running from the published copy in ~/CelticHeroesAnalysis/service/bin:
#   - Tailscale mode when this PC has a Tailscale address, else localhost only; switches to Tailscale mode once it comes
#     up (restarting the server, which disconnects anyone on it);
#   - restarts the server 5 s after it stops for any reason;
#   - keeps the PC from idle-sleeping while it runs;
#   - backs up the database once a day (SQLite online backup), keeping the newest 14.
# Each server run logs to logs\server-<time>.log; this loop logs to logs\service.log.
. (Join-Path $PSScriptRoot 'common.ps1')
$ErrorActionPreference = 'Continue'

$bin = Join-Path $ServiceDir 'bin'
$dll = Join-Path $bin 'CH.Server.dll'
$backups = Join-Path $Data 'backups'
$serviceLog = Join-Path $Logs 'service.log'
New-Item -ItemType Directory -Force $Data, $Logs | Out-Null

function Log([string]$Text) {
    Add-Content -Path $serviceLog -Value ("{0} [service] {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Text)
}

function Backup-IfDue {
    if (-not (Test-Path (Join-Path $Data 'private-server.db'))) { return }
    New-Item -ItemType Directory -Force $backups | Out-Null
    $newest = Get-ChildItem $backups -Filter 'private-server-*.db' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newest -and $newest.LastWriteTime -gt (Get-Date).AddDays(-1)) { return }
    $out = Join-Path $backups ("private-server-{0}.db" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    & dotnet $dll backup --data $Data --out $out 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { Log "database backed up to $out" } else { Log 'database backup FAILED' }
    Get-ChildItem $backups -Filter 'private-server-*.db' | Sort-Object LastWriteTime -Descending | Select-Object -Skip 14 |
        Remove-Item -Force
}

# No idle sleep while this loop runs (released when the process ends).
Add-Type -Namespace CH -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint esFlags);'
[CH.Power]::SetThreadExecutionState([uint32]'0x80000001') | Out-Null # ES_CONTINUOUS | ES_SYSTEM_REQUIRED

# A server left over from a previous run of this loop (Task Scheduler stops only this script, not its child).
Get-ServerProcesses $bin | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

while ($true) {
    $ts = Get-TailscaleIp
    $arguments = @("`"$dll`"", '--data', "`"$Data`"")
    $mode = 'localhost only'
    if ($ts) { $arguments += '--tailscale'; $mode = "Tailscale $ts" }
    Backup-IfDue
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $server = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $Logs "server-$stamp.log") -RedirectStandardError (Join-Path $Logs "server-$stamp.err.log")
    $null = $server.Handle # keeps ExitCode readable after exit (a Start-Process -PassThru quirk)
    $revision = '?'
    if (Test-Path (Join-Path $bin 'REVISION')) { $revision = (Get-Content (Join-Path $bin 'REVISION') -Raw).Trim() }
    Log "server started ($mode, pid $($server.Id), revision $revision, log server-$stamp.log)"
    while (-not $server.HasExited) {
        Start-Sleep -Seconds 5
        if ($mode -eq 'localhost only' -and (Get-TailscaleIp)) {
            Log 'Tailscale is up: restarting the server so friends can join'
            Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
            break
        }
        Backup-IfDue
    }
    $server.WaitForExit()
    Log "server exited (status $($server.ExitCode)); restarting in 5 s"
    Start-Sleep -Seconds 5
}
