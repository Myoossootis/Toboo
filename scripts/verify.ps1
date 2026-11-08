param()

$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repositoryPath
try {
    & (Join-Path $PSScriptRoot 'verify-product-boundary.ps1')
    function Invoke-DotNet {
        param([string[]]$Arguments)
        & dotnet @Arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
    }

    Invoke-DotNet -Arguments @('restore', 'PanTiltTracker.sln', '--locked-mode')
    Invoke-DotNet -Arguments @('build', 'PanTiltTracker.sln', '-c', 'Release', '--no-restore')
    Invoke-DotNet -Arguments @('test', 'PanTiltTracker.sln', '-c', 'Release', '--no-build', '--no-restore')

    $applicationPath = & dotnet msbuild src/PanTiltTracker.App/PanTiltTracker.App.csproj `
        -getProperty:TargetPath -property:Configuration=Release
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the built CLI application path.' }
    $applicationPath = $applicationPath.Trim()
    Invoke-DotNet -Arguments @($applicationPath, 'simulate', '--frames', '300', '--out', 'artifacts/verification/simulation')
    Invoke-DotNet -Arguments @($applicationPath, 'marker', '--out', 'artifacts/verification/marker-7.png')
    Invoke-DotNet -Arguments @($applicationPath, 'image', '--input', 'artifacts/verification/marker-7.png', '--out', 'artifacts/verification/image')

    $rows = @(Import-Csv -LiteralPath 'artifacts/verification/simulation/telemetry.csv')
    if ($rows.Count -ne 300) { throw "Expected 300 simulated frames, got $($rows.Count)." }
    if (@($rows | Where-Object { $_.state -eq 'Lost' }).Count -eq 0) {
        throw 'The scheduled synthetic occlusion did not produce a Lost state.'
    }
    $imageRows = @(Import-Csv -LiteralPath 'artifacts/verification/image/telemetry.csv')
    if ($imageRows.Count -ne 1 -or $imageRows[0].detected -ne '1') {
        throw 'Generated marker was not detected by the image command.'
    }
    $metadata = Get-Content -LiteralPath 'artifacts/verification/simulation/run.json' -Raw | ConvertFrom-Json
    if ($metadata.hardwareOutput -ne 'disabled' -or !$metadata.simulated) {
        throw 'Simulation metadata is not correctly labeled.'
    }
    Write-Host 'PASS: build, tests, 300-frame synthetic pipeline, occlusion, marker and image CLI.'
    Write-Host 'Hardware camera, UART, servo behavior and physical accuracy remain unverified.'
}
finally {
    Pop-Location
}
