# Prompt caching turns on a property, not a setting: the system prompt has to be
# byte-identical from one batch to the next. The cache marker covers the system
# block as a unit, so ONE varying line in it - a domain from the metadata, a
# recalled pair, this batch's terminology - and every batch pays the full input
# rate for the instructions and the memory bank as well.
#
# That is exactly the kind of thing a later change breaks without any symptom.
# Nothing fails, nothing looks wrong, the bill is just four to ten times what it
# should be. Hence a test.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$Root      = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ'
$PluginDll = "$Root\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll"

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
$mt     = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.MTInterfaces.dll")
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

$builder  = $plugin.GetType('Supervertaler.MemoQ.Core.PromptBuilder')
$generalT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings')
$metaT    = $mt.GetType('MemoQ.MTInterfaces.MTRequestMetadata')

$general = [Activator]::CreateInstance($generalT)

function Meta($domain, $subject) {
    $m = [Activator]::CreateInstance($metaT)
    $metaT.GetProperty('Domain').SetValue($m, $domain)
    $metaT.GetProperty('Subject').SetValue($m, $subject)
    return $m.PSObject.BaseObject
}

$KB = 'MEMORY-BANK-MARKER: the client prefers the formal register.'

function BuildBatch($metadata) {
    $argv = New-Object object[] 8
    $argv[0] = $general
    $argv[1] = 'eng'
    $argv[2] = 'nld'
    $argv[3] = $metadata
    $argv[4] = $null          # recalled
    $argv[5] = $null          # ownTerms
    $argv[6] = 'INSTRUCTIONS-MARKER'
    $argv[7] = $KB
    return $builder.GetMethod('BuildForBatch', $Static).Invoke($null, $argv)
}

$a = BuildBatch (Meta 'chemistry' 'catalysts')
$b = BuildBatch (Meta 'mechanics' 'gearboxes')

$sysA = $a.GetType().GetProperty('System').GetValue($a)
$sysB = $b.GetType().GetProperty('System').GetValue($b)
$usrA = $a.GetType().GetProperty('User').GetValue($a)
$usrB = $b.GetType().GetProperty('User').GetValue($b)

# ---- the property the cache depends on ------------------------------------
Check ($sysA -ceq $sysB) 'the system prompt is byte-identical across two different batches'
Check ($usrA -ne $usrB) 'while the per-batch half does differ, so nothing was simply dropped'

# ---- and the right things are in each half --------------------------------
Check ($sysA.Contains('INSTRUCTIONS-MARKER')) 'the instructions are in the stable half'
Check ($sysA.Contains($KB)) 'and so is the memory bank'

Check ($usrA.Contains('chemistry')) "this batch's project metadata is in the varying half"
Check (-not $sysA.Contains('chemistry')) 'and not in the stable half, where it would defeat the cache'

# ---- nothing is lost in the split -----------------------------------------
# The model must still see every section it saw when the two were joined.
Check ($usrA.Contains('catalysts')) 'the subject survives the split'
Check ($usrA -notmatch 'Source segment:') 'and the single-segment trailer is still dropped for a batch'

Write-Host ''
Write-Host "CACHING TEST COMPLETE - $fails failure(s)"
