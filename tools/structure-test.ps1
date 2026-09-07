# List markers as structure context, the memoQ half (#7).
#
# Core's reader is tested on the Trados side (59 checks on a synthetic .docx
# and the real document's marker sequence). What is memoQ's alone, and what is
# tested here: matching the paragraphs the preview tool holds to the paragraphs
# the reader found, by text with order as the tiebreak; a marker on the first
# segment of a paragraph only; the mode decision; the strip on every reply; and
# the rule reaching the system prompt where a prompt the plugin did not write
# still sees it.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$PluginDll = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll'

$script:probed = @{}
[AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler] {
    param($s, $e)
    $name = ($e.Name -split ',')[0]
    if ($script:probed.ContainsKey($name)) { return $null }
    $script:probed[$name] = $true
    foreach ($dir in @($MemoQPath, "$MemoQPath\Addins")) {
        $c = Join-Path $dir "$name.dll"
        if (Test-Path $c) { try { return [Reflection.Assembly]::LoadFrom($c) } catch { return $null } }
    }
    return $null
})

$plugin = [Reflection.Assembly]::LoadFrom($PluginDll)
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'
$Inst = [Reflection.BindingFlags]'Public,NonPublic,Instance'

$markersT = $plugin.GetType('Supervertaler.MemoQ.Core.StructureMarkers')
$planT = $markersT.GetNestedType('Plan', $Inst)
$paraT = $plugin.GetType('Supervertaler.Core.DocxParagraph')
$modeT = $plugin.GetType('Supervertaler.Core.StructureContextMode')
$ctxT = $plugin.GetType('Supervertaler.Core.StructureContext')
$sharedT = $plugin.GetType('Supervertaler.MemoQ.Core.SharedSettings')

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# ---- a document the way the reader would describe it ---------------------
function Para($text, $marker, $bullet = $false) {
    $p = [Activator]::CreateInstance($paraT)
    $paraT.GetProperty('Text').SetValue($p, [string]$text)
    $paraT.GetProperty('Marker').SetValue($p, $marker)
    $paraT.GetProperty('IsBullet').SetValue($p, [bool]$bullet)
    return $p
}

$paraListT = [Collections.Generic.List`1].MakeGenericType(@($paraT))
$strListT = [Collections.Generic.List`1].MakeGenericType(@([string]))

# The unary comma on each return is load-bearing: a function that returns a
# List<T> unrolls it into an Object[] on the way out, and reflection then
# refuses to pass an Object[] where the method wants the list.
function Paragraphs($items) {
    $l = [Activator]::CreateInstance($paraListT)
    foreach ($i in $items) { $l.Add($i) }
    return ,$l
}
function Strings($items) {
    $l = [Activator]::CreateInstance($strListT)
    foreach ($i in $items) { $l.Add([string]$i) }
    return ,$l
}

$match = $markersT.GetMethod('Match', $Static)
function Match($paras, $parts) {
    # @($list) would enumerate a list into N arguments; build the array by hand.
    $argv = New-Object object[] 2
    $argv[0] = $paras
    $argv[1] = $parts
    # The unary comma again: a one-element array unrolls to its element on
    # return, and $m[0] on a string is its first character - "9" for "9.".
    return ,@($match.Invoke($null, $argv))
}

# A method claim: a heading, six lettered steps, a closing clause, and the
# things a real document has around them - an empty paragraph, a bullet, an
# unnumbered paragraph.
$doc = Paragraphs @(
    (Para 'Method for inserting an object into a pipe, comprising the steps of:' '9.'),
    (Para '' $null),
    (Para 'mounting the insertion tool on the first valve;' 'a)'),
    (Para 'equalising the internal pressure;' 'b)'),
    (Para 'opening the first valve;' 'c)'),
    (Para 'embracing and centring the cable;' 'd)'),
    (Para 'fixing and sealing around the cable;' 'e)'),
    (Para 'depressurising the insertion tool;' 'f)'),
    (Para 'wherein the main pipe remains under pressure during steps a. to f.' $null),
    (Para 'Renewable energy' ([string][char]0x2022) $true),
    (Para 'Method according to claim 9, comprising the further steps of:' '10.'),
    (Para 'mounting a receiving tool on the second valve;' 'g)')
)

