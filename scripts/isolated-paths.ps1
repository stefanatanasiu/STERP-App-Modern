<#
.SYNOPSIS
  Isolated verification paths of one Agent 08 LocalDB instance (STERPA08_<name>). Dot-source this file and call
  Get-SterpIsolatedPaths; run-host.ps1, run-parity.ps1, restart-host.ps1 and new-isolated-env.ps1 share it so hosts and
  harness agree.

.DESCRIPTION
  Every location is derived from where this repository is checked out. Its parent folder (the workspace) also holds the
  legacy application clone (STERP-App) and the isolated-test scratch area (run\STERP-App-isolated-tests); set
  STERP_ISOLATED_ROOT to keep that scratch area somewhere else.

  OutputRoot is the instance's target run root (Sterp:Jobs:OutputRoot). LegacyRunRoot is a scratch stand-in for the
  legacy run root (Sterp:Jobs:LegacyRunRoot): the binding default ..\STERP-App\run is the live folder of the legacy
  application, so verification never reads fixtures from it or writes fixtures to it.
#>
$script:SterpWorkspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

function Get-SterpIsolatedTestsRoot {
    if ($env:STERP_ISOLATED_ROOT) { return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($env:STERP_ISOLATED_ROOT) }
    Join-Path $script:SterpWorkspace 'run\STERP-App-isolated-tests'
}

function Get-SterpLiveLegacyRoot {
    Join-Path $script:SterpWorkspace 'STERP-App'
}

function Get-SterpIsolatedPaths {
    param([Parameter(Mandatory)][string]$Instance)
    if ($Instance -notmatch '^STERPA08_[A-Za-z0-9]+$') { throw "Refusing instance '$Instance': only Agent 08 isolated LocalDB instances (STERPA08_*) are allowed." }
    $suffix = if ($Instance -eq 'STERPA08_aa6a44e4') { '' } else { '-' + $Instance.Substring(9) }
    $root = Join-Path (Get-SterpIsolatedTestsRoot) "a08-aa6a44e4$suffix"
    [pscustomobject]@{
        Root          = $root
        Backups       = Join-Path $root 'backups'
        OutputRoot    = Join-Path $root 'out\run'
        LegacyRunRoot = Join-Path $root 'legacy-run-root'
    }
}

function Assert-SterpNotLiveLegacyRoot {
    param([Parameter(Mandatory)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $live = Get-SterpLiveLegacyRoot
    if ($full -ieq $live -or $full.StartsWith("$live\", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing legacy run root '$Path': it is the live legacy application folder; use an isolated scratch folder."
    }
}

function Get-SterpSqlcmd {
    # STERP_SQLCMD_DIR (as the Agent 07 harness), else the ODBC 18 sqlcmd in its standard location, else sqlcmd on PATH
    $candidates = @()
    if ($env:STERP_SQLCMD_DIR) { $candidates += Join-Path $env:STERP_SQLCMD_DIR 'SQLCMD.EXE' }
    $candidates += Join-Path $env:ProgramFiles 'Microsoft SQL Server\Client SDK\ODBC\180\Tools\Binn\SQLCMD.EXE'
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $onPath = Get-Command sqlcmd.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Source }
    throw 'sqlcmd not found: install the ODBC 18 sqlcmd utility, set STERP_SQLCMD_DIR or put sqlcmd.exe on PATH.'
}
