param(
    [Parameter(Mandatory = $true)][string]$BaseZipPath,
    [Parameter(Mandatory = $true)][string]$TargetZipPath
)
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
$baseZip = (Resolve-Path -LiteralPath $BaseZipPath).Path
$targetZip = (Resolve-Path -LiteralPath $TargetZipPath).Path
$scratch = Join-Path $repositoryPath ('artifacts/update-builds/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $scratch | Out-Null
Expand-Archive -LiteralPath $baseZip -DestinationPath (Join-Path $scratch 'base')
Expand-Archive -LiteralPath $targetZip -DestinationPath (Join-Path $scratch 'target')
$basePath = (Get-ChildItem -LiteralPath (Join-Path $scratch 'base') -Directory | Select-Object -First 1).FullName
$targetPath = (Get-ChildItem -LiteralPath (Join-Path $scratch 'target') -Directory | Select-Object -First 1).FullName
$base = Get-Content -LiteralPath (Join-Path $basePath 'build-info.json') -Raw | ConvertFrom-Json
$target = Get-Content -LiteralPath (Join-Path $targetPath 'build-info.json') -Raw | ConvertFrom-Json
if ($base.workingTreeDirty -or $target.workingTreeDirty -or $base.platform -ne 'win-x64' -or $target.platform -ne 'win-x64') {
    throw 'Updates require clean Windows x64 release archives.'
}
foreach ($version in @($base.version, $target.version)) {
    if ($version -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$') { throw 'Invalid archive version.' }
}
if ($base.version -eq $target.version) { throw 'Base and target versions must differ.' }
$packageName = "toboo-$($target.version)-update-from-$($base.version)-win-x64"
$outputZip = Join-Path $repositoryPath "artifacts/release/$packageName.zip"
if (Test-Path -LiteralPath $outputZip) { throw 'Update archive already exists.' }
$payload = Join-Path $scratch 'payload'
New-Item -ItemType Directory $payload | Out-Null
$baseFiles = @{}
foreach ($file in Get-ChildItem -LiteralPath $basePath -File -Recurse) {
    $relative = $file.FullName.Substring($basePath.Length + 1).Replace('\', '/')
    $baseFiles[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
$targetFiles = @()
$changedFiles = @()
foreach ($file in Get-ChildItem -LiteralPath $targetPath -File -Recurse | Sort-Object FullName) {
    $relative = $file.FullName.Substring($targetPath.Length + 1).Replace('\', '/')
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $targetFiles += [ordered]@{ path = $relative; sha256 = $hash }
    if (!$baseFiles.ContainsKey($relative) -or $baseFiles[$relative] -ne $hash) {
        $destination = Join-Path $payload $relative
        New-Item -ItemType Directory -Force (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
        $changedFiles += $relative
    }
    $baseFiles.Remove($relative)
}
if ($baseFiles.Count) { throw 'Target removes files. A simple overlay update would leave stale files; use a full package.' }
[ordered]@{
    baseVersion = $base.version; targetVersion = $target.version
    baseArchiveSha256 = (Get-FileHash -LiteralPath $baseZip -Algorithm SHA256).Hash.ToLowerInvariant()
    targetArchiveSha256 = (Get-FileHash -LiteralPath $targetZip -Algorithm SHA256).Hash.ToLowerInvariant()
    changedFiles = $changedFiles; targetFiles = $targetFiles
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload 'update-manifest.json') -Encoding UTF8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'verify-update.ps1') -Destination $payload
@"
# toboo $($target.version) 增量覆盖更新

本增量包仅适用于完整的 toboo $($base.version) Windows x64 便携版，不能单独运行。
先关闭 toboo，把 ZIP 内全部文件解压到原来的 toboo.exe 所在目录，
合并子目录并替换同名文件。保留原有 models、runtime 等目录，然后重新运行 toboo.exe。
不需要重新烧录固件。本机 LOCALAPPDATA/PanTiltTracker 中的设备标定保持原样。

可选：在程序目录打开 PowerShell，运行下面的命令，检查覆盖结果。
powershell -NoProfile -ExecutionPolicy Bypass -File ./verify-update.ps1

在云台卡片点击「使用视场角估算并保存」即可快速补齐缺少的相机模型。
操作详情见 docs/releases/v$($target.version).md。
"@ | Set-Content -LiteralPath (Join-Path $payload 'README-update.txt') -Encoding UTF8
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $outputZip, [IO.Compression.CompressionLevel]::Optimal, $false)
$archiveHash = (Get-FileHash -LiteralPath $outputZip -Algorithm SHA256).Hash.ToLowerInvariant()
"$archiveHash  $packageName.zip" | Set-Content -LiteralPath "$outputZip.sha256" -Encoding ASCII
Write-Host "Update ZIP: $outputZip"
Write-Host "Changed files: $($changedFiles.Count); target files: $($targetFiles.Count)"