# ---- 1. the straightforward case ----------------------------------------
$parts = Strings @(
    'Method for inserting an object into a pipe, comprising the steps of:',
    'mounting the insertion tool on the first valve;',
    'equalising the internal pressure;',
    'opening the first valve;',
    'embracing and centring the cable;',
    'fixing and sealing around the cable;',
    'depressurising the insertion tool;',
    'wherein the main pipe remains under pressure during steps a. to f.',
    'Renewable energy',
    'Method according to claim 9, comprising the further steps of:',
    'mounting a receiving tool on the second valve;'
)
$m = Match $doc $parts
Check ($m.Count -eq 11) "one answer per preview paragraph: $($m.Count)"
Check ($m[0] -eq '9.' -and $m[1] -eq 'a)' -and $m[6] -eq 'f)') "the claim and its steps get their markers: $($m[0]) $($m[1]) .. $($m[6])"
Check ($null -eq $m[7]) "the closing clause, unnumbered, gets none"
Check ($null -eq $m[8]) "a bullet gets none - it is structure the model need not be told"
Check ($m[9] -eq '10.' -and $m[10] -eq 'g)') "the next claim continues the letters the document never restarted: $($m[10])"

# The empty document paragraph was never offered as a part, and did not shift
# anything: every marker above is on the right paragraph.

# ---- 2. what the preview tool does that the document does not -----------
# memoQ produces parts the document has no paragraph for - the alt-text of an
# image - and a wrong step there would shift every marker after it by one.
$withAltText = Strings @(
    'Method for inserting an object into a pipe, comprising the steps of:',
    'Image with diagram, sketch, line illustrations',
    'mounting the insertion tool on the first valve;',
    'equalising the internal pressure;'
)
$m2 = Match $doc $withAltText
Check ($null -eq $m2[1]) "a part the document does not have gets no marker"
Check ($m2[2] -eq 'a)' -and $m2[3] -eq 'b)') "and does not shift the ones after it: $($m2[2]) $($m2[3])"

# Order is the tiebreak, not the rule: a part that matches nothing ahead of the
# cursor leaves the cursor where it is, so the next real part still matches.
$outOfOrder = Strings @(
    'equalising the internal pressure;',
    'mounting the insertion tool on the first valve;'
)
$m3 = Match $doc $outOfOrder
Check ($m3[0] -eq 'b)') "matching walks forward to the paragraph: $($m3[0])"
Check ($null -eq $m3[1]) "and does not walk back for one that came earlier"

# ---- 3. text differences that must not break a match --------------------
$whitespace = Strings @("  Method for inserting an object into a pipe,`tcomprising the steps of:  ")
$m4 = Match $doc $whitespace
Check ($m4[0] -eq '9.') "tabs and stray spaces do not break a match: $($m4[0])"

$split = Strings @('mounting the insertion tool')
$m5 = Match $doc $split
Check ($m5[0] -eq 'a)') "a part that is the front of a paragraph still matches it (memoQ splits at a page break)"

$empty = Strings @('', '   ')
$m6 = Match $doc $empty
Check ($null -eq $m6[0] -and $null -eq $m6[1]) "empty parts get nothing and consume nothing"

