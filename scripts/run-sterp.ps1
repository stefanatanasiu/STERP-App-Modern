<#
.SYNOPSIS
  Starts, stops, restarts, resets or reports the four STERP services of the demo environment in one command.
  run-sterp.bat in the repository root is the double-clickable front end.

.DESCRIPTION
  The demo environment is the README quick start in one step: the isolated LocalDB instance STERPA08_demo, restored from
  the demo dataset with your Windows login mapped to SYSADM, and the four services on ports 7140-7143: STERP.Web (GUI),
  STERP.Api (online API), STERP.Jobs (batch jobs API) and STERP.ApiPortal (API Console). The services run hidden in the
  background through run-host.ps1, so only the isolated database is used. Their console output is written to
  <instance folder>\hosts\<service>-<port base>.log (isolated-paths.ps1).

  Start    builds STERP.Modernized.slnx in Release (not with -NoBuild, and not while a STERP host of this repository still
           runs on the binaries), creates the demo instance on the first start (new-isolated-env.ps1), starts every
           service that is not running, waits until each one is ready, checks your sign-on and opens the GUI.
  Stop     stops the services on the demo ports that were started from this repository. It refuses while STERP.Jobs is
           executing batch runs, because a job host stopped mid-run marks those runs LOST on its next start; -Force stops
           it anyway.
  Restart  Stop, then Start.
  Reset    Stop, restore the demo dataset (many demo steps are one-shot), then Start.
  Status   reports each service with its process and readiness. Exit code 0 only when all four are up and ready.

.EXAMPLE
  ./scripts/run-sterp.ps1
  ./scripts/run-sterp.ps1 -Action Stop
  ./scripts/run-sterp.ps1 -Action Reset -NoBuild -NoBrowser
  ./scripts/run-sterp.ps1 -Name demo2 -PortBase 7150      # a second, separate demo environment
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet('Start', 'Stop', 'Restart', 'Reset', 'Status', 'Help')][string]$Action = 'Start',
    [switch]$NoBuild,
    [switch]$NoBrowser,
    [switch]$Force,
    # the demo environment: LocalDB instance STERPA08_<Name>, ports PortBase to PortBase+3, your Windows login mapped to User
    [ValidatePattern('^[a-z0-9]{1,12}$')][string]$Name = 'demo',
    [ValidateRange(1024, 65532)][int]$PortBase = 7140,
    [ValidatePattern('^[A-Z0-9_]{1,20}$')][string]$User = 'SYSADM',
    [ValidateRange(30, 1800)][int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'isolated-paths.ps1')

$instance = "STERPA08_$Name"
$hostsDir = Join-Path (Get-SterpIsolatedPaths $instance).Root 'hosts'
$solution = Join-Path $root 'STERP.Modernized.slnx'
$launcher = Join-Path $PSScriptRoot 'run-host.ps1'
$pwsh = (Get-Process -Id $PID).Path
$svcWeb = [pscustomobject]@{ Key = 'Web'; Project = 'STERP.Web'; Label = 'GUI'; Port = $PortBase; Database = $false }
$svcApi = [pscustomobject]@{ Key = 'Api'; Project = 'STERP.Api'; Label = 'Online API'; Port = $PortBase + 1; Database = $true }
$svcJobs = [pscustomobject]@{ Key = 'Jobs'; Project = 'STERP.Jobs'; Label = 'Batch jobs'; Port = $PortBase + 2; Database = $true }
$svcPortal = [pscustomobject]@{ Key = 'Portal'; Project = 'STERP.ApiPortal'; Label = 'API Console'; Port = $PortBase + 3; Database = $false }
$services = @($svcWeb, $svcApi, $svcJobs, $svcPortal)
# the API hosts start first, so the API Console finds them when it first looks; they stop last
$startOrder = @($svcApi, $svcJobs, $svcWeb, $svcPortal)
$stopOrder = @($svcPortal, $svcWeb, $svcJobs, $svcApi)

