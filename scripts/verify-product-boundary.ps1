$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
$sourceFiles = Get-ChildItem -LiteralPath (Join-Path $repositoryPath 'src') -Recurse -File |
    Where-Object { $_.Extension -in '.cs', '.axaml' -and $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }
$boundaryViolations = @($sourceFiles | Select-String -Pattern 'DroneTarget|DroneModelPath|TrackDrones|detect-drone|evaluate-drone|无人机')
if ($boundaryViolations.Count -gt 0) {
    throw ('Drone recognition belongs to codex/archive-drone-recognition-20261109, not the toboo mainline: ' +
        (($boundaryViolations | ForEach-Object { $_.Path + ':' + $_.LineNumber }) -join ', '))
}
Write-Host 'PASS: toboo source contains no archived drone-recognition integration.'
