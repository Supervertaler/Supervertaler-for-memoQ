# Fetching and installing the MCP server: the half of the button that can be
# tested without touching anything of the user's.
#
# tools/codex-config-test.ps1 pins the text written into ChatGPT's config, which
# is the part that can silently corrupt somebody else's file. This is the other
# half - resolve the release asset, download it, unpack it, and swap it into
# place - and until now nothing had exercised it at all.
#
# It never calls RunAsync, because RunAsync writes the real config. It calls
# DownloadServerAsync directly, with a ServerDir pointing at a scratch folder,
# so the user's own server and their ChatGPT configuration are untouched either
# way. It does reach the network and downloads tens of megabytes, twice, which
# is why it is not part of build.sh.
$ErrorActionPreference = 'Stop'
$EditorExe = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.PromptEditor\bin\Release\Supervertaler.PromptEditor.exe'
$Scratch = Join-Path $env:TEMP ('sv-mcp-download-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))

Add-Type -AssemblyName System.Windows.Forms | Out-Null
$asm = [Reflection.Assembly]::LoadFrom($EditorExe)
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'

$setup = $asm.GetType('Supervertaler.Core.ChatGptMcpSetup')
$optType = $asm.GetType('Supervertaler.Core.ChatGptMcpSetup+Options')
$download = $setup.GetMethod('DownloadServerAsync', $Static)
$exePathOf = $setup.GetMethod('ServerExePath', $Static)
$versionOf = $setup.GetMethod('InstalledServerVersion', $Static)

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

# The real memoQ options in every respect except where the server lands. The
# config file is never opened, so the block name and the environment are along
# for the ride only.
$opt = [Activator]::CreateInstance($optType)
$opt.ProductName = 'memoQ'
$opt.BlockName = 'mcp_servers.supervertaler_memoq'
$opt.ServerDir = $Scratch
$opt.BlockComment = '# test'
$opt.Log = [Action[string]]{ param($m) Write-Host "    log: $m" }

$exe = $exePathOf.Invoke($null, [object[]]@($opt))

function Download() {
    $task = $download.Invoke($null, [object[]]@($opt))
    return $task.GetAwaiter().GetResult()
}

try {
    # ---- 1. a first install, into a folder that does not exist yet --------
    Write-Host 'first install:'
    $error1 = Download
    Check ($null -eq $error1) "the download reports no error: $error1"
    Check (Test-Path $exe) 'the server is on disk'

    if (Test-Path $exe) {
        $size = (Get-Item $exe).Length
        Check ($size -gt 1MB) "and is a real executable, not an error page: $([int]($size / 1MB)) MB"

        $version = $versionOf.Invoke($null, [object[]]@($opt))
        Check ($null -ne $version) "its version can be read: $version"
    }

    # The staged and retired files are cleanup, and cleanup that only happens
    # on the paths we thought of is the thing that leaks.
    Check (-not (Test-Path ($exe + '.new'))) 'no staged file is left behind'
    Check (-not (Test-Path ($exe + '.old'))) 'no retired file is left behind'
    Check (-not (Test-Path (Join-Path $Scratch 'Supervertaler-MCP-Server-exe.zip'))) `
          'and the zip it came in is gone'

    # ---- 2. a second run, over a server that is already there ------------
    # This is the path a user reaches by pressing the button twice, and the one
    # that fails with "the process cannot access the file" if the install
    # overwrites in place rather than renaming.
    Write-Host ''
    Write-Host 'second install, over the first:'
    $sizeBefore = (Get-Item $exe).Length

    $error2 = Download
    Check ($null -eq $error2) "the second download reports no error: $error2"
    Check (Test-Path $exe) 'the server is still on disk'

    # The failure this guards against is a swap that leaves a truncated or
    # unreadable file where a working server used to be - which would look
    # exactly like success from the outside. So check the file itself, not a
    # timestamp: the two downloads are the same asset, so the size must match
    # and the version must still read.
    Check ((Get-Item $exe).Length -eq $sizeBefore) `
          "the replacement is the same size as what it replaced: $((Get-Item $exe).Length)"
    Check ($null -ne $versionOf.Invoke($null, [object[]]@($opt))) `
          'and its version still reads, so it is not a truncated file'
    Check (-not (Test-Path ($exe + '.new'))) 'no staged file after the replacement'
    Check (-not (Test-Path ($exe + '.old'))) 'and the retired one was swept'

    $left = @(Get-ChildItem $Scratch -File)
    Check ($left.Count -eq 1) "one file in the folder when it is done, not a pile: $($left.Count)"

    # ---- 3. the real folder was not touched ------------------------------
    # The whole point of running this against a scratch directory.
    Check ($Scratch -notmatch 'Supervertaler\\memoq') "the test ran in a scratch folder: $Scratch"
}
finally {
    # On the failure path too: a harness that leaves tens of megabytes in TEMP
    # when it throws is its own small version of the bug it is testing for.
    try { Remove-Item $Scratch -Recurse -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ''
if ($fails -gt 0) { Write-Host "$fails failed"; exit 1 }
Write-Host 'all passed'
