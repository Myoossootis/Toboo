param()
$ErrorActionPreference = 'Stop'
$installPath = $PSScriptRoot
$manifestPath = Join-Path $installPath 'update-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$build = Get-Content -LiteralPath (Join-Path $installPath 'build-info.json') -Raw | ConvertFrom-Json
if ($build.version -ne $manifest.targetVersion) { throw 'Version does not match the update. Copy all update files beside toboo.exe.' }
$prefix = [IO.Path]::GetFullPath($installPath).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
foreach ($file in $manifest.targetFiles) {
    $path = [IO.Path]::GetFullPath((Join-Path $installPath $file.path))
    if (!$path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid file path in update manifest.' }
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
        throw "Missing or different file: $($file.path). Close toboo and copy all update files again."
    }
}
Write-Host "PASS: toboo $($manifest.targetVersion), all $(@($manifest.targetFiles).Count) application files match the full release."
