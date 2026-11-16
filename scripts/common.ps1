Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:RepoRoot = Split-Path $PSScriptRoot -Parent
$script:RuntimePath = Join-Path $script:RepoRoot '.runtime'
function Invoke-Checked {
 param([string]$Command, [string[]]$Arguments)
 & $Command @Arguments
 if ($LASTEXITCODE -ne 0) { throw "$Command failed with exit code $LASTEXITCODE" }
}
function Assert-Prerequisites {
 if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'Install .NET 10 SDK, then open a new terminal. Or rerun setup-windows.ps1 -InstallPrerequisites.' }
 $sdks = & dotnet --list-sdks
 if (-not ($sdks -match '^10\.0\.')) { throw '.NET 10 SDK is required. Install Microsoft.DotNet.SDK.10 and reopen the terminal.' }
 if (-not (Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js 24 LTS is recommended. Rerun setup-windows.ps1 -InstallPrerequisites.' }
 $nodeVersion = [version]((& node --version).TrimStart('v'))
 if ($nodeVersion -lt [version]'22.12.0') { throw 'Node.js >=22.12 is required; install Node.js 24 LTS.' }
 if (-not (Get-Command npm -ErrorAction SilentlyContinue)) { throw 'npm is required (included with Node.js).' }
}
function Set-AppEnvironment {
 param([ValidateSet('Production')][string]$Environment='Production')
 $env:ASPNETCORE_ENVIRONMENT = $Environment
 $env:APP_ENV = 'Production'
 $env:TRADINGBOT_ROOT = $script:RepoRoot
}
function Get-AppPort {
 param([string]$Environment)
 return 5101
}
function Wait-AppHealth {
 param([int]$Port)
 $deadline = (Get-Date).AddSeconds(45)
 do {
  try {
   $response = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/healthz" -TimeoutSec 2
   if ($response.status -eq 'ok') { return }
  } catch { }
  Start-Sleep -Milliseconds 500
 } while ((Get-Date) -lt $deadline)
 throw "API did not become healthy on port $Port. Inspect .runtime/*.err.log and .runtime/*.out.log."
}
function Assert-PortFree {
 param([int]$Port)
 $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
 try { $listener.Start() } catch { throw "Port $Port is already in use. Stop the previous application or use scripts/stop-bot.ps1 first." } finally { $listener.Stop() }
}
function Start-ManagedProcess {
 param([string]$Name,[string]$File,[string]$ArgumentString,[string]$Directory)
 New-Item -ItemType Directory -Force -Path $script:RuntimePath | Out-Null
 $recordPath = Join-Path $script:RuntimePath "$Name.json"
 if (Test-Path $recordPath) { throw "$Name has a process record. Run scripts/stop-bot.ps1 before restarting." }
 $launchOptions = @{}
 if ($env:OS -eq 'Windows_NT') { $launchOptions.WindowStyle = 'Hidden' }
 $process = Start-Process -FilePath $File -ArgumentList $ArgumentString -WorkingDirectory $Directory -RedirectStandardOutput (Join-Path $script:RuntimePath "$Name.out.log") -RedirectStandardError (Join-Path $script:RuntimePath "$Name.err.log") -PassThru @launchOptions
 try {
  $started = $process.StartTime.ToUniversalTime().Ticks
  @{ id = $process.Id; startedUtcTicks = $started; name = $Name } | ConvertTo-Json | Set-Content -Encoding UTF8 $recordPath
 } catch { throw "Failed to record $Name process: $_" }
 Write-Host "Started $Name (PID $($process.Id)). Logs: .runtime/$Name.*.log"
}
function Stop-ManagedProcess {
 param([string]$Name)
 $recordPath = Join-Path $script:RuntimePath "$Name.json"
 if (-not (Test-Path $recordPath)) { return }
 $record = Get-Content -Raw $recordPath | ConvertFrom-Json
 $process = Get-Process -Id $record.id -ErrorAction SilentlyContinue
 if ($null -ne $process) {
  if ($process.StartTime.ToUniversalTime().Ticks -ne $record.startedUtcTicks) { throw "PID reused for $Name. Refusing to terminate another process; inspect $recordPath." }
  # Terminate only the recorded process tree, including dotnet run / npm children.
  if ($env:OS -eq 'Windows_NT') { & taskkill.exe /PID $record.id /T /F | Out-Null; if ($LASTEXITCODE -ne 0) { throw "Failed to stop $Name" } }
  else { Stop-Process -Id $record.id -Force }
 }
 Remove-Item $recordPath -Force
 Write-Host "Stopped $Name."
}
