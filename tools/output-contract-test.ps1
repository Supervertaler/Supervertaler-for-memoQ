# The output contract, the reply check and tag-only rows.
#
# On a live job a model appended an English note to the translator, in
# markdown, to a Dutch target, and the plugin wrote it into the grid. On the
# same run, five rows whose source was a single placeholder came back EMPTY.
# This pins the three things that now stand in the way of both: every system
# prompt ends with the contract, a reply with commentary is caught, and a row
# with nothing to translate gets its own source back.
#
# Pure checks - no model is called and nothing is written. Run it through
# tools/run-harness.ps1 like every harness.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$memoq = 'C:\Program Files\memoQ\memoQ-12'
$script:probed = @{}
[AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler] {
    param($s, $e)
    $name = ($e.Name -split ',')[0]
    if ($script:probed.ContainsKey($name)) { return $null }
    $script:probed[$name] = $true
    foreach ($dir in @($memoq, "$memoq\Addins")) {
        $c = Join-Path $dir "$name.dll"
        if (Test-Path $c) { try { return [Reflection.Assembly]::LoadFrom($c) } catch { return $null } }
    }
    return $null
})

$common = [Reflection.Assembly]::LoadFrom("$memoq\MemoQ.Addins.Common.dll")
$mt     = [Reflection.Assembly]::LoadFrom("$memoq\MemoQ.MTInterfaces.dll")
$plugin = [Reflection.Assembly]::LoadFrom('D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll')
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

$check    = $plugin.GetType('Supervertaler.MemoQ.Core.ReplyCheck')
$problem  = $check.GetMethod('Problem', $Static)
$tagDiff  = $check.GetMethod('TagDifference', $Static)
function Problem([string]$source, [string]$reply) { return $problem.Invoke($null, @($source, $reply)) }

# ---- 1. the reply check ------------------------------------------------------
# The live case, with the client's own words replaced: the translation, then a
# note in markdown, no marker, no line break.
$src = 'Installation requirements for anchor bolts (cabinet, console, chiller) (earthquake prone areas)'
$nl  = 'Installatievereisten voor ankerbouten (kast, console, koeler) (aardbevingsgevoelige gebieden)'
$note = ' Note: I followed the approved match but changed "oppervlakken" to "gebieden", since areas means regions here.'

Check ($null -ne (Problem $src ($nl + '**' + $note.Trim() + '**'))) 'the live case: translation + **Note: ...** is refused'
Check ($null -ne (Problem $src ($nl + $note))) 'the same note without markdown is still refused (first person / label)'
Check ($null -ne (Problem $src ($nl + ' I kept the approved wording.'))) 'first person alone is refused'
Check ($null -ne (Problem $src ($nl + ' (the fuzzy match said otherwise)'))) 'a remark about the match is refused'
Check ($null -eq (Problem $src $nl)) 'a clean translation passes'
Check ($null -eq (Problem 'Specification of anchor bolts' 'Specificatie van ankerbouten')) 'a clean short segment passes'

# The one sanctioned place for a comment.
Check ($null -eq (Problem $src ($nl + ' [[TC: "areas" read as regions, not surfaces. Please check.]]'))) 'one trailing [[TC]] marker passes'
Check ($null -eq (Problem $src ($nl + ' ' + [char]0x27E6 + 'TC: older bracket form' + [char]0x27E7))) 'the older bracket form passes too'
Check ($null -ne (Problem $src ($nl + ' [[TC: one]] [[TC: two]]'))) 'two markers are refused'
Check ($null -ne (Problem $src ('[[TC: first]] ' + $nl))) 'a marker that is not at the end is refused'

# Signals that are in the source are not commentary.
Check ($null -eq (Problem 'Opmerking: niet afdekken.' 'Note: do not cover.')) 'a label at the start translates a source label and passes'
Check ($null -eq (Problem 'Note: I changed the filter.' 'Note: I changed the filter.')) 'first person present in the source passes'
Check ($null -eq (Problem 'Press **Start**.' 'Druk op **Start**.')) 'markdown present in the source passes'
Check ($null -ne (Problem 'Press Start.' 'Druk op **Start**.')) 'markdown the source does not have is refused'
Check ($null -ne (Problem 'One line only.' ("Een regel.`nEn uitleg."))) 'a line break the source does not have is refused'

# Length: only for sources long enough to judge.
Check ($null -eq (Problem 'OK' 'In orde')) 'a short source is not judged by length'
Check ($null -ne (Problem $src ($nl + ' ' + ('en verder uitgelegd ' * 12)))) 'a reply far longer than the source is refused'

# Tags differ: reported, never refused.
$tagged  = '<inline_tag id="0"/>Specification of anchor bolts'
$dropped = 'Specificatie van ankerbouten'
Check ($null -eq (Problem $tagged $dropped)) 'a dropped tag is not a reason to refuse'
Check ($null -ne $tagDiff.Invoke($null, @($tagged, $dropped))) 'but the tag difference is reported'
Check ($null -eq $tagDiff.Invoke($null, @($tagged, '<inline_tag id="0"/>Specificatie van ankerbouten'))) 'matching tags report nothing'
Check ($null -eq $tagDiff.Invoke($null, @($tagged, '<inline_tag id="0"/>Specificatie [[TC: <b>check</b>]]'))) 'tags inside a [[TC]] marker are not counted'

