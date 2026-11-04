param()
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $repositoryPath 'docs/samples/halmstad-phase2.json') -Raw | ConvertFrom-Json
$destination = Join-Path $repositoryPath 'data/raw/drone-detection'
New-Item -ItemType Directory -Force $destination | Out-Null
foreach ($sequence in $manifest.sequences) {
    foreach ($asset in @(@{name="$($sequence.name).mp4"; hash=$sequence.videoSha256},
                          @{name="$($sequence.name)_LABELS.mat"; hash=$sequence.labelsSha256})) {
        if ($asset.name -notmatch '^V_(DRONE|BIRD|AIRPLANE)_\d{3}(\.mp4|_LABELS\.mat)$') { throw 'Unexpected manifest filename.' }
        $target = Join-Path $destination $asset.name
        if (!(Test-Path -LiteralPath $target)) {
            $temporary = "$target.$([Guid]::NewGuid().ToString('N')).download"
            try {
                Invoke-WebRequest -Uri "https://raw.githubusercontent.com/$($manifest.repository)/$($manifest.commit)/$($manifest.path)/$($asset.name)" -OutFile $temporary
                if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.hash) { throw 'Dataset SHA256 mismatch.' }
                Move-Item -LiteralPath $temporary -Destination $target
            }
            finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
        }
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -ne $asset.hash) { throw "Existing dataset file differs: $target" }
    }
}
Write-Host 'Verified five external sequences and official labels. No data files are committed.'
