# What bridge mode returns for a segment nobody staged.
#
# It returned an empty translation until 2026-09-20, and memoQ does exactly what
# it is told: it writes the empty string into the target. So a row with nothing
# staged did not keep what was in it - a fuzzy match, an earlier pass, a human's
# work - it was cleared. Found on a live job where 32 rows came back blank, every
# one of which had content before the run.
#
# The assertion that matters is the negative one: the result must NOT be an empty
# translation. A test that only checked "a result came back" would pass the bug.
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

$asm = [Reflection.Assembly]::LoadFrom('D:\SynologyDrive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll')
$B = [Reflection.BindingFlags]'Public,NonPublic,Static'

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# ---- 1. the source says what it must ------------------------------------
# Read rather than executed: constructing a real EngineContext means building an
# engine, which seeds settings and can make a billable call. The shape of the
# branch is what regressed, and the shape is what this guards.
$src = Get-Content 'D:\SynologyDrive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\Core\BatchTranslator.cs' -Raw
$branch = [regex]::Match($src, 'if \(context\.General\.BridgeMode\)\s*\{(?<body>[\s\S]*?)\n            \}')

Check ($branch.Success) "the bridge-mode branch is still where the test expects it"
$body = $branch.Groups['body'].Value

Check ($body -notmatch 'Translation\s*=\s*Segment\.Empty') `
    "bridge mode does NOT return an empty translation for an unstaged segment"
Check ($body -match 'Exception\s*=') `
    "it returns a result carrying an exception, which is how this SDK says no-result"
Check ($body -match 'staged') "and the message tells the reader what to do about it"

# ---- 2. an empty SOURCE still gets an empty translation -----------------
# The fix must not reach the other branch: a segment with no text legitimately
# translates to nothing, and memoQ expects that rather than an error.
$empty = [regex]::Match($src, 'IsEmptyText\)\s*\n\s*results\[i\] = (?<r>[^;]+);')
Check ($empty.Success -and $empty.Groups['r'].Value -match 'Segment\.Empty') `
    "an empty source still returns an empty translation, not an error"

# ---- 3. the unmatched-source check --------------------------------------
$check = $asm.GetType('Supervertaler.MemoQ.Core.StagedSourceCheck')
$unmatched = $check.GetMethod('Unmatched', $B)
$describe  = $check.GetMethod('Describe', $B)

# With nothing captured and no live document, the plugin knows nothing, so it
# must say nothing. Warning about everything would teach the reader to ignore it.
$list = [Collections.Generic.List[string]]::new()
$list.Add('something nobody has ever seen')
$got = $unmatched.Invoke($null, [object[]]@(, [Collections.Generic.IEnumerable[string]]$list))
Check ($got.Count -eq 0) "with nothing seen yet, nothing is reported as unmatched ($($got.Count))"

Check ($null -eq $describe.Invoke($null, [object[]]@([Collections.Generic.List[string]]$null))) "no sentence when there is nothing to say"
$none = [Collections.Generic.List[string]]::new()
Check ($null -eq $describe.Invoke($null, [object[]]@(, $none))) "nor for an empty list"

$two = [Collections.Generic.List[string]]::new()
$two.Add('draagarm'); $two.Add('werkwijze')
$sentence = $describe.Invoke($null, [object[]]@(, $two))
Check ($sentence -like '*2 of them*') "the sentence counts them: $($sentence.Substring(0, [Math]::Min(40, $sentence.Length)))..."
Check ($sentence -like '*draagarm*') "and names them"

# ---- 4. trailing whitespace: a staged target carries none ----------------
# memoQ sends a provider the source without its trailing whitespace and puts it
# back on the result. So a target must carry none, whatever the source looks
# like: one ending in a space on a source ending in a space came back doubled,
# and one matched to the live link's source - where an sdlxliff keeps the
# inter-sentence space that memoQ's grid does not - got a space the source lacks.
$pc = $asm.GetType('Supervertaler.MemoQ.Core.StagedPairCheck')
$trim = $pc.GetMethod('TrimTrailingWhitespace', $B)
function Fix($t) { return $trim.Invoke($null, [object[]]@([string]$t)) }

Check ((Fix 'Opnieuw installeren ') -eq 'Opnieuw installeren') "a trailing space is removed - memoQ adds the source's own"
Check ((Fix "Opnieuw installeren`t `r`n") -eq 'Opnieuw installeren') "so is any trailing run"
Check ((Fix 'Opnieuw installeren') -eq 'Opnieuw installeren') "and a clean target is untouched, so none is ever ADDED"
Check ((Fix '  Opnieuw installeren') -eq '  Opnieuw installeren') "leading whitespace is left alone"

