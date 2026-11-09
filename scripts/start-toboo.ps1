param([switch]$ServoCalibration, [switch]$Sensors)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'start-face-preview.ps1') -ServoCalibration:$ServoCalibration -Sensors:$Sensors
