# Proves the forwarded fuzzy TM match reaches both prompts, that failures are
# wrapped the way memoQ expects, and that the store call returns indices.
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

$common = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.Addins.Common.dll")
$mt = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.MTInterfaces.dll")
$plugin = [Reflection.Assembly]::LoadFrom($PluginDll)

$NonPublicStatic = [Reflection.BindingFlags]'NonPublic,Static'
$PublicStatic = [Reflection.BindingFlags]'Public,Static'

function Seg([string]$text) {
    $builder = $common.GetType('MemoQ.Addins.Common.DataStructures.SegmentBuilder')
    return $builder.GetMethod('CreateFromString').Invoke($null, @($text))
}

function SetM($type, $obj, [string]$name, $value) {
    $prop = $type.GetProperty($name)
    if ($prop) { $prop.SetValue($obj, $value); return }
    $field = $type.GetField($name)
    if ($field) { $field.SetValue($obj, $value); return }
    throw "no member $name on $($type.Name)"
}

function GetM($obj, [string]$name) {
    $type = $obj.GetType()
    $prop = $type.GetProperty($name)
    if ($prop) { return $prop.GetValue($obj) }
    $field = $type.GetField($name)
    if ($field) { return $field.GetValue($obj) }
    throw "no member $name on $($type.Name)"
}

# ---- 1. the batched pre-translate prompt ----------------------------------
$prompt = $plugin.GetType('Supervertaler.Core.TranslationPrompt')
$inputType = $plugin.GetType('Supervertaler.Core.BatchSegmentInput')

$listType = [Collections.Generic.List`1].MakeGenericType(@($inputType))
$inputs = [Activator]::CreateInstance($listType)

$a = [Activator]::CreateInstance($inputType)
SetM $inputType $a 'Number' 1
SetM $inputType $a 'SourceText' 'A device comprising a widget.'
$inputs.Add($a)

$b = [Activator]::CreateInstance($inputType)
SetM $inputType $b 'Number' 2
SetM $inputType $b 'SourceText' 'A device comprising a widget and a lever.'
SetM $inputType $b 'FuzzySourceText' 'A device comprising a widget.'
SetM $inputType $b 'FuzzyTargetText' 'Een inrichting omvattende een widget.'
$inputs.Add($b)

$build1 = $prompt.GetMethod('BuildBatchUserPrompt', $PublicStatic)
$argv = [object[]]::new(1); $argv[0] = $inputs
$user = $build1.Invoke($null, $argv)

$hasBlock = $user -like '*CLOSEST APPROVED TRANSLATIONS*'
$namesRow = $user -like '*Segment 2, approved translation: Een inrichting omvattende een widget.*'
$noRow1 = -not ($user -like '*Segment 1, source in memory*')
Write-Host "$(if ($hasBlock -and $namesRow -and $noRow1) {'PASS'} else {'FAIL'}) batch prompt: block=$hasBlock namesRow2=$namesRow onlyRowsWithAMatch=$noRow1"

# Without any fuzzy text the prompt must be byte-identical to the old shape.
$plain = [Activator]::CreateInstance($listType)
$plain.Add($a)
$argv2 = [object[]]::new(1); $argv2[0] = $plain
$plainPrompt = $build1.Invoke($null, $argv2)
$unchanged = -not ($plainPrompt -like '*TRANSLATION MEMORY*')
Write-Host "$(if ($unchanged) {'PASS'} else {'FAIL'}) batch prompt without a match carries no memory block"

# ---- 2. the interactive prompt --------------------------------------------
$builder = $plugin.GetType('Supervertaler.MemoQ.Core.PromptBuilder')
$kind = $builder.GetField('FuzzyMatchKind', $PublicStatic).GetValue($null)

$bundleType = $mt.GetType('MemoQ.MTInterfaces.TranslationBundle')
$itemType = $mt.GetType('MemoQ.MTInterfaces.SegmentContextItem')
$bundle = [Activator]::CreateInstance($bundleType)
SetM $bundleType $bundle 'Source' (Seg 'A device comprising a widget and a lever.')
$item = [Activator]::CreateInstance($itemType)
SetM $itemType $item 'Kind' $kind
SetM $itemType $item 'SourceSegment' (Seg 'A device comprising a widget.')
SetM $itemType $item 'TargetSegment' (Seg 'Een inrichting omvattende een widget.')
$itemListType = [Collections.Generic.List`1].MakeGenericType(@($itemType))
$items = [Activator]::CreateInstance($itemListType)
$items.Add($item)
SetM $bundleType $bundle 'SegmentContext' $items
$settingsType = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings')
$settings = [Activator]::CreateInstance($settingsType)

$build = $builder.GetMethod('Build', $PublicStatic)
$args = [object[]]@($bundle, $settings, 'eng', 'nld', $null, $null, $null, 'Translate.', $null)
$built = $build.Invoke($null, $args)
$system = GetM $built 'User'