# A target that is nothing but whitespace is not a translation; emptying it
# would turn it into something else.
Check ((Fix '   ') -eq '   ') "an all-whitespace target is left alone"
# PowerShell turns $null into "" for a [string] parameter, so this goes through Invoke directly.
Check ($null -eq $trim.Invoke($null, [object[]]@($null))) "and a null target stays null"

# ---- 5. tag differences are reported, not refused -----------------------
# A difference is not always an error - memoQ's markup varies by file filter -
# so these warn rather than refuse. The NAME is the identity, which is all memoQ
# gives a plugin.
#
# Built inline with no helper function: PowerShell unrolls a collection at every
# boundary it can, including a function's return, so each list is constructed and
# passed in place.
$problems = $pc.GetMethod('Problems', $B)
$KV = [Collections.Generic.KeyValuePair[string,string]]

function TagCount($sourceText, $targetText) {
    $list = [Collections.Generic.List[Collections.Generic.KeyValuePair[string,string]]]::new()
    $list.Add($KV::new($sourceText, $targetText))
    $a = New-Object object[] 1
    $a[0] = $list
    return @($problems.Invoke($null, $a)).Count
}

Check ((TagCount 'About <inline_tag id="0"/> s' 'Ongeveer <inline_tag id="0"/> s') -eq 0) `
    "a faithfully reproduced placeholder is no complaint"
Check ((TagCount 'About <inline_tag id="0"/> s' 'Ongeveer s') -eq 1) `
    "a DROPPED placeholder is reported"
Check ((TagCount 'Log <spec_char val="&amp;"/> Export' 'Log <inline_tag id="0"/> Export') -eq 1) `
    "so is a tag of the wrong kind, even when the counts are equal"
Check ((TagCount 'plain text' 'gewone tekst') -eq 0) `
    "text without tags is never a complaint"

# ---- 6. memoQ's row state is recorded with the captured source ----------
# Without it a reader cannot tell a row that arrived pre-filled from one nobody
# has touched. On the first production job the pre-filled targets were badly
# wrong and 231 rows of 799 needed changing, and the client's standing
# instruction is to review every matched row - which was unanswerable.
#
# Kept as a lookup by source text rather than a list beside the sources: a
# parallel list has to stay aligned through every add, cap and copy, and the one
# that drifts reports the wrong state for every row after it, which reads as data
# rather than as a bug.
$cs = $asm.GetType('Supervertaler.MemoQ.Core.CaptureStore')
$capture = $cs.GetNestedType('DocumentCapture', $B)
Check ($null -ne $capture.GetField('StatusBySource')) "a captured document carries a status per source"

$rs = $asm.GetType('Supervertaler.MemoQ.Core.RowStatus')
$describe3 = $rs.GetMethod('Describe', $B)
function State($n) { return $describe3.Invoke($null, [object[]]@([int]$n)) }

# memoQ's TranslationStates constants, recovered from the installed assembly.
Check ((State 0) -match 'not started|started') "0 reads as not started: $(State 0)"
Check ((State 1000) -match 'pre-?translated') "1000 reads as pre-translated: $(State 1000)"
Check ((State 3000) -match 'confirm') "3000 reads as confirmed: $(State 3000)"
Check ((State 6000) -match 'machine') "6000 reads as machine translated: $(State 6000)"

