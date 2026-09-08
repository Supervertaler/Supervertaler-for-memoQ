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

# ---- 2c. a memoQ view: several files in one tab ---------------------------
# Measured over the live bridge on a real three-file view: memoQ reports it as
# THREE documents - 82, 10 and 27 paragraphs - each with its own DocumentGuid,
# its own import path AND its own view guid in its part ids. Nothing marks them
# as one tab, so the caller names the set and the bridge concatenates it.
$setDocs = @([Guid]::NewGuid(), [Guid]::NewGuid(), [Guid]::NewGuid())
$sizes = @(4, 2, 3)
for ($d = 0; $d -lt 3; $d++) {
    $setParts = [Activator]::CreateInstance($listType)
    foreach ($i in 1..$sizes[$d]) {
        $p = [Activator]::CreateInstance($partType)
        # Its OWN view guid, as memoQ really sends - not one shared across the view.
        $partType.GetField('PartId').SetValue($p, "mQ-default-$([Guid]::NewGuid())-$i")
        $partType.GetField('DocumentGuid').SetValue($p, $setDocs[$d])
        $partType.GetField('DocumentName').SetValue($p, "File $d.docx")
        $partType.GetField('SourceLangCode').SetValue($p, 'eng-GB')
        $partType.GetField('TargetLangCode').SetValue($p, 'dut-NL')
        $partType.GetField('Source').SetValue($p, "Doc $d paragraph $i.")
        $partType.GetField('Target').SetValue($p, '')
        $setParts.Add($p)
    }
    $a = [object[]]::new(1); $a[0] = $setParts
    $preview.GetMethod('Upsert').Invoke($null, $a) | Out-Null
}
$preview.GetMethod('NoteTool').Invoke($null, [object[]]@($true)) | Out-Null

$guidListType = [Collections.Generic.List`1].MakeGenericType(@([Guid]))
$wanted = [Activator]::CreateInstance($guidListType)
foreach ($g in $setDocs) { $wanted.Add($g) }
$rowsOfDocs = $preview.GetMethod('RowsOfDocuments')
# A List<Guid> unrolls into the argument array, so build it by hand.
$aw = [object[]]::new(1); $aw[0] = $wanted
$rd = $rowsOfDocs.Invoke($null, $aw)
Write-Host "$(if ($rd.Count -eq 9) {'PASS'} else {'FAIL'}) every document of the set is gathered: $($rd.Count) of 9"

# Document by document, each in its own order: memoQ numbers the parts of each
# document from 1, so interleaving by number would shuffle the three together.
$expected = @('Doc 0 paragraph 1.','Doc 0 paragraph 2.','Doc 0 paragraph 3.','Doc 0 paragraph 4.',
              'Doc 1 paragraph 1.','Doc 1 paragraph 2.',
              'Doc 2 paragraph 1.','Doc 2 paragraph 2.','Doc 2 paragraph 3.')
$inOrder = $true
for ($i = 0; $i -lt $expected.Count; $i++) { if ($rd[$i].Source -ne $expected[$i]) { $inOrder = $false } }
Write-Host "$(if ($inOrder) {'PASS'} else {'FAIL'}) one document after another, each in its own order"

$emptyList = [Activator]::CreateInstance($guidListType)
$ae = [object[]]::new(1); $ae[0] = $emptyList
$an = [object[]]::new(1); $an[0] = $null
Write-Host "$(if ($rowsOfDocs.Invoke($null, $ae).Count -eq 0) {'PASS'} else {'FAIL'}) an empty set gathers nothing"
Write-Host "$(if ($rowsOfDocs.Invoke($null, $an).Count -eq 0) {'PASS'} else {'FAIL'}) a null set gathers nothing rather than throwing"

$key = 'docs:' + (($setDocs | ForEach-Object { $_.ToString('D') }) -join ',')
$viaSet = $resolve.Invoke($bridge, [object[]]@($key))
$viaSetSources = $viaSet.GetType().GetField('Sources').GetValue($viaSet)
$viaSetName = $viaSet.GetType().GetField('DocumentName').GetValue($viaSet)
$viaSetUnit = $viaSet.GetType().GetField('Unit').GetValue($viaSet)
Write-Host "$(if ($viaSetSources.Count -eq 9) {'PASS'} else {'FAIL'}) AutoPrompt drafts from all of them: $($viaSetSources.Count) paragraph(s)"
Write-Host "$(if ($viaSetName -eq '3 documents memoQ is showing') {'PASS'} else {'FAIL'}) named by what it is: '$viaSetName'"
Write-Host "$(if ($viaSetUnit -eq 'paragraphs') {'PASS'} else {'FAIL'}) counted in paragraphs: '$viaSetUnit'"

# Two of the three, and a duplicate, and a rogue id among them.
$partial = 'docs:' + $setDocs[0].ToString('D') + ',' + $setDocs[0].ToString('D') + ',' + $setDocs[2].ToString('D') + ',not-a-guid'
$viaPartial = $resolve.Invoke($bridge, [object[]]@($partial))
$viaPartialSources = $viaPartial.GetType().GetField('Sources').GetValue($viaPartial)
Write-Host "$(if ($viaPartialSources.Count -eq 7) {'PASS'} else {'FAIL'}) a subset takes just those, duplicates once and rubbish ignored: $($viaPartialSources.Count) of 7"

# One document of the set, chosen on its own, is still just that document.
$one = $resolve.Invoke($bridge, [object[]]@([string]$setDocs[1].ToString('D')))
$oneSources = $one.GetType().GetField('Sources').GetValue($one)
Write-Host "$(if ($oneSources.Count -eq 2) {'PASS'} else {'FAIL'}) choosing one file of the set still gives that file alone: $($oneSources.Count)"

# A set naming nothing the tool knows must not fall through to another document.
$badSet = $resolve.Invoke($bridge, [object[]]@('docs:' + [Guid]::NewGuid().ToString('D')))
$badSetSources = $badSet.GetType().GetField('Sources').GetValue($badSet)
Write-Host "$(if ($badSetSources.Count -eq 0) {'PASS'} else {'FAIL'}) an unknown set draws nothing rather than another document: $($badSetSources.Count)"

# ---- 3. with the tool disconnected it must not invent anything -----------
$preview.GetMethod('NoteTool').Invoke($null, [object[]]@($false)) | Out-Null
$src2 = $resolve.Invoke($bridge, [object[]]@('no-such-capture-key'))
$sources2 = $src2.GetType().GetField('Sources').GetValue($src2)
Write-Host "$(if ($sources2.Count -eq 0) {'PASS'} else {'FAIL'}) no preview tool and no capture yields nothing: $($sources2.Count)"

Write-Host ''
Write-Host 'AUTOPROMPT SOURCE TEST COMPLETE'
