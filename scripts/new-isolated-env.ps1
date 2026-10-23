<#
.SYNOPSIS
  Provisions a dedicated isolated SQL Server LocalDB instance (STERPA08_<Name>) restored from the Agent 07 dataset backups.

.DESCRIPTION
  Parallel Agent 08 work streams each need their own isolated database, because every parity scenario restores the
  database it runs on. The instance is a separate LocalDB instance (profile isolated_environments.database:
  separate-instance), never the shared server. Data and backup files live under
  ..\run\STERP-App-isolated-tests\a08-aa6a44e4-<Name> next to this repository (STERP_ISOLATED_ROOT overrides the
  ..\run\STERP-App-isolated-tests part; see isolated-paths.ps1).

  The two dataset backups (freshc.bak = the legacy database deploy, democ.bak = the deploy plus the legacy demonstration
  data; docs/DEMO-DATA.md) are read from -SourceBackups, by default the backups folder of the main verification
  instance (..\run\STERP-App-isolated-tests\a08-aa6a44e4\backups).

  For trying the application: -Dataset demo restores the Agent 07 demo dataset (democ.bak) instead of the empty deploy
  (freshc.bak), and -MapLoginTo maps your Windows login to that STERP user exactly as the parity harness does (without a
  mapping the hosts answer 403 API0010). Mapping to SYSADM also allows "Continue as" any other user. Run it again to
  reset the instance to the dataset.

.EXAMPLE
  ./scripts/new-isolated-env.ps1 -Name itm
  ./scripts/new-isolated-env.ps1 -Name demo -Dataset demo -MapLoginTo SYSADM
  ./scripts/new-isolated-env.ps1 -Name demo -Dataset demo -MapLoginTo SYSADM -SourceBackups ..\sterp-datasets
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]{1,12}$')][string]$Name,
    [string]$SourceBackups,
    [ValidateSet('fresh', 'demo')][string]$Dataset = 'fresh',
    [ValidatePattern('^[A-Z0-9_]{0,20}$')][string]$MapLoginTo = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'isolated-paths.ps1')
if (-not $SourceBackups) { $SourceBackups = (Get-SterpIsolatedPaths 'STERPA08_aa6a44e4').Backups }
$SourceBackups = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($SourceBackups)
$instance = "STERPA08_$Name"
$server = "(localdb)\$instance"
$root = (Get-SterpIsolatedPaths $instance).Root
$sqlcmd = Get-SterpSqlcmd
New-Item -ItemType Directory -Force "$root\backups", "$root\data", "$root\out" | Out-Null
foreach ($b in 'freshc.bak', 'democ.bak') {
    if (-not (Test-Path "$root\backups\$b")) {
        $source = Join-Path $SourceBackups $b
        if (-not (Test-Path $source)) { throw "Dataset backup $source not found: build it from the legacy application (docs/DEMO-DATA.md) or pass -SourceBackups <folder>." }
        Copy-Item $source "$root\backups\$b"
    }
}
$info = sqllocaldb info 2>$null
if ($info -notcontains $instance) { sqllocaldb create $instance | Out-Host }
sqllocaldb start $instance | Out-Host
& $sqlcmd -S $server -E -C -b -Q "EXEC sp_configure 'contained database authentication', 1; RECONFIGURE;" | Out-Null
$inst = & $sqlcmd -S $server -E -C -b -h -1 -W -Q "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128))"
if (-not $inst -or "$inst".Trim() -eq 'NULL') { throw "Refusing ${server}: not a named/LocalDB instance" }
& $sqlcmd -S $server -E -C -b -Q "IF DB_ID('STERP') IS NOT NULL ALTER DATABASE [STERP] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; RESTORE DATABASE [STERP] FROM DISK = N'$root\backups\$($Dataset)c.bak' WITH REPLACE, MOVE N'STERP' TO N'$root\data\STERP.STERP.mdf', MOVE N'STERP_log' TO N'$root\data\STERP.STERP_log.ldf'; ALTER DATABASE [STERP] SET MULTI_USER;" | Out-Null
if ($MapLoginTo) {
    # same statements as the parity harness: the login maps to one user only and the audit trail keeps the dataset's state
    $login = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    $mapped = & $sqlcmd -S $server -E -C -b -I -d STERP -h -1 -W -Q "SET NOCOUNT ON; DECLARE @login nvarchar(200) = N'$($login.Replace("'", "''"))'; DECLARE @max bigint = (SELECT ISNULL(MAX(AuditId), 0) FROM core.AuditLog); UPDATE core.AppUser SET WindowsLogin = NULL WHERE WindowsLogin = @login; UPDATE core.AppUser SET WindowsLogin = @login WHERE UserCode = N'$MapLoginTo'; SELECT COUNT(*) FROM core.AppUser WHERE UserCode = N'$MapLoginTo' AND WindowsLogin = @login; DELETE FROM core.AuditLog WHERE AuditId > @max AND EntityType = 'core.AppUser'; DBCC CHECKIDENT ('core.AuditLog', RESEED, @max) WITH NO_INFOMSGS;"
    if ("$mapped".Trim() -ne '1') { throw "STERP user '$MapLoginTo' not found in the $Dataset dataset" }
    Write-Host "Windows login $login -> STERP user $MapLoginTo"
}
[ordered]@{
    created_at_utc   = (Get-Date).ToUniversalTime().ToString('o')
    instance         = $instance
    server           = $server
    database         = 'STERP'
    dataset          = "$($Dataset)c.bak"
    login_mapped_to  = $MapLoginTo
    owner_attempt_id = 'a08-20261020T204651Z-aa6a44e4'
    work_stream      = $Name
    source_backups   = $SourceBackups
    shared_database_contacted = $false
} | ConvertTo-Json | Set-Content "$root\ownership.json" -Encoding utf8
Write-Host "Isolated environment ready: server=$server dataset=$Dataset backups=$root\backups"
