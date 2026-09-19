# Chemical formulas: the fold that makes a term findable however it was written.
#
# The termbase is shared with Supervertaler for Trados, and the two products
# store the same formula differently. Trados turns sub- and superscript
# FORMATTING into real Unicode when a term is saved, so it stores ClO₃⁻; memoQ
# reads a selection through the clipboard, which arrives as plain text with the
# formatting gone, so it stores ClO3-. Without a fold, terms saved in one
# product are invisible in the other - in the same termbase, with no error and
# nothing to see. This file is the guard on that.
#
# Agreed character for character with the Trados side on 2026-09-19. If a case
# here is changed, the same change belongs there.
$ErrorActionPreference = 'Stop'
$PluginDll = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll'

$asm = [Reflection.Assembly]::LoadFrom($PluginDll)
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'

$sc = $asm.GetType('Supervertaler.MemoQ.Core.ScriptChars')
$fold = $sc.GetMethod('Fold', $Static)
$needs = $sc.GetMethod('NeedsFolding', $Static)

$index = $asm.GetType('Supervertaler.MemoQ.Core.TermIndex')
$whole = $index.GetMethod('IsWholeWord', $Static)

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}
function Fold($s) { return $fold.Invoke($null, [object[]]@($s)) }
function Whole($t, $a, $b) { return $whole.Invoke($null, [object[]]@($t, [int]$a, [int]$b)) }

function U($code) { return [char][Convert]::ToInt32($code, 16) }

# ---- 1. the digits ------------------------------------------------------
$subs = ''; $sups = ''
0..9 | ForEach-Object { $subs += U ('{0:X4}' -f (0x2080 + $_)) }
# Superscript 1, 2 and 3 are Latin-1 and sit nowhere near the others.
$sups = (U '2070') + (U '00B9') + (U '00B2') + (U '00B3')
4..9 | ForEach-Object { $sups += U ('{0:X4}' -f (0x2070 + $_)) }

Check ((Fold $subs) -eq '0123456789') "subscript digits fold to plain: $(Fold $subs)"
Check ((Fold $sups) -eq '0123456789') "superscript digits fold to plain, including the Latin-1 three: $(Fold $sups)"

# ---- 2. the signs and the dots ------------------------------------------
Check ((Fold ((U '207A') + (U '208A'))) -eq '++') "superscript and subscript plus fold to plus"
Check ((Fold ((U '207B') + (U '208B'))) -eq '--') "superscript and subscript minus fold to minus"

$dots = (U '00B7') + (U '2219') + (U '22C5') + (U '2022')
Check ((Fold $dots) -eq ([string](U '00B7')) * 4) "every radical dot folds to U+00B7"

# ---- 3. the two products' spellings meet --------------------------------
# This is the whole point of the exercise.
$trados = 'ClO' + (U '2083') + (U '207B')     # what Trados stores
$memoq  = 'ClO3-'                              # what memoQ stores
Check ((Fold $trados) -eq (Fold $memoq)) "the same ion saved in either product folds alike: '$(Fold $trados)' = '$(Fold $memoq)'"

$h2o2 = 'H' + (U '2082') + 'O' + (U '2082')
Check ((Fold $h2o2) -eq 'H2O2') "H2O2 with real subscripts folds to plain: $(Fold $h2o2)"

# ---- 4. nothing else is touched -----------------------------------------
# A fold that reached further would quietly change ordinary terminology.
$prose = "Eine Zusammensetzung, die 5 % Wasser enthält - z.B. 'Öl' (nr. 3)."
Check ((Fold $prose) -eq $prose) "ordinary text is returned unchanged"
Check ((Fold $prose).Equals($prose)) "and is the same string, not a copy"
Check (-not $needs.Invoke($null, [object[]]@($prose))) "NeedsFolding says so"
Check ($needs.Invoke($null, [object[]]@($h2o2))) "and says the opposite for a formula"

Check ((Fold '') -eq '') "an empty string folds to itself"
Check ($null -eq (Fold $null)) "null folds to null rather than throwing"

# ---- 5. one character for one character ---------------------------------
# Matching runs on the folded text while highlighting uses offsets into the
# original. That is only sound while a fold cannot change a length, so this is
# load-bearing, not a detail.
foreach ($s in @($subs, $sups, $dots, $trados, $h2o2, $prose)) {
    Check ((Fold $s).Length -eq $s.Length) "folding keeps the length ($($s.Length))"
}

# ---- 6. the bug this was written for ------------------------------------
# A one-letter term used to be accepted as a whole word inside a formula,
# because a subscript digit is not a letter or a digit as far as .NET is
# concerned, so the boundary test saw nothing on either side of the O in H₂O₂.
# Folded first, the neighbours are ordinary digits and the term is refused.
$foldedFormula = Fold $h2o2
$o = $foldedFormula.IndexOf('O')
Check (-not (Whole $foldedFormula $o ($o + 1))) "a term 'O' is refused inside a folded H2O2"
Check ((Whole $h2o2 $h2o2.IndexOf('O') ($h2o2.IndexOf('O') + 1))) `
    "and would have been accepted unfolded - the bug, still demonstrable"

# A term that IS the whole formula is still found.
Check ((Whole $foldedFormula 0 $foldedFormula.Length)) "the whole formula is a whole word"

Write-Host ''
Write-Host "SCRIPT CHARS TEST COMPLETE - $fails failure(s)"
