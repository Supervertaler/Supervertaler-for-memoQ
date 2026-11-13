# A document closed in memoQ leaves the live view.
#
# The plugin kept every paragraph memoQ ever sent, for the whole memoQ session:
# a closed document stayed in get_project's liveDocuments, was checked by the
# QA tools, and could be the document a tool used when none was named. memoQ
# has no "document closed" event. What it does send is its list of part ids,
# which holds exactly what is open (measured in memoQ 12.6.28: 137 ids with two
# documents open, 15 after one tab was closed). The plugin now keeps only that.
#
# Run through tools\run-harness.ps1. memoQ must be closed. The documents and
# the data folder are this test's own; the names are invented. ASCII only.
. (Join-Path $PSScriptRoot 'bridge-test-common.ps1')

$activeType = PluginType 'Supervertaler.MemoQ.Core.PreviewStore+Active'
$guidA = [Guid]'11111111-aaaa-4bbb-8ccc-0123456789ab'
$guidB = [Guid]'22222222-aaaa-4bbb-8ccc-0123456789ab'
$guidC = [Guid]'33333333-aaaa-4bbb-8ccc-0123456789ab'
$nameA = 'Acme PROJ-001 contract.docx'
$nameB = 'Acme PROJ-002 manual.docx'
$nameC = 'Acme PROJ-003 letter.docx'

function Ids($view, [int]$n) { @(1..$n | ForEach-Object { "mQ-default-$view-$_" }) }
function PartsOf($view, [Guid]$g, $name, [int]$n) {
    @(1..$n | ForEach-Object { NewPart "mQ-default-$view-$_" $g $name "Paragrafo $_ del documento $view, costa 12 euro." "Paragraph $_, it costs 21 euros." })
}
function KeepOnly([string[]]$ids) {
    $a = [object[]]::new(1); $a[0] = $ids
    return $previewType.GetMethod('KeepOnly', $Static).Invoke($null, $a)
}
function Docs { return @($previewType.GetMethod('Documents', $Static).Invoke($null, $null)) }
function DocGuids { return @(Docs | ForEach-Object { $_.DocumentGuid }) }
function CountOf([Guid]$g) { $a = [object[]]::new(1); $a[0] = $g; return $previewType.GetMethod('Count', $Static).Invoke($null, $a) }
function CursorIn([string]$partId) {
    if (-not $partId) { $x = $null } else { $x = [Activator]::CreateInstance($activeType, $true); $activeType.GetField('PartId').SetValue($x, $partId) }
    $a = [object[]]::new(1); $a[0] = $x
    $previewType.GetMethod('SetActive', $Static).Invoke($null, $a) | Out-Null
}
function Cursor { return $previewType.GetMethod('GetActive', $Static).Invoke($null, $null) }
function TwoOpen {
    ClearLive
    Upsert (PartsOf 'aaaa' $guidA $nameA 3)
    Start-Sleep -Milliseconds 30
    Upsert (PartsOf 'bbbb' $guidB $nameB 5)
    ToolConnected $true
}
function PostIds([string]$json) { return Call 'HandlePreviewIds' 'POST' '/v1/preview/ids' $json }
function IdsJson([string[]]$ids) { return '{"partIds":' + (ConvertTo-Json @($ids) -Compress) + '}' }

Write-Host '--- a closed document goes'
TwoOpen
Check 'both listed: nothing dropped' ((KeepOnly ((Ids 'aaaa' 3) + (Ids 'bbbb' 5))) -eq 0 -and (Docs).Count -eq 2)
$dropped = KeepOnly (Ids 'aaaa' 3)
Check 'B closed: its five paragraphs are dropped' ($dropped -eq 5 -and (CountOf $guidB) -eq 0)
Check 'A is untouched' ((CountOf $guidA) -eq 3 -and (DocGuids) -contains $guidA -and (Docs).Count -eq 1)
$r = Call 'HandleQaCheck' 'GET' ('/v1/qa-check?type=numbers&document=' + (Esc $nameB))
Check 'check_numbers on the closed document: refused, and only A is named as open' ($r.Status -ne 200 -and $r.Body.Contains('No document matches') -and $r.Body.Contains($nameA) -and -not $r.Body.Contains('PROJ-002 manual.docx" (')) $r.Body
$r = Call 'HandleQaCheck' 'GET' '/v1/qa-check?type=numbers'
Check 'no document named: the open one is checked' ($r.Status -eq 200 -and $r.Body.Contains('PROJ-001') -and $r.Body.Contains('"checked":3')) $r.Body
$r = Call 'HandleStagedVerify' 'GET' '/v1/staged/verify'
Check 'compare_staged_to_grid counts the open document only' ($r.Status -eq 200 -and $r.Body.Contains('"rows":3') -and -not $r.Body.Contains('PROJ-002')) $r.Body

