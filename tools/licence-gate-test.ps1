# memoQ's licence gate: what a lapsed licence switches off, and what it never may.
#
# The licence itself is core's and has its own tests. What is memoQ's is the
# policy on top of it, and that is small enough to state completely:
#
#   - Only a licence KNOWN to have lapsed pauses anything. Unknown - the licence
#     could not be read - must never be a refusal, because a missing answer must
#     never lock out a paying customer. That is the assertion here that matters
#     most, and the one a well-meaning change would most easily break.
#   - What pauses is the AI: every route an assistant calls, and AutoPrompt.
#     Reading which project is open stays open, because the editor's own panel
#     uses the same route.
#   - A harness never reads or writes the real licence. Harnesses in this repo
#     have written the user's real state before; this one checks it did not.
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

$Repo = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ'
$asm = [Reflection.Assembly]::LoadFrom((Join-Path $Repo 'src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll'))
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'

$licence = $asm.GetType('Supervertaler.MemoQ.Core.Licence')
$bridge  = $asm.GetType('Supervertaler.MemoQ.Core.MemoQBridge')
$stateT  = $asm.GetType('Supervertaler.Core.LicenceState')

$allows  = $licence.GetMethod('AllowsAi', $Static)
$closed  = $bridge.GetMethod('ClosedWhenLapsed', $Static)

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}
function Allows($name) { return $allows.Invoke($null, [object[]]@([Enum]::Parse($stateT, $name))) }
function Closed($path) { return $closed.Invoke($null, [object[]]@([string]$path)) }

# ---- 1. the policy, for every state ---------------------------------------
Check (Allows 'Unknown')      'an UNREADABLE licence keeps the AI running (never a refusal)'
Check (Allows 'Trial')        'a trial keeps the AI running'
Check (Allows 'Licensed')     'a licence keeps the AI running'
Check (-not (Allows 'Expired')) 'only a licence known to have lapsed pauses it'

# Every state the enum has, so a state added later cannot slip past this test
# unexamined: if core adds one, this fails until someone decides what it means.
$known = @('Unknown', 'Licensed', 'Trial', 'Expired')
foreach ($n in [Enum]::GetNames($stateT)) {
    Check ($known -contains $n) "the licence state '$n' has had its meaning for memoQ decided"
}

# ---- 2. which routes pause ------------------------------------------------
$tools = (Get-Content (Join-Path $Repo 'src\Supervertaler.MemoQ\Resources\mcp-tools.json') -Raw | ConvertFrom-Json).tools
if (-not $tools) { $tools = (Get-Content (Join-Path $Repo 'src\Supervertaler.MemoQ\Resources\mcp-tools.json') -Raw | ConvertFrom-Json) }
$paths = @($tools | ForEach-Object { $_.path } | Sort-Object -Unique)
Check ($paths.Count -gt 5) "the assistants' routes were read from the manifest: $($paths.Count)"

foreach ($p in $paths) {
    if ($p -eq '/v1/project') { continue }
    Check (Closed $p) "an assistant's route pauses when lapsed: $p"
}

Check (-not (Closed '/v1/project'))       'reading the open project stays open - the editor panel uses it'
Check (-not (Closed '/v1/project/sync'))  "the editor's project sync stays open"
Check (-not (Closed '/v1/tools'))         'the tool list stays open, so the server can start and say why'
Check (Closed '/v1/autoprompt')           'AutoPrompt pauses'
Check (Closed '/v1/autoprompt/classify')  'and its classification step'
Check (Closed '/v1/autoprompt/preview')   'and its preview'
Check (Closed '/v1/segments/')            'a trailing slash does not slip past the gate'
Check (-not (Closed ''))                  'an empty path is not treated as a tool'

# ---- 3. a harness never touches the real licence --------------------------
# Run through run-harness.ps1, so SUPERVERTALER_HARNESS is set. The licence
# file and the trial anchor must be exactly as they were afterwards.
$file = 'D:\Supervertaler\licence\licence.json'
$before = if (Test-Path $file) { [IO.File]::ReadAllBytes($file) } else { $null }
$anchorBefore = (Get-ItemProperty -Path 'HKCU:\Software\Supervertaler' -Name 'ts' -ErrorAction SilentlyContinue).ts

Check ($env:SUPERVERTALER_HARNESS -eq '1') 'this is running under the harness flag'

$aiAllowed = $licence.GetProperty('AiAllowed', $Static).GetValue($null)
Check ($aiAllowed -eq $true) 'under a harness the gate is open'

$message = $licence.GetProperty('PausedMessage', $Static).GetValue($null)
Check ($message -match 'trial') 'the paused message reads as the trial one when no licence is consulted'

$start = $licence.GetMethod('Start', $Static)
[void]$start.Invoke($null, [object[]]@($null))
Start-Sleep -Milliseconds 500   # the online check, if it wrongly started, would be under way

$after = if (Test-Path $file) { [IO.File]::ReadAllBytes($file) } else { $null }
$anchorAfter = (Get-ItemProperty -Path 'HKCU:\Software\Supervertaler' -Name 'ts' -ErrorAction SilentlyContinue).ts

$same = ($null -eq $before -and $null -eq $after) -or
        ($null -ne $before -and $null -ne $after -and [Convert]::ToBase64String($before) -eq [Convert]::ToBase64String($after))
Check $same 'the real licence file was not touched'
Check ($anchorBefore -eq $anchorAfter) 'nor the real trial anchor'

Write-Host ''
if ($fails -gt 0) { Write-Host "$fails failed"; exit 1 }
Write-Host 'all passed'
