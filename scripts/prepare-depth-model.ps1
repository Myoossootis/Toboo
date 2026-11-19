param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$metadata = Get-Content (Join-Path $root 'models/depth/model.json') -Raw | ConvertFrom-Json
$destination = Join-Path $root "models/depth/$($metadata.file)"
function Test-DepthModel([string]$path) {
    (Test-Path -LiteralPath $path) -and (Get-Item -LiteralPath $path).Length -eq $metadata.bytes -and
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $metadata.sha256
}
if (Test-DepthModel $destination) { Write-Host 'Depth model verified.'; return }
$temporary = "$destination.$([Guid]::NewGuid().ToString('N')).part"
try {
    Invoke-WebRequest -Uri $metadata.source -OutFile $temporary -TimeoutSec 300
    if (!(Test-DepthModel $temporary)) { throw 'Depth model size or SHA256 mismatch.' }
    Move-Item -LiteralPath $temporary -Destination $destination -Force
    Write-Host 'Depth model downloaded and verified.'
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
