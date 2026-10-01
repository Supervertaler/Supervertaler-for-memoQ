# The update check (Core/UpdateCheck.cs): reading GitHub's answer, deciding what
# to offer, and refusing an installer that is not the one GitHub published.
#
# No request goes to GitHub: the release JSON is a copy of the real v0.1.1
# answer. The one download attempted is to a closed local port, to prove a
# failed download leaves no file behind. Run through tools/run-harness.ps1.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$script:probed = @{}
[AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler] {
    param($s, $e)
    $name = ($e.Name -split ',')[0]
    if ($script:probed.ContainsKey($name)) { return $null }
    $script:probed[$name] = $true
    foreach ($dir in @($MemoQPath, "$MemoQPath\Addins")) {
        $c = Join-Path $dir "$name.dll"
        if (Test-Path $c) { try { return [Reflection.Assembly]::LoadFrom($c) } catch { return $null } }
    }
    return $null
})
$plugin = [Reflection.Assembly]::LoadFrom('D:\SynologyDrive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll')
$Static = [Reflection.BindingFlags]'Public,NonPublic,Static'
$fails = 0
function Check($ok, $label) { if (-not $ok) { $script:fails++ }; Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label" }

$uc   = $plugin.GetType('Supervertaler.MemoQ.Core.UpdateCheck')
$info = $plugin.GetType('Supervertaler.MemoQ.Core.ReleaseInfo')
function Call($name, [object[]]$argv) {
    $m = $uc.GetMethods($Static) | Where-Object { $_.Name -eq $name } | Select-Object -First 1
    return $m.Invoke($null, $argv)
}
function FromGitHub([string]$json) { $a = New-Object object[] 2; $a[0] = $json; $a[1] = [DateTime]::UtcNow; return Call 'FromGitHub' $a }
function Newer([string]$a, [string]$b) { $x = New-Object object[] 2; $x[0] = $a; $x[1] = $b; return [bool](Call 'IsNewer' $x) }

$digest = 'sha256:9b9d3063bb504fdf77e7b40c998c3e8c73cc4d284eb4e546e63aa4667eedd6e9'
$real = @"
{"url":"https://api.github.com/repos/x","html_url":"https://github.com/Supervertaler/Supervertaler-for-memoQ/releases/tag/v0.1.1",
 "tag_name":"v0.1.1","draft":false,"prerelease":false,"author":{"login":"x","id":1},
 "assets":[
  {"name":"Supervertaler-for-memoQ-0.1.1.0.zip","size":33350308,"digest":"sha256:b6","browser_download_url":"https://github.com/z.zip","uploader":{"login":"x"}},
  {"name":"Supervertaler-for-memoQ-Setup.exe","size":33108139,"digest":"$digest","browser_download_url":"https://github.com/Supervertaler/Supervertaler-for-memoQ/releases/download/v0.1.1/Supervertaler-for-memoQ-Setup.exe"}
 ],"body":"notes"}
"@

# ---- 1. reading GitHub's answer ------------------------------------------------
$r = FromGitHub $real
Check ($r -ne $null -and $r.Version -eq '0.1.1') "the version comes from the tag: $($r.Version)"
Check ($r.SetupSize -eq 33108139 -and $r.SetupDigest -eq $digest) 'the installer is the fixed-name asset, with its size and digest'
Check ($r.SetupUrl -like '*/v0.1.1/Supervertaler-for-memoQ-Setup.exe') 'and its own download address, not the zip'
Check ($r.NotesUrl -like '*/tag/v0.1.1') "What's new opens the release page"
Check ($null -eq (FromGitHub ($real -replace '"draft":false', '"draft":true'))) 'a draft is never offered'
Check ($null -eq (FromGitHub ($real -replace '"prerelease":false', '"prerelease":true'))) 'nor a pre-release'
Check ($null -eq (FromGitHub ($real -replace 'Supervertaler-for-memoQ-Setup.exe"', 'Other.exe"'))) 'a release without the installer is not offered'
Check ($null -eq (FromGitHub ($real -replace '"v0.1.1"', '"latest"'))) 'nor one whose tag is not a version'
Check ($null -eq (FromGitHub 'not json')) 'an answer that is not JSON is no answer'

# ---- 2. what is offered ----------------------------------------------------------
Check (Newer '0.1.2' '0.1.1') '0.1.2 is newer than 0.1.1'
Check (Newer '0.1.10' '0.1.9') '0.1.10 is newer than 0.1.9 - numbers, not text'
Check (-not (Newer '0.1.1' '0.1.1')) 'the same version is not newer'
Check (-not (Newer '0.1.0' '0.1.1')) 'an older one is not newer'
Check (-not (Newer 'rubbish' '0.1.1')) 'nonsense is not newer'

function Offer($latest, [string]$current, $skipped) { $a = New-Object object[] 3; $a[0] = $latest; $a[1] = $current; $a[2] = $skipped; return Call 'Offer' $a }
Check ($null -ne (Offer $r '0.1.0' $null)) '0.1.1 is offered to 0.1.0'
Check ($null -eq (Offer $r '0.1.1' $null)) 'nothing is offered to 0.1.1 itself'
Check ($null -eq (Offer $r '0.1.0' '0.1.1')) 'a skipped version is not offered'
Check ($null -ne (Offer $r '0.1.0' '0.1.0')) 'skipping an earlier one does not hide a later one'
Check ($null -eq (Offer $null '0.1.0' $null)) 'no answer from GitHub offers nothing'

Check ([string](Call 'InfoSuffix' @()) -eq '') "no notice on memoQ's hits under a harness"
Check ([string](Call 'CurrentVersion' @()) -match '^\d+\.\d+\.\d+$') "this build's version has three parts: $(Call 'CurrentVersion' @())"

# ---- 3. only the installer GitHub published is ever run --------------------------
$setup = 'D:\SynologyDrive\Dev\Sv\Supervertaler-for-memoQ\dist\Supervertaler-for-memoQ-Setup.exe'
function Verify([string]$path, $release) { $a = New-Object object[] 2; $a[0] = $path; $a[1] = $release; return Call 'Verify' $a }
if ((Test-Path $setup) -and (Get-Item $setup).Length -eq 33108139) {
    Check ($null -eq (Verify $setup $r)) 'the real 0.1.1 installer passes: right size, right SHA-256'

    $wrongDigest = FromGitHub ($real -replace '9b9d3063', '00000000')
    Check ($null -ne (Verify $setup $wrongDigest)) 'the same size with a different checksum is refused'

    $wrongSize = FromGitHub ($real -replace '"size":33108139', '"size":33108140')
    Check ((Verify $setup $wrongSize) -like '*bytes*') 'a different size is refused'

    $noDigest = FromGitHub ($real -replace [regex]::Escape($digest), '')
    Check ($null -eq (Verify $setup $noDigest)) 'with no checksum from GitHub, the size alone decides'
} else {
    Write-Host 'SKIP the real-installer checks: dist\ does not hold the published 0.1.1 installer'
}

$tmp = Join-Path $env:TEMP 'sv-update-test.exe'
$bad = FromGitHub ($real -replace 'https://github.com/Supervertaler/Supervertaler-for-memoQ/releases/download/v0.1.1/', 'http://127.0.0.1:9/')
$da = New-Object object[] 2; $da[0] = $bad; $da[1] = [string]$tmp
$task = Call 'DownloadAsync' $da
$problem = $task.GetAwaiter().GetResult()
Check ($problem -like 'the download failed*') "a failed download says so: $problem"
Check (-not (Test-Path $tmp)) 'and leaves no file behind to be run by mistake'

# A download that completes but is not the published installer: a one-shot local
# server answers with five bytes where GitHub listed 33 MB. The file was written,
# so this is the case where leaving it behind would matter.
$port = Get-Random -Minimum 20000 -Maximum 60000
$server = Start-Job -ArgumentList $port -ScriptBlock {
    param($port)
    $l = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port); $l.Start()
    $c = $l.AcceptTcpClient(); $s = $c.GetStream()
    $buf = New-Object byte[] 4096; [void]$s.Read($buf, 0, $buf.Length)
    $reply = [Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Length: 5`r`nConnection: close`r`n`r`nhello")
    $s.Write($reply, 0, $reply.Length); $s.Flush(); $c.Close(); $l.Stop()
}
Start-Sleep -Milliseconds 1500
$wrong = FromGitHub ($real -replace 'https://github.com/Supervertaler/Supervertaler-for-memoQ/releases/download/v0.1.1/', "http://127.0.0.1:$port/")
$da = New-Object object[] 2; $da[0] = $wrong; $da[1] = [string]$tmp
$problem = (Call 'DownloadAsync' $da).GetAwaiter().GetResult()
Wait-Job $server -Timeout 10 | Out-Null; Remove-Job $server -Force
Check ($problem -like '*5 bytes*') "a complete download of the wrong file is refused: $problem"
Check (-not (Test-Path $tmp)) 'and deleted, so it can never be run'

Write-Host ""
if ($fails -eq 0) { Write-Host "All passed." } else { Write-Host "$fails FAILED"; exit 1 }