# The distinction the client's question turns on: pre-translated is not the same
# as untouched, and both must be nameable.
Check ((State 0) -ne (State 1000)) "a pre-filled row is distinguishable from an untouched one"

# Record still works without a status, which is how terminology lookups arrive.
$record = $cs.GetMethods($B) | Where-Object { $_.Name -eq 'Record' }
Check ($record.Count -eq 2) "Record still has its two-argument form for callers with no status ($($record.Count))"

# ---- 7. the tool catalogue agents actually read -------------------------
# These definitions live in this repo, not the Trados one - I had believed
# otherwise and handed them off before checking. The descriptions are the only
# instructions an agent gets, so a wrong one is a wrong result: the tag wording
# below described <b> and <t1>, and a real job contained neither, being
# inline_tag and spec_char throughout.
$json = Get-Content 'D:\SynologyDrive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\Resources\mcp-tools.json' -Raw
$cat = $json | ConvertFrom-Json
$tools = if ($cat -is [array]) { $cat } else { $cat.tools }
function Tool($n) { return $tools | Where-Object { $_.name -eq $n } }

Check ($null -ne (Tool 'compare_staged_to_grid')) "the verification tool is published"
Check ((Tool 'compare_staged_to_grid').path -eq '/v1/staged/verify') "and points at the endpoint that serves it"

$stage = Tool 'stage_translations'
Check ($null -ne $stage.inputSchema.properties.pairs.items.properties.partId) "a staged pair may name its row id"
Check ($stage.description -match 'partId') "and the description tells the agent to prefer it"
Check ($stage.description -match 'per GRID ROW|per sentence') "the paragraph-versus-row trap is stated"
Check ($stage.description -match 'captured-requests') "and the two-phase procedure points at the ground truth"

$seg = Tool 'get_segments'
# Not "the string never appears" - <b> and <t1> DO occur and the description
# lists them among the forms. What must be gone is the claim that they are THE
# forms, which is what sent an agent looking for tags a job did not contain.
Check ($seg.description -notmatch 'Tags appear as') "get_segments no longer claims tags appear as one fixed form"
Check ($seg.description -match 'inline_tag' -and $seg.description -match 'spec_char') "it names the forms that actually occur"
Check ($seg.description -match 'byte for byte') "and states the rule rather than enumerating"

$gs = Tool 'get_staged'
foreach ($p in 'offset','limit','neverServed','sourceContains','compact') {
    Check ($null -ne $gs.inputSchema.properties.$p) "get_staged accepts $p"
    Check ($gs.paramMap.$p -eq $p) "  and forwards it"
}

# ---- 8. a source staged without its tags still matches memoQ's request ---
# memoQ asks in its segment XML - tags as elements, "&amp;" for "&" - while a
# source taken from the live link has neither. Staged by row id, every row with
# an inline tag missed and went to the model: 31 of 501 rows on one job.
$st = $asm.GetType('Supervertaler.MemoQ.Core.StagedTranslations')
$loose = $st.GetMethod('Loose', $B)
$hasTags = $st.GetMethod('HasTags', $B)
$tryGet = $st.GetMethods($B) | Where-Object { $_.Name -eq 'TryGet' -and $_.GetParameters().Count -eq 3 }
function L($t) { return $loose.Invoke($null, [object[]]@([string]$t)) }
function Get3($s, $p) {
    $a = [object[]]@([string]$s, [string]$p, $false)
    $e = $tryGet.Invoke($null, $a)
    return @{ Entry = $e; Loose = [bool]$a[2] }
}
function StagePairs($pairs, $lp) {
    $list = [Collections.Generic.List[Collections.Generic.KeyValuePair[string,string]]]::new()
    foreach ($k in $pairs.Keys) { $list.Add([Collections.Generic.KeyValuePair[string,string]]::new($k, $pairs[$k])) }
    return $st.GetMethod('Stage', $B).Invoke($null, [object[]]@($list, $lp, 'test'))
}

