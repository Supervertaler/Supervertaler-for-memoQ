# Editing a memory-bank article, driven through the window rather than around it.
#
# The glossary harness tests the document; this one tests the path a person
# actually takes: select an article in the tree, type, press Save, and have the
# file on disk change. Two things it exists to catch. The prompt fields have to
# hide and come back, because leaving them on screen invites someone to fill in
# a name an article does not have. And the file has to be written UTF-8 with no
# byte order mark - MemoryBankReader does not strip one, so a BOM arrives in the
# prompt as three characters at the top of the article.
#
# The article is a temp file, not a real bank: the point is the save path, and
# a harness has no business writing client terminology.
#
# It calls Save(), the method the button and Ctrl+S call, rather than the
# SaveArticle() underneath it. The first version of this file called the leaf
# directly and passed while the whole path was dead: Save() began with a guard
# that returned early whenever no PROMPT was open, so it never reached the
# article branch, and the button it lives behind stayed grey. Testing the leaf
# proves the leaf.
$ErrorActionPreference = 'Stop'
$EditorExe = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe'

Add-Type -AssemblyName System.Windows.Forms

$editor = [Reflection.Assembly]::LoadFrom($EditorExe)
$Inst = [Reflection.BindingFlags]'NonPublic,Instance'
$Ctor = [Reflection.BindingFlags]'Instance,Public,NonPublic'

$formT = $editor.GetType('Supervertaler.PromptEditor.MainForm')
$articleT = $editor.GetType('Supervertaler.PromptEditor.BankArticleNode')

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

$dir = Join-Path $env:TEMP 'sv-article-test'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$path = [string](Join-Path $dir 'method.md')

$body = "# Method" + [char]10 + [char]10 + "Translate claims literally." + [char]10
[IO.File]::WriteAllText($path, $body, (New-Object Text.UTF8Encoding($false)))

$form = [Activator]::CreateInstance($formT, $Ctor, $null, @([string]$null), $null)

# Control.Visible reports EFFECTIVE visibility - a child of a form that was
# never shown reads false however it was set - so the form is shown, off-screen
# so it does not flash in front of whoever is running this.
$form.StartPosition = 'Manual'
$form.Location = New-Object Drawing.Point(-4000, -4000)
$form.Show()

try {
    function Field($name) { return $formT.GetField($name, $Inst).GetValue($form) }
    function Call($name, $argv) { return $formT.GetMethod($name, $Inst).Invoke($form, $argv) }

    $fields = Field '_promptFields'
    $body_ = Field '_editor'
    $grid = Field '_glossaryGrid'

    Check ($fields.Visible) "the prompt fields are on screen to begin with"

    # ---- open the article the way the tree does --------------------------
    $node = [Activator]::CreateInstance($articleT)
    $articleT.GetField('Bank').SetValue($node, 'harness-bank')
    $articleT.GetField('Path').SetValue($node, $path)
    $articleT.GetField('Title').SetValue($node, 'method')

    $argv = New-Object object[] 1
    $argv[0] = $node
    Call 'LoadArticle' $argv | Out-Null

    Check ($body_.Text -eq $body) "the article's text is loaded verbatim"
    Check (-not $fields.Visible) "the prompt fields hide - an article has no name or sort order"
    Check (-not $grid.Visible) "and the glossary grid stays out of the way"
    Check (-not $body_.ReadOnly) "the article is editable"

    # ---- edit and save ---------------------------------------------------
    $edited = $body + [char]10 + "Never close a claim." + [char]10
    $body_.Text = $edited
    Check ($formT.GetField('_dirty', $Inst).GetValue($form)) "typing marks the window unsaved"

    $save = Field '_save'
    Check ($save.Enabled) "the Save button becomes usable"

    # Save(), not SaveArticle(): this is what the button and Ctrl+S call.
    $saved = Call 'Save' @()
    Check ($saved) "Save reports success"
    Check (-not $formT.GetField('_dirty', $Inst).GetValue($form)) "and clears the unsaved marker"

    $onDisk = [IO.File]::ReadAllText($path)
    Check ($onDisk -eq $edited) "the file on disk is what was typed"

    # The check that matters most, and the one an eye cannot make.
    $bytes = [IO.File]::ReadAllBytes($path)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    Check (-not $bom) "written without a byte order mark, which would reach the prompt as three characters"

    # ---- going back to a prompt restores the pane -----------------------
    # Without this the pane keeps whatever shape the last selection left it in,
    # so a prompt opened after an article would have no name box.
    Call 'Clear' @() | Out-Null
    $prompts = $editor.GetType('Supervertaler.PromptEditor.MainForm')
    $lib = New-Object 'Supervertaler.Core.PromptLibrary'
    $all = $lib.GetAllPrompts()
    $any = if ($all.Count -gt 0) { $all[0] } else { $null }

    if ($any) {
        # Indexed rather than piped: a pipeline hands back a PSObject wrapper,
        # and reflection will not convert one to PromptTemplate.
        $argv2 = New-Object object[] 1
        $argv2[0] = $any
        Call 'LoadPrompt' $argv2 | Out-Null
        Check ($fields.Visible) "selecting a prompt afterwards brings the fields back"
        Check (-not $grid.Visible) "and leaves the grid hidden"
    }
    else {
        Check $false "no prompt in the library to switch back to"
    }
}
finally {
    $form.Dispose()
}

