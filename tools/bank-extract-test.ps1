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
}
finally {
    $shared.GetProperty('MemoryBank', $Static).SetValue($null, $savedBank)
    $paths.GetMethod('Reset', $Static).Invoke($null, @())
    Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
}

Write-Host ''
if ($fails -gt 0) { Write-Host "$fails FAILED"; exit 1 } else { Write-Host 'All passed.' }
