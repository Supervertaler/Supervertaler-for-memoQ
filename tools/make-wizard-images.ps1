# The two bitmaps Inno Setup puts on its wizard, drawn in THIS product's colour.
#
# The mark exists in four colourways - blue for Trados, vermillion for memoQ,
# black for the brand, and an inverted one for dark backgrounds. The first
# version of this used sv-icon-512.png from the repository root, which is the
# BLUE one, so the memoQ installer wore Trados's colour. Caught by eye on the
# wizard, which is exactly where a customer would have seen it.
#
# Drawn rather than converted, because the mark is a circle, a gradient and two
# letters, and there is no reliable SVG renderer in .NET. The colours are read
# from the SVG rather than typed here, so a change to the brand colour reaches
# the installer without anybody remembering this file exists.
#
# Inno wants BMP and will not read an SVG or a PNG, so these are generated at
# build time and gitignored: one source of truth for the mark, and no bitmap to
# fall out of date.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent
$out  = Join-Path $root 'installer'
$svg  = Join-Path $out 'sv-icon-memoq.svg'

if (-not (Test-Path $svg)) { throw "The memoQ mark is missing: $svg" }

# The two gradient stops, in the order the SVG declares them.
$stops = @([regex]::Matches((Get-Content $svg -Raw), 'stop-color:(#[0-9A-Fa-f]{6})') |
           ForEach-Object { $_.Groups[1].Value })
if ($stops.Count -lt 2) { throw "Could not read two gradient stops from $svg" }

$from = [System.Drawing.ColorTranslator]::FromHtml($stops[0])
$to   = [System.Drawing.ColorTranslator]::FromHtml($stops[1])
Write-Host ("mark: {0} -> {1}" -f $stops[0], $stops[1])

# Everything below is in the SVG's own 256-unit coordinates and scaled once, so
# the numbers here can be compared with the SVG line by line.
function DrawMark($g, $left, $top, $size) {
    $s = $size / 256.0

    $d = 224 * $s                      # circle r=112
    $cx = $left + ($size - $d) / 2
    $cy = $top  + ($size - $d) / 2

    $rect = New-Object System.Drawing.RectangleF($cx, $cy, $d, $d)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect, $from, $to, [System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal)
    try { $g.FillEllipse($brush, $rect) } finally { $brush.Dispose() }

    # "Sv": two sizes, as the SVG has them, sitting on one baseline.
    $fam = New-Object System.Drawing.FontFamily('Arial')
    $bold = [System.Drawing.FontStyle]::Bold
    # ::new with an explicit [single], because New-Object's parenthesised form
    # picks the wrong Font overload here and fails with a cast error that names
    # a type the constructor does not even have.
    $fS = [System.Drawing.Font]::new('Arial', [single](135 * $s), $bold, [System.Drawing.GraphicsUnit]::Pixel)
    $fv = [System.Drawing.Font]::new('Arial', [single](112 * $s), $bold, [System.Drawing.GraphicsUnit]::Pixel)
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    try {
        # DrawString positions the TOP of the layout box, and the SVG positions
        # the BASELINE. The distance between them is the font's own ascent, read
        # from the font rather than guessed at with a magic fraction.
        $ascent = $fam.GetCellAscent($bold) / $fam.GetEmHeight($bold)

        $fmt = [System.Drawing.StringFormat]::GenericTypographic
        $wS = $g.MeasureString('S', $fS, 0, $fmt).Width
        $wv = $g.MeasureString('v', $fv, 0, $fmt).Width

        # Centred as a pair, and sat on the SVG's baseline of y=178 - which is
        # below the circle's centre, because the mark is optically centred
        # rather than measured from the glyph box.
        $x = $left + ($size - ($wS + $wv)) / 2
        $baseline = $top + 178 * $s

        $g.DrawString('S', $fS, $white, $x,       $baseline - $fS.Size * $ascent, $fmt)
        $g.DrawString('v', $fv, $white, $x + $wS, $baseline - $fv.Size * $ascent, $fmt)
    } finally { $fS.Dispose(); $fv.Dispose(); $white.Dispose(); $fam.Dispose() }
}

function Draw($width, $height, $markSize, $path, $format) {
    $bmp = New-Object System.Drawing.Bitmap($width, $height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        # White for the wizard, so the image does not read as a pasted-on
        # rectangle against the dialog; transparent for the app icon, which sits
        # on whatever background the host app happens to use.
        if ($format -eq [System.Drawing.Imaging.ImageFormat]::Bmp) {
            $g.Clear([System.Drawing.Color]::White)
        } else {
            $g.Clear([System.Drawing.Color]::Transparent)
        }
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

        DrawMark $g (($width - $markSize) / 2) (($height - $markSize) / 2) $markSize

        $bmp.Save($path, $format)
    } finally { $g.Dispose(); $bmp.Dispose() }
    Write-Host ("  {0}  ({1}x{2})" -f (Split-Path $path -Leaf), $width, $height)
}

$bmp = [System.Drawing.Imaging.ImageFormat]::Bmp
$png = [System.Drawing.Imaging.ImageFormat]::Png

Write-Host 'wizard images:'
Draw 164 314 128 (Join-Path $out 'wizard-large.bmp') $bmp   # the tall panel on the first and last pages
Draw  55  58  48 (Join-Path $out 'wizard-small.bmp') $bmp   # the corner mark on every other page

# The MCP bundle's icon, which Claude Desktop shows beside the extension. It was
# sv-icon-512.png - the blue one - for the same reason the wizard was.
Draw 512 512 512 (Join-Path $root 'sv-icon-memoq-512.png') $png
