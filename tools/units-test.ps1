# Two things the plugin used to state confidently and wrongly (#4, #2).
#
# #4: the AutoPrompt dialog and the meta-prompt said "170 segments" of a
# document that had 370, because the live-document link hands over paragraphs
# and nothing labelled them. The count reaches the model too - SEGMENT COUNT in
# the ANALYSIS RESULTS block - so it was told the job was half its size when
# deciding how much to lock.
#
# #2: the only log line naming a model was written when memoQ built the engine
# and never revised, so after a switch in the settings the Activity window kept
# naming the old one for the rest of the session. Nothing was translated by the
# wrong model; the diagnostic was simply stale.
#
# No network, no log file: the label and the change-detection are pure.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$PluginDll = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll'
$EditorExe = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe'

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

Add-Type -AssemblyName System.Windows.Forms
$plugin = [Reflection.Assembly]::LoadFrom($PluginDll)
$editor = [Reflection.Assembly]::LoadFrom($EditorExe)
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# ---- #4: the meta-prompt says what it counted -----------------------------
$ctxT = $plugin.GetType('Supervertaler.Core.PromptGenerationContext')
$gen = $plugin.GetType('Supervertaler.Core.PromptGenerator')
$build = $gen.GetMethod('BuildMetaPrompt', $Static)

function MetaPrompt($count, $unit) {
    $ctx = [Activator]::CreateInstance($ctxT)
    $ctxT.GetProperty('SourceLang').SetValue($ctx, 'dut')
    $ctxT.GetProperty('TargetLang').SetValue($ctx, 'eng')
    $ctxT.GetProperty('DetectedDomain').SetValue($ctx, 'patent')
    $ctxT.GetProperty('SegmentCount').SetValue($ctx, [int]$count)
    if ($null -ne $unit) { $ctxT.GetProperty('SegmentUnit').SetValue($ctx, [string]$unit) }
    $argv = New-Object object[] 1
    $argv[0] = $ctx
    return [string]$build.Invoke($null, $argv)
}

$default = MetaPrompt 370 $null
Check ($default.Contains('SEGMENT COUNT: 370')) "with no unit given, the meta-prompt counts segments - Trados's case"
Check (-not $default.Contains('PARAGRAPH')) "and says nothing about paragraphs"

$live = MetaPrompt 170 'paragraphs'
Check ($live.Contains('PARAGRAPH COUNT: 170')) "from the live document it counts paragraphs: the model is no longer told 170 segments"
Check (-not $live.Contains('SEGMENT COUNT')) "and the wrong label is gone, not merely joined by the right one"

$one = MetaPrompt 1 'paragraphs'
Check ($one.Contains('PARAGRAPH COUNT: 1')) "one paragraph is not 'paragraphs'"

$blank = MetaPrompt 12 ''
Check ($blank.Contains('SEGMENT COUNT: 12')) "an empty unit falls back to segments rather than to a blank label"

# ---- #4: the dialog's line ----------------------------------------------
# Counted lives on the dialog's class in the editor; found by its name rather
# than by the class's, so a rename of the form does not break the test.
$counted = $null
foreach ($t in $editor.GetTypes()) {
    $m = $t.GetMethod('Counted', $Static)
    if ($m) { $counted = $m; break }
}
Check ($null -ne $counted) "the editor has the label helper"

function Label($n, $unit) { return [string]$counted.Invoke($null, [object[]]@([int]$n, [string]$unit)) }
Check ((Label 170 'paragraphs') -eq '170 paragraphs') "the dialog says '170 paragraphs' for the live document: $(Label 170 'paragraphs')"
Check ((Label 9 'segments') -eq '9 segments') "and '9 segments' for captured segments"
Check ((Label 1 'segments') -eq '1 segment') "singular when there is one"
Check ((Label 1250 '') -eq '1,250 segments') "no unit from an older bridge still reads as segments, with thousands grouped: $(Label 1250 '')"

# ---- #2: the model line is written on change, not per batch --------------
$log = $plugin.GetType('Supervertaler.MemoQ.Core.PluginLog')
$changed = $log.GetMethod('ModelChanged', $Static)
function Changed($p, $m) { return [bool]$changed.Invoke($null, [object[]]@([string]$p, [string]$m)) }

Check ((Changed 'Anthropic' 'claude-opus-5')) "the first request of a session names its model"
Check (-not (Changed 'Anthropic' 'claude-opus-5')) "the next request with the same model writes nothing - forty batches, one line"
Check ((Changed 'Anthropic' 'claude-fable-5-1')) "switching models in the settings shows up on the very next request"
Check (-not (Changed 'Anthropic' 'claude-fable-5-1')) "and only once"
Check ((Changed 'OpenAI' 'claude-fable-5-1')) "a provider change counts as a change even with the model name unchanged"
Check ((Changed 'Anthropic' ' claude-opus-5 ')) "whitespace around a name does not hide a real change back"
Check (-not (Changed 'Anthropic' 'claude-opus-5')) "nor does it invent one"

Write-Host ''
Write-Host "UNITS TEST COMPLETE - $fails failure(s)"
