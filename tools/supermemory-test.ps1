# The memory banks, read the way an MCP client will read them.
#
# Read-only throughout: this exercises the three bridge endpoints against the
# real memory-banks folder, because the thing most worth checking is not that
# the code runs but that it says something TRUE about a bank that exists - and
# specifically that it never quietly substitutes one bank for another. A wrong
# bank supplies one client's terminology to another client's job and reads
# exactly like a right one.
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
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'

$sm = $plugin.GetType('Supervertaler.MemoQ.Core.SuperMemory')
$banksType = $plugin.GetType('Supervertaler.Core.MemoryBanks')

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

function Field($obj, $name) { $obj.GetType().GetProperty($name).GetValue($obj) }

# ---- 1. the banks on disk -------------------------------------------------
$names = $banksType.GetMethod('List', $Static).Invoke($null, @())
Check ($names.Count -gt 0) "banks found on disk: $($names.Count)"

# _shared is not a sibling: it layers underneath whichever bank is read. Sorting
# it last is what stops it being picked as though it were an ordinary choice.
if ($names -contains '_shared') {
    Check ($names[$names.Count - 1] -eq '_shared') 'the shared overlay sorts last, not first'
}

# Obsidian's own folder and the deletion holding pen are not banks.
Check (-not ($names -contains '.obsidian')) 'Obsidian state is not listed as a bank'
Check (-not ($names -contains '_to_delete')) 'the deletion folder is not listed as a bank'

$banks = $sm.GetMethod('Banks', $Static).Invoke($null, [object[]]@($null))
Check ((Field $banks 'Available')) 'the banks endpoint reports available'
Check ((Field $banks 'Note') -like '*name the one you want*') 'with no bank selected it says so rather than choosing one'

$rows = Field $banks 'Banks'
$withArticles = @($rows | Where-Object { $_.GetType().GetProperty('Articles').GetValue($_) -gt 0 }).Count
Check ($withArticles -gt 0) "article counts are real: $withArticles bank(s) with content"

# ---- 2. reading one -------------------------------------------------------
# [string] because a pipeline wraps its output in a PSObject, and
# reflection into a String parameter refuses one.
$real = [string](@($names | Where-Object { $_ -ne '_shared' })[0])
$ctx = $sm.GetMethod('Context', $Static).Invoke($null,
    [object[]]@($real, $null, $null, 0, 'Dutch', 'English', $null))

Check ((Field $ctx 'Available')) "read the bank '$real'"
$block = Field $ctx 'Context'
Check ($block -like '*SUPERMEMORY*') 'the formatted block is what a prompt would receive'
Check (@(Field $ctx 'Sources').Count -gt 0) "sources are cited: $(@(Field $ctx 'Sources').Count) file(s)"

# ---- 3. an unknown bank is an error, never a substitution -----------------
# The response carries a bank name either way, so a fall back would look
# exactly like success while feeding the model another client's rules.
$bogus = $sm.GetMethod('Context', $Static).Invoke($null,
    [object[]]@('no-such-bank-xyz', $null, $null, 0, 'Dutch', 'English', $null))

Check (-not (Field $bogus 'Available')) 'an unknown bank is unavailable'
Check ((Field $bogus 'Context') -eq $null) 'and returns no content at all'
Check ((Field $bogus 'Note') -like '*No memory bank called*') "and says which name failed: $(Field $bogus 'Note')"

# A blank bank name must behave the same way, not fall through to something.
$blank = $sm.GetMethod('Context', $Static).Invoke($null,
    [object[]]@('', $null, $null, 0, 'Dutch', 'English', $null))
Check (-not (Field $blank 'Available')) 'a blank bank name yields nothing rather than a default'

# Path traversal: a bank name arrives over a localhost bridge and is used to
# build a path, so it must not be able to leave the banks root.
$evil = $sm.GetMethod('Context', $Static).Invoke($null,
    [object[]]@('..\..\prompt_library', $null, $null, 0, 'Dutch', 'English', $null))
Check (-not (Field $evil 'Available')) 'a traversal attempt resolves to no bank'

# ---- 4. search ------------------------------------------------------------
$hit = $sm.GetMethod('Search', $Static).Invoke($null, [object[]]@($real, 'the', 5))
Check ((Field $hit 'Available')) 'search runs'
Check (@(Field $hit 'BanksSearched').Count -ge 1) "and reports which banks it covered: $((Field $hit 'BanksSearched') -join ', ')"

$empty = $sm.GetMethod('Search', $Static).Invoke($null, [object[]]@($real, 'zzzznotawordzzzz', 5))
Check (@(Field $empty 'Hits').Count -eq 0) 'a miss returns no hits'
Check ((Field $empty 'Note') -like '*not written down*') 'and says a miss means unwritten, not unimportant'

Write-Host ''
Write-Host "SUPERMEMORY TEST COMPLETE - $fails failure(s)"
