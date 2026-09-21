# Two presses of Alt+Up make a term. This is the deciding, with no keyboard,
# no clipboard and no memoQ: which half a selected word came from, and what a
# press does with the half already held.
#
# The rule that earns its keep is that the ORDER DOES NOT MATTER. A translator
# reads the target cell and thinks of the source word as often as the other way
# round, and a shortcut that silently swapped the two would fill the termbase
# with backwards entries - which is the failure that costs most, because a
# backwards term still looks like a working feature.
$ErrorActionPreference = 'Stop'
$PluginDll = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll'

$asm = [Reflection.Assembly]::LoadFrom($PluginDll)
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'
$Inst   = [Reflection.BindingFlags]'Public,NonPublic,Instance'

$flowType = $asm.GetType('Supervertaler.MemoQ.Core.QuickTermFlow')
$sideType = $asm.GetType('Supervertaler.MemoQ.Core.TermSide')
$stepType = $asm.GetType('Supervertaler.MemoQ.Core.QuickTermStep')

# Both have overloads now, so they are picked by parameter count rather than by
# name - GetMethod throws on an ambiguous match.
$classify = $flowType.GetMethods($Static) | Where-Object { $_.Name -eq 'Classify' -and $_.GetParameters().Count -eq 3 }
$press    = $flowType.GetMethods($Inst)   | Where-Object { $_.Name -eq 'Press'    -and $_.GetParameters().Count -eq 3 }

$SOURCE  = [Enum]::Parse($sideType, 'Source')
$TARGET  = [Enum]::Parse($sideType, 'Target')
$UNKNOWN = [Enum]::Parse($sideType, 'Unknown')

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

function NewFlow { return [Activator]::CreateInstance($flowType) }
function Classify($text, $src, $tgt) { return $classify.Invoke($null, [object[]]@($text, $src, $tgt)) }
function Press($flow, $text, $side, $at) { return $press.Invoke($flow, [object[]]@($text, $side, $at)) }
function Step($o) { return $o.GetType().GetField('Step').GetValue($o).ToString() }
function Val($o, $name) { return $o.GetType().GetField($name).GetValue($o) }

$NOW = [DateTime]::UtcNow
$SRC = 'Een elektrische module met een draagarm.'
$TGT = 'An electric module with a support arm.'

# ---- 1. which half a word came from -------------------------------------
Check ((Classify 'draagarm' $SRC $TGT) -eq $SOURCE) "a Dutch word is placed in the source"
Check ((Classify 'support arm' $SRC $TGT) -eq $TARGET) "an English one is placed in the target"
Check ((Classify 'DRAAGARM' $SRC $TGT) -eq $SOURCE) "case does not matter"
Check ((Classify '  draagarm  ' $SRC $TGT) -eq $SOURCE) "nor does surrounding space"

# The two uncertain cases. Both used to come back Unknown, which threw away the
# difference between them and left press order to decide - and press order put a
# real term in backwards on 2026-09-21. They are now placed weakly instead, and
# section 8 covers the case that produced the bug.
#
# "module" is spelled the same in both languages, so it is on both sides: still a
# word of the source. "nowhere" is on neither, which in practice means the live
# view has not caught up with the cell being edited, and that cell is the target.
Check ((Classify 'module' $SRC $TGT) -eq $SOURCE) "a word on both sides is taken as the source"
Check ((Classify 'nowhere' $SRC $TGT) -eq $TARGET) "a word on neither side is taken as the target"

# With no segment at all there is genuinely nothing to reason from, and that
# remains Unknown rather than being dressed up as a placement.
Check ((Classify 'draagarm' $null $null) -eq $UNKNOWN) "with no segment to consult, nothing is claimed"
Check ((Classify '' $SRC $TGT) -eq $UNKNOWN) "an empty selection is unplaced"

