# The product marker is derived, never authored.
#
# The library deliberately treats the filename as the display name and ignores a
# "name:" key, because the two used to drift whenever someone renamed a file in
# Explorer. Adding a marker to that filename reintroduces exactly that risk, so
# the marker has to be strippable on the way in and regenerated on the way out
# with no ambiguity. That round trip is what this checks.
$ErrorActionPreference = 'Stop'
$EditorExe = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe'

$editor = [Reflection.Assembly]::LoadFrom($EditorExe)
$library = $editor.GetType('Supervertaler.Core.PromptLibrary')

$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'
$tag = $library.GetMethod('AppTag', $Static)
$strip = $library.GetMethod('StripAppTag', $Static)

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# ---- the marker each app gets --------------------------------------------
$cases = @(
    @('memoq', ' [memoQ]'),
    @('trados', ' [Trados]'),
    @('workbench', ' [Workbench]'),
    @('both', ''),
    @('', ''),
    @($null, ''),
    @('MemoQ', ' [memoQ]'),        # the field is not case-sensitive
    @('something else', '')        # an unknown value is not a product
)
$ok = $true
foreach ($c in $cases) {
    $got = $tag.Invoke($null, [object[]]@($c[0]))
    if ($got -ne $c[1]) { $ok = $false; Write-Host "    app='$($c[0])': got '$got', expected '$($c[1])'" }
}
Check $ok "markers, $($cases.Count) cases"

# ---- and what comes back off a filename ----------------------------------
# A prompt may legitimately have brackets in its name, so only the three known
# markers may be removed - anything else is part of what the user called it.
$stems = @(
    @('BRANTS (ORFF) dut-NL-eng-GB [memoQ]', 'BRANTS (ORFF) dut-NL-eng-GB'),
    @('BRANTS (ORFF-033-NL-WO) v2 [Trados]', 'BRANTS (ORFF-033-NL-WO) v2'),
    @('Something [Workbench]', 'Something'),
    @('Something [memoq]', 'Something'),          # case-insensitive on the way in
    @('Claim wording [draft]', 'Claim wording [draft]'),
    @('Default Translation Prompt', 'Default Translation Prompt'),
    # A marker with no name in front of it is the whole name: stripping it
    # would leave a prompt that cannot be written back to disk.
    @('[memoQ]', '[memoQ]'),
    @('', '')
)
$ok = $true
foreach ($c in $stems) {
    $got = $strip.Invoke($null, [object[]]@($c[0]))
    if ($got -ne $c[1]) { $ok = $false; Write-Host "    '$($c[0])': got '$got', expected '$($c[1])'" }
}
Check $ok "names recovered from stems, $($stems.Count) cases"

# ---- the round trip is stable --------------------------------------------
# Applying a marker and stripping it again must land exactly where it started,
# or a prompt would be renamed a little further every time it was saved.
$ok = $true
foreach ($name in @('BRANTS (ORFF) dut-NL-eng-GB', 'Default Translation Prompt', 'Claim wording [draft]')) {
    foreach ($app in @('memoq', 'trados', 'workbench', 'both')) {
        $stem = $name + $tag.Invoke($null, [object[]]@($app))
        $back = $strip.Invoke($null, [object[]]@($stem))
        if ($back -ne $name) { $ok = $false; Write-Host "    '$name' as $app -> '$stem' -> '$back'" }
    }
}
Check $ok 'tagging then stripping returns the original name'

# ---- and it survives a second pass ---------------------------------------
# The retag runs at every start, so a name that already carries its marker must
# not accumulate another one.
$twice = $strip.Invoke($null, [object[]]@('Foo [memoQ]'))
$twice = $twice + $tag.Invoke($null, [object[]]@('memoq'))
Check ($twice -eq 'Foo [memoQ]') "re-tagging an already tagged name is a no-op: $twice"

Write-Host ''
Write-Host "APP TAG TEST COMPLETE - $fails failure(s)"
