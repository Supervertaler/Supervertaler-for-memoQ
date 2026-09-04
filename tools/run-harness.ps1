# Runs a harness with the user's real shared settings protected.
#
# Constructing an EngineContext seeds the shared settings file from whatever
# settings object it was handed. In memoQ that is the user's MT settings
# resource, which is the point. In a harness it is a bare defaults object, and
# because seeding only ever fills gaps, those defaults would then be permanent:
# memoQ would find the keys already present and never seed the real values.
# One run of a harness would silently blank the user's selected prompt.
#
# Every file the plugin writes under its own settings folder is snapshotted, not
# just the ones a harness is expected to touch. A test that sets a value
# deliberately - a memory bank, say - must still not keep it, and the list is
# the only thing standing between a harness and the user's real state.
param([Parameter(Mandatory = $true)][string]$Harness)

$ErrorActionPreference = 'Stop'

$dir = Join-Path $env:LOCALAPPDATA 'Supervertaler.memoQ'
$protected = @(
    'shared.txt'                    # provider, model, prompt, glossary, memory bank
    'instructions.txt'              # the inline instructions
    'memory-bank-projects.txt'      # which bank each project uses
) | ForEach-Object { Join-Path $dir $_ }

$before = @{}
foreach ($path in $protected) {
    $before[$path] = if (Test-Path $path) { [IO.File]::ReadAllBytes($path) } else { $null }
}

try {
    # Stops the plugin seeding the user's settings from harness defaults and
    # stops it resolving their real API key.
    $env:SUPERVERTALER_HARNESS = '1'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $Harness
}
finally {
    Remove-Item Env:SUPERVERTALER_HARNESS -ErrorAction SilentlyContinue

    foreach ($path in $protected) {
        $bytes = $before[$path]
        if ($null -ne $bytes) { [IO.File]::WriteAllBytes($path, $bytes) }
        elseif (Test-Path $path) { Remove-Item $path -Force }
    }

    Write-Host "[shared settings restored]"
}
