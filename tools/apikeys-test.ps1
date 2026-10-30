# One key file for every Supervertaler product, and two ways to get it wrong.
#
# The first is the Google trap. memoQ shows the user "Google"; core calls the
# model provider "gemini"; and the shared key file keeps "google" for
# Supervertaler Sidekick's Google Translate key, which is a different service.
# Passing the user-facing word through unmapped reads somebody else's key and
# fails with a message about the key being invalid.
#
# The second is this harness. The key files are the user's real ones - the plain
# file in their data folder and, since core's step 1 of
# docs/design/api-key-protection.md, the locked file in their profile - so every
# path that could write them must be inert under SUPERVERTALER_HARNESS. That is
# asserted here against the files themselves, not against the flag. Section 4,
# which has to write, writes copies in a temporary folder: the locked file is
# redirected before anything else runs, and the data root before section 4,
# because redirecting the data root does not move the locked file.
#
#   powershell -NoProfile -File tools\run-harness.ps1 -Harness tools\apikeys-test.ps1
#
# The key handling itself - unreadable keys, the sync, the dialogs - is
# tools\apikeys-lock-test.ps1.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
# Its own checkout, so a worktree's run tests that worktree's build.
$PluginDll = "$PSScriptRoot\..\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll"

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
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'

$apiKeys = $plugin.GetType('Supervertaler.MemoQ.Core.ApiKeys')
$providers = $plugin.GetType('Supervertaler.MemoQ.Settings.LlmProviders')
$store = $plugin.GetType('Supervertaler.Core.ApiKeyStore')

$coreKey = $providers.GetMethod('CoreKey', $Static)
$canonical = $store.GetMethod('Canonical', $Static)
$storeGet = $store.GetMethod('Get', $Static)
$checkShape = $apiKeys.GetMethod('CheckShape', $Static)
$resolve = $apiKeys.GetMethod('Resolve', $Static)
$remember = $apiKeys.GetMethod('Remember', $Static)
$filePath = $store.GetProperty('FilePath', $Static)

$anthropic = $providers.GetField('Anthropic').GetValue($null)
$openai = $providers.GetField('OpenAI').GetValue($null)
$google = $providers.GetField('Google').GetValue($null)

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# The real key files and their neighbours, by name, size and time - never their
# contents, which are real keys. Compared again at the very end.
function Stamp($path) {
    $dir = Split-Path $path
    if (-not (Test-Path -LiteralPath $dir)) { return '' }
    (Get-ChildItem -LiteralPath $dir -Filter 'api-keys*' -Force -File | Sort-Object Name |
        ForEach-Object { '{0}|{1}|{2}|{3}' -f $_.Name, $_.Length, $_.LastWriteTimeUtc.Ticks, $_.CreationTimeUtc.Ticks }) -join ';'
}
$path = $filePath.GetValue($null)
$realLocked = $store.GetProperty('DefaultLockedFilePath', $Static).GetValue($null)
$plainBefore = Stamp $path
$lockedBefore = Stamp $realLocked

