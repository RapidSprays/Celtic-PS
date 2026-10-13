# Play on your own private server from this PC (Windows version of scripts/run-private.sh and scripts/play.sh).
#
#   scripts\windows\play.ps1                   server + game; closing the game stops the server
#   scripts\windows\play.ps1 -Server           server only, until Ctrl+C
#   scripts\windows\play.ps1 -Tailscale        also let friends on your tailnet join (docs/friends.md); combine with -Server
#
# If the background service is installed (server-service.ps1), it is the server and this only launches the game.
# The game is your own patched copy of the Windows client in ~/CelticHeroesAnalysis/client/<build>-windows, made from
# your Steam install the same way friends' copies are (CH.Patcher play), talking to the server through the local relay.
param([switch]$Server, [switch]$Tailscale)
. (Join-Path $PSScriptRoot 'common.ps1')
New-Item -ItemType Directory -Force $Logs | Out-Null

$serverProcess = $null
try {
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
        if ($Server) { Write-Host 'The background service is the server (server-service.ps1 status).'; exit 0 }
        if (-not (Wait-ServerAnswering 30)) { throw "The background server isn't answering: scripts\windows\server-service.ps1 logs" }
        Write-Host 'Using the background server.'
        $revFile = Join-Path $ServiceDir 'bin\REVISION'
        if ((Test-Path $revFile) -and (Get-Content $revFile -Raw).Trim() -ne (Get-Revision)) {
            Write-Host 'Note: the repo has newer code than the background server; run scripts\windows\server-service.ps1 update.'
        }
    } else {
        if (Test-ServerAnswering) { throw 'A private server is already running on port 5555. Close it (or the other play.ps1) first.' }
        & (Join-Path $PSScriptRoot 'prepare-server.ps1')
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $log = Join-Path $Logs "server-$stamp.log"
        $arguments = @("`"$ServerDll`"", '--data', "`"$Data`"")
        if ($Tailscale) { $arguments += '--tailscale' }
        $serverProcess = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput $log -RedirectStandardError (Join-Path $Logs "server-$stamp.err.log")
        # Loading the client balance data takes a few seconds; wait until the server actually answers.
        for ($i = 0; $i -lt 60 -and -not (Test-ServerAnswering); $i++) {
            if ($serverProcess.HasExited) { Get-Content $log -Tail 20; throw 'Server failed to start.' }
            Start-Sleep -Milliseconds 500
        }
        if (-not (Test-ServerAnswering)) { throw "Server did not answer within 30 s; see $log" }
        Write-Host "Server up (pid $($serverProcess.Id)). Log: $log"
        $ts = Select-String -Path $log -Pattern 'Tailscale address ([0-9.]+)' | Select-Object -First 1
        if ($ts) { Write-Host "Friends join at $($ts.Matches[0].Groups[1].Value) over Tailscale (docs/friends.md)." }
        if ($Server) {
            Write-Host 'Ctrl+C stops it.'
            $serverProcess.WaitForExit()
            exit 0
        }
    }

    # The client mod is embedded in CH.Patcher; it builds against the pinned client's Unity DLLs.
    Invoke-Checked 'dotnet' @('build', '-c', 'Release', (Join-Path $Repo 'server\tools\CH.ClientMods'), '-v', 'q', '-nologo') | Out-Null
    $dest = Join-Path $Analysis "client\$Build-windows"
    Write-Host "Starting the game (private copy: $dest). Closing the game ends this script."
    Invoke-Checked 'dotnet' @('run', '-c', 'Release', '--project', (Join-Path $Repo 'server\tools\CH.Patcher'), '--',
        'play', '--host', '127.0.0.1', '--dest', $dest, '--no-shortcut')
} finally {
    if ($serverProcess -and -not $serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force
        Write-Host 'Server stopped.'
    }
}
