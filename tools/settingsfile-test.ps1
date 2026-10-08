# The settings files the plugin (inside memoQ) and the editor share - shared.txt
# above all - are written through core's AtomicFile (SettingsFile), never in
# place.
#
# Written in place, a read that landed mid-write saw half a file, and shared.txt
# is read, changed by one line and written back: the half file was then saved
# as the whole one and the rest of the settings were gone. This test has a
# second PROCESS write a complete file over and over, as the other half of the
# product would, while this one reads it: every read must see a whole file.
#
# Runs in a temporary folder. Never touches the real settings.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$PluginDll = 'D:\SynologyDrive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll'

$plugin = [Reflection.Assembly]::LoadFrom($PluginDll)
$B = [Reflection.BindingFlags]'Public,NonPublic,Static'
$sf = $plugin.GetType('Supervertaler.MemoQ.Core.SettingsFile')
$read = $sf.GetMethod('ReadAllText', $B)
$write = $sf.GetMethod('WriteAllText', $B)

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

$dir = Join-Path ([IO.Path]::GetTempPath()) ('sv-settingsfile-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null
$file = [string](Join-Path $dir 'shared.txt')

# A settings file the size of a real one: 40 keys, and an end marker a torn
# read cannot have.
$lines = 1..40 | ForEach-Object { "key$_=value number $_ with some length to it" }
$whole = [string]((($lines -join "`r`n") + "`r`nend=1`r`n"))

function Torn([string]$text) { return -not ($text.EndsWith("end=1`r`n") -and ($text -split "`r`n").Count -eq 42) }

try {
    # ---- 1. byte-order mark as asked, and read back without it ---------------
    $write.Invoke($null, [object[]]@($file, $whole, $true))
    $bytes = [IO.File]::ReadAllBytes($file)
    Check ($bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) "written with a byte-order mark when asked (shared.txt keeps its own)"
    Check ($read.Invoke($null, @($file)) -eq $whole) "  and read back exactly, without it"
    $write.Invoke($null, [object[]]@($file, $whole, $false))
    Check ([IO.File]::ReadAllBytes($file)[0] -eq [byte][char]'k') "and without one when not"

    # ---- 2. a reader holding the file open does not stop a write -------------
    $held = [IO.FileStream]::new($file, 'Open', 'Read', [IO.FileShare]'ReadWrite, Delete')
    try {
        $write.Invoke($null, [object[]]@($file, [string]($whole + "extra=1`r`n"), $false))
        Check $true "a write goes through while a reader has the file open"
    } catch { Check $false "a write goes through while a reader has the file open: $($_.Exception.InnerException.Message)" }
    finally { $held.Dispose() }
    Check (($read.Invoke($null, @($file))) -match 'extra=1') "  and the new content is what is read next"

    # ---- 3. a write that cannot be made throws, and changes nothing ----------
    $write.Invoke($null, [object[]]@($file, $whole, $false))
    $lock = [IO.FileStream]::new($file, 'Open', 'Read', [IO.FileShare]::None)
    $threw = $false
    try { $write.Invoke($null, [object[]]@($file, 'replacement', $false)) }
    catch { $threw = $_.Exception.InnerException -is [IO.IOException] }
    finally { $lock.Dispose() }
    Check $threw "a write that cannot be made throws IOException, as File.WriteAllText did, so callers' handling still applies"
    Check (([IO.File]::ReadAllText($file)) -eq $whole) "  and the previous file is left exactly as it was"
    Check (@(Get-ChildItem $dir -Filter '*.tmp').Count -eq 0) "  and no temporary file is left behind"

    # ---- 4. another process writing while this one reads ---------------------
    function Hammer([bool]$atomic, [int]$ms) {
        $writer = Start-Job -ArgumentList $PluginDll, $file, $whole, $atomic, $ms -ScriptBlock {
            param($dll, $file, $whole, $atomic, $ms)
            $asm = [Reflection.Assembly]::LoadFrom($dll)
            $w = $asm.GetType('Supervertaler.MemoQ.Core.SettingsFile').GetMethod('WriteAllText', [Reflection.BindingFlags]'Public,NonPublic,Static')
            $until = [DateTime]::UtcNow.AddMilliseconds($ms); $n = 0
            while ([DateTime]::UtcNow -lt $until) {
                try {
                    if ($atomic) { $w.Invoke($null, [object[]]@($file, $whole, $true)) }
                    else { [IO.File]::WriteAllText($file, $whole, [Text.Encoding]::UTF8) }
                    $n++
                } catch { }
            }
            $n
        }
        # Wait for the writer process to be writing before reading.
        $start = (Get-Item $file).LastWriteTimeUtc
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while ((Get-Item $file).LastWriteTimeUtc -eq $start -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 20 }

        $reads = 0; $torn = 0; $errors = 0
        while ($writer.State -eq 'Running') {
            try {
                $text = if ($atomic) { $read.Invoke($null, @($file)) } else { [IO.File]::ReadAllText($file, [Text.Encoding]::UTF8) }
                $reads++
                if (Torn $text) { $torn++ }
            } catch { $errors++ }
        }
        $writes = [int](@(Receive-Job $writer -Wait)[-1]); Remove-Job $writer
        return @{ Reads = $reads; Torn = $torn; Errors = $errors; Writes = $writes }
    }

    $write.Invoke($null, [object[]]@($file, $whole, $true))
    $r = Hammer $true 4000
    Check ($r.Writes -gt 100 -and $r.Reads -gt 100) "atomic: another process wrote $($r.Writes) times while this one read $($r.Reads) times"
    Check ($r.Torn -eq 0) "  and no read ever saw a half-written file ($($r.Torn) torn)"
    Check ($r.Errors -eq 0) "  and no read failed ($($r.Errors) errors)"

    # The control: the same race with the in-place write that was replaced. Not
    # a check - a race may not show in a short run - but when it shows, it is
    # the evidence that the hammer above can see what it is looking for.
    $c = Hammer $false 4000
    Write-Host "INFO in-place writes, for comparison: $($c.Torn) torn and $($c.Errors) failed reads out of $($c.Reads + $c.Errors), against $($c.Writes) writes"
}
finally {
    Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "SETTINGS FILE TEST COMPLETE - $fails failure(s)"
