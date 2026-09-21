# The help card against the tool list.
#
# The card is what an assistant shows when the user asks what they can do, so it
# is the one piece of text describing the whole feature set - and the one most
# likely to rot, because nothing breaks when it does. It had been written before
# the live document link, the QA checks and the termbases existed, and still said
# the cursor could not be moved and that terms went into a glossary that no
# longer exists. A user's assistant noticed the contradiction between the card
# and the tool list and told him the help was outdated. Being caught by the model
# is luckier than it is repeatable.
#
# So: every tool has to be represented in the card, and the card must not claim
# it cannot do something a tool plainly does.
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$Repo = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ'
$PluginDll = Join-Path $Repo 'src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll'
$ToolsJson = Join-Path $Repo 'src\Supervertaler.MemoQ\Resources\mcp-tools.json'

# The plugin is compiled against memoQ's own assemblies, which live in memoQ's
# folder rather than beside the DLL. The in-flight set is not decoration: without
# it a name this resolver cannot find sends it round again and the process dies
# with a StackOverflowException, which no catch can reach.
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

$asm = [Reflection.Assembly]::LoadFrom($PluginDll)
$flags = [Reflection.BindingFlags]'Public,NonPublic,Static'

# Found rather than assumed: the constant has moved class once already.
# GetTypes throws when any single type will not load, and hands back the ones
# that did on the exception - which is enough, since the card is on a type with
# no memoQ types in its signature.
$card = $null
try { $types = $asm.GetTypes() }
catch [Reflection.ReflectionTypeLoadException] { $types = @($_.Exception.Types | Where-Object { $_ }) }

foreach ($t in $types) {
    $f = $t.GetField('HelpCard', $flags)
    if ($f -and $f.IsLiteral) { $card = $f.GetRawConstantValue(); break }
}

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

Check ($null -ne $card) 'the help card is in the built plugin'
if ($null -eq $card) { Write-Host ''; Write-Host '1 failed'; exit 1 }

$tools = (Get-Content $ToolsJson -Raw | ConvertFrom-Json).tools
if (-not $tools) { $tools = (Get-Content $ToolsJson -Raw | ConvertFrom-Json) }
$names = @($tools | ForEach-Object { $_.name })
Check ($names.Count -gt 0) "the tool list was read: $($names.Count) tools"

# What the card must SAY for each tool, in the user's words rather than the
# tool's name - the card is for a translator, not for a developer. A tool with no
# entry here is a tool nobody decided how to describe, which is the gap this
# catches: add the tool, and this fails until the card mentions it.
$mustMention = @{
    'get_project'            = 'project'
    'get_segments'           = 'segments'
    'get_active_segment'     = 'segment am I on'
    'go_to_segment'          = 'Take me to'
    'check_numbers'          = 'numbers'
    'check_tags'             = 'tags'
    'check_nbsp'             = 'non-breaking'
    'check_terminology'      = 'terminology'
    'find_inconsistencies'   = 'inconsistencies'
    'get_confirmed_pairs'    = 'confirmed'
    'lookup_term'            = 'Look up a term'
    'add_term'               = 'Add a term'
    'stage_translations'     = 'staged'
    'get_staged'             = "What's staged"
    'compare_staged_to_grid' = 'actually landed'
    'clear_staged'           = 'Clear the staging'
    'list_prompts'           = 'prompts'
    'get_prompt'             = 'prompts'
    'save_prompt'            = 'save'
    'list_supermemory_banks' = 'SuperMemory'
    'get_supermemory_context' = 'SuperMemory'
    'search_supermemory'     = 'SuperMemory'
    'help'                   = ''      # the card itself
}

foreach ($name in $names) {
    if (-not $mustMention.ContainsKey($name)) {
        Check $false "the card has no wording decided for the tool '$name'"
        continue
    }
    $phrase = $mustMention[$name]
    if ([string]::IsNullOrEmpty($phrase)) { continue }
    Check ($card -like "*$phrase*") "the card covers '$name' (looks for: $phrase)"
}

# And nothing in the map that is no longer a tool, so the map cannot quietly
# become a list of things that used to exist.
foreach ($name in $mustMention.Keys) {
    Check ($names -contains $name) "'$name' is still a real tool"
}

# ---- the card must not contradict the tools -------------------------------
# This is the failure that actually happened. The closing paragraph lists what
# the bridge cannot do, and it named the cursor while go_to_segment shipped.
$cannot = ($card -split 'cannot do:')[-1]

Check ($names -notcontains 'go_to_segment' -or $cannot -notmatch 'cursor') `
    'the card does not say it cannot move the cursor while it has a tool that does'
Check ($names -notcontains 'lookup_term' -or $cannot -notmatch 'termbase') `
    'and does not say it cannot read termbases while it has a tool that does'
Check ($names -notcontains 'get_segments' -or $cannot -notmatch 'read the document') `
    'and does not say it cannot read the document'

# The glossaries were removed and replaced by termbases. The card outlived them
# by a fortnight, telling users their terms went somewhere that no longer exists.
Check ($card -notmatch 'glossary|glossaries') 'the card does not send the user to the glossaries, which are gone'

# The one thing the card exists to explain, per its own tool description.
Check ($card -match 'staged') 'the card explains that translations are staged'
Check ($card -match 'Pre-translate') 'and names what makes them land'

Write-Host ''
if ($fails -gt 0) { Write-Host "$fails failed"; exit 1 }
Write-Host 'all passed'
