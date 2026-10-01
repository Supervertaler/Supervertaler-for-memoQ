$ErrorActionPreference = 'Stop'
if (Get-Process memoQ -ErrorAction SilentlyContinue) { throw 'memoQ is running: this harness starts its own bridge and must not compete with the live one. Close memoQ first.' }
if (Get-Process Supervertaler.MemoQ.Preview -ErrorAction SilentlyContinue) { throw 'The Supervertaler preview tool is running (an orphan if memoQ is closed): it would connect to this harness bridge and eat its commands. Stop it from the tray, or: taskkill /IM Supervertaler.MemoQ.Preview.exe /F' }
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$Bin = 'D:\SynologyDrive\Dev\Sv\Supervertaler-for-memoQ\src'

# The resolver is compiled C#, not a scriptblock. This harness talks to the
# plugin's bridge over HTTP, and the bridge answers on thread-pool threads; an
# assembly load there raised the event on a thread with no PowerShell runspace,
# a scriptblock handler cannot run on such a thread, and the attempt recursed
# into the resolver until the process died with a StackOverflowException.
# A compiled delegate runs on any thread.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public static class BridgeHarnessResolver
{
    static string[] probe;

    // Per thread, so two threads resolving the same name do not turn each
    // other away; within a thread it stops a load recursing into itself.
    [ThreadStatic] static Dictionary<string, bool> inFlight;

    public static void Attach(string[] dirs)
    {
        probe = dirs;
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
    }

    static Assembly Resolve(object sender, ResolveEventArgs e)
    {
        string name = new AssemblyName(e.Name).Name;
        if (inFlight == null) inFlight = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (inFlight.ContainsKey(name)) return null;
        inFlight[name] = true;
        try
        {
            foreach (string dir in probe)
            {
                string candidate = Path.Combine(dir, name + ".dll");
                if (File.Exists(candidate)) return Assembly.LoadFrom(candidate);
            }
            return null;
        }
        catch { return null; }
        finally { inFlight.Remove(name); }
    }
}
'@
[BridgeHarnessResolver]::Attach([string[]]@($MemoQPath, "$MemoQPath\Addins", "$Bin\Supervertaler.MemoQ\bin\Release"))

