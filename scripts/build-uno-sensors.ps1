param([switch]$EnableUltrasonic, [switch]$EnableMmaAfterVoltageCheck)
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repositoryPath
try {
    $cli = Join-Path $repositoryPath 'tmp/esp32-tools/cli/arduino-cli.exe'
    $config = Join-Path $repositoryPath 'tmp/esp32-tools/arduino-cli.yaml'
    $settings = Get-Content firmware/uno-toolchain.json -Raw | ConvertFrom-Json
    $version = & $cli version --format json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $version.VersionString -ne $settings.arduinoCliVersion) { throw 'Pinned Arduino CLI required; see docs/19-uno-sensor-telemetry.md.' }
    $cores = & $cli core list --config-file $config --format json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or !($cores.platforms | Where-Object { $_.id -eq 'arduino:avr' -and $_.installed_version -eq $settings.coreVersion })) {
        throw 'Install arduino:avr@1.8.6 in the isolated toolchain first; see docs/19-uno-sensor-telemetry.md.'
    }
    $variant = if ($EnableMmaAfterVoltageCheck) { 'mma' } else { 'no-mma' }
    $variant += if ($EnableUltrasonic) { '-distance' } else { '-no-distance' }
    $flags = '-DTOBOO_ENABLE_MMA=' + [int][bool]$EnableMmaAfterVoltageCheck + ' -DTOBOO_ENABLE_ULTRASONIC=' + [int][bool]$EnableUltrasonic
    & $cli compile --config-file $config --fqbn $settings.fqbn --warnings all `
        --build-property "compiler.cpp.extra_flags=$flags" `
        --build-path (Join-Path $repositoryPath "tmp/esp32-tools/build-uno-$variant") `
        --output-dir (Join-Path $repositoryPath "artifacts/uno-sensors/$variant") firmware/uno_sensors
    if ($LASTEXITCODE -ne 0) { throw 'UNO firmware compilation failed.' }
    Write-Host "PASS: UNO $variant compiled. No board was flashed. Module wiring and sensor performance remain unverified."
}
finally { Pop-Location }
