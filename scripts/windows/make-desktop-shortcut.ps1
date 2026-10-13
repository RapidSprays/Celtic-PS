# Put a "Celtic Heroes Private (host)" shortcut on the Desktop that runs play.ps1 (Windows version of
# scripts/make-desktop-shortcut.sh). Run again after moving the repo.
. (Join-Path $PSScriptRoot 'common.ps1')

$lnk = Join-Path ([Environment]::GetFolderPath('Desktop')) 'Celtic Heroes Private (host).lnk'
$play = Join-Path $PSScriptRoot 'play.ps1'
$shell = New-Object -ComObject WScript.Shell
$s = $shell.CreateShortcut($lnk)
$s.TargetPath = 'powershell.exe'
$s.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$play`""
$s.WorkingDirectory = $Repo
$s.WindowStyle = 7 # minimized: the window shows server output and closes with the game
$game = Join-Path $Analysis "client\$Build-windows\game\CelticHeroes.exe"
if (Test-Path $game) { $s.IconLocation = "$game,0" }
$s.Description = 'Celtic Heroes on your own private server'
$s.Save()
Write-Host "Created $lnk"