function Say([string]$Text = '', [string]$Color = '') {
    if ($Color) { Write-Host $Text -ForegroundColor $Color } else { Write-Host $Text }
}

function Url($Svc) { "https://localhost:$($Svc.Port)" }

function Row($Svc) { '{0,-12} {1,-16} {2,5}' -f $Svc.Label, $Svc.Project, $Svc.Port }

function Has([string]$Text, [string]$Part) { $Text.IndexOf($Part, [StringComparison]::OrdinalIgnoreCase) -ge 0 }

function LogPath($Svc, [string]$Kind) { Join-Path $hostsDir ('{0}-{1}{2}.log' -f $Svc.Key, $PortBase, $Kind) }

# a STERP host built in this repository: dotnet <root>\src\Hosts\<Project>\bin\<configuration>\net10.0\<Project>.dll
function Test-OwnHost($Process, [string]$Project) {
    $null -ne $Process -and (Has ([string]$Process.CommandLine) "$root\src\Hosts\$Project\bin\")
}

<#
  One entry per service:
    up        a STERP host of this repository listens on the service port
    starting  run-host.ps1 runs for the service, but its host does not listen yet
    foreign   another program listens on the service port
    down      nothing
#>
function Get-States {
    $table = @(Get-CimInstance Win32_Process -Property ProcessId, ParentProcessId, Name, CommandLine)
    $owners = @{}
    foreach ($c in @(Get-NetTCPConnection -State Listen -LocalPort $services.Port -ErrorAction SilentlyContinue)) {
        if (-not $owners.ContainsKey([int]$c.LocalPort)) { $owners[[int]$c.LocalPort] = [int]$c.OwningProcess }
    }
    foreach ($svc in $services) {
        $owner = $owners[$svc.Port]
        $listener = if ($null -ne $owner) { $table | Where-Object { $_.ProcessId -eq $owner } | Select-Object -First 1 }
        $launchers = @($table | Where-Object {
                $_.Name -eq 'pwsh.exe' -and (Has ([string]$_.CommandLine) $launcher) -and
                [string]$_.CommandLine -match "\s-Name\s+`"?$($svc.Key)`"?(\s|$)" -and
                [string]$_.CommandLine -match "\s-PortBase\s+`"?$PortBase`"?(\s|$)" })
        $children = @($table | Where-Object { $_.Name -eq 'dotnet.exe' -and $launchers.ProcessId -contains $_.ParentProcessId })
        $state = if ($null -eq $owner) { if ($launchers.Count) { 'starting' } else { 'down' } }
        elseif (Test-OwnHost $listener $svc.Project) { 'up' }
        else { 'foreign' }
        [pscustomobject]@{
            Service   = $svc
            State     = $state
            ProcessId = $owner
            Owner     = if ($listener) { $listener.Name } else { 'an unknown process' }
            Launchers = $launchers
            Children  = $children
        }
    }
}

# every STERP host of this repository on any port: they lock the binaries a build would replace
function Get-RunningHosts {
    @(Get-CimInstance Win32_Process -Filter "Name = 'dotnet.exe'" -Property ProcessId, CommandLine |
            Where-Object { Has ([string]$_.CommandLine) "$root\src\Hosts\" })
}

function Invoke-Probe([string]$Url, [int]$TimeoutSec = 5, [switch]$Windows) {
    try {
        $r = Invoke-WebRequest $Url -SkipCertificateCheck -SkipHttpErrorCheck -TimeoutSec $TimeoutSec -UseDefaultCredentials:$Windows
        $text = if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { [string]$r.Content }
        $json = $null
        try { $json = $text | ConvertFrom-Json -Depth 32 } catch { }
        [pscustomobject]@{ Status = [int]$r.StatusCode; Json = $json }
    }
    catch { $null }
}

# /health/ready as one line: status, delivered slices, executing batch runs and every check that is not Healthy
function Get-Readiness($Svc) {
    $probe = Invoke-Probe "$(Url $Svc)/health/ready"
    if (-not $probe) { return [pscustomobject]@{ Ready = $false; Text = 'not answering'; Runs = $null } }
    $json = $probe.Json
    $status = if ($json -and $json.status) { [string]$json.status } else { "HTTP $($probe.Status)" }
    $checks = @(if ($json -and $json.checks) { $json.checks })
    $queue = $checks | Where-Object { $_.name -eq 'job-queue' } | Select-Object -First 1
    $runs = if ($queue -and $queue.data) { $queue.data.depth } else { $null }
    $notes = [System.Collections.Generic.List[string]]::new()
    if ($json -and $json.deliveredSlices) { $notes.Add("$(@($json.deliveredSlices).Count) slices delivered") }
    if ($null -ne $runs) { $notes.Add("$runs batch run(s) executing") }
    foreach ($c in $checks | Where-Object { $_.status -ne 'Healthy' }) { $notes.Add("$($c.name): $($c.description)") }
    [pscustomobject]@{
        Ready = ($probe.Status -eq 200 -and $status -eq 'Healthy')
        Text  = if ($notes.Count) { "$status - $($notes -join '; ')" } else { $status }
        Runs  = $runs
    }
}

function Show-LogTail($Svc) {
    foreach ($kind in '.err', '') {
        $path = LogPath $Svc $kind
        $lines = @(if (Test-Path $path) { Get-Content $path -Tail 12 -ErrorAction SilentlyContinue | Where-Object { $_.Trim() } })
        if ($lines.Count -eq 0) { continue }
        Say "           $path" DarkGray
        foreach ($line in $lines) { Say ('             ' + $(if ($line.Length -gt 300) { $line.Substring(0, 300) + ' ...' } else { $line })) DarkGray }
    }
}

function Stop-SterpService($State) {
    $ids = @(@(if ($State.State -eq 'up') { $State.ProcessId }) + @($State.Children | ForEach-Object { $_.ProcessId }) |
            Where-Object { $_ } | ForEach-Object { [int]$_ } | Select-Object -Unique)
    foreach ($id in $ids) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
    # run-host.ps1 ends by itself once its host has stopped
    $deadline = (Get-Date).AddSeconds(5)
    foreach ($l in $State.Launchers) {
        while ((Get-Process -Id $l.ProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
        Stop-Process -Id $l.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Say ("    [stopped] {0}  {1}" -f (Row $State.Service), $(if ($ids.Count) { 'pid ' + ($ids -join ', ') } else { 'launcher only' }))
}

function Invoke-Stop {
    $states = @(Get-States)
    foreach ($st in $states | Where-Object { $_.State -eq 'foreign' }) {
        Say ("  Port {0} is used by {1} (pid {2}), which was not started from this folder - left running." -f $st.Service.Port, $st.Owner, $st.ProcessId) Yellow
    }
    $active = @($states | Where-Object { $_.State -in 'up', 'starting' })
    if ($active.Count -eq 0) {
        Say ("  Nothing to stop: no STERP service of this folder runs on ports {0}-{1}." -f $PortBase, ($PortBase + 3))
        return $true
    }
    $jobs = $active | Where-Object { $_.Service -eq $svcJobs -and $_.State -eq 'up' }
    if ($jobs -and -not $Force) {
        $runs = (Get-Readiness $svcJobs).Runs
        if ($runs -gt 0) {
            Say "  STERP.Jobs is executing $runs batch run(s). A job host stopped now marks them LOST on its next start." Yellow
            Say '  Wait until they have finished (Job Monitor in the GUI), or add /force to stop anyway. Nothing was stopped.' Yellow
            return $false
        }
    }
    Say '  Stopping services ...'
    foreach ($svc in $stopOrder) {
        $st = $active | Where-Object { $_.Service -eq $svc }
        if ($st) { Stop-SterpService $st }
    }
    # a host that survived would only show up as a bind error on the next start
    $ports = @($active | ForEach-Object { $_.Service.Port })
    $deadline = (Get-Date).AddSeconds(15)
    while ($true) {
        $busy = @(Get-NetTCPConnection -State Listen -LocalPort $ports -ErrorAction SilentlyContinue)
        if ($busy.Count -eq 0) { break }
        if ((Get-Date) -gt $deadline) {
            foreach ($c in $busy | Sort-Object LocalPort -Unique) { Say ("  Port {0} is still in use by pid {1}." -f $c.LocalPort, $c.OwningProcess) Red }
            return $false
        }
        Start-Sleep -Milliseconds 300
    }
    Say '  All STERP services stopped.' Green
    return $true
}

# polls each service until it is ready: /health/ready Healthy for the API hosts (database and schema), /health/live otherwise
function Wait-Ready([object[]]$Targets, [hashtable]$Processes) {
    $pending = [System.Collections.Generic.List[object]]::new()
    foreach ($t in $Targets) { $pending.Add($t) }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $unhealthySince = @{}
    $ok = $true
    while ($pending.Count -gt 0) {
        foreach ($svc in @($pending)) {
            # Windows takes seconds to refuse a connection to a closed localhost port: probe only once the host listens
            $listening = @(Get-NetTCPConnection -State Listen -LocalPort $svc.Port -ErrorAction SilentlyContinue).Count -gt 0
            $probe = if ($listening) { Invoke-Probe ('{0}/health/{1}' -f (Url $svc), $(if ($svc.Database) { 'ready' } else { 'live' })) }
            $status = if ($probe -and $probe.Json) { [string]$probe.Json.status } else { '' }
            if ($probe -and $probe.Status -eq 200 -and ($status -eq 'Healthy' -or -not $svc.Database)) {
                Say ("    [OK]   {0}  {1}" -f (Row $svc), (Url $svc)) Green
                [void]$pending.Remove($svc)
                continue
            }
            $process = $Processes[$svc.Key]
            if ($process -and $process.HasExited) {
                Say ("    [FAIL] {0}  stopped during start-up" -f (Row $svc)) Red
                Show-LogTail $svc
                [void]$pending.Remove($svc)
                $ok = $false
                continue
            }
            # an answer that stays unhealthy (database missing, schema drift) does not heal by waiting
            if ($status -and $status -ne 'Healthy') {
                if (-not $unhealthySince.ContainsKey($svc.Key)) { $unhealthySince[$svc.Key] = Get-Date }
                elseif (((Get-Date) - $unhealthySince[$svc.Key]).TotalSeconds -ge 45) {
                    Say ("    [FAIL] {0}  {1}" -f (Row $svc), (Get-Readiness $svc).Text) Red
                    [void]$pending.Remove($svc)
                    $ok = $false
                    continue
                }
            }
            else { $unhealthySince.Remove($svc.Key) }
        }
        if ($pending.Count -eq 0) { break }
        if ((Get-Date) -gt $deadline) {
            foreach ($svc in $pending) {
                Say ("    [FAIL] {0}  not ready after {1} seconds" -f (Row $svc), $TimeoutSeconds) Red
                Show-LogTail $svc
            }
            return $false
        }
        Start-Sleep -Milliseconds 750
    }
    return $ok
}

function Invoke-Provision {
    & (Join-Path $PSScriptRoot 'new-isolated-env.ps1') -Name $Name -Dataset demo -MapLoginTo $User | Out-Host
}

function Show-SignOn {
    $probe = Invoke-Probe "$(Url $svcApi)/api/platform/v1/session" 30 -Windows
    $json = if ($probe) { $probe.Json }
    if ($probe -and $probe.Status -eq 200 -and $json.user) {
        Say ("  Signed on as {0} ({1}) with your Windows login {2}." -f $json.user.userCode, $json.user.displayName, $json.windowsLogin) Green
    }
    elseif ($probe -and $probe.Status -eq 403 -and $json.reasonCode -eq 'API0010') {
        Say "  $($json.detail) The GUI and the API Console answer 403 until it is." Yellow
        Say "  `"run-sterp.bat /reset`" maps it to $User again (and restores the demo data)." Yellow
    }
    else {
        Say ("  Could not confirm your sign-on: GET /api/platform/v1/session answered {0}." -f $(if ($probe) { $probe.Status } else { 'nothing' })) Yellow
    }
}

function Show-Ready {
    Show-SignOn
    Say ''
    # only addresses that open in a browser: the API hosts are web services without pages, browsed in the API Console
    Say ("    {0,-12} {1}" -f $svcWeb.Label, (Url $svcWeb))
    Say ("    {0,-12} {1,-36} browse and try every Online API and Batch jobs operation" -f $svcPortal.Label, (Url $svcPortal))
    foreach ($svc in $svcApi, $svcJobs) {
        Say ("    {0,-12} {1,-36} web service without pages: its health check" -f $svc.Label, "$(Url $svc)/health/ready")
    }
    Say ''
    if (-not $NoBrowser) {
        Say '  Opening the GUI in your browser ...'
        Start-Process (Url $svcWeb)
    }
    Say "  The services run in the background without windows; their logs are in $hostsDir."
    Say '  "run-sterp.bat /stop" stops them and "run-sterp.bat /status" shows how they are.'
}

function Invoke-Start {
    $states = @(Get-States)
    $foreign = @($states | Where-Object { $_.State -eq 'foreign' })
    if ($foreign.Count) {
        foreach ($st in $foreign) {
            Say ("  Port {0} ({1}) is in use by {2} (pid {3}), which is not a STERP service from this folder." -f $st.Service.Port, $st.Service.Label, $st.Owner, $st.ProcessId) Red
        }
        Say '  Stop that program first, or run the demo on other ports: scripts\run-sterp.ps1 -PortBase <port>.' Red
        return $false
    }
    if (@($states | Where-Object { $_.State -eq 'up' }).Count -eq $services.Count) {
        Say '  STERP is already running.' Green
        Show-Ready
        return $true
    }
    foreach ($tool in 'dotnet', 'sqllocaldb') {
        if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
            Say "  $tool was not found: install the prerequisites listed in README.md (.NET SDK 10, SQL Server LocalDB)." Red
            return $false
        }
    }
    & dotnet dev-certs https --check --trust *> $null
    if ($LASTEXITCODE -ne 0) {
        Say '  The ASP.NET Core HTTPS development certificate is not trusted, so the browser will refuse the pages.' Yellow
        Say '  Trust it once with: dotnet dev-certs https --trust' Yellow
    }

    if (@(sqllocaldb info 2>$null) -notcontains $instance) {
        Say "  First start: creating the demo database ((localdb)\$instance, demo dataset, your login = $User) ..."
        Invoke-Provision
        Say ''
    }
    else {
        sqllocaldb start $instance *> $null
    }

    $toStart = @($startOrder | Where-Object { $svc = $_; ($states | Where-Object { $_.Service -eq $svc }).State -eq 'down' })
    if (-not $NoBuild) {
        $inUse = Get-RunningHosts
        if ($inUse.Count) {
            $ports = @(Get-NetTCPConnection -State Listen -OwningProcess $inUse.ProcessId -ErrorAction SilentlyContinue |
                    ForEach-Object { [int]$_.LocalPort } | Sort-Object -Unique)
            $others = @($ports | Where-Object { $_ -notin $services.Port })
            Say ("  Not rebuilding: STERP hosts of this folder still run on the binaries (ports {0})." -f $(if ($ports.Count) { $ports -join ', ' } else { 'starting' }))
            if ($others.Count) { Say ("  To rebuild, stop the hosts on ports {0} first." -f ($others -join ', ')) }
            else { Say '  "run-sterp.bat /restart" stops them and rebuilds.' }
        }
        else {
            Say '  Building STERP.Modernized.slnx (Release) ...'
            & dotnet build $solution -c Release --nologo -v q | Out-Host
            if ($LASTEXITCODE -ne 0) {
                Say '  BUILD FAILED - nothing was started.' Red
                return $false
            }
            Say '  Build succeeded.'
        }
        Say ''
    }
    foreach ($svc in $toStart) {
        $dll = Join-Path $root "src\Hosts\$($svc.Project)\bin\Release\net10.0\$($svc.Project).dll"
        if (-not (Test-Path $dll)) {
            Say "  $($svc.Project) is not built yet ($dll): start without /nobuild." Red
            return $false
        }
    }

    New-Item -ItemType Directory -Force $hostsDir | Out-Null
    $processes = @{}
    if ($toStart.Count) {
        Say '  Starting services ...'
        foreach ($svc in $toStart) {
            $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$launcher`"", '-Name', $svc.Key, '-Instance', $instance, '-PortBase', $PortBase)
            $processes[$svc.Key] = Start-Process $pwsh -ArgumentList $arguments -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (LogPath $svc '') -RedirectStandardError (LogPath $svc '.err')
        }
    }
    Say '  Waiting for the services to become ready ...'
    foreach ($st in $states | Where-Object { $_.State -eq 'up' }) { Say ("    [OK]   {0}  already running" -f (Row $st.Service)) Green }
    $waitFor = @($startOrder | Where-Object { $svc = $_; ($states | Where-Object { $_.Service -eq $svc }).State -in 'down', 'starting' })
    if (-not (Wait-Ready $waitFor $processes)) {
        Say ''
        Say "  Not every service came up; their logs are in $hostsDir." Red
        Say '  When the demo database is the problem, "run-sterp.bat /reset" restores it.' Red
        return $false
    }
    # the API Console keeps the host states for a few seconds: make it look again now that every host answers
    $null = Invoke-Probe "$(Url $svcPortal)/console/status.json?refresh=true" 15
    Say ''
    Say '  All services ready.' Green
    Show-Ready
    return $true
}

function Show-Banner {
    Say ''
    Say '  STERP - Supply Chain Management (modernized)'
    Say "  Root:      $root"
    Say "  Database:  STERP on (localdb)\$instance - demo dataset, your Windows login = $User"
    Say ("  Ports:     GUI {0}, Online API {1}, Batch jobs {2}, API Console {3}" -f $svcWeb.Port, $svcApi.Port, $svcJobs.Port, $svcPortal.Port)
    Say ''
}

function Show-Status {
    Say ''
    Say "  STERP demo services - database STERP on (localdb)\$instance"
    Say ''
    $states = @(Get-States)
    $allReady = $true
    foreach ($st in $states) {
        switch ($st.State) {
            'up' {
                $r = Get-Readiness $st.Service
                if (-not $r.Ready) { $allReady = $false }
                Say ("    {0}  UP        pid {1,-6}  {2}" -f (Row $st.Service), $st.ProcessId, $r.Text) $(if ($r.Ready) { 'Green' } else { 'Yellow' })
            }
            'starting' {
                $allReady = $false
                Say ("    {0}  STARTING" -f (Row $st.Service)) Yellow
            }
            'foreign' {
                $allReady = $false
                Say ("    {0}  IN USE    by {1} (pid {2}), not a STERP service of this folder" -f (Row $st.Service), $st.Owner, $st.ProcessId) Red
            }
            default {
                $allReady = $false
                Say ("    {0}  DOWN" -f (Row $st.Service))
            }
        }
    }
    Say ''
    if (@($states | Where-Object { $_.State -eq 'up' }).Count) {
        Say ("    GUI {0}    API Console {1}" -f (Url $svcWeb), (Url $svcPortal))
        Say "    Logs: $hostsDir"
        Say ''
    }
    return $allReady
}

function Show-Help {
    Say ''
    Say '  run-sterp.bat - start and stop the STERP demo services'
    Say ''
    Say '    run-sterp.bat              build, start all four services, wait until ready, open the GUI'
    Say '    run-sterp.bat /nobuild     start without rebuilding'
    Say '    run-sterp.bat /stop        stop the services'
    Say '    run-sterp.bat /restart     stop, then start again (rebuilds unless /nobuild)'
    Say '    run-sterp.bat /reset       stop, restore the demo data, then start again'
    Say '    run-sterp.bat /status      show which services are up and ready'
    Say '    run-sterp.bat /?           this help'
    Say ''
    Say '  Options'
    Say '    /nobrowser   do not open the GUI in the browser'
    Say '    /force       stop even while STERP.Jobs is executing batch runs (they are marked LOST)'
    Say ''
    Say "  Services - database STERP on (localdb)\$instance, demo dataset, your Windows login = $User"
    Say ("    {0}  {1}" -f (Row $svcWeb), (Url $svcWeb))
    Say ("    {0}  {1} - browse and try the Online API and Batch jobs" -f (Row $svcPortal), (Url $svcPortal))
    foreach ($svc in $svcApi, $svcJobs) { Say ("    {0}  web service without pages ({1}/health/ready)" -f (Row $svc), (Url $svc)) }
    Say ''
    Say '  The services run in the background without windows. Their logs are in'
    Say "    $hostsDir"
    Say '  scripts\run-sterp.ps1 does the work; it also takes -Name and -PortBase for a second demo environment.'
    Say ''
}

# double-clicked: Explorer started cmd.exe for run-sterp.bat, which started this script
function Test-FromExplorer {
    try {
        $self = Get-CimInstance Win32_Process -Filter "ProcessId = $PID" -Property ParentProcessId
        $cmd = Get-CimInstance Win32_Process -Filter "ProcessId = $($self.ParentProcessId)" -Property Name, ParentProcessId
        if (-not $cmd -or $cmd.Name -ne 'cmd.exe') { return $false }
        $shell = Get-Process -Id $cmd.ParentProcessId -ErrorAction SilentlyContinue
        return [bool]($shell -and $shell.ProcessName -eq 'explorer')
    }
    catch { return $false }
}

# a double-clicked window would close before anyone could read it
function Wait-BeforeClose([bool]$Success) {
    if (-not (Test-FromExplorer)) { return }
    try {
        if ($Success -and $Action -in 'Start', 'Restart', 'Reset') {
            Say '  This window closes in 30 seconds (press any key to close it now); the services keep running.'
            $until = (Get-Date).AddSeconds(30)
            while ((Get-Date) -lt $until -and -not [Console]::KeyAvailable) { Start-Sleep -Milliseconds 200 }
        }
        else {
            Say '  Press any key to close this window.'
            [void][Console]::ReadKey($true)
        }
    }
    catch { }
}

$success = $false
try {
    switch ($Action) {
        'Help' { Show-Help; $success = $true }
        'Status' { $success = Show-Status }
        'Stop' { Show-Banner; $success = Invoke-Stop }
        'Start' { Show-Banner; $success = Invoke-Start }
        'Restart' {
            Show-Banner
            if (Invoke-Stop) {
                Say ''
                $success = Invoke-Start
            }
        }
        'Reset' {
            Show-Banner
            if (Invoke-Stop) {
                Say ''
                Say "  Restoring the demo data: (localdb)\$instance back to the demo dataset, your Windows login = $User ..."
                Invoke-Provision
                Say ''
                $success = Invoke-Start
            }
        }
    }
}
catch {
    Say ''
    Say "  $($_.Exception.Message)" Red
    $success = $false
}
Say ''
Wait-BeforeClose $success
exit $(if ($success) { 0 } else { 1 })
