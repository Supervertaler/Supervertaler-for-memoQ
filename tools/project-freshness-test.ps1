# Issue #8: is the project the editor shows the one open NOW?
#
# The editor showed yesterday's memoQ project as if it were open, and a memory
# bank chosen at that moment was filed against the other client's project. The
# fix records which memoQ session reported the project - memoQ's process start
# time - and the editor compares it with the memoQ that is running. This pins
# the comparison for every case, and the one trap in writing the stamp.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$memoq = 'C:\Program Files\memoQ\memoQ-12'
$inFlight = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)
    $name = ([Reflection.AssemblyName]::new($eventArgs.Name)).Name
    if (-not $inFlight.Add($name)) { return $null }
    try {
        $c = Join-Path $memoq "$name.dll"
        if (Test-Path -LiteralPath $c) { return [Reflection.Assembly]::LoadFrom($c) }
        return $null
    } finally { [void]$inFlight.Remove($name) }
})

$asm = [Reflection.Assembly]::LoadFrom('D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll')
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'

$session  = $asm.GetType('Supervertaler.MemoQ.Core.MemoQSession')
$settings = $asm.GetType('Supervertaler.MemoQ.Core.SharedSettings')
$engine   = $asm.GetType('Supervertaler.MemoQ.Core.EngineContext')
$classify = $session.GetMethod('Classify', $Static)

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# A list of running start times as the method takes it: nullable DateTimes.
function Running([object[]]$starts) {
    $list = New-Object 'System.Collections.Generic.List[Nullable[DateTime]]'
    foreach ($s in $starts) {
        if ($null -eq $s) { $list.Add($null) } else { $list.Add([Nullable[DateTime]]([DateTime]$s)) }
    }
    return ,$list
}
function Judge($hasProject, $recorded, $running) {
    # Built by index: PowerShell wraps a list passed through a function in its own
    # object, which reflection will not convert back to the interface type.
    $a = New-Object object[] 3
    $a[0] = [bool]$hasProject
    $a[1] = [string]$recorded
    $a[2] = $running.psobject.BaseObject
    return [string]$classify.Invoke($null, $a)
}

$today     = [DateTime]::new(2026, 9, 23, 8, 0, 0, [DateTimeKind]::Utc)
$yesterday = $today.AddDays(-1)
$stamp     = { param($t) $t.ToString('o', [Globalization.CultureInfo]::InvariantCulture) }

# ---- 1. every case of the comparison ---------------------------------------
Check ((Judge $false (& $stamp $today) (Running @($today))) -eq 'None') `
    'no project recorded: nothing to judge'
Check ((Judge $true (& $stamp $today) (Running @())) -eq 'MemoQClosed') `
    'memoQ not running: the last project it reported, not the open one'
Check ((Judge $true (& $stamp $today) (Running @($today))) -eq 'Current') `
    'reported by the memoQ running now: current'
Check ((Judge $true (& $stamp $today.AddMilliseconds(900)) (Running @($today))) -eq 'Current') `
    'the same start read twice, a moment apart: still current'
Check ((Judge $true (& $stamp $yesterday) (Running @($today))) -eq 'EarlierSession') `
    "yesterday's memoQ reported it and today's has not spoken: THE CASE IN THE ISSUE"
Check ((Judge $true '' (Running @($today))) -eq 'EarlierSession') `
    'recorded before sessions were recorded: cannot be proved current, so it is not'
Check ((Judge $true 'not a date' (Running @($today))) -eq 'EarlierSession') `
    'an unreadable stamp: not current'
Check ((Judge $true (& $stamp $today) (Running @($yesterday, $today))) -eq 'Current') `
    'two memoQs running, one of them reported it: current'
Check ((Judge $true (& $stamp $yesterday) (Running @($null))) -eq 'CannotTell') `
    "memoQ running but its start time unreadable: says so, rather than guessing either way"
Check ((Judge $true (& $stamp $today) (Running @($null, $today))) -eq 'Current') `
    'one unreadable, another matches: current'

# ---- 2. the trap: stamping a session that reopened the same project ---------
# RecordProject returns early when the project has not changed. A new memoQ
# session reopening yesterday's project is exactly that case, so if the stamp
# were written only on a change, a genuinely current project would read as an
# earlier session for as long as the translator worked in it. Run under
# run-harness.ps1, which restores the settings file afterwards.
$get = { param($p) $settings.GetProperty($p, $Static).GetValue($null) }
$set = { param($p, $v) $settings.GetProperty($p, $Static).SetValue($null, $v) }
$record = $engine.GetMethod('RecordProject', $Static)
$thisSession = [string]$session.GetMethod('ThisProcessStamp', $Static).Invoke($null, @())

Check ($thisSession.Length -gt 0) "this process's session can be read: $thisSession"

$project = [Guid]::NewGuid()
& $set 'MemoryBankProject' $project.ToString('D')
& $set 'MemoryBankProjectName' 'Acme (PROJ-001)'
& $set 'MemoryBankSession' (& $stamp $yesterday)

[void]$record.Invoke($null, [object[]]@($project, 'Acme (PROJ-001)'))
Check ((& $get 'MemoryBankSession') -eq $thisSession) `
    'the SAME project reported in a new session is stamped with that session'

$other = [Guid]::NewGuid()
& $set 'MemoryBankSession' (& $stamp $yesterday)
[void]$record.Invoke($null, [object[]]@($other, 'Acme (PROJ-002)'))
Check ((& $get 'MemoryBankSession') -eq $thisSession) 'a different project is stamped too'
Check ((& $get 'MemoryBankProject') -eq $other.ToString('D')) 'and recorded as the project'

Write-Host ''
if ($fails -gt 0) { Write-Host "$fails failed"; exit 1 }
Write-Host 'all passed'
