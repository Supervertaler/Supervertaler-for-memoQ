# A short list by default, the provider's whole inventory on request.
#
# The thing worth testing here is not the HTTP call. It is that a dialog can ask
# what to show without blocking, spending anything or revealing a key; that the
# short list is short and every row in it says something; that ticking "show
# all" adds the fetched list without losing the recommended few or repeating a
# model; and that a corrupt or half-written cache file degrades to a shorter
# list rather than an exception inside a form's constructor.
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
# The class is internal but its members are public, so both flags are needed.
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'

$catalog = $plugin.GetType('Supervertaler.MemoQ.Core.ModelCatalog')
$providers = $plugin.GetType('Supervertaler.MemoQ.Settings.LlmProviders')
$llmModels = $plugin.GetType('Supervertaler.Core.LlmModels')

$coreKey = $catalog.GetMethod('CoreKey', $Static)
$canFetch = $catalog.GetMethod('CanFetch', $Static)
$curated = $catalog.GetMethod('Curated', $Static)
$entries = $catalog.GetMethod('Entries', $Static)
$extraCount = $catalog.GetMethod('ExtraCount', $Static)
$fetched = $catalog.GetMethod('Fetched', $Static)
$fetchedOn = $catalog.GetMethod('FetchedOn', $Static)
$fetchAsync = $catalog.GetMethod('FetchAsync', $Static)
$cacheFile = $catalog.GetMethod('CacheFile', $Static)

$anthropic = $providers.GetField('Anthropic').GetValue($null)
$openai = $providers.GetField('OpenAI').GetValue($null)
$google = $providers.GetField('Google').GetValue($null)

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

function Ids($list) { @($list | ForEach-Object { $_.GetType().GetField('Id').GetValue($_) }) }
function Names($list) { @($list | ForEach-Object { $_.GetType().GetField('DisplayName').GetValue($_) }) }
function Descs($list) { @($list | ForEach-Object { $_.GetType().GetField('Description').GetValue($_) }) }

# ---- 1. memoQ's three provider names reach core's nine -------------------
# memoQ calls them Anthropic, OpenAI and Google; core calls them claude, openai
# and gemini. A mistranslation here shows as an empty dropdown, not an error.
Check ($coreKey.Invoke($null, [object[]]@($anthropic)) -eq 'claude') "Anthropic maps to claude"
Check ($coreKey.Invoke($null, [object[]]@($openai)) -eq 'openai') "OpenAI maps to openai"
Check ($coreKey.Invoke($null, [object[]]@($google)) -eq 'gemini') "Google maps to gemini"
Check ($null -eq $coreKey.Invoke($null, [object[]]@('no-such-provider'))) "an unknown provider maps to nothing"

# ---- 2. the short list is short, and every row says something ------------
foreach ($p in @($anthropic, $openai, $google)) {
    $short = $curated.Invoke($null, [object[]]@($p))
    Check ($short.Count -ge 3 -and $short.Count -le 8) "$p short list is $($short.Count) models"

    $blank = @(Descs $short | Where-Object { [string]::IsNullOrWhiteSpace($_) })
    Check ($blank.Count -eq 0) "$p every model carries a verdict"

    $ids = Ids $short
    Check (($ids | Sort-Object -Unique).Count -eq $ids.Count) "$p no id appears twice"
}

# ---- 3. the current judgement, as the handoff describes it ---------------
# Superseded models are removed rather than annotated, so their absence is the
# assertion. This is the check that fails the release after the short list is
# re-judged and one product is rebuilt without the other.
$claude = Ids ($curated.Invoke($null, [object[]]@($anthropic)))
Check ($claude -contains 'claude-fable-5-1') "Fable 5.1 is on the short list"
Check ($claude -notcontains 'claude-fable-5') "Fable 5 is gone, superseded"
$gpt = Ids ($curated.Invoke($null, [object[]]@($openai)))
Check ($gpt -contains 'gpt-5.6-sol') "GPT-5.6 Sol is on the short list"
Check ($gpt -notcontains 'gpt-5.5') "GPT-5.5 is gone, superseded by 5.6 Sol"

# ---- 4. the dropdown label ----------------------------------------------
# The name with its verdict after it, because a name alone is what made the old
# list unusable. A fetched model has no verdict and shows its name alone.
$first = ($curated.Invoke($null, [object[]]@($anthropic)))[0]
$label = $first.ToString()
$name = $first.GetType().GetField('DisplayName').GetValue($first)
Check ($label.StartsWith($name) -and $label.Contains([char]0x2013)) "labelled with a verdict: $label"

$entryType = $catalog.GetNestedType('Entry', [Reflection.BindingFlags]'Public,NonPublic')
$bare = [Activator]::CreateInstance($entryType)
$entryType.GetField('Id').SetValue($bare, 'some-model-id')
Check ($bare.ToString() -eq 'some-model-id') "a model with no name at all shows its id"