# Before anything can save a key: core writes the locked file beside every save,
# and it is in the profile, where no redirected data folder would move it.
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('sv-apikeys-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null
$tmpLocked = Join-Path $tmp 'profile\api-keys.locked.json'
$store.GetMethod('RedirectLockedFile', $Static).Invoke($null, @([string]$tmpLocked))
if ($store.GetProperty('LockedFilePath', $Static).GetValue($null) -ne $tmpLocked) { throw "The locked key file could not be redirected; stopping before anything writes." }

# ---- 1. the Google trap --------------------------------------------------
Check ($coreKey.Invoke($null, [object[]]@($anthropic)) -eq 'claude') "Anthropic maps to claude"
Check ($coreKey.Invoke($null, [object[]]@($openai)) -eq 'openai') "OpenAI maps to openai"
Check ($coreKey.Invoke($null, [object[]]@($google)) -eq 'gemini') "Google maps to gemini, not google"
Check ($null -eq $coreKey.Invoke($null, [object[]]@('no-such-provider'))) "an unknown provider maps to nothing"

# The reason the mapping cannot be skipped: the file really does keep a
# different service under that name, and core will not silently rescue us.
Check ($canonical.Invoke($null, [object[]]@('google')) -ne 'gemini') `
    "the key file's 'google' is Sidekick's Google Translate key, not Gemini"
Check ($canonical.Invoke($null, [object[]]@('anthropic')) -eq 'claude') "'anthropic' is accepted as an alias for claude"

# ---- 2. a key from another service is named before the 401 is ------------
# The hint exists because a provider handed someone else's key answers "the
# API key was refused - incorrect API key provided", which is true and the
# least useful way to say it.
$wrong = $checkShape.Invoke($null, [object[]]@($anthropic, 'sk-proj-abc123'))
Check ($null -ne $wrong -and $wrong -match 'OpenAI') "an OpenAI key in the Anthropic box is named: $wrong"

$wrong2 = $checkShape.Invoke($null, [object[]]@($google, 'sk-ant-abc123'))
Check ($null -ne $wrong2) "an Anthropic key in the Google box is named: $wrong2"

# Every OpenRouter key also starts with OpenAI's "sk-", so a check that tests
# the wanted prefix before identifying the key waves this one straight through.
$wrong3 = $checkShape.Invoke($null, [object[]]@($openai, 'sk-or-v1-abc123'))
Check ($null -ne $wrong3 -and $wrong3 -match 'OpenRouter') "an OpenRouter key in the OpenAI box is named: $wrong3"

Check ($null -eq $checkShape.Invoke($null, [object[]]@($openai, 'sk-proj-real'))) "a real OpenAI key passes"
Check ($null -eq $checkShape.Invoke($null, [object[]]@($anthropic, 'sk-ant-api03-real'))) "a real Anthropic key passes"
Check ($null -eq $checkShape.Invoke($null, [object[]]@($google, 'AIzaSyReal'))) "a real Gemini key passes"
Check ($null -eq $checkShape.Invoke($null, [object[]]@($anthropic, ''))) "an empty box is not an error"

# An unfamiliar shape must pass: a gateway issues keys in its own format, and a
# hint that cried wolf on every one of them would be turned off and ignored.
Check ($null -eq $checkShape.Invoke($null, [object[]]@('no-such-provider', 'whatever-abc'))) `
    "a provider with no known shape accepts anything"

# ---- 3. a harness never touches the real key file ------------------------
$resolved = $resolve.Invoke($null, [object[]]@($anthropic, 'from-the-resource'))
$key = $resolved.GetType().GetField('Key').GetValue($resolved)
$source = $resolved.GetType().GetField('Source').GetValue($resolved)
Check ([string]::IsNullOrEmpty($key)) "resolving under a harness yields no key: '$source'"

Check ($remember.Invoke($null, [object[]]@($anthropic, 'sk-ant-harness-must-not-write-this'))) `
    "remembering a key under a harness reports success without writing"

Check ((Stamp $path) -ceq $plainBefore) "the real key file is untouched after all of that"
Check (-not (Test-Path -LiteralPath $tmpLocked)) "  and nothing reached even the redirected locked file"

# And it is genuinely the shared file, in the shared place, rather than a copy
# somewhere only memoQ looks.
Check ($path -like '*\settings\api-keys.json') "the file is the shared one: $path"
Check ($null -eq $storeGet.Invoke($null, [object[]]@('no-such-provider'))) "an unknown provider has no key"

# ---- 4. what Set's false actually means ---------------------------------
# False must mean the write failed, and nothing else. When it also meant
# "nothing needed writing", every ordinary OK in memoQ's dialog looked like a
# failed save - because a dialog saves every field whether or not it was
# touched, so the commonest case of all is writing the value already there.
#
# In a temporary data folder, with a made-up key standing in for a real entry:
# until core's step 1 this wrote the user's own file and put it back afterwards,
# and a save in memoQ in between would have been lost.
$store_set = $store.GetMethod('Set', $Static)
$probe = 'sv-harness-probe'

$plugin.GetType('Supervertaler.Core.SupervertalerPaths').GetMethod('Set', $Static).Invoke($null, @([string](Join-Path $tmp 'data')))
$tmpPlain = $filePath.GetValue($null)
if (-not ([string]$tmpPlain).StartsWith($tmp)) { throw "The data folder could not be redirected; stopping before anything writes." }
New-Item -ItemType Directory -Path (Split-Path $tmpPlain) -Force | Out-Null
[IO.File]::WriteAllText($tmpPlain, "{`r`n  `"claude`": `"sk-ant-test-standing-entry`"`r`n}`r`n", (New-Object Text.UTF8Encoding $false))
$realBefore = $storeGet.Invoke($null, [object[]]@('claude'))

try {
    Check ($store_set.Invoke($null, [object[]]@($probe, 'first-value'))) "writing a new key succeeds"
    Check ($storeGet.Invoke($null, [object[]]@($probe)) -eq 'first-value') "and it reads back"

    Check ($store_set.Invoke($null, [object[]]@($probe, 'first-value'))) `
        "writing the value already stored succeeds - it needed no write, which is not a failure"

    Check ($store_set.Invoke($null, [object[]]@($probe, 'second-value'))) "changing it succeeds"
    Check ($storeGet.Invoke($null, [object[]]@($probe)) -eq 'second-value') "and the new value reads back"

    Check ($store_set.Invoke($null, [object[]]@($probe, ''))) "removing it succeeds"
    Check ($null -eq $storeGet.Invoke($null, [object[]]@($probe))) "and it is gone"

    Check ($store_set.Invoke($null, [object[]]@($probe, ''))) `
        "removing what is not there succeeds - there was nothing to write"

    # The whole file is rewritten on every Set, so the entries this harness never
    # named are the ones at risk.
    Check ($realBefore -eq 'sk-ant-test-standing-entry' -and $storeGet.Invoke($null, [object[]]@('claude')) -eq $realBefore) `
        "a write for one provider leaves the others alone"

    # Core's step 1: every save is also locked, to the redirected locked file.
    Check ((Test-Path -LiteralPath $tmpLocked) -and -not ([IO.File]::ReadAllText($tmpLocked)).Contains('second-value')) `
        "the saves went to the redirected locked file too, locked rather than in clear text"
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Check ((Stamp $path) -ceq $plainBefore) "the real plain key file and its neighbours are untouched ($path)"
Check ((Stamp $realLocked) -ceq $lockedBefore) "the real locked key file and its neighbours are untouched ($realLocked)"

Write-Host ''
Write-Host "API KEYS TEST COMPLETE - $fails failure(s)"
