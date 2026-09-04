# The prompt must say how the runtime terminology relates to the prompt's own
# glossary, and that a forbidden term wins over it.
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

$common = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.Addins.Common.dll")
$mt = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.MTInterfaces.dll")
$plugin = [Reflection.Assembly]::LoadFrom($PluginDll)
$PublicStatic = [Reflection.BindingFlags]'Public,Static'

$sbType = $common.GetType('MemoQ.Addins.Common.DataStructures.SegmentBuilder')
function Seg([string]$t) { return $sbType.GetMethod('CreateFromString').Invoke($null, @($t)) }

# A glossary with one preferred and one forbidden entry.
$tmp = Join-Path $env:TEMP ('sv-relation-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
$glossary = Join-Path $tmp 'g.txt'
@(
    '#! source=eng target=dut',
    "device`tinrichting",
    "device`tapparaat`tforbidden"
) -join "`r`n" | Set-Content -Path $glossary -Encoding UTF8

try {
    $ti = $plugin.GetType('Supervertaler.MemoQ.Core.TermIndex')
    $matches = $ti.GetMethod('Find', $PublicStatic).Invoke(
        $null, [object[]]@([string]$glossary, 'the device is mounted on the frame'))

    $builder = $plugin.GetType('Supervertaler.MemoQ.Core.PromptBuilder')
    $bundleType = $mt.GetType('MemoQ.MTInterfaces.TranslationBundle')
    $bundle = [Activator]::CreateInstance($bundleType)
    $bundleType.GetField('Source').SetValue($bundle, (Seg 'The device is mounted on the frame.'))

    $settingsType = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings')
    $settings = [Activator]::CreateInstance($settingsType)

    $built = $builder.GetMethod('Build', $PublicStatic).Invoke(
        $null, [object[]]@($bundle, $settings, 'eng', 'dut', $null, $null, $matches, 'Translate.', $null))
    $user = $built.GetType().GetProperty('User').GetValue($built)

    $saysSameTerminology = $user -like '*same terminology filtered to what is in front of you*'
    $saysWhichWins = $user -like '*if*the two ever disagree, follow these*'
    Write-Host "$(if ($saysSameTerminology -and $saysWhichWins) {'PASS'} else {'FAIL'}) terminology block explains the relationship and names the winner"

    $forbiddenOverrides = $user -like '*even if the instructions above name one as the locked rendering*'
    Write-Host "$(if ($forbiddenOverrides) {'PASS'} else {'FAIL'}) forbidden terms are said to override the prompt's own glossary"

    $stillHedged = $user -like '*unless one is clearly wrong for this particular*'
    Write-Host "$(if ($stillHedged) {'PASS'} else {'FAIL'}) preferred terms are still a steer, not a mandate"

    $bothPresent = ($user -like '*inrichting*') -and ($user -like '*apparaat*')
    Write-Host "$(if ($bothPresent) {'PASS'} else {'FAIL'}) both the preferred and the forbidden entry reached the prompt"

    # With no terms at all, neither block should appear.
    $empty = [Activator]::CreateInstance($bundleType)
    $bundleType.GetField('Source').SetValue($empty, (Seg 'Nothing matches here.'))
    $plain = $builder.GetMethod('Build', $PublicStatic).Invoke(
        $null, [object[]]@($empty, $settings, 'eng', 'dut', $null, $null, $null, 'Translate.', $null))
    $plainUser = $plain.GetType().GetProperty('User').GetValue($plain)
    $quiet = -not ($plainUser -like '*Client terminology*') -and -not ($plainUser -like '*Forbidden terms*')
    Write-Host "$(if ($quiet) {'PASS'} else {'FAIL'}) no terminology, no blocks and no precedence talk"
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }

# The generated-prompt side must ask for the same rule.
$bridgeType = $plugin.GetType('Supervertaler.MemoQ.Core.MemoQBridge')
$field = $bridgeType.GetField('MemoQHostConstraints', [Reflection.BindingFlags]'NonPublic,Static,Public')
$constraints = $field.GetValue($null)
$asksForRule = ($constraints -like '*the runtime terms govern*') -and ($constraints -like '*overrides this prompt*')
Write-Host "$(if ($asksForRule) {'PASS'} else {'FAIL'}) AutoPrompt host constraints ask the generated prompt to state the same rule"

Write-Host ''
Write-Host 'TERMINOLOGY RELATION TEST COMPLETE'
