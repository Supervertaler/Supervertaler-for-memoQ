# The write side of the shared termbase database, against a DISPOSABLE file.
#
# This product is a second writer to a schema Supervertaler for Trados and the
# Workbench own. What makes that honest rather than reckless is checked here:
# a database built from nothing has exactly the live database's schema, object
# by object; every insert also reaches the full-text index the Workbench
# searches; and a delete leaves nothing dangling. The live file is opened
# read-only, once, to compare against - and its termbase count is asserted
# unchanged at the end, because "the test wrote into the real database" is the
# one outcome worse than the test failing.
#
# Run through tools/run-harness.ps1: TermbaseSelection.Forget writes the real
# selection file, and the wrapper snapshots it.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$PluginDll = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll'
$LiveDb    = 'D:\Supervertaler\resources\supervertaler.db'
$Scratch   = Join-Path $env:TEMP 'supervertaler-writer-test'

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

# ---- types -----------------------------------------------------------------
$db      = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseDb')
$writer  = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseWriter')
$schema  = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseSchema')
$files   = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseFiles')
$sel     = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseSelection')
$rowType   = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseFiles+Row')
$shapeType = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseFiles+Shape')
$flagsType = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseSelection+Flags')
foreach ($t in @($db, $writer, $schema, $files, $sel, $rowType, $shapeType, $flagsType)) {
    if ($null -eq $t) { throw 'a required type was not found in the plugin assembly' }
}
foreach ($t in @($db, $writer, $sel)) {
    $t.GetField('ErrorSink', $NPS).SetValue($null, [Action[string, Exception]]{
        param($m, $ex) Write-Host ("   sink: {0} {1}" -f $m, $(if ($ex) { $ex.Message })) -ForegroundColor DarkGray
    })
}

# ---- the disposable database ------------------------------------------------
if (Test-Path $Scratch) { Remove-Item $Scratch -Recurse -Force }
New-Item -ItemType Directory -Path $Scratch | Out-Null
$TestDb = Join-Path $Scratch 'supervertaler.db'
$db.GetField('PathOverride', $NPS).SetValue($null, $TestDb)
Check 'the reader is pointed at the disposable file' (($db.GetProperty('Path', $NPS).GetValue($null)) -eq $TestDb)

# A direct SQLite handle of our own, to look at what the writer wrote.
$env:PATH = "$MemoQPath;$env:PATH"
foreach ($d in @('System.Memory','System.Runtime.CompilerServices.Unsafe','SQLitePCLRaw.core',
                 'SQLitePCLRaw.provider.dynamic_cdecl','SQLitePCLRaw.batteries_v2','Microsoft.Data.Sqlite')) {
    $null = [Reflection.Assembly]::LoadFrom((Join-Path $MemoQPath "$d.dll"))
}
[SQLitePCL.Batteries_V2]::Init()
function OpenDb([string]$path, [bool]$readOnly) {
    $mode = if ($readOnly) { 'ReadOnly' } else { 'ReadWrite' }
    $c = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$path;Mode=$mode")
    $c.Open()
    return $c
}
function Scalar($con, [string]$sql) { $c = $con.CreateCommand(); $c.CommandText = $sql; return $c.ExecuteScalar() }
function Rows($con, [string]$sql) {
    $c = $con.CreateCommand(); $c.CommandText = $sql
    $r = $c.ExecuteReader(); $out = @()
    while ($r.Read()) { $row = @{}; for ($i = 0; $i -lt $r.FieldCount; $i++) { $row[$r.GetName($i)] = $r.GetValue($i) }; $out += $row }
    $r.Close(); return ,$out
}