# ---- 2. two presses, in either order ------------------------------------
$f = NewFlow
$a = Press $f 'draagarm' $SOURCE $NOW
Check ((Step $a) -eq 'Awaiting') "the first press holds the word"
Check ((Val $a 'Wanted') -eq $TARGET) "and asks for the other half"

$b = Press $f 'support arm' $TARGET $NOW
Check ((Step $b) -eq 'Ready') "the second press completes the term"
Check ((Val $b 'Source') -eq 'draagarm' -and (Val $b 'Target') -eq 'support arm') `
    "source and target the right way round: $(Val $b 'Source') -> $(Val $b 'Target')"

# The same two words, pressed the other way about, must come out the same way.
$f = NewFlow
$null = Press $f 'support arm' $TARGET $NOW
$b = Press $f 'draagarm' $SOURCE $NOW
Check ((Val $b 'Source') -eq 'draagarm' -and (Val $b 'Target') -eq 'support arm') `
    "target first gives the same term: $(Val $b 'Source') -> $(Val $b 'Target')"

# ---- 3. when the segment cannot place a word ----------------------------
# One side known is enough: the other is whatever this one is not.
$f = NewFlow
$null = Press $f 'module' $UNKNOWN $NOW
$b = Press $f 'elektrische module' $SOURCE $NOW
Check ((Val $b 'Source') -eq 'elektrische module' -and (Val $b 'Target') -eq 'module') `
    "a known half places the unplaced one: $(Val $b 'Source') -> $(Val $b 'Target')"

# Neither known: the press order decides, which is the order the dialog reads in.
$f = NewFlow
$null = Press $f 'module' $UNKNOWN $NOW
$b = Press $f 'modul' $UNKNOWN $NOW
Check ((Val $b 'Source') -eq 'module' -and (Val $b 'Target') -eq 'modul') `
    "with neither placed, press order decides: $(Val $b 'Source') -> $(Val $b 'Target')"

# ---- 4. changing your mind about one half -------------------------------
# Two presses on the same side are not a term. Pairing a word with itself is
# the kind of entry that is worse than none, so the second replaces the first.
$f = NewFlow
$null = Press $f 'draagarm' $SOURCE $NOW
$b = Press $f 'elektrische module' $SOURCE $NOW
Check ((Step $b) -eq 'Awaiting') "a second source press replaces rather than pairs"
Check ((Val $b 'Held') -eq 'elektrische module') "and holds the newer word: $(Val $b 'Held')"

