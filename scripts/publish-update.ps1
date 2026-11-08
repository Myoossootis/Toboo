param(
    [Parameter(Mandatory = $true)][string]$BaseZip,
    [Parameter(Mandatory = $true)][string]$TargetDirectory
)
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
$baseArchive = (Resolve-Path -LiteralPath $BaseZip).Path
$targetPath = (Resolve-Path -LiteralPath $TargetDirectory).Path.TrimEnd('\', '/')
$target = Get-Content -LiteralPath (Join-Path $targetPath 'build-info.json') -Raw | ConvertFrom-Json
if ($target.workingTreeDirty) { throw 'Publish updates only from a clean committed build.' }
$scratch = Join-Path $repositoryPath ('artifacts/update-build/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $scratch | Out-Null
Expand-Archive -LiteralPath $baseArchive -DestinationPath (Join-Path $scratch 'base')
$baseFolders = @(Get-ChildItem -LiteralPath (Join-Path $scratch 'base') -Directory)
if ($baseFolders.Count -ne 1) { throw 'Expected one application directory in the base portable ZIP.' }
$basePath = $baseFolders[0].FullName
$base = Get-Content -LiteralPath (Join-Path $basePath 'build-info.json') -Raw | ConvertFrom-Json
foreach ($metadata in @($base, $target)) {
    if ($metadata.platform -ne 'win-x64' -or !$metadata.selfContained -or
        $metadata.version -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$') { throw 'Unsupported portable metadata.' }
}
if ($base.version -eq $target.version) { throw 'Base and target versions must differ.' }

function Get-PortableFiles([string]$Root) {
    @(Get-ChildItem -LiteralPath $Root -Recurse -File | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{
            path = $_.FullName.Substring($Root.Length + 1).Replace('\', '/')
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            bytes = $_.Length
        }
    })
}
$baseFiles = @{}
foreach ($file in (Get-PortableFiles $basePath)) { $baseFiles[$file.path] = $file }
$targetFiles = @(Get-PortableFiles $targetPath)
$targetNames = @{}
foreach ($file in $targetFiles) { $targetNames[$file.path] = $true }
$removed = @($baseFiles.Keys | Where-Object { !$targetNames.ContainsKey($_) })
if ($removed.Count -gt 0) { throw "Manual overlay cannot remove old files. Use the full ZIP. Removed: $($removed -join ', ')" }
$changed = @($targetFiles | Where-Object { !$baseFiles.ContainsKey($_.path) -or $baseFiles[$_.path].sha256 -ne $_.sha256 })
$patchPath = Join-Path $scratch 'patch'
New-Item -ItemType Directory -Force $patchPath | Out-Null
foreach ($file in $changed) {
    $destination = Join-Path $patchPath $file.path
    New-Item -ItemType Directory -Force (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $targetPath $file.path) -Destination $destination
}
@{
    baseVersion = $base.version; targetVersion = $target.version; platform = 'win-x64'
    baseArchiveSha256 = (Get-FileHash -LiteralPath $baseArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    targetCommit = $target.commit; changedFiles = $changed; targetFiles = $targetFiles
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $patchPath 'update-manifest.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $repositoryPath 'docs/11-portable-update.md') -Destination (Join-Path $patchPath 'UPDATE-README.md')
$name = "toboo-$($target.version)-update-from-$($base.version)-win-x64.zip"
$output = Join-Path (Split-Path -Parent $targetPath) $name
if (Test-Path -LiteralPath $output) { throw "Output already exists: $output" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($patchPath, $output, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $name" | Set-Content -LiteralPath "$output.sha256" -Encoding ASCII
Write-Host "Update ZIP: $output ($($changed.Count) changed/new files)"
Write-Host "SHA256: $hash"
