# Choosing a memory bank, and remembering which project chose it.
#
# The rule this exists to defend is one line long and easy to break by
# accident: a project with no bank recorded uses NO bank, rather than
# inheriting whichever one the last job used. Everything else here is
# plumbing; that is the behaviour whose failure is silent and expensive,
# because the wrong bank supplies one client's terminology to another
# client's job and reads exactly like the right one.
#
# Writes are confined to fabricated project GUIDs, and run-harness.ps1
# snapshots the two files this touches regardless.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$Root      = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ'
$PluginDll = "$Root\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll"

Add-Type -AssemblyName System.Windows.Forms

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
$NonPublicInstance = [Reflection.BindingFlags]'NonPublic,Instance'

$choice   = $plugin.GetType('Supervertaler.MemoQ.Core.MemoryBankChoice')
$picker   = $plugin.GetType('Supervertaler.MemoQ.Settings.MemoryBankPicker')
$shared   = $plugin.GetType('Supervertaler.MemoQ.Core.SharedSettings')
$banks    = $plugin.GetType('Supervertaler.Core.MemoryBanks')
$engineCt = $plugin.GetType('Supervertaler.MemoQ.Core.EngineContext')

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# Two PowerShell traps in one helper. $args is an automatic variable inside a
# function, so a parameter named after it silently binds nothing; and New-Object
# hands back a PSObject wrapper that reflection refuses to convert to the real
# parameter type, so every argument is unwrapped on the way in.
function Call($type, $name, $argv) {
    $raw = @($argv | ForEach-Object {
        if ($null -ne $_ -and $_ -is [PSObject]) { $_.PSObject.BaseObject } else { $_ }
    })
    $type.GetMethod($name, $Static).Invoke($null, $raw)
}
function SetProp($type, $name, $value) { $type.GetProperty($name, $Static).SetValue($null, $value) }
function GetProp($type, $name) { $type.GetProperty($name, $Static).GetValue($null) }

# Fabricated projects, so nothing here can collide with a real one.
$projA = [Guid]'aaaaaaaa-0000-4000-8000-000000000001'
$projB = [Guid]'bbbbbbbb-0000-4000-8000-000000000002'
$never = [Guid]'cccccccc-0000-4000-8000-000000000003'

# ---- 1. what a project remembers ------------------------------------------
Call $choice 'Remember' @([object]$projA, [object]'Acme')
Call $choice 'Reset' @()                       # force a re-read from disk
Check ((Call $choice 'ForProject' @([object]$projA)) -eq 'Acme') 'a recorded bank comes back'