# ---- 4. first segment of the paragraph only ------------------------------
# A paragraph of three sentences is three grid rows; the marker belongs on the
# first. Three markers would tell the model the step had three letters.
$plan = [Activator]::CreateInstance($planT)
$planT.GetField('Mode').SetValue($plan, [Enum]::Parse($modeT, 'Markers'))
$pairs = $planT.GetField('Paragraphs').GetValue($plan)
$kvT = [Collections.Generic.KeyValuePair`2].MakeGenericType(@([string], [string]))
$pairs.Add([Activator]::CreateInstance($kvT, @('First sentence of the step. Second sentence. Third sentence.', 'c)')))
$pairs.Add([Activator]::CreateInstance($kvT, @('A paragraph with no marker at all.', $null)))

$markerFor = $planT.GetMethod('MarkerFor', $Inst)
function MarkerFor($p, $seg) { return $markerFor.Invoke($p, [object[]]@([string]$seg)) }

Check ((MarkerFor $plan 'First sentence of the step.') -eq 'c)') "the paragraph's first segment carries the marker"
Check ($null -eq (MarkerFor $plan 'Second sentence.')) "its second segment does not"
Check ($null -eq (MarkerFor $plan 'Third sentence.')) "nor its third"
Check ($null -eq (MarkerFor $plan 'A paragraph with no marker at all.')) "a paragraph without a marker gives none"
Check ($null -eq (MarkerFor $plan '')) "an empty segment gives none"

$planT.GetField('Mode').SetValue($plan, [Enum]::Parse($modeT, 'Unavailable'))
Check ($null -eq (MarkerFor $plan 'First sentence of the step.')) "and nothing is given when the mode is not Markers"

# ---- 5. the mode decision -------------------------------------------------
# With no preview rows the answer is Unavailable, which is an answer: the model
# is told numbering is supplied by the document. Off only via the kill switch.
$generalT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings')
$secureT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSecureSettings')
$settingsT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSettings')
$engineCt = $plugin.GetType('Supervertaler.MemoQ.Core.EngineContext')
$settings = $settingsT.GetMethod('Create').Invoke($null,
    @([Activator]::CreateInstance($generalT), [Activator]::CreateInstance($secureT)))
$ctx = [Activator]::CreateInstance($engineCt, [Reflection.BindingFlags]'Instance,Public,NonPublic',
    $null, @($settings, 'dut', 'eng'), $null)

$planFor = $markersT.GetMethod('PlanFor', $Static)
function PlanFor($c) { return $planFor.Invoke($null, [object[]]@($c)) }
function ModeOf($p) { return [string]$planT.GetField('Mode').GetValue($p) }

# run-harness.ps1 restores shared.txt, so the switch can be set here.
$sharedT.GetProperty('StructureContext', $Static).SetValue($null, $true)
Check ($sharedT.GetProperty('StructureContext', $Static).GetValue($null)) "the setting reads on"
Check ((ModeOf (PlanFor $ctx)) -eq 'Unavailable') "on, with no document known: Unavailable, not Off - the fallback rule is sent"

$sharedT.GetProperty('StructureContext', $Static).SetValue($null, $false)
Check ((ModeOf (PlanFor $ctx)) -eq 'Off') "the kill switch gives Off"
$sharedT.GetProperty('StructureContext', $Static).SetValue($null, $true)

# ---- 6. the strip, on the shape of reply a batch produces -----------------
$strip = $ctxT.GetMethod('Strip', $Static)
function Strip($t) {
    $argv = New-Object object[] 2
    $argv[0] = [string]$t
    $argv[1] = $false
    $out = $strip.Invoke($null, $argv)
    return @($out, [bool]$argv[1])
}
$r = Strip '[#f)] depressurising the insertion tool;'
Check ($r[0] -eq 'depressurising the insertion tool;' -and $r[1]) "an echoed marker is removed and reported"
$r = Strip 'depressurising the insertion tool; [[TC: source ambiguous]]'
Check ($r[0] -eq 'depressurising the insertion tool; [[TC: source ambiguous]]' -and -not $r[1]) "a translator comment in double brackets is untouched"
$r = Strip '[[TC: note]] depressurising'
Check ($r[0] -eq '[[TC: note]] depressurising' -and -not $r[1]) "even one that opens the segment"

# ---- 7. the rule reaches the prompt, ahead of a prompt we did not write --
$builder = $plugin.GetType('Supervertaler.MemoQ.Core.PromptBuilder')
$general = [Activator]::CreateInstance($generalT)
function BuildBatch($mode) {
    $argv = New-Object object[] 9
    $argv[0] = $general; $argv[1] = 'eng'; $argv[2] = 'nld'; $argv[3] = $null
    $argv[4] = $null; $argv[5] = $null; $argv[6] = 'CUSTOM-PROMPT-MARKER'; $argv[7] = $null
    $argv[8] = [Enum]::Parse($modeT, $mode)
    return $builder.GetMethod('BuildForBatch', $Static).Invoke($null, $argv)
}
function SystemOf($b) { return [string]$b.GetType().GetProperty('System').GetValue($b) }

$off = SystemOf (BuildBatch 'Off')
$on = SystemOf (BuildBatch 'Markers')
$fallback = SystemOf (BuildBatch 'Unavailable')

Check (-not $off.Contains('DOCUMENT STRUCTURE')) "Off adds nothing"
Check ($on.Contains('DOCUMENT STRUCTURE') -and $on.Contains('[#')) "Markers adds the rule about the sentinel"
Check ($on.IndexOf('DOCUMENT STRUCTURE') -lt $on.IndexOf('CUSTOM-PROMPT-MARKER')) "and it comes BEFORE the custom prompt, so a prompt the plugin did not write still sees it"
Check ($fallback.Contains('supplied by the document') -and -not $fallback.Contains('[#')) "Unavailable adds the fallback rule and does not mention a sentinel"
Check ($off -eq (SystemOf (BuildBatch 'Off'))) "Off is stable from call to call - the cache prefix is unchanged"

Write-Host ''
Write-Host "STRUCTURE TEST COMPLETE - $fails failure(s)"
