# The entry Supervertaler writes into ChatGPT's own configuration file.
#
# This edits a file another application owns, and which may already carry the
# user's own servers and Supervertaler for Trados's entry beside ours. Getting it
# wrong does not fail loudly - it silently detaches somebody's other MCP server,
# or leaves two copies of ours, or strands an env section under the wrong block.
# So the two pure functions that decide the text are pinned here.
#
# Nothing in this file touches the real config: it exercises the text functions
# only, with strings made up on the spot. It loads the editor rather than the
# plugin, starts no bridge and is safe to run with memoQ open.
$ErrorActionPreference = 'Stop'
$EditorExe = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe'

Add-Type -AssemblyName System.Windows.Forms | Out-Null
$asm = [Reflection.Assembly]::LoadFrom($EditorExe)
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'

$setup  = $asm.GetType('Supervertaler.Core.ChatGptMcpSetup')
$dialog = $asm.GetType('Supervertaler.PromptEditor.ChatGptSetupDialog')

$buildBlock = $setup.GetMethod('BuildBlock', $Static)
$removeOurs = $setup.GetMethod('RemoveOurBlock', $Static)
$optionsOf  = $dialog.GetMethod('Options', $Static)

# The real memoQ options, not a copy typed here - the point is to pin what the
# product actually writes.
$opt = $optionsOf.Invoke($null, @())

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}
function Block() { return $buildBlock.Invoke($null, [object[]]@($opt)) }
function Remove($text) { return $removeOurs.Invoke($null, [object[]]@($text, $opt.BlockName)) }

# What WriteConfig does to the file, with the file I/O taken out. Kept in step
# with it by hand; if that changes, change this.
function Apply($existing) {
    $updated = Remove $existing
    if ($updated.Length -gt 0 -and -not $updated.EndsWith("`n")) { $updated += "`n" }
    return $updated + (Block)
}

# ---- 1. the options are memoQ's, and distinct from Trados's ---------------
Check ($opt.BlockName -eq 'mcp_servers.supervertaler_memoq') `
      "the block is named for this product: $($opt.BlockName)"
Check ($opt.BlockName -ne 'mcp_servers.supervertaler') `
      'the block is not the Trados one, so registering both keeps both'
Check ($opt.Environment['SUPERVERTALER_HOST'] -eq 'memoq') `
      'the server is told which CAT tool it is for'
Check ($opt.ServerDir -notmatch 'Addins') `
      "the server is not kept in memoQ's Addins folder: $($opt.ServerDir)"
Check ($opt.ProductName -eq 'memoQ') 'the product name is what the messages will say'

# ---- 2. the block itself --------------------------------------------------
$block = Block
Check ($block -match '(?m)^\[mcp_servers\.supervertaler_memoq\]$') 'the block has its header'
Check ($block -match "(?m)^command = '.*SupervertalerMcpServer\.exe'$") `
      'the command is a TOML literal string, so Windows backslashes need no escaping'
Check ($block -match '(?m)^env = \{ SUPERVERTALER_HOST = "memoq" \}$') `
      'the environment is an INLINE table'
Check ($block -notmatch '\[mcp_servers\.supervertaler_memoq\.env\]') `
      'and never a sub-table, which the remover would leave behind'
Check ($block -match '(?m)^enabled = true$') 'the server is enabled'

# ---- 3. removal leaves everything else alone ------------------------------
# The realistic case: the user has a server of their own, and Trados's entry.
$existing = @"
[mcp_servers.someone_elses]
command = 'C:\tools\other.exe'

# Supervertaler for Trados - live connection to the open Studio session.
[mcp_servers.supervertaler]
type = "stdio"
command = 'D:\Supervertaler\trados\mcp\SupervertalerMcpServer.exe'
enabled = true

[some_other_section]
key = "value"
"@

$after = Apply $existing
Check ($after -match '\[mcp_servers\.someone_elses\]') "a user's own server survives"
Check ($after -match "C:\\tools\\other\.exe") "and so does its command line"
Check ($after -match '\[mcp_servers\.supervertaler\]') "the Trados entry survives"
Check ($after -match 'trados\\mcp\\SupervertalerMcpServer\.exe') 'including its own server path'
Check ($after -match '\[some_other_section\]') 'an unrelated section survives'
Check ($after -match '\[mcp_servers\.supervertaler_memoq\]') 'and ours is now there'

# The prefix trap: our name starts with Trados's, so a StartsWith test that
# forgot the closing bracket would eat the Trados block every time.
$tradosBlocks = ([regex]::Matches($after, '(?m)^\[mcp_servers\.supervertaler\]$')).Count
Check ($tradosBlocks -eq 1) "the Trados block appears exactly once: $tradosBlocks"

# ---- 4. running it twice does not leave two -------------------------------
$twice = Apply (Apply $existing)
$ours = ([regex]::Matches($twice, '(?m)^\[mcp_servers\.supervertaler_memoq\]$')).Count
Check ($ours -eq 1) "pressing the button twice leaves one entry, not two: $ours"
Check ($twice -eq $after) 'and the file is byte for byte what one run produced'

$envLines = ([regex]::Matches($twice, '(?m)^env = \{')).Count
Check ($envLines -eq 1) "one env line, not one per run: $envLines"

# ---- 5. our own comments do not pile up -----------------------------------
$comments = ([regex]::Matches($twice, '(?m)^# Supervertaler for memoQ')).Count
Check ($comments -eq 1) "our comment is replaced, not repeated: $comments"

# ---- 6. the empty and absent cases ----------------------------------------
Check ((Remove '') -eq '') 'removing from nothing gives nothing'
$fresh = Apply ''
Check ($fresh -match '\[mcp_servers\.supervertaler_memoq\]') 'a first run writes the block into an empty file'
Check (-not $fresh.StartsWith("`n`n")) 'and does not open the file with a run of blank lines'

# ---- 7. a block that is ours but stale ------------------------------------
# An older entry with a different server path, and a section after it. The old
# block must go entirely, and the section after it must not.
$stale = @"
# Supervertaler for memoQ - live connection to the open memoQ project.
# Local stdio server; it reaches memoQ on this machine only.
[mcp_servers.supervertaler_memoq]
type = "stdio"
command = 'C:\Somewhere\Old\SupervertalerMcpServer.exe'
env = { SUPERVERTALER_HOST = "memoq" }
enabled = true

[mcp_servers.kept]
command = 'C:\kept.exe'
"@

$replaced = Apply $stale
Check ($replaced -notmatch 'Somewhere\\Old') 'the old server path is gone'
Check ($replaced -match '\[mcp_servers\.kept\]') 'the block after it is kept'
Check ($replaced -match 'C:\\kept\.exe') 'with its contents'

Write-Host ''
if ($fails -gt 0) { Write-Host "$fails failed"; exit 1 }
Write-Host 'all passed'
