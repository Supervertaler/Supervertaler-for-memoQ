# The model dropdown populates itself, and never at the user's expense.
#
# Two things are worth testing here and neither is the HTTP call. First, that a
# dialog can ask for a list without blocking or spending anything - Cached()
# reads disk or falls back, RefreshAsync() declines without a key. Second, that
# a corrupt or half-written cache file degrades to a shorter list rather than an
# exception inside a form's constructor.
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
$shared = $plugin.GetType('Supervertaler.MemoQ.Settings.SharedSettings')
$providers = $plugin.GetType('Supervertaler.MemoQ.Settings.LlmProviders')

$cached = $catalog.GetMethod('Cached', $Static)
$refresh = $catalog.GetMethod('RefreshAsync', $Static)
$cacheFile = $catalog.GetMethod('CacheFile', $Static)

$anthropic = $providers.GetField('Anthropic').GetValue($null)
$google = $providers.GetField('Google').GetValue($null)

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# ---- 1. an unknown provider yields nothing, not an exception --------------
$none = $cached.Invoke($null, [object[]]@('no-such-provider'))
Check ($none.Count -eq 0) "unknown provider returns an empty list: $($none.Count)"

# ---- 2. a known provider always has something to show ---------------------
# Google has no list endpoint here, so it exercises the built-in fallback.
$fallback = $cached.Invoke($null, [object[]]@($google))
Check ($fallback.Count -ge 1) "known provider falls back to a built-in name: $($fallback.Count)"

# ---- 3. a written cache is read back, display name and all ---------------
$testProvider = 'harness-catalog-test'
$path = $cacheFile.Invoke($null, [object[]]@($testProvider))
$lines = @(
    "claude-test-1`tClaude Test One",
    "claude-test-2`tClaude Test Two",
    "",                       # blank lines are skipped
    "`tno id here",           # so is a row with no id
    "claude-test-3"           # an id with no display name is legal
)
[IO.File]::WriteAllLines($path, $lines, (New-Object Text.UTF8Encoding($false)))

try {
    $read = $cached.Invoke($null, [object[]]@($testProvider))
    Check ($read.Count -eq 3) "cache parsed, junk rows dropped: $($read.Count) of 5 lines"

    $entryType = $read[0].GetType()
    $id = $entryType.GetField('Id').GetValue($read[0])
    $name = $entryType.GetField('DisplayName').GetValue($read[0])
    Check ($id -eq 'claude-test-1' -and $name -eq 'Claude Test One') "id and display name survive the round trip: $id / $name"

    # The dropdown shows the readable name with the id after it, because the id
    # is what goes on the wire and the user needs to be able to see which one
    # they picked.
    Check ($read[0].ToString() -eq 'Claude Test One   (claude-test-1)') "labelled entry: $($read[0])"
    Check ($read[2].ToString() -eq 'claude-test-3') "unlabelled entry shows its id: $($read[2])"
}
finally {
    Remove-Item $path -ErrorAction SilentlyContinue
}

# ---- 4. no key, no call --------------------------------------------------
# The settings dialog opens before a key is necessarily configured, and an
# unauthenticated request to a provider is a wasted round trip at best.
$ct = [System.Threading.CancellationToken]::None
$argv = [object[]]::new(5)
$argv[0] = $anthropic; $argv[1] = ''; $argv[2] = ''; $argv[3] = $true; $argv[4] = $ct
$task = $refresh.Invoke($null, $argv)
$task.Wait()
Check ($null -eq $task.Result) 'no API key means no request at all'

# ---- 5. a provider with no list endpoint declines quietly ---------------
$argv2 = [object[]]::new(5)
$argv2[0] = $google; $argv2[1] = 'not-a-real-key'; $argv2[2] = ''; $argv2[3] = $true; $argv2[4] = $ct
$task2 = $refresh.Invoke($null, $argv2)
$task2.Wait()
Check ($null -eq $task2.Result) 'a provider we cannot list returns null, keeping the fallback on screen'

Write-Host ''
Write-Host "MODEL CATALOG TEST COMPLETE - $fails failure(s)"
