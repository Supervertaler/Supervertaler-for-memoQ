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

# The build beside this script, so a worktree tests its own build.
$plugin = [Reflection.Assembly]::LoadFrom((Join-Path $PSScriptRoot '..\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll'))
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

    # ---- 5. memoQ's own library gets no Trados-only built-ins ----------------
    # 0.1.32 wrote ten of them into prompt_library_memoQ, shown greyed out.
    $paths.GetMethod('SetProduct', $Static).Invoke($null, @('memoQ'))
    $own = [string]$paths.GetProperty('PromptLibraryDir', $Static).GetValue($null)
    if (-not $own.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) { throw "memoQ's library is $own, not under the temporary folder" }
    $ensure.Invoke($null, @($noLog))
    $trados = @(Get-ChildItem -Path $own -Recurse -Filter *.md | Where-Object { [IO.File]::ReadAllText($_.FullName) -match 'app: "trados"' })
    Check ($trados.Count -eq 0) "memoQ's library gets no Trados-only built-ins ($($trados.Count))"
    Check (Test-Path (Join-Path $own 'Translate\Default\Default Translation Prompt.md')) '  but does get the untagged ones'

    # ---- 6. one 0.1.32 wrote is removed and logged; an edited one stays ------
    $ql = Join-Path $own 'QuickLauncher\Default'
    $define = Join-Path $ql 'Define [Trados].md'
    $brief = Join-Path $ql 'Generate project brief [Trados].md'
    $shipped = "---`ntype: prompt`nname: `"Define`"`ncategory: `"QuickLauncher/Default`"`ndefault: true`nread_only: true`nquicklauncher: true`napp: `"trados`"`n---`n`nDefine `"{{SELECTION}}`" and give practical examples showing how it's used."
    [IO.File]::WriteAllText($define, $shipped, (New-Object Text.UTF8Encoding $true))
    [IO.File]::WriteAllText($brief, "---`ntype: prompt`ndefault: true`napp: `"trados`"`n---`n`nMy own brief prompt.")
    $logged = New-Object System.Collections.Generic.List[string]
    $ensure.Invoke($null, @([Action[string]]{ param($m) $logged.Add($m) }))
    Check (-not (Test-Path -LiteralPath $define)) 'an untouched Trados built-in from 0.1.32 is removed'
    Check (($logged -join ' ') -match 'Removed 1 built-in.*Define \[Trados\]\.md') "  and the log says so: $($logged -join ' / ')"
    Check (Test-Path -LiteralPath $brief) 'an edited one stays'
}
finally {
    $env:SUPERVERTALER_HARNESS = $savedFlag
    $paths.GetMethod('ClearProduct', $Static).Invoke($null, @())
    $paths.GetMethod('Reset', $Static).Invoke($null, @())
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}

Write-Host ''
if ($fails -gt 0) { Write-Host "$fails FAILED"; exit 1 } else { Write-Host 'All passed.' }
