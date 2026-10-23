<#
.SYNOPSIS
  Runs an Agent 07 safety-net tier against running target hosts and an ISOLATED database (Agent 08 verification).

.DESCRIPTION
  Sets the harness variables (tests/README.md) to an Agent 08 isolated instance and its backups, refuses anything else,
  builds tests/STERP.Parity.Tests.slnx once (unless -NoBuild) and runs the tier's xUnit v3 executable with any extra
  arguments after "--" (for example -trait "application=STERP-ITM", -class ..., -method ...). Host URLs follow the port
  convention of run-host.ps1 (Web = PortBase, Api = +1, Jobs = +2, Portal = +3). Agent 07 cases, contracts and fixtures
  are never modified.

  Harness environment for scenarios that stage files or inject faults (tests/README.md): STERP_TARGET_OUTPUT_ROOT and
  STERP_TARGET_LEGACY_RUN_ROOT name the hosts' Sterp:Jobs:OutputRoot and Sterp:Jobs:LegacyRunRoot (same defaults as
  run-host.ps1; pass -OutputRoot/-LegacyRunRoot when the hosts were started with them). STERP_TARGET_RESTART_COMMAND is
  a PowerShell command (run it with pwsh -NoProfile -Command after replacing {host} with Api, Jobs, Web or Portal) that
  kills that host and starts it again with the same configuration; it exits 0 once the host is ready.

.EXAMPLE
  ./scripts/run-parity.ps1 -Tier target -ResultXml out/itm.xml -- -trait "application=STERP-ITM"
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet('target', 'gui', 'legacy')][string]$Tier = 'target',
    [string]$Instance = 'STERPA08_aa6a44e4',
    [int]$PortBase = 7100,
    [string]$ResultXml,
    [string]$WebUrl,
    [string]$OutputRoot,
    [string]$LegacyRunRoot,
    [switch]$NoBuild,
    [Parameter(ValueFromRemainingArguments)][string[]]$XunitArgs = @()
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'isolated-paths.ps1')
$paths = Get-SterpIsolatedPaths $Instance
if (-not $OutputRoot) { $OutputRoot = $paths.OutputRoot }
if (-not $LegacyRunRoot) { $LegacyRunRoot = $paths.LegacyRunRoot }
Assert-SterpNotLiveLegacyRoot $LegacyRunRoot
New-Item -ItemType Directory -Force $LegacyRunRoot | Out-Null
$env:STERP_PARITY_SQLSERVER = "(localdb)\$Instance"
$env:STERP_PARITY_DATABASE = 'STERP'
$env:STERP_PARITY_BACKUPS = $paths.Backups
$env:STERP_TARGET_WEB_URL = if ($WebUrl) { $WebUrl } else { "https://localhost:$PortBase" }   # portal GUI scenarios navigate the harness Web URL: pass the portal URL
$env:STERP_TARGET_API_URL = "https://localhost:$($PortBase + 1)"
$env:STERP_TARGET_JOBS_URL = "https://localhost:$($PortBase + 2)"
$env:STERP_TARGET_PORTAL_URL = "https://localhost:$($PortBase + 3)"
$env:STERP_TARGET_OUTPUT_ROOT = $OutputRoot
$env:STERP_TARGET_LEGACY_RUN_ROOT = $LegacyRunRoot
$env:STERP_TARGET_RESTART_COMMAND = "& '$(Join-Path $PSScriptRoot 'restart-host.ps1')' -Name {host} -Instance $Instance -PortBase $PortBase -OutputRoot '$OutputRoot' -LegacyRunRoot '$LegacyRunRoot'"
if (-not $NoBuild) {
    dotnet build (Join-Path $root 'tests\STERP.Parity.Tests.slnx') -c Release --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'test build failed' }
}
$exe = switch ($Tier) {
    'target' { 'tests\parity\STERP.Target.Parity.Tests\bin\Release\net10.0\STERP.Target.Parity.Tests.exe' }
    'gui' { 'tests\parity\STERP.Target.Gui.Parity.Tests\bin\Release\net10.0\STERP.Target.Gui.Parity.Tests.exe' }
    'legacy' { 'tests\legacy\STERP.Legacy.Characterization.Tests\bin\Release\net10.0\STERP.Legacy.Characterization.Tests.exe' }
}
$argsList = @()
if ($ResultXml) {
    $full = if ([IO.Path]::IsPathRooted($ResultXml)) { [IO.Path]::GetFullPath($ResultXml) } else { [IO.Path]::GetFullPath((Join-Path (Get-Location) $ResultXml)) }
    New-Item -ItemType Directory -Force (Split-Path $full) | Out-Null
    $argsList += @('-xml', $full)
}
# theories are data rows: pre-enumerate them so -trait / -method filters see each row (xUnit v3)
$argsList += @('-preenumeratetheories')
$argsList += @($XunitArgs | Where-Object { $_ -ne '--' })
& (Join-Path $root $exe) @argsList
exit $LASTEXITCODE
