# A large memory bank is cut down to what the job's document needs.
#
# Core's BankExtract is tested in core; this checks the memoQ wiring through the
# object memoQ drives, EngineContext: that the document memoQ has shown the
# plugin is what the bank is selected against, that the result is stable
# between requests (so the provider's cache holds), that it follows the
# document as more of it arrives, and that a harness never asks a model or
# writes the extract file.
#
# Against a temporary data root: the banks here are made by this test, and the
# translator's own - and their _shared - are never read or written. The test
# stops before touching anything if the redirect did not take.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
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
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

if (-not $env:SUPERVERTALER_HARNESS) { Write-Host 'ABORT: run this through tools/run-harness.ps1.'; exit 1 }

$paths  = $plugin.GetType('Supervertaler.Core.SupervertalerPaths')
$banks  = $plugin.GetType('Supervertaler.Core.MemoryBanks')
$shared = $plugin.GetType('Supervertaler.MemoQ.Core.SharedSettings')
$store  = $plugin.GetType('Supervertaler.MemoQ.Core.CaptureStore')

$temp = Join-Path ([IO.Path]::GetTempPath()) ('sv-bank-extract-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
$paths.GetMethod('Set', $Static).Invoke($null, [object[]]@([string]$temp))

$banksRoot = [string]$banks.GetProperty('Root', $Static).GetValue($null)
if (-not $banksRoot.StartsWith($temp, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "ABORT: the banks root is $banksRoot, not the temporary folder. Nothing was touched."
    exit 1
}

$savedBank = [string]$shared.GetProperty('MemoryBank', $Static).GetValue($null)
$savedExtract = [string]$shared.GetProperty('BankExtract', $Static).GetValue($null)

try {
    # A client bank of ~40,000 tokens: a brief, a style guide, and a terminology
    # table of 3,000 rows of which two occur in the document below.
    $acme = Join-Path $banksRoot 'acme'
    New-Item -ItemType Directory -Path $acme -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $acme 'brief.md'), "# Acme`r`n`r`nBRIEF-MARKER: formal register.`r`n")
    [IO.File]::WriteAllText((Join-Path $acme 'style.md'), "# Style`r`n`r`nSTYLE-MARKER: no contractions.`r`n")
    $rows = New-Object Text.StringBuilder
    [void]$rows.Append("# Terminology`r`n`r`n| Source | Target | Scope | Note |`r`n|---|---|---|---|`r`n")
    [void]$rows.Append("| steam turbine | stoomturbine | domain | |`r`n")
    [void]$rows.Append("| widget | onderdeel | client | |`r`n")
    [void]$rows.Append("| later term | later-term | client | |`r`n")
    for ($i = 0; $i -lt 3000; $i++) {
        [void]$rows.Append("| unrelatedterm$i | ongerelateerdterm$i | client | padding to make the bank large enough |`r`n")
    }
    [IO.File]::WriteAllText((Join-Path $acme 'terminology.md'), $rows.ToString())

    $generalT  = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings')
    $secureT   = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSecureSettings')
    $settingsT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSettings')
    $settings  = $settingsT.GetMethod('Create').Invoke($null,
        @([Activator]::CreateInstance($generalT), [Activator]::CreateInstance($secureT)))
    $ctx = [Activator]::CreateInstance($plugin.GetType('Supervertaler.MemoQ.Core.EngineContext'),
        [Reflection.BindingFlags]'Instance,Public,NonPublic', $null, @($settings, 'eng', 'nld'), $null)

    $shared.GetProperty('MemoryBank', $Static).SetValue($null, 'acme')
    $block = { $ctx.GetType().GetMethod('KbContextBlock').Invoke($ctx, @()) }
    $record = $store.GetMethod('Record', [Type[]]@($ctx.GetType(), [string]))

    # ---- 1. no document yet: the bank as before, within the budget ----------
    $before = & $block
    Check ($before -and $before.Contains('BRIEF-MARKER')) 'with no document yet, the bank is sent within the budget as before'

    # ---- 2. a document: only the rows it uses --------------------------------
    $record.Invoke($null, @($ctx, 'The steam turbine drives the widget assembly.'))
    $extract = & $block
    Check ($extract -and $extract.Contains('| steam turbine |') -and $extract.Contains('| widget |')) 'the rows the document uses are sent'
    Check ($extract -and -not $extract.Contains('unrelatedterm1234')) 'the rows it does not use are not'
    Check ($extract -and $extract.Contains('BRIEF-MARKER') -and $extract.Contains('STYLE-MARKER')) 'brief and style go regardless'
    Check ($extract.Length -lt 5000) "and the block is small: $($extract.Length) characters"

    # ---- 3. stable between requests, so the provider's cache holds ----------
    $again = & $block
    Check ([object]::ReferenceEquals($extract, $again)) 'the same block on the next request, not a rebuild'

    # ---- 4. it follows the document as more of it arrives --------------------
    $record.Invoke($null, @($ctx, ('A later term appears further on in the document, with more text around it. ' * 3)))
    $grown = & $block
    Check ($grown -and $grown.Contains('| later term |')) 'once the captured text has grown, a term from the new part is sent too'

    # ---- 5. a harness asks no model and writes no file -----------------------
    Check (-not (Test-Path (Join-Path $temp 'memoq\bank-extracts'))) 'no extract file is written under a harness'
    Check (([string]$shared.GetProperty('BankExtract', $Static).GetValue($null)) -eq $savedExtract) 'and the editor pointer is left alone'

    # ---- 5a. notes for the assistants only --------------------------------------
    # audience: assistant keeps a note out of translation - the MT block and
    # AutoPrompt - while the MCP tools, which is who it is written for, still get it.
    $sharedDir = Join-Path $banksRoot '_shared'
    New-Item -ItemType Directory -Path $sharedDir -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $sharedDir 'brief.md'), "# House`r`n`r`nBritish spelling. HOUSE-MARKER`r`n")
    [IO.File]::WriteAllText((Join-Path $sharedDir 'method.md'), "---`r`naudience: assistant`r`n---`r`n# Method`r`n`r`nCheck the termbase has entries. METHOD-MARKER`r`n")
    Start-Sleep -Milliseconds 50   # a newer write, so the cached block is rebuilt

    $mt = & $block
    Check ($mt -and $mt.Contains('HOUSE-MARKER') -and -not $mt.Contains('METHOD-MARKER')) 'translation: the assistant note is left out, the rest of _shared is sent'
    $auto = $ctx.GetType().GetMethod('KbContextForAutoPrompt').Invoke($ctx, @())
    Check ($auto -and -not $auto.Contains('METHOD-MARKER')) 'AutoPrompt leaves it out too'

    # A small bank, so nothing is trimmed for size and the check is about the marker alone.
    $tiny = Join-Path $banksRoot 'tiny'
    New-Item -ItemType Directory -Path $tiny -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $tiny 'brief.md'), "# Tiny`r`n`r`nA small client. TINY-MARKER`r`n")

    $sm = $plugin.GetType('Supervertaler.MemoQ.Core.SuperMemory')
    $contextMethod = $sm.GetMethods($Static) | Where-Object { $_.Name -eq 'Context' } | Select-Object -First 1
    $argv = New-Object object[] ($contextMethod.GetParameters().Count)
    for ($i = 0; $i -lt $argv.Count; $i++) {
        $p = $contextMethod.GetParameters()[$i]
        $argv[$i] = if ($p.Name -match 'bank') { 'tiny' } elseif ($p.ParameterType -eq [int]) { 0 } elseif ($p.HasDefaultValue) { $p.DefaultValue } else { $null }
    }
    $mcp = $contextMethod.Invoke($null, $argv)
    $mcpText = ($mcp | ConvertTo-Json -Depth 5 -Compress)
    Check ($mcpText -match 'METHOD-MARKER') 'the MCP tool still gives it to the assistant'

    # ---- 5b. when the article choice may be asked ------------------------------
    # Once 3,000 characters are known - or at once, if the live link has handed
    # over the whole document: a short job seen in full is a complete sample, and
    # without this it would never be asked at all.
    $can = $plugin.GetType('Supervertaler.MemoQ.Core.EngineContext').GetMethod('CanChooseArticles', $Static)
    function Can($len, $whole) { $a = New-Object object[] 2; $a[0] = [int]$len; $a[1] = [bool]$whole; [bool]$can.Invoke($null, $a) }
    Check (-not (Can 500 $false)) 'a short partial capture is not asked about'
    Check (Can 500 $true) 'a short document known in full is'
    Check (Can 5000 $false) 'a long enough capture is, whole or not'

    # ---- 6. extract files do not pile up ---------------------------------------
    # One file per document ever translated would grow forever. The newest 200
    # stay, anything not rewritten for 90 days goes, and the one just written is
    # never touched.
    $prune = $plugin.GetType('Supervertaler.MemoQ.Core.EngineContext').GetMethod('PruneExtracts', $Static)
    $folder = Join-Path $temp 'prune-test'
    New-Item -ItemType Directory -Path $folder | Out-Null
    $now = [DateTime]::UtcNow
    for ($i = 0; $i -lt 203; $i++) {
        $f = Join-Path $folder ("recent-{0:000}.md" -f $i)
        [IO.File]::WriteAllText($f, 'x')
        [IO.File]::SetLastWriteTimeUtc($f, $now.AddMinutes(-$i))
    }
    $old = Join-Path $folder 'finished-job.md'
    [IO.File]::WriteAllText($old, 'x')
    [IO.File]::SetLastWriteTimeUtc($old, $now.AddDays(-100))
    $current = Join-Path $folder 'recent-000.md'

    $removed = $prune.Invoke($null, @([string]$folder, [int]200, [TimeSpan]::FromDays(90), [string]$current))
    $left = @(Get-ChildItem $folder -Filter *.md)
    Check ($left.Count -eq 200) "the newest 200 are kept: $($left.Count) left, $removed removed"
    Check (-not (Test-Path $old)) 'a file not rewritten for 90 days is removed'
    Check (Test-Path $current) 'the one just written is kept'
    Check (-not (Test-Path (Join-Path $folder 'recent-202.md'))) 'the oldest recent ones go first'
}
finally {
    $shared.GetProperty('MemoryBank', $Static).SetValue($null, $savedBank)
    $paths.GetMethod('Reset', $Static).Invoke($null, @())
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}

Write-Host ''
if ($fails -gt 0) { Write-Host "$fails FAILED"; exit 1 } else { Write-Host 'All passed.' }
