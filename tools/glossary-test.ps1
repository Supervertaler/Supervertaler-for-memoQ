# The glossary language header, and the mismatch it is there to catch.
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
$PublicStatic = [Reflection.BindingFlags]'Public,Static'
$NonPublicStatic = [Reflection.BindingFlags]'NonPublic,Static'

# ---- 1. direction classification -----------------------------------------
$gd = $plugin.GetType('Supervertaler.MemoQ.Core.GlossaryDirection')
$compare = $gd.GetMethod('Compare', $PublicStatic)

function Rel($ps, $pt, $gs, $gt) { return $compare.Invoke($null, [object[]]@($ps, $pt, $gs, $gt)).ToString() }

$cases = @(
    @('eng', 'dut', 'eng', 'dut', 'Aligned'),
    @('eng-GB', 'dut-NL', 'eng', 'dut', 'Aligned'),          # regions must not matter
    @('dut-NL', 'eng-GB', 'eng', 'dut', 'Inverted'),         # today's real case
    @('fra', 'deu', 'eng', 'dut', 'Unrelated'),
    @('eng', 'dut', '', '', 'Undeclared'),                   # a file with no header
    @('dut', 'fra', 'eng', 'dut', 'Unrelated')               # shares one side only
)
$ok = $true
foreach ($c in $cases) {
    $got = Rel $c[0] $c[1] $c[2] $c[3]
    if ($got -ne $c[4]) { $ok = $false; Write-Host "    project $($c[0])->$($c[1]) glossary $($c[2])->$($c[3]): got $got, expected $($c[4])" }
}
Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) direction classification, $($cases.Count) cases"

# ---- 2. the header is read, and only from a #! line ----------------------
$ti = $plugin.GetType('Supervertaler.MemoQ.Core.TermIndex')
$find = $ti.GetMethod('Find', $PublicStatic)
$declaredSource = $ti.GetProperty('DeclaredSource', $PublicStatic)
$declaredTarget = $ti.GetProperty('DeclaredTarget', $PublicStatic)

$tmp = Join-Path $env:TEMP ('sv-glossary-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
try {
    $withHeader = Join-Path $tmp 'with-header.txt'
    @(
        '# patent eng-dut',
        '#! source=eng target=dut',
        '# prose, not a setting: source=zzz target=zzz',
        '',
        "device`tinrichting",
        "apparatus`tapparaat`tforbidden"
    ) -join "`r`n" | Set-Content -Path $withHeader -Encoding UTF8

    $hits = $find.Invoke($null, [object[]]@([string]$withHeader, 'the device is mounted'))
    $src = $declaredSource.GetValue($null)
    $tgt = $declaredTarget.GetValue($null)
    Write-Host "$(if ($src -eq 'eng' -and $tgt -eq 'dut') {'PASS'} else {'FAIL'}) header read: source='$src' target='$tgt' (prose line ignored)"
    Write-Host "$(if ($hits.Count -eq 1) {'PASS'} else {'FAIL'}) entries still load alongside the header: $($hits.Count) hit(s)"

    $noHeader = Join-Path $tmp 'no-header.txt'
    @('# just prose', '', "device`tinrichting") -join "`r`n" | Set-Content -Path $noHeader -Encoding UTF8
    $find.Invoke($null, [object[]]@([string]$noHeader, 'the device')) | Out-Null
    $src2 = $declaredSource.GetValue($null)
    Write-Host "$(if ([string]::IsNullOrEmpty($src2)) {'PASS'} else {'FAIL'}) a file without a header declares nothing (got '$src2')"
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }

# ---- 3. the exporter stamps the pair -------------------------------------
$ex = $plugin.GetType('Supervertaler.Core.PromptGlossaryExtractor')
$entryType = $plugin.GetType('Supervertaler.Core.PromptGlossaryExtractor+Entry')
$listType = [Collections.Generic.List`1].MakeGenericType(@($entryType))
$entries = [Activator]::CreateInstance($listType)
$e = [Activator]::CreateInstance($entryType)
$entryType.GetProperty('Source').SetValue($e, 'device')
$entryType.GetProperty('Target').SetValue($e, 'inrichting')
$entries.Add($e)

$toText = $ex.GetMethod('ToGlossaryText', $PublicStatic)
$stamped = $toText.Invoke($null, [object[]]@($entries, 'patent eng-dut', 'eng', 'dut'))
$unstamped = $toText.Invoke($null, [object[]]@($entries, 'patent eng-dut', $null, $null))

$hasHeader = $stamped -like '*#! source=eng target=dut*'
$noHeaderWhenUnknown = -not ($unstamped -like '*#!*')
Write-Host "$(if ($hasHeader -and $noHeaderWhenUnknown) {'PASS'} else {'FAIL'}) exporter stamps the pair when known ($hasHeader) and omits it when not ($noHeaderWhenUnknown)"

# ---- 4. the warning text names both pairs --------------------------------
$explain = $gd.GetMethod('Explain', $PublicStatic)
$relType = $gd.GetNestedType('Relation', $NonPublicStatic)
$inverted = [Enum]::Parse($relType, 'Inverted')
$msg = $explain.Invoke($null, [object[]]@($inverted, 'eng', 'dut', 'dut-NL', 'eng-GB'))
$names = ($msg -like '*eng to dut*') -and ($msg -like '*dut-NL to eng-GB*')
Write-Host "$(if ($names) {'PASS'} else {'FAIL'}) mismatch message names the glossary pair and the project pair"

$aligned = [Enum]::Parse($relType, 'Aligned')
$quiet = $explain.Invoke($null, [object[]]@($aligned, 'eng', 'dut', 'eng', 'dut'))
Write-Host "$(if ($null -eq $quiet) {'PASS'} else {'FAIL'}) an aligned glossary produces no message"

Write-Host ''
Write-Host 'GLOSSARY HEADER TEST COMPLETE'
