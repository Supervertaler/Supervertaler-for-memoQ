# Copies the built add-in into memoQ's Addins folder.
#
# Must run elevated: memoQ probes for add-ins in its own install directory (see
# memoQ.exe.config, privatePath="DocConverters;Addins;CefSharp;x64") and there is
# no per-user equivalent. build.sh calls this, re-launching itself via UAC if the
# folder is not writable.
#
#   powershell -ExecutionPolicy Bypass -File tools\deploy.ps1 [-LogFile <path>]

param(
    [string]$PluginDll = "$PSScriptRoot\..\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll",
    [string]$MemoQPath = '',
    [string]$LogFile   = ''
)

$ErrorActionPreference = 'Stop'

function Say($message) {
    Write-Host $message
    if ($LogFile) {
        # Not Add-Content -Encoding UTF8: Windows PowerShell 5 prefixes a BOM,
        # which then defeats an anchored ^OK match in the calling script.
        [System.IO.File]::AppendAllText($LogFile, $message + "`r`n", (New-Object System.Text.UTF8Encoding($false)))
    }
}

# Copies one file and then checks it arrived. Copy-Item raising nothing is not
# proof: the caller reads this log to decide whether what it built is what is on
# disk, and "OK" has to mean the bytes match.
function Deploy($source, $target) {
    Copy-Item -LiteralPath $source -Destination $target -Force

    $from = Get-Item -LiteralPath $source
    $to   = Get-Item -LiteralPath $target
    if ($to.Length -ne $from.Length) {
        throw "$target is $($to.Length) bytes, expected $($from.Length) - the copy did not take"
    }

    Say "OK  $target"
    Say "    $($to.Length) bytes, written $($to.LastWriteTime)"
}

try {
    # Newest installed memoQ wins, so a machine with 12 and 13 side by side gets 13.
    if (-not $MemoQPath) {
        $found = Get-ChildItem 'C:\Program Files\memoQ' -Directory -Filter 'memoQ-*' -ErrorAction SilentlyContinue |
                 Sort-Object { [int](($_.Name -split '-')[-1]) } |
                 Select-Object -Last 1
        if (-not $found) { throw 'No memoQ installation found under C:\Program Files\memoQ' }
        $MemoQPath = $found.FullName
    }

    $addins = Join-Path $MemoQPath 'Addins'
    if (-not (Test-Path $addins))    { throw "Addins folder not found: $addins" }
    if (-not (Test-Path $PluginDll)) { throw "Plugin not built: $PluginDll" }

    if (Get-Process -Name 'memoQ' -ErrorAction SilentlyContinue) {
        throw 'memoQ is running — it locks the add-in DLL. Close it and retry.'
    }

    # Before the first copy, not when its turn comes: a run that deploys the
    # DLLs and then dies on the editor leaves the two halves at different
    # versions, which is harder to notice than either failing outright.
    if (Get-Process -Name 'Supervertaler.PromptEditor' -ErrorAction SilentlyContinue) {
        throw 'The prompt editor is running - it locks its own exe. Close it and retry.'
    }

    # Deploy every DLL staged beside this script, not just $PluginDll: the plugin
    # ships as two assemblies (MT engine and terminology provider) because memoQ
    # loads one module per DLL.
    $stage = Split-Path $PluginDll -Parent
    $dlls  = Get-ChildItem -LiteralPath $stage -Filter 'Supervertaler.MemoQ*.dll'
    if (-not $dlls) { throw "No Supervertaler DLLs found in $stage" }

    foreach ($dll in $dlls) {
        Deploy $dll.FullName (Join-Path $addins $dll.Name)
    }

    # The prompt editor, if it was staged. memoQ never loads this one — it is a
    # standalone exe — but it lives in Addins so the options dialog can find it
    # next to its own assembly. Not fatal when absent: the dialog says so, and a
    # deploy that skipped it still leaves a working add-in.
    $editor = Join-Path $stage 'Supervertaler.PromptEditor.exe'
    if (Test-Path -LiteralPath $editor) {
        Deploy $editor (Join-Path $addins 'Supervertaler.PromptEditor.exe')
    }
    else {
        Say "--  prompt editor not staged; skipped"
    }

    exit 0
}
catch {
    Say "FAIL  $($_.Exception.Message)"
    exit 1
}