Write-Host '--- lists that are not the whole truth'
TwoOpen
$dropped = KeepOnly @('mQ-default-aaaa-2', 'mQ-default-bbbb-9')
Check 'a list naming one paragraph of each: both documents stay whole' ($dropped -eq 0 -and (CountOf $guidA) -eq 3 -and (CountOf $guidB) -eq 5)
$dropped = KeepOnly @('', $null, 'mQ-default-aaaa-1')
Check 'empty ids in a list are ignored, the rest is believed' ($dropped -eq 5 -and (CountOf $guidA) -eq 3)
TwoOpen
Check 'no list at all (null): nothing is dropped' ((KeepOnly $null) -eq 0 -and (Docs).Count -eq 2)
$dropped = KeepOnly @()
Check 'an empty list: no document is open, everything goes' ($dropped -eq 8 -and (Docs).Count -eq 0)
$r = Call 'HandleQaCheck' 'GET' '/v1/qa-check?type=numbers'
Check 'and check_numbers then says none has been seen' ($r.Status -ne 200 -and $r.Body.Contains('No document has been seen yet')) $r.Body

Write-Host '--- the list as the preview tool posts it'
TwoOpen
$r = PostIds 'not json at all'
Check 'a garbled body: accepted, and nothing is dropped' ($r.Status -eq 200 -and (Docs).Count -eq 2)
$r = PostIds '{"partIds":null}'
Check 'partIds null: nothing is dropped' ($r.Status -eq 200 -and (Docs).Count -eq 2)
$r = PostIds '{}'
Check 'no partIds: nothing is dropped' ($r.Status -eq 200 -and (Docs).Count -eq 2)
$r = PostIds (IdsJson (Ids 'bbbb' 5))
Check 'a real list without A: A goes' ($r.Status -eq 200 -and (DocGuids) -notcontains $guidA -and (CountOf $guidB) -eq 5)
$r = PostIds '{"partIds":[]}'
Check 'a real empty list: everything goes' ($r.Status -eq 200 -and (Docs).Count -eq 0)

Write-Host '--- the cursor'
TwoOpen
CursorIn 'mQ-default-bbbb-2'
[void](KeepOnly (Ids 'aaaa' 3))
Check 'the cursor was in the document that closed: it is nowhere now' ($null -eq (Cursor))
TwoOpen
CursorIn 'mQ-default-aaaa-2'
[void](KeepOnly (Ids 'aaaa' 3))
Check 'the cursor was in the document that stayed: it is kept' ($null -ne (Cursor) -and (Cursor).PartId -eq 'mQ-default-aaaa-2')

Write-Host '--- a paragraph of a closed document that arrives late'
TwoOpen
[void](KeepOnly (Ids 'aaaa' 3))
Upsert @((NewPart 'mQ-default-bbbb-4' $guidB $nameB 'In ritardo.' 'Late.'))
Check 'it is stored (memoQ sent it)' ((CountOf $guidB) -eq 1)
Check 'and goes with the next list' ((KeepOnly (Ids 'aaaa' 3)) -eq 1 -and (CountOf $guidB) -eq 0)
# A document opened since the last list: its paragraphs arrive before any list names it.
Upsert (PartsOf 'cccc' $guidC $nameC 2)
Check 'a newly opened document is there before a list names it' ((CountOf $guidC) -eq 2)
Check 'and stays once the list does' ((KeepOnly ((Ids 'aaaa' 3) + (Ids 'cccc' 2))) -eq 0 -and (CountOf $guidC) -eq 2)

