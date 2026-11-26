param(
  [string]$CommitMessage
)

$ErrorActionPreference = 'Stop'
$rootScript = Join-Path (Split-Path -Parent $PSScriptRoot) 'save-to-github.ps1'
if (-not (Test-Path -LiteralPath $rootScript)) { throw "Main save script not found: $rootScript" }
& $rootScript -CommitMessage $CommitMessage
if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
