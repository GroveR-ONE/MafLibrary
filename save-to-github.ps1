param(
  [string]$CommitMessage
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

$git = Get-Command git.exe -ErrorAction SilentlyContinue
$gitPath = $git.Source
if (-not $git) {
  $candidate = 'C:\Program Files\Git\cmd\git.exe'
  if (Test-Path -LiteralPath $candidate) { $gitPath = $candidate }
}
if (-not $gitPath) { throw 'Git for Windows is not installed or git.exe could not be found.' }

function Invoke-Git {
  & $gitPath @args
  if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path -LiteralPath (Join-Path $root '.git'))) {
  throw 'This folder is not initialized as a Git repository. Run the one-time setup first.'
}

$branch = (& $gitPath branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or -not $branch) { throw 'Could not determine the current Git branch.' }

$remote = (& $gitPath remote get-url origin 2>$null)
if ($LASTEXITCODE -ne 0 -or $remote -notmatch '^https://github\.com/GroveR-ONE/MafLibrary(?:\.git)?$') {
  throw 'The origin remote is missing or is not the configured private repository GroveR-ONE/MafLibrary.'
}

Write-Host ''
Write-Host 'Changes that will be included (data/, uploads, backups, bin and obj are excluded):' -ForegroundColor Cyan
Invoke-Git status --short

if ((& $gitPath status --porcelain).Count -eq 0) {
  Write-Host 'No changes to save.' -ForegroundColor Green
  exit 0
}

if (-not $CommitMessage) { $CommitMessage = Read-Host 'Commit message' }
if ([string]::IsNullOrWhiteSpace($CommitMessage)) { throw 'Commit message cannot be empty.' }

Invoke-Git add --all

$staged = @(& $gitPath diff --cached --name-only)
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect staged files.' }
$forbidden = $staged | Where-Object { $_ -match '^(data|backups|bin|obj)/' -or $_ -match '(^|/)\.env($|\.)' }
if ($forbidden) {
  & $gitPath reset -- $forbidden | Out-Null
  throw "Refusing to commit excluded or secret-looking paths: $($forbidden -join ', ')"
}

Write-Host ''
Write-Host 'Files staged for the commit:' -ForegroundColor Cyan
Invoke-Git diff --cached --name-status
Write-Host ''
Invoke-Git diff --cached --stat

$commitAnswer = Read-Host 'Create this local commit? [y/N]'
if ($commitAnswer -notmatch '^(y|yes)$') {
  Invoke-Git reset
  Write-Host 'Commit cancelled. Working files were not changed.' -ForegroundColor Yellow
  exit 0
}

Invoke-Git commit -m $CommitMessage

Write-Host ''
$pushAnswer = Read-Host "Push commit to private GitHub repository on branch '$branch'? [y/N]"
if ($pushAnswer -notmatch '^(y|yes)$') {
  Write-Host 'Local commit saved; push skipped.' -ForegroundColor Yellow
  exit 0
}

Invoke-Git push -u origin $branch
Write-Host 'Changes saved to GitHub.' -ForegroundColor Green
