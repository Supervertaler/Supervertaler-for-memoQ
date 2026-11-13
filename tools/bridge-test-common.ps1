# Shared by bridge-query-test.ps1 and document-keys-test.ps1: loads the plugin
# and sends REAL HTTP requests to the bridge's own handlers.
#
# No bridge is started and no handshake file is written. A listener of this
# script's own, on a port of its own, receives each request; the request it
# receives is handed to the handler exactly as MemoQBridge's dispatcher would
# hand it. So the query string really crosses HTTP, which is where it broke.
#
# ASCII only: Windows PowerShell 5.1 misreads a file without a byte order mark.
$ErrorActionPreference = 'Stop'

foreach ($name in @('memoQ', 'Supervertaler.MemoQ.Preview')) {
    if (@(Get-Process -Name $name -ErrorAction SilentlyContinue).Count -gt 0) {
        Write-Host "$name is running. Close it first: this test fills the live view of whatever plugin is loaded here, and an orphaned preview tool would talk to it."
        exit 2
    }
}
if (-not $env:SUPERVERTALER_HARNESS) {
    Write-Host 'Run this through tools\run-harness.ps1, never directly.'
    exit 2
}

$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$PluginDll = Join-Path $PSScriptRoot '..\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll'

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
Add-Type -AssemblyName System.Net.Http

$plugin = [Reflection.Assembly]::LoadFrom((Resolve-Path $PluginDll))
$Static   = [Reflection.BindingFlags]'Public,NonPublic,Static'
$Instance = [Reflection.BindingFlags]'Public,NonPublic,Instance'
function PluginType([string]$name) {
    $t = $plugin.GetType($name)
    if ($null -eq $t) { throw "type not found: $name" }
    return $t
}

# A data folder of this test's own, and no look at memoQ's project folders.
$script:tmp = Join-Path ([IO.Path]::GetTempPath()) ('sv-bridge-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $script:tmp 'data') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $script:tmp 'projects') -Force | Out-Null
(PluginType 'Supervertaler.Core.SupervertalerPaths').GetMethod('Set', $Static).Invoke($null, @([string](Join-Path $script:tmp 'data')))
(PluginType 'Supervertaler.MemoQ.Core.DocumentNames').GetField('RootsOverride', $Static).SetValue($null, [string[]]@((Join-Path $script:tmp 'projects')))

$script:pass = 0
$script:fail = 0
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { $script:pass++; Write-Host "PASS $name" }
    else { $script:fail++; Write-Host "FAIL $name  $detail" }
}
function U([int[]]$codes) { -join ($codes | ForEach-Object { [char]$_ }) }

$bridgeType  = PluginType 'Supervertaler.MemoQ.Core.MemoQBridge'
$previewType = PluginType 'Supervertaler.MemoQ.Core.PreviewStore'
$partType    = PluginType 'Supervertaler.MemoQ.Core.PreviewStore+Part'
$bridge = [Activator]::CreateInstance($bridgeType, $true)

function NewPart([string]$partId, [Guid]$doc, [string]$name, [string]$source, [string]$target) {
    $p = [Activator]::CreateInstance($partType, $true)
    $partType.GetField('PartId').SetValue($p, $partId)
    $partType.GetField('DocumentGuid').SetValue($p, $doc)
    $partType.GetField('DocumentName').SetValue($p, $name)
    $partType.GetField('SourceLangCode').SetValue($p, 'ita-IT')
    $partType.GetField('TargetLangCode').SetValue($p, 'eng-GB')
    $partType.GetField('Source').SetValue($p, $source)
    $partType.GetField('Target').SetValue($p, $target)
    return $p
}
function PartList($parts) {
    $list = [Activator]::CreateInstance([System.Collections.Generic.List`1].MakeGenericType($partType))
    foreach ($p in $parts) { $list.Add($p) }
    return ,$list
}
# Puts paragraphs into the live view, as the preview tool does.
function Upsert($parts) {
    $a = [object[]]::new(1); $a[0] = (PartList $parts).PSObject.BaseObject
    $previewType.GetMethod('Upsert', $Static).Invoke($null, $a) | Out-Null
}
function ToolConnected([bool]$on) { $previewType.GetMethod('NoteTool', $Static).Invoke($null, [object[]]@($on)) | Out-Null }
function ClearLive { $previewType.GetMethod('Clear', $Static).Invoke($null, $null) | Out-Null }

# The listener.
$script:listener = $null
foreach ($try in 1..20) {
    $script:port = Get-Random -Minimum 20000 -Maximum 60000
    $l = New-Object System.Net.HttpListener
    $l.Prefixes.Add("http://127.0.0.1:$($script:port)/")
    try { $l.Start(); $script:listener = $l; break } catch { }
}
if ($null -eq $script:listener) { throw 'no free port' }
$script:http = New-Object System.Net.Http.HttpClient

function Esc([string]$s) { [Uri]::EscapeDataString($s) }

# Sends one request and lets $handle answer the context the listener received.
# Returns Status and Body (text, read as UTF-8).
function Send([string]$method, [string]$pathAndQuery, [string]$jsonBody, [scriptblock]$handle) {
    $req = New-Object System.Net.Http.HttpRequestMessage ([System.Net.Http.HttpMethod]$method), "http://127.0.0.1:$($script:port)$pathAndQuery"
    if ($jsonBody) { $req.Content = New-Object System.Net.Http.StringContent $jsonBody, ([Text.Encoding]::UTF8), 'application/json' }
    $pending = $script:listener.BeginGetContext($null, $null)
    $task = $script:http.SendAsync($req)
    $ctx = $script:listener.EndGetContext($pending)
    try { & $handle $ctx }
    finally { try { $ctx.Response.Close() } catch { } }
    $resp = $task.Result
    $bytes = $resp.Content.ReadAsByteArrayAsync().Result
    return [pscustomobject]@{ Status = [int]$resp.StatusCode; Body = [Text.Encoding]::UTF8.GetString($bytes) }
}

# One of the bridge's handlers, by name, answering a real request.
function Call([string]$handler, [string]$method, [string]$pathAndQuery, [string]$jsonBody = $null) {
    $m = $bridgeType.GetMethod($handler, $Instance)
    if ($null -eq $m) { throw "no handler $handler" }
    return Send $method $pathAndQuery $jsonBody { param($ctx) $m.Invoke($bridge, [object[]]@($ctx)) | Out-Null }
}

function Finish {
    try { $script:listener.Stop() } catch { }
    try { $script:http.Dispose() } catch { }
    try { Remove-Item -Recurse -Force $script:tmp -ErrorAction SilentlyContinue } catch { }
    Write-Host ''
    if ($script:fail -gt 0) { Write-Host "$($script:fail) FAILED, $($script:pass) passed"; exit 1 }
    Write-Host "All $($script:pass) passed."
    exit 0
}
