# API keys locked to the computer, step 1, as memoQ uses them (core's
# docs/design/api-key-protection.md: section 4 "memoQ", the step-1 rules in
# section 5, the messages in section 7).
#
# Core keeps every key twice during step 1: the plain file in the data folder,
# written as before, and a file in the user's profile whose values only this
# Windows account can unlock. This checks what memoQ does with that, without
# memoQ and without a real key:
#
#   1. a key this computer cannot unlock gets core's sentence, never a blank key
#      and never an older one from memoQ's own leftovers;
#   2. a damaged locked file the same, then the start-up sync sets it aside and
#      says so once;
#   3. a key saved by an older memoQ, or by Trados, is seen, and locked by the sync;
#   4. the migration never puts an older key over one this computer cannot read;
#   5. an empty key box saved over such a key keeps it;
#   6. the request guard: a provider is never sent a blank key in place of one;
#   7. both settings dialogs show core's note in full under the empty key box;
#   8. end to end through memoQ's session: the refusal is what memoQ shows, and
#      nothing is sent.
#
# Every place a key is read from is pointed at a temporary folder FIRST: the data
# root, the locked file (in the profile, which redirecting the data root does not
# move) and memoQ's shared.txt. The real files are compared by name, size and time
# before and after - never opened.
#
#   powershell -NoProfile -File tools\apikeys-lock-test.ps1
#
# Sections 1-7 run without SUPERVERTALER_HARNESS, which would hide the key stores
# from the code under test; section 8 sets it, as its engine needs. Through
# tools\run-harness.ps1 works too. Section 8 is skipped while memoQ is running.
param(
    [string]$Root = (Split-Path $PSScriptRoot -Parent),
    [string]$MemoQPath = 'C:\Program Files\memoQ\memoQ-12',
    [string]$Config = 'Release'
)
$ErrorActionPreference = 'Stop'
$PluginDll = "$Root\src\Supervertaler.MemoQ\bin\$Config\Supervertaler.MemoQ.dll"
$EditorExe = "$Root\src\Supervertaler.PromptEditor\bin\$Config\Supervertaler.PromptEditor.exe"
Write-Host "plugin: $PluginDll"

