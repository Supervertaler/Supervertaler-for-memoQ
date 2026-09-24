# memoQ puts the built-in prompts in place, and keeps them current.
#
# Until 2026-09-24 only the Trados plugin did, so a memoQ-only customer never
# had the Default Translation Prompt. Everything here runs against a temporary
# library: the data root is pointed at a new folder in this process, and the
# test stops before writing anything if that did not take.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$memoq = 'C:\Program Files\memoQ\memoQ-12'
$script:probed = @{}
[AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler] {
    param($s, $e)
    $name = ($e.Name -split ',')[0]
    if ($script:probed.ContainsKey($name)) { return $null }
    $script:probed[$name] = $true
    foreach ($dir in @($memoq, "$memoq\Addins")) {
        $c = Join-Path $dir "$name.dll"
        if (Test-Path $c) { try { return [Reflection.Assembly]::LoadFrom($c) } catch { return $null } }
    }
    return $null
})

$plugin = [Reflection.Assembly]::LoadFrom('D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll')
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

$paths   = $plugin.GetType('Supervertaler.Core.SupervertalerPaths')
$ensure  = $plugin.GetType('Supervertaler.MemoQ.Core.DefaultPrompts').GetMethod('Ensure', $Static)
$noLog   = [Action[string]]{ param($m) Write-Host "  log: $m" }

$temp = Join-Path ([IO.Path]::GetTempPath()) ('sv-default-prompts-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$paths.GetMethod('Set', $Static).Invoke($null, [object[]]@([string]$temp))

$library = [string]$paths.GetProperty('PromptLibraryDir', $Static).GetValue($null)
if (-not $library.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "ABORT: the prompt library is $library, not the temporary folder. Nothing was written."
    exit 1
}

$prompt = Join-Path $library 'Translate\Default\Default Translation Prompt.md'
$savedFlag = $env:SUPERVERTALER_HARNESS

try {
    # ---- 1. never under a harness -------------------------------------------
    $env:SUPERVERTALER_HARNESS = '1'
    $ensure.Invoke($null, @($noLog))
    Check (-not (Test-Path $library)) 'under a harness nothing is written'

    # From here on the harness flag is off, in this process only, and the
    # library is the temporary one checked above.
    $env:SUPERVERTALER_HARNESS = $null

    # ---- 2. an empty library gets the built-in prompts ------------------------
    $ensure.Invoke($null, @($noLog))
    Check (Test-Path $prompt) 'an empty library gets the Default Translation Prompt'
    $text = if (Test-Path $prompt) { [IO.File]::ReadAllText($prompt) } else { '' }
    Check ($text -match 'default: true') '  flagged as built-in'
    Check ($text -match '\[\[TC: \.\.\.\]\] marker' -and $text -notmatch 'brief explanation in parentheses') '  with the current wording'
    Check (@(Get-ChildItem -Path (Join-Path $library 'Proofread') -Recurse -Filter *.md -ErrorAction SilentlyContinue).Count -gt 0) 'and the other built-in prompts with it'

    # ---- 3. an old shipped copy is brought up to date; an edited one is not ---
    $old = "---`r`ntype: prompt`r`ndefault: true`r`n---`r`n- When a term has no established equivalent, keep the source term and add a brief explanation in parentheses if needed`r`n"
    [IO.File]::WriteAllText($prompt, $old)
    $ensure.Invoke($null, @($noLog))
    Check (-not ([IO.File]::ReadAllText($prompt) -match 'brief explanation in parentheses')) 'an old shipped copy is replaced by the current one'

    $edited = "---`r`ntype: prompt`r`ndefault: false`r`n---`r`nMY OWN WORDS - add a brief explanation in parentheses if needed`r`n"
    [IO.File]::WriteAllText($prompt, $edited)
    $ensure.Invoke($null, @($noLog))
    Check ([IO.File]::ReadAllText($prompt) -ceq $edited) 'an edited copy is left exactly as it is'

    # ---- 4. running it twice changes nothing ---------------------------------
    Remove-Item $prompt
    $ensure.Invoke($null, @($noLog))
    $first = [IO.File]::ReadAllText($prompt)
    $ensure.Invoke($null, @($noLog))
    Check ([IO.File]::ReadAllText($prompt) -ceq $first) 'a second run leaves the file as the first wrote it'
}
finally {
    $env:SUPERVERTALER_HARNESS = $savedFlag
    $paths.GetMethod('Reset', $Static).Invoke($null, @())
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}

Write-Host ''
if ($fails -gt 0) { Write-Host "$fails FAILED"; exit 1 } else { Write-Host 'All passed.' }
