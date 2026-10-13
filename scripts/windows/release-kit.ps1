# Rebuild the friend kit and publish it as a new release of the private GitHub repo (Windows version of
# scripts/release-kit.sh), so the files can be downloaded on any of your machines instead of being sent around.
#
#   scripts\windows\release-kit.ps1 [note]     e.g. scripts\windows\release-kit.ps1 "Fix the passive tree button"
#
# Tags are kit-<date>-<commit>. Needs the GitHub CLI signed in with access to the repo (gh auth status).
param([string]$Note)
. (Join-Path $PSScriptRoot 'common.ps1')

if (& git -C $Repo status --porcelain --untracked-files=no) { throw 'Commit your changes first, so the release matches a commit.' }
Invoke-Checked 'git' @('-C', $Repo, 'push', '-q')
& (Join-Path $PSScriptRoot 'make-friend-kit.ps1')

$kit = Join-Path $Analysis 'friend-kit'
$commit = Get-Revision
$tag = "kit-$(Get-Date -Format 'yyyyMMdd-HHmm')-$commit"
$assets = @('CelticHeroesPrivate.exe', 'CelticHeroesPrivate-Mac.zip', 'HOW-TO-JOIN.txt') |
    ForEach-Object { Join-Path $kit $_ } | Where-Object { Test-Path $_ }
$notes = "Windows: run CelticHeroesPrivate.exe (More info, then Run anyway). Running a newer file updates your copy. Built from $commit."
if ($Note) { $notes = "$Note`n`n$notes" }
Invoke-Checked 'gh' (@('release', 'create', $tag, '--repo', (& git -C $Repo remote get-url origin), '--target', (& git -C $Repo rev-parse HEAD),
    '--title', "Friend kit $tag", '--notes', $notes) + $assets)
