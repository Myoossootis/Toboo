param()
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
$metadata = Get-Content (Join-Path $repositoryPath 'models/speech/model.json') -Raw | ConvertFrom-Json
function Test-Model([string]$Path, $metadata) {
    (Test-Path -LiteralPath $Path) -and (Get-Item -LiteralPath $Path).Length -eq $metadata.bytes -and
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -eq $metadata.sha256
}
function Prepare-Model($metadata) {
    $destination = Join-Path $repositoryPath ('models/speech/' + $metadata.file)
    if (Test-Model $destination $metadata) { Write-Host "Speech model verified: $($metadata.file)"; return }
    $temporary = "$destination.$([Guid]::NewGuid().ToString('N')).part"
    try {
        foreach ($url in @($metadata.source, $metadata.mirror)) {
            try {
                Invoke-WebRequest -Uri $url -OutFile $temporary -TimeoutSec 300
                if (!(Test-Model $temporary $metadata)) { throw 'Speech model size or SHA256 mismatch.' }
                Move-Item -LiteralPath $temporary -Destination $destination -Force
                Write-Host "Speech model downloaded and verified: $($metadata.file)"
                return
            }
            catch { Write-Warning $_.Exception.Message }
        }
        throw 'Could not prepare the pinned speech model.'
    }
    finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
Prepare-Model $metadata
Prepare-Model $metadata.vad
