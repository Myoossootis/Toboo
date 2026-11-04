param([string]$Destination, [ValidateSet('baseline', 'candidate', 'yolo11')][string]$Variant = 'baseline')
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
$file = switch ($Variant) { 'candidate' { 'best2.onnx' } 'yolo11' { 'yolo11-drone-bird.onnx' } default { 'best.onnx' } }
if (!$Destination) { $Destination = Join-Path $repositoryPath "models/drone/$file" }
$expected = switch ($Variant) {
    'candidate' { 'ad7dbd93bb7da4e20651d49d7dff399af8ffc7a2c69a4214fe0a682179318fed' }
    'yolo11' { 'f3b238b537fe420f21ce1e2504055a21816b5ad80a8d9346f56ac3bced1afc29' }
    default { '4d078f4235221dbaa1299796d5a6bd5cc8f2210013b07e8199e5d06e88b43e8a' }
}
$url = if ($Variant -eq 'yolo11') {
    'https://raw.githubusercontent.com/kanishk083/DRONE-DETECTION/ee6a84ac18f188f9a85363cebc2dabea257f98de/models/best.onnx'
} else { "https://media.githubusercontent.com/media/eahmeddarwish/drone-detection-yolov5/ca515c165600a776a04d9f98db83efe829f18603/models/$file" }
if (Test-Path -LiteralPath $Destination) {
    if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
        throw 'Existing model differs; choose a new -Destination. Nothing was overwritten.'
    }
}
else {
    New-Item -ItemType Directory -Force (Split-Path -Parent ([IO.Path]::GetFullPath($Destination))) | Out-Null
    $temporary = "$Destination.$([Guid]::NewGuid().ToString('N')).download"
    try {
        Invoke-WebRequest -Uri $url -OutFile $temporary
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Model SHA256 mismatch.' }
        Move-Item -LiteralPath $temporary -Destination $Destination
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
Write-Host "Verified local experimental model: $([IO.Path]::GetFullPath($Destination))"
Write-Host 'Weights are not committed or bundled. See models/drone/README.md for provenance and licensing status.'
