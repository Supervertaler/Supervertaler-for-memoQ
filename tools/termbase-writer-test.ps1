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
    # A writable handle also CREATES: the field-file fixtures below start from nothing.
    $mode = if ($readOnly) { 'ReadOnly' } else { 'ReadWriteCreate' }
    $c = New-Object Microsoft.Data.Sqlite.SqliteConnection("Data Source=$path;Mode=$mode;Foreign Keys=False")
    $c.Open()
    return $c
}
function Scalar($con, [string]$sql) { $c = $con.CreateCommand(); $c.CommandText = $sql; return $c.ExecuteScalar() }

# FTS5's integrity check is an INSERT statement, so it needs a WRITABLE handle
# even though it writes nothing - on a read-only connection it fails whatever the
# index's state, which is a check that cannot pass, not a check that failed.
function Integrity([string]$path) {
    $w = OpenDb $path $false
    try { $null = Scalar $w "insert into termbase_terms_fts(termbase_terms_fts, rank) values('integrity-check', 0)"; return $true }
    catch { Write-Host ("   integrity-check: {0}" -f $_.Exception.InnerException.Message) -ForegroundColor DarkGray; return $false }
    finally { $w.Close() }
}
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
$pendingTriggers = 0
foreach ($item in $objects) {
    $liveSql = Scalar $live ("select sql from sqlite_master where name = '{0}'" -f $item.Name)
    $testSql = Scalar $test ("select sql from sqlite_master where name = '{0}'" -f $item.Name)
    if ($null -eq $liveSql -and $item.Type -eq 'trigger') { $pendingTriggers++; continue }
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
if ($pendingTriggers -gt 0) {
    # Until the Trados release that installs them (18.20.192, 2026-09-21) the
    # live file has no triggers; after it, they are compared like everything
    # else. Say which state we are in rather than silently skipping.
    Write-Host ("   ({0} trigger(s) not yet in the live file - Trados installs them on release; compared once they exist)" -f $pendingTriggers)
}
Check 'a created file has all three index triggers from birth' `
      ((Scalar $test "select count(*) from sqlite_master where type='trigger' and tbl_name='termbase_terms'") -eq 3)

# The trigger text this product emits for a three-column index must be the
# agreed text, byte for byte - the Trados side asserts the same on theirs.
function TriggerSql([string[]]$cols, [int]$kind) {
    $a = [object[]]::new(2); $a[0] = $cols; $a[1] = $kind
    return $schema.GetMethod('TriggerSql', $NPS).Invoke($null, $a)
}
$three = @('source_term', 'target_term', 'definition')
$agreedInsert = "CREATE TRIGGER IF NOT EXISTS termbase_terms_fts_ai AFTER INSERT ON termbase_terms BEGIN`n" +
                "  INSERT INTO termbase_terms_fts(rowid, source_term, target_term, definition)`n" +
                "  VALUES (new.id, new.source_term, new.target_term, new.definition);`n" +
                "END;"
$agreedDelete = "CREATE TRIGGER IF NOT EXISTS termbase_terms_fts_ad AFTER DELETE ON termbase_terms BEGIN`n" +
                "  INSERT INTO termbase_terms_fts(termbase_terms_fts, rowid, source_term, target_term, definition)`n" +
                "  VALUES ('delete', old.id, old.source_term, old.target_term, old.definition);`n" +
                "END;"
$agreedUpdate = "CREATE TRIGGER IF NOT EXISTS termbase_terms_fts_au AFTER UPDATE ON termbase_terms BEGIN`n" +
                "  INSERT INTO termbase_terms_fts(termbase_terms_fts, rowid, source_term, target_term, definition)`n" +
                "  VALUES ('delete', old.id, old.source_term, old.target_term, old.definition);`n" +
                "  INSERT INTO termbase_terms_fts(rowid, source_term, target_term, definition)`n" +
                "  VALUES (new.id, new.source_term, new.target_term, new.definition);`n" +
                "END;"
Check 'insert trigger text equals the agreed SQL byte for byte' ((TriggerSql $three 0) -ceq $agreedInsert)
Check 'delete trigger text equals the agreed SQL byte for byte' ((TriggerSql $three 1) -ceq $agreedDelete)
Check 'update trigger text equals the agreed SQL byte for byte' ((TriggerSql $three 2) -ceq $agreedUpdate)
Check 'the drift test compares the same text the file was created from' `
      ((Comparable (TriggerSql $three 0)) -eq (Comparable (Scalar $test "select sql from sqlite_master where name='termbase_terms_fts_ai'")))
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
# Kept by the TRIGGERS now, not by the writer - and a manual insert on top of a
# trigger corrupts an external-content index rather than duplicating, so the
# writer must not touch it. This MATCH is the proof that the triggers fired.
Check 'the index finds each term that was just added'  ((Scalar $test "select count(*) from termbase_terms_fts where termbase_terms_fts match 'werkwijze'") -eq 1)
Check 'and the index passes its own integrity check after the import' (Integrity $TestDb)
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
# 5b. Single terms: add, correct, remove - and the edit reaches lookup
# =============================================================================
function AddTerm([long]$tb, [string]$s, [string]$t, [bool]$forbidden, [string]$notes) {
    $a = [object[]]::new(5); $a[0] = $tb; $a[1] = $s; $a[2] = $t; $a[3] = $forbidden; $a[4] = $notes
    return [long]$writer.GetMethod('AddTerm', $NPS).Invoke($null, $a)
}
function UpdateTerm([long]$id, [string]$s, [string]$t, [bool]$forbidden, [string]$notes) {
    $a = [object[]]::new(5); $a[0] = $id; $a[1] = $s; $a[2] = $t; $a[3] = $forbidden; $a[4] = $notes
    $writer.GetMethod('UpdateTerm', $NPS).Invoke($null, $a)
}
function DeleteTerm([long]$id) {
    $a = [object[]]::new(1); $a[0] = $id
    $writer.GetMethod('DeleteTerm', $NPS).Invoke($null, $a)
}
function Stamp([long[]]$ids) {
    $l = New-Object 'System.Collections.Generic.List[long]'; foreach ($i in $ids) { $l.Add($i) }
    $a = [object[]]::new(1); $a[0] = $l.PSObject.BaseObject
    return $db.GetMethod('ChangeStamp', $NPS).Invoke($null, $a)
}
function Match([string]$word) { return Scalar $test ("select count(*) from termbase_terms_fts where termbase_terms_fts match '{0}'" -f $word) }

$stamp0 = Stamp @($id1)
$kolom = AddTerm $id1 'kolom' 'column' $false 'added by hand'
Check 'a term added by hand gets an id'                 ($kolom -gt 0)
Check 'and is found by the index at once (insert trigger)' ((Match 'kolom') -eq 1)
Check 'and moves the change stamp'                       ((Stamp @($id1)) -ne $stamp0)
$termsOf = $db.GetMethod('TermsOf', $NPS).Invoke($null, [object[]]@($id1))
$mine = $termsOf | Where-Object { $_.Id -eq $kolom } | Select-Object -First 1
Check 'TermsOf returns it with its id, notes and all'   ($null -ne $mine -and $mine.Notes -eq 'added by hand')

$refusal = ''
try { $null = AddTerm $id1 'column' 'kolom' $false $null }
catch { $e = $_.Exception; if ($e -is [Reflection.TargetInvocationException] -and $e.InnerException) { $e = $e.InnerException }; $refusal = $e.Message }
Check 'the same pair the other way round is refused, in words' ($refusal -like '*already in this termbase*') $refusal

$stamp1 = Stamp @($id1)
# The stamp's date part is whole seconds, so a correction in the same second as
# the add above would not move it - measured, the first time this ran. That is
# the real contract: the database's stamp catches changes a second apart (every
# change from another product, in practice), and the editor's own same-second
# edits are caught by the selection file's write time, which is the other half
# of TermIndex's reload key and is tested at the end of this section.
Start-Sleep -Milliseconds 1100
UpdateTerm $kolom 'kolommen' 'columns' $true 'corrected'
Check 'after a correction the old word is no longer found (update trigger)' ((Match 'kolom') -eq 0)
Check 'and the new one is'                                                  ((Match 'kolommen') -eq 1)
Check 'the row kept its id'                                                  ((Scalar $test "select count(*) from termbase_terms where id = $kolom and source_term = 'kolommen'") -eq 1)
Check 'forbidden and notes were written'                                     ((Scalar $test "select forbidden || '|' || notes from termbase_terms where id = $kolom") -eq '1|corrected')
Check 'a correction moves the change stamp'                                  ((Stamp @($id1)) -ne $stamp1)
Check 'the index passes its integrity check after an update'                (Integrity $TestDb)

$stamp2 = Stamp @($id1)
DeleteTerm $kolom
Check 'a removed term is gone'                          ((Scalar $test "select count(*) from termbase_terms where id = $kolom") -eq 0)
Check 'and no longer found (delete trigger)'            ((Match 'kolommen') -eq 0)
Check 'a removal moves the change stamp'                ((Stamp @($id1)) -ne $stamp2)

# The point of the stamp: an edit reaches memoQ's lookup WITHOUT the selection
# changing. Before the stamp, TermIndex reloaded a selection only when the ids
# or ranks changed, so a corrected term reached the grid after a restart.
$index = $plugin.GetType('Supervertaler.MemoQ.Core.TermIndex')
$index.GetField('ErrorSink', $NPS).SetValue($null, [Action[string, Exception]]{ param($m, $ex) })
$lastCheck = $index.GetField('_lastCheck', $NPS)
function Unthrottle { $lastCheck.SetValue($null, [DateTime]::MinValue) }
function Find([Guid]$project, [string]$text) {
    $m = $index.GetMethods($PS) | Where-Object { $_.Name -eq 'Find' -and $_.GetParameters().Count -eq 2 }
    $a = [object[]]::new(2); $a[0] = $project; $a[1] = $text
    return $m.Invoke($null, $a)
}
function FindForModel([Guid]$project, [string]$text) {
    $a = [object[]]::new(2); $a[0] = $project; $a[1] = $text
    return $index.GetMethod('FindForModel', $PS).Invoke($null, $a)
}
$ua = [object[]]::new(2); $ua[0] = 'dut'; $ua[1] = 'eng'
$index.GetMethod('UseLanguages', $PS).Invoke($null, $ua)

$editProject = [Guid]'66666666-6666-6666-6666-666666666666'
$ef = [Activator]::CreateInstance($flagsType); $ef.Id = $id1; $ef.Rank = 1; $ef.Name = 'Writer test A'
$eIds = New-Object 'System.Collections.Generic.List[long]'; $eIds.Add($id1)
$eFlags = [Activator]::CreateInstance([System.Collections.Generic.List`1].MakeGenericType($flagsType)); $eFlags.Add($ef)
$ea = [object[]]::new(3); $ea[0] = $editProject; $ea[1] = $eIds.PSObject.BaseObject; $ea[2] = $eFlags.PSObject.BaseObject
$sel.GetMethod('Save', $NPS).Invoke($null, $ea)

