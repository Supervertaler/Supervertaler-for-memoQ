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

# TermsIn has two overloads now - one that turns a reversed termbase round for
# the job, one that does not. Pick the plain one by its parameter count; by name
# alone reflection cannot tell them apart.
$TermsInMethod = $db.GetMethods([Reflection.BindingFlags]'NonPublic,Static') |
    Where-Object { $_.Name -eq 'TermsIn' -and $_.GetParameters().Count -eq 1 }
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
$termsIn = $db.GetMethods($NonPublicStatic) |
    Where-Object { $_.Name -eq 'TermsIn' -and $_.GetParameters().Count -eq 1 }
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

# -- the provider, and the reason we chose it -------------------------------
# TermbaseDb moved from System.Data.SQLite to Microsoft.Data.Sqlite for one
# reason: memoQ's build of the former has NO fts5 module, so it cannot read the
# six full-text indexes already in this file and could never create the schema
# for a translator who has no database yet. Assert both halves, because a
# revert to the old provider would otherwise pass every test above.
$loaded = [AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetName().Name }
Check 'the reader is on Microsoft.Data.Sqlite' ($loaded -contains 'Microsoft.Data.Sqlite') ("$($loaded -join ', ')")
Check 'System.Data.SQLite was not dragged in' (-not ($loaded -contains 'System.Data.SQLite'))

# fts5 through the very provider the plugin just used, not a fresh one.
$mds = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'Microsoft.Data.Sqlite' }
if ($mds) {
    $con = $mds.CreateInstance('Microsoft.Data.Sqlite.SqliteConnection')
    $con.ConnectionString = "Data Source=$path;Mode=ReadOnly"
    try {
        $con.Open()
        $c = $con.CreateCommand()
        $c.CommandText = 'select count(*) from termbase_terms_fts'
        $n = $c.ExecuteScalar()
        Check "fts5 is reachable ($n rows in the term index)" ($n -gt 0)

        $c = $con.CreateCommand()
        $c.CommandText = 'create virtual table temp.probe using fts5(x)'
        [void]$c.ExecuteNonQuery()
        Check 'fts5 tables can be CREATED, which is what a memoQ-only user needs' $true
    } catch {
        Check 'fts5 is reachable' $false $_.Exception.Message
    } finally {
        $con.Close()
    }
} else {
    Check 'fts5 is reachable' $false 'Microsoft.Data.Sqlite never loaded'
}

Write-Host ''
Write-Host ("TERMBASE READER TEST COMPLETE - {0} passed, {1} failed" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
