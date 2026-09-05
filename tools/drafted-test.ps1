# A prompt records what drafted it, and the runtime acts on that.
#
# The rule: a prompt written by AutoPrompt already ends in a locked-terms table
# chosen for the document, so the project glossary is NOT sent to the model as
# well. Two sources of terminology that were never written to agree, with
# nothing telling the model which wins, is the contradiction this removes.
#
# The frontmatter key is the mechanism, so the round trip is what has to hold:
# a prompt saved as drafted must load as drafted, and a hand-written one must
# never come back claiming otherwise.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$Root      = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ'
$PluginDll = "$Root\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll"

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

$templateT = $plugin.GetType('Supervertaler.Core.Models.PromptTemplate')
if ($null -eq $templateT) { $templateT = $plugin.GetType('Supervertaler.Core.PromptTemplate') }
$libraryT  = $plugin.GetType('Supervertaler.Core.PromptLibrary')

# ---- 1. the round trip ------------------------------------------------------
# Written into a temporary folder under the library so nothing real is touched,
# and removed again whatever happens.
$library = [Activator]::CreateInstance($libraryT)
$paths = $plugin.GetType('Supervertaler.Core.SupervertalerPaths')
$dir = $paths.GetProperty('PromptLibraryDir', $Static).GetValue($null)
# Under Translate, because IsDrafted asks Available() - which filters to the
# prompts memoQ can actually run. That coupling is deliberate: an unavailable
# prompt falls back to the inline instructions, which carry no locked-terms
# table, so the glossary SHOULD still be sent in that case.
$folder = 'Translate/zz-drafted-harness'
$folderPath = Join-Path $dir 'Translate\zz-drafted-harness'