$mt     = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.MTInterfaces.dll")
$tbi    = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.TBInterfaces.dll")
$common = [Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.Addins.Common.dll")
$main   = [Reflection.Assembly]::LoadFrom("$Bin\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll")
$terms  = [Reflection.Assembly]::LoadFrom("$Bin\Supervertaler.MemoQ.Terms\bin\Release\Supervertaler.MemoQ.Terms.dll")

$builder = $common.GetType('MemoQ.Addins.Common.DataStructures.SegmentBuilder')
$xmlConv = $common.GetType('MemoQ.Addins.Common.Utils.SegmentXMLConverter')
function Seg($t) { $builder.GetMethod('CreateFromString').Invoke($null, @($t)) }
function Xml($s) {
    if ($null -eq $s) { return '<null>' }
    return $xmlConv.GetMethod('ConvertSegment2Xml').Invoke($null, @($s, $true, $false))
}

# --- Build an engine with BridgeMode=true, directly (bypasses memoQ's XML settings envelope). ---
$generalT = $main.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings')
$secureT  = $main.GetType('Supervertaler.MemoQ.Settings.SupervertalerSecureSettings')
$settingsT = $main.GetType('Supervertaler.MemoQ.Settings.SupervertalerSettings')
$general = [Activator]::CreateInstance($generalT)
$generalT.GetProperty('BridgeMode').SetValue($general, $true)

# EngineContext.General overlays the shared settings file over whatever memoQ
# stored, so setting the property alone is no longer enough: the file decides.
# run-harness.ps1 snapshots and restores that file, so writing to it is safe.
$sharedT = $main.GetType('Supervertaler.MemoQ.Core.SharedSettings')
$sharedT.GetProperty('BridgeMode', [Reflection.BindingFlags]'NonPublic,Public,Static').SetValue($null, $true)
$settings = $settingsT.GetMethod('Create').Invoke($null, @($general, [Activator]::CreateInstance($secureT)))

$engineT = $main.GetType('Supervertaler.MemoQ.SupervertalerMTEngine')
$engine = [Activator]::CreateInstance($engineT, [Reflection.BindingFlags]'Instance,Public,NonPublic', $null, @($settings, 'eng', 'nld'), $null)
$lookup = $engineT.GetMethod('CreateLookupSession').Invoke($engine, @())
$segT = (Seg 'x').GetType()
$m3 = $lookup.GetType().GetMethod('TranslateCorrectSegment', [Type[]]@($segT, $segT, $segT))
$mArr = $lookup.GetType().GetMethod('TranslateCorrectSegment', [Type[]]@($segT.MakeArrayType(), $segT.MakeArrayType(), $segT.MakeArrayType()))

Start-Sleep -Milliseconds 500
$hs = Get-Content 'D:\Supervertaler\memoq\runtime\bridge.json' -Raw | ConvertFrom-Json
$base = "http://127.0.0.1:$($hs.port)"; $auth = @{ Authorization = "Bearer $($hs.token)" }
function GET($p) { Invoke-RestMethod -Uri "$base$p" -Headers $auth -Method Get }
function POST($p, $o) { Invoke-RestMethod -Uri "$base$p" -Headers $auth -Method Post -Body ($o | ConvertTo-Json -Depth 5) -ContentType 'application/json' }

# 1. Bridge mode is scoped to Pre-translate: a SINGLE-segment lookup still tries the model
#    (here with no API key, so it comes back as an error result rather than a translation).
$r = $m3.Invoke($lookup, @((Seg 'The controller comprises a processor.'), $null, $null))
$tried = ($null -ne $r.Exception)
Write-Host "$(if ($tried) {'PASS'} else {'FAIL'}) bridge mode single: model attempted (exception without key)=$tried"

# 2. ...but it was captured.
$segs = GET '/v1/segments'
$cap = ($segs.segments | Where-Object source -eq 'The controller comprises a processor.') -ne $null
Write-Host "$(if ($cap) {'PASS'} else {'FAIL'}) bridge mode captured the segment"

# 3. Bridge mode, batch of 3 with one staged: staged one served, no model call.
#    The unstaged two carry no translation but the bridge-mode "nothing staged"
#    error: since 2026-09-20 an empty translation is never returned, because memoQ
#    writes it into the target and wipes what was there (bridgemode-miss-test.ps1).
#    The message is what proves no model call - without a key, a model call would
#    fail too, but with a different message.
POST '/v1/stage' @{ pairs = @(@{ source = 'Second sentence.'; target = 'Tweede zin.' }); label = 'T' } | Out-Null
$arr = [Array]::CreateInstance($segT, 3)
$arr[0] = Seg 'First sentence.'; $arr[1] = Seg 'Second sentence.'; $arr[2] = Seg 'Third sentence.'
$rs = $mArr.Invoke($lookup, @($arr, $null, $null))
function Unstaged($r) { ($null -eq $r.Translation) -and ($null -ne $r.Exception) -and ($r.Exception.Message -like '*No staged translation*') }
$ok = ((Xml $rs[1].Translation) -eq 'Tweede zin.') -and -not $rs[1].Exception -and (Unstaged $rs[0]) -and (Unstaged $rs[2])
Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) bridge mode batch: [0]='$(Xml $rs[0].Translation)' [1]='$(Xml $rs[1].Translation)' [2]='$(Xml $rs[2].Translation)'; unstaged error: '$($rs[0].Exception.Message)'"

# 4. TB channel: a Lookup on the terminology plugin captures into the per-pair bucket, with NO MT involvement.
$dirT = $terms.GetType('Supervertaler.MemoQ.SupervertalerTBPluginDirector')
$dir = [Activator]::CreateInstance($dirT)
$dirT.GetMethod('Initialize').Invoke($dir, @($null))
$tbEngine = $dirT.GetMethod('CreateEngine').Invoke($dir, @('eng', 'nld'))
$tbSession = $tbEngine.GetType().GetMethod('CreateSession').Invoke($tbEngine, @())
$tbSession.GetType().GetMethod('Lookup', [Type[]]@($segT)).Invoke($tbSession, @((Seg 'Visited via the terminology pane only.'))) | Out-Null

$proj = GET '/v1/project'
$visited = $proj.documents | Where-Object { $_.key -like 'visited_*' }
$vs = GET ("/v1/segments?document=" + [uri]::EscapeDataString($visited.key))
$hit = ($vs.segments | Where-Object source -eq 'Visited via the terminology pane only.') -ne $null
Write-Host "$(if ($visited -and $hit) {'PASS'} else {'FAIL'}) TB capture: bucket=$($visited.key) origin='$($visited.origin)'"

POST '/v1/staged/clear' @{} | Out-Null
Write-Host ''
Write-Host 'BRIDGE MODE TEST COMPLETE'
