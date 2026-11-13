# Letters outside ASCII survive the trip to the bridge.
#
# The MCP server sends a query as UTF-8 percent-escapes. .NET Framework's
# HttpListener decoded them with the computer's own code page, so "a with a
# grave accent" arrived as two wrong characters and a term, a document or a
# memory bank named with one was never found. Nothing failed: the answer was
# simply "no terms".
#
# Run through tools\run-harness.ps1. memoQ must be closed. Nothing of the
# translator's is read or written: the terms, documents and data folder are
# this test's own. ASCII only; accented text is built from code points.
. (Join-Path $PSScriptRoot 'bridge-test-common.ps1')

$queryType = PluginType 'Supervertaler.MemoQ.Core.BridgeQuery'
$parse = $queryType.GetMethod('Parse', $Static)
function Parse($rawUrl) { $a = [object[]]::new(1); $a[0] = $rawUrl; return ,$parse.Invoke($null, $a) }

$aGrave = U 0xE0
$samples = [ordered]@{
    'plain ASCII'          = 'tavola rotonda'
    'Italian a-grave'      = 'sovranit' + $aGrave + ' digitale'
    'decomposed a-grave'   = 'sovranita' + (U 0x300) + ' digitale'
    'French e-acute'       = 'soci' + (U 0xE9) + 't' + (U 0xE9)
    'German u-umlaut'      = 'Pr' + (U 0xFC) + 'fung'
    'Spanish n-tilde'      = 'a' + (U 0xF1) + 'o'
    'Polish'               = (U 0x17C,0xF3,0x142,0x107)
    'Greek'                = (U 0x3BA,0x3C5,0x3C1,0x3B9,0x3B1,0x3C1,0x3C7,0x3AF,0x3B1)
    'Russian'              = (U 0x441,0x443,0x432,0x435,0x440,0x435,0x43D,0x438,0x442,0x435,0x442)
    'Japanese'             = (U 0x4E3B,0x6A29)
    'outside the BMP'      = 'x' + [char]::ConvertFromUtf32(0x1F600) + 'y'
    'ampersand and equals' = 'a&b=c d'
    'a real plus'          = '1+1'
}

Write-Host '--- the parser on its own'
foreach ($name in $samples.Keys) {
    $sent = $samples[$name]
    $got = (Parse ('/v1/terms?text=' + (Esc $sent) + '&limit=5'))['text']
    Check "$name arrives unchanged" ($got -ceq $sent)
}
$q = Parse '/v1/x?Text=abc&empty=&flag&a=1&a=2'
Check 'names ignore case' ($q['text'] -ceq 'abc' -and $q['TEXT'] -ceq 'abc')
Check 'a missing name answers null' ($null -eq $q['document'])
Check 'an empty value is empty, not missing' ($q['empty'] -ceq '')
Check 'a name without = has an empty value' ($q['flag'] -ceq '')
Check 'a name given twice answers both' ($q['a'] -ceq '1,2')
Check 'a plus is a space, as before' ((Parse '/v1/x?text=tavola+rotonda')['text'] -ceq 'tavola rotonda')
Check 'no query at all' ((Parse '/v1/x').Count -eq 0)
Check 'a bare question mark' ((Parse '/v1/x?').Count -eq 0)
Check 'no address at all' ((Parse $null).Count -eq 0 -and (Parse '').Count -eq 0)

Write-Host '--- garbled input is refused quietly, never thrown on'
# A lone byte E0 is a-grave in the old Western code page and not valid UTF-8.
$threw = $false
try { $lone = (Parse '/v1/terms?text=sovranit%E0')['text'] } catch { $threw = $true }
Check 'a Western-code-page escape does not throw' (-not $threw)
Check 'and is not guessed into a letter' ($lone -cne ('sovranit' + $aGrave))
foreach ($bad in @('%', '%zz', '%E', 'a%', '%C3', '%C3%', '=%=', '&&&', '%00')) {
    $threw = $false
    try { [void](Parse ('/v1/terms?text=' + $bad)) } catch { $threw = $true }
    Check "malformed '$bad' does not throw" (-not $threw)
}

