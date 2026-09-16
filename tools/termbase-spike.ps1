# Can we read the shared Supervertaler database using memoQ's own SQLite?
#
# This is the question the whole termbase feature rests on. The rule in this
# repo is ship nothing: add-ins are probed out of memoQ's install directory, so
# a SQLite library of ours in the Addins folder would compete with memoQ's copy
# of the same library - which is recorded in the Trados repo as the cause of an
# EntryPointNotFoundException. memoQ already ships BOTH providers:
#
#   System.Data.SQLite.dll      + SQLite.Interop.dll   (the classic provider)
#   Microsoft.Data.Sqlite.dll   + SQLitePCLRaw.* + e_sqlite3.dll
#
# PowerShell is .NET Framework, the same runtime the plugin and the editor run
# on, so if a query works here it works there.
#
#   powershell -NoProfile -File tools\termbase-spike.ps1
$ErrorActionPreference = 'Stop'

$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$Db = 'D:\Supervertaler\resources\supervertaler.db'

if (-not (Test-Path $Db)) { throw "No database at $Db" }

Write-Host "memoQ:    $MemoQPath"
Write-Host "database: $Db  ($([math]::Round((Get-Item $Db).Length / 1MB, 1)) MB)"
Write-Host ""

# The native half has to be findable before the managed half asks for it.
# System.Data.SQLite looks for SQLite.Interop.dll beside itself or on PATH.
$env:PATH = "$MemoQPath;$env:PATH"

$asm = Join-Path $MemoQPath 'System.Data.SQLite.dll'
Write-Host "loading $asm"
$sqlite = [Reflection.Assembly]::LoadFrom($asm)
Write-Host ("  version {0}" -f $sqlite.GetName().Version)

# Read-only, and with no attempt to take the write-ahead log: another process
# (Trados, Workbench) may have this open, and this spike must not disturb it.
$csb = "Data Source=$Db;Version=3;Read Only=True;"
$con = $sqlite.CreateInstance('System.Data.SQLite.SQLiteConnection')
$con.ConnectionString = $csb
$con.Open()
Write-Host "  opened: $($con.State)"

function Scalar($sql) {
    $cmd = $con.CreateCommand()
    $cmd.CommandText = $sql
    return $cmd.ExecuteScalar()
}

Write-Host ""
Write-Host "termbases:       $(Scalar 'select count(*) from termbases')"
Write-Host "termbase_terms:  $(Scalar 'select count(*) from termbase_terms')"
Write-Host "synonyms:        $(Scalar 'select count(*) from termbase_synonyms')"

# The shape the editor's table needs: one row per termbase with its term count.
Write-Host ""
Write-Host "top termbases by size (the shape the editor's table wants):"
$cmd = $con.CreateCommand()
$cmd.CommandText = @'
select t.id, t.name, t.source_lang, t.target_lang, count(tt.id) as terms
from termbases t
left join termbase_terms tt on tt.termbase_id = t.id
group by t.id
order by terms desc
limit 6
'@
$r = $cmd.ExecuteReader()
while ($r.Read()) {
    Write-Host ("   {0,4}  {1,-52} {2}->{3}  {4,6}" -f $r['id'],
        ($r['name'].ToString().Substring(0, [Math]::Min(52, $r['name'].ToString().Length))),
        $r['source_lang'], $r['target_lang'], $r['terms'])
}
$r.Close()

# And the lookup the TB plugin would do: is FTS usable, and how fast?
Write-Host ""
$sw = [Diagnostics.Stopwatch]::StartNew()
$cmd = $con.CreateCommand()
$cmd.CommandText = "select count(*) from termbase_terms where termbase_id = 13"
$n = $cmd.ExecuteScalar()
$sw.Stop()
Write-Host ("one termbase's terms (id 13): {0} rows in {1} ms" -f $n, $sw.ElapsedMilliseconds)

$sw = [Diagnostics.Stopwatch]::StartNew()
$cmd = $con.CreateCommand()
$cmd.CommandText = "select source_term, target_term, forbidden from termbase_terms where termbase_id = 13"
$r = $cmd.ExecuteReader()
$rows = 0
while ($r.Read()) { $rows++ }
$r.Close()
$sw.Stop()
Write-Host ("reading all {0} of them into memory: {1} ms" -f $rows, $sw.ElapsedMilliseconds)

$con.Close()
Write-Host ""
Write-Host "SPIKE COMPLETE - memoQ's own SQLite reads the shared database."
