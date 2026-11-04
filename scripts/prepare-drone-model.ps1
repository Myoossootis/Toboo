param([string]$Destination)
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
if (!$Destination) { $Destination = Join-Path $repositoryPath 'models/drone/best.onnx' }
$expected = '4d078f4235221dbaa1299796d5a6bd5cc8f2210013b07e8199e5d06e88b43e8a'
if (Test-Path -LiteralPath $Destination) {
    if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) {
        throw 'Existing model differs; choose a new -Destination. Nothing was overwritten.'
    }
}
else {
    New-Item -ItemType Directory -Force (Split-Path -Parent ([IO.Path]::GetFullPath($Destination))) | Out-Null
    $temporary = "$Destination.$([Guid]::NewGuid().ToString('N')).download"
    try {
        Invoke-WebRequest -Uri 'https://media.githubusercontent.com/media/eahmeddarwish/drone-detection-yolov5/ca515c165600a776a04d9f98db83efe829f18603/models/best.onnx' -OutFile $temporary
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Model SHA256 mismatch.' }
        Move-Item -LiteralPath $temporary -Destination $Destination
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
Write-Host "Verified local experimental model: $([IO.Path]::GetFullPath($Destination))"
Write-Host 'Weights are not committed or bundled. See models/drone/README.md for provenance and licensing status.'
