# TermbaseDb against the real database.
#
# The spike (termbase-spike.ps1) proved memoQ's SQLite can open the file. This
# proves our reader does, through the plugin assembly memoQ actually loads, with
# the reference resolved the way it will be at runtime - Private=false, native
# half found on PATH.
#
# Run through tools/run-harness.ps1, never directly.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$PluginDll = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll'

$script:probed = @{}
[AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler] {
    param($s, $e)
    $name = ($e.Name -split ',')[0]
    if ($script:probed.ContainsKey($name)) { return $null }
    $script:probed[$name] = $true
    foreach ($dir in @($MemoQPath, "$MemoQPath\Addins")) {
        $candidate = Join-Path $dir "$name.dll"
        if (Test-Path $candidate) { try { return [Reflection.Assembly]::LoadFrom($candidate) } catch { return $null } }
    }
    return $null
})

$plugin = [Reflection.Assembly]::LoadFrom($PluginDll)
$NonPublicStatic = [Reflection.BindingFlags]'NonPublic,Static'

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("PASS {0}" -f $name) }
    else { $script:fail++; Write-Host ("FAIL {0}  {1}" -f $name, $detail) -ForegroundColor Red }
}

$db = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseDb')
if ($null -eq $db) { throw 'TermbaseDb not found in the assembly.' }

# Errors must not be swallowed while testing: point the sink at the console, or a
# broken query looks exactly like an empty database.
$sink = $db.GetField('ErrorSink', $NonPublicStatic)
$sink.SetValue($null, [Action[string, Exception]]{
    param($m, $ex) Write-Host ("   sink: {0} {1}" -f $m, $(if ($ex) { $ex.Message })) -ForegroundColor Yellow
})

$path = $db.GetProperty('Path', $NonPublicStatic).GetValue($null)
$exists = $db.GetProperty('Exists', $NonPublicStatic).GetValue($null)
Write-Host "database: $path"
Check 'the shared database is where we expect it' $exists $path
if (-not $exists) { Write-Host 'Nothing more to test without it.'; exit 1 }

$sw = [Diagnostics.Stopwatch]::StartNew()
$all = $db.GetMethod('All', $NonPublicStatic).Invoke($null, @())
$sw.Stop()
Check "every termbase lists ($($all.Count) in $($sw.ElapsedMilliseconds) ms)" ($all.Count -gt 0)

$withTerms = @($all | Where-Object { $_.Terms -gt 0 })
Check 'term counts come back' ($withTerms.Count -gt 0)

$biggest = $all | Sort-Object -Property Terms -Descending | Select-Object -First 1
Write-Host ("   biggest: {0} ({1} terms, {2}->{3})" -f $biggest.Name, $biggest.Terms, $biggest.SourceLang, $biggest.TargetLang)
Check 'names are not blank' (-not [string]::IsNullOrWhiteSpace($biggest.Name))
Check 'a language pair comes back' (-not [string]::IsNullOrWhiteSpace($biggest.SourceLang))

# The load the plugin would do for a selection.
$termsIn = $db.GetMethod('TermsIn', $NonPublicStatic)
# .PSObject.BaseObject, not the variable: New-Object hands back a PSObject
# wrapper, and Invoke refuses to convert that to IEnumerable<long>. The same
# trap as passing a List<T> straight into a reflected call.
$ids = New-Object 'System.Collections.Generic.List[long]'
$ids.Add([long]$biggest.Id)
$a = [object[]]::new(1); $a[0] = $ids.PSObject.BaseObject

$sw = [Diagnostics.Stopwatch]::StartNew()
$entries = $termsIn.Invoke($null, $a)
$sw.Stop()
Check "the biggest termbase loads ($($entries.Count) entries in $($sw.ElapsedMilliseconds) ms)" ($entries.Count -gt 0)
Check 'entries have a source' (-not [string]::IsNullOrWhiteSpace($entries[0].Source))

# Forbidden has to survive the trip: it is the flag that changes what the model
# is told, so a silent loss here is a wrong instruction rather than a missing one.
$forbidden = @($entries | Where-Object { $_.Forbidden }).Count
Write-Host ("   forbidden entries in that termbase: {0}" -f $forbidden)

# An empty selection must not become "everything".
$empty = New-Object 'System.Collections.Generic.List[long]'
$a2 = [object[]]::new(1); $a2[0] = $empty.PSObject.BaseObject
$none = $termsIn.Invoke($null, $a2)
Check 'no termbases selected means no terms' ($none.Count -eq 0)

Write-Host ''
Write-Host ("TERMBASE READER TEST COMPLETE - {0} passed, {1} failed" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
