# Which project is open, learnt from the live document link.
#
# The plugin used to learn this from one place only: the ProjectGuid on a
# translation request. A project that never sends one - MT plugins disabled by
# the project manager, or simply not clicked into - left the editor naming the
# previous job, and a memory bank chosen then was filed against it. This checks
# the other channel: a document GUID the preview tool reports, resolved through
# memoQ's project folders to the project and its own GUID.
#
# The premise being tested is a measured one: the <ID> inside <CoreInfo> of
# project.mprx is the same GUID memoQ sends as ProjectGuid. A synthetic project
# folder stands in for a real one, so the harness never depends on which
# projects happen to exist on this machine.
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
$Inst = [Reflection.BindingFlags]'Public,NonPublic,Instance'

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

$namesT = $plugin.GetType('Supervertaler.MemoQ.Core.DocumentNames')
$followT = $plugin.GetType('Supervertaler.MemoQ.Core.ProjectFollow')
$sharedT = $plugin.GetType('Supervertaler.MemoQ.Core.SharedSettings')
$choiceT = $plugin.GetType('Supervertaler.MemoQ.Core.MemoryBankChoice')
$engineT = $plugin.GetType('Supervertaler.MemoQ.Core.EngineContext')

# ---- a synthetic projects root -------------------------------------------------
$root = Join-Path ([IO.Path]::GetTempPath()) ("sv-projects-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null

function MakeProject($folderName, $projectId, $documentIds, $withMprx = $true) {
    $dir = Join-Path $root $folderName
    New-Item -ItemType Directory -Path (Join-Path $dir 'Documents') -Force | Out-Null
    if ($withMprx) {
        $mprx = '<?xml version="1.0" encoding="utf-8"?><ProjectInfo><CoreInfo>' +
                '<SourceLangCode>eng</SourceLangCode><Name>' + $folderName + '</Name>' +
                '<ID>' + $projectId + '</ID>' +
                '<MasterProjectGuid>' + $projectId + '</MasterProjectGuid><Type>LocalCopy</Type>' +
                '</CoreInfo></ProjectInfo>'
        [IO.File]::WriteAllText((Join-Path $dir 'project.mprx'), $mprx, (New-Object Text.UTF8Encoding($false)))
    }
    foreach ($d in $documentIds) {
        $ver = Join-Path (Join-Path (Join-Path $dir 'Documents') $d) 'ver1'
        New-Item -ItemType Directory -Path $ver -Force | Out-Null
        # majorVersionStore.info: a length-prefixed string among binary noise.
        $name = 'PROJ-001 spec.docx'
        $bytes = [byte[]]@(0x0c, 0x00, 0xff) + [byte]$name.Length + [Text.Encoding]::ASCII.GetBytes($name) + [byte[]]@(0x00, 0x01)
        [IO.File]::WriteAllBytes((Join-Path $ver 'majorVersionStore.info'), $bytes)
    }
    return $dir
}

$projectA = [Guid]::NewGuid().ToString('D')
$projectB = [Guid]::NewGuid().ToString('D')
$docA = [Guid]::NewGuid().ToString('D')
$docB = [Guid]::NewGuid().ToString('D')
$docNoMprx = [Guid]::NewGuid().ToString('D')
MakeProject 'Acme (PROJ-001)' $projectA @($docA) | Out-Null
MakeProject 'Acme (PROJ-002) server copy' $projectB @($docB) | Out-Null
MakeProject 'Folder without a project file' ([Guid]::NewGuid().ToString('D')) @($docNoMprx) $false | Out-Null

# Point the resolver at it. Without this the test would depend on the projects
# this machine happens to hold - and would write nothing it could assert.
$namesT.GetField('RootsOverride', $Static).SetValue($null, [string[]]@($root))
$namesT.GetField('MissRetry', $Static).SetValue($null, [TimeSpan]::Zero)

try {

# ---- 1. the project GUID out of project.mprx ----------------------------------
$projectIdOf = $namesT.GetMethod('ProjectIdOf', $Static)
function ProjectIdOf($p) { return [Guid]$projectIdOf.Invoke($null, [object[]]@([string]$p)) }
Check ((ProjectIdOf (Join-Path (Join-Path $root 'Acme (PROJ-001)') 'project.mprx')).ToString('D') -eq $projectA) 'the ID inside CoreInfo is read'
Check ((ProjectIdOf (Join-Path $root 'no-such-file.mprx')) -eq [Guid]::Empty) 'a missing file is Empty, not an exception'
$junk = Join-Path $root 'junk.mprx'
[IO.File]::WriteAllText($junk, 'not xml at all')
Check ((ProjectIdOf $junk) -eq [Guid]::Empty) 'a file with no CoreInfo is Empty'
# An <ID> that is not inside CoreInfo must not be mistaken for the project's.
$decoy = Join-Path $root 'decoy.mprx'
[IO.File]::WriteAllText($decoy, '<ProjectInfo><Other><ID>11111111-1111-1111-1111-111111111111</ID></Other><CoreInfo><ID>' + $projectB + '</ID></CoreInfo></ProjectInfo>')
Check ((ProjectIdOf $decoy).ToString('D') -eq $projectB) 'an ID before CoreInfo is not mistaken for the project GUID'

# ---- 2. document GUID -> project -----------------------------------------------
$resolve = $namesT.GetMethod('Resolve', $Static)
function Resolve($g) { return $resolve.Invoke($null, [object[]]@([Guid]$g)) }
$n = Resolve $docA
Check ($n -ne $null -and $n.Project -eq 'Acme (PROJ-001)') "the document resolves to its project folder (got '$(if ($n) {$n.Project})')"
Check ($n -ne $null -and $n.ProjectId.ToString('D') -eq $projectA) 'and carries the project GUID'
Check ($n -ne $null -and $n.Document -eq 'PROJ-001 spec.docx') "and the document's file name (got '$(if ($n) {$n.Document})')"
$n2 = Resolve $docNoMprx
Check ($n2 -ne $null -and $n2.ProjectId -eq [Guid]::Empty) 'a project folder with no project.mprx: named, but no GUID'
Check ((Resolve ([Guid]::NewGuid())) -eq $null) 'a document no folder holds resolves to nothing'
Check ((Resolve ([Guid]::Empty)) -eq $null) 'the empty GUID resolves to nothing'

# ---- 3. following it -------------------------------------------------------------
$follow = $followT.GetMethod('Follow', $Static)
function Follow($g, $ctx) { return $follow.Invoke($null, [object[]]@([Guid]$g, $ctx)) }
$bankProject = $sharedT.GetProperty('MemoryBankProject', $Static)
$bankName = $sharedT.GetProperty('MemoryBankProjectName', $Static)
$bank = $sharedT.GetProperty('MemoryBank', $Static)
$remember = $choiceT.GetMethod('Remember', $Static)

# Each project's recorded bank, so the switch has something to carry.
$remember.Invoke($null, [object[]]@([Guid]$projectA, 'acme-proj-001')) | Out-Null
$remember.Invoke($null, [object[]]@([Guid]$projectB, '')) | Out-Null

$bankProject.SetValue($null, 'something-else')
$bankName.SetValue($null, 'The previous job')
$bank.SetValue($null, 'previous-client')

$r = Follow $docA $null
Check ($r -ne $null) 'following a known document answers with what it found'
Check ($bankProject.GetValue($null) -eq $projectA) 'the recorded project switches to the one the document belongs to'
Check ($bankName.GetValue($null) -eq 'Acme (PROJ-001)') "and its name (got '$($bankName.GetValue($null))')"
Check ($bank.GetValue($null) -eq 'acme-proj-001') "and the bank that project uses (got '$($bank.GetValue($null))')"

# The failure mode this whole change exists to stop: a project with no bank
# recorded must CLEAR, never inherit the last client's terminology.
$r = Follow $docB $null
Check ($bankProject.GetValue($null) -eq $projectB) 'a second document switches again'
Check ($bank.GetValue($null) -eq '') 'a project with no bank recorded clears rather than inheriting the previous one'

# A document nothing holds leaves everything alone - a wrong guess would be
# worse than the stale name it replaced.
$bankProject.SetValue($null, $projectB)
$bank.SetValue($null, 'kept')
Check ((Follow ([Guid]::NewGuid()) $null) -eq $null) 'an unknown document follows nothing'
Check ($bankProject.GetValue($null) -eq $projectB -and $bank.GetValue($null) -eq 'kept') 'and changes nothing'
Check ((Follow ([Guid]::Empty) $null) -eq $null) 'the empty GUID follows nothing'
$r = Follow $docNoMprx $null
Check ($r -ne $null -and $bankProject.GetValue($null) -eq $projectB) 'a project whose GUID could not be read changes nothing either'

# ---- 4. re-following the same project is not a bank writer ----------------------
# This runs on every cursor move once the preview tool is connected. The bank
# chooser writes the bank and only then records the choice, so a re-apply here
# would race it and revert the user's click.
$bankProject.SetValue($null, $projectA)
$bankName.SetValue($null, 'Acme (PROJ-001)')
$bank.SetValue($null, 'chosen-by-hand-a-moment-ago')
Follow $docA $null | Out-Null
Check ($bank.GetValue($null) -eq 'chosen-by-hand-a-moment-ago') 'the same project again does not overwrite a bank just chosen'
Follow $docA $null | Out-Null
Follow $docA $null | Out-Null
Check ($bank.GetValue($null) -eq 'chosen-by-hand-a-moment-ago') 'nor on the tenth cursor move'

# The name catches up when the GUID was recorded before the folder was found.
$bankName.SetValue($null, '')
Follow $docA $null | Out-Null
Check ($bankName.GetValue($null) -eq 'Acme (PROJ-001)') 'a missing name is filled in without touching the bank'
Check ($bank.GetValue($null) -eq 'chosen-by-hand-a-moment-ago') 'and the bank is still the chosen one'

# ---- 5. through an engine context -------------------------------------------------
$generalT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings')
$secureT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSecureSettings')
$settingsT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSettings')
$settings = $settingsT.GetMethod('Create').Invoke($null,
    @([Activator]::CreateInstance($generalT), [Activator]::CreateInstance($secureT)))
$ctx = [Activator]::CreateInstance($engineT, $Inst, $null, @($settings, 'eng', 'dut'), $null)
$currentProject = $engineT.GetProperty('CurrentProject')

$bankProject.SetValue($null, 'something-else')
$bank.SetValue($null, 'previous-client')
Follow $docA $ctx | Out-Null
Check ([Guid]$currentProject.GetValue($ctx) -eq [Guid]$projectA) 'with an engine, the engine learns the project too'
Check ($bank.GetValue($null) -eq 'acme-proj-001') 'and the bank follows'

# ---- 6. the file stamp the editor watches -----------------------------------------
$stamp = $sharedT.GetProperty('FileStamp', $Static)
$before = [long]$stamp.GetValue($null)
Check ($before -gt 0) 'the settings file has a stamp'
Start-Sleep -Milliseconds 20
$bank.SetValue($null, 'moved')
$after = [long]$stamp.GetValue($null)
Check ($after -ge $before) 'writing the file does not lower it'
Check ([long]$stamp.GetValue($null) -eq $after) 'and reading it twice gives the same answer'

} finally {
    $namesT.GetField('RootsOverride', $Static).SetValue($null, $null)
    try { Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue } catch { }
}

Write-Host ''
Write-Host "PROJECT FOLLOW TEST COMPLETE - $fails failure(s)"
