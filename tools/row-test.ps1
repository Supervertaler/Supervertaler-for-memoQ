# Row identity and status, and the fragment rule that makes MatchPatch usable.
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
$NonPublicStatic = [Reflection.BindingFlags]'NonPublic,Static'
$sbType = $common.GetType('MemoQ.Addins.Common.DataStructures.SegmentBuilder')
function Seg([string]$t) { return $sbType.GetMethod('CreateFromString').Invoke($null, @($t)) }

$builder = $plugin.GetType('Supervertaler.MemoQ.Core.PromptBuilder')
$rowKind = $builder.GetField('RowStatusKind', $PublicStatic).GetValue($null)
$bundleType = $mt.GetType('MemoQ.MTInterfaces.TranslationBundle')
$itemType = $mt.GetType('MemoQ.MTInterfaces.SegmentContextItem')
$settingsType = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings')
$settings = [Activator]::CreateInstance($settingsType)
$build = $builder.GetMethod('Build', $PublicStatic)

function UserPromptFor([string]$sourceText, $status) {
    $bundle = [Activator]::CreateInstance($bundleType)
    $bundleType.GetField('Source').SetValue($bundle, (Seg $sourceText))

    if ($null -ne $status) {
        $item = [Activator]::CreateInstance($itemType)
        $itemType.GetField('Kind').SetValue($item, $rowKind)
        $itemType.GetField('NumericValue').SetValue($item, [single]$status)
        $lt = [Collections.Generic.List`1].MakeGenericType(@($itemType))
        $items = [Activator]::CreateInstance($lt)
        $items.Add($item)
        $bundleType.GetField('SegmentContext').SetValue($bundle, $items)
    }

    $built = $build.Invoke($null, [object[]]@($bundle, $settings, 'eng', 'nld', $null, $null, $null, 'Translate.', $null))
    return $built.GetType().GetProperty('User').GetValue($built)
}

# ---- 1. status decoding ---------------------------------------------------
$rs = $plugin.GetType('Supervertaler.MemoQ.Core.RowStatus')
$describe = $rs.GetMethod('Describe', $PublicStatic)
$isRejected = $rs.GetMethod('IsRejected', $PublicStatic)
$isConfirmed = $rs.GetMethod('IsConfirmed', $PublicStatic)

$named = $describe.Invoke($null, @([int]3000))
$unknown = $describe.Invoke($null, @([int]1234))
Write-Host "$(if ($named -eq 'confirmed' -and $unknown -eq 'state 1234') {'PASS'} else {'FAIL'}) status names: 3000='$named' 1234='$unknown'"

$r = $isRejected.Invoke($null, @([int]7000))
$nr = $isRejected.Invoke($null, @([int]3000))
$c = $isConfirmed.Invoke($null, @([int]5000))
Write-Host "$(if ($r -and -not $nr -and $c) {'PASS'} else {'FAIL'}) predicates: rejected(7000)=$r rejected(3000)=$nr confirmed(5000)=$c"

# ---- 2. a rejected row changes the request --------------------------------
$rejected = UserPromptFor 'A device comprising a widget.' 7000
$confirmed = UserPromptFor 'A device comprising a widget.' 3000
$noStatus = UserPromptFor 'A device comprising a widget.' $null

$onlyRejected = ($rejected -like '*rejected a previous translation*') `
    -and -not ($confirmed -like '*rejected a previous translation*') `
    -and -not ($noStatus -like '*rejected a previous translation*')
Write-Host "$(if ($onlyRejected) {'PASS'} else {'FAIL'}) rejected row asks for a different rendering, other states do not"

# ---- 3. the fragment rule -------------------------------------------------
$fragment = UserPromptFor 'safety valve housing' $null
$sentence = UserPromptFor 'The safety valve housing is mounted on the frame.' $null
$longNoStop = UserPromptFor 'The safety valve housing is mounted on the frame of the apparatus described here' $null
$colon = UserPromptFor 'Technical field:' $null

$fragHit = $fragment -like '*fragment rather than a complete sentence*'
$sentMiss = -not ($sentence -like '*fragment rather than*')
$longMiss = -not ($longNoStop -like '*fragment rather than*')
$colonMiss = -not ($colon -like '*fragment rather than*')
Write-Host "$(if ($fragHit -and $sentMiss -and $longMiss -and $colonMiss) {'PASS'} else {'FAIL'}) fragment rule: phrase=$fragHit sentence-skipped=$sentMiss long-skipped=$longMiss colon-skipped=$colonMiss"

# ---- 4. the engine offers itself to MatchPatch ----------------------------
$director = [Activator]::CreateInstance($plugin.GetType('Supervertaler.MemoQ.SupervertalerMTPluginDirector'))
$dirType = $plugin.GetType('Supervertaler.MemoQ.SupervertalerMTPluginDirector')
$dirType.GetMethod('Initialize').Invoke($director, @($null))
$paramsType = $mt.GetType('MemoQ.MTInterfaces.CreateEngineParams')
$engine = $dirType.GetMethod('CreateEngine').Invoke($director, @([Activator]::CreateInstance($paramsType, @('eng', 'nld', $null))))
$mp = $engine.GetType().GetProperty('SupportsFuzzyCorrection').GetValue($engine)
Write-Host "$(if ($mp) {'PASS'} else {'FAIL'}) engine offers SupportsFuzzyCorrection = $mp"

# ---- 5. row lookup by SegmentIndex ---------------------------------------
$sessionType = $plugin.GetType('Supervertaler.MemoQ.SupervertalerSession')
$rowAt = $sessionType.GetMethod('RowAt', $NonPublicStatic)
$metaType = $mt.GetType('MemoQ.MTInterfaces.MTRequestMetadata')
$smType = $mt.GetType('MemoQ.MTInterfaces.SegmentMetadata')
$meta = [Activator]::CreateInstance($metaType)
$rowListType = [Collections.Generic.List`1].MakeGenericType(@($smType))
$rows = [Activator]::CreateInstance($rowListType)

# Deliberately out of order, so a positional read would get it wrong.
foreach ($pair in @(@(2, 7000), @(0, 3000), @(1, 1000))) {
    $sm = [Activator]::CreateInstance($smType)
    $smType.GetProperty('SegmentIndex').SetValue($sm, [int]$pair[0])
    $smType.GetProperty('SegmentStatus').SetValue($sm, [uint16]$pair[1])
    $smType.GetProperty('SegmentID').SetValue($sm, [Guid]::NewGuid())
    $rows.Add($sm)
}
$metaType.GetProperty('SegmentLevelMetadata').SetValue($meta, $rows)

$got = @()
foreach ($i in 0, 1, 2) {
    $row = $rowAt.Invoke($null, [object[]]@($meta, $i))
    $got += [int]$smType.GetProperty('SegmentStatus').GetValue($row)
}
$expected = ($got -join ',') -eq '3000,1000,7000'
Write-Host "$(if ($expected) {'PASS'} else {'FAIL'}) RowAt matches by SegmentIndex, not position: [$($got -join ', ')] (expected [3000, 1000, 7000])"

$none = $rowAt.Invoke($null, [object[]]@($null, 0))
Write-Host "$(if ($null -eq $none) {'PASS'} else {'FAIL'}) RowAt with no metadata returns nothing"

Write-Host ''
Write-Host 'ROW AWARENESS TEST COMPLETE'