Unthrottle
Check 'lookup finds an existing term of the selection'   ((Find $editProject 'Het adsorbens werd gemeten.').Count -eq 1)
Check 'and not one that does not exist yet'              ((Find $editProject 'De meetsonde werd gemeten.').Count -eq 0)
$null = AddTerm $id1 'meetsonde' 'probe' $false $null
Unthrottle
Check 'a term added by hand reaches lookup with NO change to the selection' ((Find $editProject 'De meetsonde werd gemeten.').Count -eq 1)

# The same-second case, which the database stamp cannot see: correct the term
# immediately after lookup loaded it, then do what the editor does on OK - save
# the selection, which rewrites the file - and look again.
$probe = ($db.GetMethod('TermsOf', $NPS).Invoke($null, [object[]]@($id1)) | Where-Object { $_.Source -eq 'meetsonde' } | Select-Object -First 1).Id
UpdateTerm $probe 'meetkop' 'probe head' $false $null
Start-Sleep -Milliseconds 20      # so the file's write time below is distinguishable from the save above
$sel.GetMethod('Save', $NPS).Invoke($null, $ea)
Unthrottle
Check 'a correction made in the same second reaches lookup once the editor saves' ((Find $editProject 'De meetkop werd gemeten.').Count -eq 1)
Check 'and the old form is gone from lookup'                                          ((Find $editProject 'De meetsonde werd gemeten.').Count -eq 0)

