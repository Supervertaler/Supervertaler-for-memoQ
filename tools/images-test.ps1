# The Images panel, memoQ side - the leaves that do not need memoQ or a model.
#
# memoQ never holds the original document, so the panel lives or dies by how
# it finds files: memoQ's recorded import path when that exists on this disk,
# the path the user pointed at when it does not. That store is shared with
# structure context, so locating a document once must light up both - the
# last section drives the real PlanFor through a located file and checks it.
#
# The dialog is probed shown, off-screen: Control.Visible is effective
# visibility and reads false on a form that was never shown.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$PluginDll = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll'
$EditorExe = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression

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
$editor = [Reflection.Assembly]::LoadFrom($EditorExe)
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'
$Inst = [Reflection.BindingFlags]'Public,NonPublic,Instance'

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# ---- a synthetic .docx: a lettered list, one described image ---------------
$W = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"'
function P($text, $numId) {
    $ppr = if ($numId -ne $null) { "<w:pPr><w:numPr><w:ilvl w:val=`"0`"/><w:numId w:val=`"$numId`"/></w:numPr></w:pPr>" } else { '' }
    return "<w:p>$ppr<w:r><w:t xml:space=`"preserve`">$text</w:t></w:r></w:p>"
}
$paras = @(
    (P 'Title' $null)                                                    # 0
    (P 'step one, fixing the seat' 1)                                    # 1 -> a)
    (P 'step two, sealing the seat' 1)                                   # 2 -> b)
    (P 'Figure 1 shows a valve with a seat 12 and a stem 14.' $null)     # 3
    '<w:p><w:r><w:drawing><a:blip r:embed="rId5"/></w:drawing></w:r></w:p>'  # 4 the image
    (P 'Figure 1' $null)                                                 # 5 caption
)
$docXml = "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><w:document $W><w:body>" + ($paras -join '') + '</w:body></w:document>'
$numXml = "<?xml version=`"1.0`" encoding=`"UTF-8`" standalone=`"yes`"?><w:numbering $W>" +
    '<w:abstractNum w:abstractNumId="0"><w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="lowerLetter"/><w:lvlText w:val="%1)"/></w:lvl></w:abstractNum>' +
    '<w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num></w:numbering>'
$relsXml = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId5" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image1.png"/></Relationships>'
$ctXml = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="png" ContentType="image/png"/><Default Extension="xml" ContentType="application/xml"/><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/></Types>'
$png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==')

function MakeDocx($entries) {
    $ms = New-Object IO.MemoryStream
    $zip = New-Object IO.Compression.ZipArchive($ms, [IO.Compression.ZipArchiveMode]::Create, $true)
    for ($i = 0; $i -lt $entries.Length; $i += 2) {
        $e = $zip.CreateEntry($entries[$i])
        $s = $e.Open()
        $b = if ($entries[$i + 1] -is [byte[]]) { $entries[$i + 1] } else { [Text.Encoding]::UTF8.GetBytes([string]$entries[$i + 1]) }
        $s.Write($b, 0, $b.Length); $s.Dispose()
    }
    $zip.Dispose()
    return $ms.ToArray()
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("sv-images-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$docxPath = [string](Join-Path $work 'PROJ-001 figures.docx')
[IO.File]::WriteAllBytes($docxPath, (MakeDocx @('[Content_Types].xml', $ctXml, 'word/document.xml', $docXml, 'word/numbering.xml', $numXml, 'word/_rels/document.xml.rels', $relsXml, 'word/media/image1.png', $png)))
$deadPath = 'C:\_In\source\eng\PROJ-001 figures.docx'   # what a server project records: the PM's machine
Check (-not (Test-Path $deadPath)) 'the recorded server path does not exist here (the premise of the whole panel)'

try {

# ---- 1. the per-document file store (plugin copy: the one PlanFor reads) ---
$files = $plugin.GetType('Supervertaler.MemoQ.Core.DocumentFiles')
$files.GetField('AllowInHarness', $Static).SetValue($null, $true)
$forDoc = $files.GetMethod('ForDocument', $Static)
$resolve = $files.GetMethod('Resolve', $Static)
$remember = $files.GetMethod('Remember', $Static)
$withPrefix = $files.GetMethod('WithPrefix', $Static)
function ForDoc($k) { return $forDoc.Invoke($null, [object[]]@([string]$k)) }
function Resolve($k, $recorded) { $r = if ($recorded -eq $null) { $null } else { [string]$recorded }; return $resolve.Invoke($null, [object[]]@([string]$k, $r)) }
function Remember($k, $p) { return [bool]$remember.Invoke($null, [object[]]@([string]$k, [string]$p)) }

$key = 'harness-' + [Guid]::NewGuid().ToString('D')
$other = 'harness-' + [Guid]::NewGuid().ToString('D')
Check ((ForDoc $key) -eq $null) 'an unknown document has no remembered file'
Check (Remember $key $docxPath) 'remembering a file reports success'
Check ((ForDoc $key) -eq $docxPath) 'and it reads back'
Check ((ForDoc ("  $key  ")) -eq $docxPath) 'the key is trimmed on the way in'
Check ((Resolve $key $docxPath) -eq $docxPath) "memoQ's own path wins when it exists"
Check ((Resolve $key $deadPath) -eq $docxPath) 'a dead recorded path falls through to the located file'
Check ((Resolve $key $null) -eq $docxPath) 'no recorded path at all: the located file'
Check ((Resolve $other $deadPath) -eq $null) 'nothing located and a dead path: null, not the dead path'
Check ((Resolve $other $null) -eq $null) 'nothing at all: null'

$store = Join-Path (Join-Path $env:LOCALAPPDATA 'Supervertaler.memoQ') 'document-files.txt'
$text = [IO.File]::ReadAllText($store)
Check ($text.StartsWith('#')) 'the file opens with the comment that says what it is'
Check ($text.Contains("$key=$docxPath")) 'one key=path line per document'

# Files added by hand are keyed under the bank they were added for.
$bank = 'harness-bank-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
Remember "added:${bank}:one.docx" $docxPath | Out-Null
Remember "added:${bank}:two.docx" $deadPath | Out-Null
Remember "added:${bank}-other:three.docx" $docxPath | Out-Null
$mine = @($withPrefix.Invoke($null, [object[]]@("added:${bank}:")))
Check ($mine.Count -eq 2) "WithPrefix lists the two files added for this bank, not the third (got $($mine.Count))"
Check (@($withPrefix.Invoke($null, [object[]]@('added:no-such-bank:'))).Count -eq 0) 'and nothing for a bank with none'
Check (@($withPrefix.Invoke($null, [object[]]@(''))).Count -eq 0) 'an empty prefix lists nothing rather than everything'

Check (Remember $key '') 'an empty path forgets'
Check ((ForDoc $key) -eq $null) 'and the document is unknown again'
foreach ($k in @("added:${bank}:one.docx", "added:${bank}:two.docx", "added:${bank}-other:three.docx")) { Remember $k '' | Out-Null }

# ---- 2. the extractor on the synthetic document (core, as the editor compiles it)
$extractorT = $editor.GetType('Supervertaler.Core.DocxImageExtractor')
$extract = $extractorT.GetMethod('Extract', [type[]]@([string], [bool], [string]))
$set = $extract.Invoke($null, [object[]]@($docxPath, $false, $null))
$images = $set.GetType().GetProperty('Images').GetValue($set)
Check ($images.Count -eq 1) "one image found (got $($images.Count))"
$img = $images[0]
Check ($img.GetType().GetProperty('ParagraphIndex').GetValue($img) -eq 4) "anchored to paragraph 4, the one holding the drawing (got $($img.GetType().GetProperty('ParagraphIndex').GetValue($img)))"
$label = $img.GetType().GetProperty('Label').GetValue($img)
Write-Host "      label: $(if ($label) {$label} else {'(none)'}); descriptions: $($img.GetType().GetProperty('Descriptions').GetValue($img).Count)"

$folder = [string](Join-Path $work 'figures')
$set2 = $extract.Invoke($null, [object[]]@($docxPath, $false, $folder))
$saved = $set2.GetType().GetProperty('SavedFiles').GetValue($set2)
Check ($saved.Count -eq 1) "extracting to a folder saves one file (got $($saved.Count))"
Check ($saved.Count -eq 1 -and (Test-Path (Join-Path $folder $saved[0]))) "and it is there: $(if ($saved.Count) {$saved[0]})"
$img0 = $set2.GetType().GetProperty('Images').GetValue($set2)[0]
Check ($img0.GetType().GetProperty('SavedFileName').GetValue($img0) -eq $saved[0]) 'the image carries the file it was written to, so a failed save cannot shift the pairing'

# ---- 2b. a folder per document, once there is more than one ---------------------
# Every document numbers its figures from 1, so two of them in one folder means
# the second's "Figure 01.png" silently replaces the first's - after the model
# has already been shown the wrong picture.
$hostT2 = $editor.GetType('Supervertaler.PromptEditor.ImagesHost')
$targetFolder = $hostT2.GetMethod('TargetFolder', $Static)
$safeName = $hostT2.GetMethod('SafeFolderName', $Static)
$countImages = $hostT2.GetMethod('CountImages', $Static)
function Target($f, $n, $c) { return [string]$targetFolder.Invoke($null, [object[]]@([string]$f, [string]$n, [int]$c)) }
function Safe($n) { return [string]$safeName.Invoke($null, [object[]]@([string]$n)) }
function CountImgs($f) { return [int]$countImages.Invoke($null, [object[]]@([string]$f)) }

Check ((Target $folder 'PROJ-001 figures.docx' 1) -eq $folder) 'one document keeps the flat folder'
Check ((Target $folder 'PROJ-001 figures.docx' 0) -eq $folder) 'and so does none'
Check ((Target $folder 'PROJ-001 figures.docx' 2) -eq (Join-Path $folder 'PROJ-001 figures')) "two documents: a folder each (got '$(Target $folder 'PROJ-001 figures.docx' 2)')"
Check ((Safe 'Annex A.docx') -eq 'Annex A') 'the extension is dropped from the folder name'
Check ((Safe 'a/b:c*?.docx') -eq 'abc') 'characters a path cannot hold are dropped'
Check ((Safe '') -eq 'document' -and (Safe $null) -eq 'document' -and (Safe '...') -eq 'document') 'a name that reduces to nothing still gives a folder'
Check ((Safe '..\..\escape.docx') -eq 'escape') 'a name cannot climb out of the folder'

# The collision itself: the same figure name written by two documents.
$two = Join-Path $work 'two-docs'
$second = Join-Path $work 'Annex A.docx'
Copy-Item $docxPath $second
$a = Target $two 'PROJ-001 figures.docx' 2
$b = Target $two 'Annex A.docx' 2
$extract.Invoke($null, [object[]]@($docxPath, $false, $a)) | Out-Null
$extract.Invoke($null, [object[]]@([string]$second, $false, $b)) | Out-Null
Check ((CountImgs $two) -eq 2) "both documents' images survive: $(CountImgs $two) files under the figures folder"
Check ((Get-ChildItem -Path $two -Recurse -File).Count -eq 2) 'two files on disk, not one overwritten'
$flat = Join-Path $work 'flat'
$extract.Invoke($null, [object[]]@($docxPath, $false, [string]$flat)) | Out-Null
$extract.Invoke($null, [object[]]@([string]$second, $false, [string]$flat)) | Out-Null
Check ((Get-ChildItem -Path $flat -File).Count -eq 1) 'and the same two into one folder would have been one file - the bug this prevents'
Check ((CountImgs (Join-Path $work 'no-such-folder')) -eq -1) 'a folder that does not exist counts -1, not 0'
# The third state the dialog needs and core's count does not have: a folder
# that exists and is empty is not the same as one that was never made - only
# the first is worth an Open folder link.
$empty = Join-Path $work 'empty-figures'
New-Item -ItemType Directory -Path $empty | Out-Null
Check ((CountImgs $empty) -eq 0) 'an empty folder counts 0, told apart from one that is not there'
Check ((CountImgs '') -eq -1 -and (CountImgs $null) -eq -1) 'no folder at all counts -1'
# One level and no deeper, matching what extraction writes.
$deep = Join-Path (Join-Path $two 'PROJ-001 figures') 'deeper'
New-Item -ItemType Directory -Path $deep -Force | Out-Null
Copy-Item (Get-ChildItem -Path $two -Recurse -File | Select-Object -First 1).FullName (Join-Path $deep 'Figure 01.png')
Check ((CountImgs $two) -eq 2) 'a file two levels down is not counted'

# ---- 3. counting rows in figures.md ------------------------------------------
$hostT = $editor.GetType('Supervertaler.PromptEditor.ImagesHost')
$rows = $hostT.GetMethod('TableRows', $Static)
function Rows($md) { return [int]$rows.Invoke($null, [object[]]@($md)) }
$md = "# Figures`n`n| Figure | What the text says |`n|---|---|`n| Fig. 1 | a valve |`n| Fig. 2 | a seat |`n| Fig. 3 | a stem |`n`nSigns:`n`n| Sign | Where |`n|:--|:--|`n| 12 | drawing only |`n| 14 | both |`n"
Check ((Rows $md) -eq 5) "two tables, five rows: header and separator of each excluded (got $(Rows $md))"
Check ((Rows "| a | b |`n|---|---|`n") -eq 0) 'a header with no rows counts nothing'
Check ((Rows '') -eq 0 -and (Rows $null) -eq 0) 'empty and null count nothing'
Check ((Rows ($md -replace "`n", "`r`n")) -eq 5) 'CRLF reads the same'

# ---- 4. the documents line ----------------------------------------------------
$stateT = $editor.GetType('Supervertaler.PromptEditor.ImagesState')
$dialogT = $editor.GetType('Supervertaler.PromptEditor.ImagesDialog')
$actionsT = $editor.GetType('Supervertaler.PromptEditor.ImagesActions')
$docsText = $dialogT.GetMethod('DocumentsText', $Static)
function NewState() { return [Activator]::CreateInstance($stateT) }
function Put($o, $name, $v) {
    # A bare -1 reaches a function as the string "-1"; convert to the field's type.
    $f = $o.GetType().GetField($name)
    if ($v -ne $null -and $f.FieldType.IsValueType) { $v = [Convert]::ChangeType($v, $f.FieldType) }
    $f.SetValue($o, $v)
}
function Text($st) { return [string]$docsText.Invoke($null, [object[]]@($st)) }

$st = NewState
Check ((Text $st) -like 'No documents yet*') 'nothing known, no reason: the plain "no documents yet"'
Put $st 'WhyNoDocuments' 'memoQ is not running.'
Check ((Text $st) -eq 'memoQ is not running.') 'nothing known, with a reason: the reason, verbatim'
$st = NewState; Put $st 'DocumentCount' 3
Check ((Text $st) -eq 'No images in the 3 documents listed.') "documents but no images (got '$(Text $st)')"
$rowT = $editor.GetType('Supervertaler.PromptEditor.DocumentRow')
function Row($name, $note) { return [Activator]::CreateInstance($rowT, [object[]]@([string]$name, [string]$note)) }
function AddRow($st, $field, $name, $note) { $st.GetType().GetField($field).GetValue($st).Add((Row $name $note)) }

# The name and the finding are separate fields, which is the point of the
# table: run together, the eye has to hunt for where the name ends on every row.
$r = Row 'Annex A.docx' '2 images'
Check ($r.Name -eq 'Annex A.docx' -and $r.Note -eq '2 images') 'a row keeps the name and the finding apart'

$st = NewState; Put $st 'DocumentCount' 12; Put $st 'TotalImages' 25
AddRow $st 'Documents' 'a.docx' '9 images'
AddRow $st 'Documents' 'b.docx' '9 images'
AddRow $st 'Documents' 'c.docx' '7 images'
AddRow $st 'DocumentsWithoutImages' 'd.docx' 'no images'
AddRow $st 'DocumentsWithoutFile' 'e.docx' 'not on this computer' 
Check ((Text $st) -eq '25 images in 3 of 12 documents; the rest have none. 1 document not on this computer.') "the full summary (got '$(Text $st)')"

# ---- 5. what the dialog enables, shown off-screen -----------------------------
function ShowDialog($actions, $state) {
    $d = [Activator]::CreateInstance($dialogT, $Inst, $null, @($actions, $state), $null)
    $d.StartPosition = 'Manual'; $d.Location = New-Object Drawing.Point(-4000, -4000)
    $d.Show()
    return $d
}
function Ctl($d, $name) { return $dialogT.GetField($name, $Inst).GetValue($d) }
function Actions($names) {
    $a = [Activator]::CreateInstance($actionsT)
    foreach ($n in $names) { $actionsT.GetField($n).SetValue($a, [Action]{ }) }
    return $a
}

# The layout probe's own state: shared bank, one document missing, no actions.
$d = [Activator]::CreateInstance($dialogT)
$d.StartPosition = 'Manual'; $d.Location = New-Object Drawing.Point(-4000, -4000); $d.Show()
Check (-not (Ctl $d '_lnkLocate').Visible) 'no Locate action: the link is hidden even with a missing document'
Check (-not (Ctl $d '_lnkAddFile').Visible) 'no Add action: hidden'
Check (-not (Ctl $d '_btnExtract').Enabled) 'shared bank: Extract is disabled'
Check ((Ctl $d '_lblExtractNote').Text -like '*memory bank*') 'and the note says a bank is needed'
Check (-not (Ctl $d '_lnkCreateBank').Visible) 'no CreateProjectBank action: the offer is hidden'
$d.Close()

# A project bank, images extracted, every action wired.
$st = NewState
Put $st 'ProjectKnown' $true; Put $st 'ProjectName' 'Acme PROJ-001'; Put $st 'SuggestedBankName' 'acme-proj-001'
Put $st 'DocumentCount' 2; Put $st 'TotalImages' 3; Put $st 'Labelled' 3
AddRow $st 'Documents' 'PROJ-001 figures.docx' '3 images, 3 with a figure label'
AddRow $st 'DocumentsWithoutFile' 'Annex.docx' 'not on this computer - memoQ recorded C:\_In\source\eng\Annex.docx' 
Put $st 'BankName' 'acme-proj-001'; Put $st 'BankIsShared' $false
Put $st 'Folder' (Join-Path $work 'figures'); Put $st 'FolderImages' 3
Put $st 'ProviderName' 'Anthropic / claude-opus-5'
$d = ShowDialog (Actions @('Extract', 'OpenFolder', 'LocateDocument', 'AddDocumentFile', 'Analyse', 'WriteFigures', 'ShowReport', 'CreateProjectBank')) $st
Check ((Ctl $d '_lnkLocate').Visible) 'a document not on this computer: Locate is offered'
Check ((Ctl $d '_lnkAddFile').Visible) 'Add a document file is offered'
Check ((Ctl $d '_btnExtract').Enabled) 'a project bank and images: Extract is enabled'
Check ((Ctl $d '_btnAnalyse').Enabled) 'images extracted: Describe with AI is enabled'
Check ((Ctl $d '_lblAnalyseNote').Text -like '3 AI requests to Anthropic / claude-opus-5*') "the note states the cost (got '$((Ctl $d '_lblAnalyseNote').Text)')"
Check ((Ctl $d '_btnWrite').Enabled) 'the free alternative is enabled'
Check (-not (Ctl $d '_lnkCreateBank').Visible) 'a project bank is active: no offer to create one'
Check ((Ctl $d '_lblResult').Text -like 'No descriptions yet*acme-proj-001*') "Result says where figures.md would go (got '$((Ctl $d '_lblResult').Text)')"
$lst = Ctl $d '_lstDocs'
Check ($lst.Items.Count -eq 2) 'the list holds the readable document and the missing one'
Check ($lst.Columns.Count -eq 2) 'two columns: the name and what was found in it'
Check ($lst.Items[0].Text -eq 'PROJ-001 figures.docx') "the first column is the name alone (got '$($lst.Items[0].Text)')"
Check ($lst.Items[0].SubItems[1].Text -eq '3 images, 3 with a figure label') 'and the second is the finding alone'
Check ($lst.Items[1].Text -eq 'Annex.docx' -and $lst.Items[1].SubItems[1].Text -like 'not on this computer*') 'the missing one names the file in its own column'
Check ($lst.Columns[0].Width + $lst.Columns[1].Width -le $lst.ClientSize.Width + 8) 'the columns fit the width rather than pushing the finding off the edge'
Check ($lst.Columns[0].Width -le [Math]::Max(140, [int]($lst.ClientSize.Width * 0.42))) 'a long name cannot take more than its share of the width' 
$d.Close()

# Nothing extracted yet: the AI step waits for step 1.
Put $st 'FolderImages' -1
$d = ShowDialog (Actions @('Extract', 'Analyse', 'WriteFigures')) $st
Check (-not (Ctl $d '_btnAnalyse').Enabled) 'no extracted images: Describe with AI is disabled'
Check ((Ctl $d '_lblAnalyseNote').Text -eq 'Do step 1 first.') 'and the note says so'
Check (-not (Ctl $d '_lnkOpen').Visible) 'no folder yet: no Open folder link'
Check ((Ctl $d '_lblFolder').Text -like '*(not created yet)') 'the folder line says it is not created yet'
Check (-not (Ctl $d '_lnkLocate').Visible) 'no Locate action wired: hidden despite the missing document'
$d.Close()

# The shared bank: the offer to create a project bank, unless the project is stale.
Put $st 'FolderImages' 3; Put $st 'BankName' '_shared'; Put $st 'BankIsShared' $true; Put $st 'Folder' $null
$d = ShowDialog (Actions @('Extract', 'Analyse', 'CreateProjectBank')) $st
Check ((Ctl $d '_lnkCreateBank').Visible) 'shared bank, live project: the offer to create a project bank shows'
Check ((Ctl $d '_lnkCreateBank').Text -like '*acme-proj-001*') 'named after the project'
Check (-not (Ctl $d '_btnExtract').Enabled -and -not (Ctl $d '_btnAnalyse').Enabled) 'and both steps wait for it'
$d.Close()
Put $st 'ProjectIsStale' $true
$d = ShowDialog (Actions @('CreateProjectBank')) $st
Check (-not (Ctl $d '_lnkCreateBank').Visible) 'stale project: no offer - it could be filed against the wrong job'
Check ((Ctl $d '_lblResult').Text -like '*earlier memoQ session*') 'and Result says why'
$d.Close()

# A run in progress: everything that would start another is off, progress shows.
Put $st 'ProjectIsStale' $false; Put $st 'BankName' 'acme-proj-001'; Put $st 'BankIsShared' $false; Put $st 'Folder' (Join-Path $work 'figures')
Put $st 'AnalysisRunning' $true; Put $st 'Progress' 'Describing image 2 of 3 (PROJ-001 figures.docx)…'
$d = ShowDialog (Actions @('Extract', 'Analyse', 'WriteFigures')) $st
Check (-not (Ctl $d '_btnAnalyse').Enabled -and -not (Ctl $d '_btnExtract').Enabled -and -not (Ctl $d '_btnWrite').Enabled) 'while a run is on, the three buttons are off'
Check ((Ctl $d '_lblAnalyseNote').Text -eq 'Describing image 2 of 3 (PROJ-001 figures.docx)…') 'the note shows the progress line'
$d.Close()

# ---- 5b. the folder a new bank goes in ------------------------------------------
# MemoryBanks.DirFor answers null for a bank that is not there - it exists to
# say "no such bank", not to propose a path. Handing that null to
# CreateDirectory was "Value cannot be null. Parameter name: path" on the one
# case the Create link exists for: a project with no bank yet.
$banksT = $editor.GetType('Supervertaler.Core.MemoryBanks')
$dirFor = $banksT.GetMethod('DirFor', $Static)
$rootProp = $banksT.GetProperty('Root', $Static)
$sanitize = $banksT.GetMethod('Sanitize', $Static)
$newName = [string]$sanitize.Invoke($null, [object[]]@('Example project (patent, en-nl)'))
Check ($newName -eq 'Example project (patent, en-nl)') "a project name survives sanitising (got '$newName')"
Check ($dirFor.Invoke($null, [object[]]@('bank-that-is-not-there-' + [Guid]::NewGuid().ToString('N'))) -eq $null) 'DirFor answers null for a bank that does not exist - the null that crashed it'
$wouldBe = [IO.Path]::Combine([string]$rootProp.GetValue($null), $newName)
Check (-not [string]::IsNullOrEmpty($wouldBe)) "so the folder to create is built from Root: $wouldBe"

# ---- 6. structure context through a located file --------------------------------
# The whole point of sharing the store: a server project's document, located
# once in the Images panel, gets its list numbering without the preview tool
# ever having reported a usable path.
$markersT = $plugin.GetType('Supervertaler.MemoQ.Core.StructureMarkers')
$planT = $markersT.GetNestedType('Plan', $Inst)
$sharedT = $plugin.GetType('Supervertaler.MemoQ.Core.SharedSettings')
$generalT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings')
$secureT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSecureSettings')
$settingsT = $plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerSettings')
$engineCt = $plugin.GetType('Supervertaler.MemoQ.Core.EngineContext')
$settings = $settingsT.GetMethod('Create').Invoke($null,
    @([Activator]::CreateInstance($generalT), [Activator]::CreateInstance($secureT)))
$ctx = [Activator]::CreateInstance($engineCt, $Inst, $null, @($settings, 'dut', 'eng'), $null)

$metaT = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'MemoQ.MTInterfaces' } | Select-Object -First 1
if (-not $metaT) { $metaT = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.MTInterfaces.dll") }
$metaType = $metaT.GetType('MemoQ.MTInterfaces.MTRequestMetadata')
$meta = [Activator]::CreateInstance($metaType)
$docGuid = [Guid]::NewGuid()
$metaType.GetProperty('DocumentID').SetValue($meta, $docGuid)
$engineCt.GetMethod('NoteMetadata').Invoke($ctx, [object[]]@($meta))

$planFor = $markersT.GetMethod('PlanFor', $Static)
function PlanFor($c) { return $planFor.Invoke($null, [object[]]@($c)) }
function ModeOf($p) { return [string]$planT.GetField('Mode').GetValue($p) }
function ReasonOf($p) { return [string]$planT.GetField('Reason').GetValue($p) }
function MarkerFor($p, $seg) { return $planT.GetMethod('MarkerFor').Invoke($p, [object[]]@([string]$seg)) }

$sharedT.GetProperty('StructureContext', $Static).SetValue($null, $true)
$plan = PlanFor $ctx
Check ((ModeOf $plan) -eq 'Unavailable') "document known, nothing located, no preview rows: Unavailable ($(ReasonOf $plan))"
Check ((ReasonOf $plan) -like '*has not reported*') 'and the reason is the one the log has always given'

Remember ($docGuid.ToString('D')) $docxPath | Out-Null
$plan = PlanFor $ctx
Check ((ModeOf $plan) -eq 'Markers') "the document located: Markers ($(ReasonOf $plan))"
Check ((ReasonOf $plan) -like '*located file*') 'the reason says the markers came from the located file'
Check ((MarkerFor $plan 'step one, fixing the seat') -eq 'a)') "first list item is a) (got '$(MarkerFor $plan 'step one, fixing the seat')')"
Check ((MarkerFor $plan 'step two') -eq 'b)') 'a segment that starts the second item is b)'
Check ((MarkerFor $plan 'Title') -eq $null) 'an unnumbered paragraph has no marker'
Check ((MarkerFor $plan 'seat 12 and a stem 14') -eq $null) 'a sentence from inside a paragraph is not a first segment: no marker'

Remember ($docGuid.ToString('D')) '' | Out-Null
$plan = PlanFor $ctx
Check ((ModeOf $plan) -eq 'Unavailable') 'forgetting the file takes the markers away again'

} finally {
    try { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue } catch { }
}

Write-Host ''
Write-Host "IMAGES TEST COMPLETE - $fails failure(s)"
