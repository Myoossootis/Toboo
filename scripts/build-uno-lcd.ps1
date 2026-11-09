param([string]$Compiler = 'g++')
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repositoryPath
try {
    $cli = Join-Path $repositoryPath 'tmp/esp32-tools/cli/arduino-cli.exe'
    $config = Join-Path $repositoryPath 'tmp/esp32-tools/arduino-cli.yaml'
    $settings = Get-Content firmware/uno-toolchain.json -Raw | ConvertFrom-Json
    $version = & $cli version --format json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $version.VersionString -ne $settings.arduinoCliVersion) { throw 'Pinned Arduino CLI required.' }
    $cores = & $cli core list --config-file $config --format json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or !($cores.platforms | Where-Object { $_.id -eq 'arduino:avr' -and $_.installed_version -eq $settings.coreVersion })) { throw 'Install arduino:avr@1.8.6 first.' }
    $libraries = & $cli lib list --config-file $config --format json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or !($libraries.installed_libraries | Where-Object { $_.library.name -eq 'hd44780' -and $_.library.version -eq '1.3.2' })) { throw 'Install pinned LCD library: arduino-cli lib install hd44780@1.3.2 --config-file tmp/esp32-tools/arduino-cli.yaml' }
    $output = Join-Path $repositoryPath 'artifacts/uno-lcd'
    New-Item -ItemType Directory -Force $output | Out-Null
    & $Compiler -std=c++11 -Wall -Wextra -Werror -pedantic firmware/tests/lcd_protocol_test.cpp -o "$output/lcd_protocol_test.exe"
    if ($LASTEXITCODE -ne 0) { throw 'LCD protocol test build failed.' }
    & "$output/lcd_protocol_test.exe"
    if ($LASTEXITCODE -ne 0) { throw 'LCD protocol tests failed.' }
    & ./tmp/esp32-tools/host/Scripts/python.exe -m unittest discover -s firmware/tests -p lcd_expression_test.py
    if ($LASTEXITCODE -ne 0) { throw 'LCD host tests failed.' }
    & $cli compile --config-file $config --fqbn $settings.fqbn --warnings all `
        --build-path (Join-Path $repositoryPath 'tmp/esp32-tools/build-uno-lcd') --output-dir $output firmware/uno_lcd
    if ($LASTEXITCODE -ne 0) { throw 'UNO LCD compilation failed.' }
    Copy-Item tmp/esp32-tools/user/libraries/hd44780/license.txt "$output/hd44780-GPL-3.0.txt"
    Write-Host 'PASS: LCD protocol/host tests and UNO build. No board was flashed; physical LCD remains unverified.'
}
finally { Pop-Location }
