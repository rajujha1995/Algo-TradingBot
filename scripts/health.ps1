[CmdletBinding()]
param([ValidateSet('Production')][string]$Environment='Production')
. "$PSScriptRoot/common.ps1"
$port=Get-AppPort $Environment
Invoke-RestMethod "http://127.0.0.1:$port/healthz" -TimeoutSec 5 | ConvertTo-Json
Write-Host 'This is process readiness only. Sign in and open System Health for trading prerequisites.'
