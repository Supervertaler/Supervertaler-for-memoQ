# Every tool that takes a document accepts every spelling get_project hands out.
#
# get_project gives the live view's documentGuid (8-4-4-4-12) and the capture's
# key (32 hex digits, an underscore, the language pair). get_segments took
# both; the QA checks refused the key with "No document has been seen yet",
# compare_staged_to_grid ignored a key it did not know and reported on every
# document, and get_confirmed_pairs answered a dashed GUID with nothing. One
# resolver serves them all now, and a value that names no document is an error
# that says so.
#
# Run through tools\run-harness.ps1. memoQ must be closed. The documents, the
# captures and the data folder are this test's own; the names are invented.
# ASCII only.
. (Join-Path $PSScriptRoot 'bridge-test-common.ps1')

$keys        = PluginType 'Supervertaler.MemoQ.Core.DocumentKeys'
$captureType = PluginType 'Supervertaler.MemoQ.Core.CaptureStore'
$capDocType  = PluginType 'Supervertaler.MemoQ.Core.CaptureStore+DocumentCapture'

$guidA = [Guid]'11111111-aaaa-4bbb-8ccc-0123456789ab'
$guidB = [Guid]'22222222-aaaa-4bbb-8ccc-0123456789ab'
$guidC = [Guid]'33333333-aaaa-4bbb-8ccc-0123456789ab'     # captured, not open
$guidNone = [Guid]'99999999-aaaa-4bbb-8ccc-0123456789ab'  # nowhere
$nameA = 'Acme PROJ-001 contract.docx'
$nameB = 'Acme PROJ-002 manual.docx'
$pair = 'ita-IT-eng-GB'
function KeyOf([Guid]$g) { $g.ToString('N') + '_' + $pair }

