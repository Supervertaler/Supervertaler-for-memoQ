# Do the selected termbases actually reach terminology lookup?
#
# This is the join the whole feature rests on: a tick in the editor's Termbases
# dialog has to become a highlighted term in memoQ's grid. Until this existed,
# TermIndex read the glossary text file and nothing else, so a full set of
# ticked termbases produced exactly no hits - and looked, from the outside,
# indistinguishable from a broken lookup.
#
# No termbase is named here. The test picks one from whatever the database
# holds, so it carries no client terminology into a public repository.
#
# Run through tools/run-harness.ps1, never directly - it writes the real
# selection file and the wrapper snapshots it.
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
$NPS = [Reflection.BindingFlags]'NonPublic,Static'
$PS  = [Reflection.BindingFlags]'Public,Static'

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("PASS {0}" -f $name) }
    else { $script:fail++; Write-Host ("FAIL {0}  {1}" -f $name, $detail) -ForegroundColor Red }
}

$db    = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseDb')
$sel   = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseSelection')
$index = $plugin.GetType('Supervertaler.MemoQ.Core.TermIndex')
$flagsType = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseSelection+Flags')

foreach ($t in @($db, $sel, $index, $flagsType)) { if ($null -eq $t) { throw 'a required type was not found' } }
foreach ($t in @($db, $sel, $index)) {
    $t.GetField('ErrorSink', $NPS).SetValue($null, [Action[string, Exception]]{
        param($m, $ex) Write-Host ("   sink: {0} {1}" -f $m, $(if ($ex) { $ex.Message })) -ForegroundColor DarkGray
    })
}

if (-not $db.GetProperty('Exists', $NPS).GetValue($null)) { Write-Host 'No database.'; exit 1 }

# TermIndex re-checks its sources on a three-second throttle, which is right in
# memoQ and wrong in a test: without resetting it, the second phase would be
# answered from the first phase's index and pass for the wrong reason.
$lastCheck = $index.GetField('_lastCheck', $NPS)
function Unthrottle { $lastCheck.SetValue($null, [DateTime]::MinValue) }

