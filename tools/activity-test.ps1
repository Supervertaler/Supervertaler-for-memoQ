# The activity window reads the plugin's own log, so its parsing is coupled to
# text nobody writes for it.
#
# That coupling is the deliberate trade: no event buffer in the plugin to fill,
# drain, cap or leak, and no endpoint to keep in step - at the price of matching
# strings. This runs the matcher over lines taken verbatim from a real run, so a
# reworded log message fails here rather than quietly emptying the window.
#
# The one rule that must hold: an unrecognised line is shown raw, never dropped.
$ErrorActionPreference = 'Stop'
$EditorExe = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$editor = [Reflection.Assembly]::LoadFrom($EditorExe)
$form = $editor.GetType('Supervertaler.PromptEditor.ActivityForm')
$NonPublic = [Reflection.BindingFlags]'NonPublic,Instance,Static'

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# Lines copied from the 569-segment pre-translate of 2026-09-03.
$real = @(
    '[2026-09-03 23:48:55.770] [42] CreateEngine: dut-NL -> eng-GB, provider=Anthropic, model=claude-opus-5',
    '[2026-09-03 23:49:00.656] [24] TB CreateEngine: dut-NL -> eng-GB, glossary=set',
    '[2026-09-03 23:49:00.662] [24] TermIndex: loaded 162 term(s) (5 forbidden, 151 bucket(s)) from BRANTS.txt [dut-NL to eng-GB]',
    '[2026-09-03 23:51:22.456] [65] batch: 10 segment(s) sent, 10 returned | terms: 5 | recall: 0',
    '[2026-09-03 23:51:42.895] [65] batch: 10 segment(s) sent, 10 returned | terms: 21 | recall: 1',
    '[2026-09-03 23:51:22.473] [21] CreateLookupSession (ISession + ISessionWithMetadata)',
    '[2026-09-03 23:51:22.479] [21]   metadata: project=set, document=set, client=set, domain=set',
    '[2026-09-03 23:21:04.579] [1] HasCapability("AGT") -> false',
    '[2026-09-03 23:27:57.087] [25] AutoPrompt: drafted 31670 chars for a525c97f_dut-NL-eng-GB (domain patent)',
    '[2026-09-03 23:20:22.000] [44] translate: 97 src chars, 0 tag(s) -> 97 target chars, 0 tag(s) | terms: 5',
    '[2026-09-03 23:20:00.000] [47] DocumentMemory: restored 3 confirmed pair(s) from disk',
    '[2026-09-03 23:59:59.999] [9] TagBridge: XML parse failed, falling back to plain text',
    '[2026-09-03 23:59:59.999] [9] some future log line nobody has written yet'
)

# ---- the two patterns must match the real format --------------------------
$entry = $form.GetField('Entry', $NonPublic).GetValue($null)
$batch = $form.GetField('Batch', $NonPublic).GetValue($null)

$matched = @($real | Where-Object { $entry.IsMatch($_) }).Count
Check ($matched -eq $real.Count) "every real line parses as a log entry: $matched of $($real.Count)"

$batchLines = @($real | Where-Object { $batch.IsMatch(($entry.Match($_).Groups['body'].Value)) })
Check ($batchLines.Count -eq 2) "batch lines recognised: $($batchLines.Count)"

$m = $batch.Match($entry.Match($real[3]).Groups['body'].Value)
Check ($m.Groups['sent'].Value -eq '10' -and $m.Groups['back'].Value -eq '10') 'sent and returned counts are read'
Check ($m.Groups['terms'].Value -eq '5') 'glossary hits are read'
Check ($m.Groups['recall'].Value -eq '0') 'recall count is read'

# A short return shifts every translation after it, so the numbers must be
# compared rather than assumed equal.
$short = $batch.Match('batch: 10 segment(s) sent, 7 returned | terms: 3 | recall: 0')
Check ($short.Groups['sent'].Value -ne $short.Groups['back'].Value) 'a short batch is distinguishable from a complete one'

# A batch line with neither optional field still parses - the plugin does not
# always append them.
$bare = $batch.Match('batch: 4 segment(s) sent, 4 returned')
Check ($bare.Success -and -not $bare.Groups['terms'].Success) 'a batch line without terms or recall still parses'

# ---- what is hidden, and what is never hidden -----------------------------
$isDiag = $form.GetMethod('IsDiagnostic', $NonPublic)
$isProb = $form.GetMethod('IsProblem', $NonPublic)

$hidden = @($real | Where-Object { $isDiag.Invoke($null, [object[]]@($entry.Match($_).Groups['body'].Value)) })
Check ($hidden.Count -eq 3) "only the per-request diagnostics are hidden by default: $($hidden.Count)"

# The unknown line is the one that matters: a reworded or brand-new log message
# must still reach the window.
$unknownBody = $entry.Match($real[12]).Groups['body'].Value
Check (-not $isDiag.Invoke($null, [object[]]@($unknownBody))) 'an unrecognised line is not hidden'

Check ($isProb.Invoke($null, [object[]]@($entry.Match($real[11]).Groups['body'].Value))) 'a tag parse failure counts as a problem'
Check (-not $isProb.Invoke($null, [object[]]@($entry.Match($real[3]).Groups['body'].Value))) 'an ordinary batch does not'

# ---- the readable form ----------------------------------------------------
$friendly = $form.GetMethod('Friendly', $NonPublic)
$clock = $form.GetMethod('Clock', $NonPublic)

$eng = $friendly.Invoke($null, [object[]]@($entry.Match($real[0]).Groups['body'].Value))
Check ($eng -like '*dut-NL*eng-GB*claude-opus-5*') "engine line stays informative: $eng"

$gloss = $friendly.Invoke($null, [object[]]@($entry.Match($real[2]).Groups['body'].Value))
Check ($gloss -like 'Glossary*162 term*') "glossary line stays informative: $gloss"

$passthru = $friendly.Invoke($null, [object[]]@($unknownBody))
Check ($passthru -eq $unknownBody) 'an unrecognised line is passed through unchanged'

Check (($clock.Invoke($null, [object[]]@('2026-09-03 23:51:22.456'))) -eq '23:51:22') 'the timestamp is reduced to the part anyone reads'

Write-Host ''
Write-Host "ACTIVITY TEST COMPLETE - $fails failure(s)"
