# Remove MT marks from a Trados return package (the editor's memoQ menu) must take
# out every machine-translation origin and nothing else, and must hand Trados back
# a package it accepts.
#
# memoQ writes origin="mt" on a row filled by MT (Trados shows AT), and on a row
# edited afterwards origin="interactive" with <sdl:prev-origin origin="mt"> inside
# (a faded AT). Cleaning in memoQ and exporting again changes exactly those two
# things; this checks the direct clean does the same, byte for byte, on a real
# return package.
#
# The fixtures are a made-up two-file test package ("Acme PROJ-001", Dutch kettle
# and bicycle sentences), exported from memoQ 12.6 on 2026-11-23 and anonymised:
#   trados-return-with-mt.sdlrpx           the return package as memoQ made it
#   trados-return-cleaned-by-memoq.sdlrpx  the same after Remove MT marks, removing
#                                          the package and importing the clean files
# Trados accepted the second with no AT on any row; so did it a copy cleaned
# directly by the prototype of this code.
#
# Run through tools\run-harness.ps1. Every case is built in a temp folder.
$ErrorActionPreference = 'Stop'
$EditorExe = Join-Path $PSScriptRoot '..\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe'
$WithMt = Join-Path $PSScriptRoot 'fixtures\trados-return-with-mt.sdlrpx'
$ByMemoQ = Join-Path $PSScriptRoot 'fixtures\trados-return-cleaned-by-memoq.sdlrpx'

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Windows.Forms

$editor = [Reflection.Assembly]::LoadFrom((Resolve-Path $EditorExe))
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'
$marksT = $editor.GetType('Supervertaler.PromptEditor.TradosMtMarks')
$resultT = $editor.GetType('Supervertaler.PromptEditor.MtMarks').GetNestedType('Result', [Reflection.BindingFlags]'Public,NonPublic')
$cleanM = $marksT.GetMethod('Clean', $Static)
$cleanTextM = $marksT.GetMethod('CleanText', $Static)
$formT = $editor.GetType('Supervertaler.PromptEditor.MainForm')
$reportM = $formT.GetMethod('TradosMtMarksReport', $Static)

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}
function Field($o, $name) { return $o.GetType().GetField($name).GetValue($o) }
function Unwrap($e) {
    while ($e.InnerException -and ($e -is [Management.Automation.MethodInvocationException] -or
            $e -is [Reflection.TargetInvocationException])) { $e = $e.InnerException }
    return $e
}
function Clean($path) {
    try { return @{ Result = $cleanM.Invoke($null, [object[]]@([string]$path)); Error = $null } }
    catch { return @{ Result = $null; Error = (Unwrap $_.Exception) } }
}
function CleanText($text) {
    $r = [Activator]::CreateInstance($resultT)
    $out = $cleanTextM.Invoke($null, [object[]]@([string]$text, $r))
    return @{ Text = $out; Result = $r }
}
function Same([byte[]]$a, [byte[]]$b) {
    if ($a.Length -ne $b.Length) { return $false }
    for ($i = 0; $i -lt $a.Length; $i++) { if ($a[$i] -ne $b[$i]) { return $false } }
    return $true
}
function Entries($path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($path)
    try {
        $map = [ordered]@{}
        foreach ($e in $zip.Entries) {
            $s = $e.Open(); $m = New-Object IO.MemoryStream; $s.CopyTo($m); $s.Dispose()
            $map[$e.FullName] = $m.ToArray()
        }
        return $map
    } finally { $zip.Dispose() }
}

$dir = Join-Path $env:TEMP 'sv-trados-mtmarks-test'
if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)

