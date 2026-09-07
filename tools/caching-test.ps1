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
$off = [Activator]::CreateInstance($plugin.GetType('Supervertaler.Core.StructureContextMode'))   # structure context: Off
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
    $argv = New-Object object[] 9
    $argv[0] = $general
    $argv[1] = 'eng'
    $argv[2] = 'nld'
    $argv[3] = $metadata
    $argv[4] = $null          # recalled
    $argv[5] = $null          # ownTerms
    $argv[6] = 'INSTRUCTIONS-MARKER'
    $argv[7] = $KB
    $argv[8] = $off
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

# ---- the instrument itself ----------------------------------------------
# Every judgement about caching - including whether #5 paid for its round trip -
# rests on the token line, and the token line rests on this parser. Anthropic
# reports a cache write two ways: a flat cache_creation_input_tokens, and a
# nested cache_creation object broken down by lifetime. Reading only the flat
# one made a real run report a write of zero while the very next request read
# 24,918 tokens back - a cache plainly written and silently unreported.
$client = $plugin.GetType('Supervertaler.Core.LlmClient')
$extract = $client.GetMethod('ExtractClaudeUsage', $Static)
$usageT = $plugin.GetType('Supervertaler.Core.ApiUsage')
function Usage($json) { return $extract.Invoke($null, [object[]]@([string]$json)) }
function Field($u, $name) { return $usageT.GetField($name).GetValue($u) }

$flat = '{"usage":{"input_tokens":1041,"cache_creation_input_tokens":45870,"cache_read_input_tokens":0,"output_tokens":1233}}'
$u = Usage $flat
Check ((Field $u 'CacheWriteTokens') -eq 45870) "the flat cache_creation_input_tokens is read: $(Field $u 'CacheWriteTokens')"
Check ((Field $u 'RegularInputTokens') -eq 1041) "and does not leak into the regular count"

$nested = '{"usage":{"input_tokens":2754,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"cache_creation":{"ephemeral_5m_input_tokens":0,"ephemeral_1h_input_tokens":24918},"output_tokens":5016}}'
$u = Usage $nested
Check ((Field $u 'CacheWriteTokens') -eq 24918) "a write reported only in the nested object is counted: $(Field $u 'CacheWriteTokens')"

$both = '{"usage":{"input_tokens":100,"cache_creation":{"ephemeral_5m_input_tokens":300,"ephemeral_1h_input_tokens":200},"output_tokens":9}}'
$u = Usage $both
Check ((Field $u 'CacheWriteTokens') -eq 500) "two lifetimes are summed, not one preferred: $(Field $u 'CacheWriteTokens')"

# The flat field, when present and non-zero, is authoritative: the nested
# object is only consulted when it is absent or zero, so the two never add up
# to double the real figure.
$flatWins = '{"usage":{"input_tokens":100,"cache_creation_input_tokens":700,"cache_creation":{"ephemeral_5m_input_tokens":700},"output_tokens":9}}'
$u = Usage $flatWins
Check ((Field $u 'CacheWriteTokens') -eq 700) "a write present in both shapes is counted once: $(Field $u 'CacheWriteTokens')"

$read = '{"usage":{"input_tokens":2754,"cache_read_input_tokens":24918,"output_tokens":5016}}'
$u = Usage $read
Check ((Field $u 'CacheReadTokens') -eq 24918 -and (Field $u 'CacheWriteTokens') -eq 0) "a read is a read, and no write is invented for it"

Check ($null -eq (Usage '{"id":"msg_1"}')) "a response with no usage block yields null, so callers fall back to the estimate"

Write-Host ''
Write-Host "CACHING TEST COMPLETE - $fails failure(s)"
