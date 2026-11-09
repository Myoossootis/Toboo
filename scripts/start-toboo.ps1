param([switch]$ServoCalibration, [switch]$Sensors, [switch]$Expression, [switch]$SelfTest)
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
$artifactPath = Join-Path $repositoryPath 'artifacts/toboo'
Push-Location -LiteralPath $repositoryPath
try {
    & dotnet build src/Toboo.Desktop/Toboo.Desktop.csproj -c Release `
        -p:RestoreLockedMode=true -p:UseArtifactsOutput=true "-p:ArtifactsPath=$artifactPath"
    if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
    $executable = Join-Path $artifactPath 'bin/Toboo.Desktop/release/toboo.exe'
    if ($SelfTest) {
        $process = Start-Process -FilePath $executable -ArgumentList '--self-test' -WorkingDirectory $repositoryPath -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw 'toboo native self-test failed. See the local diagnostics directory.' }
        Write-Host 'PASS: source launcher and native application dependencies.'
    }
    else {
        $launchArguments = @('--face-preview')
        if ($ServoCalibration) { $launchArguments += '--servo-calibration' }
        if ($Sensors) { $launchArguments += '--sensors' }
        if ($Expression) { $launchArguments += '--expression-preview' }
        Start-Process -FilePath $executable -ArgumentList $launchArguments -WorkingDirectory $repositoryPath
    }
}
finally { Pop-Location }