# ---- 1. segment definitions, one case at a time --------------------------------
Write-Host "-- segment definitions"
$cases = @(
    @{ Label = 'MT, never edited: origin becomes interactive';
       In = '<sdl:seg id="2" conf="Translated" origin="mt" />';
       Out = '<sdl:seg id="2" conf="Translated" origin="interactive" />'; Removed = 1 },
    @{ Label = 'edited after MT: the MT earlier origin goes, and the seg closes itself as memoQ writes it';
       In = "<sdl:seg id=`"1`" conf=`"Translated`" origin=`"interactive`"><sdl:prev-origin origin=`"mt`" origin-system=`"MT / Supervertaler`" percent=`"0`" /></sdl:seg>";
       Out = '<sdl:seg id="1" conf="Translated" origin="interactive" />'; Removed = 1 },
    @{ Label = 'neural MT from Trados itself: engine and percentage go with the origin';
       In = '<sdl:seg id="3" conf="Draft" origin="nmt" origin-system="Language Weaver" percent="0" />';
       Out = '<sdl:seg id="3" conf="Draft" origin="interactive" />'; Removed = 1 },
    @{ Label = 'adaptive MT counts as MT too';
       In = '<sdl:seg id="4" conf="Translated" origin="amt" origin-system="AdaptiveMT" />';
       Out = '<sdl:seg id="4" conf="Translated" origin="interactive" />'; Removed = 1 },
    @{ Label = 'a TM match is not touched';
       In = '<sdl:seg id="5" conf="Translated" origin="tm" origin-system="Client TM" percent="100" />';
       Out = '<sdl:seg id="5" conf="Translated" origin="tm" origin-system="Client TM" percent="100" />'; Removed = 0 },
    @{ Label = 'typed by hand, with a non-MT earlier origin: not touched';
       In = '<sdl:seg id="6" conf="Translated" origin="interactive"><sdl:prev-origin origin="tm" percent="85" /></sdl:seg>';
       Out = '<sdl:seg id="6" conf="Translated" origin="interactive"><sdl:prev-origin origin="tm" percent="85" /></sdl:seg>'; Removed = 0 },
    @{ Label = 'an MT earlier origin that replaced a TM fuzzy: the MT link goes, the TM one moves up and stays';
       In = '<sdl:seg id="7" conf="Translated" origin="interactive"><sdl:prev-origin origin="mt" origin-system="DeepL"><sdl:prev-origin origin="tm" percent="75" /></sdl:prev-origin></sdl:seg>';
       Out = '<sdl:seg id="7" conf="Translated" origin="interactive"><sdl:prev-origin origin="tm" percent="75" /></sdl:seg>'; Removed = 1 },
    @{ Label = 'a TM origin with MT further back in its chain: only the MT goes';
       In = '<sdl:seg id="8" conf="Translated" origin="tm" percent="100"><sdl:prev-origin origin="interactive"><sdl:prev-origin origin="mt" /></sdl:prev-origin></sdl:seg>';
       Out = '<sdl:seg id="8" conf="Translated" origin="tm" percent="100"><sdl:prev-origin origin="interactive"></sdl:prev-origin></sdl:seg>'; Removed = 1 },
    @{ Label = 'MT now and MT before: both records go';
       In = '<sdl:seg id="9" conf="Draft" origin="mt"><sdl:prev-origin origin="nmt" origin-system="Google" /></sdl:seg>';
       Out = '<sdl:seg id="9" conf="Draft" origin="interactive" />'; Removed = 2 },
    @{ Label = 'other content in the seg (a value) stays where it is';
       In = '<sdl:seg id="10" conf="Translated" origin="mt"><sdl:value key="SegmentIdentityHash">abc</sdl:value></sdl:seg>';
       Out = '<sdl:seg id="10" conf="Translated" origin="interactive"><sdl:value key="SegmentIdentityHash">abc</sdl:value></sdl:seg>'; Removed = 1 }
)
foreach ($c in $cases) {
    $r = CleanText $c.In
    $removed = Field $r.Result 'Removed'
    Check ($r.Text -eq $c.Out -and $removed -eq $c.Removed) "$($c.Label)$(if ($r.Text -ne $c.Out) { ": got $($r.Text)" })"
}

# The prototype's bug: a self-closing seg followed by one with content must not be
# read as one element running from the first to the second's closing tag.
$two = '<sdl:seg-defs><sdl:seg id="1" conf="Translated" origin="tm" percent="100" /><sdl:seg id="2" conf="Translated" origin="interactive"><sdl:prev-origin origin="mt" /></sdl:seg></sdl:seg-defs>'
$r = CleanText $two
Check ($r.Text -eq '<sdl:seg-defs><sdl:seg id="1" conf="Translated" origin="tm" percent="100" /><sdl:seg id="2" conf="Translated" origin="interactive" /></sdl:seg-defs>' -and
       (Field $r.Result 'Removed') -eq 1) "a self-closing seg does not swallow the next one: $($r.Text)"

$r = CleanText '<sdl:seg id="1" conf="Translated" origin="mt" /><sdl:seg id="2" origin="interactive"><sdl:prev-origin origin="mt" origin-system="MT / Supervertaler" /></sdl:seg>'
$engines = Field $r.Result 'Engines'
Check ($engines['machine translation'] -eq 1 -and $engines['MT / Supervertaler'] -eq 1) "engines are counted, an unnamed one as 'machine translation'"

# ---- 2. the real return package -------------------------------------------------
Write-Host "-- the real return package"
$pkg = Join-Path $dir 'Acme PROJ-001 MT package test.sdlrpx'
Copy-Item $WithMt $pkg
$before = [IO.File]::ReadAllBytes($pkg)
$r = Clean $pkg
Check ($r.Error -eq $null) "cleans without error $(if ($r.Error) { $r.Error.Message })"
$written = Field $r.Result 'WrittenTo'
Check ($written -eq (Join-Path $dir 'Clean\Acme PROJ-001 MT package test.sdlrpx')) "the copy goes into a Clean folder beside it, same name: $written"
Check ((Field $r.Result 'Removed') -eq 12) "12 MT records removed (5 + 1 per document): $(Field $r.Result 'Removed')"
Check (Same ([IO.File]::ReadAllBytes($pkg)) $before) "the original package is not changed"

