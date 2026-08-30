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

    # Deploy every DLL staged beside this script, not just $PluginDll: the plugin
    # ships as two assemblies (MT engine and terminology provider) because memoQ
    # loads one module per DLL.
    $stage = Split-Path $PluginDll -Parent
    $dlls  = Get-ChildItem -LiteralPath $stage -Filter 'Supervertaler.MemoQ*.dll'
    if (-not $dlls) { throw "No Supervertaler DLLs found in $stage" }

    foreach ($dll in $dlls) {
        $target = Join-Path $addins $dll.Name
        Copy-Item -LiteralPath $dll.FullName -Destination $target -Force
        $info = Get-Item -LiteralPath $target
        Say "OK  $target"
        Say "    $($info.Length) bytes, written $($info.LastWriteTime)"
    }
    exit 0
}
catch {
    Say "FAIL  $($_.Exception.Message)"
    exit 1
}
