# Run the private server in the background on this PC, so friends can play without you starting it (docs/friends.md).
# Windows version of scripts/server-service.sh: a Task Scheduler task that starts at logon and runs service-run.ps1.
#
#   scripts\windows\server-service.ps1 install     build, publish and start it; it then starts by itself whenever you log in
#   scripts\windows\server-service.ps1 update      after pulling or changing server code: rebuild, republish, restart
#   scripts\windows\server-service.ps1 restart     restart it (disconnects players)
#   scripts\windows\server-service.ps1 stop        stop it until the next logon or 'restart'
#   scripts\windows\server-service.ps1 status      running? which mode, which revision, last backup
#   scripts\windows\server-service.ps1 logs        follow the current server log (Ctrl+C to stop following)
#   scripts\windows\server-service.ps1 uninstall   stop it and remove it from logon (keeps the database and backups)
#
# While it runs it keeps the PC from idle-sleeping, restarts the server if it stops, switches to Tailscale mode once
# Tailscale is up, and backs up the database daily. play.ps1 notices the service and just launches your game against it.
param([string]$Command = 'status')
. (Join-Path $PSScriptRoot 'common.ps1')

$bin = Join-Path $ServiceDir 'bin'
$serviceLog = Join-Path $Logs 'service.log'

function Get-Task { Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue }

function Stop-Service-Now {
    if (Get-Task) { Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue }
    # Task Scheduler stops the loop, not the server it started.
    Get-ServerProcesses $bin | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
}

function Start-Service-Now {
    Start-ScheduledTask -TaskName $TaskName
    if (-not (Wait-ServerAnswering 90)) {
        Write-Host "The service didn't answer within 90 s. Last log lines:"
        if (Test-Path $serviceLog) { Get-Content $serviceLog -Tail 10 }
        $latest = Get-ChildItem $Logs -Filter 'server-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 2
        foreach ($l in $latest) { Get-Content $l.FullName -Tail 10 }
        exit 1
    }
}

# Publishes into bin.new while the old server keeps running, then swaps it in with a short stop.
function Publish-Service {
    Write-Host "Preparing data and publishing the server to $bin ..."
    New-Item -ItemType Directory -Force $ServiceDir, $Logs | Out-Null
    $staging = Join-Path $ServiceDir 'bin.new'
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    & (Join-Path $PSScriptRoot 'prepare-server.ps1') -Publish $staging
    Stop-Service-Now
    if (Test-Path $bin) { Remove-Item -Recurse -Force $bin }
    Move-Item $staging $bin
    Copy-Item (Join-Path $PSScriptRoot 'common.ps1'), (Join-Path $PSScriptRoot 'service-run.ps1') $ServiceDir -Force
}

function Register-Task {
    $runner = Join-Path $ServiceDir 'service-run.ps1'
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$runner`""
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
        -Description 'Celtic Heroes private server (scripts\windows\server-service.ps1)' | Out-Null
}

function Show-Status {
    if (-not (Get-Task)) { Write-Host 'Not installed.'; return }
    if (Test-ServerAnswering) { Write-Host 'Running.' } else { Write-Host "Installed, but not answering right now (starting, stopped, or failing: see 'logs')." }
    if (Test-Path $serviceLog) {
        $last = Select-String -Path $serviceLog -Pattern 'server started' | Select-Object -Last 1
        if ($last) { Write-Host "  last start: $($last.Line)" }
    }
    Write-Host "  data: $Data"
    $backup = Get-ChildItem (Join-Path $Data 'backups') -Filter 'private-server-*.db' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    Write-Host "  last backup: $(if ($backup) { $backup.FullName } else { 'none yet' })"
    $revFile = Join-Path $bin 'REVISION'
    $revision = if (Test-Path $revFile) { (Get-Content $revFile -Raw).Trim() } else { '' }
    if ($revision -ne (Get-Revision)) { Write-Host "  The repo has newer code than the service: run '$PSCommandPath update'." }
    $ts = Get-TailscaleIp
    if ($ts) { Write-Host "  Friends join at $ts over Tailscale." } else { Write-Host '  Tailscale is not connected: friends cannot join.' }
}

switch ($Command) {
    'install' {
        if (Get-Task) { Write-Host "Already installed; use 'update' or 'restart'."; exit 0 }
        if (Test-ServerAnswering) {
            Write-Host 'A server is already running on port 5555 (probably play.ps1). Quit the game first.'
            exit 1
        }
        Publish-Service
        Register-Task
        Start-Service-Now
        Write-Host 'Installed and running. It starts by itself whenever you log in.'
        Show-Status
    }
    'update' {
        if (-not (Get-Task)) { Write-Host "Not installed; run 'install'."; exit 1 }
        Publish-Service
        Start-Service-Now
        Write-Host "Updated to revision $((Get-Content (Join-Path $bin 'REVISION') -Raw).Trim())."
    }
    'restart' {
        if (-not (Get-Task)) { Write-Host 'Not installed.'; exit 1 }
        Stop-Service-Now
        Start-Service-Now
        Write-Host 'Restarted.'
    }
    'stop' {
        Stop-Service-Now
        Write-Host "Stopped. It starts again at your next logon, or with 'restart'."
    }
    'status' { Show-Status }
    'logs' {
        $latest = Get-ChildItem $Logs -Filter 'server-*.log' -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notlike '*.err.log' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $latest) { Write-Host 'No server log yet.'; exit 0 }
        Write-Host "== $($latest.FullName)"
        Get-Content $latest.FullName -Tail 40 -Wait
    }
    'uninstall' {
        Stop-Service-Now
        if (Get-Task) { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false }
        Write-Host "Stopped and removed from logon. The database and backups are kept in $Data."
    }
    default {
        Get-Content $PSCommandPath | Select-Object -Skip 1 -First 10 | ForEach-Object { $_ -replace '^# ?', '' }
        exit 2
    }
}
