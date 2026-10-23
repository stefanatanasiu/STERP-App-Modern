<#
.SYNOPSIS
  Runs the Agent 07 wave-transition scenarios in the reduced (earlier-wave) configurations they describe (Agent 08 verification).

.DESCRIPTION
  A few scenarios assert the coexistence behaviour of a slice that is not yet delivered (501 API0501 naming the slice, the
  GUI placeholder, the portal "planned" marker). With every slice delivered they cannot pass, so each is run against hosts
  started with Sterp:Host:UndeliveredSlices = the slices of its own and later waves (contracts section 3 wave order). For
  every configuration the script starts Api, Jobs, Web and Portal on the isolated instance, waits for readiness, runs the
  scenario rows by requirement trait, writes one result XML per tier and stops the hosts. The 'charz' configuration keeps
  every slice delivered and starts the hosts with an empty legacy driver message prefix (FR-STERP-PLATFORM-010 AC5).
  Agent 07 cases are not modified.

.EXAMPLE
  ./scripts/run-wave-transitions.ps1 -ResultDir ..\run\STERP-App-isolated-tests\a08-aa6a44e4\results\final
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Instance = 'STERPA08_aa6a44e4',
    [int]$PortBase = 7100,
    [Parameter(Mandatory)][string]$ResultDir,
    # default: this repository's run folder, where the binding production configuration writes (contracts 8.5)
    [string]$OutputRoot,
    [string[]]$Only = @()
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $root 'run' }
$waves = 'STERP-ITM', 'STERP-AR', 'STERP-SO', 'STERP-XMD', 'STERP-FIN', 'STERP-PO', 'STERP-AP', 'STERP-BOM', 'STERP-KIT', 'STERP-CST'
function WavesFrom([string]$slice) { $waves[$waves.IndexOf($slice)..($waves.Count - 1)] }

# configuration -> undelivered slices (and extra host settings) and the requirement rows asserting that state
$configs = [ordered]@{
    'pre-W2'  = @{ Undelivered = (WavesFrom 'STERP-AR'); Api = @('FR-STERP-PLATFORM-035'); Gui = @(); Portal = @() }
    'pre-W3'  = @{ Undelivered = (WavesFrom 'STERP-SO'); Api = @('FR-STERP-API-002'); Gui = @('FR-STERP-WEB-020'); Portal = @('FR-STERP-PORTAL-002', 'FR-STERP-PORTAL-011') }
    'pre-W5'  = @{ Undelivered = (WavesFrom 'STERP-FIN'); Api = @('FR-STERP-PLATFORM-044'); Gui = @('FR-STERP-FIN-002'); Portal = @() }
    'pre-W6'  = @{ Undelivered = (WavesFrom 'STERP-PO'); Api = @(); Gui = @('FR-STERP-PO-002'); Portal = @() }
    'pre-W7'  = @{ Undelivered = (WavesFrom 'STERP-AP'); Api = @('FR-STERP-FIN-061'); Gui = @('FR-STERP-AP-002'); Portal = @() }
    'pre-W10' = @{ Undelivered = (WavesFrom 'STERP-CST'); Api = @('FR-STERP-FIN-060', 'FR-STERP-JOBS-002'); Gui = @(); Portal = @() }
    # every slice delivered, legacy driver message prefix empty (the characterization configuration of FR-STERP-PLATFORM-010 AC5)
    'charz'   = @{ Undelivered = @(); Settings = @('Sterp:Kernel:LegacyDriverMessagePrefix='); Api = @('FR-STERP-PLATFORM-010'); Gui = @(); Portal = @() }
}

function Stop-Tree([int]$ProcessId) {
    foreach ($child in Get-CimInstance Win32_Process -Filter "ParentProcessId = $ProcessId" -ErrorAction SilentlyContinue) { Stop-Tree $child.ProcessId }
    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
}

function Wait-Url([string]$url) {
    $deadline = (Get-Date).AddSeconds(180)
    while ((Get-Date) -lt $deadline) {
        try { if ((Invoke-WebRequest $url -SkipCertificateCheck -TimeoutSec 10 -SkipHttpErrorCheck -UseDefaultCredentials).StatusCode -eq 200) { return } } catch { }
        Start-Sleep -Seconds 2
    }
    throw "Host at $url did not become ready"
}

New-Item -ItemType Directory -Force $ResultDir | Out-Null
foreach ($name in $configs.Keys) {
    if ($Only.Count -gt 0 -and $Only -notcontains $name) { continue }
    $c = $configs[$name]
    Write-Host "== $name : undelivered $($c.Undelivered -join ', ') $(if ($c.Settings) { 'settings ' + ($c.Settings -join '; ') })"
    $hosts = @()
    try {
        foreach ($h in 'Api', 'Jobs', 'Web', 'Portal') {
            $hostArgs = @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'run-host.ps1'), '-Name', $h, '-Instance', $Instance, '-PortBase', $PortBase,
                '-OutputRoot', $OutputRoot)
            if ($c.Undelivered.Count -gt 0) { $hostArgs += @('-Undelivered', ($c.Undelivered -join ',')) }
            if ($c.Settings) { $hostArgs += @('-Setting', ($c.Settings -join ';')) }
            $hosts += Start-Process pwsh -ArgumentList $hostArgs -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $ResultDir "wave-$name-$h.log")
        }
        Wait-Url "https://localhost:$($PortBase + 1)/health/ready"
        Wait-Url "https://localhost:$($PortBase + 2)/health/ready"
        Wait-Url "https://localhost:$PortBase/"
        Wait-Url "https://localhost:$($PortBase + 3)/"
        $runs = @(
            @{ Tier = 'target'; Reqs = $c.Api; File = "wave-$name-api.xml"; Web = $null },
            @{ Tier = 'gui'; Reqs = $c.Gui; File = "wave-$name-gui.xml"; Web = $null },
            @{ Tier = 'gui'; Reqs = $c.Portal; File = "wave-$name-portal.xml"; Web = "https://localhost:$($PortBase + 3)" })
        foreach ($r in $runs) {
            if ($r.Reqs.Count -eq 0) { continue }
            $filters = @(); foreach ($q in $r.Reqs) { $filters += @('-trait', "requirement=$q") }
            $p = @{ Tier = $r.Tier; Instance = $Instance; PortBase = $PortBase; NoBuild = $true; ResultXml = (Join-Path $ResultDir $r.File); OutputRoot = $OutputRoot }
            if ($r.Web) { $p.WebUrl = $r.Web }
            & (Join-Path $PSScriptRoot 'run-parity.ps1') @p -- @filters | Select-Object -Last 2 | Out-Host
        }
    }
    finally {
        foreach ($p in $hosts) { Stop-Tree $p.Id }
        # a host restarted by STERP_TARGET_RESTART_COMMAND is not in $hosts
        foreach ($port in $PortBase..($PortBase + 3)) {
            foreach ($conn in Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { Stop-Tree $conn.OwningProcess }
        }
    }
}
