# Fitting a name into the job panel's column.
#
# The rule that matters is what it does NOT do. Most names are short enough to
# show whole, and a prompt and a glossary are often named nothing like each
# other or like the project - so a rule that always trimmed would mangle the
# ordinary case to tidy the awkward one. Shortening happens only when something
# genuinely does not fit, and the first thing dropped is the project name that
# every row repeats.
$ErrorActionPreference = 'Stop'
$EditorExe = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe'

Add-Type -AssemblyName System.Windows.Forms

$editor = [Reflection.Assembly]::LoadFrom($EditorExe)
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'

$label = $editor.GetType('Supervertaler.PromptEditor.JobLabel')
$fit = $label.GetMethod('Fit', $Static)
$sharedPrefix = $label.GetMethod('SharedPrefix', $Static)

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# One pixel per character, so the widths in these cases read as character counts.
$measure = [Func[string, int]] { param($s) $s.Length }

function Fit($value, $project, $width) {
    return $fit.Invoke($null, [object[]]@($value, $project, [int]$width, $measure))
}

$PROJECT = 'Acme (PROJ-001-AA-BB, PROJ-001-AA-CC)'

# ---- 1. what fits is left alone -----------------------------------------
# This is the common case and the one a clever rule would spoil.
Check ((Fit 'Acme v2' $PROJECT 40) -eq 'Acme v2') "a short name is untouched"
Check ((Fit 'patent eng-dut' $PROJECT 40) -eq 'patent eng-dut') "a name unlike the project is untouched"
Check ((Fit "$PROJECT v3" $PROJECT 80) -eq "$PROJECT v3") "even a long name is untouched when it fits"
Check ((Fit '' $PROJECT 40) -eq '') "an empty value stays empty"

# ---- 2. the project name is the first thing dropped ---------------------
# Because the panel names the project directly above these rows, repeating it
# spends the whole column on the one word the reader does not need.
Check ((Fit "$PROJECT v3" $PROJECT 10) -eq ([char]0x2026 + ' v3')) `
    "the repeated project name goes first: $(Fit "$PROJECT v3" $PROJECT 10)"
Check ((Fit "$PROJECT v3.txt" $PROJECT 12) -eq ([char]0x2026 + ' v3.txt')) `
    "and again for the glossary: $(Fit "$PROJECT v3.txt" $PROJECT 12)"

# The bank is the same words in a folder slug, which has to be recognised as
# the same name or the one row that could be shortened never is.
$slug = 'acme-proj-001-aa-bb-proj-001-aa-cc-v3'
Check ((Fit $slug $PROJECT 12) -eq ([char]0x2026 + ' v3')) "a bank slug matches the project it was named after: $(Fit $slug $PROJECT 12)"

