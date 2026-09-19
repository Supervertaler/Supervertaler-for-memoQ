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

$classify = $flowType.GetMethod('Classify', $Static)
$press    = $flowType.GetMethod('Press', $Inst)

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

# The honest answer when the segment cannot tell them apart. "module" is spelled
# the same in both, which is exactly the case that would otherwise be guessed at.
Check ((Classify 'module' $SRC $TGT) -eq $UNKNOWN) "a word on both sides is left unplaced"
Check ((Classify 'nowhere' $SRC $TGT) -eq $UNKNOWN) "a word on neither side is left unplaced"
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

Write-Host ''
Write-Host "QUICK TERM TEST COMPLETE - $fails failure(s)"