# ---- 5. off means off ----------------------------------------------------
# Everything below writes a cache file. run-harness.ps1 snapshots the models
# folder, so a real fetched list is put back afterwards.
$path = $cacheFile.Invoke($null, [object[]]@($anthropic))
$lines = @(
    "claude-opus-5`tClaude Opus 5",     # already curated: must not appear twice
    "claude-experimental-9`tClaude Experimental 9",
    "twin-name-a`tTwin Name",
    "twin-name-b`tTwin Name",
    "",                                  # blank lines are skipped
    "`tno id here",                      # so is a row with no id
    "claude-unnamed"                     # an id with no display name is legal
)
[IO.File]::WriteAllLines($path, $lines, (New-Object Text.UTF8Encoding($false)))

$short = $entries.Invoke($null, [object[]]@($anthropic, $false))
$shortIds = Ids $short
Check ($shortIds -notcontains 'claude-experimental-9') "off: a fetched model stays out of the list"
Check ($shortIds.Count -eq $claude.Count) "off: the list is exactly the short list"

# ---- 6. on means the short list first, then the rest --------------------
$all = $entries.Invoke($null, [object[]]@($anthropic, $true))
$allIds = Ids $all
Check ($allIds.Count -gt $shortIds.Count) "on: the list grows to $($allIds.Count)"
Check (@($allIds[0..($shortIds.Count - 1)] | Where-Object { $shortIds -notcontains $_ }).Count -eq 0) `
    "on: the recommended few are still first"
Check ($allIds -contains 'claude-experimental-9') "on: a model released after this build is reachable"
Check ((@($allIds | Where-Object { $_ -eq 'claude-opus-5' })).Count -eq 1) `
    "on: a model in both lists appears once, keeping its verdict"

$parsed = $fetched.Invoke($null, [object[]]@($anthropic))
Check ($parsed.Count -eq 5) "cache parsed, junk rows dropped: $($parsed.Count) of 7 lines"

# ---- 7. two models, one name --------------------------------------------
# Google returns three models called "Nano Banana Pro". Two identical rows in a
# dropdown is a coin toss, so those - and only those - carry their id.
$allNames = Names $all
Check (@($allNames | Where-Object { $_ -eq 'Twin Name' }).Count -eq 0) "a shared display name is not left ambiguous"
Check (@($allNames | Where-Object { $_ -eq 'Twin Name (twin-name-a)' }).Count -eq 1) "the ambiguous one carries its id"
Check (@($allNames | Where-Object { $_ -eq 'Claude Experimental 9' }).Count -eq 1) "an unambiguous one is left readable"