# The rule. A project nobody has chosen for gets nothing - not the last one.
Check ((Call $choice 'ForProject' @([object]$never)) -eq '') `
    'a project with no bank recorded answers with none, not the last bank used'

# Clearing is itself a choice, and has to survive the same way a name does.
# If it did not, clearing a bank would last exactly until the next project
# switch read the file and found nothing.
Call $choice 'Remember' @([object]$projB, [object]'')
Call $choice 'Reset' @()
Check ((Call $choice 'ForProject' @([object]$projB)) -eq '') 'clearing a bank is remembered as a choice'
Check ((Call $choice 'ForProject' @([object]$projA)) -eq 'Acme') 'and does not disturb another project'

# An empty GUID is memoQ saying it does not know; nothing should be filed there.
Call $choice 'Remember' @([object][Guid]::Empty, [object]'Acme')
Call $choice 'Reset' @()
Check ((Call $choice 'ForProject' @([object][Guid]::Empty)) -eq '') 'nothing is recorded against no project'

# ---- 2. surviving a hand edit ---------------------------------------------
# The file is meant to be openable in Notepad. A same-second save with a
# different length is the edit a timestamp check alone would miss.
$path = GetProp $choice 'Path'
Check (Test-Path $path) "the file is written where it says: $path"

$body = [IO.File]::ReadAllText($path)
Check ($body -match [Regex]::Escape($projA.ToString('D'))) 'a project GUID is in the file verbatim'

[IO.File]::WriteAllText($path, ($body -replace 'Acme', 'Other'))
Check ((Call $choice 'ForProject' @([object]$projA)) -eq 'Other') 'an edit made outside the plugin is picked up'

# ---- 3. the picker --------------------------------------------------------
$combo = New-Object System.Windows.Forms.ComboBox
Call $picker 'Fill' @([object]$combo, [object]'')

$items = @($combo.Items | ForEach-Object { [string]$_ })
Check ($items[0] -eq '(none)') 'no bank is an explicit first entry, not a blank row'
Check ($combo.SelectedIndex -eq 0) 'and is what an empty setting selects'

# _shared layers under whichever bank is chosen. Offering it invites someone to
# select it and silently lose the client half of their context.
Check (-not ($items -contains '_shared')) '_shared is not offered as a bank'

$onDisk = @(Call $banks 'List' @())

# Every name the picker offers must be one DirFor can open. Sanitize strips
# leading underscores, dots and spaces, so a bank named "_archive" was listed,
# selected, and then resolved to nothing - silently.
$unresolvable = @($onDisk | Where-Object { $null -eq (Call $banks 'DirFor' @([object]$_)) })
Check ($unresolvable.Count -eq 0) "every listed bank resolves to a folder ($($unresolvable.Count) do not)"

$expected = @($onDisk | Where-Object { $_ -ne '_shared' }).Count + 1
Check ($items.Count -eq $expected) "the list is the banks on disk plus (none): $($items.Count)"

Check ((Call $picker 'Chosen' @([object]$combo)) -eq '') '(none) reads back as no bank'

# A recorded bank that has since been renamed or deleted stays visible. Silently
# resetting it to (none) would make OK on this dialog destroy the only clue.
$gone = 'a-bank-that-was-deleted'
Call $picker 'Fill' @([object]$combo, [object]$gone)
Check ((Call $picker 'Chosen' @([object]$combo)) -eq $gone) 'a missing bank is kept, not silently reset'

if ($onDisk.Count -gt 0) {
    $real = @($onDisk | Where-Object { $_ -ne '_shared' })[0]
    if ($real) {
        Call $picker 'Fill' @([object]$combo, [object]$real.ToUpperInvariant())
        Check ((Call $picker 'Chosen' @([object]$combo)) -eq $real) `
            'a bank matches whatever case it was recorded in'
    }
}

# ---- 4. what the picker writes --------------------------------------------
SetProp $shared 'MemoryBankProject' $projA.ToString('D')
Call $picker 'Save' @([object]'Acme')

Check ((GetProp $shared 'MemoryBank') -eq 'Acme') 'saving sets the bank in force'
Call $choice 'Reset' @()
Check ((Call $choice 'ForProject' @([object]$projA)) -eq 'Acme') 'and records it against the open project'

# With no project known, the choice still applies now - it just has nothing to
# be remembered against, which the dialog says out loud.
SetProp $shared 'MemoryBankProject' ''
Call $picker 'Save' @([object]'Acme')
Check ((GetProp $shared 'MemoryBank') -eq 'Acme') 'a choice made with no project open still takes effect'

$note = Call $picker 'ProjectNote' @()
Check ($note -match 'once memoQ has translated') "and says why it will not be remembered: $note"

# ---- 5. the budgets --------------------------------------------------------
# These two numbers are the whole cost story, and they are meant to be far
# apart: one is paid once per job, the other once per ten segments.
$perRequest = $engineCt.GetField('PerRequestTokenBudget', $Static).GetValue($null)
$autoPrompt = $engineCt.GetField('AutoPromptTokenBudget', $Static).GetValue($null)
Check ($perRequest -eq 6000) "a translation request carries $perRequest tokens of bank"
Check ($autoPrompt -ge $perRequest * 4) "AutoPrompt carries $autoPrompt, several times more"

# ---- 6. the project switch, on a real engine context ----------------------
# This is the rule the whole feature turns on, so it is exercised against the
# object memoQ actually drives rather than against the store underneath it.
$generalT  = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings')
$secureT   = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSecureSettings')
$settingsT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSettings')
$settings  = $settingsT.GetMethod('Create').Invoke($null,
    @([Activator]::CreateInstance($generalT), [Activator]::CreateInstance($secureT)))

