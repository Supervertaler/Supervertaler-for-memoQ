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

$asm = [Reflection.Assembly]::LoadFrom('D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll')
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
$src = Get-Content 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\Core\BatchTranslator.cs' -Raw
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

# ---- 4. trailing whitespace, which memoQ doubles ------------------------
# memoQ appends the source's trailing whitespace to whatever a provider returns.
# A staged target that already carries it arrives in the grid with it twice, and
# memoQ's own QA then flags the row. Observed on a production job.
$pc = $asm.GetType('Supervertaler.MemoQ.Core.StagedPairCheck')
$match = $pc.GetMethod('MatchTrailingWhitespace', $B)
function Fix($s, $t) { return $match.Invoke($null, [object[]]@([string]$s, [string]$t)) }

Check ((Fix 'Reinstall ' 'Opnieuw installeren ') -eq 'Opnieuw installeren ') "one trailing space in, one out"
Check ((Fix 'Reinstall ' 'Opnieuw installeren') -eq 'Opnieuw installeren ') "a missing trailing space is added"
Check ((Fix 'Reinstall' 'Opnieuw installeren ') -eq 'Opnieuw installeren') "an unwanted one is removed"
Check ((Fix 'Reinstall' 'Opnieuw installeren') -eq 'Opnieuw installeren') "and a clean pair is untouched"

# A target that is nothing but whitespace is not a translation; turning it into
# the source's trailing run would invent content.
Check ((Fix 'Reinstall ' '   ') -eq '   ') "an all-whitespace target is left alone"
# PowerShell turns $null into "" for a [string] parameter, so this goes through Invoke directly.
Check ($null -eq $match.Invoke($null, [object[]]@([string]'Reinstall ', $null))) "and a null target stays null"

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

Write-Host ''
Write-Host "BRIDGE MODE MISS TEST COMPLETE - $fails failure(s)"
