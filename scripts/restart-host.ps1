<#
.SYNOPSIS
  Kills one running STERP host (Api, Jobs, Web, Portal) of an Agent 08 isolated instance and starts it again with the same
  binaries and configuration - fault injection for parity scenarios that need a host restart (for example a job-host
  restart during a run, FR-STERP-PLATFORM-057). Exits 0 once the restarted host is ready.

.DESCRIPTION
  The host is found by its port (Web = PortBase, Api = +1, Jobs = +2, Portal = +3). Its dotnet process is killed (as a
  crash, no graceful shutdown) and run-host.ps1 of the repository whose binaries were running is started again, hidden,
  with the given instance, port base, output root and legacy run root (defaults as run-host.ps1). Undelivered slices or
  extra -Setting values of the original start are not reproduced. The new process id is written to
  <instance root>\hosts\<Name>-<PortBase>.pid. Only STERPA08_* instances are accepted.

.EXAMPLE
  ./scripts/restart-host.ps1 -Name Jobs -Instance STERPA08_platform -PortBase 7320
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][ValidateSet('Api', 'Jobs', 'Web', 'Portal')][string]$Name,
    [Parameter(Mandatory)][string]$Instance,
    [Parameter(Mandatory)][int]$PortBase,
    [string]$OutputRoot,
    [string]$LegacyRunRoot,
    [int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'isolated-paths.ps1')
$paths = Get-SterpIsolatedPaths $Instance
if (-not $OutputRoot) { $OutputRoot = $paths.OutputRoot }
if (-not $LegacyRunRoot) { $LegacyRunRoot = $paths.LegacyRunRoot }
Assert-SterpNotLiveLegacyRoot $LegacyRunRoot
$port = $PortBase + @{ Web = 0; Api = 1; Jobs = 2; Portal = 3 }[$Name]

$listener = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $listener) { throw "No host is listening on port $port" }
$proc = Get-CimInstance Win32_Process -Filter "ProcessId = $($listener.OwningProcess)"
$m = [regex]::Match([string]$proc.CommandLine, '(?<dll>[A-Za-z]:\\[^"]*?\\src\\Hosts\\(?<p>[^\\"]+)\\bin\\(?<cfg>[^\\"]+)\\net10\.0\\[^\\"]+\.dll)')
if (-not $m.Success) { throw "Process $($proc.ProcessId) on port $port is not a STERP host started by run-host.ps1: $($proc.CommandLine)" }
$repo = $m.Groups['dll'].Value.Substring(0, $m.Groups['dll'].Value.IndexOf('\src\Hosts\', [StringComparison]::OrdinalIgnoreCase))
$configuration = $m.Groups['cfg'].Value

Stop-Process -Id $proc.ProcessId -Force
$deadline = (Get-Date).AddSeconds(30)
while (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
    if ((Get-Date) -gt $deadline) { throw "Port $port still in use after stopping process $($proc.ProcessId)" }
    Start-Sleep -Milliseconds 200
}

$hostDir = Join-Path $paths.Root 'hosts'
New-Item -ItemType Directory -Force $hostDir | Out-Null
$log = Join-Path $hostDir "$Name-$PortBase-restart-$((Get-Date).ToString('yyyyMMddHHmmss')).log"
$new = Start-Process pwsh -PassThru -WindowStyle Hidden -RedirectStandardOutput $log -ArgumentList @(
    '-NoProfile', '-File', (Join-Path $repo 'scripts\run-host.ps1'), '-Name', $Name, '-Instance', $Instance, '-PortBase', $PortBase,
    '-OutputRoot', $OutputRoot, '-LegacyRunRoot', $LegacyRunRoot, '-Configuration', $configuration)
Set-Content (Join-Path $hostDir "$Name-$PortBase.pid") $new.Id

$url = if ($Name -in 'Api', 'Jobs') { "https://localhost:$port/health/ready" } else { "https://localhost:$port/" }
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ($true) {
    try { if ((Invoke-WebRequest $url -SkipCertificateCheck -TimeoutSec 10 -SkipHttpErrorCheck -UseDefaultCredentials).StatusCode -eq 200) { break } } catch { }
    if ($new.HasExited) { throw "Restarted $Name host exited (see $log)" }
    if ((Get-Date) -gt $deadline) { throw "Restarted $Name host on port $port not ready after $TimeoutSeconds s (see $log)" }
    Start-Sleep -Milliseconds 500
}
Write-Host "Restarted $Name on https://localhost:$port (process $($new.Id); stopped $($proc.ProcessId))"
exit 0
