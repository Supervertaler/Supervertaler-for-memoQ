# AutoPrompt prefers the live document, and falls back to memoQ's project domain.
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
$NonPublicStatic = [Reflection.BindingFlags]'NonPublic,Static'
$NonPublicInstance = [Reflection.BindingFlags]'NonPublic,Instance'

# ---- 1. memoQ's project domain maps onto our domain list ------------------
$bridgeType = $plugin.GetType('Supervertaler.MemoQ.Core.MemoQBridge')
$fromProject = $bridgeType.GetMethod('DomainFromProject', $NonPublicStatic)

# Order matters: memoQ's guidance is that Subject holds the subject matter and
# Domain holds the end client, so Subject must win when the two disagree.
$cases = @(
    @('Patents', $null, 'patent'),        # domain alone still works
    @($null, 'Patents', 'patent'),        # subject alone
    @('Acme Corp', 'Patents', 'patent'),  # the recommended filling: subject wins
    @('Legal', 'Patents', 'patent'),      # both usable, subject still wins
    @('Legal', $null, 'legal'),
    @('Something Else', $null, $null),    # unknown stays unknown
    @($null, $null, $null)
)
$ok = $true
foreach ($c in $cases) {
    $got = $fromProject.Invoke($null, [object[]]@($c[0], $c[1]))
    if ($got -ne $c[2]) { $ok = $false; Write-Host "    domain='$($c[0])' subject='$($c[1])': got '$got', expected '$($c[2])'" }
}
Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) project domain mapping, $($cases.Count) cases"

# ---- 2. the resolver prefers whichever source has more text --------------
$preview = $plugin.GetType('Supervertaler.MemoQ.Core.PreviewStore')
$partType = $preview.GetNestedType('Part', $NonPublicInstance)
if ($null -eq $partType) { $partType = $preview.GetNestedType('Part', [Reflection.BindingFlags]'NonPublic,Public') }

$docGuid = [Guid]::NewGuid()
$listType = [Collections.Generic.List`1].MakeGenericType(@($partType))
$parts = [Activator]::CreateInstance($listType)
foreach ($i in 1..12) {
    $p = [Activator]::CreateInstance($partType)
    $partType.GetField('PartId').SetValue($p, "mQ-default-test-$i")
    $partType.GetField('DocumentGuid').SetValue($p, $docGuid)
    $partType.GetField('DocumentName').SetValue($p, 'Live document.docx')
    $partType.GetField('SourceLangCode').SetValue($p, 'dut-NL')
    $partType.GetField('TargetLangCode').SetValue($p, 'eng-GB')
    $partType.GetField('Source').SetValue($p, "Een uitvinding betreffende een voederadditief, paragraaf $i.")
    $partType.GetField('Target').SetValue($p, '')
    $parts.Add($p)
}
$argv = [object[]]::new(1); $argv[0] = $parts
$preview.GetMethod('Upsert').Invoke($null, $argv) | Out-Null
$preview.GetMethod('NoteTool').Invoke($null, [object[]]@($true)) | Out-Null

$bridge = [Activator]::CreateInstance($bridgeType, $true)   # non-public parameterless ctor
$resolve = $bridgeType.GetMethod('ResolveAutoPromptSource', $NonPublicInstance)
$src = $resolve.Invoke($bridge, [object[]]@('no-such-capture-key'))

$srcType = $src.GetType()
$sources = $srcType.GetField('Sources').GetValue($src)
$origin = $srcType.GetField('Origin').GetValue($src)
$lang = $srcType.GetField('SourceLangCode').GetValue($src)

Write-Host "$(if ($sources.Count -eq 12) {'PASS'} else {'FAIL'}) live document used when nothing is captured: $($sources.Count) source(s), origin='$origin'"
Write-Host "$(if ($lang -eq 'dut-NL') {'PASS'} else {'FAIL'}) language codes come from the live rows: $lang"

# ---- 2b. the document CHOSEN is the one used ------------------------------
# On a project whose MT plugins the project manager has disabled, nothing is
# ever captured, so every document is live-only and the chosen key is the only
# thing telling them apart. Picking the largest would silently draft against
# the wrong document.
$otherGuid = [Guid]::NewGuid()
$parts2 = [Activator]::CreateInstance($listType)
foreach ($i in 1..30) {
    $p = [Activator]::CreateInstance($partType)
    $partType.GetField('PartId').SetValue($p, "mQ-default-other-$i")
    $partType.GetField('DocumentGuid').SetValue($p, $otherGuid)
    $partType.GetField('DocumentName').SetValue($p, 'The bigger document.docx')
    $partType.GetField('SourceLangCode').SetValue($p, 'eng-GB')
    $partType.GetField('TargetLangCode').SetValue($p, 'dut-NL')
    $partType.GetField('Source').SetValue($p, "An entirely different document, paragraph $i.")
    $partType.GetField('Target').SetValue($p, '')
    $parts2.Add($p)
}
$argv2 = [object[]]::new(1); $argv2[0] = $parts2
$preview.GetMethod('Upsert').Invoke($null, $argv2) | Out-Null
$preview.GetMethod('NoteTool').Invoke($null, [object[]]@($true)) | Out-Null

$chosen = $resolve.Invoke($bridge, [object[]]@([string]$docGuid.ToString('D')))
$chosenSources = $chosen.GetType().GetField('Sources').GetValue($chosen)
$chosenLang = $chosen.GetType().GetField('SourceLangCode').GetValue($chosen)
Write-Host "$(if ($chosenSources.Count -eq 12) {'PASS'} else {'FAIL'}) the chosen document is used, not the largest: $($chosenSources.Count) source(s) (12 expected, the other has 30)"
Write-Host "$(if ($chosenLang -eq 'dut-NL') {'PASS'} else {'FAIL'}) and its own language pair travels with it: $chosenLang"

$chosen2 = $resolve.Invoke($bridge, [object[]]@([string]$otherGuid.ToString('D')))
$chosen2Sources = $chosen2.GetType().GetField('Sources').GetValue($chosen2)
Write-Host "$(if ($chosen2Sources.Count -eq 30) {'PASS'} else {'FAIL'}) choosing the other one uses that instead: $($chosen2Sources.Count)"

# A key naming no live document falls back to the largest rather than nothing.
$fallback = $resolve.Invoke($bridge, [object[]]@([string][Guid]::NewGuid().ToString('D')))
$fallbackSources = $fallback.GetType().GetField('Sources').GetValue($fallback)
Write-Host "$(if ($fallbackSources.Count -eq 0) {'PASS'} else {'FAIL'}) a key naming no live document draws nothing rather than the wrong document: $($fallbackSources.Count)"

# ---- 3. with the tool disconnected it must not invent anything -----------
$preview.GetMethod('NoteTool').Invoke($null, [object[]]@($false)) | Out-Null
$src2 = $resolve.Invoke($bridge, [object[]]@('no-such-capture-key'))
$sources2 = $src2.GetType().GetField('Sources').GetValue($src2)
Write-Host "$(if ($sources2.Count -eq 0) {'PASS'} else {'FAIL'}) no preview tool and no capture yields nothing: $($sources2.Count)"

Write-Host ''
Write-Host 'AUTOPROMPT SOURCE TEST COMPLETE'
