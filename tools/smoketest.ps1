# Loads the built plugin exactly the way memoQ's add-in scanner does, and drives
# it as far as it can be driven outside the application.
#
# Worth having because memoQ's failure mode is silent: if the type cannot be
# found, constructed or initialised, the engine simply does not appear in the MT
# settings list, with nothing logged and no error shown. This tells the
# difference between "my plugin is broken" and "I picked the wrong menu".
#
#   powershell -ExecutionPolicy Bypass -File tools\smoketest.ps1

param(
    [string]$MemoQPath = 'C:\Program Files\memoQ\memoQ-12',
    [string]$PluginDll = "$PSScriptRoot\..\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $PluginDll)) { throw "Plugin not built: $PluginDll" }
if (-not (Test-Path $MemoQPath)) { throw "memoQ not found: $MemoQPath" }

# Resolve memoQ's assemblies out of its install directory. The guard matters:
# without it a failed probe recurses until the stack overflows.
$script:probed = @{}
[System.AppDomain]::CurrentDomain.add_AssemblyResolve([System.ResolveEventHandler] {
    param($sender, $e)
    $name = ($e.Name -split ',')[0]
    if ($script:probed.ContainsKey($name)) { return $null }
    $script:probed[$name] = $true
    foreach ($dir in @($MemoQPath, "$MemoQPath\Addins")) {
        $candidate = Join-Path $dir "$name.dll"
        if (Test-Path $candidate) {
            try { return [System.Reflection.Assembly]::LoadFrom($candidate) } catch { return $null }
        }
    }
    return $null
})

$mtInterfaces = [System.Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.MTInterfaces.dll")
$addinsCommon = [System.Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.Addins.Common.dll")
$plugin       = [System.Reflection.Assembly]::LoadFrom((Resolve-Path $PluginDll))

$iDirector2 = $mtInterfaces.GetType('MemoQ.MTInterfaces.IPluginDirector2')
$iModule    = $addinsCommon.GetType('MemoQ.Addins.Common.Framework.IModule')

Write-Host "Plugin:  $($plugin.FullName)"
Write-Host "Runtime: $($plugin.ImageRuntimeVersion)  Arch: $($plugin.GetName().ProcessorArchitecture)"
Write-Host ''

# --- 1. discovery, using memoQ's own loader ---------------------------------
# Do NOT hand-roll this check. An earlier version of this script tested only
# "does a type implement IPluginDirector2 and IModule", passed, and the plugin
# was still invisible in memoQ — because ModuleManager.loadAssemblyModules calls
# tryGetModuleAttribute(assembly) FIRST and bails silently when it is absent.
# Calling memoQ's own methods is the only check worth trusting.
$common = [System.Reflection.Assembly]::LoadFrom("$MemoQPath\MemoQ.Common.dll")
$mm     = $common.GetType('MemoQ.Common.Framework.Modules.ModuleManager')
$mmBase = $common.GetType('MemoQ.Common.Framework.Modules.ModuleManagerBase')
$static = [System.Reflection.BindingFlags]'Public,NonPublic,Static'

$moduleAttr = $mm.GetMethod('tryGetModuleAttribute', $static).Invoke($null, @($plugin))
if (-not $moduleAttr) {
    throw 'FAIL: no [assembly: Module(...)] attribute. memoQ loads nothing from this assembly — silently, with no warning and no MT settings entry.'
}

# memoQ honours exactly one ModuleAttribute per assembly despite AllowMultiple.
# A second one is silently dropped, so catch it here rather than wondering later
# why half the plugin never appears.
$attrType = $addinsCommon.GetType('MemoQ.Addins.Common.Framework.ModuleAttribute')
$declared = @($plugin.GetCustomAttributes($attrType, $false))
if ($declared.Count -gt 1) {
    throw "FAIL: $($declared.Count) [assembly: Module] attributes in one assembly. memoQ loads only the first; the rest are silently ignored. Split them into separate DLLs."
}
$at = $moduleAttr.GetType()
$moduleName = $at.GetProperty('ModuleName').GetValue($moduleAttr)
$className  = $at.GetProperty('ClassName').GetValue($moduleAttr)
Write-Host "PASS  [assembly: Module] ModuleName='$moduleName' ClassName='$className'"

if (-not $mmBase.GetMethod('IsMTAddin', $static).Invoke($null, @($plugin))) {
    throw 'FAIL: memoQ does not recognise this as an MT add-in.'
}
Write-Host 'PASS  IsMTAddin'

$signed = $mmBase.GetMethod('IsAddinSigned', $static).Invoke($null, @((Resolve-Path $PluginDll).Path, $plugin))
Write-Host "      IsAddinSigned = $signed$(if (-not $signed) { '  (expected — a private plugin is unsigned until memoQ signs it)' })"

# ClassName is a string; nothing checks it at compile time, so check it here.
$type = $plugin.GetType($className)
if (-not $type) { throw "FAIL: ClassName '$className' does not resolve to a type in this assembly." }
if (-not ($iDirector2.IsAssignableFrom($type) -and $iModule.IsAssignableFrom($type))) {
    throw "FAIL: $className does not implement both IPluginDirector2 and IModule."
}
Write-Host "PASS  ClassName resolves: $($type.FullName)"

# --- 2. construction --------------------------------------------------------
if (-not $type.GetConstructor([Type]::EmptyTypes)) { throw 'FAIL: no public parameterless constructor.' }
$director = [Activator]::CreateInstance($type)
Write-Host 'PASS  constructed'

# --- 3. identity and capabilities ------------------------------------------
Write-Host ''
foreach ($p in 'PluginID', 'FriendlyName', 'CopyrightText', 'BatchSupported', 'InteractiveSupported', 'StoringTranslationSupported', 'SupportFuzzyForwarding') {
    Write-Host ("      {0,-28} = {1}" -f $p, $type.GetProperty($p).GetValue($director))
}

$icon = $type.GetProperty('DisplayIcon').GetValue($director)
if ($icon) { Write-Host ("PASS  DisplayIcon             = {0}x{1}" -f $icon.Width, $icon.Height) }
else       { Write-Host 'WARN  DisplayIcon returned null (blank square in the settings list)' }

# --- 4. lifecycle -----------------------------------------------------------
Write-Host ''
$type.GetMethod('Initialize').Invoke($director, @($null))
Write-Host ("PASS  Initialize (IsActivated = {0})" -f $type.GetProperty('IsActivated').GetValue($director))

# --- 5. engine and sessions -------------------------------------------------
$settingsType = $mtInterfaces.GetType('MemoQ.MTInterfaces.PluginSettings')
$paramsType   = $mtInterfaces.GetType('MemoQ.MTInterfaces.CreateEngineParams')
$createParams = [Activator]::CreateInstance($paramsType, @('eng', 'nld', $null))

$engine = $type.GetMethod('CreateEngine').Invoke($director, @($createParams))
if (-not $engine) { throw 'FAIL: CreateEngine returned null.' }
Write-Host "PASS  CreateEngine -> $($engine.GetType().FullName)"

$et = $engine.GetType()
Write-Host ("      {0,-28} = {1}" -f 'MaxDegreeOfParallelism', $et.GetProperty('MaxDegreeOfParallelism').GetValue($engine))

$lookup = $et.GetMethod('CreateLookupSession').Invoke($engine, @())
if (-not $lookup) { throw 'FAIL: CreateLookupSession returned null.' }
Write-Host "PASS  CreateLookupSession -> $($lookup.GetType().Name)"

$rich = $et.GetMethod('CreateRichLookupSession').Invoke($engine, @())
if (-not $rich) { throw 'FAIL: CreateRichLookupSession returned null — the context-carrying path is what this plugin is for.' }
Write-Host "PASS  CreateRichLookupSession -> $($rich.GetType().Name)"

# --- 6. tag round trip (no network) ----------------------------------------
Write-Host ''
$builder = $addinsCommon.GetType('MemoQ.Addins.Common.DataStructures.SegmentBuilder')
$xmlConv = $addinsCommon.GetType('MemoQ.Addins.Common.Utils.SegmentXMLConverter')

$sample  = $builder.GetMethod('CreateFromString').Invoke($null, @('A device comprising a widget.'))
$asXml   = $xmlConv.GetMethod('ConvertSegment2Xml').Invoke($null, @($sample, $true, $false))
Write-Host "PASS  Segment -> XML: $asXml"

$type.GetMethod('Cleanup').Invoke($director, @())
Write-Host ''
Write-Host 'All checks passed. memoQ will discover and construct this plugin.'
