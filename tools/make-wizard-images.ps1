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

function Draw($width, $height, $markSize, $path, $format, $markLeft = $null) {
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

        $left = if ($null -eq $markLeft) { ($width - $markSize) / 2 } else { $markLeft }
        DrawMark $g $left (($height - $markSize) / 2) $markSize

        $bmp.Save($path, $format)
    } finally { $g.Dispose(); $bmp.Dispose() }
    Write-Host ("  {0}  ({1}x{2})" -f (Split-Path $path -Leaf), $width, $height)
}

$bmp = [System.Drawing.Imaging.ImageFormat]::Bmp
$png = [System.Drawing.Imaging.ImageFormat]::Png

Write-Host 'wizard images:'
Draw 164 314 128 (Join-Path $out 'wizard-large.bmp') $bmp   # the tall panel on the first and last pages
# The header mark, drawn at the size of the slot Inno puts it in rather than the
# 55x58 the documentation suggests, and with the mark against the LEFT of it.
#
# Both of those are the fix for the same complaint: Inno right-aligns this slot
# hard against the window edge, so the mark sat closer to its edge than the page
# title sits to the other one. The first attempt moved Inno's own image control
# from [Code], which worked arithmetically and left a slice of the circle
# unpainted and a line trailing out of it - reported twice before it was
# believed. Moving a control Inno has already laid out is not worth the fight.
#
# So the white space goes in the bitmap instead and Inno positions nothing. A
# probe installer reports the slot as 73x77 with Stretch on, which is where these
# numbers come from - matching it exactly also means no resampling, so the mark
# is sharper than it was. The mark is smaller than it would be centred, because
# the slack in a 73-wide slot is all there is to give: the alternative is moving
# the control, and that is what left a hole in it.
Draw  73  77  48 (Join-Path $out 'wizard-small.bmp') $bmp 0

# The MCP bundle's icon, which Claude Desktop shows beside the extension. It was
# sv-icon-512.png - the blue one - for the same reason the wizard was.
Draw 512 512 512 (Join-Path $root 'sv-icon-memoq-512.png') $png

# ---------------------------------------------------------------------------
# The installer's own icon.
#
# Inno puts the wizard's images on the PAGES; the icon on the title bar, in the
# task bar, and on the .exe in Explorer comes from SetupIconFile, and with no
# SetupIconFile it uses its own generic one. So the pages were branded and the
# window was not - which is the first thing anyone sees, and the thing they see
# again every time they look at the downloaded file.
#
# Written by hand because .NET can make a single-size icon and nothing else,
# and one size is not enough: Explorer, the task bar and the title bar all ask
# for different ones and scale whatever they are given. Small sizes are DIB
# frames, which every version of Windows reads; the two large ones are PNG,
# which keeps the file to tens of kilobytes rather than a third of a megabyte.
function MarkBitmap($size) {
    $b = New-Object System.Drawing.Bitmap($size, $size,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($b)
    try {
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        # AntiAlias rather than AntiAliasGridFit: grid fitting snaps stems to
        # whole pixels, which is right on an opaque background and leaves hard
        # edges against a transparent one.
        $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
        DrawMark $g 0 0 $size
    } finally { $g.Dispose() }
    return $b
}

# A 32-bit bottom-up DIB, as an icon frame wants it: a BITMAPINFOHEADER whose
# height is DOUBLED to account for the mask, the colour rows, then a mask of
# zeroes. The mask is unused at 32 bits, where the alpha channel decides, but
# leaving it out makes the frame unreadable.
function DibFrame($bitmap) {
    $w = $bitmap.Width; $h = $bitmap.Height
    $data = $bitmap.LockBits(
        (New-Object System.Drawing.Rectangle(0, 0, $w, $h)),
        [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $pixels = New-Object byte[] ($data.Stride * $h)
        [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
    } finally { $bitmap.UnlockBits($data) }

    $maskRow = [Math]::Floor(($w + 31) / 32) * 4
    $stream = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([int]40)          # biSize
        $writer.Write([int]$w)
        $writer.Write([int]($h * 2))    # colour rows plus mask rows
        $writer.Write([int16]1)         # biPlanes
        $writer.Write([int16]32)        # biBitCount
        $writer.Write([int]0)           # BI_RGB
        $writer.Write([int]($w * $h * 4 + $maskRow * $h))
        $writer.Write([int]0); $writer.Write([int]0)
        $writer.Write([int]0); $writer.Write([int]0)

        # Bottom-up, which is the one thing about this format that catches
        # everybody: the last row of the image is written first.
        for ($y = $h - 1; $y -ge 0; $y--) {
            $writer.Write($pixels, $y * $data.Stride, $w * 4)
        }
        $writer.Write((New-Object byte[] ($maskRow * $h)))

        $writer.Flush()
        # The leading comma stops PowerShell unrolling the array into a stream
        # of separate bytes on the way out. Without it every frame arrives as a
        # single byte and the icon is a 159-byte directory pointing at nothing.
        return ,$stream.ToArray()
    } finally { $writer.Dispose(); $stream.Dispose() }
}

function PngFrame($bitmap) {
    $stream = New-Object System.IO.MemoryStream
    try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    } finally { $stream.Dispose() }
}

function WriteIcon($sizes, $pngFrom, $path) {
    $frames = @()
    foreach ($size in $sizes) {
        $bitmap = MarkBitmap $size
        try {
            $bytes = if ($size -ge $pngFrom) { PngFrame $bitmap } else { DibFrame $bitmap }
            $frames += [pscustomobject]@{ Size = $size; Bytes = [byte[]]$bytes }
        } finally { $bitmap.Dispose() }
    }

    $file = [System.IO.File]::Create($path)
    $writer = New-Object System.IO.BinaryWriter($file)
    try {
        $writer.Write([int16]0); $writer.Write([int16]1)          # reserved, type 1 = icon
        $writer.Write([int16]$frames.Count)

        # Every frame's data follows every frame's directory entry, so the first
        # offset is past the whole directory.
        $offset = 6 + 16 * $frames.Count
        foreach ($f in $frames) {
            # 256 is written as 0: the field is one byte.
            $writer.Write([byte]($(if ($f.Size -ge 256) { 0 } else { $f.Size })))
            $writer.Write([byte]($(if ($f.Size -ge 256) { 0 } else { $f.Size })))
            $writer.Write([byte]0)            # palette colours
            $writer.Write([byte]0)            # reserved
            $writer.Write([int16]1)           # planes
            $writer.Write([int16]32)          # bits per pixel
            $writer.Write([int]$f.Bytes.Length)
            $writer.Write([int]$offset)
            $offset += $f.Bytes.Length
        }
        foreach ($f in $frames) { $writer.Write([byte[]]$f.Bytes, 0, $f.Bytes.Length) }
    } finally { $writer.Dispose(); $file.Dispose() }

    $written = (Get-Item $path).Length
    $expected = 6 + 16 * $frames.Count
    foreach ($f in $frames) { $expected += $f.Bytes.Length }
    if ($written -ne $expected) { throw "The icon is $written bytes and should be $expected" }

    Write-Host ("  {0}  ({1}) {2} KB" -f (Split-Path $path -Leaf), ($sizes -join ', '), [int]($written / 1KB))
}

Write-Host 'installer icon:'
WriteIcon @(16, 20, 24, 32, 40, 48, 64, 128, 256) 128 (Join-Path $out 'sv-icon-memoq.ico')
