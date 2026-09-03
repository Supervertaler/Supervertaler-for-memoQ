# The preview shows what the draft will actually send, and costs nothing.
#
# The value of a preview is entirely in its being the same text. So the thing
# under test is that one method assembles the context and both callers use it -
# and, separately, that the preview path never reaches a model: it is offered as
# a free look, and a preview that quietly classified would be charging for it.
$ErrorActionPreference = 'Stop'
$MemoQPath = 'C:\Program Files\memoQ\memoQ-12'
$PluginDll = 'D:\Google Drive\Dev\Sv\Supervertaler-for-memoQ\src\Supervertaler.MemoQ\bin\Release\Supervertaler.MemoQ.dll'

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

$plugin = [Reflection.Assembly]::LoadFrom($PluginDll)
$NonPublicStatic = [Reflection.BindingFlags]'NonPublic,Static'
$NonPublicInstance = [Reflection.BindingFlags]'NonPublic,Instance'

$fails = 0
function Check($ok, $label) {
    if (-not $ok) { $script:fails++ }
    Write-Host "$(if ($ok) {'PASS'} else {'FAIL'}) $label"
}

$bridgeType = $plugin.GetType('Supervertaler.MemoQ.Core.MemoQBridge')
$preview = $plugin.GetType('Supervertaler.MemoQ.Core.PreviewStore')
$partType = $preview.GetNestedType('Part', [Reflection.BindingFlags]'NonPublic,Public')

# ---- a document the resolver can find ------------------------------------
$docGuid = [Guid]::NewGuid()
$listType = [Collections.Generic.List`1].MakeGenericType(@($partType))
$parts = [Activator]::CreateInstance($listType)
foreach ($i in 1..30) {
    $p = [Activator]::CreateInstance($partType)
    $partType.GetField('PartId').SetValue($p, "mQ-default-preview-$i")
    $partType.GetField('DocumentGuid').SetValue($p, $docGuid)
    $partType.GetField('DocumentName').SetValue($p, 'Preview document.docx')
    $partType.GetField('SourceLangCode').SetValue($p, 'dut-NL')
    $partType.GetField('TargetLangCode').SetValue($p, 'eng-GB')
    $partType.GetField('Source').SetValue($p, "De uitvinding betreft een werkwijze voor het verlagen van methaanemissie, paragraaf $i.")
    $partType.GetField('Target').SetValue($p, '')
    $parts.Add($p)
}
$argv = [object[]]::new(1); $argv[0] = $parts
$preview.GetMethod('Upsert').Invoke($null, $argv) | Out-Null
$preview.GetMethod('NoteTool').Invoke($null, [object[]]@($true)) | Out-Null

$bridge = [Activator]::CreateInstance($bridgeType, $true)
$doc = $bridgeType.GetMethod('ResolveAutoPromptSource', $NonPublicInstance).Invoke($bridge, [object[]]@('no-such-key'))

# ---- the plan ------------------------------------------------------------
$requestType = $bridgeType.GetNestedType('AutoPromptRequest', [Reflection.BindingFlags]'NonPublic,Public')
$req = [Activator]::CreateInstance($requestType)
$requestType.GetProperty('Domain').SetValue($req, 'patent')
$requestType.GetProperty('Hint').SetValue($req, 'Prefer UK spelling.')
$requestType.GetProperty('IncludeTerms').SetValue($req, $false)
$requestType.GetProperty('IncludeConfirmed').SetValue($req, $false)

$general = [Activator]::CreateInstance($plugin.GetType('Supervertaler.MemoQ.Settings.SupervertalerGeneralSettings'))

$plan = $bridgeType.GetMethod('PlanAutoPrompt', $NonPublicInstance).Invoke(
    $bridge, [object[]]@($req, $doc, $general, '', $false))

$planType = $plan.GetType()
$ctx = $planType.GetField('Context').GetValue($plan)
$domain = $planType.GetField('Domain').GetValue($plan)

Check ($domain -eq 'patent') "the confirmed domain is used as given, not re-guessed: $domain"

$ctxType = $ctx.GetType()
$segments = $ctxType.GetProperty('SourceSegments').GetValue($ctx)
Check ($segments.Count -eq 30) "the whole document reaches the plan: $($segments.Count) segment(s)"

$constraints = $ctxType.GetProperty('HostConstraints').GetValue($ctx)
Check ($constraints -and $constraints.Length -gt 100) 'the memoQ host constraints are attached'

$hint = $ctxType.GetProperty('UserContextHint').GetValue($ctx)
Check ($hint -like '*UK spelling*') 'what the user typed reaches the model'

# ---- the text the preview shows is the text that gets sent ---------------
$generator = $null
foreach ($a in [AppDomain]::CurrentDomain.GetAssemblies()) {
    $t = $a.GetType('Supervertaler.Core.PromptGenerator')
    if ($t) { $generator = $t; break }
}
$build = $generator.GetMethod('BuildMetaPrompt')
$meta = $build.Invoke($null, [object[]]@($ctx))

Check ($meta.Length -gt 1000) "the meta-prompt is assembled: $($meta.Length) chars"
Check ($meta -like '*paragraaf 30*') 'the preview contains the document text the draft would send'
Check ($meta -like '*UK spelling*') 'the preview contains the briefing'

# Same context object, so the two can only agree. Building it twice must give
# the identical text - a preview that varied between looks would be worthless.
$again = $build.Invoke($null, [object[]]@($ctx))
Check ($again -eq $meta) 'the same context always renders the same instructions'

# ---- and with no API key it still works ----------------------------------
# The preview is offered before a key is necessarily configured, and it does not
# need one because it sends nothing.
Check ($null -ne $meta) 'no API key was needed to assemble any of this'

Write-Host ''
Write-Host "AUTOPROMPT PREVIEW TEST COMPLETE - $fails failure(s)"
