param(
    [string]$Compiler = 'g++',
    [ValidateSet('bench', 'single')][string]$Target = 'bench'
)

$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repositoryPath
try {
    $settings = Get-Content firmware/toolchain.json -Raw | ConvertFrom-Json
    $cli = Join-Path $repositoryPath 'tmp/esp32-tools/cli/arduino-cli.exe'
    $config = Join-Path $repositoryPath 'tmp/esp32-tools/arduino-cli.yaml'
    if (!(Test-Path -LiteralPath $cli) -or !(Test-Path -LiteralPath $config)) {
        throw 'Install the isolated Arduino CLI toolchain first; see firmware/README.md.'
    }
    $version = & $cli version --format json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $version.VersionString -ne $settings.arduinoCliVersion) {
        throw 'Arduino CLI version does not match firmware/toolchain.json.'
    }
    $cores = & $cli core list --config-file $config --format json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or !($cores.platforms | Where-Object {
        $_.id -eq 'esp32:esp32' -and $_.installed_version -eq ($settings.core -split '@')[1]
    })) { throw 'Pinned ESP32 core is not installed.' }

    $isSingle = $Target -eq 'single'
    $artifactName = if ($isSingle) { 'esp32-single' } else { 'esp32-bench' }
    $sketchName = if ($isSingle) { 'esp32_single_servo' } else { 'esp32_bench' }
    $testSource = if ($isSingle) { 'single_servo_controller_test.cpp' } else { 'bench_controller_test.cpp' }
    $outputPath = Join-Path $repositoryPath "artifacts/$artifactName/build"
    New-Item -ItemType Directory -Force $outputPath | Out-Null
    $testExe = Join-Path $outputPath 'controller_test.exe'
    & $Compiler -std=c++11 -Wall -Wextra -Werror -pedantic "firmware/tests/$testSource" -o $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Firmware host test compilation failed.' }
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw 'Firmware host tests failed.' }
    & $cli compile --config-file $config --fqbn $settings.fqbn --warnings all `
        --build-path (Join-Path $repositoryPath "tmp/esp32-tools/build-$Target") `
        --output-dir $outputPath "firmware/$sketchName"
    if ($LASTEXITCODE -ne 0) { throw 'ESP32 firmware compilation failed.' }
    Write-Host 'PASS: host logic tests and ESP32-S3 firmware build. No device was flashed.'
}
finally { Pop-Location }
