# Runs a harness with the user's real shared settings protected.
#
# Constructing an EngineContext seeds the shared settings file from whatever
# settings object it was handed. In memoQ that is the user's MT settings
# resource, which is the point. In a harness it is a bare defaults object, and
# because seeding only ever fills gaps, those defaults would then be permanent:
# memoQ would find the keys already present and never seed the real values.
# One run of a harness would silently blank the user's selected prompt.
param([Parameter(Mandatory = $true)][string]$Harness)

$ErrorActionPreference = 'Stop'

$shared = Join-Path $env:LOCALAPPDATA 'Supervertaler.memoQ\shared.txt'
$instructions = Join-Path $env:LOCALAPPDATA 'Supervertaler.memoQ\instructions.txt'

$sharedBefore = if (Test-Path $shared) { [IO.File]::ReadAllBytes($shared) } else { $null }
$instructionsExisted = Test-Path $instructions
$instructionsBefore = if ($instructionsExisted) { [IO.File]::ReadAllBytes($instructions) } else { $null }

try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File $Harness
}
finally {
    if ($null -ne $sharedBefore) { [IO.File]::WriteAllBytes($shared, $sharedBefore) }
    elseif (Test-Path $shared) { Remove-Item $shared -Force }

    if ($instructionsExisted) { [IO.File]::WriteAllBytes($instructions, $instructionsBefore) }
    elseif (Test-Path $instructions) { Remove-Item $instructions -Force }

    Write-Host "[shared settings restored]"
}
