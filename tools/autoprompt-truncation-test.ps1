# What happens to a truncated AutoPrompt draft.
#
# Two faults were fixed together and this covers both:
#
#   ExtractDraft    - a start delimiter with no end used to fall through to the
#                     raw response, so the truncation was saved with the
#                     delimiter line still in it. No delimiter at all still
#                     falls back, because some models ignore the instruction
#                     and return a complete bare prompt.
#   PromptValidator - nothing checked the content at all. The checks live in
#                     core and are shared with Supervertaler for Trados, which
#                     is where the failure they exist for happened.
#
# Run through tools/run-harness.ps1, never directly.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$PluginDll = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll'

$script:probed = @{}
[AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler] {
    param($s, $e)
    $name = ($e.Name -split ',')[0]
    if ($script:probed.ContainsKey($name)) { return $null }
    $script:probed[$name] = $true
    foreach ($dir in @($MemoQPath, "$MemoQPath\Addins")) {
        $candidate = Join-Path $dir "$name.dll"
        if (Test-Path $candidate) { try { return [Reflection.Assembly]::LoadFrom($candidate) } catch { return $null } }
    }
    return $null
})

$plugin = [Reflection.Assembly]::LoadFrom($PluginDll)
$NonPublicStatic = [Reflection.BindingFlags]'NonPublic,Static'
$PublicStatic = [Reflection.BindingFlags]'Public,Static'

$pass = 0
$fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host ("PASS {0}" -f $name) }
    else { $script:fail++; Write-Host ("FAIL {0}  {1}" -f $name, $detail) -ForegroundColor Red }
}

# A prompt of the shape AutoPrompt is asked for: numbered sections, a glossary
# table, an OUTPUT FORMAT section. Cutting this simulates a truncation - the
# real one ended mid-glossary-row.
$good = @'
## 1. ROLE

You are translating a technical document from English into Dutch. Keep the
register formal and the terminology consistent with the table below.

## 2. TERMINOLOGY

| Source | Target | Note |
| --- | --- | --- |
| widget | onderdeel | as used throughout |
| flange | flens | standard term |

## 3. OUTPUT FORMAT

Return one numbered line per segment, in the order given, with no commentary
before or after the list.
'@

# ---- ExtractDraft ---------------------------------------------------------
$bridge = $plugin.GetType('Supervertaler.MemoQ.Core.MemoQBridge')
if ($null -eq $bridge) { throw 'MemoQBridge not found.' }
# (string raw, string stopped) - the provider's own stop reason, so a refusal
# can say the model hit its output limit instead of guessing at a cause.
$extract = $bridge.GetMethod('ExtractDraft', $NonPublicStatic, $null, [type[]]@([string], [string]), $null)
if ($null -eq $extract) { throw 'ExtractDraft not found - has it been renamed?' }

function Extract([string]$raw, [string]$stopped = $null) {
    $a = [object[]]::new(2)
    $a[0] = $raw
    $a[1] = $stopped
    return $extract.Invoke($null, $a)
}

$wrapped = "Here is your prompt:`n===PROMPT_START===`n$good`n===PROMPT_END===`nHope that helps."
$got = Extract $wrapped
Check 'both delimiters: the body is taken, the chatter is not' `
    ($got -match 'OUTPUT FORMAT' -and $got -notmatch 'Hope that helps' -and $got -notmatch 'PROMPT_START')

$got = Extract $good
Check 'no delimiters: falls back to the raw answer' ($got -eq $good)

# The truncation: a start, and then the answer stops. This is what used to be
# saved silently, delimiter line and all.
$truncated = "===PROMPT_START===`n## 1. ROLE`n`nYou are translating`n`n## 2. TERMINOLOGY`n`n| Source | Target |`n| --- | --- |`n| widget | onder"
$threw = $false
$message = ''
try { Extract $truncated | Out-Null } catch {
    $threw = $true
    $inner = $_.Exception.InnerException
    $message = if ($inner) { $inner.Message } else { $_.Exception.Message }
}
Check 'start with no end: refused, not saved' $threw
Check 'the refusal says it was cut off' ($message -match 'cut off') $message

$withReason = ''
try { Extract $truncated 'max_tokens' | Out-Null } catch {
    $inner = $_.Exception.InnerException
    $withReason = if ($inner) { $inner.Message } else { $_.Exception.Message }
}
Check 'and names the cause when the provider gave one' `
    ($withReason -match 'output limit' -and $withReason -match 'max_tokens') $withReason
Check 'and invents no cause when it did not' ($message -notmatch 'output limit') $message

$otherReason = ''
try { Extract $truncated 'content_filter' | Out-Null } catch {
    $inner = $_.Exception.InnerException
    $otherReason = if ($inner) { $inner.Message } else { $_.Exception.Message }
}
Check 'a stop reason that is not a limit is not reported as one' `
    ($otherReason -notmatch 'output limit') $otherReason

