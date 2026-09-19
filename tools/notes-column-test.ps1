# A prompt's term table becomes a termbase: does the note survive the journey?
#
# The note is the only part of a locked-terms table that says WHY a term was
# chosen - "only in the claims", "never in this sense". A termbase built from a
# prompt without it keeps the decision and loses the reasoning, which is the
# half that ages worst.
#
# Does a prompt's term table reach a termbase row with its note intact?
$ErrorActionPreference = 'Stop'
$exe = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe'
$asm = [Reflection.Assembly]::LoadFrom($exe)
$B = [Reflection.BindingFlags]'Public,NonPublic,Static'

$main = $asm.GetType('Supervertaler.PromptEditor.MainForm')
$split = $main.GetMethod('SplitAlternates', $B)
$ex = [Reflection.Assembly]::LoadFrom($exe).GetType('Supervertaler.Core.PromptGlossaryExtractor')
if (-not $ex) { $ex = ([AppDomain]::CurrentDomain.GetAssemblies() | ForEach-Object { $_.GetType('Supervertaler.Core.PromptGlossaryExtractor') } | Where-Object { $_ })[0] }
$extract = $ex.GetMethod('Extract', $B)

$prompt = @"
## Locked terms

| Source | Target | Notes |
|---|---|---|
| draagarm | support arm | only in the claims |
| inrichting / werkwijze | device / method | never "apparatus" |
| koppeling | coupling |  |
"@

$entries = $extract.Invoke($null, [object[]]@([string]$prompt))
$rows = $split.Invoke($null, [object[]]@(,$entries))

$fails = 0
function Check($ok, $label) { if (-not $ok) { $script:fails++ }; Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label" }

foreach ($r in $rows) { Write-Host ("  row: {0,-22} -> {1,-14} forbidden={2}  notes='{3}'" -f $r.Source, $r.Target, $r.Forbidden, $r.Notes) }

$arm = $rows | Where-Object { $_.Source -eq 'draagarm' }
Check ($arm.Notes -eq 'only in the claims') "a note reaches the termbase row"

# ---- a known defect, asserted so a fix trips this test loudly -----------
# "inrichting / werkwijze | device / method" should become two terms. It does
# not, and the cause is upstream in core: Extract collapses a target cell
# containing " / " to its first alternative BEFORE this code sees it, so the
# source arrives with two alternatives and the target with one. The counts no
# longer match, the split does not fire, and a single row is stored whose source
# is the literal string "inrichting / werkwijze" - a term no segment will ever
# contain.
#
# Not fixed here, deliberately. Splitting the source and pointing both halves at
# the surviving target would store "werkwijze -> device", which is wrong: the
# prompt said method. A dead row is better than a wrong one. The real fix is in
# core, which should pair the two sides when their counts match rather than
# collapsing the target first, and core's owner has not been settled.
#
# The information is not lost - Extract records it in the note - so a fix is
# possible on either side.
$joined = $rows | Where-Object { $_.Source -eq 'inrichting / werkwijze' -and -not $_.Forbidden }
Check ($null -ne $joined) "KNOWN DEFECT: alternates do not split, one dead row is stored instead"
Check ($joined.Notes -like '*prompt listed alternatives*') "but the note records what the prompt actually said: '$($joined.Notes)'"

$banned = $rows | Where-Object { $_.Forbidden }
Check ($null -ne $banned) "a never-use note still becomes a forbidden row"
Check ($banned.Notes.Length -gt 0) "which carries a note of its own: '$($banned.Notes)'"

$coupling = $rows | Where-Object { $_.Source -eq 'koppeling' }
Check ($coupling.Notes -eq '') "an empty note stays empty rather than becoming null"

Write-Host ''
Write-Host "NOTES COLUMN TEST COMPLETE - $fails failure(s)"
