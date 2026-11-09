param([switch]$ServoCalibration, [switch]$Sensors, [switch]$Expression)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'start-face-preview.ps1') -ServoCalibration:$ServoCalibration -Sensors:$Sensors -Expression:$Expression