$out = Entries $written
$orig = Entries $WithMt
$memoq = Entries $ByMemoQ
Check ((@($out.Keys) -join '|') -eq (@($orig.Keys) -join '|')) "same entries, in the same order ($($out.Count))"
foreach ($name in @($out.Keys | Where-Object { $_ -like '*.sdlxliff' })) {
    Check (Same $out[$name] $memoq[$name]) "$name is byte for byte what memoQ's own clean produced"
}
$untouched = @($out.Keys | Where-Object { $_ -notlike '*.sdlxliff' -and $_ -notlike '*.sdlproj' })
Check (@($untouched | Where-Object { -not (Same $out[$_] $orig[$_]) }).Count -eq 0) "the other $($untouched.Count) entries are copied byte for byte"

$proj = $utf8.GetString($out['Acme PROJ-001 MT package test.sdlproj'])
foreach ($name in @($out.Keys | Where-Object { $_ -like '*.sdlxliff' })) {
    $physical = $name.Replace('/', '\')
    $m = [regex]::Match($proj, '<FileVersion [^>]*?Size="(\d+)"[^>]*?PhysicalPath="' + [regex]::Escape($physical) + '"')
    Check ($m.Success -and [int]$m.Groups[1].Value -eq $out[$name].Length) ".sdlproj records $name's new size ($($out[$name].Length))"
}
$projBefore = $utf8.GetString($orig['Acme PROJ-001 MT package test.sdlproj'])
$sizeless = { param($t) [regex]::Replace($t, '(<FileVersion [^>]*?\sSize=")\d+', '$1') }
Check ((& $sizeless $proj) -eq (& $sizeless $projBefore)) "nothing else in the .sdlproj changes"

$text = ($out.Keys | Where-Object { $_ -like '*.sdlxliff' } | ForEach-Object { $utf8.GetString($out[$_]) }) -join ''
Check ($text -notmatch 'origin="(mt|nmt|amt)"' -and $text -notmatch 'Supervertaler') "no MT origin and no engine name left in any .sdlxliff"

# Cleaning the clean copy again finds nothing and writes nothing.
$again = Join-Path $dir 'again.sdlrpx'
Copy-Item $written $again
$r = Clean $again
Check ($r.Error -eq $null -and (Field $r.Result 'Removed') -eq 0 -and (Field $r.Result 'WrittenTo') -eq $null) "a package with no marks: nothing removed, nothing written"

# ---- 3. a single .sdlxliff --------------------------------------------------------
Write-Host "-- a single .sdlxliff"
$x = Join-Path $dir 'Pakket-test A.docx.sdlxliff'
[IO.File]::WriteAllBytes($x, $orig['en-GB/Pakket-test A.docx.sdlxliff'])
$r = Clean $x
Check ($r.Error -eq $null -and (Field $r.Result 'Removed') -eq 6) "6 records removed from the file on its own"
Check (Same ([IO.File]::ReadAllBytes((Field $r.Result 'WrittenTo'))) $memoq['en-GB/Pakket-test A.docx.sdlxliff']) "and the copy matches memoQ's clean"

# ---- 4. what is refused ------------------------------------------------------------
Write-Host "-- refusals"
$notZip = Join-Path $dir 'notzip.sdlrpx'
[IO.File]::WriteAllText($notZip, 'hello')
$r = Clean $notZip
Check ($r.Error -is [IO.InvalidDataException]) "a .sdlrpx that is not a zip is refused: $($r.Error.Message)"

$noProj = Join-Path $dir 'noproj.sdlrpx'
$z = [IO.Compression.ZipFile]::Open($noProj, 'Create')
$e = $z.CreateEntry('en-GB/a.sdlxliff'); $s = $e.Open(); $b = $orig['en-GB/Pakket-test A.docx.sdlxliff']; $s.Write($b, 0, $b.Length); $s.Dispose(); $z.Dispose()
$r = Clean $noProj
Check ($r.Error -is [IO.InvalidDataException] -and -not (Test-Path (Join-Path $dir 'Clean\noproj.sdlrpx'))) "a zip without a .sdlproj is refused and nothing is written: $($r.Error.Message)"

$damaged = Join-Path $dir 'damaged.sdlrpx'
$bytes = [IO.File]::ReadAllBytes($WithMt); [Array]::Resize([ref]$bytes, 5000)
[IO.File]::WriteAllBytes($damaged, $bytes)
$r = Clean $damaged
Check ($r.Error -is [IO.InvalidDataException]) "a cut-off package is refused: $($r.Error.Message)"

$notSdl = Join-Path $dir 'other.sdlxliff'
[IO.File]::WriteAllText($notSdl, '<xliff><file><body><sdl:seg id="1" origin="mt" /></body></file></xliff>')
$r = Clean $notSdl
Check ($r.Error -is [IO.InvalidDataException]) "a file without the SDL XLIFF namespace is refused: $($r.Error.Message)"

$badUtf = Join-Path $dir 'bad.sdlxliff'
[IO.File]::WriteAllBytes($badUtf, [byte[]](0x3C, 0x61, 0xFF, 0xFE, 0x3E))
$r = Clean $badUtf
Check ($r.Error -is [IO.InvalidDataException]) "a file that is not UTF-8 is refused: $($r.Error.Message)"

# The copy's name is taken by a read-only file: the run fails, says why, and the
# original is untouched.
$blocked = Join-Path $dir 'blocked.sdlrpx'
Copy-Item $WithMt $blocked
$target = Join-Path $dir 'Clean\blocked.sdlrpx'
[IO.File]::WriteAllText($target, 'x'); (Get-Item $target).IsReadOnly = $true
$r = Clean $blocked
Check ($r.Error -is [IO.IOException] -and (Same ([IO.File]::ReadAllBytes($blocked)) ([IO.File]::ReadAllBytes($WithMt)))) "an unwritable copy fails with a reason, original untouched: $($r.Error.Message)"
(Get-Item $target).IsReadOnly = $false

# ---- 5. the report -------------------------------------------------------------------
Write-Host "-- the report"
$tupleT = [Type]::GetType('System.ValueTuple`3, mscorlib').MakeGenericType(@([string], $resultT, [string]))
$list = [Activator]::CreateInstance([Collections.Generic.List`1].MakeGenericType(@($tupleT)))
$ok = (Clean $pkg).Result
$list.Add([Activator]::CreateInstance($tupleT, [object[]]@([string]$pkg, $ok, $null)))
$list.Add([Activator]::CreateInstance($tupleT, [object[]]@([string]$notZip, $null, 'not a zip file.')))
$report = $reportM.Invoke($null, [object[]]@(,$list))
Write-Host $report
Check ($report -like '*Acme PROJ-001 MT package test.sdlrpx: 12 MT marks removed.*') "the report says what was removed from which file"
Check ($report -like "*$dir\Clean*") "the report gives the Clean folder as a full path"
Check ($report -like '*notzip.sdlrpx: not cleaned. not a zip file.*') "a failed file is reported with the others"
Check ($report -like '*Deliver the clean copy*') "the report says what to do with the copy"

# ---- 6. scale ----------------------------------------------------------------------------
# A long document: 20,000 segment definitions, a third with MT.
Write-Host "-- scale"
$head = $utf8.GetString($orig['en-GB/Pakket-test A.docx.sdlxliff'])
$sb = New-Object Text.StringBuilder
for ($i = 0; $i -lt 20000; $i++) {
    switch ($i % 3) {
        0 { [void]$sb.Append("<sdl:seg id=`"$i`" conf=`"Translated`" origin=`"mt`" />") }
        1 { [void]$sb.Append("<sdl:seg id=`"$i`" conf=`"Translated`" origin=`"tm`" percent=`"100`" />") }
        2 { [void]$sb.Append("<sdl:seg id=`"$i`" conf=`"Translated`" origin=`"interactive`"><sdl:prev-origin origin=`"tm`" percent=`"80`" /></sdl:seg>") }
    }
}
$big = Join-Path $dir 'big.sdlxliff'
$once = New-Object Text.RegularExpressions.Regex('</sdl:seg-defs>')
[IO.File]::WriteAllText($big, $once.Replace($head, $sb.ToString() + '</sdl:seg-defs>', 1), $utf8)
$mb = [math]::Round((Get-Item $big).Length / 1MB, 1)
$sw = [Diagnostics.Stopwatch]::StartNew()
$r = Clean $big
$sw.Stop()
Check ($r.Error -eq $null -and (Field $r.Result 'Removed') -eq (6667 + 6)) "20,000 extra segments ($mb MB): $(Field $r.Result 'Removed') records removed"
Check ($sw.Elapsed.TotalSeconds -lt 10) "in $([math]::Round($sw.Elapsed.TotalSeconds, 2)) s"

Remove-Item -Recurse -Force $dir
Write-Host ""
if ($fails -eq 0) { Write-Host "ALL PASSED" } else { Write-Host "$fails FAILED" }
exit $fails
