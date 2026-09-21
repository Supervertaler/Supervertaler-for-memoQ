# Does anything in a dialog stick out past its own edge?
#
# Two clipping reports in one day, both the same shape: a fixed ClientSize with
# controls placed at fixed offsets from it, and no height set on the buttons - so
# at a larger interface font the buttons grew past the bottom, and a caption of
# more than a few words ran off the right.
#
# Looking at a screenshot found them. Measuring finds them before shipping, and
# at whatever font this machine is actually set to rather than the one the
# numbers were written for.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$memoq = 'C:\Program Files\memoQ\memoQ-12'
$inFlight = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
[AppDomain]::CurrentDomain.add_AssemblyResolve({
    param($sender, $eventArgs)
    $name = ([Reflection.AssemblyName]::new($eventArgs.Name)).Name
    if (-not $inFlight.Add($name)) { return $null }
    try {
        $c = Join-Path $memoq "$name.dll"
        if (Test-Path -LiteralPath $c) { return [Reflection.Assembly]::LoadFrom($c) }
        return $null
    } finally { [void]$inFlight.Remove($name) }
})

$exe = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe'
$asm = [Reflection.Assembly]::LoadFrom($exe)
$B = [Reflection.BindingFlags]'Public,NonPublic,Instance'

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# Every control that carries text, against the edges of the form that holds it.
function Fits($form, $what) {
    $client = $form.ClientSize
    $bad = @()
    foreach ($c in $form.Controls) {
        if ($c.Right -gt $client.Width)   { $bad += ("{0} runs {1}px past the right edge" -f $c.GetType().Name, ($c.Right - $client.Width)) }
        if ($c.Bottom -gt $client.Height) { $bad += ("{0} runs {1}px past the bottom edge" -f $c.GetType().Name, ($c.Bottom - $client.Height)) }
    }
    Check ($bad.Count -eq 0) ("$what fits inside its own dialog ({0}x{1})" -f $client.Width, $client.Height)
    foreach ($b in $bad) { Write-Host "       $b" }
}

# ---- the text prompt, with the caption that reported the bug ------------
$type = $asm.GetType('Supervertaler.PromptEditor.TextInputDialog')
$ctor = @($type.GetConstructors([Reflection.BindingFlags]'Public,NonPublic,Instance'))[0]

$long = "A name for this client or project. An existing name reuses that bank; " +
        "nothing already written in it is overwritten."

$dialog = $ctor.Invoke(@("New memory bank", $long, "MEDI.GLOBAL (J_11886-1)"))
try {
    Fits $dialog "the text prompt"

    # The caption must WRAP rather than run on: a label that does not wrap
    # reports a width wider than the dialog and the text simply disappears.
    $caption = @($dialog.Controls | Where-Object { $_ -is [Windows.Forms.Label] })[0]
    Check ($caption.Height -gt ($caption.Font.Height + 2)) `
        "a long caption wraps onto more than one line (height $($caption.Height), one line is about $($caption.Font.Height))"

    # The buttons must be clear of the bottom, which is the reported failure.
    $buttons = @($dialog.Controls | Where-Object { $_ -is [Windows.Forms.Button] })
    Check ($buttons.Count -eq 2) "it has its two buttons"
    foreach ($b in $buttons) {
        Check ($b.Bottom -le $dialog.ClientSize.Height) "$($b.Text) sits inside the bottom edge ($($b.Bottom) of $($dialog.ClientSize.Height))"
        Check ($b.Width -ge $b.PreferredSize.Width) "$($b.Text) is at least as wide as its own text"
    }

    # A short caption must not leave a tall empty dialog.
    $short = $ctor.Invoke(@("Rename", "New name", "x"))
    try {
        Check ($short.ClientSize.Height -lt $dialog.ClientSize.Height) `
            "a short caption gives a shorter dialog ($($short.ClientSize.Height) against $($dialog.ClientSize.Height))"
        Fits $short "the short prompt"
    } finally { $short.Dispose() }
}
finally { $dialog.Dispose() }

# ---- the new-termbase dialog, the first report --------------------------
$nt = $asm.GetType('Supervertaler.PromptEditor.NewTermbaseForm')
if ($nt) {
    $ntCtor = @($nt.GetConstructors([Reflection.BindingFlags]'Public,NonPublic,Instance'))[0]
    $form = $ntCtor.Invoke(@("New termbase", "MEDI.GLOBAL (J_11886-1)", "en", ""))
    try {
        Fits $form "the new-termbase dialog"
        foreach ($b in @($form.Controls | Where-Object { $_ -is [Windows.Forms.Button] })) {
            Check ($b.Bottom -le $form.ClientSize.Height) "$($b.Text) sits inside the bottom edge ($($b.Bottom) of $($form.ClientSize.Height))"
        }
    } finally { $form.Dispose() }
}

Write-Host ''
Write-Host "DIALOG FIT TEST COMPLETE - $fails failure(s)"