Write-Host '--- 100 times the size'
# 200,000 characters. Windows refuses an address past about 16,000, so this is
# more than ten times anything that can arrive. Escaped piece by piece, because
# the escaper itself stops at 65,000.
$unit = 'sovranit' + $aGrave + ' '
$long = $unit * 20000
$sw = [Diagnostics.Stopwatch]::StartNew()
$got = (Parse ('/v1/terms?text=' + ((Esc $unit) * 20000)))['text']
$sw.Stop()
Check "a 200,000-character value in $($sw.ElapsedMilliseconds) ms" ($got -ceq $long -and $sw.ElapsedMilliseconds -lt 2000)
$many = '/v1/x?' + ((1..5000 | ForEach-Object { "k$_=v$_" }) -join '&')
Check '5,000 parameters' ((Parse $many)['k5000'] -ceq 'v5000')

Write-Host '--- across real HTTP'
$differs = 0
foreach ($name in $samples.Keys) {
    $sent = $samples[$name]
    $seen = @{}
    [void](Send 'GET' ('/v1/terms?text=' + (Esc $sent)) $null {
        param($ctx)
        $seen.New = (Parse $ctx.Request.RawUrl)['text']
        $seen.Old = $ctx.Request.QueryString['text']
        $ctx.Response.StatusCode = 200
    })
    if ($seen.Old -cne $sent) { $differs++ }
    Check "$name arrives unchanged over HTTP" ($seen.New -ceq $sent)
}
# Not a check: on a computer whose code page is UTF-8 the old way works too.
Write-Host "  (the old way, Request.QueryString, changed $differs of $($samples.Count) on this computer)"

