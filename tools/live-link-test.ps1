# The live document link is started once, by the plugin, and only when it can work.
#
# Pure checks and a harness no-op: nothing is started and no setting changes.
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
$plugin = [Reflection.Assembly]::LoadFrom('D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll')
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'
$fails = 0
function Check($ok, $label) { if (-not $ok) { $script:fails++ }; Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label" }

$link   = $plugin.GetType('Supervertaler.MemoQ.Core.LiveLink')
$shared = $plugin.GetType('Supervertaler.MemoQ.Core.SharedSettings')
$should = $link.GetMethod('ShouldStart', $Static)
function Should($before, $running, $tool, $prereq) {
    $a = New-Object object[] 4; $a[0] = [bool]$before; $a[1] = [bool]$running; $a[2] = [string]$tool; $a[3] = [bool]$prereq
    return [bool]$should.Invoke($null, $a)
}

Check (Should $false $false 'C:\x\tool.exe' $true) 'first time, not running, tool and PDF Preview present: start it'
Check (-not (Should $true  $false 'C:\x\tool.exe' $true)) 'started before: never again (memoQ starts it, or the translator declined)'
Check (-not (Should $false $true  'C:\x\tool.exe' $true)) 'already running: leave it'
Check (-not (Should $false $false $null            $true)) 'no tool installed: nothing to start'
Check (-not (Should $false $false 'C:\x\tool.exe' $false)) 'no PDF Preview: not started - it could only show an error, at every start'

$flagBefore = [bool]$shared.GetProperty('LiveLinkStarted', $Static).GetValue($null)
$countBefore = @(Get-Process -Name 'Supervertaler.MemoQ.Preview' -ErrorAction SilentlyContinue).Count
$link.GetMethod('StartOnce', $Static).Invoke($null, @([Action[string]]{ param($m) Write-Host "  log: $m" }))
Start-Sleep -Milliseconds 500
Check (@(Get-Process -Name 'Supervertaler.MemoQ.Preview' -ErrorAction SilentlyContinue).Count -eq $countBefore) 'under a harness nothing is started'
Check (([bool]$shared.GetProperty('LiveLinkStarted', $Static).GetValue($null)) -eq $flagBefore) 'and the setting is not touched'

Write-Host ''
if ($fails -gt 0) { Write-Host "$fails FAILED"; exit 1 } else { Write-Host 'All passed.' }
