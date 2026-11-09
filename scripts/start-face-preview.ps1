param([switch]$ServoCalibration, [switch]$Sensors, [switch]$Expression)
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
$artifactPath = Join-Path $repositoryPath 'artifacts/toboo'
Push-Location -LiteralPath $repositoryPath
try {
    & dotnet build src/PanTiltTracker.Desktop/PanTiltTracker.Desktop.csproj -c Release `
        -p:RestoreLockedMode=true -p:UseArtifactsOutput=true "-p:ArtifactsPath=$artifactPath"
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    $launchArguments = @('--face-preview')
    if ($ServoCalibration) { $launchArguments += '--servo-calibration' }
    if ($Sensors) { $launchArguments += '--sensors' }
    if ($Expression) { $launchArguments += '--expression-preview' }
    Start-Process -FilePath (Join-Path $artifactPath 'bin/PanTiltTracker.Desktop/release/toboo.exe') `
        -ArgumentList $launchArguments -WorkingDirectory $repositoryPath
}
finally { Pop-Location }
