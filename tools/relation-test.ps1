# The prompt must say how the runtime terminology relates to the prompt's own
# glossary, and that a forbidden term wins over it.
#
# Run through tools/run-harness.ps1, never directly.
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

$common = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.Addins.Common.dll")
$mt = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.MTInterfaces.dll")
$plugin = [Reflection.Assembly]::LoadFrom($PluginDll)
$off = [Activator]::CreateInstance($plugin.GetType('Supervertaler.Core.StructureContextMode'))   # structure context: Off
$PublicStatic = [Reflection.BindingFlags]'Public,Static'
$NonPublicStatic = [Reflection.BindingFlags]'NonPublic,Static'

$script:fail = 0
function Report([bool]$ok, [string]$label) {
    if (-not $ok) { $script:fail++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

$sbType = $common.GetType('MemoQ.Addins.Common.DataStructures.SegmentBuilder')
function Seg([string]$t) { return $sbType.GetMethod('CreateFromString').Invoke($null, @($t)) }

# A data folder of this test's own. The termbase database and the selection
# file both hang off SupervertalerPaths.Root, so pointing that at a temporary
# folder means the termbase built below, and the tick that selects it, are
# written there and nowhere near the real ones.
$tmp = Join-Path $env:TEMP ('sv-relation-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null

try {
    $plugin.GetType('Supervertaler.Core.SupervertalerPaths').GetMethod('Set', $PublicStatic).Invoke($null, @([string]$tmp))

    $db        = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseDb')
    $writer    = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseWriter')
    $sel       = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseSelection')
    $ti        = $plugin.GetType('Supervertaler.MemoQ.Core.TermIndex')
    $flagsType = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseSelection+Flags')
    foreach ($t in @($db, $writer, $sel, $ti, $flagsType)) { if ($null -eq $t) { throw 'a required type was not found' } }

    # Checked, not assumed: if the override ever stopped reaching these two,
    # the lines below would write into the real database and selection.
    $dbPath  = $db.GetProperty('Path', $NonPublicStatic).GetValue($null)
    $selPath = $sel.GetProperty('Path', $NonPublicStatic).GetValue($null)
    foreach ($p in @($dbPath, $selPath)) {
        if (-not $p.StartsWith($tmp, [StringComparison]::OrdinalIgnoreCase)) { throw "refusing to write outside the test folder: $p" }
    }

    # A termbase with one preferred and one forbidden entry for the same term.
    $a = [object[]]::new(4); $a[0] = 'relation harness'; $a[1] = 'eng'; $a[2] = 'dut'; $a[3] = ''
    $tbId = [long]$writer.GetMethod('Create', $NonPublicStatic).Invoke($null, $a)
    $addTerm = $writer.GetMethod('AddTerm', $NonPublicStatic)
    $a = [object[]]::new(5); $a[0] = $tbId; $a[1] = 'device'; $a[2] = 'inrichting'; $a[3] = $false; $a[4] = $null
    $addTerm.Invoke($null, $a) | Out-Null
    $a = [object[]]::new(5); $a[0] = $tbId; $a[1] = 'device'; $a[2] = 'apparaat'; $a[3] = $true; $a[4] = $null
    $addTerm.Invoke($null, $a) | Out-Null

    # Ticked Read and AI for one project: only then may the model be told.
    $project = [Guid]'55555555-5555-5555-5555-555555555555'
    $f = [Activator]::CreateInstance($flagsType)
    $f.Id = $tbId; $f.Rank = 1; $f.CaseSensitive = $false; $f.Ai = $true; $f.Name = 'relation harness'
    $idList = New-Object 'System.Collections.Generic.List[long]'
    $idList.Add($tbId)
    $flagList = [Activator]::CreateInstance([System.Collections.Generic.List`1].MakeGenericType($flagsType))
    $flagList.Add($f)
    $a = [object[]]::new(3); $a[0] = $project; $a[1] = $idList.PSObject.BaseObject; $a[2] = $flagList.PSObject.BaseObject
    $sel.GetMethod('Save', $NonPublicStatic).Invoke($null, $a)

    # The same call the MT plugin makes for a segment.
    $a = [object[]]::new(2); $a[0] = 'eng'; $a[1] = 'dut'
    $ti.GetMethod('UseLanguages', $PublicStatic).Invoke($null, $a)
    $a = [object[]]::new(2); $a[0] = $project; $a[1] = 'the device is mounted on the frame'
    $found = $ti.GetMethod('FindForModel', $PublicStatic).Invoke($null, $a)
    Report ($found.Count -eq 2) "the termbase ticked for the project answers with both entries ($($found.Count) match(es))"

    $builder = $plugin.GetType('Supervertaler.MemoQ.Core.PromptBuilder')
    $bundleType = $mt.GetType('MemoQ.MTInterfaces.TranslationBundle')
    $bundle = [Activator]::CreateInstance($bundleType)
    $bundleType.GetField('Source').SetValue($bundle, (Seg 'The device is mounted on the frame.'))

    $settingsType = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings')
    $settings = [Activator]::CreateInstance($settingsType)

    $built = $builder.GetMethod('Build', $PublicStatic).Invoke(
        $null, [object[]]@($bundle, $settings, 'eng', 'dut', $null, $null, $found, 'Translate.', $null, $off, $null, $null))
    $user = $built.GetType().GetProperty('User').GetValue($built)

    $saysSameTerminology = $user -like '*same terminology filtered to what is in front of you*'
    $saysWhichWins = $user -like '*if*the two ever disagree, follow these*'
    Report ($saysSameTerminology -and $saysWhichWins) 'terminology block explains the relationship and names the winner'

    $forbiddenOverrides = $user -like '*even if the instructions above name one as the locked rendering*'
    Report $forbiddenOverrides "forbidden terms are said to override the prompt's own glossary"

    $stillHedged = $user -like '*unless one is clearly wrong for this particular*'
    Report $stillHedged 'preferred terms are still a steer, not a mandate'

    $bothPresent = ($user -like '*inrichting*') -and ($user -like '*apparaat*')
    Report $bothPresent 'both the preferred and the forbidden entry reached the prompt'

    # With no terms at all, neither block should appear.
    $empty = [Activator]::CreateInstance($bundleType)
    $bundleType.GetField('Source').SetValue($empty, (Seg 'Nothing matches here.'))
    $plain = $builder.GetMethod('Build', $PublicStatic).Invoke(
        $null, [object[]]@($empty, $settings, 'eng', 'dut', $null, $null, $null, 'Translate.', $null, $off, $null, $null))
    $plainUser = $plain.GetType().GetProperty('User').GetValue($plain)
    $quiet = -not ($plainUser -like '*Client terminology*') -and -not ($plainUser -like '*Forbidden terms*')
    Report $quiet 'no terminology, no blocks and no precedence talk'
}
finally {
    # The pooled connection holds the database file open; let go of it first,
    # or the folder outlives the test.
    try { [Microsoft.Data.Sqlite.SqliteConnection]::ClearAllPools() } catch { }
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

# The generated-prompt side must ask for the same rule.
$bridgeType = $plugin.GetType('Supervertaler.MemoQ.Core.MemoQBridge')
$field = $bridgeType.GetField('MemoQHostConstraints', [Reflection.BindingFlags]'NonPublic,Static,Public')
$constraints = $field.GetValue($null)
$asksForRule = ($constraints -like '*the runtime terms govern*') -and ($constraints -like '*overrides this prompt*')
Report $asksForRule 'AutoPrompt host constraints ask the generated prompt to state the same rule'

Write-Host ''
Write-Host "TERMINOLOGY RELATION TEST COMPLETE - $($script:fail) failure(s)"
if ($script:fail -gt 0) { exit 1 }
