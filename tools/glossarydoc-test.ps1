# Editing a glossary in a grid must not lose what the grid does not show.
#
# A glossary file carries more than terms: a "#!" header saying which direction
# it runs in, and comments recording where it came from. Both were put there on
# purpose, and neither appears in a three-column grid - so a save that writes
# only what the grid holds deletes them silently, and the loss shows up much
# later as a glossary that matches nothing.
#
# Nothing here touches a real glossary; every case is written to a temp file.
$ErrorActionPreference = 'Stop'
$EditorExe = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe'

Add-Type -AssemblyName System.Windows.Forms

$editor = [Reflection.Assembly]::LoadFrom($EditorExe)
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'
$Inst = [Reflection.BindingFlags]'Public,NonPublic,Instance'

$docT = $editor.GetType('Supervertaler.PromptEditor.GlossaryDocument')
$itemT = $docT.GetNestedType('Item', $Inst)
$load = $docT.GetMethod('Load', $Static)

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

$dir = Join-Path $env:TEMP 'sv-glossary-test'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

function Write-Glossary($name, $lines) {
    $path = Join-Path $dir $name
    [IO.File]::WriteAllLines($path, $lines, (New-Object Text.UTF8Encoding($false)))
    return [string]$path
}

function Entries($doc) { return @($doc.Entries) }
function Get($o, $f) { return $o.GetType().GetField($f).GetValue($o) }

function MakeItem($source, $target, $forbidden) {
    $i = [Activator]::CreateInstance($itemT)
    $itemT.GetField('Source').SetValue($i, $source)
    $itemT.GetField('Target').SetValue($i, $target)
    $itemT.GetField('Forbidden').SetValue($i, [bool]$forbidden)
    return $i
}

function SetEntries($doc, $items) {
    $listT = [Collections.Generic.List`1].MakeGenericType(@($itemT))
    $list = [Activator]::CreateInstance($listT)
    foreach ($i in $items) { $list.Add($i) }
    # @($list) would enumerate it into N arguments, so the array is built by hand.
    $argv = New-Object object[] 1
    $argv[0] = $list
    $docT.GetMethod('SetEntries', $Inst).Invoke($doc, $argv) | Out-Null
}

# ---- 1. what a real exported glossary looks like -------------------------
$lines = @(
    '#! source=dut target=eng',
    '# exported from Acme (PROJ-001) v3',
    'elektrische module	electric module',
    'elektrische module	electrical module	forbidden',
    '',
    '# added by hand after review',
    'koppelmechanisme	coupling mechanism'
)
$path = Write-Glossary 'round-trip.txt' $lines
$doc = $load.Invoke($null, [object[]]@([string]$path))

Check ($doc.Count -eq 3) "three terms read, comments not counted: $($doc.Count)"
Check ($doc.Header['source'] -eq 'dut' -and $doc.Header['target'] -eq 'eng') "the direction header is parsed"

$e = Entries $doc
Check ((Get $e[1] 'Forbidden')) "a third column of 'forbidden' marks the entry"
Check (-not (Get $e[0] 'Forbidden')) "and an entry without one is not marked"

# ---- 2. an untouched save changes nothing -------------------------------
# The strongest form of the rule: read it, write it back, and the file is what
# it was. Anything the editor cannot represent shows up here as a difference.
$rendered = $docT.GetMethod('Render', $Inst).Invoke($doc, @())
$original = [IO.File]::ReadAllText($path)
Check (($rendered -replace "`r`n", "`n") -eq (($original -replace "`r`n", "`n"))) `
    "a read and a write with no edits leaves the file identical"

# ---- 3. editing a term keeps everything around it -----------------------
$e = Entries $doc
$edited = @(
    (MakeItem 'elektrische module' 'electric module' $false),
    (MakeItem 'elektrische module' 'electrical module' $true),
    (MakeItem 'koppelmechanisme' 'coupling assembly' $false)
)
SetEntries $doc $edited
$out = $docT.GetMethod('Render', $Inst).Invoke($doc, @())

Check ($out.Contains('#! source=dut target=eng')) "the direction header survives an edit"
Check ($out.Contains('# exported from Acme (PROJ-001) v3')) "so does the comment above the terms"
Check ($out.Contains('# added by hand after review')) "and the one in the middle of them"
Check ($out.Contains("koppelmechanisme`tcoupling assembly")) "the edited term is written"
Check (-not $out.Contains('coupling mechanism')) "and the old value is gone"

# ---- 4. adding and deleting -----------------------------------------------
$doc2 = $load.Invoke($null, [object[]]@([string]$path))
SetEntries $doc2 @(
    (MakeItem 'elektrische module' 'electric module' $false),
    (MakeItem 'nieuwe term' 'new term' $false),
    (MakeItem 'nog een' 'another one' $true)
)
$out2 = $docT.GetMethod('Render', $Inst).Invoke($doc2, @())

Check ($out2.Contains("nieuwe term`tnew term")) "a term added in the grid is written"
Check ($out2.Contains("nog een`tanother one`tforbidden")) "and a forbidden one keeps its flag"
Check (-not $out2.Contains('koppelmechanisme')) "a term deleted in the grid is removed"
Check ($out2.Contains('#! source=dut target=eng')) "the header survives a delete too"
Check ($out2.Contains('# added by hand after review')) "and so does every comment"

# ---- 5. what it refuses to throw away -----------------------------------
# A line with no target matches nothing, but it is not the editor's to discard:
# somebody typed it, and a half-finished entry is a note to themselves.
$path3 = Write-Glossary 'half.txt' @('#! source=dut target=eng', 'alleen bron', 'goed	good')
$doc3 = $load.Invoke($null, [object[]]@([string]$path3))
$out3 = $docT.GetMethod('Render', $Inst).Invoke($doc3, @())
Check ($out3.Contains('alleen bron')) "a line with no target is kept as written"

# ---- 6. an empty or missing file is not an error ------------------------
$doc4 = $load.Invoke($null, [object[]]@([string](Join-Path $dir 'does-not-exist.txt')))
Check ($doc4.Count -eq 0) "a missing file loads as an empty glossary"

$path5 = Write-Glossary 'empty.txt' @()
$doc5 = $load.Invoke($null, [object[]]@([string]$path5))
Check ($doc5.Count -eq 0) "so does an empty one"

SetEntries $doc5 @((MakeItem 'eerste' 'first' $false))
$out5 = $docT.GetMethod('Render', $Inst).Invoke($doc5, @())
Check ($out5.Trim() -eq "eerste`tfirst") "and a term can be added to it: '$($out5.Trim())'"

Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host "GLOSSARY DOCUMENT TEST COMPLETE - $fails failure(s)"