# Compiled, not a scriptblock: the session works on thread-pool threads, where a
# scriptblock handler has no runspace (see bridgemode-test.ps1).
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public static class ApiKeysLockResolver
{
    static string[] probe;
    [ThreadStatic] static Dictionary<string, bool> inFlight;

    public static void Attach(string[] dirs)
    {
        probe = dirs;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name;
        if (inFlight == null) inFlight = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (inFlight.ContainsKey(name)) return null;
        inFlight[name] = true;
        try
        {
            foreach (string dir in probe)
            {
                string candidate = Path.Combine(dir, name + ".dll");
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        }
        catch { return null; }
        finally { inFlight.Remove(name); }
    }
}
'@
[ApiKeysLockResolver]::Attach([string[]]@($MemoQPath, "$MemoQPath\Addins", (Split-Path $PluginDll)))
Add-Type -AssemblyName System.Security
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$plugin = [Reflection.Assembly]::LoadFrom($PluginDll)
$editor = [Reflection.Assembly]::LoadFrom($EditorExe)
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'
$Instance = [Reflection.BindingFlags]'Public,NonPublic,Instance'

$fails = 0; $total = 0
function Check($ok, $label) {
    $script:total++
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# ---- the real files, before anything -------------------------------------------
# Name, size and times of every api-keys* file beside each real one, and of the
# real shared.txt. Never their contents: they hold real keys.
$realStore = $plugin.GetType('Supervertaler.Core.ApiKeyStore')
$realPlain = [string]$realStore.GetProperty('FilePath', $Static).GetValue($null)
$realLocked = [string]$realStore.GetProperty('DefaultLockedFilePath', $Static).GetValue($null)
$realShared = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Supervertaler.memoQ\shared.txt'
function Stamp($path, $pattern) {
    $dir = Split-Path $path
    $files = @()
    if (Test-Path -LiteralPath $dir) { $files = @(Get-ChildItem -LiteralPath $dir -Filter $pattern -Force -File | Sort-Object Name) }
    ($files | ForEach-Object { '{0}|{1}|{2}|{3}' -f $_.Name, $_.Length, $_.LastWriteTimeUtc.Ticks, $_.CreationTimeUtc.Ticks }) -join ';'
}
$realBefore = @{
    plain  = Stamp $realPlain 'api-keys*'
    locked = Stamp $realLocked 'api-keys*'
    shared = Stamp $realShared 'shared.txt'
}
Write-Host "real plain file:  $realPlain"
Write-Host "real locked file: $realLocked"

# ---- every key store into a temporary folder ------------------------------------
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('sv-apikeys-lock-' + [Guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $tmp 'data'
$profileDir = Join-Path $tmp 'profile'
$localDir = Join-Path $tmp 'local'
New-Item -ItemType Directory -Path $dataRoot, $profileDir, $localDir | Out-Null
$PlainPath = Join-Path $dataRoot 'settings\api-keys.json'
$LockedPath = Join-Path $profileDir 'api-keys.locked.json'
$TradosPath = Join-Path $dataRoot 'trados\settings\settings.json'
New-Item -ItemType Directory -Path (Split-Path $PlainPath), (Split-Path $TradosPath) | Out-Null

# The plugin and the editor each compile core and memoQ's key code in, so each has
# its own copy of these to redirect.
function Redirect($asm) {
    $asm.GetType('Supervertaler.Core.SupervertalerPaths').GetMethod('Set', $Static).Invoke($null, @([string]$dataRoot))
    $asm.GetType('Supervertaler.Core.ApiKeyStore').GetMethod('RedirectLockedFile', $Static).Invoke($null, @([string]$LockedPath))
    $asm.GetType('Supervertaler.MemoQ.Core.SharedSettings').GetProperty('DirectoryOverride', $Static).SetValue($null, [string]$localDir)
    $s = $asm.GetType('Supervertaler.Core.ApiKeyStore')
    $ok = ($s.GetProperty('FilePath', $Static).GetValue($null) -eq $PlainPath) `
        -and ($s.GetProperty('LockedFilePath', $Static).GetValue($null) -eq $LockedPath) `
        -and ($asm.GetType('Supervertaler.MemoQ.Core.SharedSettings').GetProperty('Path', $Static).GetValue($null) -eq (Join-Path $localDir 'shared.txt'))
    if (-not $ok) { throw "Could not point $($asm.GetName().Name)'s key stores at $tmp; stopping before anything is read or written." }
}
Redirect $plugin
Redirect $editor
Check $true "every key store points at $tmp (plugin and editor)"

$wasHarness = $env:SUPERVERTALER_HARNESS
Remove-Item Env:SUPERVERTALER_HARNESS -ErrorAction SilentlyContinue

$apiKeys = $plugin.GetType('Supervertaler.MemoQ.Core.ApiKeys')
$shared = $plugin.GetType('Supervertaler.MemoQ.Core.SharedSettings')
$store = $realStore
$resolve = $apiKeys.GetMethod('Resolve', $Static)
$forRequest = $apiKeys.GetMethod('ForRequest', $Static)
$unreadableNote = $apiKeys.GetMethod('UnreadableNote', $Static)
$takeNotice = $apiKeys.GetMethod('TakeSyncNotice', $Static)
$remember = $apiKeys.GetMethod('Remember', $Static)
$status = $store.GetMethod('Status', $Static)

# What core logs, to check no key ever appears in it. Synchronous here; cleared
# before section 8, whose work runs on other threads.
$logLines = New-Object Collections.Generic.List[string]
$store.GetProperty('Log', $Static).SetValue($null, [Action[string]] { param($m) $logLines.Add($m) })

# Made-up keys, one per source, so that whichever one comes back says where from.
$K = @{
    plain    = 'sk-ant-test-plain-0001'
    lockedA  = 'sk-ant-test-locked-0002'
    legacy   = 'sk-ant-test-legacy-own-0003'
    legacyMq = 'sk-ant-test-legacy-memoq-0004'
    resource = 'sk-ant-test-resource-0005'
    trados   = 'sk-proj-test-trados-0006'
    openai   = 'sk-proj-test-openai-0007'
    custom   = 'test-custom-server-0008'
    newkey   = 'sk-ant-test-pasted-again-0009'
}

$Utf8 = New-Object Text.UTF8Encoding $false
function Lock([string]$text, [byte[]]$entropy) {
    [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes($text), $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser))
}
# Locked with an entropy value, so this account's plain unlock refuses it: the
# same refusal as a key saved under another sign-in or before a password reset.
function Unreadable([string]$text) { Lock $text ([byte[]](7, 7, 7, 7)) }

function WriteJson($path, [System.Collections.Specialized.OrderedDictionary]$entries, [string]$comment) {
    $lines = New-Object Collections.Generic.List[string]
    $lines.Add('{'); $lines.Add("  `"_comment`": `"$comment`"")
    foreach ($k in $entries.Keys) { $lines[$lines.Count - 1] += ','; $lines.Add("  `"$k`": `"$($entries[$k])`"") }
    $lines.Add('}')
    [IO.File]::WriteAllText($path, ($lines -join "`r`n") + "`r`n", $Utf8)
}
function Plain([System.Collections.Specialized.OrderedDictionary]$e) { WriteJson $PlainPath $e 'written as an older Supervertaler does' }
function Locked([System.Collections.Specialized.OrderedDictionary]$e) { WriteJson $LockedPath $e 'test' }
function Trados([string]$openai) {
    # With the byte order mark Trados writes.
    $json = '{"aiSettings":{"apiKeys":{"openai":"' + $openai + '"}},"other":1}'
    [IO.File]::WriteAllText($TradosPath, $json, (New-Object Text.UTF8Encoding $true))
}
function SharedTxt([hashtable]$values) {
    $text = ($values.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join "`r`n"
    [IO.File]::WriteAllText((Join-Path $localDir 'shared.txt'), $text + "`r`n", (New-Object Text.UTF8Encoding $true))
    # The settings cache would otherwise wait out its freshness interval.
    $shared.GetField('_map', $Static).SetValue($null, $null)
}

# A fresh start: no files, and the once-per-process migration and sync not yet run.
function Fresh {
    foreach ($p in $PlainPath, $LockedPath, $TradosPath, (Join-Path $localDir 'shared.txt')) { Remove-Item -LiteralPath $p -Force -ErrorAction SilentlyContinue }
    Get-ChildItem -LiteralPath $profileDir -Force | Remove-Item -Force
    $shared.GetField('_map', $Static).SetValue($null, $null)
    $apiKeys.GetField('_migrated', $Static).SetValue($null, $false)
    $apiKeys.GetField('_syncNotice', $Static).SetValue($null, $null)
}
# Run the once-per-process work as already done, to look at one step alone.
function Migrated { $apiKeys.GetField('_migrated', $Static).SetValue($null, $true) }

function Resolve($provider, $fromResource) {
    $r = $resolve.Invoke($null, [object[]]@($provider, $fromResource))
    [pscustomobject]@{
        Key     = [string]$r.GetType().GetField('Key').GetValue($r)
        Source  = [string]$r.GetType().GetField('Source').GetValue($r)
        Problem = $r.GetType().GetField('Problem').GetValue($r)
    }
}
function Refusal($provider, $fromResource) {
    try { $k = $forRequest.Invoke($null, [object[]]@($provider, $fromResource)); return $null }
    catch { return $_.Exception.InnerException }
}
function Status($id) { [string]$status.Invoke($null, @($id)) }
function LockedRaw { if (Test-Path -LiteralPath $LockedPath) { [IO.File]::ReadAllText($LockedPath) } else { '' } }
function Entry($id) {
    $raw = LockedRaw
    if ($raw -match ('"' + [regex]::Escape($id) + '":\s*"([^"]*)"')) { $Matches[1] } else { $null }
}

$M2 = "Can't use your Anthropic key – it can't be read on this computer (saved under another Windows sign-in, " `
    + "or your Windows password was reset). Paste it in again in Supervertaler's translation settings."
$M1 = "Your Anthropic key can't be read on this computer – it was saved under another Windows sign-in, " `
    + "or your Windows password was reset. Paste the key in again; nothing else is affected."

try {
    # ---- 1. a key this computer cannot unlock -------------------------------------
    Write-Host '-- 1. a key this computer cannot unlock'
    Fresh
    $blob = Unreadable $K.lockedA
    Locked ([ordered]@{ claude = $blob })
    # Every older copy memoQ could fall back to is there, and each would be wrong.
    SharedTxt @{ provider = 'Anthropic'; apikey = $K.legacy; 'apikey.memoq' = $K.legacyMq }
    Trados $K.trados

    $r = Resolve 'Anthropic' $K.resource
    Check ($r.Key -eq '') "no key is resolved (got $(if ($r.Key) { 'one of the test keys' } else { 'none' }))"
    Check ($r.Problem -ceq $M2) "the problem is core's sentence, M2, naming memoQ's settings: $($r.Problem)"
    Check ($r.Source -ceq $M2) '  and it is also where the key came from, for anything that shows Source'
    $ex = Refusal 'Anthropic' $K.resource
    Check ($ex -is [InvalidOperationException] -and $ex.Message -ceq $M2) "a request is refused with it, before any client is made"
    Check ((Status 'claude') -eq 'Unreadable') 'core says Unreadable'
    Check ((Entry 'claude') -ceq $blob) 'the stored entry is untouched'
    Check (-not (Test-Path -LiteralPath $PlainPath) -or -not ((Get-Content -LiteralPath $PlainPath -Raw) -match 'claude')) `
        "the migration did not put memoQ's or Trados's older key in front of it"
    Check (($unreadableNote.Invoke($null, @('Anthropic'))) -ceq $M1) 'the settings note is core''s M1'
    Check ($null -eq $unreadableNote.Invoke($null, @('OpenAI'))) '  and only for that provider'
    Check ($null -eq $unreadableNote.Invoke($null, @('Ollama'))) '  never for Ollama, which has no key'

    # OpenAI from Trados was absent everywhere, so it was migrated - the rule only
    # protects what is there.
    $o = Resolve 'OpenAI' $null
    Check ($o.Key -ceq $K.trados -and $null -eq $o.Problem) "an absent key is still migrated from Trados's settings (OpenAI)"

    # ---- 2. a damaged locked file ------------------------------------------------
    Write-Host '-- 2. a damaged locked file'
    Fresh
    Plain ([ordered]@{ openai = $K.openai })
    [IO.File]::WriteAllText($LockedPath, "{`r`n  `"claude`": `"AQAAANCMnd8B", $Utf8)   # cut off mid-write
    SharedTxt @{ provider = 'Anthropic'; apikey = $K.legacy }
    Migrated   # look at the lookup alone first; the sync below changes the file

    $r = Resolve 'Anthropic' $K.resource
    Check ($r.Key -eq '' -and $r.Problem -like "Can't use your Anthropic key – the file holding your locked API keys is damaged: $LockedPath. Paste the key in again in Supervertaler's translation settings.") `
        "a damaged file is core's damaged-file sentence, not an older key: $($r.Problem)"
    $note = $unreadableNote.Invoke($null, @('Anthropic'))
    Check ($note -like "*file holding your locked API keys is damaged*set aside, not deleted.") "  and the settings note says so: $note"
    Check ((Resolve 'OpenAI' $null).Key -ceq $K.openai) '  while a key the plain file has is used as before'

    # The start-up sync: the damaged file is copied aside byte for byte and replaced.
    $apiKeys.GetField('_migrated', $Static).SetValue($null, $false)
    $notice = $takeNotice.Invoke($null, @())
    $aside = @(Get-ChildItem -LiteralPath $profileDir -Filter 'api-keys.locked.damaged-*.json')
    Check ($aside.Count -eq 1) "the sync set the damaged file aside ($($aside.Name))"
    Check ($notice -like "The file holding your locked API keys was damaged and has been set aside as $profileDir\api-keys.locked.damaged-*.json. Please paste your keys in again.") `
        "the notice is core's M6, for the settings window: $notice"
    Check ($null -eq $takeNotice.Invoke($null, @())) '  given once'
    Check ((Status 'openai') -eq 'Present' -and (Entry 'openai') -and (Entry 'openai') -notmatch 'sk-') '  and the plain key is now locked in the new file'

    # ---- 3. keys saved by an older memoQ, or by Trados ----------------------------
    Write-Host '-- 3. keys saved by an older memoQ or by Trados'
    Fresh
    Plain ([ordered]@{ claude = $K.plain })   # what memoQ 0.1.28 and Trados today write: the plain file only
    SharedTxt @{ provider = 'Anthropic' }
    $r = Resolve 'Anthropic' $null
    Check ($r.Key -ceq $K.plain -and $r.Source -eq 'the shared key file') 'a key in the plain file only is used'
    $raw = LockedRaw
    Check ((Entry 'claude') -ne $null) 'the first lookup ran the sync, which locked it'
    Check (-not $raw.Contains($K.plain) -and -not ([Text.Encoding]::Unicode.GetString([Text.Encoding]::UTF8.GetBytes($raw))).Contains($K.plain)) `
        '  and the key text appears nowhere in the locked file'
    Remove-Item -LiteralPath $PlainPath -Force
    Check ((Resolve 'Anthropic' $null).Key -ceq $K.plain) 'with the plain file gone, the locked copy still gives the key'
    Check (($logLines | Where-Object { $_ -match 'locked to this computer' }).Count -ge 1) "memoQ logged what the sync did ($(@($logLines | Where-Object { $_ -match '^API keys:' })[-1]))"

    # A key Trados saved after memoQ started: the plain file decides, as before.
    Plain ([ordered]@{ claude = $K.newkey })
    Check ((Resolve 'Anthropic' $null).Key -ceq $K.newkey) 'a key saved later by another product (plain file) wins over the older locked copy'

    # ---- 4. the migration never covers a key this computer cannot read ------------
    Write-Host '-- 4. the migration'
    Fresh
    Locked ([ordered]@{ openai = (Unreadable $K.openai); claude = (Unreadable $K.lockedA) })
    Trados $K.trados
    SharedTxt @{ provider = 'Anthropic'; apikey = $K.legacy }
    [void]$takeNotice.Invoke($null, @())   # runs the migration and the sync
    $plainNow = if (Test-Path -LiteralPath $PlainPath) { Get-Content -LiteralPath $PlainPath -Raw } else { '' }
    Check (-not $plainNow.Contains($K.trados)) "Trados's older OpenAI key was not copied over the unreadable one"
    Check (-not $plainNow.Contains($K.legacy)) "memoQ's legacy key was not copied over the unreadable Anthropic one"
    Check ((Status 'openai') -eq 'Unreadable' -and (Status 'claude') -eq 'Unreadable') '  both stay unreadable until a new key is pasted'

    # ---- 5. an empty box saved over it -------------------------------------------
    Write-Host '-- 5. saving an empty key box'
    $before = Entry 'claude'
    Check ($remember.Invoke($null, [object[]]@('Anthropic', ''))) 'saving the empty box succeeds'
    Check ((Entry 'claude') -ceq $before) '  and keeps the unreadable entry'
    Check ($remember.Invoke($null, [object[]]@('Anthropic', $K.newkey))) 'pasting a key again succeeds'
    $r = Resolve 'Anthropic' $null
    Check ($r.Key -ceq $K.newkey -and $null -eq $r.Problem) '  and it is the one used from then on'
    Check ((Entry 'claude') -cne $before) '  having replaced the unreadable entry'

    # ---- 6. the request guard ----------------------------------------------------
    Write-Host '-- 6. the request guard'
    Fresh
    SharedTxt @{ provider = 'Anthropic' }
    $ex = Refusal 'Anthropic' $null
    Check ($ex.Message -ceq "No API key is set for Anthropic. Add one in Supervertaler's translation settings.") `
        "no key anywhere: refused, not sent blank ($($ex.Message))"
    Check ($null -eq (Refusal 'Ollama' $null)) 'Ollama is never refused: it is sent no key'
    Check ($null -eq (Refusal 'OpenAI-compatible' $null)) 'a server of your own with no key stored is not refused: most need none'
    Fresh
    Locked ([ordered]@{ custom_openai = (Unreadable $K.custom) })
    # Core names this one "Custom OpenAI".
    $ex = Refusal 'OpenAI-compatible' $null
    Check ($ex.Message -like "Can't use your Custom OpenAI key – it can't be read on this computer*") `
        "a server of your own WITH a stored key it cannot read: refused ($($ex.Message))"

    # ---- 7. the two dialogs --------------------------------------------------------
    Write-Host '-- 7. the settings dialogs'
    Fresh
    Locked ([ordered]@{ claude = (Unreadable $K.lockedA) })
    SharedTxt @{ provider = 'Anthropic'; apikey = $K.legacy }
    $ctorFlags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
    function Field($o, $name) { $o.GetType().GetField($name, $Instance).GetValue($o) }
    function Realise($form) {
        $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
        $form.Location = New-Object Drawing.Point(-32000, -32000)
        $form.Opacity = 0; $form.ShowInTaskbar = $false
        $form.Show(); [Windows.Forms.Application]::DoEvents()
    }
    function NothingUnder($form, $label) {
        foreach ($c in $form.Controls) {
            if ($c -eq $label -or -not $c.Visible) { continue }
            $r = [Drawing.Rectangle]::Intersect($c.Bounds, $label.Bounds)
            if ($r.Width -gt 0 -and $r.Height -gt 0) { return "$($c.GetType().Name) '$($c.Text)'" }
        }
        return $null
    }

    $generalT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings')
    $secureT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSecureSettings')
    $secure = [Activator]::CreateInstance($secureT); $secure.ApiKey = $K.resource
    $settings = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSettings').GetMethod('Create').Invoke($null,
        @([Activator]::CreateInstance($generalT), $secure))
    $form = [Activator]::CreateInstance($plugin.GetType('Supervertaler.MemoQ.Settings.OptionsForm'), $ctorFlags, $null, @($settings), $null)
    try {
        Realise $form
        $note = Field $form '_apiKeyNote'; $box = Field $form '_apiKey'
        Check ($box.Text -eq '') "memoQ's dialog: the key box is empty, not filled with an older key"
        Check ($note.Text -ceq $M1) "  core's note is under it: $($note.Text)"
        Check ($note.Height -ge $note.GetPreferredSize((New-Object Drawing.Size($note.Width, 0))).Height -and $note.Height -gt 17) `
            "  in full: $($note.Height)px for $($note.GetPreferredSize((New-Object Drawing.Size($note.Width, 0))).Height)px of text"
        $over = NothingUnder $form $note
        Check ($null -eq $over) "  with nothing laid over it$(if ($over) { ": $over" })"
        $box.Text = $K.newkey
        Check ($note.Text -ne $M1) '  pasting a key in the box takes the note away'
        $box.Text = ''
        $collect = $form.GetType().GetMethod('Collect', $Instance)
        $result = $collect.Invoke($form, @())
        Check ($result.SecureSettings.ApiKey -ceq $K.resource) "  OK with the box empty leaves memoQ's own stored key as it was"
        $inUse = $form.GetType().GetMethod('ApiKeyInUse', $Instance)
        try { [void]$inUse.Invoke($form, @()); $msg = $null } catch { $msg = $_.Exception.InnerException.Message }
        Check ($msg -ceq $M1) '  Test connection and Fetch list say it rather than send a blank key'
        $provider = Field $form '_provider'
        $provider.SelectedItem = 'OpenAI'; [Windows.Forms.Application]::DoEvents()
        Check ($note.Text -ne $M1) '  OpenAI, whose key is fine, has no note'
        $provider.SelectedItem = 'Anthropic'; [Windows.Forms.Application]::DoEvents()
        Check ($note.Text -ceq $M1) '  back on Anthropic, the note again'
    }
    finally { $form.Close(); $form.Dispose() }

    # And with nothing unreadable, the dialog is laid out exactly as before.
    Fresh
    SharedTxt @{ provider = 'Anthropic' }
    $form = [Activator]::CreateInstance($plugin.GetType('Supervertaler.MemoQ.Settings.OptionsForm'), $ctorFlags, $null, @($settings), $null)
    try { Check ((Field $form '_apiKeyNote').Height -eq 17) "memoQ's dialog: one line under the key box when every key can be read" }
    finally { $form.Dispose() }

    Fresh
    $editorKeys = $editor.GetType('Supervertaler.MemoQ.Core.ApiKeys')
    $editorKeys.GetField('_migrated', $Static).SetValue($null, $false)
    $editor.GetType('Supervertaler.MemoQ.Core.SharedSettings').GetField('_map', $Static).SetValue($null, $null)
    Locked ([ordered]@{ claude = (Unreadable $K.lockedA) })
    SharedTxt @{ provider = 'Anthropic'; apikey = $K.legacy }
    $editor.GetType('Supervertaler.MemoQ.Core.SharedSettings').GetField('_map', $Static).SetValue($null, $null)
    $form = [Activator]::CreateInstance($editor.GetType('Supervertaler.PromptEditor.SettingsForm'), $ctorFlags, $null, @(), $null)
    try {
        Realise $form
        $note = Field $form '_apiKeySource'; $box = Field $form '_apiKey'
        Check ($box.Text -eq '') "the editor's dialog: the key box is empty"
        Check ($note.Text -ceq $M1) "  core's note is under it, not 'No API key is set': $($note.Text)"
        $over = NothingUnder $form $note
        Check ($null -eq $over) "  in full, with nothing laid over it$(if ($over) { ": $over" })"
        $inUse = $form.GetType().GetMethod('ApiKeyInUse', $Instance)
        try { [void]$inUse.Invoke($form, @()); $msg = $null } catch { $msg = $_.Exception.InnerException.Message }
        Check ($msg -ceq $M1) '  Fetch list says it rather than send a blank key'
    }
    finally { $form.Close(); $form.Dispose() }

    # ---- no key ever reached the log ------------------------------------------------
    $leaked = @($logLines | Where-Object { $line = $_; @($K.Values | Where-Object { $line.Contains($_) }).Count -gt 0 })
    Check ($leaked.Count -eq 0 -and $logLines.Count -gt 0) "core logged $($logLines.Count) line(s), none with a key in it"
    $store.GetProperty('Log', $Static).SetValue($null, $null)

    # ---- 8. end to end, through memoQ's session ----------------------------------
    Write-Host '-- 8. end to end through the session'
    if (Get-Process memoQ -ErrorAction SilentlyContinue) {
        Write-Host 'SKIP memoQ is running; this section builds an engine (whose bridge would use the redirected folder, but stays out of the way anyway)'
    }
    else {
        # As every harness: the engine's own guards on, the licence gate open. The
        # key stores are still the temporary ones.
        $env:SUPERVERTALER_HARNESS = '1'
        Fresh
        SharedTxt @{ provider = 'Anthropic'; model = 'claude-sonnet-5-5'; bridgemode = 'false'; batchsize = '5'; usedocumentcontext = 'false'; useterminology = 'false'; sendmemorybank = 'false'; promptpath = '' }

        $mt = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.MTInterfaces.dll")
        $common = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.Addins.Common.dll")
        $builder = $common.GetType('MemoQ.Addins.Common.DataStructures.SegmentBuilder')
        function Seg($t) { $builder.GetMethod('CreateFromString').Invoke($null, @($t)) }
        $engineT = $plugin.GetType('Supervertaler.MemoQ.SupervertalerMTEngine')
        $bare = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSettings').GetMethod('Create').Invoke($null,
            @([Activator]::CreateInstance($generalT), [Activator]::CreateInstance($secureT)))
        $engine = [Activator]::CreateInstance($engineT, $ctorFlags, $null, @($bare, 'eng', 'nld'), $null)
        $session = $engineT.GetMethod('CreateLookupSession').Invoke($engine, @())
        $segT = (Seg 'x').GetType(); $segArrT = $segT.MakeArrayType()
        $many = $session.GetType().GetMethod('TranslateCorrectSegment', [Type[]]@($segArrT, $segArrT, $segArrT))
        function Rows([string[]]$texts) { $a = [Array]::CreateInstance($segT, $texts.Count); for ($i = 0; $i -lt $texts.Count; $i++) { $a[$i] = Seg $texts[$i] }; , $a }

        $want = "No API key is set for Anthropic. Add one in Supervertaler's translation settings."
        $one = $many.Invoke($session, [object[]]@((Rows @('The pump is primed.')), $null, $null))
        Check ($one[0].Exception -ne $null -and $one[0].Exception.Message -ceq $want) "one row: memoQ is told why, and no request was made ($($one[0].Exception.Message))"
        $three = $many.Invoke($session, [object[]]@((Rows @('First line.', 'Second line.', 'Third line.')), $null, $null))
        Check (@($three | Where-Object { $_.Exception -ne $null -and $_.Exception.Message -ceq $want }).Count -eq 3) 'a batch of three: every row the same, none sent'
        $logT = $plugin.GetType('Supervertaler.MemoQ.Core.PluginLog')
        $logPath = [string]$logT.GetProperty('PrimaryPath', $Static).GetValue($null)
        $tail = if (Test-Path -LiteralPath $logPath) { (Get-Content -LiteralPath $logPath -Tail 60 -Encoding UTF8) -join "`n" } else { '' }
        Check ($tail -match [regex]::Escape("Batch of 3 failed; retrying them individually: InvalidOperationException: $want")) `
            "  the batch request itself was refused first, before its client was made ($logPath)"
        $engine.Dispose()
    }
}
finally {
    $store.GetProperty('Log', $Static).SetValue($null, $null)
    if ($null -ne $wasHarness) { $env:SUPERVERTALER_HARNESS = $wasHarness } else { Remove-Item Env:SUPERVERTALER_HARNESS -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- the real files, after ------------------------------------------------------
Check ((Stamp $realPlain 'api-keys*') -ceq $realBefore.plain) "the real plain key file and its neighbours are untouched ($realPlain)"
Check ((Stamp $realLocked 'api-keys*') -ceq $realBefore.locked) "the real locked key file and its neighbours are untouched ($realLocked)"
Check ((Stamp $realShared 'shared.txt') -ceq $realBefore.shared) "the real shared.txt is untouched"

Write-Host ''
if ($fails -eq 0) { Write-Host "ALL PASS ($total)" } else { Write-Host "FAILED: $fails of $total"; exit 1 }