Write-Host '--- lookup_term finds an accented term'
$index = PluginType 'Supervertaler.MemoQ.Core.TermIndex'
$entryType = PluginType 'Supervertaler.MemoQ.Core.TermIndex+Entry'
$terms = [ordered]@{
    ('sovranit' + $aGrave + ' digitale')                         = 'digital sovereignty'
    ('soci' + (U 0xE9) + 't' + (U 0xE9))                         = 'company'
    ('Pr' + (U 0xFC) + 'fung')                                   = 'test'
    (U 0x17C,0xF3,0x142,0x107)                                   = 'yellowish'
    (U 0x3BA,0x3C5,0x3C1,0x3B9,0x3B1,0x3C1,0x3C7,0x3AF,0x3B1)    = 'sovereignty'
    'tavola rotonda'                                             = 'round table'
}
$entries = [Activator]::CreateInstance([System.Collections.Generic.List`1].MakeGenericType($entryType))
foreach ($k in $terms.Keys) {
    $e = [Activator]::CreateInstance($entryType, $true)
    $entryType.GetProperty('Source').SetValue($e, [string]$k)
    $entryType.GetProperty('Target').SetValue($e, [string]$terms[$k])
    $entries.Add($e)
}
$index.GetField('_entries', $Static).SetValue($null, $entries.PSObject.BaseObject)
$index.GetMethod('Rebuild', $Static).Invoke($null, $null)
# The index re-reads its termbases at most every three seconds. Stamped now, it
# answers from the entries above and no termbase file is opened.
function HoldIndex { $index.GetField('_lastCheck', $Static).SetValue($null, [DateTime]::UtcNow) }
function Lookup([string]$text) {
    HoldIndex
    $r = Call 'HandleTermLookup' 'GET' ('/v1/terms?text=' + (Esc $text))
    return $r
}
foreach ($k in $terms.Keys) {
    $r = Lookup ('Una frase con ' + $k + ' dentro.')
    Check "found in a sentence: $($terms[$k])" ($r.Status -eq 200 -and $r.Body.Contains('"target":"' + $terms[$k] + '"')) $r.Body
}
$r = Lookup ('sovranit' + $aGrave + ' digitale')
Check 'the exact term on its own' ($r.Body.Contains('digital sovereignty'))
$r = Lookup ('SOVRANIT' + (U 0xC0) + ' DIGITALE')
Check 'in capitals' ($r.Body.Contains('digital sovereignty'))
# What the bridge used to be handed: the two bytes of a-grave read as two letters.
$r = Lookup ('sovranit' + (U 0xC3,0xA0) + ' digitale')
Check 'the garbled form is a clean miss' ($r.Status -eq 200 -and $r.Body -eq '{"terms":[]}') $r.Body
HoldIndex
$r = Call 'HandleTermLookup' 'GET' '/v1/terms?text=sovranit%E0%20digitale'
Check 'a Western-code-page request is a clean miss' ($r.Status -eq 200 -and $r.Body -eq '{"terms":[]}') $r.Body
HoldIndex
$r = Call 'HandleTermLookup' 'GET' '/v1/terms'
Check 'no text at all is a clean miss' ($r.Status -eq 200 -and $r.Body -eq '{"terms":[]}') $r.Body

Write-Host '--- a document chosen by a name with an accent'
$docName = 'Acme PROJ-001 societ' + $aGrave + '.docx'
$doc = [Guid]::NewGuid()
ClearLive
Upsert @(
    (NewPart 'mQ-default-aaaa-1' $doc $docName 'Il prezzo e 12 euro.' 'The price is 12 euros.'),
    (NewPart 'mQ-default-aaaa-2' $doc $docName 'Sono 3 mesi.' 'It is 4 months.')
)
ToolConnected $true
$r = Call 'HandleQaCheck' 'GET' ('/v1/qa-check?type=numbers&document=' + (Esc $docName))
Check 'check_numbers runs on it' ($r.Status -eq 200 -and $r.Body.Contains('"found":1')) $r.Body
$r = Call 'HandleSegments' 'GET' ('/v1/segments?document=' + (Esc $docName.ToUpperInvariant()))
Check 'get_segments finds it, capitals or not' ($r.Status -eq 200 -and $r.Body.Contains('"total":2')) $r.Body
$r = Call 'HandleQaCheck' 'GET' ('/v1/qa-check?type=numbers&document=' + (Esc ('Acme PROJ-001 societ' + (U 0xC3,0xA0) + '.docx')))
Check 'the garbled name is refused, and the answer names the open document' ($r.Status -ne 200 -and $r.Body.Contains('PROJ-001') -and $r.Body.Contains('No document matches')) $r.Body

Write-Host '--- get_staged filtered by an accented word'
$staged = PluginType 'Supervertaler.MemoQ.Core.StagedTranslations'
$pairs = New-Object 'System.Collections.Generic.List[System.Collections.Generic.KeyValuePair[string,string]]'
$pairs.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,string]' ('La sovranit' + $aGrave + ' digitale europea.'), 'European digital sovereignty.'))
$pairs.Add((New-Object 'System.Collections.Generic.KeyValuePair[string,string]' 'Una tavola rotonda.', 'A round table.'))
$a = [object[]]::new(3); $a[0] = $pairs.PSObject.BaseObject; $a[1] = 'ita-IT-eng-GB'; $a[2] = 'test'
$staged.GetMethod('Stage', $Static).Invoke($null, $a) | Out-Null
$r = Call 'HandleStagedList' 'GET' ('/v1/staged?sourceContains=' + (Esc ('sovranit' + $aGrave)))
Check 'sourceContains matches the accented word' ($r.Status -eq 200 -and $r.Body.Contains('"matched":1')) $r.Body
$staged.GetMethod('Clear', $Static).Invoke($null, $null) | Out-Null

Write-Host '--- every query read goes through the UTF-8 reader'
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\src\Supervertaler.MemoQ\Core\MemoQBridge.cs'))
Check 'MemoQBridge.cs no longer reads Request.QueryString anywhere' (-not $source.Contains('Request.QueryString'))

ClearLive
Finish