# ---- and the same for a glossary ----------------------------------------
# It was equally unsaveable and for the same reason, so it is checked the same
# way: through the button, not through the grid's own Save.
$gpath = [string](Join-Path $dir 'terms.txt')
[IO.File]::WriteAllLines($gpath, @(
    '#! source=dut target=eng',
    '# exported from Acme (PROJ-001) v3',
    'afsluiter	valve'
), (New-Object Text.UTF8Encoding($false)))

$form2 = [Activator]::CreateInstance($formT, $Ctor, $null, @([string]$null), $null)
$form2.StartPosition = 'Manual'
$form2.Location = New-Object Drawing.Point(-4000, -4000)
$form2.Show()

try {
    function Field2($name) { return $formT.GetField($name, $Inst).GetValue($form2) }
    function Call2($name, $argv) { return $formT.GetMethod($name, $Inst).Invoke($form2, $argv) }

    $glossaryT = $editor.GetType('Supervertaler.PromptEditor.GlossaryNode')
    $gnode = [Activator]::CreateInstance($glossaryT)
    $glossaryT.GetField('Path').SetValue($gnode, $gpath)
    $glossaryT.GetField('Name').SetValue($gnode, 'terms.txt')

    $gargv = New-Object object[] 1
    $gargv[0] = $gnode
    Call2 'LoadGlossary' $gargv | Out-Null

    $gridPanel = Field2 '_glossaryGrid'
    Check ($gridPanel.Visible) "the grid takes the pane when a glossary is opened"
    Check (-not (Field2 '_promptFields').Visible) "and the prompt fields step aside"

    $gridT = $gridPanel.GetType()
    $dgv = $gridT.GetField('_grid', $Inst).GetValue($gridPanel)
    Check ($dgv.Rows.Count -ge 1) "the terms are in the grid: $($dgv.Rows.Count) row(s) including the new-row"

    # Edit a target the way a person would.
    $dgv.Rows[0].Cells['target'].Value = 'shut-off valve'

    Check ($formT.GetField('_dirty', $Inst).GetValue($form2)) "editing a cell marks the window unsaved"
    Check ((Field2 '_save').Enabled) "and the Save button becomes usable"

    $gsaved = Call2 'Save' @()
    Check ($gsaved) "Save reports success"

    $text = [IO.File]::ReadAllText($gpath)
    Check ($text.Contains("afsluiter`tshut-off valve")) "the edit reaches the file"
    Check ($text.Contains('#! source=dut target=eng')) "the direction header survives the round trip"
    Check ($text.Contains('# exported from Acme (PROJ-001) v3')) "and so does the comment"
}
finally {
    $form2.Dispose()
}

Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host "ARTICLE TEST COMPLETE - $fails failure(s)"
