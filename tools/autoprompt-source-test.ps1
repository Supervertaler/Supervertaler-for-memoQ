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

$cases = @(
    @('Patents', $null, 'patent'),      # memoQ's plural, our singular
    @($null, 'Patents', 'patent'),      # subject when domain is empty
    @('Legal', $null, 'legal'),
    @('Something Else', $null, $null),  # unknown stays unknown
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

# ---- 3. with the tool disconnected it must not invent anything -----------
$preview.GetMethod('NoteTool').Invoke($null, [object[]]@($false)) | Out-Null
$src2 = $resolve.Invoke($bridge, [object[]]@('no-such-capture-key'))
$sources2 = $src2.GetType().GetField('Sources').GetValue($src2)
Write-Host "$(if ($sources2.Count -eq 0) {'PASS'} else {'FAIL'}) no preview tool and no capture yields nothing: $($sources2.Count)"

Write-Host ''
Write-Host 'AUTOPROMPT SOURCE TEST COMPLETE'