$ctx = [Activator]::CreateInstance($engineCt, [Reflection.BindingFlags]'Instance,Public,NonPublic',
    $null, @($settings, 'eng', 'nld'), $null)

$metaT = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.MTInterfaces.dll").GetType('MemoQ.MTInterfaces.MTRequestMetadata')
function Meta($project) {
    $m = [Activator]::CreateInstance($metaT)
    $metaT.GetProperty('ProjectGuid').SetValue($m, $project)
    return $m
}

$note = $engineCt.GetMethod('NoteMetadata')
Call $choice 'Remember' @([object]$projA, [object]'Acme')
Call $choice 'Remember' @([object]$projB, [object]'')
Call $choice 'Reset' @()

$note.Invoke($ctx, @((Meta $projA).PSObject.BaseObject))
Check ((GetProp $shared 'MemoryBank') -eq 'Acme') 'opening a project loads the bank it recorded'
Check ((GetProp $shared 'MemoryBankProject') -eq $projA.ToString('D')) 'and notes which project that was'

# THE RULE. Switching to a project with no bank must clear, not inherit.
$note.Invoke($ctx, @((Meta $projB).PSObject.BaseObject))
Check ((GetProp $shared 'MemoryBank') -eq '') `
    'switching to a project with no bank clears it rather than carrying the last one over'

$note.Invoke($ctx, @((Meta $projA).PSObject.BaseObject))
Check ((GetProp $shared 'MemoryBank') -eq 'Acme') 'and coming back restores it'

# A bank that is not on disk contributes nothing rather than throwing.
Check ($null -eq $ctx.GetType().GetMethod('KbContextBlock').Invoke($ctx, @())) `
    'a recorded bank that no longer exists contributes nothing'

# ---- 7. a real bank, really loaded ----------------------------------------
# Against a bank this test creates and removes, so the assertion can name the
# text it expects without reading anyone's client files.
# Not $root: PowerShell variable names are case-insensitive, and that
# quietly overwrote $Root, the repo path.
$banksRoot = GetProp $banks 'Root'
$temp = Join-Path $banksRoot '_sv-harness-bank'
$marker = 'HARNESS-MARKER-Acme-house-style'

try {
    New-Item -ItemType Directory -Path $temp -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $temp 'brief.md'), "# Harness`r`n`r`n$marker`r`n")

    SetProp $shared 'MemoryBank' '_sv-harness-bank'
    $block = $ctx.GetType().GetMethod('KbContextBlock').Invoke($ctx, @())
    Check ($null -ne $block -and $block.Contains($marker)) `
        'a selected bank reaches the prompt, leading underscore and all'

    # Cached between calls, so a 57-batch job does not re-read the bank 57 times.
    $again = $ctx.GetType().GetMethod('KbContextBlock').Invoke($ctx, @())
    Check ([object]::ReferenceEquals($block, $again)) 'and is not rebuilt for every request'

    # An edit in Obsidian mid-job has to take effect on the next batch, which is
    # what the write-time check in the cache key is for.
    Start-Sleep -Milliseconds 50
    [IO.File]::WriteAllText((Join-Path $temp 'brief.md'), "# Harness`r`n`r`nEDITED-$marker`r`n")
    $edited = $ctx.GetType().GetMethod('KbContextBlock').Invoke($ctx, @())
    Check ($edited -and $edited.Contains("EDITED-$marker")) 'editing the bank takes effect on the next request'

    # ---- 8. WHERE the block lands ------------------------------------------
    # In the system half, not with the per-request context. That is what makes it
    # a stable prefix the provider's prompt cache can recognise; moved in with
    # the terminology it would be re-read at full price on every row.
    $pb = $plugin.GetType('Supervertaler.MemoQ.Core.PromptBuilder')
    $common = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.Addins.Common.dll")
    $segBuilder = $common.GetType('MemoQ.Addins.Common.DataStructures.SegmentBuilder')
    $bundleT = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.MTInterfaces.dll").GetType('MemoQ.MTInterfaces.TranslationBundle')
    $bundle = [Activator]::CreateInstance($bundleT)
    $seg = $segBuilder.GetMethod('CreateFromString', $Static).Invoke($null, @([object]'A device comprising a widget.'))
    # TranslationBundle exposes fields, not properties.
    $bundleT.GetField('Source').SetValue($bundle, $seg)

    $general = [Activator]::CreateInstance($generalT)
    $built = $pb.GetMethod('Build', $Static).Invoke($null,
        @($bundle, $general, 'eng', 'nld', $null, $null, $null, 'INSTRUCTIONS', $marker))

    $sys = $built.GetType().GetProperty('System').GetValue($built)
    $usr = $built.GetType().GetProperty('User').GetValue($built)
    Check ($sys.Contains($marker)) 'the bank goes in the system prompt'
    Check (-not $usr.Contains($marker)) 'and not in the per-request half, which changes every call'
    Check ($sys.IndexOf('INSTRUCTIONS') -lt $sys.IndexOf($marker)) `
        'after the prompt, so a prompt written for this job outweighs standing background'
}
finally {
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
}