# ---- 3. what it refuses to do -------------------------------------------
# A value that merely starts like the project shares nothing worth dropping:
# cutting "Acme (PROJ-001-AA-B" off "…-BX" leaves a fragment that reads as a
# different case number.
$other = 'Acme (PROJ-001-AA-BX) v1'
Check ((Fit $other $PROJECT 12).StartsWith([char]0x2026 + ' ') -eq $false) `
    "a different case number is not treated as the project: $(Fit $other $PROJECT 12)"

# Nothing left after the project name is worse than a middle cut - a bare
# ellipsis says less than a shortened name does.
$exact = 'acme-proj-001-aa-bb-proj-001-aa-cc'
$got = Fit $exact $PROJECT 12
Check ($got -ne ([char]0x2026) -and $got.Length -gt 1) "a bank named exactly after the project still shows something: $got"

Check ((Fit "$PROJECT v3" '' 12).Contains([char]0x2026)) "with no project known, it still fits by cutting"

# ---- 4. cutting from the middle keeps both ends -------------------------
# For these names that is the client at one end and the version at the other,
# which is what tells two prompts for the same job apart.
$long = 'somethingentirelyunlikeanything v7'
$got = Fit $long $PROJECT 20
Check ($got.Length -le 20) "a middle cut respects the width: '$got' is $($got.Length)"
Check ($got.StartsWith('s') -and $got.EndsWith('7')) "and keeps the front and the version: $got"
Check ($got.Contains([char]0x2026)) "and says that it was cut"

# ---- 5. no width, no crash ----------------------------------------------
# The panel measures before it has been laid out, so this is a real call.
Check ((Fit "$PROJECT v3" $PROJECT 0) -eq "$PROJECT v3") "a zero width returns the value rather than throwing"
Check ((Fit 'x' $PROJECT 1) -eq 'x') "a single character survives a single pixel"

# ---- 6. the prefix test on its own ---------------------------------------
Check ($sharedPrefix.Invoke($null, [object[]]@("$PROJECT v3", $PROJECT)) -gt 0) "prefix found when the name starts with the project"
Check ($sharedPrefix.Invoke($null, [object[]]@('patent eng-dut', $PROJECT)) -eq 0) "no prefix when the name is unrelated"
Check ($sharedPrefix.Invoke($null, [object[]]@("$PROJECT v3", '')) -eq 0) "no prefix when no project is known"
Check ($sharedPrefix.Invoke($null, [object[]]@('Acme (PROJ', $PROJECT)) -eq 0) "no prefix when the value is shorter than the project"


# ---- 7. a row that can be reworded shortens its words before its name ----
# The Termbases row summarises a set, and its project termbase is usually named
# after the project - so the rule above, drop the repeated project name, turned
# the whole line into an ellipsis and left the reader with nothing. A row may
# now offer the same line in several wordings, longest first.
$best = $label.GetMethod('Best', $Static)
function Best($candidates, $project, $width) {
    $list = [Collections.Generic.List[string]]::new()
    foreach ($c in $candidates) { $list.Add([string]$c) }
    return $best.Invoke($null, [object[]]@($list, $project, [int]$width, $measure))
}

$NAME = 'BRANTS (INOP-003-BE)'
$wordings = @(
    "$NAME (project) + 2 background    ·    1 of 3 to the model",
    "$NAME (project) + 2 more    ·    1 of 3 to the model",
    "$NAME + 2 more    ·    1 of 3 to the model",
    "$NAME + 2 more · 1 of 3 AI"
)

Check ((Best $wordings $NAME 200) -eq $wordings[0]) "the fullest wording is shown when it fits"
# The four wordings are 71, 65, 55 and 41 characters, and one character is one
# pixel here, so each width below is just wide enough for the wording named.
Check ((Best $wordings $NAME 70) -eq $wordings[1]) "'background' is spent first: $(Best $wordings $NAME 70)"
Check ((Best $wordings $NAME 60) -eq $wordings[2]) "'(project)' goes next: $(Best $wordings $NAME 60)"
Check ((Best $wordings $NAME 50) -eq $wordings[3]) "then the spacing and 'to the model': $(Best $wordings $NAME 50)"

# The name survives every rewording. This is the whole point of the change:
# before it, the row read "ELLIPSIS (project) + 2 background" and named nothing.
foreach ($w in 200, 70, 60, 50) {
    Check ((Best $wordings $NAME $w).StartsWith('BRANTS')) "the termbase is still named at $w px"
}

# Only when no wording fits does it cut, and it cuts the shortest one. The row
# passes no project name to cut against - see JobPanel.Field.DropsProjectName -
# because dropping the leading name is exactly what must not happen here: this
# name is the project termbase's, not a repeat of the heading, and a row reading
# "ELLIPSIS 2 more" names nothing. A middle cut keeps the front.
$tight = Best $wordings '' 30
Check ($tight.Length -le 30) "a width narrower than every wording still fits: '$tight'"
Check ($tight.Contains([char]0x2026)) "and says it was cut: $tight"
Check ($tight.StartsWith('BRANT')) "and the name still starts the row when cut: $tight"

# One candidate is the old behaviour exactly, which is what every other row passes.
Check ((Best @("$PROJECT v3") $PROJECT 10) -eq (Fit "$PROJECT v3" $PROJECT 10)) "a single wording behaves as before"

# Degenerate inputs: the panel measures before layout and fields start empty.
Check ((Best @() $PROJECT 40) -eq '') "no wordings at all returns empty rather than throwing"
Check ((Best @('', '  ') $PROJECT 40) -eq '') "blank wordings are skipped"
Check ((Best @('', 'BEIJER') $PROJECT 40) -eq 'BEIJER') "a blank among real ones is skipped, not chosen"

Write-Host ''
Write-Host "JOB PANEL TEST COMPLETE - $fails failure(s)"
