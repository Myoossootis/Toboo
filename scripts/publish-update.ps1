param(
    [Parameter(Mandatory = $true)][string]$BaseZipPath,
    [Parameter(Mandatory = $true)][string]$TargetZipPath
)
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
$baseZip = (Resolve-Path -LiteralPath $BaseZipPath).Path
$targetZip = (Resolve-Path -LiteralPath $TargetZipPath).Path
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Package-Files($archive) {
    $files = @{}
    $prefix = $null
    foreach ($entry in $archive.Entries) {
        if (!$entry.Name) { continue }
        $parts = $entry.FullName -split '/', 2
        if ($parts.Count -ne 2 -or !$parts[0] -or $parts[1] -match '(^|/)\.\.?(/|$)|\\|^/') { throw 'Invalid package path.' }
        if ($null -eq $prefix) { $prefix = $parts[0] }
        if ($prefix -ne $parts[0] -or $files.ContainsKey($parts[1])) { throw 'Ambiguous package structure.' }
        $files[$parts[1]] = $entry
    }
    return ,$files
}
function Read-PackageInfo($files) {
    if (!$files.ContainsKey('build-info.json')) { throw 'Missing build metadata.' }
    $reader = [IO.StreamReader]::new($files['build-info.json'].Open())
    try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
}
function Entry-Hash($entry) {
    $stream = $entry.Open()
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose(); $stream.Dispose() }
}
function Add-Text($archive, [string]$name, [string]$content) {
    $entry = $archive.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
    $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
    try { $writer.Write($content) } finally { $writer.Dispose() }
}
$baseArchive = [IO.Compression.ZipFile]::OpenRead($baseZip)
$targetArchive = $null
$output = $null
try {
    $targetArchive = [IO.Compression.ZipFile]::OpenRead($targetZip)
    $baseFiles = Package-Files $baseArchive
    $targetEntries = Package-Files $targetArchive
    $base = Read-PackageInfo $baseFiles
    $target = Read-PackageInfo $targetEntries
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
    foreach ($relative in $baseFiles.Keys) {
        if (!$targetEntries.ContainsKey($relative)) { throw 'Target removes files; use a full package instead of an overlay.' }
    }
    $targetFiles = @()
    $changedFiles = @()
    $output = [IO.Compression.ZipFile]::Open($outputZip, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($relative in $targetEntries.Keys | Sort-Object) {
        $entry = $targetEntries[$relative]
        $hash = Entry-Hash $entry
        $targetFiles += [ordered]@{ path = $relative; sha256 = $hash }
        if (!$baseFiles.ContainsKey($relative) -or (Entry-Hash $baseFiles[$relative]) -ne $hash) {
            $destination = $output.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $sourceStream = $entry.Open(); $destinationStream = $destination.Open()
            try { $sourceStream.CopyTo($destinationStream) }
            finally { $destinationStream.Dispose(); $sourceStream.Dispose() }
            $changedFiles += $relative
        }
    }
    $manifest = [ordered]@{
        baseVersion = $base.version; targetVersion = $target.version
        baseArchiveSha256 = (Get-FileHash -LiteralPath $baseZip -Algorithm SHA256).Hash.ToLowerInvariant()
        targetArchiveSha256 = (Get-FileHash -LiteralPath $targetZip -Algorithm SHA256).Hash.ToLowerInvariant()
        changedFiles = $changedFiles; targetFiles = $targetFiles
    } | ConvertTo-Json -Depth 5
    Add-Text $output 'update-manifest.json' $manifest
    Add-Text $output 'verify-update.ps1' ([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'verify-update.ps1')))
    $instructions = @"
# toboo $($target.version) 增量覆盖更新

本包适用于完整 toboo $($base.version) Windows x64 便携版，不能独立运行。
先关闭 toboo，将全部文件解压到原 toboo.exe 所在目录，合并子目录并替换同名文件。
保留原有 models、runtime 等目录，然后重新运行 toboo.exe。无需重新烧录固件。
本机 LOCALAPPDATA/PanTiltTracker 中的设备设置和标定保留。

在程序目录可运行以下命令检查覆盖结果：
powershell -NoProfile -ExecutionPolicy Bypass -File ./verify-update.ps1

变更及操作步骤见 docs/releases/v$($target.version).md。
"@
    Add-Text $output 'README-update.txt' $instructions
    $output.Dispose(); $output = $null
    $hash = (Get-FileHash -LiteralPath $outputZip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $packageName.zip" | Set-Content -LiteralPath "$outputZip.sha256" -Encoding ASCII
    Write-Host "Update ZIP: $outputZip"
    Write-Host "Changed files: $($changedFiles.Count); target files: $($targetFiles.Count)"
}
finally {
    if ($null -ne $output) { $output.Dispose() }
    if ($null -ne $targetArchive) { $targetArchive.Dispose() }
    $baseArchive.Dispose()
}
