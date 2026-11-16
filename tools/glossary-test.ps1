# The language pair a prompt or glossary declares, and the mismatch it is there
# to catch.
#
# The tab-separated glossary files were retired on 2026-09-18, and with them
# the part of this test that read a file's #! header through TermIndex. What is
# left is what still runs: the direction comparison, the exporter's stamp and
# the wording of the warning.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$PluginDll = "$(Split-Path -Parent $PSScriptRoot)\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll"

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

$script:fail = 0
function Report([bool]$ok, [string]$label) {
    if (-not $ok) { $script:fail++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

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
Report $ok "direction classification, $($cases.Count) cases"

# ---- 2. the exporter stamps the pair -------------------------------------
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
Report ($hasHeader -and $noHeaderWhenUnknown) "exporter stamps the pair when known ($hasHeader) and omits it when not ($noHeaderWhenUnknown)"

# ---- 3. the warning text names both pairs --------------------------------
$explain = $gd.GetMethod('Explain', $PublicStatic)
$relType = $gd.GetNestedType('Relation', $NonPublicStatic)
$inverted = [Enum]::Parse($relType, 'Inverted')
$msg = $explain.Invoke($null, [object[]]@($inverted, 'eng', 'dut', 'dut-NL', 'eng-GB'))
$names = ($msg -like '*eng to dut*') -and ($msg -like '*dut-NL to eng-GB*')
Report $names 'mismatch message names the glossary pair and the project pair'

$aligned = [Enum]::Parse($relType, 'Aligned')
$quiet = $explain.Invoke($null, [object[]]@($aligned, 'eng', 'dut', 'eng', 'dut'))
Report ($null -eq $quiet) 'an aligned glossary produces no message'

Write-Host ''
Write-Host "GLOSSARY DIRECTION TEST COMPLETE - $($script:fail) failure(s)"
if ($script:fail -gt 0) { exit 1 }
