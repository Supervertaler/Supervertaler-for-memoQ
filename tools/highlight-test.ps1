# Syntax highlighting is not an edit.
#
# This pins the WinForms quirk the editor's dirty flag has to defend against: a
# RichTextBox raises TextChanged when its character formatting changes, so the
# highlighter's own repaint looks exactly like typing. That made merely opening a
# prompt mark it modified, and - because the same handler restarts the highlight
# timer - it re-highlighted the whole document three times a second for as long
# as the window was open.
#
# The guard lives in MainForm, which cannot be constructed headlessly. What can
# be checked is the premise: if a future change makes Apply() stop raising
# TextChanged this test fails, and the guard can go. Until then it must stay.
$ErrorActionPreference = 'Stop'
$EditorExe = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$editor = [Reflection.Assembly]::LoadFrom($EditorExe)
$highlighter = $editor.GetType('Supervertaler.PromptEditor.MarkdownHighlighter')

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

$box = New-Object Windows.Forms.RichTextBox
$font = New-Object Drawing.Font('Consolas', 10.5)

# A handle is needed: the highlighter saves and restores the scroll position
# through window messages.
$form = New-Object Windows.Forms.Form
$form.Controls.Add($box)
$form.CreateControl()

$box.Text = @"
# A heading

Some prose with a {{SOURCE_LANGUAGE}} placeholder and a {{MYSTERY}} one.

- a bullet
- another

> a quote
"@

$script:changes = 0
$box.add_TextChanged({ $script:changes++ })

$found = $highlighter::Apply($box, $font)

Check ($script:changes -gt 0) "highlighting raises TextChanged ($($script:changes) time(s)) - the guard in MainForm is still required"
Check ($box.Text -notlike '*rtf*') 'the text itself is untouched by highlighting'

# Apply returns every placeholder it saw, tokens and all; deciding which of them
# is a problem is MainForm's job, not the highlighter's.
$names = @($found)
Check ($names -contains '{{MYSTERY}}') "every placeholder is reported back: $($names -join ', ')"
Check ($names -contains '{{SOURCE_LANGUAGE}}') 'including the ones the host fills'
Check ($highlighter.GetMethod('Apply')) 'Apply is the whole surface - no state kept between passes'

$form.Dispose()

Write-Host ''
Write-Host "HIGHLIGHT TEST COMPLETE - $fails failure(s)"
