[CmdletBinding()]
param([ValidateSet('Production')][string]$Environment='Production',[switch]$Remove)
. "$PSScriptRoot/common.ps1"
$name="INDstocks-$Environment"
if($Remove){Unregister-ScheduledTask -TaskName $name -Confirm:$false;return}
$script='start.ps1'
$path=Join-Path $PSScriptRoot $script
$action=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ('-NoProfile -ExecutionPolicy RemoteSigned -File "'+$path+'" -NoBrowser') -WorkingDirectory $script:RepoRoot
$trigger=New-ScheduledTaskTrigger -AtLogOn -User ([Security.Principal.WindowsIdentity]::GetCurrent().Name)
$settings=New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Description 'Starts local paper-trading monitoring. New orders require manual recovery; Live is disabled.' | Out-Null
Write-Host "Registered $name at logon for the current user. Auto-trading remains disabled."