Check ((L 'Press <inline_tag id="0"/>Start<inline_tag id="1"/> now') -eq 'Press Start now') "Loose drops inline tags"
Check ((L 'Tom &amp; Jerry') -eq 'Tom & Jerry') "Loose undoes XML escapes"
Check ((L 'Tom <spec_char val="&amp;"/> Jerry') -eq 'Tom & Jerry') "a spec_char becomes its character, not nothing"
Check ($hasTags.Invoke($null, [object[]]@('a <inline_tag id="0"/> b'))) "HasTags sees an inline tag"
Check (-not $hasTags.Invoke($null, [object[]]@('Tom &amp; Jerry'))) "an escape alone is not a tag"
Check (-not $hasTags.Invoke($null, [object[]]@('if a < b and c > d'))) "nor is a comparison"
Check ((L 'if a < b and c > d') -eq 'if a < b and c > d') "Loose leaves a comparison in the live link's text alone"
Check ((L 'if a &lt; b and c &gt; d') -eq 'if a < b and c > d') "  and memoQ's escaped form of it comes out the same"

$lp = 'eng-GB-dut-NL'
[void]$st.GetMethod('Clear', $B).Invoke($null, @())
[void](StagePairs @{ 'Press Start now' = 'Druk nu op Start'; 'Tom & Jerry' = 'Tom en Jerry' } $lp)

$r = Get3 'Press <inline_tag id="0"/>Start<inline_tag id="1"/> now' $lp
Check ($null -ne $r.Entry -and $r.Entry.Target -eq 'Druk nu op Start') "memoQ's tagged request finds the pair staged without tags"
Check ($r.Loose) "  and says it matched loosely"
$r = Get3 'Tom &amp; Jerry' $lp
Check ($null -ne $r.Entry) "an escaped request finds the unescaped staging"
$r = Get3 'Press Start now' 'eng-GB-ger-DE'
Check ($null -eq $r.Entry) "the loose match still respects the language pair"

# Exact wins: a pair staged in memoQ's tagged form is preferred over a loose one.
[void](StagePairs @{ 'Press <inline_tag id="0"/>Start<inline_tag id="1"/> now' = 'Druk nu op <inline_tag id="0"/>Start<inline_tag id="1"/>' } $lp)
$r = Get3 'Press <inline_tag id="0"/>Start<inline_tag id="1"/> now' $lp
Check ($r.Entry.Target -match 'inline_tag' -and -not $r.Loose) "an exact tagged pair wins over a loose one"
$r = Get3 'Unrelated text' $lp
Check ($null -eq $r.Entry) "and nothing unrelated matches"
[void]$st.GetMethod('Clear', $B).Invoke($null, @())
$r = Get3 'Tom &amp; Jerry' $lp
Check ($null -eq $r.Entry) "Clear empties the loose index too"

# The Info line says so when the tags could not be placed.
$note = $asm.GetType('Supervertaler.MemoQ.Core.BatchTranslator').GetMethod('StagedTagNote', $B)
function Note($l, $s, $t) { return $note.Invoke($null, [object[]]@([bool]$l, [string]$s, [string]$t)) }
Check ((Note $true 'a <inline_tag id="0"/> b' 'x y') -match 'tags not placed') "a loose match with untagged target is noted"
Check ((Note $false 'a <inline_tag id="0"/> b' 'x y') -eq '') "  an exact match is not"
Check ((Note $true 'a <inline_tag id="0"/> b' 'x <inline_tag id="0"/> y') -eq '') "  nor a target that carries tags"
Check ((Note $true 'Tom &amp; Jerry' 'Tom en Jerry') -eq '') "  nor a loose match on escapes alone"

$seg2 = Tool 'get_segments'
Check ($seg2.description -match 'taggedSource') "get_segments tells the agent where the tags are"
Check ((Tool 'stage_translations').description -match 'taggedSource') "and stage_translations points at it"

Write-Host ''
Write-Host "BRIDGE MODE MISS TEST COMPLETE - $fails failure(s)"