# ---- PromptValidator ------------------------------------------------------
$validator = $plugin.GetType('Supervertaler.Core.PromptValidator')
if ($null -eq $validator) { throw 'PromptValidator not found in the merged assembly.' }
$validate = $validator.GetMethod('Validate', $PublicStatic, $null, [type[]]@([string]), $null)

function Validate([string]$text) {
    $a = [object[]]::new(1)
    $a[0] = $text
    return $validate.Invoke($null, $a)
}

$ok = Validate $good
Check 'a complete prompt passes' $ok.Ok ($ok.Describe())

$body = $truncated -replace '===PROMPT_START===\s*', ''
$bad = Validate $body
Check 'the truncated prompt is refused' (-not $bad.Ok)
Check 'and the reason names a real check' `
    ($bad.Describe() -match 'OUTPUT FORMAT|table|row|truncated') ($bad.Describe())

$empty = Validate ''
Check 'an empty prompt is refused' (-not $empty.Ok)

# Cut the sample at several points. Three of the four are caught; the fourth is
# a known gap, asserted here rather than glossed over, so that the day core
# closes it this test fails and says so.
#
# The gap: a cut landing INSIDE the body of the final section - after OUTPUT
# FORMAT already exists, with more than ~40 characters under the last heading,
# and not inside a table - passes every check. A prompt that stops mid-sentence
# in its last paragraph is exactly that shape, and it is the shape a memoQ
# AutoPrompt is most likely to hit, since OUTPUT FORMAT is not the last section
# it is asked for. The fix both products want is to validate the output against
# the section list the profile asked for; that needs the template threaded into
# the validation call. Raised with the Trados side.
$expected = @{ 0.3 = $true; 0.5 = $true; 0.7 = $true; 0.9 = $false }
foreach ($fraction in 0.3, 0.5, 0.7, 0.9) {
    $cut = $good.Substring(0, [int]($good.Length * $fraction))
    $refused = -not (Validate $cut).Ok
    $name = if ($expected[$fraction]) { "a cut at $fraction is refused" }
            else { "a cut at $fraction is NOT refused - the known final-body gap" }
    Check $name ($refused -eq $expected[$fraction])
}

# ---- the section list the meta-prompt asked for ---------------------------
# Validate(prompt, requestedSections) refuses a prompt that is missing a RUN of
# sections off the end, and names them. A section renamed or merged in the
# middle is tolerated: the model has lost nothing, and refusing a paraphrase
# would cost a regeneration.
#
# Measured while wiring this up, because it bounds what the check buys here:
# all seven domain templates end with OUTPUT FORMAT, so a plain tail cut
# already trips the output-format check on its own. What this adds is a
# refusal that says WHICH sections went missing, and a search of the text
# rather than of the numbered run of headings.
$sectionsFor = $plugin.GetType('Supervertaler.Core.PromptGenerator').GetMethod(
    'SectionsFor', $PublicStatic, $null, [type[]]@([string]), $null)
$a = [object[]]::new(1); $a[0] = 'general'
$requested = $sectionsFor.Invoke($null, $a)
Check 'the general profile asks for sections' ($requested.Count -gt 5)
Check 'and OUTPUT FORMAT is the last of them' `
    ($requested[$requested.Count - 1] -match 'OUTPUT FORMAT') $requested[$requested.Count - 1]

$validateWith = $validator.GetMethod('Validate', $PublicStatic, $null,
    [type[]]@([string], [System.Collections.Generic.IList[string]]), $null)
function ValidateAgainst([string]$text, $sections) {
    $a = [object[]]::new(2)
    $a[0] = $text
    $a[1] = $sections
    return $validateWith.Invoke($null, $a)
}

# A prompt carrying every requested heading, in order, each with a body.
function Compose($sections, [int]$howMany) {
    $sb = New-Object System.Text.StringBuilder
    for ($i = 0; $i -lt $howMany; $i++) {
        [void]$sb.AppendLine("## $($i + 1). $($sections[$i])")
        [void]$sb.AppendLine()
        [void]$sb.AppendLine("Body text for this section, long enough to count as a body rather than a bare heading.")
        [void]$sb.AppendLine()
    }
    return $sb.ToString()
}

$whole = Compose $requested $requested.Count
Check 'a prompt with every requested section passes' (ValidateAgainst $whole $requested).Ok `
    ((ValidateAgainst $whole $requested).Describe())

$lostTail = Compose $requested ($requested.Count - 3)
$r = ValidateAgainst $lostTail $requested
Check 'a prompt missing the last three sections is refused' (-not $r.Ok)
Check 'and the refusal names them' ($r.Describe() -match 'never arrived') ($r.Describe())

# A middle section renamed: everything still arrived as far as the tail goes.
$renamed = $whole -replace [regex]::Escape($requested[3]), 'SOMETHING THE MODEL CALLED IT INSTEAD'
$r = ValidateAgainst $renamed $requested
Check 'a section renamed in the middle is tolerated' $r.Ok ($r.Describe())

Write-Host ''
Write-Host ("AUTOPROMPT TRUNCATION TEST COMPLETE - {0} passed, {1} failed" -f $pass, $fail)
if ($fail -gt 0) { exit 1 }
