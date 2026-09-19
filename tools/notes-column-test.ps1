# A prompt's term table becomes a termbase: does every term, and every note,
# survive the journey?
#
# The note is the only part of a locked-terms table that says WHY a term was
# chosen - "only in the claims", "never in this sense". A termbase built from a
# prompt without it keeps the decision and loses the reasoning, which is the
# half that ages worst.
#
# The alternatives cases below are error handling, not a supported notation. The
# generator is told that a locked target is the single binding rendering and
# that a collocation gets its own row, so a cell holding "a / b" is the model
# disobeying. It happens, and what the extractor did about it used to lose data.
$ErrorActionPreference = 'Stop'
$exe = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe'

$asm = [Reflection.Assembly]::LoadFrom($exe)
$B = [Reflection.BindingFlags]'Public,NonPublic,Static'

$split = $asm.GetType('Supervertaler.PromptEditor.MainForm').GetMethod('SplitAlternates', $B)
$extract = $asm.GetType('Supervertaler.Core.PromptGlossaryExtractor').GetMethod('Extract', $B)

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

$prompt = @"
## Locked terms

| Source | Target | Notes |
|---|---|---|
| draagarm | support arm | only in the claims |
| inrichting / werkwijze | device / method | never "apparatus" |
| koppeling | coupling |  |
"@

$entries = $extract.Invoke($null, [object[]]@([string]$prompt))
$rows = $split.Invoke($null, [object[]]@(, $entries))

foreach ($r in $rows) {
    Write-Host ("  row: {0,-22} -> {1,-14} forbidden={2}  notes='{3}'" -f $r.Source, $r.Target, $r.Forbidden, $r.Notes)
}
Write-Host ''

# ---- 1. the note reaches the row ----------------------------------------
$arm = $rows | Where-Object { $_.Source -eq 'draagarm' }
Check ($arm.Notes -eq 'only in the claims') "a note reaches the termbase row"

$coupling = $rows | Where-Object { $_.Source -eq 'koppeling' }
Check ($coupling.Notes -eq '') "an empty note stays empty rather than becoming null"

# ---- 2. alternatives in one row become one term each --------------------
# The extractor collapsed the target to its first alternative BEFORE anything
# looked at the source. So "inrichting / werkwijze | device / method" left a
# source holding a slash and a target holding one word: one row was stored whose
# source was the literal "inrichting / werkwijze" - a term no segment can
# contain - and "method" survived only inside a note. One dead row, one
# alternative lost. Pairing happens first now.
#
# Fixed in core on 2026-09-19. The Trados side has no consumer of this
# extractor, so those dead rows were only ever this product's.
#
# -not Forbidden, because the same two sources also carry a banned rendering.
$device = $rows | Where-Object { $_.Source -eq 'inrichting' -and -not $_.Forbidden }
$method = $rows | Where-Object { $_.Source -eq 'werkwijze' -and -not $_.Forbidden }

Check ($null -ne $device -and $null -ne $method) "a row listing alternatives on both sides becomes two terms"
Check ($device.Target -eq 'device') "paired positionally, not collapsed: inrichting -> $($device.Target)"
Check ($method.Target -eq 'method') "and the second alternative survives: werkwijze -> $($method.Target)"
Check ($device.Notes -eq $method.Notes) "with the same note on both"
Check (@($rows | Where-Object { $_.Source -like '* / *' }).Count -eq 0) "and no row is left holding a slash in its source"

# ---- 3. a ban applies to every term the row produced --------------------
# The first version of the pairing returned early from each branch and silently
# took the "never use X" handling with it, so rows with alternatives - the ones
# where a ban matters most - stopped generating a forbidden term at all. Caught
# by this test, which is the reason it asserts a count rather than existence.
$banned = @($rows | Where-Object { $_.Forbidden })

Check ($banned.Count -eq 2) "a never-use note bans the rendering for BOTH terms the row produced ($($banned.Count))"
Check ((@($banned | Where-Object { $_.Source -eq 'inrichting' }).Count -eq 1) -and
       (@($banned | Where-Object { $_.Source -eq 'werkwijze' }).Count -eq 1)) `
    "one for each source: $(($banned | ForEach-Object { $_.Source }) -join ', ')"
Check (@($banned | Where-Object { $_.Target -ne 'apparatus' }).Count -eq 0) "both banning the rendering the prompt refused"
Check ($banned[0].Notes.Length -gt 0) "and carrying a note saying where the ban came from: '$($banned[0].Notes)'"

# ---- 4. counts that do not match ----------------------------------------
# Three sources against two targets. Neither list can be paired, so each source
# takes the target - but the target is itself a list and has to be collapsed
# first, exactly as the one-source case does it.
#
# The first version of the pairing did not collapse here, so every source was
# given the uncollapsed "x / y" as its target and nothing recorded that
# alternatives had been listed: slash-bearing TARGETS, which is the one thing
# the code being replaced never produced. Found by the core owner reviewing the
# commit, in the one branch the new structure did not name.
$odd = $extract.Invoke($null, [object[]]@([string]@"
| Source | Target | Notes |
|---|---|---|
| a / b / c | x / y |  |
"@))

Check ($odd.Count -eq 3) "three sources against two targets gives three terms ($($odd.Count))"
Check (@($odd | Where-Object { $_.Target -like '* / *' }).Count -eq 0) "none of them carrying a slash in the target"
Check (@($odd | Where-Object { $_.Target -eq 'x' }).Count -eq 3) "all taking the first rendering as binding"
Check (@($odd | Where-Object { $_.Note -like '*listed alternatives*' }).Count -eq 3) "and all recording what the prompt actually listed"

Write-Host ''
Write-Host "NOTES COLUMN TEST COMPLETE - $fails failure(s)"