# The AI tick is a separate decision from Read, and it decides what the model
# is told. Until 2026-09-18 nothing read it: every Read termbase reached the
# prompt. Now Find (the grid, the pane) answers from Read; FindForModel (the
# prompt, AutoPrompt) answers only from termbases ticked AI as well.
$ef.Ai = $false
$sel.GetMethod('Save', $NPS).Invoke($null, $ea)
Unthrottle
Check 'Read without AI: the grid sees the term'          ((Find $editProject 'Het adsorbens werd gemeten.').Count -eq 1)
Check 'Read without AI: the model is NOT told about it'  ((FindForModel $editProject 'Het adsorbens werd gemeten.').Count -eq 0)
$ef.Ai = $true
$sel.GetMethod('Save', $NPS).Invoke($null, $ea)
Unthrottle
Check 'Read with AI: the model is told'                  ((FindForModel $editProject 'Het adsorbens werd gemeten.').Count -eq 1)

# The CS tick, which the matcher ignored until 2026-09-18 - every match was
# case-insensitive whatever the window said. Now a termbase ticked CS matches
# only the case as written; the rest keep matching any case.
Check 'CS off: ADSORBENS in capitals still matches adsorbens' ((Find $editProject 'Het ADSORBENS werd gemeten.').Count -eq 1)
$ef.CaseSensitive = $true
$sel.GetMethod('Save', $NPS).Invoke($null, $ea)
Unthrottle
Check 'CS on: ADSORBENS in capitals no longer matches'       ((Find $editProject 'Het ADSORBENS werd gemeten.').Count -eq 0)
Check 'CS on: adsorbens as written still does'                ((Find $editProject 'Het adsorbens werd gemeten.').Count -eq 1)
$ef.CaseSensitive = $false
$sel.GetMethod('Save', $NPS).Invoke($null, $ea)
Unthrottle

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
# 6b. Files from the field: rows without triggers, and a four-column index
# =============================================================================
# (i) A file that already has terms but no triggers - the live file before the
# Trados release, every Workbench file, every old Trados file. Opening it for
# writing must install the triggers AND rebuild once, so the index is correct
# from the first row the triggers see.
$noTrig = Join-Path $Scratch 'notriggers.db'
$raw = OpenDb $noTrig $false
$null = Scalar $raw (Scalar $test "select sql from sqlite_master where name='termbases'")
$null = Scalar $raw (Scalar $test "select sql from sqlite_master where name='termbase_terms'")
$null = Scalar $raw (Scalar $test "select sql from sqlite_master where name='termbase_terms_fts'")
$null = Scalar $raw "insert into termbases (name, source_lang, target_lang) values ('field', 'nl', 'en')"
$null = Scalar $raw "insert into termbase_terms (source_term, target_term, termbase_id, term_uuid) values ('veldterm', 'fieldterm', 1, 'u-field-1')"
Check 'fixture: a term no index knows about' ((Scalar $raw "select count(*) from termbase_terms_fts where termbase_terms_fts match 'veldterm'") -eq 0)
$raw.Close()