# ---- 9. the dialogs still fit ---------------------------------------------
# Adding a row to a dialog whose buttons anchor to the bottom edge is how the
# endpoint hint lost its last three words and the "Keep on top" checkbox lost
# its box. Neither showed up until someone opened the window, so the check is
# here: build both dialogs and look for anything hanging off the bottom or
# sitting on top of something else.
function LayoutFaults($form) {
    $faults = @()
    $controls = @($form.Controls)

    foreach ($c in $controls) {
        if ($c.Bottom -gt $form.ClientSize.Height) {
            $faults += "$($c.GetType().Name) '$($c.Text)' ends at $($c.Bottom), past $($form.ClientSize.Height)"
        }
    }

    # Overlap between the two things most likely to collide: a wrapped hint and
    # whatever was placed underneath it.
    for ($i = 0; $i -lt $controls.Count; $i++) {
        for ($j = $i + 1; $j -lt $controls.Count; $j++) {
            $a = $controls[$i]; $b = $controls[$j]
            if ($a.Bounds.IntersectsWith($b.Bounds)) {
                $faults += "$($a.GetType().Name) '$($a.Text)' overlaps $($b.GetType().Name) '$($b.Text)'"
            }
        }
    }
    return $faults
}

$optionsT = $plugin.GetType('Supervertaler.MemoQ.Settings.OptionsForm')
$options = [Activator]::CreateInstance($optionsT, [Reflection.BindingFlags]'Instance,Public,NonPublic',
    $null, @($settings), $null)
try {
    $faults = LayoutFaults $options
    Check ($faults.Count -eq 0 -and $options.Controls.Count -gt 10) `
        "memoQ's options dialog lays out cleanly: $($options.Controls.Count) controls, $($faults.Count) fault(s)"
    $faults | ForEach-Object { Write-Host "     $_" }
}
finally { $options.Dispose() }

$editorExe = "$Root\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe"
$editor = [Reflection.Assembly]::LoadFrom($editorExe)
$settingsFormT = $editor.GetType('Supervertaler.PromptEditor.SettingsForm')
$settingsForm = [Activator]::CreateInstance($settingsFormT, [Reflection.BindingFlags]'Instance,Public,NonPublic',
    $null, @(), $null)
try {
    $faults = LayoutFaults $settingsForm
    Check ($faults.Count -eq 0 -and $settingsForm.Controls.Count -gt 10) `
        "the editor's translation settings lay out cleanly: $($settingsForm.Controls.Count) controls, $($faults.Count) fault(s)"
    $faults | ForEach-Object { Write-Host "     $_" }
}
finally { $settingsForm.Dispose() }

