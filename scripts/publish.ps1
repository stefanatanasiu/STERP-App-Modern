<#
.SYNOPSIS
  Publishes the four STERP deployables and writes a release manifest with every file's SHA-256 (release evidence).

.DESCRIPTION
  Publishes STERP.Api, STERP.Jobs, STERP.Web (with its WebAssembly client) and STERP.ApiPortal as framework-dependent
  net10.0 applications (contracts section 1: four .NET 10 web applications on this machine) to <OutputDir>\<deployable>,
  then writes <OutputDir>\release-manifest.json (commit, clean/dirty tree, SDK, configuration, per-file hashes).
  Publishing deploys, starts and configures nothing. Running the published hosts against the shared database is a
  deployment that needs an Agent 09 decision and explicit human approval (docs/DEPLOYMENT.md).

.EXAMPLE
  ./scripts/publish.ps1
  ./scripts/publish.ps1 -OutputDir ..\drops\sterp-modern
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$OutputDir,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $root 'artifacts\publish' }
$OutputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
$deployables = 'STERP.Api', 'STERP.Jobs', 'STERP.Web', 'STERP.ApiPortal'

if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
foreach ($d in $deployables) {
    Write-Host "Publishing $d"
    dotnet publish (Join-Path $root "src\Hosts\$d\$d.csproj") -c $Configuration -o (Join-Path $OutputDir $d) --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $d" }
}

$files = Get-ChildItem $OutputDir -Recurse -File | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        path   = $_.FullName.Substring($OutputDir.Length + 1).Replace('\', '/')
        bytes  = $_.Length
        sha256 = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
$manifest = [ordered]@{
    product          = 'STERP modernized (STERP-API, STERP-JOBS, STERP-WEB, STERP-PORTAL)'
    commit           = (git -C $root rev-parse HEAD)
    workingTreeClean = -not [bool](git -C $root status --porcelain)
    sdk              = (dotnet --version)
    configuration    = $Configuration
    createdUtc       = (Get-Date).ToUniversalTime().ToString('o')
    deployables      = $deployables
    files            = @($files)
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDir 'release-manifest.json') -Encoding utf8
Write-Host "Published $($files.Count) files to $OutputDir (release-manifest.json)"
