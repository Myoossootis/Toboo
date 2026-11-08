param([switch]$ServoCalibration)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'start-face-preview.ps1') -ServoCalibration:$ServoCalibration