# ---- the resolver on its own ------------------------------------------------
$guidOf = $keys.GetMethod('GuidOf', $Static)
function GuidOf($key) { $a = [object[]]::new(1); $a[0] = $key; return $guidOf.Invoke($null, $a) }
function CaptureList($captures) {
    $list = [Activator]::CreateInstance([System.Collections.Generic.List`1].MakeGenericType($capDocType))
    foreach ($c in $captures) { $list.Add($c) }
    return ,$list
}
# The live view's own row reader, as the bridge passes it.
$rowsType = [Func`2].MakeGenericType([Guid], [System.Collections.Generic.IList`1].MakeGenericType($partType))
$rowsOf = [Delegate]::CreateDelegate($rowsType, $previewType.GetMethod('Rows', $Static))
function Live($key, $parts, $captures = @()) {
    $a = [object[]]::new(5); $a[0] = $key; $a[1] = (PartList $parts).PSObject.BaseObject
    $a[2] = (CaptureList $captures).PSObject.BaseObject; $a[3] = $rowsOf; $a[4] = $null
    $doc = $keys.GetMethod('Live', $Static).Invoke($null, $a)
    return [pscustomobject]@{ Doc = $doc; Problem = $a[4] }
}
function NewCapture([string]$key, [Guid]$doc) {
    $c = [Activator]::CreateInstance($capDocType, $true)
    $capDocType.GetField('Key').SetValue($c, $key)
    $capDocType.GetField('DocumentId').SetValue($c, $doc)
    $capDocType.GetField('SourceLangCode').SetValue($c, 'ita-IT')
    $capDocType.GetField('TargetLangCode').SetValue($c, 'eng-GB')
    $capDocType.GetField('LastSeenUtc').SetValue($c, [DateTime]::UtcNow)
    return $c
}
function Captured($key, $captures, $parts) {
    $a = [object[]]::new(5); $a[0] = $key; $a[1] = (CaptureList $captures).PSObject.BaseObject; $a[2] = (PartList $parts).PSObject.BaseObject
    $a[3] = $rowsOf; $a[4] = $null
    $doc = $keys.GetMethod('Captured', $Static).Invoke($null, $a)
    return [pscustomobject]@{ Doc = $doc; Problem = $a[4] }
}
function IsDoc($r, [Guid]$g) { $null -ne $r.Doc -and $r.Doc.DocumentGuid -eq $g -and $null -eq $r.Problem }

Write-Host '--- reading a GUID out of a key'
Check 'dashed GUID'                      ((GuidOf $guidA.ToString('D')) -eq $guidA)
Check '32 hex digits'                    ((GuidOf $guidA.ToString('N')) -eq $guidA)
Check '32 hex digits and language pair'  ((GuidOf (KeyOf $guidA)) -eq $guidA)
Check 'in capitals'                      ((GuidOf (KeyOf $guidA).ToUpperInvariant()) -eq $guidA)
Check 'with spaces round it'             ((GuidOf ('  ' + $guidA.ToString('D') + ' ')) -eq $guidA)
Check 'in braces'                        ((GuidOf $guidA.ToString('B')) -eq $guidA)
foreach ($bad in @($null, '', '   ', $nameA, 'visited_ita-IT-eng-GB', 'nodoc_ita-IT-eng-GB',
                   ($guidA.ToString('N').Substring(0, 31) + '_' + $pair),          # one digit short
                   ($guidA.ToString('N') + '_'),                                   # nothing after the underscore
                   ('zz' + $guidA.ToString('N').Substring(2) + '_' + $pair),       # not hex
                   ($guidA.ToString('N') + 'x' + $pair))) {                        # no underscore
    Check "not a GUID: '$bad'" ($null -eq (GuidOf $bad))
}

$a1 = NewPart 'mQ-default-aaaa-1' $guidA $nameA 'Costa 12 euro.' 'It costs 21 euros.'
$b1 = NewPart 'mQ-default-bbbb-1' $guidB $nameB 'Costa 12 euro.' 'It costs 12 euros.'
$open = @($b1, $a1)     # B first: the one a call without a document gets

Write-Host '--- an open document, every spelling'
Check 'no document named: the first of the list'  (IsDoc (Live $null $open) $guidB)
Check 'empty: the same'                           (IsDoc (Live '' $open) $guidB)
Check 'only spaces: the same'                     (IsDoc (Live '   ' $open) $guidB)
Check 'dashed GUID'                               (IsDoc (Live $guidA.ToString('D') $open) $guidA)
Check '32 hex digits'                             (IsDoc (Live $guidA.ToString('N') $open) $guidA)
Check 'key with language pair'                    (IsDoc (Live (KeyOf $guidA) $open) $guidA)
Check 'key in capitals'                           (IsDoc (Live (KeyOf $guidA).ToUpperInvariant() $open) $guidA)
Check 'name'                                      (IsDoc (Live $nameA $open) $guidA)
Check 'name in capitals'                          (IsDoc (Live $nameA.ToUpperInvariant() $open) $guidA)
Check 'name with spaces round it'                 (IsDoc (Live (' ' + $nameA + ' ') $open) $guidA)

Write-Host '--- a value that names nothing is a problem, never another document'
$r = Live (KeyOf $guidNone) $open
Check 'unknown key: no document'                  ($null -eq $r.Doc)
Check 'the problem quotes the key'                ($r.Problem -and $r.Problem.Contains((KeyOf $guidNone)))
Check 'and names the open documents'              ($r.Problem -and $r.Problem.Contains($nameA) -and $r.Problem.Contains($nameB) -and $r.Problem.Contains($guidA.ToString('D')))
Check 'and does not claim none was seen'          ($r.Problem -and -not $r.Problem.Contains('No document has been seen yet'))
$r = Live 'Acme PROJ-009 nothing.docx' $open
Check 'unknown name: no document, a problem'      ($null -eq $r.Doc -and $r.Problem -and $r.Problem.Contains('PROJ-009'))
$r = Live $null @()
Check 'nothing open, nothing named'               ($null -eq $r.Doc -and $r.Problem -eq 'No document has been seen yet. Open one in memoQ and click into a segment.')
$r = Live (KeyOf $guidA) @()
Check 'nothing open, a key named'                 ($null -eq $r.Doc -and $r.Problem.Contains('No document is open in the live view'))
$twin = NewPart 'mQ-default-cccc-1' $guidC $nameA 'x' 'y'
$r = Live $nameA @($b1, $a1, $twin)
Check 'two open documents with one name: refused' ($null -eq $r.Doc -and $r.Problem.Contains('2 open documents are named') -and $r.Problem.Contains($guidA.ToString('D')) -and $r.Problem.Contains($guidC.ToString('D')))
Check 'but its GUID still picks one of them'      (IsDoc (Live $guidC.ToString('D') @($b1, $a1, $twin)) $guidC)
$r = Live ('x' * 5000) $open
Check 'a very long value is cut in the problem'   ($null -eq $r.Doc -and $r.Problem.Length -lt 700)

Write-Host '--- 100 times the size'
$many = @(1..2000 | ForEach-Object { NewPart "mQ-default-$_-1" ([Guid]::NewGuid()) "Acme PROJ-$_ file.docx" 's' 't' }) + @($a1)
$sw = [Diagnostics.Stopwatch]::StartNew()
$hit = Live (KeyOf $guidA) $many
$miss = Live (KeyOf $guidNone) $many
$sw.Stop()
Check "2,001 open documents: found and refused in $($sw.ElapsedMilliseconds) ms" ((IsDoc $hit $guidA) -and $null -eq $miss.Doc -and $sw.ElapsedMilliseconds -lt 1000)
Check 'the problem lists ten and counts the rest' ($miss.Problem.Contains('and 1991 more') -and $miss.Problem.Length -lt 2000)

Write-Host '--- a captured document, every spelling'
$capA = NewCapture (KeyOf $guidA) $guidA
$capC = NewCapture (KeyOf $guidC) $guidC
$capV = NewCapture 'visited_ita-IT-eng-GB' ([Guid]::Empty)
$caps = @($capC, $capA, $capV)
function IsCap($r, [string]$key) { $null -ne $r.Doc -and $r.Doc.Key -eq $key -and $null -eq $r.Problem }
Check 'no document named: the first of the list'  (IsCap (Captured $null $caps $open) (KeyOf $guidC))
Check 'its own key'                               (IsCap (Captured (KeyOf $guidA) $caps $open) (KeyOf $guidA))
Check 'its own key in capitals'                   (IsCap (Captured (KeyOf $guidA).ToUpperInvariant() $caps $open) (KeyOf $guidA))
Check 'dashed GUID'                               (IsCap (Captured $guidA.ToString('D') $caps $open) (KeyOf $guidA))
Check '32 hex digits'                             (IsCap (Captured $guidA.ToString('N') $caps $open) (KeyOf $guidA))
Check 'the name of the open document'             (IsCap (Captured $nameA.ToLowerInvariant() $caps $open) (KeyOf $guidA))
Check 'a captured document that is not open'      (IsCap (Captured $guidC.ToString('D') $caps $open) (KeyOf $guidC))
Check 'the visited-rows bucket by its key'        (IsCap (Captured 'visited_ita-IT-eng-GB' $caps $open) 'visited_ita-IT-eng-GB')
$r = Captured $null @() $open
Check 'nothing captured, nothing named: nothing, and no problem' ($null -eq $r.Doc -and $null -eq $r.Problem)
$r = Captured (KeyOf $guidNone) $caps $open
Check 'unknown key: a problem listing the captured keys' ($null -eq $r.Doc -and $r.Problem.Contains((KeyOf $guidC)) -and $r.Problem.Contains('visited_ita-IT-eng-GB') -and $r.Problem.Contains($nameB))
$r = Captured $guidB.ToString('D') $caps $open
Check 'open but never captured: says exactly that, by GUID' ($null -eq $r.Doc -and $r.Problem.Contains($nameB) -and $r.Problem.Contains('is open in memoQ, but'))
$r = Captured $nameB $caps $open
Check 'open but never captured: the same by name' ($null -eq $r.Doc -and $r.Problem.Contains('is open in memoQ, but'))
$r = Captured $nameA $caps @($b1, $a1, $twin)
Check 'two open documents with one name: refused' ($null -eq $r.Doc -and $r.Problem.Contains('2 open documents are named'))
$r = Captured (KeyOf $guidNone) @() @()
Check 'nothing anywhere, a key named: a problem'  ($null -eq $r.Doc -and $r.Problem.Contains('Nothing has been captured') -and $r.Problem.Contains('No document is open'))

# ---- the tools themselves, over HTTP ----------------------------------------
# Document A has a wrong number and a repeated paragraph translated two ways.
# Document B is clean. So an answer shows which document it was about.
ClearLive
Upsert @(
    (NewPart 'mQ-default-aaaa-1' $guidA $nameA 'Costa 12 euro.' 'It costs 21 euros.'),
    (NewPart 'mQ-default-aaaa-2' $guidA $nameA 'Buongiorno.' 'Good morning.'),
    (NewPart 'mQ-default-aaaa-3' $guidA $nameA 'Buongiorno.' 'Hello.')
)
Start-Sleep -Milliseconds 30
Upsert @(
    (NewPart 'mQ-default-bbbb-1' $guidB $nameB 'Costa 12 euro.' 'It costs 12 euros.'),
    (NewPart 'mQ-default-bbbb-2' $guidB $nameB 'Arrivederci.' 'Goodbye.')
)
ToolConnected $true

# Captures, put straight into the store: A (also open) and C (not open).
$docs = $captureType.GetField('_docs', $Static).GetValue($null)
$docs.Clear()
foreach ($c in @((NewCapture (KeyOf $guidA) $guidA), (NewCapture (KeyOf $guidC) $guidC))) {
    $capDocType.GetField('Sources').GetValue($c).Add('Costa 12 euro.')
    $docs[$c.Key] = $c
}

$spellings = [ordered]@{
    'dashed GUID'            = $guidA.ToString('D')
    '32 hex digits'          = $guidA.ToString('N')
    'key from get_project'   = (KeyOf $guidA)
    'name'                   = $nameA
    'name in capitals'       = $nameA.ToUpperInvariant()
}
$unknown = KeyOf $guidNone
function Refused($r) { $r.Status -eq 404 -and $r.Body.Contains('No document matches') -and $r.Body.Contains($nameA) -and $r.Body.Contains($nameB) }

Write-Host '--- check_numbers, check_tags, check_nbsp, check_terminology'
foreach ($s in $spellings.Keys) {
    $r = Call 'HandleQaCheck' 'GET' ('/v1/qa-check?type=numbers&document=' + (Esc $spellings[$s]))
    Check "check_numbers by $s" ($r.Status -eq 200 -and $r.Body.Contains('PROJ-001') -and $r.Body.Contains('"found":1')) $r.Body
}
foreach ($type in @('tags', 'nbsp', 'terminology')) {
    $r = Call 'HandleQaCheck' 'GET' ("/v1/qa-check?type=$type&document=" + (Esc (KeyOf $guidA)))
    Check "check_$type by key from get_project" ($r.Status -eq 200 -and $r.Body.Contains('PROJ-001')) $r.Body
}
$r = Call 'HandleQaCheck' 'GET' ('/v1/qa-check?type=numbers&document=' + (Esc (KeyOf $guidB)))
Check 'and the other document is the other document' ($r.Status -eq 200 -and $r.Body.Contains('PROJ-002') -and $r.Body.Contains('"found":0')) $r.Body
$r = Call 'HandleQaCheck' 'GET' '/v1/qa-check?type=numbers'
Check 'no document named still works' ($r.Status -eq 200 -and $r.Body.Contains('PROJ-002')) $r.Body
$r = Call 'HandleQaCheck' 'GET' ('/v1/qa-check?type=numbers&document=' + (Esc $unknown))
Check 'an unknown key is refused, naming the open documents' ($r.Status -ne 200 -and $r.Body.Contains('No document matches') -and $r.Body.Contains($nameA) -and $r.Body.Contains($nameB) -and -not $r.Body.Contains('has been seen yet')) $r.Body

Write-Host '--- find_inconsistencies'
foreach ($s in $spellings.Keys) {
    $r = Call 'HandleInconsistencies' 'GET' ('/v1/inconsistencies?document=' + (Esc $spellings[$s]))
    Check "by $s" ($r.Status -eq 200 -and $r.Body.Contains('PROJ-001') -and $r.Body.Contains('"total":1')) $r.Body
}
$r = Call 'HandleInconsistencies' 'GET' ('/v1/inconsistencies?document=' + (Esc $unknown))
Check 'an unknown key is refused' ($r.Status -ne 200 -and $r.Body.Contains('No document matches')) $r.Body

Write-Host '--- get_segments'
foreach ($s in $spellings.Keys) {
    $r = Call 'HandleSegments' 'GET' ('/v1/segments?document=' + (Esc $spellings[$s]))
    Check "by $s" ($r.Status -eq 200 -and $r.Body.Contains('PROJ-001') -and $r.Body.Contains('"total":3')) $r.Body
}
$r = Call 'HandleSegments' 'GET' ('/v1/segments?document=' + (Esc $guidC.ToString('D')))
Check 'a captured document that is not open, by dashed GUID' ($r.Status -eq 200 -and $r.Body.Contains((KeyOf $guidC)) -and $r.Body.Contains('"total":1')) $r.Body
$r = Call 'HandleSegments' 'GET' ('/v1/segments?document=' + (Esc $unknown))
Check 'an unknown key is refused, not answered with 0 segments' ($r.Status -eq 404 -and $r.Body.Contains('No document matches') -and $r.Body.Contains((KeyOf $guidC))) $r.Body

Write-Host '--- compare_staged_to_grid'
foreach ($s in $spellings.Keys) {
    $r = Call 'HandleStagedVerify' 'GET' ('/v1/staged/verify?document=' + (Esc $spellings[$s]))
    Check "by ${s}: that document only" ($r.Status -eq 200 -and $r.Body.Contains('PROJ-001') -and -not $r.Body.Contains('PROJ-002') -and $r.Body.Contains('"rows":3')) $r.Body
}
$r = Call 'HandleStagedVerify' 'GET' '/v1/staged/verify'
Check 'no document named: the whole project' ($r.Status -eq 200 -and $r.Body.Contains('PROJ-001') -and $r.Body.Contains('PROJ-002') -and $r.Body.Contains('"rows":5')) $r.Body
$r = Call 'HandleStagedVerify' 'GET' ('/v1/staged/verify?document=' + (Esc $unknown))
Check 'an unknown key is refused, not answered for every document' (Refused $r) $r.Body
$r = Call 'HandleStagedVerify' 'GET' ('/v1/staged/verify?document=' + (Esc 'Acme PROJ-009 nothing.docx'))
Check 'an unknown name too' (Refused $r) $r.Body

Write-Host '--- get_confirmed_pairs'
foreach ($s in $spellings.Keys) {
    $r = Call 'HandleConfirmed' 'GET' ('/v1/confirmed?document=' + (Esc $spellings[$s]))
    Check "by $s" ($r.Status -eq 200 -and $r.Body.Contains('"documentKey":"' + (KeyOf $guidA) + '"')) $r.Body
}
$r = Call 'HandleConfirmed' 'GET' ('/v1/confirmed?document=' + (Esc $unknown))
Check 'an unknown key is refused, not answered with no pairs' ($r.Status -eq 404 -and $r.Body.Contains('No document matches')) $r.Body
$r = Call 'HandleConfirmed' 'GET' ('/v1/confirmed?document=' + (Esc $guidB.ToString('D')))
Check 'an open document nothing was captured for: says so' ($r.Status -eq 404 -and $r.Body.Contains('is open in memoQ, but')) $r.Body

Write-Host '--- go_to_segment'
function TakeCommands {
    $a = [object[]]::new(1); $a[0] = [TimeSpan]::Zero
    return @($previewType.GetMethod('TakeCommands', $Static).Invoke($null, $a))
}
[void](TakeCommands)
foreach ($s in $spellings.Keys) {
    $body = '{"index":2,"document":' + (ConvertTo-Json $spellings[$s]) + '}'
    $r = Call 'HandleGoTo' 'POST' '/v1/goto' $body
    $sent = TakeCommands
    Check "by ${s}: asks memoQ for that document's row" ($r.Status -eq 200 -and $sent.Count -eq 1 -and $sent[0].PartId -eq 'mQ-default-aaaa-2') $r.Body
}
$r = Call 'HandleGoTo' 'POST' '/v1/goto' ('{"index":1,"document":' + (ConvertTo-Json $unknown) + '}')
Check 'an unknown key is refused' (Refused $r) $r.Body
Check 'and memoQ is asked nothing' ((TakeCommands).Count -eq 0)
$r = Call 'HandleGoTo' 'POST' '/v1/goto' ('{"index":99,"document":' + (ConvertTo-Json (KeyOf $guidA)) + '}')
Check 'a row past the end is still "No such segment"' ($r.Status -eq 404 -and $r.Body.Contains('No such segment')) $r.Body

Write-Host '--- no document open'
ClearLive
$r = Call 'HandleQaCheck' 'GET' '/v1/qa-check?type=numbers'
Check 'check_numbers, nothing named: none has been seen' ($r.Status -ne 200 -and $r.Body.Contains('No document has been seen yet')) $r.Body
$r = Call 'HandleQaCheck' 'GET' ('/v1/qa-check?type=numbers&document=' + (Esc (KeyOf $guidA)))
Check 'check_numbers, a key named: none is open' ($r.Status -ne 200 -and $r.Body.Contains('No document is open in the live view')) $r.Body
$r = Call 'HandleStagedVerify' 'GET' ('/v1/staged/verify?document=' + (Esc (KeyOf $guidA)))
Check 'compare_staged_to_grid, a key named: refused' ($r.Status -eq 404 -and $r.Body.Contains('No document is open in the live view')) $r.Body
$r = Call 'HandleGoTo' 'POST' '/v1/goto' '{"index":1}'
Check 'go_to_segment: refused, and memoQ is asked nothing' ($r.Status -eq 404 -and (TakeCommands).Count -eq 0) $r.Body
$r = Call 'HandleSegments' 'GET' ('/v1/segments?document=' + (Esc (KeyOf $guidA)))
Check 'get_segments falls back to what was captured for that document' ($r.Status -eq 200 -and $r.Body.Contains((KeyOf $guidA)) -and $r.Body.Contains('"total":1')) $r.Body

Write-Host '--- preview tool not connected'
Upsert @((NewPart 'mQ-default-aaaa-1' $guidA $nameA 'Costa 12 euro.' 'It costs 21 euros.'))
ToolConnected $false
$r = Call 'HandleQaCheck' 'GET' ('/v1/qa-check?type=numbers&document=' + (Esc (KeyOf $guidA)))
Check 'check_numbers says the tool is not connected' ($r.Status -ne 200 -and $r.Body.Contains('preview tool is not connected')) $r.Body
$r = Call 'HandleSegments' 'GET' ('/v1/segments?document=' + (Esc $unknown))
Check 'get_segments, unknown key: refused, and claims nothing is open' ($r.Status -eq 404 -and $r.Body.Contains('No document is open in the live view') -and -not $r.Body.Contains($nameA)) $r.Body

Write-Host '--- nothing captured, nothing open, nothing named'
$docs.Clear(); ClearLive
$r = Call 'HandleSegments' 'GET' '/v1/segments'
Check 'get_segments keeps its "nothing captured yet" answer' ($r.Status -eq 200 -and $r.Body.Contains('Nothing captured yet')) $r.Body
$r = Call 'HandleConfirmed' 'GET' '/v1/confirmed'
Check 'get_confirmed_pairs keeps its empty answer' ($r.Status -eq 200 -and $r.Body.Contains('"pairs":[]')) $r.Body

# ---- one document, two ids --------------------------------------------------
# Measured in memoQ 12.5 on 13 Nov 2026: the id memoQ sends with a translation
# request is NOT the documentGuid its live view reports for the same document.
# So the key get_project lists for a captured document names no open document
# by id, and the two are joined by their text. Everything above gave both sides
# one id, which is why it passed while the real thing failed.
Write-Host '--- one document, two ids: the resolver'
$mtA   = [Guid]'aaaaaaaa-1111-4222-8333-0123456789ab'   # memoQ's translation-side id for document A
$mtOld = [Guid]'bbbbbbbb-1111-4222-8333-0123456789ab'
$s1 = 'La presente invenzione riguarda una <inline_tag id="0">lampada</inline_tag> per biciclette.'
$s2 = 'Il prezzo &amp; la consegna sono indicati di seguito.'
$s3 = 'Il fanale si accende quando la luce ambiente diminuisce.'
$paraA = 'La presente invenzione riguarda una <b>lampada</b> per biciclette. Costa 12 euro. Il prezzo & la consegna sono indicati di seguito.'
function TwoIds {
    ClearLive
    Upsert @(
        (NewPart 'mQ-default-aaaa-1' $guidA $nameA 'Premessa' 'Background'),
        (NewPart 'mQ-default-aaaa-2' $guidA $nameA $paraA 'The present invention relates to a bicycle lamp. It costs 21 euros. Price & delivery are given below.'),
        (NewPart 'mQ-default-aaaa-3' $guidA $nameA $s3 'The lamp comes on when the ambient light falls.')
    )
    Start-Sleep -Milliseconds 30
    Upsert @(
        (NewPart 'mQ-default-bbbb-1' $guidB $nameB 'Premessa' 'Background'),
        (NewPart 'mQ-default-bbbb-2' $guidB $nameB 'Questo manuale descrive la manutenzione ordinaria della macchina.' 'This manual describes routine maintenance of the machine.')
    )
    ToolConnected $true
}
function OpenNow { return @($previewType.GetMethod('Documents', $Static).Invoke($null, $null)) }
function CaptureWith([Guid]$id, [string[]]$sources, [string]$key = $null) {
    if (-not $key) { $key = KeyOf $id }
    $c = NewCapture $key $id
    foreach ($x in $sources) { $capDocType.GetField('Sources').GetValue($c).Add($x) }
    return $c
}
TwoIds
$open2 = OpenNow
$capMt = CaptureWith $mtA @('<b>Premessa</b>', $s1, $s2)
Check 'the capture''s key finds the open document'      (IsDoc (Live (KeyOf $mtA) $open2 @($capMt)) $guidA)
Check 'its id with dashes too'                          (IsDoc (Live $mtA.ToString('D') $open2 @($capMt)) $guidA)
Check 'the open document''s id finds the capture'       (IsCap (Captured $guidA.ToString('D') @($capMt) $open2) (KeyOf $mtA))
Check 'so does its name'                                (IsCap (Captured $nameA @($capMt) $open2) (KeyOf $mtA))
Check 'and its id in the key spelling'                  (IsCap (Captured (KeyOf $guidA) @($capMt) $open2) (KeyOf $mtA))

Write-Host '--- one document, two ids: when the text does not settle it'
function NotJoined($r) { $null -eq $r.Doc -and $r.Problem.Contains('could not be matched') -and $r.Problem.Contains($nameA) }
$r = Live (KeyOf $mtA) $open2 @((CaptureWith $mtA @('Un testo che non si trova in nessun documento aperto.')))
Check 'text in no open document: refused, open documents named' (NotJoined $r) $r.Problem
$r = Live (KeyOf $mtA) $open2 @((CaptureWith $mtA @('<b>Premessa</b>', 'Costa 12.')))
Check 'only a heading both documents have: refused'       (NotJoined $r) $r.Problem
$r = Live (KeyOf $mtA) $open2 @((CaptureWith $mtA @()))
Check 'an empty capture: refused'                         (NotJoined $r) $r.Problem
$r = Live (KeyOf $mtA) $open2 @((CaptureWith $mtA @($s1, 'Una frase che non esiste da nessuna parte, uno.', 'Una frase che non esiste da nessuna parte, due.', 'Una frase che non esiste da nessuna parte, tre.')))
Check 'one segment in four found: not enough, refused'    (NotJoined $r) $r.Problem
$visited = CaptureWith ([Guid]::Empty) @($s1, $s2) 'visited_ita-IT-eng-GB'
$r = Live 'visited_ita-IT-eng-GB' $open2 @($visited)
Check 'the visited-rows bucket is never taken for a document' (NotJoined $r) $r.Problem
$other = CaptureWith $mtA @($s1, $s2)
$capDocType.GetField('TargetLangCode').SetValue($other, 'ger-DE')
$r = Live (KeyOf $mtA) $open2 @($other)
Check 'another language pair: refused'                    (NotJoined $r) $r.Problem
$r = Captured $guidA.ToString('D') @($capMt, (CaptureWith $mtOld @($s1, $s2))) $open2
Check 'two captures with this text: refused, both keys named' ($null -eq $r.Doc -and $r.Problem.Contains('2 captured documents') -and $r.Problem.Contains((KeyOf $mtA)) -and $r.Problem.Contains((KeyOf $mtOld))) $r.Problem
$r = Captured $guidB.ToString('D') @($capMt) $open2
Check 'the other open document still has nothing captured' ($null -eq $r.Doc -and $r.Problem.Contains('is open in memoQ, but')) $r.Problem
# The same file open twice: the text is in both, so it is neither.
Upsert @(
    (NewPart 'mQ-default-cccc-2' $guidC $nameA $paraA ''),
    (NewPart 'mQ-default-cccc-3' $guidC $nameA $s3 '')
)
$r = Live (KeyOf $mtA) (OpenNow) @($capMt)
Check 'the same text open twice: refused, not guessed'    ($null -eq $r.Doc -and $r.Problem.Contains('could not be matched')) $r.Problem

Write-Host '--- one document, two ids: the tools'
TwoIds
$docs.Clear()
$stored = CaptureWith $mtA @('<b>Premessa</b>', $s1, $s2)
$docs[$stored.Key] = $stored
$mtKey = KeyOf $mtA
foreach ($type in @('numbers', 'tags', 'nbsp', 'terminology')) {
    $r = Call 'HandleQaCheck' 'GET' ("/v1/qa-check?type=$type&document=" + (Esc $mtKey))
    Check "check_$type by the key get_project lists" ($r.Status -eq 200 -and $r.Body.Contains('PROJ-001')) $r.Body
}
$r = Call 'HandleQaCheck' 'GET' ('/v1/qa-check?type=numbers&document=' + (Esc $mtKey))
Check 'check_numbers finds that document''s wrong number' ($r.Body.Contains('"found":1')) $r.Body
$r = Call 'HandleInconsistencies' 'GET' ('/v1/inconsistencies?document=' + (Esc $mtKey))
Check 'find_inconsistencies by that key' ($r.Status -eq 200) $r.Body
$r = Call 'HandleStagedVerify' 'GET' ('/v1/staged/verify?document=' + (Esc $mtKey))
Check 'compare_staged_to_grid by that key: that document only' ($r.Status -eq 200 -and $r.Body.Contains('PROJ-001') -and -not $r.Body.Contains('PROJ-002')) $r.Body
[void](TakeCommands)
$r = Call 'HandleGoTo' 'POST' '/v1/goto' ('{"index":2,"document":' + (ConvertTo-Json $mtKey) + '}')
$sent = TakeCommands
Check 'go_to_segment by that key' ($r.Status -eq 200 -and $sent.Count -eq 1 -and $sent[0].PartId -eq 'mQ-default-aaaa-2') $r.Body
foreach ($spelling in @($guidA.ToString('D'), $nameA)) {
    $r = Call 'HandleConfirmed' 'GET' ('/v1/confirmed?document=' + (Esc $spelling))
    Check "get_confirmed_pairs by the open document ($spelling)" ($r.Status -eq 200 -and $r.Body.Contains('"documentKey":"' + $mtKey + '"')) $r.Body
}
$r = Call 'HandleSegments' 'GET' ('/v1/segments?document=' + (Esc $mtKey))
Check 'get_segments by that key still shows what memoQ asked for' ($r.Status -eq 200 -and $r.Body.Contains('"documentKey":"' + $mtKey + '"') -and $r.Body.Contains('"total":3') -and $r.Body.Contains('inline_tag')) $r.Body
$r = Call 'HandleQaCheck' 'GET' ('/v1/qa-check?type=numbers&document=' + (Esc (KeyOf $mtOld)))
Check 'a key nothing was captured under is still refused' ($r.Status -ne 200 -and $r.Body.Contains('No document matches')) $r.Body

Write-Host '--- one document, two ids: 100 times the size'
# 40 open documents of 1,000 paragraphs and 30 captures of 5,000 segments; the
# one being asked for is the last of each. A real job is a handful of documents
# of a few hundred paragraphs.
ClearLive
$big = New-Object System.Collections.Generic.List[object]
foreach ($d in 1..40) {
    $g = if ($d -eq 40) { $guidA } else { [Guid]::NewGuid() }
    $n = if ($d -eq 40) { $nameA } else { "Acme PROJ-$d big.docx" }
    foreach ($row in 1..1000) { $big.Add((NewPart "mQ-default-$d-$row" $g $n "Documento $d, paragrafo numero $row del testo di prova." 't')) }
}
Upsert $big
$bigCaps = @(1..30 | ForEach-Object {
    $d = $_ + 10
    $id = if ($d -eq 40) { $mtA } else { [Guid]::NewGuid() }
    CaptureWith $id @(1..5000 | ForEach-Object { "Documento $d, paragrafo numero $(($_ % 1000) + 1) del testo di prova." })
})
$openBig = OpenNow
$sw = [Diagnostics.Stopwatch]::StartNew()
$hit = Live (KeyOf $mtA) $openBig $bigCaps
$back = Captured $guidA.ToString('D') $bigCaps $openBig
$sw.Stop()
Check "40,000 paragraphs, 150,000 captured segments: both directions in $($sw.ElapsedMilliseconds) ms" ((IsDoc $hit $guidA) -and (IsCap $back (KeyOf $mtA)) -and $sw.ElapsedMilliseconds -lt 5000)

$docs.Clear(); ClearLive
Finish
