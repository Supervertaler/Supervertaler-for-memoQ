# The two bitmaps Inno Setup puts on its wizard, drawn from the product's own
# icon so the installer carries the Supervertaler mark rather than Inno's
# generic box-and-disc picture - which is the first thing a customer sees and
# currently says nothing about whose software this is.
#
# Inno wants BMP and will not read an .ico, so these are generated rather than
# committed: one source of truth for the mark, and no image file to fall out of
# date when the icon changes.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path (Split-Path $MyInvocation.MyCommand.Path -Parent) -Parent
# The PNG rather than the .ico: a 128px or larger icon frame is PNG-compressed
# inside the .ico, and System.Drawing's Icon.ToBitmap cannot read those - it
# throws "Requested range extends past the end of the array". The PNG is the
# same mark at 512px and reads cleanly.
$icon = Join-Path $root 'sv-icon-512.png'
$out  = Join-Path $root 'installer'

if (-not (Test-Path $icon)) { throw "Mark not found: $icon" }

# Inno's own sizes for the two images, at 100% scaling.
function Draw($width, $height, $markSize, $path) {
    $bmp = New-Object System.Drawing.Bitmap($width, $height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        # The wizard's own background, so the image does not read as a pasted-on
        # rectangle against the dialog.
        $g.Clear([System.Drawing.Color]::White)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

        $img = [System.Drawing.Image]::FromFile($icon)
        try {
            $x = [int](($width - $markSize) / 2)
            $y = [int](($height - $markSize) / 2)
            $g.DrawImage($img, $x, $y, $markSize, $markSize)
        } finally { $img.Dispose() }

        $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp)
    } finally { $g.Dispose(); $bmp.Dispose() }
    Write-Host ("  {0}  ({1}x{2})" -f (Split-Path $path -Leaf), $width, $height)
}

Write-Host 'wizard images:'
Draw 164 314 128 (Join-Path $out 'wizard-large.bmp')   # the tall panel on the first and last pages
Draw  55  58  48 (Join-Path $out 'wizard-small.bmp')   # the corner mark on every other page
