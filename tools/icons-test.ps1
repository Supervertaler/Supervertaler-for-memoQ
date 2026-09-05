# The toolbar icons come from Windows' own icon font by codepoint, and a
# codepoint is exactly the kind of thing that is right in the documentation and
# wrong in the source. A glyph the font does not have renders as a hollow box or
# as nothing at all, and both look deliberate enough to survive a glance.
#
# So: every glyph must draw something, and no two may draw the SAME something -
# which is what a pair of wrong codepoints landing on the font's fallback looks
# like.
$ErrorActionPreference = 'Stop'
$Root      = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ'
$EditorExe = "$Root\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe"

Add-Type -AssemblyName System.Drawing

$editor = [Reflection.Assembly]::LoadFrom($EditorExe)
$glyphs = $editor.GetType('Supervertaler.PromptEditor.Glyphs')
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# ---- the font ---------------------------------------------------------------
$family = $glyphs.GetProperty('Family', $Static).GetValue($null)
Check ($null -ne $family) "an icon font is installed: $family"

if ($null -eq $family) {
    # Not a failure of the code - the fallback to text-only is deliberate - but
    # nothing below can say anything useful.
    Write-Host ''
    Write-Host "ICON TEST COMPLETE - $fails failure(s)"
    exit
}

# ---- every glyph draws, and draws something of its own ----------------------
$render = $glyphs.GetMethod('Render', $Static)
$names = @('NewPrompt', 'Save', 'Placeholder', 'AutoPrompt', 'Mcp',
           'Activity', 'Settings', 'Prompt', 'Glossary', 'Bank')

$sha = [Security.Cryptography.SHA1]::Create()
$seen = @{}
$blank = @()
$dupes = @()

foreach ($name in $names) {
    $glyph = $glyphs.GetField($name, $Static).GetValue($null)

    $argv = New-Object object[] 3
    $argv[0] = $glyph
    $argv[1] = [Drawing.Color]::Black
    $argv[2] = 20
    $image = $render.Invoke($null, $argv)

    if ($null -eq $image) { $blank += $name; continue }

    # Hash the pixels rather than compare images: two glyphs that both fell back
    # to the same box are byte-identical, which is the signature being looked for.
    $bmp = [Drawing.Bitmap]$image
    $bytes = New-Object byte[] ($bmp.Width * $bmp.Height * 4)
    $i = 0
    for ($y = 0; $y -lt $bmp.Height; $y++) {
        for ($x = 0; $x -lt $bmp.Width; $x++) {
            $p = $bmp.GetPixel($x, $y)
            $bytes[$i++] = $p.A; $bytes[$i++] = $p.R; $bytes[$i++] = $p.G; $bytes[$i++] = $p.B
        }
    }
    $hash = [Convert]::ToBase64String($sha.ComputeHash($bytes))

    if ($seen.ContainsKey($hash)) { $dupes += "$name = $($seen[$hash])" }
    else { $seen[$hash] = $name }
}

Check ($blank.Count -eq 0) "every glyph draws something ($($blank.Count) blank: $($blank -join ', '))"
Check ($dupes.Count -eq 0) "and each draws its own ($($dupes.Count) identical: $($dupes -join ', '))"

# ---- a codepoint the font certainly does not have --------------------------
# The guard that makes the two checks above meaningful. A private-use codepoint
# is a poor probe - Segoe Fluent Icons is large and E0FF turned out to be in it.
# A CJK character is unambiguous: neither the icon font nor Segoe UI has one, so
# both renders fall back to the same third font and come out identical, which is
# exactly the signature Render looks for.
$argv = New-Object object[] 3
$argv[0] = [char]0x6F22 + ''
$argv[1] = [Drawing.Color]::Black
$argv[2] = 20
Check ($null -eq $render.Invoke($null, $argv)) 'a glyph the font lacks returns nothing rather than a blank box'

# ---- scaling ---------------------------------------------------------------
$sizeFor = $glyphs.GetMethod('SizeFor', $Static)
Check ($sizeFor.Invoke($null, @([object]96)) -eq 16) 'icons are 16px at 100%'
Check ($sizeFor.Invoke($null, @([object]144)) -eq 24) 'and 24px at 150%'
Check ($sizeFor.Invoke($null, @([object]0)) -ge 16) 'a nonsense DPI still gives a usable size'

Write-Host ''
Write-Host "ICON TEST COMPLETE - $fails failure(s)"