Write-Host '--- which document a tool uses when none is named'
TwoOpen     # B was updated last
Check 'memoQ has not said where the cursor is: the most recently updated' ((Docs)[0].DocumentGuid -eq $guidB)
CursorIn 'mQ-default-aaaa-1'
Check 'the cursor is in A: A, though B was updated later' ((Docs)[0].DocumentGuid -eq $guidA)
$r = Call 'HandleQaCheck' 'GET' '/v1/qa-check?type=numbers'
Check 'check_numbers with no document named checks A' ($r.Status -eq 200 -and $r.Body.Contains('PROJ-001') -and $r.Body.Contains('"checked":3')) $r.Body
Upsert (PartsOf 'bbbb' $guidB $nameB 5)     # the 45-second re-pull ends with B
Check 'a re-pull of B does not move the default off the cursor' ((Docs)[0].DocumentGuid -eq $guidA)
CursorIn 'mQ-default-bbbb-3'
Check 'the cursor moves to B: B' ((Docs)[0].DocumentGuid -eq $guidB)
CursorIn 'mQ-default-zzzz-1'
Check 'a cursor in a paragraph nobody has: back to most recent, no error' ((Docs)[0].DocumentGuid -eq $guidB)
$activeDoc = $previewType.GetMethod('ActiveDocument', $Static)
Check 'and no active document is claimed' ($null -eq $activeDoc.Invoke($null, $null))

Write-Host '--- AutoPrompt with no document chosen'
$resolve = $bridgeType.GetMethod('ResolveAutoPromptSource', $Instance)
function SourcesOf($key) { $s = $resolve.Invoke($bridge, [object[]]@([string]$key)); return @($s.GetType().GetField('Sources').GetValue($s)) }
TwoOpen     # A has 3 paragraphs, B has 5
CursorIn 'mQ-default-aaaa-1'
Check 'the document the cursor is in, not the larger one' ((SourcesOf '').Count -eq 3)
CursorIn $null
Check 'cursor unknown: the larger one, as before' ((SourcesOf '').Count -eq 5)
[void](KeepOnly (Ids 'aaaa' 3))
Check 'the larger one closed: the open one, never the closed one' ((SourcesOf '').Count -eq 3)

Write-Host '--- 100 times the size, and the limit'
# The store holds at most 50,000 paragraphs. One document fills it; before this
# change it stayed full after that document closed, and every later document
# was silently left out.
ClearLive
$full = New-Object System.Collections.Generic.List[object]
foreach ($i in 1..50000) { $full.Add((NewPart "mQ-default-big-$i" $guidA $nameA "Paragrafo $i." 't')) }
Upsert $full
Upsert (PartsOf 'bbbb' $guidB $nameB 5)
Check 'full: a second document does not fit (the limit, unchanged)' ((CountOf $guidA) -eq 50000 -and (CountOf $guidB) -eq 0)
$sw = [Diagnostics.Stopwatch]::StartNew()
$dropped = KeepOnly (Ids 'bbbb' 5)
$sw.Stop()
Check "the big document closes: 50,000 paragraphs dropped in $($sw.ElapsedMilliseconds) ms" ($dropped -eq 50000 -and $sw.ElapsedMilliseconds -lt 2000)
Upsert (PartsOf 'bbbb' $guidB $nameB 5)
Check 'and the next document fits' ((CountOf $guidB) -eq 5)
Upsert $full
$allIds = @(1..50000 | ForEach-Object { "mQ-default-big-$_" }) + (Ids 'bbbb' 5)
$sw = [Diagnostics.Stopwatch]::StartNew()
$dropped = KeepOnly $allIds
$sw.Stop()
Check "a 50,005-id list that drops nothing in $($sw.ElapsedMilliseconds) ms" ($dropped -eq 0 -and $sw.ElapsedMilliseconds -lt 2000)

ClearLive
Finish
