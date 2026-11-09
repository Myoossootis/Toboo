# Compatibility alias. The canonical desktop launcher is start-toboo.ps1.
param([switch]$ServoCalibration, [switch]$Sensors, [switch]$Expression)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'start-toboo.ps1') -ServoCalibration:$ServoCalibration -Sensors:$Sensors -Expression:$Expression