# ---- 8. what the status line reports ------------------------------------
Check ($extraCount.Invoke($null, [object[]]@($anthropic)) -eq 4) `
    "counted 4 beyond the short list: $($extraCount.Invoke($null, [object[]]@($anthropic)))"
Check ($null -ne $fetchedOn.Invoke($null, [object[]]@($anthropic))) "a fetched list is dated"
Check ($null -eq $fetchedOn.Invoke($null, [object[]]@('never-fetched-provider'))) "a provider never fetched has no date"

# ---- 9. a corrupt cache is a shorter list, not an exception -------------
[IO.File]::WriteAllBytes($path, [byte[]]@(0xFF, 0xFE, 0x00, 0x01, 0x02))
$junk = $entries.Invoke($null, [object[]]@($anthropic, $true))
Check ((Ids $junk).Count -ge $claude.Count) "a corrupt cache still shows the short list: $((Ids $junk).Count)"
Remove-Item $path -ErrorAction SilentlyContinue

# ---- 10. no key, no call ------------------------------------------------
# The dialog opens before a key is necessarily configured, and an
# unauthenticated request is a wasted round trip at best.
$ct = [System.Threading.CancellationToken]::None
$argv = [object[]]::new(4)
$argv[0] = $anthropic; $argv[1] = ''; $argv[2] = ''; $argv[3] = $ct
$task = $fetchAsync.Invoke($null, $argv)
$task.Wait()
Check ($null -eq $task.Result) 'no API key means no request at all'

$argv2 = [object[]]::new(4)
$argv2[0] = 'no-such-provider'; $argv2[1] = 'a-key'; $argv2[2] = ''; $argv2[3] = $ct
$task2 = $fetchAsync.Invoke($null, $argv2)
$task2.Wait()
Check ($null -eq $task2.Result) 'a provider we cannot list returns null, keeping the short list on screen'

# ---- 11. all three providers can be asked -------------------------------
# Google could not be, before core's fetch replaced the two hand-written ones.
Check ($canFetch.Invoke($null, [object[]]@($anthropic))) "Anthropic can be asked for its list"
Check ($canFetch.Invoke($null, [object[]]@($openai))) "OpenAI can be asked for its list"
Check ($canFetch.Invoke($null, [object[]]@($google))) "Google can be asked for its list"
Check (-not $canFetch.Invoke($null, [object[]]@('no-such-provider'))) "an unknown provider cannot"

# ---- 12. both dialogs actually offer the list ---------------------------
# The catalogue being right is worth nothing if a dialog still shows a bare
# text box, which is what memoQ's own Configure plugin dialog did until now -
# the one the setup guide sends people to first. Constructed headlessly, so
# this fails at build time rather than the next time someone opens it.
Add-Type -AssemblyName System.Windows.Forms

$Instance = [Reflection.BindingFlags]'NonPublic,Instance'
$Ctor = [Reflection.BindingFlags]'Instance,Public,NonPublic'

function ModelBox($form, $field) {
    return $form.GetType().GetField($field, $Instance).GetValue($form)
}

$generalT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings')
$secureT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSecureSettings')
$settingsT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSettings')
$settings = $settingsT.GetMethod('Create').Invoke($null,
    @([Activator]::CreateInstance($generalT), [Activator]::CreateInstance($secureT)))

$optionsT = $plugin.GetType('Supervertaler.MemoQ.Settings.OptionsForm')
$options = [Activator]::CreateInstance($optionsT, $Ctor, $null, @($settings), $null)
try {
    $box = ModelBox $options '_model'
    Check ($box -is [Windows.Forms.ComboBox]) "memoQ's own dialog offers a list, not a bare box: $($box.GetType().Name)"
    Check ($box.DropDownStyle -eq [Windows.Forms.ComboBoxStyle]::DropDown) "and it is still typeable"
    Check ($box.Items.Count -ge 3) "filled with the short list: $($box.Items.Count) models"

    $labelled = @($box.Items | Where-Object { $_.ToString().Contains([char]0x2013) })
    Check ($labelled.Count -eq $box.Items.Count) "every row carries its verdict"

    $tick = ModelBox $options '_showAllModels'
    Check (-not $tick.Checked) "the short list is what it opens on"
    Check ((ModelBox $options '_fetchModels').Enabled) "and the provider can be asked for the rest"
}
finally { $options.Dispose() }

$editorExe = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe'
$editor = [Reflection.Assembly]::LoadFrom($editorExe)
$settingsFormT = $editor.GetType('Supervertaler.PromptEditor.SettingsForm')
$settingsForm = [Activator]::CreateInstance($settingsFormT, $Ctor, $null, @(), $null)
try {
    $box = ModelBox $settingsForm '_model'
    Check ($box.Items.Count -ge 3) "the editor's dialog is filled the same way: $($box.Items.Count) models"
    Check (-not (ModelBox $settingsForm '_showAllModels').Checked) "and opens on the short list too"
}
finally { $settingsForm.Dispose() }

# ---- 13. switching provider switches the key and the model --------------
# Both dialogs used to fill the API key box once, at load, for whichever
# provider they opened on - so choosing OpenAI and pressing Fetch list sent an
# Anthropic key to OpenAI and got back a 401 that blamed the key. The model
# came over too: Google with claude-opus-5 selected is a pair that can only
# fail at the provider.
#
# Key resolution is suppressed under SUPERVERTALER_HARNESS, so every provider
# inherits an empty key here. That is enough: a box still holding the previous
# provider's value is exactly the fault, and a typed value coming back proves
# the per-provider memory.
$settingsForm = [Activator]::CreateInstance($settingsFormT, $Ctor, $null, @(), $null)
try {
    $providerBox = ModelBox $settingsForm '_provider'
    $keyBox = ModelBox $settingsForm '_apiKey'
    $modelBox = ModelBox $settingsForm '_model'

    $providerBox.SelectedItem = $anthropic
    $keyBox.Text = 'typed-for-anthropic'

    $providerBox.SelectedItem = $openai
    Check ($keyBox.Text -ne 'typed-for-anthropic') `
        "switching provider does not leave the old provider's key in the box: '$($keyBox.Text)'"

    $firstOpenAi = (Ids ($curated.Invoke($null, [object[]]@($openai))))[0]
    $chosen = $settingsFormT.GetMethod('ChosenModelId', $Instance).Invoke($settingsForm, @())
    Check ($chosen -eq $firstOpenAi) "and moves the model to the new provider's first recommendation: $chosen"

    $providerBox.SelectedItem = $anthropic
    Check ($keyBox.Text -eq 'typed-for-anthropic') "a key typed for a provider comes back on returning to it"
}
finally { $settingsForm.Dispose() }

Write-Host ''
Write-Host "MODEL CATALOG TEST COMPLETE - $fails failure(s)"
