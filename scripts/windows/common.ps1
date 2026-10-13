# Shared settings for the Windows scripts (dot-sourced; Windows PowerShell 5.1). The macOS scripts in scripts/ are the
# originals; these do the same jobs on a Windows host (docs/windows-host.md).
$ErrorActionPreference = 'Stop'

$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$Build = '24928510'
$Analysis = Join-Path $env:USERPROFILE 'CelticHeroesAnalysis'
$Extracted = Join-Path $Analysis "extracted\$Build"
$Data = Join-Path $Analysis 'server-data'
$Logs = Join-Path $Analysis 'logs'
$ServiceDir = Join-Path $Analysis 'service'
$ServerDll = Join-Path $Repo 'server\src\CH.Server\bin\Release\net8.0\CH.Server.dll'
$TaskName = 'Celtic Heroes Private Server'
$HttpPort = 5555
$GamePort = 5556

function Get-Revision { (& git -C $Repo rev-parse --short HEAD 2>$null) }

# This PC's Tailscale IPv4 (100.64.0.0/10), or $null when Tailscale isn't connected.
function Get-TailscaleIp {
    Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -match '^100\.(\d+)\.' -and [int]$Matches[1] -ge 64 -and [int]$Matches[1] -le 127 } |
        Select-Object -First 1 -ExpandProperty IPAddress
}

function Test-ServerAnswering {
    try {
        $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 2 "http://127.0.0.1:$HttpPort/ping"
        return $r.StatusCode -eq 200
    } catch {
        return $false
    }
}

function Wait-ServerAnswering([int]$Seconds = 60) {
    for ($i = 0; $i -lt $Seconds; $i++) {
        if (Test-ServerAnswering) { return $true }
        Start-Sleep -Seconds 1
    }
    return $false
}

# Every CH.Server process started from $Dir (its published or built folder).
function Get-ServerProcesses([string]$Dir) {
    $needle = (Join-Path $Dir 'CH.Server.dll')
    Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.Contains($needle) }
}

# Python 3 for the data scripts: the py launcher if present, else python on PATH.
function Get-Python {
    if (Get-Command py -ErrorAction SilentlyContinue) { return @('py', '-3') }
    if (Get-Command python -ErrorAction SilentlyContinue) { return @('python') }
    throw 'Python 3 is not installed (python.org, tick "Add to PATH").'
}

function Invoke-Python([string[]]$Arguments) {
    $py = Get-Python
    $rest = @()
    if ($py.Count -gt 1) { $rest = $py[1..($py.Count - 1)] }
    Invoke-Checked $py[0] ($rest + $Arguments)
}

# Runs a program and fails on a non-zero exit code. Its stderr is shown, not treated as failure: Windows PowerShell 5.1
# otherwise turns any stderr line (pip's upgrade notice, dotnet's workload hint) into a terminating error under 'Stop'.
function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $Exe @Arguments 2>&1 | ForEach-Object { if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.Exception.Message } else { $_ } }
    } finally {
        $ErrorActionPreference = $previous
    }
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($Arguments -join ' ') failed ($LASTEXITCODE)" }
}