# ---- 2. the contract is on every system prompt --------------------------------
$contract = $plugin.GetType('Supervertaler.MemoQ.Core.OutputContract')
$text     = [string]$contract.GetField('Text', $Static).GetValue($null)
$builder  = $plugin.GetType('Supervertaler.MemoQ.Core.PromptBuilder')
$settingsType = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings')
$settings = [Activator]::CreateInstance($settingsType)
$off      = [Activator]::CreateInstance($plugin.GetType('Supervertaler.Core.StructureContextMode'))
$batch    = $builder.GetMethod('BuildForBatch', $Static)

foreach ($case in @(
        @{ Name = 'a library prompt with a bank'; Instructions = 'PROMPT-TEXT'; Bank = 'BANK-TEXT' },
        @{ Name = 'a prompt with no bank';        Instructions = 'PROMPT-TEXT'; Bank = $null },
        @{ Name = 'no prompt at all (settings default)'; Instructions = $null; Bank = $null })) {
    $built  = $batch.Invoke($null, [object[]]@($settings, 'eng', 'nld', $null, $null, $null, $case.Instructions, $case.Bank, $off))
    $system = [string]$built.GetType().GetProperty('System').GetValue($built)
    Check ($system.TrimEnd().EndsWith($text.TrimEnd())) "the system prompt ends with the contract: $($case.Name)"
    if ($case.Bank) {
        Check ($system.IndexOf('BANK-TEXT') -lt $system.IndexOf('# OUTPUT CONTRACT')) '  ...after the memory bank, so nothing follows it'
    }
}
Check ($text -match '\[\[TC: ') 'the contract names the [[TC: ...]] marker'
Check ($text -notmatch [char]0x27E6) 'and not the older bracket form, which memoQ grid fonts cannot show'
Check ($text -notmatch [char]0x2014) 'and has no em dash in it'

# ---- 3. rows with nothing to translate ----------------------------------------
$nothing = $plugin.GetType('Supervertaler.MemoQ.Core.NothingToTranslate')
$in      = $nothing.GetMethod('In', $Static)
Check ([bool]$in.Invoke($null, @(''))) 'no text at all counts as nothing to translate'
Check ([bool]$in.Invoke($null, @(" - ; "))) 'punctuation and spaces count as nothing to translate'
Check (-not [bool]$in.Invoke($null, @('uCT 520'))) 'a product name is text'
Check (-not [bool]$in.Invoke($null, @('1,000.5'))) 'a number is text: its format changes by language'

# A real memoQ segment holding one placeholder and nothing else.
$ds       = 'MemoQ.Addins.Common.DataStructures'
$tagTypes = $common.GetType("$ds.InlineTagTypes")
$tagType  = $common.GetType("$ds.InlineTag")
$avpType  = $common.GetType("$ds.AttrValPair")
$sbType   = $common.GetType("$ds.SegmentBuilder")
$bridge   = $plugin.GetType('Supervertaler.MemoQ.Core.TagBridge')
$toTagged = $bridge.GetMethod('ToTaggedText', $Static)

Write-Host "  (tag types: $([string]::Join(', ', [Enum]::GetNames($tagTypes))))"
# Built by index and invoked on the constructor itself: PowerShell wraps an
# enum and flattens an empty array on the way through CreateInstance.
$ctorArgs = New-Object object[] 3
$ctorArgs[0] = [Enum]::Parse($tagTypes, 'Empty')
$ctorArgs[1] = 'inline_tag'
$ctorArgs[2] = [Array]::CreateInstance($avpType, 0)
$tag      = $tagType.GetConstructors()[0].Invoke($ctorArgs)
$sb       = [Activator]::CreateInstance($sbType)
$sbType.GetMethod('AppendInlineTag', [Type[]]@($tagType)).Invoke($sb, @($tag))
$tagOnly  = $sbType.GetMethod('ToSegment', [Type[]]@()).Invoke($sb, @())
$sourceXml = [string]$toTagged.Invoke($null, @($tagOnly))

Check ([bool]$tagOnly.IsEmptyText -and -not [bool]$tagOnly.IsEmpty) 'memoQ calls a tag-only segment text-empty but not empty - the test both paths used to answer with nothing'
Check ([bool]$nothing.GetMethod('Applies', $Static).Invoke($null, @($tagOnly))) "a tag-only segment ($sourceXml) has nothing to translate"
$result   = $nothing.GetMethod('Copy', $Static).Invoke($null, @($tagOnly))
$copied   = $result.GetType().GetField('Translation').GetValue($result)
$copiedXml = [string]$toTagged.Invoke($null, @($copied))
Check ($copiedXml -eq $sourceXml -and $copiedXml.Length -gt 0) "its translation is the same tag: '$copiedXml'"

$withText = $sbType.GetMethod('CreateFromString').Invoke($null, @('uCT 520'))
Check (-not [bool]$nothing.GetMethod('Applies', $Static).Invoke($null, @($withText))) 'a row with a product name still goes to the model'
$blank = $sbType.GetMethod('CreateFromString').Invoke($null, @(''))
Check (-not [bool]$nothing.GetMethod('Applies', $Static).Invoke($null, @($blank))) 'a truly empty row stays empty'

Write-Host ''
if ($fails -gt 0) { Write-Host "$fails FAILED"; exit 1 } else { Write-Host 'All passed.' }
