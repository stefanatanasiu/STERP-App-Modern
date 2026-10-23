<#
.SYNOPSIS
  Starts one STERP host (Api, Jobs, Web, Portal) in the foreground against the ISOLATED database only.

.DESCRIPTION
  Development and parity-test launcher (Agent 08). The binding production configuration (appsettings.json) names the
  shared database (server "."); this script always overrides it with an isolated instance (profile
  isolated_environments.database) and sets Sterp:Database:IsolatedOnly, so the host refuses to start against the shared
  server. Ports: Web = PortBase, Api = PortBase+1, Jobs = PortBase+2, Portal = PortBase+3 (default 7100 = the binding
  local URLs). Never point this script at server ".".

  Sterp:Jobs:LegacyRunRoot defaults to the instance's scratch folder (isolated-paths.ps1), never the live legacy folder
  ..\STERP-App\run that the binding production configuration names.

.EXAMPLE
  ./scripts/run-host.ps1 -Name Api
  ./scripts/run-host.ps1 -Name Jobs -Instance STERPA08_itm -PortBase 7120 -Undelivered STERP-SO,STERP-CST
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][ValidateSet('Api', 'Jobs', 'Web', 'Portal')][string]$Name,
    [string]$Instance = 'STERPA08_aa6a44e4',
    [string]$Database = 'STERP',
    [int]$PortBase = 7100,
    [string]$OutputRoot,
    [string]$LegacyRunRoot,
    [string]$Configuration = 'Release',
    [string[]]$Undelivered = @(),
    # extra configuration as Key=Value command-line settings (an empty value such as 'Sterp:Kernel:LegacyDriverMessagePrefix='
    # cannot be passed through an environment variable on Windows)
    [string[]]$Setting = @()
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'isolated-paths.ps1')
$paths = Get-SterpIsolatedPaths $Instance
$Server = "(localdb)\$Instance"
if (-not $OutputRoot) { $OutputRoot = $paths.OutputRoot }
if (-not $LegacyRunRoot) { $LegacyRunRoot = $paths.LegacyRunRoot }
Assert-SterpNotLiveLegacyRoot $LegacyRunRoot
New-Item -ItemType Directory -Force $LegacyRunRoot | Out-Null

$project = @{ Api = 'STERP.Api'; Jobs = 'STERP.Jobs'; Web = 'STERP.Web'; Portal = 'STERP.ApiPortal' }[$Name]
$port = $PortBase + @{ Web = 0; Api = 1; Jobs = 2; Portal = 3 }[$Name]
$dll = Join-Path $root "src\Hosts\$project\bin\$Configuration\net10.0\$project.dll"
if (-not (Test-Path $dll)) { throw "Build first: dotnet build STERP.Modernized.slnx -c $Configuration ($dll not found)" }

$env:ASPNETCORE_ENVIRONMENT = 'Parity'
$env:ASPNETCORE_CONTENTROOT = Join-Path $root "src\Hosts\$project"
$env:Sterp__Database__ConnectionString = "Server=$Server;Database=$Database;Integrated Security=True;Encrypt=Optional;TrustServerCertificate=True"
$env:Sterp__Database__IsolatedOnly = 'true'
$env:Sterp__Jobs__OutputRoot = $OutputRoot
$env:Sterp__Jobs__LegacyRunRoot = $LegacyRunRoot
$env:Kestrel__Endpoints__Https__Url = "https://localhost:$port"
$env:Sterp__Host__CorsOrigins__0 = "https://localhost:$($PortBase)"
$env:Sterp__Host__CorsOrigins__1 = "https://localhost:$($PortBase + 3)"
$env:Sterp__Web__ApiBaseUrl = "https://localhost:$($PortBase + 1)"
$env:Sterp__Web__JobsBaseUrl = "https://localhost:$($PortBase + 2)"
$env:Sterp__Portal__ApiBaseUrl = "https://localhost:$($PortBase + 1)"
$env:Sterp__Portal__JobsBaseUrl = "https://localhost:$($PortBase + 2)"
$Undelivered = @($Undelivered | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })   # -File passes "A,B" as one string
for ($i = 0; $i -lt $Undelivered.Count; $i++) { Set-Item "env:Sterp__Host__UndeliveredSlices__$i" $Undelivered[$i] }

Write-Host "Starting $project on https://localhost:$port against $Server/$Database (isolated; output $OutputRoot; legacy run root $LegacyRunRoot)"
$Setting = @($Setting | ForEach-Object { $_ -split ';' } | Where-Object { $_ -match '=' })   # -File passes "A=1;B=2" as one string
$cli = @($Setting | ForEach-Object { '--' + $_ })
Push-Location (Join-Path $root "src\Hosts\$project")
try { & dotnet $dll @cli } finally { Pop-Location }
