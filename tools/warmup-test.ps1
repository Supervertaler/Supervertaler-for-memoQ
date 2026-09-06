# The gate that lets one request warm the prompt cache before the rest go (#5).
#
# Everything here is about the ways a gate like this goes wrong, because the
# happy path is one line and the failure paths hang a whole Pre-translate:
#
#   - it must open again when the warming request THROWS, or every other batch
#     in the run waits on one that will never complete;
#   - it must be one-shot, or every batch serialises and the job takes four
#     times as long for no saving at all;
#   - it must be per engine, because the block being cached is that engine's
#     system prompt.
#
# No network and no key: the gate is exercised directly, which is the only part
# that has a right answer.
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
$Ctor = [Reflection.BindingFlags]'Instance,Public,NonPublic'

$engineCt = $plugin.GetType('Supervertaler.MemoQ.Core.EngineContext')
$generalT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings')
$secureT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSecureSettings')
$settingsT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSettings')

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

function NewContext {
    $settings = $settingsT.GetMethod('Create').Invoke($null,
        @([Activator]::CreateInstance($generalT), [Activator]::CreateInstance($secureT)))
    return [Activator]::CreateInstance($engineCt, $Ctor, $null, @($settings, 'dut', 'eng'), $null)
}

$ct = [System.Threading.CancellationToken]::None
function Enter($ctx) {
    $t = $engineCt.GetMethod('EnterWarmupAsync').Invoke($ctx, [object[]]@($ct))
    $t.Wait()
    return $t.Result
}
function Leave($ctx) { $engineCt.GetMethod('LeaveWarmup').Invoke($ctx, @()) | Out-Null }

# ---- 1. exactly one caller warms ----------------------------------------
$ctx = NewContext
Check (Enter $ctx) "the first caller is told to warm the cache"

# A second caller while the first still holds it would block, so the ordering
# is checked the way it happens in a run: warm, release, then the next arrives.
Leave $ctx
Check (-not (Enter $ctx)) "the next caller is not - it reads what the first wrote"
Check (-not (Enter $ctx)) "and neither is the one after that"

# ---- 2. one-shot, so the run does not serialise -------------------------
# The expensive failure: a gate that stays closed makes every batch wait for
# the one before it, and a 38-batch run takes four times as long for nothing.
$ctx2 = NewContext
Check (Enter $ctx2) "a fresh engine warms once"
Leave $ctx2
$serialised = $false
foreach ($i in 1..20) { if (Enter $ctx2) { $serialised = $true } }
Check (-not $serialised) "twenty later batches all go straight through"

# ---- 3. per engine ------------------------------------------------------
# A new engine means a new system prompt and a new cache entry, so the gate has
# to close again. A static would leave the second engine's first wave racing.
$ctx3 = NewContext
Check (Enter $ctx3) "a second engine warms its own cache"
Leave $ctx3

# ---- 4. releasing more than once is not fatal ---------------------------
# LeaveWarmup runs in a finally; a future edit that also called it on the happy
# path would throw SemaphoreFullException into the middle of a translation.
$threw = $false
try { Leave $ctx3; Leave $ctx3 } catch { $threw = $true }
Check (-not $threw) "an extra release is swallowed rather than thrown into a batch"

# ---- 5. the failure path ------------------------------------------------
# The one that hangs a Pre-translate: the warming request throws and the gate
# has to open anyway. Modelled the way the code does it - the release is in a
# finally, so it runs whatever happened.
$ctx4 = NewContext
$warming = Enter $ctx4
Check ($warming) "a caller takes the gate"

$caught = $false
try {
    try { throw [InvalidOperationException]::new('the model refused the key') }
    finally { if ($warming) { Leave $ctx4 } }
}
catch { $caught = $true }

Check ($caught) "the failure still propagates to the caller"
Check (-not (Enter $ctx4)) "and the gate is open, so the rest of the run proceeds"

# ---- 6. an already-cancelled token does not stall -----------------------
# Waiting throws on a cancelled token. Translation must go ahead unsynchronised
# rather than stop: the worst case is the old behaviour, a second cache write.
$ctx5 = NewContext
$cts = New-Object System.Threading.CancellationTokenSource
$cts.Cancel()
$t = $engineCt.GetMethod('EnterWarmupAsync').Invoke($ctx5, [object[]]@($cts.Token))
$t.Wait()
Check (-not $t.Result) "a cancelled token means do not warm, rather than throw or wait"

# And the gate was never taken, so a later caller can still warm.
Check (Enter $ctx5) "the gate is still free afterwards"

# ---- 7. the property itself: four at once, one warmer -------------------
# Everything above is sequential, which proves the bookkeeping and not the
# point. memoQ calls the session on Parallel.ForEach workers, so the case that
# matters is four callers arriving together - and the whole fix is that exactly
# one of them warms while the other three wait and then read.
#
# No threads needed: EnterWarmupAsync returns a Task, so four un-awaited calls
# are four callers in flight. The three that lose block inside WaitAsync.
$ctx6 = NewContext
$enter = $engineCt.GetMethod('EnterWarmupAsync')

$tasks = @()
foreach ($i in 1..4) { $tasks += $enter.Invoke($ctx6, [object[]]@($ct)) }

$done = @($tasks | Where-Object { $_.IsCompleted })
Check ($done.Count -eq 1) "one of four simultaneous callers gets through; the rest wait: $($done.Count) completed"
Check ($done[0].Result) "and the one that got through is the warmer"

# The warmer finishes its request and opens the gate. The other three should
# then come back one after another, all told not to warm.
Leave $ctx6
[System.Threading.Tasks.Task]::WaitAll($tasks, 5000) | Out-Null

$warmers = @($tasks | Where-Object { $_.Result })
Check ($warmers.Count -eq 1) "exactly one warmer among all four: $($warmers.Count)"
Check (@($tasks | Where-Object { $_.IsCompleted }).Count -eq 4) "and the other three were released rather than left waiting"

# Which is the saving: three cache writes became three cache reads.
Check (-not (Enter $ctx6)) "later batches go straight through"

Write-Host ''
Write-Host "WARM-UP TEST COMPLETE - $fails failure(s)"
