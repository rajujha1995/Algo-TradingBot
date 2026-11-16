[CmdletBinding()]
param([switch]$NoBrowser)
# The operator workspace has one deployment: PROD, with read-only real-time research.
& "$PSScriptRoot/run-prod.ps1" -NoBrowser:$NoBrowser