$db.GetField('PathOverride', $NPS).SetValue($null, $noTrig)
$null = Create 'field two' 'nl' 'en'          # any write opens the file, which installs and rebuilds
$chk = OpenDb $noTrig $true
Check 'opening a field file installs the three triggers'        ((Scalar $chk "select count(*) from sqlite_master where type='trigger' and tbl_name='termbase_terms'") -eq 3)
Check 'and rebuilds once, so the pre-existing term is now found' ((Scalar $chk "select count(*) from termbase_terms_fts where termbase_terms_fts match 'veldterm'") -eq 1)
$chk.Close()

# (ii) An old Trados-created file: four-column index. The triggers must be
# written for FOUR columns, or every delete leaves notes tokens behind.
$four = Join-Path $Scratch 'fourcol.db'
$raw = OpenDb $four $false
$null = Scalar $raw (Scalar $test "select sql from sqlite_master where name='termbases'")
$null = Scalar $raw (Scalar $test "select sql from sqlite_master where name='termbase_terms'")
$null = Scalar $raw "CREATE VIRTUAL TABLE termbase_terms_fts USING fts5(source_term, target_term, definition, notes, content=termbase_terms, content_rowid=id)"
$raw.Close()

$db.GetField('PathOverride', $NPS).SetValue($null, $four)
$id4 = Create 'four columns' 'nl' 'en'
$null = Import $id4 @((NewRow 'vierkolom' 'fourcol' $false 'a note to index')) 'nl' 'en'
$chk = OpenDb $four $true
$aiSql = Scalar $chk "select sql from sqlite_master where name='termbase_terms_fts_ai'"
Check 'on a four-column index the triggers name four columns' ($aiSql -match 'definition, notes')
Check 'and a term inserted through them is found'               ((Scalar $chk "select count(*) from termbase_terms_fts where termbase_terms_fts match 'vierkolom'") -eq 1)
Check 'including by its notes, which that index does carry'    ((Scalar $chk "select count(*) from termbase_terms_fts where termbase_terms_fts match 'notes:index'") -eq 1)
$chk.Close()
Delete $id4
$chk = OpenDb $four $true
Check 'and the term is gone from it' ((Scalar $chk "select count(*) from termbase_terms_fts where termbase_terms_fts match 'vierkolom'") -eq 0)
$chk.Close()
Check 'deleting through a four-column trigger leaves the index consistent' (Integrity $four)

$db.GetField('PathOverride', $NPS).SetValue($null, $TestDb)

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
Write-Host ("   deleting it (the delete trigger, once per row): {0} ms" -f $sw.ElapsedMilliseconds)

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
