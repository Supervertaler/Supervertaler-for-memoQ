# A prompt can declare its language pair, it round-trips, and a mismatch is
# reported the way the glossary's is.
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

# ---- 1. the frontmatter round-trips --------------------------------------
# PromptLibrary reads the real folder and takes no root argument, so the test
# writes a throwaway prompt there and removes it again, as bridge-test does.
$libRoot = 'D:\Supervertaler\prompt_library\Translate'
$file = Join-Path $libRoot 'zz-direction-roundtrip-test.md'
try {
    @(
        '---',
        'description: "throwaway; a prompt that knows its direction"',
        'category: "Translate"',
        'source_lang: "dut-NL"',
        'target_lang: "eng-GB"',
        '---',
        '',
        'Translate faithfully.'
    ) -join "`r`n" | Set-Content -Path $file -Encoding UTF8

    $libType = $plugin.GetType('Supervertaler.Core.PromptLibrary')
    $lib = [Activator]::CreateInstance($libType)
    $all = $libType.GetMethod('GetAllPrompts').Invoke($lib, @())
    $one = @($all | Where-Object { $_.Name -eq 'zz-direction-roundtrip-test' })[0]

    if ($null -eq $one) {
        Write-Host 'FAIL the throwaway prompt was not loaded'
    } else {
        $t = $one.GetType()
        $src = $t.GetProperty('SourceLang').GetValue($one)
        $tgt = $t.GetProperty('TargetLang').GetValue($one)
        Write-Host "$(if ($src -eq 'dut-NL' -and $tgt -eq 'eng-GB') {'PASS'} else {'FAIL'}) parsed: source='$src' target='$tgt'"

        $argv = [object[]]::new(1); $argv[0] = $one.PSObject.BaseObject
        $libType.GetMethod('SavePrompt').Invoke($lib, $argv) | Out-Null
        $text = [IO.File]::ReadAllText($file)
        $kept = ($text -like '*source_lang: "dut-NL"*') -and ($text -like '*target_lang: "eng-GB"*')
        Write-Host "$(if ($kept) {'PASS'} else {'FAIL'}) survives a save"
    }
}
finally {
    Remove-Item $file -Force -ErrorAction SilentlyContinue
}

# ---- 2. a prompt with no declaration stays quiet -------------------------
$gd = $plugin.GetType('Supervertaler.MemoQ.Core.GlossaryDirection')
$compare = $gd.GetMethod('Compare', $PublicStatic)
$undeclared = $compare.Invoke($null, [object[]]@('dut-NL', 'eng-GB', '', '')).ToString()
Write-Host "$(if ($undeclared -eq 'Undeclared') {'PASS'} else {'FAIL'}) an undeclared prompt is not judged: $undeclared"

# ---- 3. the real mismatch is classified as inverted ----------------------
$inverted = $compare.Invoke($null, [object[]]@('dut-NL', 'eng-GB', 'eng', 'dut')).ToString()
$aligned = $compare.Invoke($null, [object[]]@('dut-NL', 'eng-GB', 'dut', 'eng')).ToString()
Write-Host "$(if ($inverted -eq 'Inverted' -and $aligned -eq 'Aligned') {'PASS'} else {'FAIL'}) eng-dut prompt in a dut-eng project: $inverted; correct one: $aligned"

# ---- 4. the plugin exposes the lookup the warning needs ------------------
$pr = $plugin.GetType('Supervertaler.MemoQ.Core.PromptResolver')
$tryGet = $pr.GetMethod('TryGetLanguages', $PublicStatic)
Write-Host "$(if ($null -ne $tryGet) {'PASS'} else {'FAIL'}) PromptResolver.TryGetLanguages is available"

$ec = $plugin.GetType('Supervertaler.MemoQ.Core.EngineContext')
$warn = $ec.GetMethod('WarnIfPromptFacesTheWrongWay')
Write-Host "$(if ($null -ne $warn) {'PASS'} else {'FAIL'}) EngineContext.WarnIfPromptFacesTheWrongWay is available"

Write-Host ''
Write-Host 'PROMPT LANGUAGE TEST COMPLETE'
