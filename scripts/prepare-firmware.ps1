param()
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repositoryPath
try {
    $settings = Get-Content firmware/toolchain.json -Raw | ConvertFrom-Json
    $toolsPath = Join-Path $repositoryPath 'tmp/esp32-tools'
    New-Item -ItemType Directory -Force $toolsPath | Out-Null
    $archivePath = Join-Path $toolsPath 'arduino-cli.zip'
    if (!(Test-Path -LiteralPath $archivePath)) {
        Invoke-WebRequest -Uri $settings.arduinoCliWindowsUrl -OutFile $archivePath
    }
    if ((Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash -ne $settings.arduinoCliWindowsSha256) {
        throw 'Arduino CLI archive checksum mismatch; no package was executed.'
    }
    Expand-Archive -LiteralPath $archivePath -DestinationPath (Join-Path $toolsPath 'cli') -Force
    $cli = Join-Path $toolsPath 'cli/arduino-cli.exe'
    $configPath = Join-Path $toolsPath 'arduino-cli.yaml'
    $portableToolsPath = $toolsPath.Replace('\', '/')
    @"
board_manager:
  additional_urls:
    - $($settings.packageIndex)
directories:
  data: $portableToolsPath/data
  downloads: $portableToolsPath/downloads
  user: $portableToolsPath/user
"@ | Set-Content -LiteralPath $configPath -Encoding utf8
    & $cli core update-index --config-file $configPath
    if ($LASTEXITCODE -ne 0) { throw 'Arduino package index update failed.' }
    & $cli core install $settings.core --config-file $configPath
    if ($LASTEXITCODE -ne 0) { throw 'Pinned ESP32 toolchain installation failed.' }
    Write-Host 'Installed the isolated Arduino toolchain. No device was flashed.'
}
finally { Pop-Location }
