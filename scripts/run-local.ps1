<#
.SYNOPSIS
  Starts the four STERP deployables together (Web PortBase, Api +1, Jobs +2, Portal +3) - the no-Aspire local start of
  contracts section 1.1 - and runs the smoke check.

.DESCRIPTION
  Default (isolated): every host is started through run-host.ps1 against an Agent 08 isolated LocalDB instance
  (STERPA08_*), for demonstrations and verification. Nothing touches the shared database.

  -SharedDatabase: starts the PUBLISHED hosts (scripts/publish.ps1) with their binding production configuration
  (appsettings.json: server ".", database STERP shared in place with the legacy application, ports 7100-7103). That is
  a deployment to the shared environment. It requires an Agent 09 replacement-ready decision and explicit human approval,
  whose reference must be passed as -ApprovalReference; the script refuses otherwise. Agent 08 never runs this mode.

  The started processes keep running; stop them with Stop-Process -Id <pid> (the ids are printed).

.EXAMPLE
  ./scripts/run-local.ps1
  ./scripts/run-local.ps1 -Instance STERPA08_demo -PortBase 7140
  ./scripts/run-local.ps1 -SharedDatabase -ApprovalReference evt-... -PublishDir .\artifacts\publish
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Instance = 'STERPA08_aa6a44e4',
    [int]$PortBase = 7100,
    [switch]$SharedDatabase,
    [string]$ApprovalReference,
    [string]$PublishDir,
    # isolated mode only: passed to every run-host.ps1 (defaults: the instance's isolated folders)
    [string]$OutputRoot,
    [string]$LegacyRunRoot
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$started = @()

if ($SharedDatabase) {
    if ([string]::IsNullOrWhiteSpace($ApprovalReference)) {
        throw 'Refusing: -SharedDatabase deploys against the shared STERP database and needs -ApprovalReference (Agent 09 decision and explicit human approval).'
    }
    if ($PortBase -ne 7100) { throw 'The shared deployment uses the binding ports 7100-7103.' }
    if (-not $PublishDir) { $PublishDir = Join-Path $root 'artifacts\publish' }
    if (-not (Test-Path (Join-Path $PublishDir 'release-manifest.json'))) { throw "No published release in $PublishDir - run scripts/publish.ps1 first." }
    Write-Host "Starting the published release from $PublishDir against the SHARED database (approval $ApprovalReference)"
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    foreach ($d in 'STERP.Api', 'STERP.Jobs', 'STERP.Web', 'STERP.ApiPortal') {
        $dir = Join-Path $PublishDir $d
        $started += Start-Process dotnet -ArgumentList "`"$(Join-Path $dir "$d.dll")`"" -WorkingDirectory $dir -PassThru -WindowStyle Minimized
    }
}
else {
    if ($Instance -notmatch '^STERPA08_[A-Za-z0-9]+$') { throw "Refusing instance '$Instance': the default mode runs only on isolated LocalDB instances (STERPA08_*)." }
    foreach ($h in 'Api', 'Jobs', 'Web', 'Portal') {
        $hostArgs = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'run-host.ps1'), '-Name', $h, '-Instance', $Instance, '-PortBase', $PortBase)
        if ($OutputRoot) { $hostArgs += @('-OutputRoot', $OutputRoot) }
        if ($LegacyRunRoot) { $hostArgs += @('-LegacyRunRoot', $LegacyRunRoot) }
        $started += Start-Process pwsh -ArgumentList $hostArgs -PassThru -WindowStyle Minimized
    }
}

$deadline = (Get-Date).AddSeconds(180)
foreach ($url in "https://localhost:$($PortBase + 1)/health/live", "https://localhost:$($PortBase + 2)/health/live", "https://localhost:$PortBase/", "https://localhost:$($PortBase + 3)/") {
    while ($true) {
        try { if ((Invoke-WebRequest $url -SkipCertificateCheck -TimeoutSec 10 -SkipHttpErrorCheck -UseDefaultCredentials).StatusCode -eq 200) { break } } catch { }
        if ((Get-Date) -gt $deadline) { throw "Host at $url did not start; started process ids: $($started.Id -join ', ')" }
        Start-Sleep -Seconds 2
    }
}
Write-Host "Started process ids: $($started.Id -join ', ') (stop with Stop-Process -Id <id>)"
& (Join-Path $PSScriptRoot 'smoke.ps1') -PortBase $PortBase