function Save($project, $ids, $flags) {
    $idList = New-Object 'System.Collections.Generic.List[long]'
    foreach ($i in $ids) { $idList.Add([long]$i) }
    # Built inline, not in a helper: returning a List from a PowerShell function
    # ENUMERATES it, so the caller gets its elements rather than the list.
    $flagList = [Activator]::CreateInstance([System.Collections.Generic.List`1].MakeGenericType($flagsType))
    foreach ($f in $flags) { $flagList.Add($f) }
    $a = [object[]]::new(3)
    $a[0] = $project; $a[1] = $idList.PSObject.BaseObject; $a[2] = $flagList.PSObject.BaseObject
    $sel.GetMethod('Save', $NPS).Invoke($null, $a)
}
function Find($project, $text) {
    $m = $index.GetMethods($PS) | Where-Object { $_.Name -eq 'Find' -and $_.GetParameters().Count -eq 3 }
    $a = [object[]]::new(3); $a[0] = $null; $a[1] = $project; $a[2] = $text
    return $m.Invoke($null, $a)
}

$project = [Guid]'33333333-3333-3333-3333-333333333333'

# -- pick a real term out of a real termbase --------------------------------
$all = $db.GetMethod('All', $NPS).Invoke($null, @())
$candidate = $all | Where-Object { $_.Terms -gt 20 -and $_.Terms -lt 4000 } | Select-Object -First 1
if ($null -eq $candidate) { $candidate = $all | Sort-Object Terms -Descending | Select-Object -First 1 }

$ids = New-Object 'System.Collections.Generic.List[long]'
$ids.Add([long]$candidate.Id)
$a = [object[]]::new(1); $a[0] = $ids.PSObject.BaseObject
$terms = $db.GetMethod('TermsIn', $NPS).Invoke($null, $a)

# A single word, long enough not to collide with ordinary prose.
$term = $terms | Where-Object { $_.Source -notmatch '\s' -and $_.Source.Length -ge 6 } | Select-Object -First 1
if ($null -eq $term) { $term = $terms | Select-Object -First 1 }
Write-Host ("using a termbase of {0} terms; probe term is {1} characters" -f $candidate.Terms, $term.Source.Length)

Check 'terms carry the termbase they came from' ($term.TermbaseId -eq $candidate.Id) ("$($term.TermbaseId) vs $($candidate.Id)")
Check 'terms carry the termbase name for the pane' ($term.Origin -eq $candidate.Name)

$sentence = "Het " + $term.Source + " werd onderzocht."

# -- nothing selected: no hits, and no glossary either ------------------------
Save $project @() @()
Unthrottle
$none = Find $project $sentence
Check 'with nothing selected there are no hits' ($none.Count -eq 0) ("got $($none.Count)")

# -- select it: the term is found -------------------------------------------
$f = [Activator]::CreateInstance($flagsType)
$f.Id = [long]$candidate.Id; $f.Rank = 1; $f.CaseSensitive = $false; $f.Ai = $true; $f.Name = $candidate.Name
Save $project @($candidate.Id) @($f)
Unthrottle

$hits = Find $project $sentence
Check "the selected termbase answers ($($hits.Count) hit(s))" ($hits.Count -ge 1)

if ($hits.Count -ge 1) {
    $hit = $hits[0]
    Check 'the hit is the term we planted' ($hit.Entry.Source -eq $term.Source) ("$($hit.Entry.Source)")
    Check 'the rank travels with it, which is what colours it' ($hit.Entry.Rank -eq 1) ("rank=$($hit.Entry.Rank)")
    Check 'the origin travels with it, for the pane' ($hit.Entry.Origin -eq $candidate.Name)
    $planted = $sentence.IndexOf($term.Source)
    Check 'the offset points at the term in the segment' ($hit.Start -eq $planted) ("start=$($hit.Start) expected=$planted")
    Check 'the length covers the term' ($hit.Length -eq $term.Source.Length)
}

# -- a changed selection is picked up without a restart ----------------------
Save $project @() @()
Unthrottle
$after = Find $project $sentence
Check 'unticking it takes the terms away again' ($after.Count -eq 0) ("got $($after.Count)")

# -- one project's selection is not another's --------------------------------
$other = [Guid]'44444444-4444-4444-4444-444444444444'
Save $project @($candidate.Id) @($f)
Unthrottle
$mine = Find $project $sentence
Unthrottle
$theirs = Find $other $sentence
Check 'the selection is per project on the lookup path too' ($mine.Count -ge 1 -and $theirs.Count -eq 0) ("mine=$($mine.Count) theirs=$($theirs.Count)")

# -- scale: how long does a lookup take with a big selection? ----------------
$big = $all | Sort-Object Terms -Descending | Select-Object -First 8
$bigIds = @(); $bigFlags = @()
$rank = 1
foreach ($tb in $big) {
    $bigIds += $tb.Id
    $bf = [Activator]::CreateInstance($flagsType)
    $bf.Id = [long]$tb.Id; $bf.Rank = $rank; $bf.Name = $tb.Name
    $bigFlags += $bf
    $rank++
}
Save $project $bigIds $bigFlags
Unthrottle

$sw = [Diagnostics.Stopwatch]::StartNew()
$null = Find $project $sentence
$sw.Stop()
$loadMs = $sw.ElapsedMilliseconds
$total = ($big | Measure-Object -Property Terms -Sum).Sum
Write-Host ("   first lookup after selecting the 8 biggest termbases ({0:N0} terms): {1} ms" -f $total, $loadMs)

# The load happens once; every later segment must not pay for it again.
$sw = [Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt 200; $i++) { $null = Find $project $sentence }
$sw.Stop()
$per = $sw.Elapsed.TotalMilliseconds / 200
Write-Host ("   200 further lookups: {0:N2} ms each" -f $per)
Check 'a warm lookup is well under a millisecond' ($per -lt 1.0) ("$per ms")

Write-Host ''
Write-Host ("TERMINDEX/TERMBASE TEST COMPLETE - {0} passed, {1} failed" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
