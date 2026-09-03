# The glossary extractor against both table shapes, including the real prompt.
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
$PublicStatic = [Reflection.BindingFlags]'Public,Static'
$ex = $plugin.GetType('Supervertaler.Core.PromptGlossaryExtractor')
$extract = $ex.GetMethod('Extract', $PublicStatic)
$entryType = $plugin.GetType('Supervertaler.Core.PromptGlossaryExtractor+Entry')

function Extract([string]$text) { return $extract.Invoke($null, [object[]]@($text)) }
function Field($e, $n) { return $entryType.GetProperty($n).GetValue($e) }

# ---- 1. proper Markdown, which the memoQ generator writes -----------------
$markdown = @(
    '| Dutch (source) | English (locked target) | Notes |',
    '|---|---|---|',
    '| inrichting | device | EPO standard; never "apparatus" |',
    '| werkwijze | method | never "process" |'
) -join "`n"
$a = Extract $markdown
$aPreferred = @($a | Where-Object { -not (Field $_ 'Forbidden') }).Count
$aForbidden = @($a | Where-Object { Field $_ 'Forbidden' }).Count
Write-Host "$(if ($aPreferred -eq 2 -and $aForbidden -eq 2) {'PASS'} else {'FAIL'}) Markdown table: $aPreferred preferred, $aForbidden forbidden (expected 2 and 2)"

# ---- 2. no outer pipes, no separator, which a model often writes ----------
$bare = @(
    'Dutch (source) | English (locked target) | Notes',
    'voederadditief | feed additive | Claim term; never "fodder additive"',
    'werkwijze | method | Never "process" in this project',
    'pens | rumen |'
) -join "`n"
$b = Extract $bare
$bPreferred = @($b | Where-Object { -not (Field $_ 'Forbidden') }).Count
$bForbidden = @($b | Where-Object { Field $_ 'Forbidden' }).Count
Write-Host "$(if ($bPreferred -eq 3 -and $bForbidden -eq 2) {'PASS'} else {'FAIL'}) bare pipe table: $bPreferred preferred, $bForbidden forbidden (expected 3 and 2)"

$banned = @($b | Where-Object { Field $_ 'Forbidden' } | ForEach-Object { Field $_ 'Target' }) -join ', '
Write-Host "$(if ($banned -like '*fodder additive*' -and $banned -like '*process*') {'PASS'} else {'FAIL'}) forbidden targets read from the notes: $banned"

# ---- 3. prose with pipes must not become a table --------------------------
$prose = @(
    'The pipe character | is used in tables.',
    'Some prose | with pipes | but no header naming columns.',
    'more | prose | here'
) -join "`n"
$c = Extract $prose
Write-Host "$(if ($c.Count -eq 0) {'PASS'} else {'FAIL'}) prose with pipes yields nothing: $($c.Count) entries"

# ---- 4. a table that is not a glossary is still skipped -------------------
$headings = @(
    'Dutch heading | English (locked)',
    'TECHNISCH DOMEIN | TECHNICAL FIELD',
    'CONCLUSIES | CLAIMS'
) -join "`n"
$d = Extract $headings
Write-Host "$(if ($d.Count -eq 0) {'PASS'} else {'FAIL'}) a table whose header names no source/target column is skipped: $($d.Count) entries"

# ---- 5. the real prompt ---------------------------------------------------
$real = 'D:\Supervertaler\prompt_library\Translate\BRANTS (ORFF-033-NL-WO) v2.md'
if (Test-Path $real) {
    $e = Extract ([IO.File]::ReadAllText($real))
    $ePreferred = @($e | Where-Object { -not (Field $_ 'Forbidden') }).Count
    $eForbidden = @($e | Where-Object { Field $_ 'Forbidden' }).Count
    Write-Host "$(if ($ePreferred -gt 140 -and $eForbidden -ge 5) {'PASS'} else {'FAIL'}) real prompt: $ePreferred preferred, $eForbidden forbidden (expected >140 and >=5)"

    $sample = $e | Where-Object { (Field $_ 'Source') -eq 'voederadditief' }
    $both = @($sample).Count -eq 2
    Write-Host "$(if ($both) {'PASS'} else {'FAIL'}) voederadditief yields both the locked target and the ban: $(@($sample).Count) entries"
} else {
    Write-Host "SKIP real prompt not found"
}

Write-Host ''
Write-Host 'EXTRACTOR TEST COMPLETE'