try {
    New-Item -ItemType Directory -Path $folderPath -Force | Out-Null

    function SaveOne($name, $draftedBy) {
        $t = [Activator]::CreateInstance($templateT)
        $templateT.GetProperty('Name').SetValue($t, $name)
        $templateT.GetProperty('Category').SetValue($t, $folder)
        $templateT.GetProperty('Content').SetValue($t, 'Translate faithfully.')
        $templateT.GetProperty('App').SetValue($t, 'memoq')
        if ($draftedBy) { $templateT.GetProperty('DraftedBy').SetValue($t, $draftedBy) }
        $libraryT.GetMethod('SavePrompt').Invoke($library, @($t))
        return $t
    }

    SaveOne 'harness drafted' 'autoprompt' | Out-Null
    SaveOne 'harness handwritten' $null | Out-Null

    # The key must actually be in the file - this is a format, and something
    # else reads it.
    $draftedFile = Get-ChildItem $folderPath -Filter '*drafted*' | Select-Object -First 1
    $body = [IO.File]::ReadAllText($draftedFile.FullName)
    Check ($body -match 'drafted_by:\s*"autoprompt"') 'the key is written into the frontmatter'

    $handFile = Get-ChildItem $folderPath -Filter '*handwritten*' | Select-Object -First 1
    Check (-not ([IO.File]::ReadAllText($handFile.FullName) -match 'drafted_by')) `
        'and is absent from a hand-written prompt rather than written empty'

    # And it must survive being read back.
    $fresh = [Activator]::CreateInstance($libraryT)
    $all = $libraryT.GetMethod('GetAllPrompts').Invoke($fresh, @())

    $drafted = $all | Where-Object { $templateT.GetProperty('Name').GetValue($_) -eq 'harness drafted' }
    $hand    = $all | Where-Object { $templateT.GetProperty('Name').GetValue($_) -eq 'harness handwritten' }

    Check ($null -ne $drafted -and $templateT.GetProperty('IsDrafted').GetValue($drafted)) `
        'a drafted prompt loads as drafted'
    Check ($null -ne $hand -and -not $templateT.GetProperty('IsDrafted').GetValue($hand)) `
        'and a hand-written one does not'

    # ---- 2. what the runtime does with it ----------------------------------
    $resolver = $plugin.GetType('Supervertaler.MemoQ.Core.PromptResolver')
    $isDrafted = $resolver.GetMethod('IsDrafted', $Static)

    $draftedPath = $templateT.GetProperty('RelativePath').GetValue($drafted)
    $handPath    = $templateT.GetProperty('RelativePath').GetValue($hand)

    Check ($isDrafted.Invoke($null, @([object]$draftedPath))) 'the resolver agrees it is drafted'
    Check (-not $isDrafted.Invoke($null, @([object]$handPath))) 'and that the other is not'

    # The three ways of knowing nothing must all mean "assume nothing", not
    # "drafted" - a wrong answer here silently stops the glossary being sent.
    Check (-not $isDrafted.Invoke($null, @([object]''))) 'no prompt selected is not drafted'
    Check (-not $isDrafted.Invoke($null, @([object]'Translate\does-not-exist.md'))) `
        'and neither is a path that resolves to nothing'
    # ---- 3. what actually reaches the model --------------------------------
    # Three states, and the middle one is why this exists: a drafted prompt
    # suppresses the glossary's PREFERRED renderings, which can contradict its
    # own locked terms, while its FORBIDDEN terms still go - those are
    # constraints rather than advice, and there are few of them.
    $indexT  = $plugin.GetType('Supervertaler.MemoQ.Core.TermIndex')
    $entryT  = $indexT.GetNestedType('Entry', [Reflection.BindingFlags]'NonPublic,Public')
    $matchT  = $indexT.GetNestedType('Match', [Reflection.BindingFlags]'NonPublic,Public')

    function MakeMatch($source, $target, $forbidden) {
        $e = [Activator]::CreateInstance($entryT)
        $entryT.GetProperty('Source').SetValue($e, $source)
        $entryT.GetProperty('Target').SetValue($e, $target)
        $entryT.GetProperty('Forbidden').SetValue($e, [bool]$forbidden)
        $m = [Activator]::CreateInstance($matchT)
        $matchT.GetProperty('Entry').SetValue($m, $e)
        return $m
    }

    $listT = [Collections.Generic.List`1].MakeGenericType(@($matchT))
    $matches = [Activator]::CreateInstance($listT)
    $matches.Add((MakeMatch 'device' 'inrichting' $false))
    $matches.Add((MakeMatch 'apparatus' 'apparaat' $true))

    $generalT  = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings')
    $secureT   = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSecureSettings')
    $settingsT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSettings')
    $engineCtT = $plugin.GetType('Supervertaler.MemoQ.Core.EngineContext')
    $sharedT   = $plugin.GetType('Supervertaler.MemoQ.Core.SharedSettings')

    $settings = $settingsT.GetMethod('Create').Invoke($null,
        @([Activator]::CreateInstance($generalT), [Activator]::CreateInstance($secureT)))
    $ctx = [Activator]::CreateInstance($engineCtT, [Reflection.BindingFlags]'Instance,Public,NonPublic',
        $null, @($settings, 'eng', 'nld'), $null)

    $forModel = $engineCtT.GetMethod('GlossaryForModel')
    function Ask() {
        $argv = New-Object object[] 1
        $argv[0] = $matches
        return $forModel.Invoke($ctx, $argv)
    }

    $sharedT.GetProperty('UseTerminologyContext', $Static).SetValue($null, $true)

    # A hand-written prompt claims nothing about terminology, so all of it goes.
    $sharedT.GetProperty('PromptPath', $Static).SetValue($null, $handPath)
    $all = Ask
    Check ($null -ne $all -and $all.Count -eq 2) "a hand-written prompt gets the whole glossary: $($all.Count)"

    # A drafted prompt carries its own locked terms, so only the constraints go.
    $sharedT.GetProperty('PromptPath', $Static).SetValue($null, $draftedPath)
    $some = Ask
    Check ($null -ne $some -and $some.Count -eq 1) "a drafted prompt gets only the forbidden ones: $($some.Count)"
    Check ($entryT.GetProperty('Forbidden').GetValue($matchT.GetProperty('Entry').GetValue($some[0]))) `
        'and the one that goes is the forbidden one'

    # The setting names forbidden terms explicitly, so off means off.
    $sharedT.GetProperty('UseTerminologyContext', $Static).SetValue($null, $false)
    Check ($null -eq (Ask)) 'switching terminology context off stops even those'
}
finally {
    if (Test-Path $folderPath) { Remove-Item $folderPath -Recurse -Force }
}

Write-Host ''
Write-Host "DRAFTED PROMPT TEST COMPLETE - $fails failure(s)"
