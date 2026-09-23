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

# Make a form report what it will actually look like.
#
# WinForms' Control.Visible walks the parent chain, so EVERY child of a form that
# has not been shown reports false. Both NoOverlaps and TextFits skip invisible
# controls, so against an unshown form they skipped every control and passed
# having checked nothing - in the harness that exists because this dialog shipped
# clipped three times. Found 2026-09-23 while adding a check that also passed
# vacuously.
#
# Showing it off-screen at zero opacity costs a few milliseconds, makes Visible
# mean what it says, and has the side benefit that the layout and every
# PreferredSize are the ones the user gets rather than the ones a constructor
# computed.
function Realise($form) {
    $form.StartPosition = [Windows.Forms.FormStartPosition]::Manual
    $form.Location = New-Object Drawing.Point(-32000, -32000)
    $form.Opacity = 0
    $form.ShowInTaskbar = $false
    $form.TopMost = $false
    $form.Show()
    [Windows.Forms.Application]::DoEvents()
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

# Controls sitting on top of each other. Fitting inside the dialog is not enough:
# a swap button placed at x=234 with a width of 26, beside a box starting at 246,
# is inside the form and on top of its neighbour - which is exactly what shipped
# on 2026-09-21 because this test only looked at the edges.
function NoOverlaps($form, $what) {
    $visible = @($form.Controls | Where-Object { $_.Visible -and $_.Width -gt 0 -and $_.Height -gt 0 })
    $bad = @()

    for ($i = 0; $i -lt $visible.Count; $i++) {
        for ($j = $i + 1; $j -lt $visible.Count; $j++) {
            $a = $visible[$i]; $b = $visible[$j]
            $r = [Drawing.Rectangle]::Intersect($a.Bounds, $b.Bounds)
            if ($r.Width -gt 0 -and $r.Height -gt 0) {
                $bad += ("{0} '{1}' overlaps {2} '{3}' by {4}x{5}px" -f `
                    $a.GetType().Name, $a.Text, $b.GetType().Name, $b.Text, $r.Width, $r.Height)
            }
        }
    }

    Check ($bad.Count -eq 0) "$what has no controls sitting on top of each other"
    foreach ($b in $bad) { Write-Host "       $b" }
}

# Controls smaller than the text inside them. This is the one that generalises:
# every clipping report so far has been a control given a number instead of being
# asked how big it needs to be - a button 84 wide, 26 tall, an arrow 28 wide.
# Bounds and overlaps both pass while the text is cut off inside.
function TextFits($form, $what) {
    $bad = @()
    foreach ($c in $form.Controls) {
        if (-not $c.Visible) { continue }
        if (-not ($c -is [Windows.Forms.Button] -or $c -is [Windows.Forms.Label])) { continue }
        if ([string]::IsNullOrEmpty($c.Text)) { continue }

        $want = $c.PreferredSize
        if ($c.Width -lt $want.Width)   { $bad += ("{0} '{1}' is {2}px narrower than its text" -f $c.GetType().Name, $c.Text, ($want.Width - $c.Width)) }
        if ($c.Height -lt $want.Height) { $bad += ("{0} '{1}' is {2}px shorter than its text" -f $c.GetType().Name, $c.Text, ($want.Height - $c.Height)) }
    }
    Check ($bad.Count -eq 0) "$what has nothing clipped inside a control"
    foreach ($b in $bad) { Write-Host "       $b" }

    # A button whose caption is a character the font does not have is blank on
    # screen, and if its width was measured from that character it collapses too.
    # Both happened: the swap button in the quick-add dialog carried U+21C4, and
    # inside memoQ it was an empty 25-pixel box between the two term fields. The
    # check above cannot see it, because a control whose text measures nothing is
    # never narrower than its text.
    #
    # This runs outside memoQ, in a different font, so it cannot ask whether THIS
    # font has the glyph. What it can do is refuse the gamble: a button caption is
    # two or three words, and there is no button in this product that needs a
    # character outside ASCII to say what it does. Labels are exempt - they carry
    # en dashes on purpose and are long enough that a missing glyph is visible.
    $captions = @()
    foreach ($c in $form.Controls) {
        if (-not ($c -is [Windows.Forms.Button])) { continue }
        if (-not $c.Visible) { continue }
        if ([string]::IsNullOrEmpty($c.Text)) {
            $captions += ("a button at {0},{1} has no caption at all" -f $c.Left, $c.Top)
            continue
        }
        $odd = @($c.Text.ToCharArray() | Where-Object { [int]$_ -gt 126 })
        if ($odd.Count -gt 0) {
            $captions += ("button '{0}' uses {1} outside ASCII, which may be missing from the font memoQ uses" -f `
                $c.Text, (($odd | ForEach-Object { 'U+{0:X4}' -f [int]$_ }) -join ', '))
        }
    }
    Check ($captions.Count -eq 0) "$what has no button that could come out blank"
    foreach ($b in $captions) { Write-Host "       $b" }
}

# ---- the text prompt, with the caption that reported the bug ------------
$type = $asm.GetType('Supervertaler.PromptEditor.TextInputDialog')
$ctor = @($type.GetConstructors([Reflection.BindingFlags]'Public,NonPublic,Instance'))[0]

$long = "A name for this client or project. An existing name reuses that bank; " +
        "nothing already written in it is overwritten."

$dialog = $ctor.Invoke(@("New memory bank", $long, "ACME.GLOBAL (PROJ-00001)"))
try {
    Realise $dialog
    Fits $dialog "the text prompt"
    NoOverlaps $dialog "the text prompt"
    TextFits $dialog "the text prompt"

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
        Realise $short
        Fits $short "the short prompt"
        NoOverlaps $short "the short prompt"
        TextFits $short "the short prompt"
    } finally { $short.Dispose() }
}
finally { $dialog.Dispose() }

# ---- the new-termbase dialog, the first report --------------------------
$nt = $asm.GetType('Supervertaler.PromptEditor.NewTermbaseForm')
if ($nt) {
    $ntCtor = @($nt.GetConstructors([Reflection.BindingFlags]'Public,NonPublic,Instance'))[0]
    $form = $ntCtor.Invoke(@("New termbase", "ACME.GLOBAL (PROJ-00001)", "en", ""))
    try {
        Realise $form
        Fits $form "the new-termbase dialog"
        NoOverlaps $form "the new-termbase dialog"
        TextFits $form "the new-termbase dialog"
        foreach ($b in @($form.Controls | Where-Object { $_ -is [Windows.Forms.Button] })) {
            Check ($b.Bottom -le $form.ClientSize.Height) "$($b.Text) sits inside the bottom edge ($($b.Bottom) of $($form.ClientSize.Height))"
        }
    } finally { $form.Dispose() }
}

# ---- the connect-AI-assistant dialog --------------------------------------
# Four wrapped paragraphs, two headings, a button, a status line and a link,
# every one of them measured rather than placed at a number - which is the shape
# that has been reported clipped three times in this product. The status line
# also changes length depending on what is already set up, so the dialog has to
# be right at both lengths rather than at the one that was on screen when it was
# written.
$connect = $asm.GetType('Supervertaler.PromptEditor.ChatGptSetupDialog')
$dlg = [Activator]::CreateInstance($connect, $true)
try {
    Realise $dlg
    Fits $dlg "the connect-AI-assistant dialog"
    NoOverlaps $dlg "the connect-AI-assistant dialog"
    TextFits $dlg "the connect-AI-assistant dialog"

    # The link has to be reachable, not merely present: it is the only route to
    # the Claude Desktop half.
    $link = @($dlg.Controls | Where-Object { $_ -is [Windows.Forms.LinkLabel] })[0]
    Check ($null -ne $link) 'the Claude Desktop link is there'
    if ($link) {
        Check ($link.Bottom -le $dlg.ClientSize.Height) `
            "and sits inside the bottom edge ($($link.Bottom) of $($dlg.ClientSize.Height))"
        Check ($link.Right -le $dlg.ClientSize.Width) `
            "and inside the right edge ($($link.Right) of $($dlg.ClientSize.Width))"
    }

    # The old wording was reported as confusing on sight. This is not a style
    # check - it is the specific sentence that said nothing about what to do.
    $texts = @($dlg.Controls | ForEach-Object { $_.Text }) -join ' '
    Check ($texts -notmatch 'installs itself') 'the dialog does not say an assistant "installs itself"'
    # This used to assert the dialog EXPLAINED why one assistant had no button.
    # It has one now, so that sentence had to go, and the check with it - the
    # harness flagged the stale assertion on the first run after the change,
    # which is the whole reason it pins wording as well as geometry.
    #
    # What matters now is that neither half sends the user off to find a file:
    # both are a single press.
    Check ($texts -notmatch 'no button') 'neither assistant is described as having no button'

    $buttons = @($dlg.Controls | Where-Object { $_ -is [Windows.Forms.Button] } | ForEach-Object { $_.Text })
    Check ($buttons -contains 'Set up ChatGPT desktop') 'ChatGPT has its button'
    Check ($buttons -contains 'Show me the extension') 'and Claude Desktop has one too'

    # The button says what it does. It used to say "Install in Claude Desktop" and could
    # not install anything: Claude Desktop does that from inside its own settings, and the
    # file association for extensions offers three identical unlabelled entries on a machine
    # that has updated Claude a few times.
    Check ($texts -notmatch 'takes over') 'and does not promise that Claude Desktop takes over'

    foreach ($b in @($dlg.Controls | Where-Object { $_ -is [Windows.Forms.Button] })) {
        Check ($b.Bottom -le $dlg.ClientSize.Height) "$($b.Text) sits inside the bottom edge ($($b.Bottom) of $($dlg.ClientSize.Height))"
    }
} finally { $dlg.Dispose() }

# ---- the licence window, in every state -----------------------------------
# Built from a LicenceView the test makes up, so no state here reads or writes
# anybody's real licence. Every state has different wording and a different set
# of buttons, so each is measured separately - a window that fits when licensed
# can clip when the trial has ended and a key box appears.
$viewT = $asm.GetType('Supervertaler.PromptEditor.LicenceView')
$dlgT  = $asm.GetType('Supervertaler.PromptEditor.LicenceDialog')
$stT   = $asm.GetType('Supervertaler.Core.LicenceState')

function View($state, $hasKey, $days, $damaged) {
    $v = [Activator]::CreateInstance($viewT, $true)
    $viewT.GetField('State').SetValue($v, [Enum]::Parse($stT, $state))
    $viewT.GetField('HasKey').SetValue($v, [bool]$hasKey)
    $viewT.GetField('MaskedKey').SetValue($v, 'ABCD1234-****-****-****-****WXYZ')
    $viewT.GetField('TrialDaysRemaining').SetValue($v, [int]$days)
    $viewT.GetField('TrialEndsUtc').SetValue($v, [DateTime]::UtcNow.AddDays($days))
    $viewT.GetField('LastValidatedUtc').SetValue($v, [DateTime]::UtcNow.AddDays(-2))
    $viewT.GetField('DamagedFileFound').SetValue($v, [bool]$damaged)
    return $v
}

$cases = @(
    @{ what = 'licensed';                     v = (View 'Licensed' $true  0  $false) },
    @{ what = 'on trial';                     v = (View 'Trial'    $false 11 $false) },
    @{ what = 'trial ended';                  v = (View 'Expired'  $false 0  $false) },
    @{ what = 'key not confirmed for 30 days'; v = (View 'Expired'  $true  0  $false) },
    @{ what = 'unreadable';                   v = (View 'Unknown'  $false 0  $false) },
    @{ what = 'damaged file, key needed';     v = (View 'Expired'  $false 0  $true)  }
)

foreach ($c in $cases) {
    $w = "the licence window ($($c.what))"
    $d = [Activator]::CreateInstance($dlgT, [object[]]@($c.v))
    try {
        Realise $d
        Fits $d $w
        NoOverlaps $d $w
        TextFits $d $w
        foreach ($b in @($d.Controls | Where-Object { $_ -is [Windows.Forms.Button] })) {
            Check ($b.Bottom -le $d.ClientSize.Height) "$w - $($b.Text) sits inside the bottom edge"
        }
    } finally { $d.Dispose() }
}

# The warning a damaged file needs is shown only when there is one.
$damaged = [Activator]::CreateInstance($dlgT, [object[]]@((View 'Expired' $false 0 $true)))
$clean   = [Activator]::CreateInstance($dlgT, [object[]]@((View 'Expired' $false 0 $false)))
try {
    $hasWarning = { param($f) @($f.Controls | Where-Object { $_ -is [Windows.Forms.Label] -and $_.Text -match 'damaged' }).Count -gt 0 }
    Check (& $hasWarning $damaged) 'a damaged licence file gets its warning'
    Check (-not (& $hasWarning $clean)) 'and nothing else does'
} finally { $damaged.Dispose(); $clean.Dispose() }

# ---- the quick-add dialog -------------------------------------------------
# In the plugin assembly rather than the editor, which is why this test did not
# cover it and why a button shipped sitting on top of a text box.
$plugin = [Reflection.Assembly]::LoadFrom('D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll')
$qa = $plugin.GetType('Supervertaler.MemoQ.Core.QuickAddForm')
$qaCtor = @($qa.GetConstructors([Reflection.BindingFlags]'Public,NonPublic,Instance')) |
          Where-Object { $_.GetParameters().Count -eq 6 }

# The real case: the warning shown, and terms long enough to be awkward.
$quick = $qaCtor.Invoke(@('eng-GB', 'dut-NL', 'Coronary Artery Disease', 'kransslagaderaandoening',
                          'ACME.GLOBAL (PROJ-00001)', $true))
try {
    Realise $quick
    Fits $quick "the quick-add dialog"
    NoOverlaps $quick "the quick-add dialog"
    TextFits $quick "the quick-add dialog"

    # The warning must be readable, which means wrapped rather than cut off.
    $warning = @($quick.Controls | Where-Object { $_ -is [Windows.Forms.Label] -and $_.ForeColor.Name -eq 'Firebrick' })[0]
    Check ($null -ne $warning) "the inferred-order warning is shown when the order was inferred"
    if ($warning) {
        # This was written as "preferred width fits OR it is more than one line",
        # and the first half can never be false: the label is AutoSize with a
        # MaximumSize, so WinForms sets its width to its preferred width by
        # definition. TextFits above compares the same two numbers for every
        # label and button, and is worth having for the ones given a fixed size -
        # but for an AutoSize control it asks a question with one answer. So the
        # whole check passed unconditionally, in the harness that exists because
        # this dialog shipped clipped three times.
        #
        # What can actually fail is whether the text wrapped at all. Take the
        # MaximumSize away and this becomes one long line, which is how the
        # warning first shipped, cut off mid-sentence.
        Check ($warning.Height -gt ($warning.Font.Height + 4)) `
            "and the warning wraps onto more than one line ($($warning.Height)px, one line is about $($warning.Font.Height)px)"
        Check ($warning.Right -le $quick.ClientSize.Width) `
            "and its right edge is inside the dialog ($($warning.Right) of $($quick.ClientSize.Width))"
        Check ($warning.Text -notmatch 'inferred, not read') "and says what to do rather than what happened"
    }
} finally { $quick.Dispose() }

# Without the warning the dialog must not keep a gap where it would have been.
$quiet = $qaCtor.Invoke(@('eng-GB', 'dut-NL', 'device', 'hulpmiddel', 'ACME.GLOBAL (PROJ-00001)', $false))
try {
    Realise $quiet
    Fits $quiet "the quick-add dialog, nothing inferred"
    NoOverlaps $quiet "the quick-add dialog, nothing inferred"
    TextFits $quiet "the quick-add dialog, nothing inferred"
    Check (@($quiet.Controls | Where-Object { $_ -is [Windows.Forms.Label] -and $_.Visible -and $_.ForeColor.Name -eq 'Firebrick' }).Count -eq 0) `
        "no warning when the segment settled it"
} finally { $quiet.Dispose() }

Write-Host ''
Write-Host "DIALOG FIT TEST COMPLETE - $fails failure(s)"