# ---- helpers over the writer -------------------------------------------------
function NewRow([string]$s, [string]$t, [bool]$forbidden = $false, [string]$notes = $null) {
    $r = [Activator]::CreateInstance($rowType)
    $r.Source = $s; $r.Target = $t; $r.Forbidden = $forbidden; $r.Notes = $notes
    return $r
}
function RowList($rows) {
    $list = [Activator]::CreateInstance([System.Collections.Generic.List`1].MakeGenericType($rowType))
    foreach ($r in $rows) { $list.Add($r) }
    return ,$list
}
function Create([string]$name, [string]$src, [string]$tgt) {
    $a = [object[]]::new(4); $a[0] = $name; $a[1] = $src; $a[2] = $tgt; $a[3] = ''
    return [long]$writer.GetMethod('Create', $NPS).Invoke($null, $a)
}
function Import([long]$id, $rows, [string]$src, [string]$tgt) {
    $list = RowList $rows
    $a = [object[]]::new(4); $a[0] = $id; $a[1] = $list.PSObject.BaseObject; $a[2] = $src; $a[3] = $tgt
    return $writer.GetMethod('Import', $NPS).Invoke($null, $a)
}
function Delete([long]$id) {
    $a = [object[]]::new(1); $a[0] = $id
    $writer.GetMethod('Delete', $NPS).Invoke($null, $a)
}
function RowsOf([long]$id) {
    $a = [object[]]::new(1); $a[0] = $id
    return $db.GetMethod('RowsOf', $NPS).Invoke($null, $a)
}
function WriteFile([string]$path, [string]$shape, [string]$name, [string]$src, [string]$tgt, $rows) {
    $list = RowList $rows
    $a = [object[]]::new(6)
    $a[0] = $path; $a[1] = [Enum]::Parse($shapeType, $shape); $a[2] = $name; $a[3] = $src; $a[4] = $tgt; $a[5] = $list.PSObject.BaseObject
    $files.GetMethod('Write', $NPS).Invoke($null, $a)
}
function ReadFile([string]$path) {
    $a = [object[]]::new(1); $a[0] = $path
    return $files.GetMethod('Read', $NPS).Invoke($null, $a)
}
function Comparable([string]$sql) {
    $a = [object[]]::new(1); $a[0] = $sql
    return $schema.GetMethod('Comparable', $NPS).Invoke($null, $a)
}

# =============================================================================
# 1. A database from nothing has the live database's schema
# =============================================================================
$live = OpenDb $LiveDb $true
$liveTermbasesBefore = Scalar $live 'select count(*) from termbases'
$liveTermsBefore     = Scalar $live 'select count(*) from termbase_terms'

$id1 = Create 'Writer test A' 'dut-NL' 'eng-GB'
Check 'a database was created where none was' (Test-Path $TestDb)
Check 'and the first termbase has id 1' ($id1 -eq 1) "id=$id1"

$test = OpenDb $TestDb $true
Check 'it is in WAL mode, as the live file is' ((Scalar $test 'pragma journal_mode') -eq 'wal')
Check 'user_version stays 0, per the additive-only agreement' ((Scalar $test 'pragma user_version') -eq 0)

function WithoutTmKey([string]$sql) {
    $a = [object[]]::new(1); $a[0] = $sql
    return $schema.GetMethod('WithoutForeignTmKey', $NPS).Invoke($null, $a)
}

$objects = $schema.GetField('Objects', $NPS).GetValue($null)
$drift = @()
foreach ($item in $objects) {
    $liveSql = Scalar $live ("select sql from sqlite_master where name = '{0}'" -f $item.Name)
    $testSql = Scalar $test ("select sql from sqlite_master where name = '{0}'" -f $item.Name)
    if ($null -eq $liveSql) { $drift += "$($item.Name): not in the LIVE database"; continue }
    if ($null -eq $testSql) { $drift += "$($item.Name): not created"; continue }
    # One deliberate deviation, applied to the LIVE side: the Workbench's DDL
    # gives termbase_terms a foreign key to translation_units, a table this
    # product does not create. A created file must not carry a key to nothing
    # (see TermbaseSchema), so the live text is compared without that clause.
    $liveCmp = Comparable (WithoutTmKey $liveSql)
    if ($liveCmp -ne (Comparable $testSql)) { $drift += "$($item.Name): differs" }
    if ($liveCmp -ne (Comparable $item.Sql)) { $drift += "$($item.Name): TermbaseSchema text differs from live" }
}
Check ("every one of the {0} schema objects matches the live database" -f $objects.Count) ($drift.Count -eq 0) ($drift -join '; ')
Check 'the live file does carry the TM foreign key this product leaves out - the deviation is real, not stale' `
      ((Scalar $live "select sql from sqlite_master where name = 'termbase_terms'") -match 'REFERENCES\s+translation_units')
Check 'and a created file does not carry it' `
      (-not ((Scalar $test "select sql from sqlite_master where name = 'termbase_terms'") -match 'translation_units'))
Check 'no activation tables are created - those are a host''s business' `
      (((Scalar $test "select count(*) from sqlite_master where name like 'termbase%activation'") -eq 0))

# =============================================================================
# 2. Create: the same row Trados would write
# =============================================================================
$tb = (Rows $test 'select * from termbases where id = 1')[0]
Check 'name stored'                     ($tb['name'] -eq 'Writer test A')
Check 'languages stored in the shared form: dut-NL becomes nl-NL' ($tb['source_lang'] -eq 'nl-NL' -and $tb['target_lang'] -eq 'en-GB') ("$($tb['source_lang'])->$($tb['target_lang'])")
Check 'is_global 1, as Trados writes'   ($tb['is_global'] -eq 1)
Check 'read_only 0 (writable), as Trados writes' ($tb['read_only'] -eq 0)
Check 'project_id is an empty string, not NULL, as Trados writes' ($tb['project_id'] -is [string] -and $tb['project_id'] -eq '')
Check 'ranking is 1 in an empty database' ($tb['ranking'] -eq 1)
Check 'created_date is the CURRENT_TIMESTAMP form' ("$($tb['created_date'])" -match '^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$') "$($tb['created_date'])"

$id2 = Create 'Writer test B' 'en' 'nl'
Check 'a second termbase ranks after the first' ((Scalar $test 'select ranking from termbases where id = 2') -eq 2)

$refused = ''
try { $null = Create 'Writer test A' 'nl' 'en' }
catch {
    # Reflection wraps the throw in TargetInvocationException; the message a
    # user would see is one level in, not the innermost - that is SQLite's own
    # "UNIQUE constraint failed", which is what the writer exists to translate.
    $e = $_.Exception
    if ($e -is [Reflection.TargetInvocationException] -and $e.InnerException) { $e = $e.InnerException }
    $refused = $e.Message
}
Check 'a duplicate name is refused with a readable message' ($refused -like '*already exists*') $refused

# =============================================================================
# 3. Import: rows, duplicates, the index
# =============================================================================
$rows = @(
    (NewRow 'adsorbens' 'adsorbent'),
    (NewRow 'werkwijze' 'method' $false 'the usual patent sense'),
    (NewRow 'toestel' 'device' $true),
    (NewRow 'Adsorbens' 'adsorbent'),        # same pair, different case
    (NewRow 'method' 'werkwijze')            # same pair, other way round
)
$r = Import $id1 $rows 'dut-NL' 'eng-GB'
Check "three added, two duplicates ($($r.Added)/$($r.Duplicates))" ($r.Added -eq 3 -and $r.Duplicates -eq 2)
Check 'not reversed: file and termbase run the same way' (-not $r.Reversed)

# NEVER assert the index by count(*). On an external-content FTS5 table a query
# without MATCH is answered from the CONTENT table, so count(*) equals the term
# count with a completely empty index - measured 2026-09-17: content-only
# insert gave count 1, match 0. Only MATCH reads the index. The 36,106 = 36,106
# that looked like a healthy live index the day before was exactly this.
Check 'the index finds each term that was just added'  ((Scalar $test "select count(*) from termbase_terms_fts where termbase_terms_fts match 'werkwijze'") -eq 1)
Check 'and the forbidden one'                          ((Scalar $test "select count(*) from termbase_terms_fts where termbase_terms_fts match 'toestel'") -eq 1)
Check 'and does not find one that was never added'     ((Scalar $test "select count(*) from termbase_terms_fts where termbase_terms_fts match 'nonesuch'") -eq 0)

$t = (Rows $test "select * from termbase_terms where source_term = 'toestel'")[0]
Check 'forbidden survives'               ($t['forbidden'] -eq 1)
Check 'term rows carry the termbase''s languages' ($t['source_lang'] -eq 'nl-NL' -and $t['target_lang'] -eq 'en-GB')
Check 'every term has a 36-character uuid' ((Scalar $test "select count(*) from termbase_terms where length(term_uuid) <> 36") -eq 0)
Check 'notes land in notes'               ((Scalar $test "select notes from termbase_terms where source_term = 'werkwijze'") -eq 'the usual patent sense')

# Importing the same file again adds nothing: safe to re-run, as Trados's is.
$r2 = Import $id1 $rows 'dut-NL' 'eng-GB'
Check "a second import of the same file adds nothing ($($r2.Added) added, $($r2.Duplicates) duplicates)" ($r2.Added -eq 0 -and $r2.Duplicates -eq 5)

# =============================================================================
# 4. Import into a termbase that runs the other way
# =============================================================================
$r3 = Import $id2 @((NewRow 'huis' 'house')) 'dut' 'eng'
Check 'rows declared nl->en into an en->nl termbase are recognised as reversed' $r3.Reversed
$h = (Rows $test "select source_term, target_term from termbase_terms where termbase_id = 2")[0]
Check 'and stored turned round: source is the English side' ($h['source_term'] -eq 'house' -and $h['target_term'] -eq 'huis') ("$($h['source_term'])->$($h['target_term'])")

$r4 = Import $id2 @((NewRow 'tuin' 'garden')) '' ''
Check 'rows with no declared direction are taken as the termbase''s own' (-not $r4.Reversed)

# =============================================================================
# 5. Files: both shapes round-trip, and the existing glossary format is read
# =============================================================================
$exported = RowsOf $id1
Check "export reads back every row ($($exported.Count))" ($exported.Count -eq 3)

$gl = Join-Path $Scratch 'export.txt'
WriteFile $gl 'Glossary' 'Writer test A' 'nl-NL' 'en-GB' $exported
$back = ReadFile $gl
Check 'glossary shape: name comes back'        ($back.Name -eq 'Writer test A')
Check 'glossary shape: languages come back'    ($back.SourceLang -eq 'nl-NL' -and $back.TargetLang -eq 'en-GB')
Check 'glossary shape: every row comes back'   ($back.Rows.Count -eq 3)
$fb = $back.Rows | Where-Object { $_.Source -eq 'toestel' } | Select-Object -First 1
Check 'glossary shape: forbidden survives the round trip' ($null -ne $fb -and $fb.Forbidden)
$nt = $back.Rows | Where-Object { $_.Source -eq 'werkwijze' } | Select-Object -First 1
Check 'glossary shape: notes survive the round trip' ($null -ne $nt -and $nt.Notes -eq 'the usual patent sense')

$tsv = Join-Path $Scratch 'export.tsv'
WriteFile $tsv 'HeaderRowTsv' 'Writer test A' 'nl-NL' 'en-GB' $exported
$first = (Get-Content $tsv -First 1)
Check 'header-row shape: the heading names the languages for a person' ($first -like 'Dutch (nl-NL)*English (en-GB)*') $first
$back2 = ReadFile $tsv
Check 'header-row shape: the header is recognised, not read as a term' ($back2.Rows.Count -eq 3) "rows=$($back2.Rows.Count)"
Check 'header-row shape: languages are taken from the headings' ($back2.SourceLang -eq 'nl-NL' -and $back2.TargetLang -eq 'en-GB') "$($back2.SourceLang)->$($back2.TargetLang)"

# The format the three glossaries in memoq\glossaries actually have.
$legacy = Join-Path $Scratch 'legacy.txt'
@(
    '# Some client (CASE-001) v3',
    '#! source=dut-NL target=eng-GB',
    "# Exported from the prompt library by Supervertaler. Tab-separated: source, target, optional 'forbidden'.",
    "adsorbens`tadsorbent",
    "toestel`tapparaat`tforbidden",
    "werkwijze`tmethod"
) | Set-Content -Path $legacy -Encoding UTF8
$lg = ReadFile $legacy
Check 'an existing prompt-library glossary is read: name'      ($lg.Name -eq 'Some client (CASE-001) v3')
Check 'an existing prompt-library glossary is read: languages' ($lg.SourceLang -eq 'dut-NL' -and $lg.TargetLang -eq 'eng-GB')
Check 'an existing prompt-library glossary is read: rows'      ($lg.Rows.Count -eq 3 -and ($lg.Rows | Where-Object { $_.Forbidden }).Count -eq 1)

# A spreadsheet's CSV, with a header row, quoted cells and a semicolon.
$csv = Join-Path $Scratch 'sheet.csv'
@(
    'Dutch;English;Notes',
    'adsorbens;adsorbent;',
    '"werkwijze; alt";"method";"has a ""quote"" in it"'
) | Set-Content -Path $csv -Encoding UTF8
$cs = ReadFile $csv
Check 'a semicolon CSV with a header row is read'      ($cs.Rows.Count -eq 2) "rows=$($cs.Rows.Count)"
Check 'languages are taken from the header names'     ($cs.SourceLang -eq 'Dutch' -and $cs.TargetLang -eq 'English')
Check 'quoted cells keep their delimiter and doubled quotes' ($cs.Rows[1].Source -eq 'werkwijze; alt' -and $cs.Rows[1].Notes -eq 'has a "quote" in it') ("$($cs.Rows[1].Source) / $($cs.Rows[1].Notes)")

# =============================================================================
# 6. Delete leaves nothing dangling, in the file or in the selection
# =============================================================================
# Put the termbase into a project's selection first, so Forget has something to do.
$project = [Guid]'55555555-5555-5555-5555-555555555555'
$f = [Activator]::CreateInstance($flagsType); $f.Id = $id1; $f.Rank = 1; $f.Name = 'Writer test A'
$idList = New-Object 'System.Collections.Generic.List[long]'; $idList.Add($id1)
$flagList = [Activator]::CreateInstance([System.Collections.Generic.List`1].MakeGenericType($flagsType)); $flagList.Add($f)
$sa = [object[]]::new(3); $sa[0] = $project; $sa[1] = $idList.PSObject.BaseObject; $sa[2] = $flagList.PSObject.BaseObject
$sel.GetMethod('Save', $NPS).Invoke($null, $sa)

Delete $id1
Check 'the termbase row is gone'      ((Scalar $test 'select count(*) from termbases where id = 1') -eq 0)
Check 'its terms are gone'            ((Scalar $test 'select count(*) from termbase_terms where termbase_id = 1') -eq 0)
Check 'the other termbase is untouched' ((Scalar $test 'select count(*) from termbase_terms where termbase_id = 2') -eq 2)
# The real proof of the rebuild: a term of the deleted termbase must no longer
# MATCH. Without the rebuild it still would - measured: after a content-only
# delete, count(*) fell to 0 and the deleted term STILL matched. A count here
# would pass with the index untouched.
Check 'the index no longer finds a term of the deleted termbase' ((Scalar $test "select count(*) from termbase_terms_fts where termbase_terms_fts match 'adsorbens'") -eq 0)
Check 'and still finds one of the termbase that remains'         ((Scalar $test "select count(*) from termbase_terms_fts where termbase_terms_fts match 'house'") -eq 1)
$ra = [object[]]::new(1); $ra[0] = $project
$readBack = $sel.GetMethod('ReadFor', $NPS).Invoke($null, $ra)
Check 'the selection file no longer names it' ($readBack.Count -eq 0) "still $($readBack.Count)"

# =============================================================================
# 7. Scale: an import the size of the biggest real termbase
# =============================================================================
$big = New-Object 'System.Collections.Generic.List[object]'
for ($i = 1; $i -le 12000; $i++) { $big.Add((NewRow ("bronterm{0:D5}" -f $i) ("targetterm{0:D5}" -f $i))) }
$id3 = Create 'Writer test scale' 'nl' 'en'
$sw = [Diagnostics.Stopwatch]::StartNew()
$rb = Import $id3 $big 'nl' 'en'
$sw.Stop()
Write-Host ("   12,000-row import: {0} ms" -f $sw.ElapsedMilliseconds)
Check '12,000 rows imported' ($rb.Added -eq 12000) "added=$($rb.Added)"
Check 'the index finds the last row of a large import' ((Scalar $test "select count(*) from termbase_terms_fts where termbase_terms_fts match 'bronterm12000'") -eq 1)
Check 'and the first'                                   ((Scalar $test "select count(*) from termbase_terms_fts where termbase_terms_fts match 'bronterm00001'") -eq 1)
Check 'a large import stays under five seconds' ($sw.ElapsedMilliseconds -lt 5000) "$($sw.ElapsedMilliseconds) ms"

$sw = [Diagnostics.Stopwatch]::StartNew()
Delete $id3
$sw.Stop()
Write-Host ("   deleting it (index rebuild over what remains): {0} ms" -f $sw.ElapsedMilliseconds)

$test.Close()

# =============================================================================
# 8. The live database was never written to
# =============================================================================
Check 'the live database has the same number of termbases as before' ((Scalar $live 'select count(*) from termbases') -eq $liveTermbasesBefore)
Check 'and the same number of terms' ((Scalar $live 'select count(*) from termbase_terms') -eq $liveTermsBefore)
$live.Close()

Remove-Item $Scratch -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host ("TERMBASE WRITER TEST COMPLETE - {0} passed, {1} failed" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
