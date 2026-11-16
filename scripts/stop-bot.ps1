[CmdletBinding()]
param([ValidateSet('Production')][string]$Environment='Production')
. "$PSScriptRoot/common.ps1"
# Interrupted watch sessions remain paused after restart.
Stop-ManagedProcess 'api-prod'
Write-Host 'Application stopped. Saved observations are retained. Start Watching manually after restart; no broker orders were sent or cancelled.'
