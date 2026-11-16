[CmdletBinding()]
param([switch]$InstallPrerequisites)
. "$PSScriptRoot/common.ps1"
Set-Location $script:RepoRoot
if ($InstallPrerequisites) {
 if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { throw 'winget is unavailable. Install .NET 10 SDK and Node.js 24 LTS manually using the README links.' }
 if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or -not ((& dotnet --list-sdks) -match '^10\.0\.')) {
  Invoke-Checked 'winget' @('install','--id','Microsoft.DotNet.SDK.10','--exact','--source','winget','--accept-package-agreements','--accept-source-agreements')
 }
 if (-not (Get-Command node -ErrorAction SilentlyContinue) -or [version]((& node --version).TrimStart('v')) -lt [version]'22.12.0') {
  Invoke-Checked 'winget' @('install','--id','OpenJS.NodeJS.LTS','--exact','--source','winget','--accept-package-agreements','--accept-source-agreements')
 }
 $env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
}
Assert-Prerequisites
Assert-PortFree 5101
foreach ($environment in @('production')) {
 foreach ($component in @('api','ui')) {
  $target = Join-Path $script:RepoRoot "$component/.env.$environment"
  if (-not (Test-Path $target)) {
   if (-not (Test-Path "$target.example")) { throw "Private local configuration required: create $component/.env.$environment before setup. Environment files and examples are intentionally excluded from the source-only repository." }
   Copy-Item "$target.example" $target
   if ($component -eq 'api') {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $password = [Convert]::ToBase64String($bytes)
    (Get-Content -Raw $target).Replace('REPLACE_WITH_UNIQUE_16_PLUS_CHARACTER_PASSWORD', $password) | Set-Content -Encoding UTF8 $target
   }
   Write-Host "Created $component/.env.$environment (existing files are never replaced)."
  }
 }
}
Invoke-Checked 'dotnet' @('tool','restore')
Invoke-Checked 'dotnet' @('restore','api/TradingBot.sln','--locked-mode')
Push-Location ui
try { Invoke-Checked 'npm' @('ci'); Invoke-Checked 'npm' @('run','build') } finally { Pop-Location }
Invoke-Checked 'dotnet' @('build','api/TradingBot.sln','--no-restore')
foreach ($environment in @('Production')) {
 Set-AppEnvironment $environment
 Invoke-Checked 'dotnet' @('run','--no-build','--no-launch-profile','--project','api/TradingBot.Api','--','--migrate-only')
}
Write-Host 'Setup complete. Run .\scripts\start.ps1.'
Write-Host 'Read LOCAL_ADMIN_PASSWORD in api/.env.production to sign in.'
Write-Host 'Optional: add INDSTOCKS_ACCESS_TOKEN to the relevant API file for read-only broker requests, then restart. No email/SMTP is required.'
