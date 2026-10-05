# A line break inside one segment's translation reaches memoQ as the model
# wrote it, with no CR added.
#
# Core's batch parser joined a segment's lines with AppendLine - CR LF on
# Windows - so every multi-line batch translation carried a CR its source did
# not have (core 402ab02, found in Trados, where a CR in a target is saved as a
# hard return). memoQ's SegmentXMLConverter keeps line-break characters
# verbatim, so whatever the parser hands over is what lands in the grid. This
# pins the whole memoQ path: reply -> ParseBatchResponse -> TagBridge -> Segment.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$PluginDll = 'D:\SynologyDrive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll'

$script:probed = @{}
[AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler] {
    param($s, $e)
    $name = ($e.Name -split ',')[0]
    if ($script:probed.ContainsKey($name)) { return $null }
    $script:probed[$name] = $true
    foreach ($dir in @($MemoQPath, "$MemoQPath\Addins")) {
        $candidate = Join-Path $dir "$name.dll"
        if (Test-Path $candidate) { try { return [Reflection.Assembly]::LoadFrom($candidate) } catch { return $null } }
    }
    return $null
})

$common = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.Addins.Common.dll")
$plugin = [Reflection.Assembly]::LoadFrom($PluginDll)
$B = [Reflection.BindingFlags]'Public,NonPublic,Static'

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}
function Codes($s) {
    ($s.ToCharArray() | ForEach-Object { if ([int]$_ -lt 32) { 'U+{0:X4}' -f [int]$_ } else { [string]$_ } }) -join ''
}

$builder = $common.GetType('MemoQ.Addins.Common.DataStructures.SegmentBuilder')
$parse = $plugin.GetType('Supervertaler.Core.TranslationPrompt').GetMethods($B) |
    Where-Object { $_.Name -eq 'ParseBatchResponse' -and $_.GetParameters()[1].ParameterType -eq [int] }
$fromTagged = $plugin.GetType('Supervertaler.MemoQ.Core.TagBridge').GetMethod('FromTaggedText', $B)

# A reply as a model on Windows can send it: CR LF throughout, segment 1 on two lines.
$reply = "1. Eerste regel`r`nTweede regel`r`n2. Volgende zin."
$parsed = @($parse.Invoke($null, [object[]]@($reply, 2)))
$first = $parsed[0].Translation

Check ($parsed.Count -eq 2) "the reply parses into two segments ($($parsed.Count))"
Check ($first -eq "Eerste regel`nTweede regel") "segment 1 keeps its line break as a plain LF: $(Codes $first)"
Check ($parsed[1].Translation -notmatch "`r") "and segment 2 carries no stray CR"

$source = $builder.GetMethod('CreateFromString').Invoke($null, @("First line`nSecond line"))
$seg = $fromTagged.Invoke($null, [object[]]@($first, $source))
Check ($seg.PlainText -eq "Eerste regel`nTweede regel") "memoQ's segment gets exactly that, LF and no CR: $(Codes $seg.PlainText)"

Write-Host ''
Write-Host "LINE BREAK TEST COMPLETE - $fails failure(s)"
