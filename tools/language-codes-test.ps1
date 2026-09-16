# The shared language table (core/src/LanguageCodes.cs).
#
# It is data, and data with a typo in it is worse than no table: a wrong row
# does not fail loudly, it quietly decides that two languages are the same when
# they are not. So this checks the shape of the whole table as well as the rows
# that actually matter to a CAT tool.
#
#   powershell -NoProfile -File tools\language-codes-test.ps1
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
        $candidate = Join-Path $dir "$name.dll"
        if (Test-Path $candidate) { try { return [Reflection.Assembly]::LoadFrom($candidate) } catch { return $null } }
    }
    return $null
})

$plugin = [Reflection.Assembly]::LoadFrom($PluginDll)
$lc = $plugin.GetType('Supervertaler.Core.LanguageCodes')
if ($null -eq $lc) { throw 'LanguageCodes not found - is core/ compiled in?' }

$PS = [Reflection.BindingFlags]'Public,Static'
function Norm($code)   { $a = [object[]]::new(1); $a[0] = $code; return $lc.GetMethod('Normalise', $PS).Invoke($null, $a) }
function Same($a, $b)  { $x = [object[]]::new(2); $x[0] = $a; $x[1] = $b; return $lc.GetMethod('AreSame', $PS).Invoke($null, $x) }
function Known($code)  { $a = [object[]]::new(1); $a[0] = $code; return $lc.GetMethod('IsKnown', $PS).Invoke($null, $a) }

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++ }
    else { $script:fail++; Write-Host ("FAIL {0}  {1}" -f $name, $detail) -ForegroundColor Red }
}

# -- the shape of the table itself -------------------------------------------
# Read the source rather than the compiled form: a duplicate key would have
# thrown at class-init, but a row with the wrong number of fields or a repeated
# two-letter code is a silent wrong answer.
$source = Get-Content 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\core\src\LanguageCodes.cs' -Raw
$rows = [regex]::Matches($source, '"([a-z]{2})\|([a-z]{3})\|([a-z]{3})\|([^"]+)"')
Write-Host ("table rows: {0}" -f $rows.Count)
Check 'the table is the full ISO 639-1 set, near enough' ($rows.Count -ge 180) ("only $($rows.Count) rows")

$two = @{}
$three = @{}
$dupTwo = @(); $dupThree = @()
foreach ($m in $rows) {
    $t = $m.Groups[1].Value
    if ($two.ContainsKey($t)) { $dupTwo += $t } else { $two[$t] = $true }
    foreach ($c in @($m.Groups[2].Value, $m.Groups[3].Value)) {
        if ($three.ContainsKey($c) -and $three[$c] -ne $t) { $dupThree += "$c ($($three[$c]) vs $t)" }
        $three[$c] = $t
    }
}
Check 'no two-letter code appears twice' ($dupTwo.Count -eq 0) ($dupTwo -join ', ')
Check 'no three-letter code maps to two different languages' ($dupThree.Count -eq 0) ($dupThree -join ', ')

# -- the codes the two hosts actually produce --------------------------------
# memoQ hands a terminology session things like "dut-NL"; the database holds
# "nl". This pair is the one that was silently dead before the table existed.
$cases = @(
    @('dut-NL','nl'), @('eng-GB','en'), @('nl','nl'), @('en-US','en'),
    @('ger','de'), @('deu','de'), @('fre','fr'), @('fra','fr'),
    @('dut','nl'), @('nld','nl'), @('cze','cs'), @('ces','cs'),
    @('gre','el'), @('ell','el'), @('chi','zh'), @('zho','zh'),
    @('rum','ro'), @('ron','ro'), @('slo','sk'), @('slk','sk'),
    @('ice','is'), @('isl','is'), @('per','fa'), @('fas','fa'),
    @('may','ms'), @('msa','ms'), @('baq','eu'), @('eus','eu'),
    @('arm','hy'), @('hye','hy'), @('geo','ka'), @('kat','ka'),
    @('mac','mk'), @('mkd','mk'), @('mao','mi'), @('mri','mi'),
    @('tib','bo'), @('bod','bo'), @('wel','cy'), @('cym','cy'),
    @('alb','sq'), @('sqi','sq'), @('bur','my'), @('mya','my'),
    @('tur','tr'), @('spa','es'), @('swe','sv'), @('swa','sw'),
    @('por','pt'), @('pt-BR','pt'), @('zh-Hans','zh'), @('nb-NO','nb'),
    @('English','en'), @('Dutch','nl'), @('German','de'), @('Flemish','nl')
)
foreach ($c in $cases) {
    $got = Norm $c[0]
    Check ("{0} -> {1}" -f $c[0], $c[1]) ($got -eq $c[1]) ("got '$got'")
}

# -- the traps this table exists to avoid ------------------------------------
# Truncating a three-letter code is not merely lossy, it invents matches:
# "swe" would become "sw", which is Swahili.
Check 'Swedish is not Swahili' (-not (Same 'swe' 'sw')) 'swe matched sw'
Check 'Turkish is not whatever "tu" is' ((Norm 'tur') -eq 'tr')
Check 'Spanish does not truncate to sp' ((Norm 'spa') -eq 'es')

# An unknown code must come back untouched, so a comparison fails rather than
# being answered wrongly.
Check 'an unknown code is left alone' ((Norm 'xyz') -eq 'xyz')
Check 'an unknown code is not claimed as known' (-not (Known 'xyz'))
Check 'two different unknowns are not the same language' (-not (Same 'xyz' 'abc'))
Check 'the same unknown still equals itself' (Same 'xyz' 'XYZ')

# -- the comparison the termbase loader makes --------------------------------
Check 'a job and a termbase in the same pair agree' ((Same 'dut-NL' 'nl') -and (Same 'eng-GB' 'en'))
Check 'a German termbase is not a Dutch job' (-not (Same 'de' 'dut-NL'))
Check 'empty is never equal to anything, including empty' (-not (Same '' '') -and -not (Same 'en' ''))

# -- superseded codes still in the wild --------------------------------------
Check 'iw is Hebrew' ((Norm 'iw') -eq 'he')
Check 'in is Indonesian' ((Norm 'in') -eq 'id')
Check 'mo is Romanian' ((Norm 'mo') -eq 'ro')

Write-Host ''
Write-Host ("LANGUAGE CODE TEST COMPLETE - {0} passed, {1} failed" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