$hasMatch = $system -like '*Closest approved translation*'
$hasTarget = $system -like '*Een inrichting omvattende een widget.*'
Write-Host "$(if ($hasMatch -and $hasTarget) {'PASS'} else {'FAIL'}) interactive prompt: header=$hasMatch target=$hasTarget"

# Document context off must not discard it.
SetM $settingsType $settings 'UseDocumentContext' $false
$built2 = $build.Invoke($null, [object[]]@($bundle, $settings, 'eng', 'nld', $null, $null, $null, 'Translate.', $null))
$system2 = GetM $built2 'User'
$stillThere = $system2 -like '*Closest approved translation*'
Write-Host "$(if ($stillThere) {'PASS'} else {'FAIL'}) survives document context being switched off"
SetM $settingsType $settings 'UseDocumentContext' $true
# A bundle with no fuzzy item must produce no header.
$empty = [Activator]::CreateInstance($bundleType)
SetM $bundleType $empty 'Source' (Seg 'Plain segment.')
$built3 = $build.Invoke($null, [object[]]@($empty, $settings, 'eng', 'nld', $null, $null, $null, 'Translate.', $null))
$system3 = GetM $built3 'User'
Write-Host "$(if (-not ($system3 -like '*Closest approved*')) {'PASS'} else {'FAIL'}) no header without a match"

# ---- 3. failures are wrapped the way memoQ expects ------------------------
$batch = $plugin.GetType('Supervertaler.MemoQ.Core.BatchTranslator')
$asError = $batch.GetMethod('AsMemoQError', $NonPublicStatic)

$wrapped = $asError.Invoke($null, @([Exception]::new('boom')))
$isMt = $wrapped.GetType().FullName -eq 'MemoQ.MTInterfaces.MTException'
$keepsMessage = $wrapped.Message -eq 'boom'
Write-Host "$(if ($isMt -and $keepsMessage) {'PASS'} else {'FAIL'}) AsMemoQError wraps: type=$($wrapped.GetType().Name) message='$($wrapped.Message)'"

$cancel = [OperationCanceledException]::new('stop')
$passthrough = $asError.Invoke($null, @($cancel))
Write-Host "$(if ([object]::ReferenceEquals($passthrough, $cancel)) {'PASS'} else {'FAIL'}) cancellation passes through unwrapped"

$already = $asError.Invoke($null, @($wrapped))
Write-Host "$(if ([object]::ReferenceEquals($already, $wrapped)) {'PASS'} else {'FAIL'}) an MTException is not wrapped twice"

# ---- 4. StoreTranslation returns indices ---------------------------------
$director = [Activator]::CreateInstance($plugin.GetType('Supervertaler.MemoQ.SupervertalerMTPluginDirector'))
$plugin.GetType('Supervertaler.MemoQ.SupervertalerMTPluginDirector').GetMethod('Initialize').Invoke($director, @($null))
$fwd = GetM $director 'SupportFuzzyForwarding'
Write-Host "$(if ($fwd) {'PASS'} else {'FAIL'}) director declares SupportFuzzyForwarding = $fwd"

$paramsType = $mt.GetType('MemoQ.MTInterfaces.CreateEngineParams')
$engine = $plugin.GetType('Supervertaler.MemoQ.SupervertalerMTPluginDirector').GetMethod('CreateEngine').Invoke(
    $director, @([Activator]::CreateInstance($paramsType, @('eng', 'nld', $null))))
$store = $engine.GetType().GetMethod('CreateStoreTranslationSession').Invoke($engine, @())

$tuType = $mt.GetType('MemoQ.MTInterfaces.TranslationUnit')
function TU($src, $tgt) {
    $s = if ($src) { Seg $src } else { $null }
    $t = if ($tgt) { Seg $tgt } else { $null }
    return [Activator]::CreateInstance($tuType, [object[]]@($s, $t))
}

# Three storable units. Indices are 0,1,2; the flags this used to return would
# have been 1,1,1, so the two are told apart.
$typed = [Array]::CreateInstance($tuType, 3)
$typed[0] = TU 'First source.' 'Eerste doel.'
$typed[1] = TU 'Second source.' 'Tweede doel.'
$typed[2] = TU 'Third source.' 'Derde doel.'

$types = [Type[]]::new(1); $types[0] = $typed.GetType()
$method = $store.GetType().GetMethod('StoreTranslation', $types)
$argv3 = [object[]]::new(1); $argv3[0] = $typed
$indices = $method.Invoke($store, $argv3)
$asText = ($indices -join ',')
Write-Host "$(if ($asText -eq '0,1,2') {'PASS'} else {'FAIL'}) StoreTranslation returns indices: [$asText] (expected [0,1,2], flags would be [1,1,1])"

Write-Host ''
Write-Host 'FUZZY FORWARDING TEST COMPLETE'
