[CmdletBinding()]
param([switch]$NoBrowser)
. "$PSScriptRoot/common.ps1"
Set-Location $script:RepoRoot
Assert-Prerequisites
Set-AppEnvironment 'Production'
Assert-PortFree 5101
if (-not (Test-Path api/.env.production)) { throw 'Run scripts/setup-windows.ps1 first.' }
Push-Location ui
try { Invoke-Checked 'npm' @('run','build') } finally { Pop-Location }
$publish=Join-Path $script:RuntimePath 'publish'
Invoke-Checked 'dotnet' @('publish','api/TradingBot.Api','-c','Release','--no-restore','-o',$publish)
try {
 $dll=Join-Path $publish 'TradingBot.Api.dll'
 Start-ManagedProcess 'api-prod' 'dotnet' ('"' + $dll + '"') $script:RepoRoot
 Wait-AppHealth 5101
 if (-not $NoBrowser) { Start-Process 'http://127.0.0.1:5101' }
 Write-Host 'PROD workspace: http://127.0.0.1:5101. Browsing does not start trading. Stop with .\scripts\stop-bot.ps1 -Environment Production.'
} catch { Stop-ManagedProcess 'api-prod'; throw }
