# The team folder (core e2c44a0) as memoQ reports it, and the OpenAI default.
#
# Core decides once per process whether the team folder - shared memory banks
# and prompts - is in use, and falls back to the user's own data folder when it
# does not answer. memoQ must never fall back silently: the AI would be handed
# the user's own banks while they believe it has the team's. And memoQ runs two
# processes (the plugin inside memoQ, and the editor) that decide separately.
#
# Nothing here reads or writes config.json. A fallback is simulated by setting
# core's cached decision in this process only, then put back.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$PluginDll = 'D:\SynologyDrive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll'

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
$B = [Reflection.BindingFlags]'Public,NonPublic,Static'

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

$paths = $plugin.GetType('Supervertaler.Core.SupervertalerPaths')
$status = $plugin.GetType('Supervertaler.MemoQ.Core.TeamFolderStatus')
$line = $status.GetMethod('Line', $B)
$fellBack = $status.GetProperty('FellBack', $B)
$mismatch = $status.GetMethod('Mismatch', $B)
$withTeam = $plugin.GetType('Supervertaler.MemoQ.Core.SuperMemory').GetMethod('WithTeamFolder', $B)

function L { return $line.Invoke($null, @()) }
function FB { return [bool]$fellBack.GetValue($null) }
function MM($p) { return $mismatch.Invoke($null, [object[]]@([string]$p)) }
function WT($n) { return $withTeam.Invoke($null, [object[]]@($n)) }

# Force core's decision now, then remember it, so the simulation can be undone.
$root = $paths.GetProperty('ContentRoot', $B).GetValue($null)
$fContent = $paths.GetField('_contentRoot', $B)
$fTeam = $paths.GetField('_teamFolder', $B)
$fProblem = $paths.GetField('_teamFolderProblem', $B)
Check ($null -ne $fContent -and $null -ne $fTeam -and $null -ne $fProblem) "core's cached decision is where the test expects it"
$saved = @($fContent.GetValue($null), $fTeam.GetValue($null), $fProblem.GetValue($null))

try {
    # ---- 1. no team folder: nothing to say ------------------------------
    $fTeam.SetValue($null, $null); $fProblem.SetValue($null, $null)
    Check ($null -eq (L)) "with no team folder set, nothing is said"
    Check (-not (FB)) "  and nothing has fallen back"
    Check ((WT 'Pick a bank.') -eq 'Pick a bank.') "  and the bank list's note is unchanged"
    Check ($null -eq (WT $null)) "  including no note at all"

    # ---- 2. team folder in use ------------------------------------------
    $fContent.SetValue($null, '\\server\team'); $fTeam.SetValue($null, '\\server\team'); $fProblem.SetValue($null, $null)
    Check ((L) -match 'come from the team folder' -and (L) -match [regex]::Escape('\\server\team')) "in use: says where banks and prompts come from"
    Check (-not (FB)) "  and is not a fallback"
    Check ((WT $null) -eq $null) "  and the assistant is not warned"

    # ---- 3. team folder set but not answering: never silent ---------------
    $problem = 'The team folder "\\server\team" did not answer within 3 seconds, so your own data folder is used until you restart.'
    $fContent.SetValue($null, $root); $fProblem.SetValue($null, $problem)
    Check ((L) -eq $problem) "fallen back: core's reason is what is shown"
    Check ((L) -notmatch 'Trados Studio') "  and it does not tell a memoQ user to restart Trados"
    Check (FB) "  and it counts as a fallback"
    $note = WT 'Pick a bank.'
    Check ($note -match '^WARNING:' -and $note -match 'own memory banks, not the team' -and $note -match 'Pick a bank\.$') "  the assistant's bank list leads with a warning and keeps its note"

    # ---- 4. plugin and editor disagree ------------------------------------
    Check ($null -eq (MM $null)) "no answer from memoQ is not a mismatch"
    Check ($null -eq (MM $root)) "the same folder is not a mismatch"
    Check ($null -eq (MM ($root.ToUpperInvariant() + '\'))) "  nor is the same folder in other case with a trailing slash"
    $m = MM '\\server\team'
    Check ($m -match [regex]::Escape('\\server\team') -and $m -match [regex]::Escape($root) -and $m -match 'start them again') "a different folder names both and says what to do"
}
finally {
    $fContent.SetValue($null, $saved[0]); $fTeam.SetValue($null, $saved[1]); $fProblem.SetValue($null, $saved[2])
}

# ---- 5. a fresh OpenAI setup lands on GPT-6.1 Sol, not the first entry ------
$catalog = $plugin.GetType('Supervertaler.MemoQ.Core.ModelCatalog')
$providers = $plugin.GetType('Supervertaler.MemoQ.Settings.LlmProviders')
$openAi = $providers.GetField('OpenAI', $B).GetValue($null)
$anthropic = $providers.GetField('Anthropic', $B).GetValue($null)
$def = $catalog.GetMethod('DefaultModelId', $B)
$curated = $catalog.GetMethod('Curated', $B)

$firstOpenAi = @($curated.Invoke($null, @($openAi)))[0].Id
Check ($def.Invoke($null, @($openAi)) -eq 'gpt-6.1-sol') "OpenAI's default is GPT-6.1 Sol ($($def.Invoke($null, @($openAi))))"
Check ($firstOpenAi -ne 'gpt-6.1-sol') "  which is not the list's first entry ($firstOpenAi), so the pickers must ask for it"
Check ($def.Invoke($null, @($anthropic)) -eq @($curated.Invoke($null, @($anthropic)))[0].Id) "other providers keep their first entry"

foreach ($form in 'Supervertaler.MemoQ\Settings\OptionsForm.cs', 'Supervertaler.PromptEditor\SettingsForm.cs') {
    $src = Get-Content "D:\SynologyDrive\Dev\Sv\Supervertaler-for-memoQ\src\$form" -Raw
    Check ($src -match 'ModelCatalog\.DefaultModelId\(') "$(Split-Path $form -Leaf) selects the provider's default when nothing is configured"
}

Write-Host ''
Write-Host "TEAM FOLDER TEST COMPLETE - $fails failure(s)"
