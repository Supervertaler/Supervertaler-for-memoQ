# Which termbases memoQ uses, and how - the store, not the database.
#
# This writes D:\Supervertaler\memoq\termbases.txt for real, which is the user's
# own choice of terminology. run-harness.ps1 snapshots and restores it; do not
# run this directly.
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
        $candidate = Join-Path $dir "$name.dll"
        if (Test-Path $candidate) { try { return [Reflection.Assembly]::LoadFrom($candidate) } catch { return $null } }
    }
    return $null
})

$plugin = [Reflection.Assembly]::LoadFrom($PluginDll)
$NonPublicStatic = [Reflection.BindingFlags]'NonPublic,Static'

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("PASS {0}" -f $name) }
    else { $script:fail++; Write-Host ("FAIL {0}  {1}" -f $name, $detail) -ForegroundColor Red }
}

$sel = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseSelection')
$flagsType = $plugin.GetType('Supervertaler.MemoQ.Core.TermbaseSelection+Flags')
if ($null -eq $sel -or $null -eq $flagsType) { throw 'TermbaseSelection not found.' }

$sel.GetField('ErrorSink', $NonPublicStatic).SetValue($null, [Action[string, Exception]]{
    param($m, $ex) Write-Host ("   sink: {0} {1}" -f $m, $(if ($ex) { $ex.Message })) -ForegroundColor Yellow
})

$path = $sel.GetProperty('Path', $NonPublicStatic).GetValue($null)
Write-Host "store: $path"
if (Test-Path $path) { Remove-Item $path -Force }

$projectA = [Guid]'11111111-1111-1111-1111-111111111111'
$projectB = [Guid]'22222222-2222-2222-2222-222222222222'

function ReadFor($project) {
    $a = [object[]]::new(1); $a[0] = $project
    return $sel.GetMethod('ReadFor', $NonPublicStatic).Invoke($null, $a)
}
function AiFor($project) {
    $a = [object[]]::new(1); $a[0] = $project
    return $sel.GetMethod('AiFor', $NonPublicStatic).Invoke($null, $a)
}
function NewFlags([long]$id, [int]$rank, [bool]$cs, [bool]$ai, [string]$name) {
    $f = [Activator]::CreateInstance($flagsType)
    $f.Id = $id; $f.Rank = $rank; $f.CaseSensitive = $cs; $f.Ai = $ai; $f.Name = $name
    return $f
}
function Save($project, $readIds, $flags) {
    $ids = New-Object 'System.Collections.Generic.List[long]'
    foreach ($i in $readIds) { $ids.Add([long]$i) }
    # Built by reflection, not by name: Flags is internal to an assembly loaded
    # with LoadFrom, and PowerShell cannot resolve such a type from a string.
    $listType = [System.Collections.Generic.List`1].MakeGenericType($flagsType)
    $list = [Activator]::CreateInstance($listType)
    foreach ($f in $flags) { $list.Add($f) }
    $a = [object[]]::new(3)
    $a[0] = $project
    $a[1] = $ids.PSObject.BaseObject
    $a[2] = $list.PSObject.BaseObject
    $sel.GetMethod('Save', $NonPublicStatic).Invoke($null, $a)
}

# -- nothing chosen yet ------------------------------------------------------
Check 'no file means no termbases, not all of them' ((ReadFor $projectA).Count -eq 0)

# -- a selection round-trips -------------------------------------------------
$flags = @(
    (NewFlags 13 2 $false $true  'BEIJER'),
    (NewFlags 17 1 $true  $false 'EuroVoc'),
    (NewFlags 52 0 $false $false 'Cedefop')      # unranked
)
Save $projectA @(13, 17, 52) $flags
Check 'the file was written' (Test-Path $path)

$back = ReadFor $projectA
Check "three termbases come back ($($back.Count))" ($back.Count -eq 3)

# Rank 1 first, rank 2 second, unranked last - which is what decides the shade
# of a term hit in memoQ's pane, so the order is not cosmetic.
Check 'ranked first, in rank order' ($back[0] -eq 17 -and $back[1] -eq 13) ("$back")
Check 'unranked sorts last' ($back[2] -eq 52) ("$back")

$ai = AiFor $projectA
Check 'only the AI-ticked one reaches the model' ($ai.Count -eq 1 -and $ai[0] -eq 13) ("$ai")

$all = $sel.GetMethod('All', $NonPublicStatic).Invoke($null, @())
Check 'case sensitivity survives the round trip' ($all[[long]17].CaseSensitive)
Check 'names are kept for readability' ($all[[long]13].Name -eq 'BEIJER')

# -- one project's choice is not another's -----------------------------------
Check 'another project has its own (empty) selection' ((ReadFor $projectB).Count -eq 0)
Save $projectB @(52) $flags
Check 'and keeps it' ((ReadFor $projectB).Count -eq 1)
Check 'without disturbing the first' ((ReadFor $projectA).Count -eq 3)

# -- the failure path --------------------------------------------------------
# A damaged file must cost a forgotten selection, never an exception in the
# middle of a memoQ lookup.
Set-Content -Path $path -Value "this is not`tthe format`nread`tnonsense" -Encoding UTF8
$damaged = ReadFor $projectA
Check 'a damaged file yields nothing rather than throwing' ($damaged.Count -eq 0)

# And it recovers: writing again replaces the wreckage.
Save $projectA @(13) $flags
Check 'and the store recovers on the next save' ((ReadFor $projectA).Count -eq 1)

Write-Host ''
Write-Host ("TERMBASE SELECTION TEST COMPLETE - {0} passed, {1} failed" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
