# Synonyms: matched on one side, shown on the other, never sent to the model.
#
# This product ignored the synonyms table entirely until 2026-09-21 while
# Supervertaler for Trados wrote and matched it, so a synonym saved there was
# invisible here in the same termbase - 1,081 of 1,147 of them in the one
# termbase in daily use, which is the worst possible place for a silent gap.
#
# The rule, agreed with the Trados side and written down in core's alternatives
# note: SOURCE-side synonyms are matched, because a term with two spellings has
# two things to look for. TARGET-side synonyms are shown to the translator and
# never matched on, and never reach the model - the model must not choose
# between renderings, and that is what a single locked target is for.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$memoq = 'C:\Program Files\memoQ\memoQ-12'
$inFlight = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)
    $name = ([Reflection.AssemblyName]::new($eventArgs.Name)).Name
    if (-not $inFlight.Add($name)) { return $null }
    try {
        $c = Join-Path $memoq "$name.dll"
        if (Test-Path -LiteralPath $c) { return [Reflection.Assembly]::LoadFrom($c) }
        return $null
    } finally { [void]$inFlight.Remove($name) }
})

$asm = [Reflection.Assembly]::LoadFrom('D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll')
$S = [Reflection.BindingFlags]'Public,NonPublic,Static'

$db = $asm.GetType('Supervertaler.MemoQ.Core.TermbaseDb')
$db.GetMethod('EnsureProvider', $S).Invoke($null, @()) | Out-Null

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# ---- a termbase built for the purpose, not the live one -----------------
# Read-only against the real database would be a weaker test - it cannot create
# the reversed case - and writing to it is out of the question.
$tmp = Join-Path $env:TEMP ("sv-syn-" + [guid]::NewGuid().ToString('N') + ".db")
$override = $db.GetField('PathOverride', $S)
$override.SetValue($null, $tmp)

try {
    $schema = $asm.GetType('Supervertaler.MemoQ.Core.TermbaseSchema')
    $writer = $asm.GetType('Supervertaler.MemoQ.Core.TermbaseWriter')

    $conn = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$tmp")
    $conn.Open()
    $schema.GetMethod('EnsureCreated', $S).Invoke($null, [object[]]@($conn)) | Out-Null

    function Exec($sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; return $c.ExecuteNonQuery() }
    function Scalar($sql) { $c = $conn.CreateCommand(); $c.CommandText = $sql; return $c.ExecuteScalar() }

    # One termbase the right way round, one reversed, so the side-swap is tested
    # rather than assumed. Getting that backwards would index a translation as
    # something to look for in the source text.
    Exec "insert into termbases (id, name, source_lang, target_lang) values (1, 'FORWARD', 'nl', 'en')" | Out-Null
    Exec "insert into termbases (id, name, source_lang, target_lang) values (2, 'BACKWARD', 'en', 'nl')" | Out-Null

    Exec "insert into termbase_terms (id, source_term, target_term, termbase_id) values (10, 'inrichting', 'device', 1)" | Out-Null
    Exec "insert into termbase_synonyms (term_id, synonym_text, language) values (10, 'apparaat', 'source')" | Out-Null
    Exec "insert into termbase_synonyms (term_id, synonym_text, language) values (10, 'apparatus', 'target')" | Out-Null

    # Stored en->nl; on a nl->en job this is read backwards, so its 'source'
    # synonym is a TARGET-side one for the job and must not be matched on.
    Exec "insert into termbase_terms (id, source_term, target_term, termbase_id) values (20, 'method', 'werkwijze', 2)" | Out-Null
    Exec "insert into termbase_synonyms (term_id, synonym_text, language) values (20, 'procedure', 'source')" | Out-Null
    Exec "insert into termbase_synonyms (term_id, synonym_text, language) values (20, 'methode', 'target')" | Out-Null
    $conn.Close()

    $termsIn = $db.GetMethods($S) | Where-Object { $_.Name -eq 'TermsIn' -and $_.GetParameters().Count -eq 3 }
    $ids = [Collections.Generic.List[long]]::new(); $ids.Add(1); $ids.Add(2)
    $args = New-Object object[] 3
    $args[0] = $ids; $args[1] = 'nl'; $args[2] = 'en'
    $entries = @($termsIn.Invoke($null, $args))

    foreach ($e in $entries) {
        Write-Host ("   {0,-12} -> {1,-12} syn={2} also=[{3}] from={4}" -f `
            $e.Source, $e.Target, $e.FromSynonym, ($(if ($e.AlsoAcceptable) { $e.AlsoAcceptable -join ', ' } else { '' })), $e.Origin)
    }
    Write-Host ''

    function Find($src) { return @($entries | Where-Object { $_.Source -eq $src })[0] }

    # ---- the forward termbase -------------------------------------------
    Check ($null -ne (Find 'inrichting')) "the term itself is loaded"
    Check ($null -ne (Find 'apparaat')) "its SOURCE synonym is loaded as something to look for"
    Check ((Find 'apparaat').Target -eq 'device') "carrying the same target"
    Check ((Find 'apparaat').FromSynonym) "and marked as a synonym, so the pane can say so"
    Check (-not (Find 'inrichting').FromSynonym) "while the term itself is not"

    Check ($null -eq (Find 'apparatus')) "a TARGET synonym is NOT something to look for"
    Check ((Find 'inrichting').AlsoAcceptable -contains 'apparatus') "it is offered to the translator instead"

    # ---- the reversed termbase, where the sides swap --------------------
    # Stored en->nl, read on a nl->en job: source and target swap, and so must
    # the synonyms. 'procedure' is stored source-side, so for THIS job it is a
    # target-side variant and must not be matched.
    Check ($null -ne (Find 'werkwijze')) "a reversed termbase still yields its term the job's way round"
    Check ((Find 'werkwijze').Target -eq 'method') "with source and target swapped"
    Check ($null -ne (Find 'methode')) "its stored TARGET synonym becomes a source-side one for this job"
    Check ((Find 'methode').Target -eq 'method') "pointing at the job's target"
    Check ($null -eq (Find 'procedure')) "and its stored SOURCE synonym is NOT matched, being a target-side variant here"
    Check ((Find 'werkwijze').AlsoAcceptable -contains 'procedure') "it is offered to the translator instead"

    # ---- scale ----------------------------------------------------------
    # One query for all synonyms, not one per term. The live database holds
    # 1,147 against 37,359 terms; a query per term is tens of thousands of round
    # trips to save a dictionary.
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $null = $termsIn.Invoke($null, $args)
    $sw.Stop()
    Check ($sw.ElapsedMilliseconds -lt 2000) "loading stays quick ($($sw.ElapsedMilliseconds) ms on a small file)"
}
finally {
    $override.SetValue($null, $null)
    [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools()
    Remove-Item -LiteralPath $tmp -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "SYNONYMS TEST COMPLETE - $fails failure(s)"
