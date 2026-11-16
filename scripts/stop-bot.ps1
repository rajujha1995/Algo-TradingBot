[CmdletBinding()]
param([ValidateSet('Production')][string]$Environment='Production')
. "$PSScriptRoot/common.ps1"
# Process shutdown is separate from the persistent EMERGENCY STOP action in the UI.
# An interrupted running session always requires manual recovery after restart.
Stop-ManagedProcess 'api-prod'
Write-Host 'Application stopped. Running paper sessions require manual recovery on restart. Use the red UI button first to persist EMERGENCY_STOPPED and cancel pending paper remainders.'