# ---- 10. the chooser -------------------------------------------------------
# The bank and the prompt share one dialog because both lists grow with the
# work. What is worth checking is the part a menu would not have needed: that
# filtering narrows without moving the answer, and that an empty result does
# not hand back the first row of the unfiltered list.
$chooserT = $editor.GetType('Supervertaler.PromptEditor.ChooserForm')
$rowT = $chooserT.GetNestedType('Row', [Reflection.BindingFlags]'NonPublic,Public')
$chooserHelpers = $editor.GetType('Supervertaler.PromptEditor.PromptChooserForm')
$bankRowT = $chooserHelpers.GetNestedType('BankRow', [Reflection.BindingFlags]'NonPublic,Public')

function MakeBankRow($name, $articles) {
    $r = [Activator]::CreateInstance($bankRowT)
    $bankRowT.GetField('Name').SetValue($r, $name)
    $bankRowT.GetField('Articles').SetValue($r, [int]$articles)
    return $r
}

$listT = [Collections.Generic.List`1].MakeGenericType(@($bankRowT))
$bankList = [Activator]::CreateInstance($listT)
foreach ($b in @(@('acme-legal', 4), @('acme-patents', 12), @('other-client', 1))) {
    $bankList.Add((MakeBankRow $b[0] $b[1]))
}

# @(...) enumerates a collection, so @($list) is three arguments, not one.
# The array has to be built by hand whenever an argument is itself a list.
$argv = New-Object object[] 1
$argv[0] = $bankList
$rows = $chooserHelpers.GetMethod('BankRows', $Static).Invoke($null, $argv)
$displays = @($rows | ForEach-Object { $rowT.GetField('Display').GetValue($_) })

Check ($displays[0] -eq '(none)') 'no bank is the first row, and a real one'
Check ($displays.Count -eq 4) "every bank passed in is offered: $($displays.Count) rows"
Check ((@($rows | ForEach-Object { $rowT.GetField('Detail').GetValue($_) })[2]) -match '12 articles') `
    'a bank says how much it holds'
Check ((@($rows | ForEach-Object { $rowT.GetField('Detail').GetValue($_) })[3]) -match '^1 article,') `
    'and says it in the singular when there is one'

# A chooser over those rows. Not shown - the assertions are about its state.
$ctorArgs = New-Object object[] 5
$ctorArgs[0] = 't'; $ctorArgs[1] = 'c'; $ctorArgs[2] = 'f'
$ctorArgs[3] = $rows; $ctorArgs[4] = 'acme-patents'
$chooser = [Activator]::CreateInstance($chooserT, [Reflection.BindingFlags]'Instance,Public,NonPublic',
    $null, $ctorArgs, $null)
try {
    $filterF = $chooserT.GetField('_filter', $NonPublicInstance)
    $listF = $chooserT.GetField('_list', $NonPublicInstance)
    $filter = $filterF.GetValue($chooser)
    $list = $listF.GetValue($chooser)

    Check ($list.Items.Count -eq 4) 'the chooser opens showing everything'
    Check ($rowT.GetField('Value').GetValue($list.SelectedItem) -eq 'acme-patents') `
        'and with the current choice selected'

    # Narrowing must not move the answer onto a different bank.
    $filter.Text = 'acme'
    Check ($list.Items.Count -eq 2) "typing narrows the list: $($list.Items.Count) left"
    Check ($rowT.GetField('Value').GetValue($list.SelectedItem) -eq 'acme-patents') `
        'and the selection stays on what was already chosen'

    # Narrowing PAST the selection has to land somewhere sensible.
    $filter.Text = 'other'
    Check ($rowT.GetField('Value').GetValue($list.SelectedItem) -eq 'other-client') `
        'filtering away the selection moves it to what is left'

    # An empty result must not silently mean "the first bank".
    $filter.Text = 'zzzzz-nothing-matches'
    Check ($list.Items.Count -eq 0) 'a filter that matches nothing shows nothing'
    $chooserT.GetMethod('Accept', $NonPublicInstance).Invoke($chooser, @())
    Check ($chooser.DialogResult -eq [Windows.Forms.DialogResult]::Cancel) `
        'and OK on an empty list cancels rather than choosing the first row'
    Check ($null -eq $chooser.SelectedValue) 'with nothing returned'
}
finally { $chooser.Dispose() }

Write-Host ''
Write-Host "MEMORY BANK TEST COMPLETE - $fails failure(s)"
