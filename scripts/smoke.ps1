<#
.SYNOPSIS
  Deployment smoke check of the four STERP hosts (contracts section 10): health, OpenAPI documents, GUI and portal.

.DESCRIPTION
  Checks Api (PortBase+1) and Jobs (PortBase+2) /health/live and /health/ready (database probe, schema drift, output
  folders, delivered slices), the OpenAPI index, the Web shell (PortBase) with its /_sterp/config, and the portal
  (PortBase+3), all with the caller's Windows credentials. With -Authenticated it also calls GET /api/platform/v1/session
  (200, or 403 API0010 when the login is not mapped to a STERP user - both prove the Negotiate pipeline). Read-only:
  nothing is written. Exit code 0 when every check passes, 1 otherwise.

.EXAMPLE
  ./scripts/smoke.ps1
  ./scripts/smoke.ps1 -PortBase 7140 -Authenticated
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [int]$PortBase = 7100,
    [switch]$Authenticated
)
$ErrorActionPreference = 'Stop'
$results = [System.Collections.Generic.List[object]]::new()

function Check([string]$name, [string]$url, [scriptblock]$ok) {
    try {
        # the Web shell and the portal require Windows authentication like the API (browsers negotiate it automatically)
        $r = Invoke-WebRequest $url -SkipCertificateCheck -TimeoutSec 30 -SkipHttpErrorCheck -UseDefaultCredentials
        $pass = & $ok $r
        $results.Add([pscustomobject]@{ Check = $name; Status = $r.StatusCode; Pass = [bool]$pass; Url = $url })
    }
    catch {
        $results.Add([pscustomobject]@{ Check = $name; Status = 'error'; Pass = $false; Url = "$url ($($_.Exception.Message))" })
    }
}

$api = "https://localhost:$($PortBase + 1)"
$jobs = "https://localhost:$($PortBase + 2)"
foreach ($h in @(@{ Name = 'Api'; Url = $api }, @{ Name = 'Jobs'; Url = $jobs })) {
    Check "$($h.Name) live" "$($h.Url)/health/live" { param($r) $r.StatusCode -eq 200 }
    Check "$($h.Name) ready" "$($h.Url)/health/ready" { param($r) $r.StatusCode -eq 200 -and ($r.Content | ConvertFrom-Json).status -eq 'Healthy' }
    Check "$($h.Name) OpenAPI index" "$($h.Url)/openapi/index.json" { param($r) $r.StatusCode -eq 200 }
}
Check 'Web shell' "https://localhost:$PortBase/" { param($r) $r.StatusCode -eq 200 }
Check 'Web config' "https://localhost:$PortBase/_sterp/config" { param($r) $r.StatusCode -eq 200 }
Check 'Portal' "https://localhost:$($PortBase + 3)/" { param($r) $r.StatusCode -eq 200 }
if ($Authenticated) {
    Check 'Api session (Windows auth)' "$api/api/platform/v1/session" {
        param($r) $r.StatusCode -eq 200 -or ($r.StatusCode -eq 403 -and $r.Content -match 'API0010')
    }
}

$results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
if ($results.Where({ -not $_.Pass }).Count -gt 0) { exit 1 }
exit 0
