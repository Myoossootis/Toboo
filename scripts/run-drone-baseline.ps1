param(
    [string]$InputVideo = 'data/processed/drone-11204831-1080p30.mp4',
    [string]$InitialRegion = '770,435,590,150',
    [string]$OutputDirectory = 'artifacts/drone-experiments'
)

$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repositoryPath
try {
    if (!(Test-Path -LiteralPath $InputVideo -PathType Leaf)) { throw "Video not found: $InputVideo" }
    & dotnet build src/PanTiltTracker.App -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $applicationPath = Join-Path $repositoryPath 'src/PanTiltTracker.App/bin/Release/net8.0/PanTiltTracker.App.dll'
    & dotnet $applicationPath track-video --input $InputVideo --algorithm csrt --roi $InitialRegion --out (Join-Path $OutputDirectory 'csrt')
    if ($LASTEXITCODE -ne 0) { throw 'CSRT experiment failed.' }
    & dotnet $applicationPath track-video --input $InputVideo --algorithm frame-diff --out (Join-Path $OutputDirectory 'frame-difference')
    if ($LASTEXITCODE -ne 0) { throw 'Frame-difference experiment failed.' }
    Write-Host 'Recorded-video experiments complete. Reported target frames are not an accuracy measurement.'
}
finally { Pop-Location }
