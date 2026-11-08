param([switch]$ServoCalibration)
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
$artifactPath = Join-Path $repositoryPath $(if ($ServoCalibration) { 'artifacts/s2-calibration-app' } else { 'artifacts/face-follow-faster' })
Push-Location -LiteralPath $repositoryPath
try {
    & dotnet build src/PanTiltTracker.Desktop/PanTiltTracker.Desktop.csproj -c Release `
        -p:RestoreLockedMode=true -p:UseArtifactsOutput=true "-p:ArtifactsPath=$artifactPath"
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    $launchArguments = @('--face-preview')
    if ($ServoCalibration) { $launchArguments += '--servo-calibration' }
    Start-Process -FilePath (Join-Path $artifactPath 'bin/PanTiltTracker.Desktop/release/PanTiltTracker.Desktop.exe') `
        -ArgumentList $launchArguments -WorkingDirectory $repositoryPath
}
finally { Pop-Location }
