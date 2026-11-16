[CmdletBinding()]
param([switch]$EndToEnd)
. "$PSScriptRoot/common.ps1"
Set-Location $script:RepoRoot
Assert-Prerequisites
Invoke-Checked 'dotnet' @('test','api/TradingBot.sln','--no-restore','--configuration','Release')
Push-Location ui
try {
 Invoke-Checked 'npm' @('run','check')
 Invoke-Checked 'npm' @('test')
 Invoke-Checked 'npm' @('run','build')
 if ($EndToEnd) {
  if (-not $env:E2E_PASSWORD) { throw 'Set E2E_PASSWORD to the disposable test operator password and start the isolated test host first. E2E creates paper orders and emergency-stops the engine.' }
  Invoke-Checked 'npx' @('playwright','install','chromium')
  Invoke-Checked 'npm' @('run','test:e2e')
 }
} finally { Pop-Location }