$c = Press $f 'electric module' $TARGET $NOW
Check ((Val $c 'Source') -eq 'elektrische module' -and (Val $c 'Target') -eq 'electric module') `
    "the replaced word is the one that is used: $(Val $c 'Source') -> $(Val $c 'Target')"

# ---- 5. nothing selected -------------------------------------------------
$f = NewFlow
Check ((Step (Press $f '' $UNKNOWN $NOW)) -eq 'Nothing') "an empty press does nothing"
Check ((Step (Press $f '   ' $UNKNOWN $NOW)) -eq 'Nothing') "nor does a press on whitespace"
$b = Press $f 'draagarm' $SOURCE $NOW
Check ((Step $b) -eq 'Awaiting') "and neither one left anything behind"

# ---- 6. a half-finished term does not wait for ever ---------------------
# Pressing once, going to lunch and pressing again somewhere else must not make
# a term out of two unrelated words.
$f = NewFlow
$null = Press $f 'draagarm' $SOURCE $NOW
$stale = Press $f 'something else' $TARGET $NOW.AddMinutes(10)
Check ((Step $stale) -eq 'Awaiting') "a press ten minutes later starts again rather than pairing"
Check ((Val $stale 'Held') -eq 'something else') "holding the new word: $(Val $stale 'Held')"

# Inside the window it still pairs, so the timeout is not merely always on.
$f = NewFlow
$null = Press $f 'draagarm' $SOURCE $NOW
Check ((Step (Press $f 'support arm' $TARGET $NOW.AddSeconds(30))) -eq 'Ready') "half a minute later is still the same term"

# ---- 7. Forget ----------------------------------------------------------
# Called when the project changes: a term started in one job must not finish in
# another, because it would be written to the new job's termbase.
$f = NewFlow
$null = Press $f 'draagarm' $SOURCE $NOW
$f.GetType().GetMethod('Forget', $Inst).Invoke($f, @())
Check (-not $f.GetType().GetProperty('IsHolding', $Inst).GetValue($f)) "Forget drops the half-finished term"
Check ((Step (Press $f 'support arm' $TARGET $NOW)) -eq 'Awaiting') "so the next press starts a new one"

# ---- 8. the case that put a term in backwards -------------------------
# 2026-09-21, a live job. The source cell read "Coronary Artery Disease, DCB:"
# and the target "Coronary Artery Disease (kransslagaderaandoening), DCB:". So
# the English term appeared on BOTH sides, and the Dutch one had just been typed
# and had not reached the live view, so it appeared on NEITHER. Both came back
# unplaced, press order decided, Michael works target-first, and the pair went in
# reversed.
#
# Neither of those two cases is uninformative, and they are no longer treated
# alike: on both sides it is still a word of the source; on neither side the
# live view is stale, and the cell being edited is the target.
$classify4 = $flowType.GetMethods($Static) | Where-Object { $_.Name -eq 'Classify' -and $_.GetParameters().Count -eq 4 }

function Place($text, $src, $tgt) {
    $a = New-Object object[] 4
    $a[0] = $text; $a[1] = $src; $a[2] = $tgt; $a[3] = $false
    $side = $classify4.Invoke($null, $a)
    return @{ Side = $side; Strong = $a[3] }
}

$SRC2 = 'Coronary Artery Disease, DCB:'
$TGT2 = 'Coronary Artery Disease (kransslagaderaandoening), DCB:'

$both = Place 'Coronary Artery Disease' $SRC2 $TGT2
Check ($both.Side -eq $SOURCE) "a word on BOTH sides is taken as the source"
Check (-not $both.Strong) "  but weakly, since the segment did not settle it"

$neither = Place 'kransslagaderaandoening' $SRC2 ''
Check ($neither.Side -eq $TARGET) "a word on NEITHER side is taken as the target - the stale cell is the one being edited"
Check (-not $neither.Strong) "  also weakly"

# A word on one side only is still read, and confidently.
Check ((Place 'DCB' $SRC2 '').Side -eq $SOURCE) "a word only in the source is still the source"
Check ((Place 'DCB' $SRC2 '').Strong) "  and confidently"

# And the pair now comes out the right way round in the order he actually works.
$f = NewFlow
$press4 = $flowType.GetMethods($Inst) | Where-Object { $_.Name -eq 'Press' -and $_.GetParameters().Count -eq 4 }
function Press4($flow, $text, $side, $strong, $at) {
    return $press4.Invoke($flow, [object[]]@($text, $side, [bool]$strong, $at))
}

$null = Press4 $f 'kransslagaderaandoening' $TARGET $false $NOW
$b = Press4 $f 'Coronary Artery Disease' $SOURCE $false $NOW
Check ((Step $b) -eq 'Ready') "target-first still completes the term"
Check ((Val $b 'Source') -eq 'Coronary Artery Disease' -and (Val $b 'Target') -eq 'kransslagaderaandoening') `
    "and the right way round this time: $(Val $b 'Source') -> $(Val $b 'Target')"
Check ((Val $b 'Guessed')) "flagged as inferred, so the dialog warns and offers to swap"

# A pair read confidently off the segment is NOT flagged, or the warning would
# appear on every add and stop being read.
$f = NewFlow
$null = Press4 $f 'draagarm' $SOURCE $true $NOW
$b = Press4 $f 'support arm' $TARGET $true $NOW
Check (-not (Val $b 'Guessed')) "a pair read from the segment is not flagged as a guess"

Write-Host ''
Write-Host "QUICK TERM TEST COMPLETE - $fails failure(s)"
