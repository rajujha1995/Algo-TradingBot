[CmdletBinding()]
param([switch]$NoBrowser)
# The operator workspace has one deployment: PROD. PAPER/LIVE is a separate choice.
& "$PSScriptRoot/run-prod.ps1" -NoBrowser:$NoBrowser
