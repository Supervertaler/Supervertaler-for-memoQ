# The usage ping and the trial registration (Core/Stats.cs), and the consent
# the editor records (SharedSettings.SetUsageStats).
#
# Pure checks on what would be sent - nothing is sent: OnStart does nothing under
# a harness, and the payload builders are called directly. Run through
# tools/run-harness.ps1, which restores shared.txt afterwards.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$script:probed = @{}
[AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler] {
    param($s, $e)
    $name = ($e.Name -split ',')[0]
    if ($script:probed.ContainsKey($name)) { return $null }
    $script:probed[$name] = $true
    foreach ($dir in @($MemoQPath, "$MemoQPath\Addins")) {
        $c = Join-Path $dir "$name.dll"
        if (Test-Path $c) { try { return [Reflection.Assembly]::LoadFrom($c) } catch { return $null } }
    }
    return $null
})
$plugin = [Reflection.Assembly]::LoadFrom('D:\SynologyDrive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll')
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'
$fails = 0
function Check($ok, $label) { if (-not $ok) { $script:fails++ }; Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label" }

$stats  = $plugin.GetType('Supervertaler.MemoQ.Core.Stats')
$shared = $plugin.GetType('Supervertaler.MemoQ.Core.SharedSettings')
$state  = $plugin.GetType('Supervertaler.Core.LicenceState')
function Call($type, $name, [object[]]$argv) {
    $m = $type.GetMethods($Static) | Where-Object { $_.Name -eq $name } | Select-Object -First 1
    return $m.Invoke($null, $argv)
}
function Keys($json) { return @(($json | ConvertFrom-Json).PSObject.Properties.Name) }

# ---- 1. the usage ping carries exactly what the privacy policy lists ---------
$a = New-Object object[] 5
$a[0] = [string]'11111111-2222-3333-4444-555555555555'; $a[1] = [string]'0.1.1'; $a[2] = [string]'12.4.53'
$a[3] = [string]'Microsoft Windows NT 10.0.26200.0'; $a[4] = [string]'en-GB'
$ping = [string](Call $stats 'PingJson' $a)
$p = $ping | ConvertFrom-Json
Check ($p.product -eq 'memoq') "the ping is filed under memoq: $($p.product)"
Check ($p.trados_version -eq '12.4.53') 'the memoQ version goes in the host-version field the dashboard labels by product'
Check ($p.plugin_version -eq '0.1.1' -and $p.locale -eq 'en-GB' -and $p.id.Length -eq 36) 'id, plugin version and locale are there'
$allowed = @('id', 'product', 'plugin_version', 'trados_version', 'os_version', 'locale')
$extra = @(Keys $ping | Where-Object { $allowed -notcontains $_ })
Check ($extra.Count -eq 0) "nothing beyond the privacy policy's list (extra: $($extra -join ', '))"

# ---- 2. the trial registration --------------------------------------------------
$start = [DateTime]::SpecifyKind([DateTime]'2026-09-20T08:00:00', 'Utc')
$status = [string](Call $stats 'TrialStatus' @($start, [Enum]::Parse($state, 'Trial')))
Check ($status -eq 'trial') "a running trial reports trial: $status"
$status = [string](Call $stats 'TrialStatus' @($start, [Enum]::Parse($state, 'Expired')))
Check ($status -eq 'expired') "a lapsed trial reports expired: $status"
$status = [string](Call $stats 'TrialStatus' @([DateTime]::MinValue, [Enum]::Parse($state, 'Trial')))
Check ($status -eq 'new') "no start yet reports new: $status"

$b = New-Object object[] 6
$b[0] = [string]('ab' * 32); $b[1] = [string]'0.1.1'; $b[2] = [string]'12.4.53'; $b[3] = [string]'nl-NL'; $b[4] = $start; $b[5] = [string]'trial'
$t = ([string](Call $stats 'TrialJson' $b)) | ConvertFrom-Json
Check ($t.product -eq 'memoq') 'the trial is filed under memoq, so the server records which plugins a computer used'
Check ($t.studio_version -eq 'memoQ 12.4.53') "the host is named: $($t.studio_version)"
Check ($t.claimed_start -like '2026-09-20T08:00:00*') "the local start is sent in UTC: $($t.claimed_start)"
$b[4] = [DateTime]::MinValue; $b[5] = [string]'new'
$tNew = [string](Call $stats 'TrialJson' $b)
Check ((Keys $tNew) -notcontains 'claimed_start') 'no start is sent as absent, not as year 1'

$v = [string](Call $stats 'PluginVersion' @())
Check ($v -match '^\d+\.\d+\.\d+$') "the plugin version has three parts: $v"

# ---- 3. consent: one id per install, minted by the editor ----------------------
Call $shared 'SetUsageStats' @($true) | Out-Null
$id1 = [string]$shared.GetProperty('UsageStatsId', $Static).GetValue($null)
Check ([bool]$shared.GetProperty('UsageStats', $Static).GetValue($null)) 'yes turns the ping on'
Check ($id1.Length -eq 36) "yes mints an id: $id1"
Check ([bool]$shared.GetProperty('UsageStatsAsked', $Static).GetValue($null)) 'an answer means the question is not asked again'

Call $shared 'SetUsageStats' @($false) | Out-Null
Check (-not [bool]$shared.GetProperty('UsageStats', $Static).GetValue($null)) 'no turns it off'
Call $shared 'SetUsageStats' @($true) | Out-Null
$id2 = [string]$shared.GetProperty('UsageStatsId', $Static).GetValue($null)
Check ($id2 -eq $id1) 'off and on again keeps the same id, so one install stays one user on the dashboard'

# ---- 4. never under a harness ---------------------------------------------------
$sw = [Diagnostics.Stopwatch]::StartNew()
Call $stats 'OnStart' @([Action[string]]{ param($m) Write-Host "  log: $m" }) | Out-Null
Check ($sw.ElapsedMilliseconds -lt 1000) 'OnStart returns at once and sends nothing under a harness'

Write-Host ""
if ($fails -eq 0) { Write-Host "All passed." } else { Write-Host "$fails FAILED"; exit 1 }
