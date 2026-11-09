param([string]$Version)
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
if (!$Version) {
    [xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repositoryPath 'Directory.Build.props') -Raw
    $Version = $buildProperties.Project.PropertyGroup.Version
}
if ($Version -notmatch '^\d+\.\d+\.\d+(-[a-zA-Z0-9.]+)?$') { throw 'Invalid release version.' }
$runtimeVersion = '8.0.33'
$packageName = "toboo-$Version-win-x64-portable"
$outputPath = Join-Path $repositoryPath "artifacts/release/$packageName"
$zipPath = "$outputPath.zip"
if ((Test-Path -LiteralPath $outputPath) -or (Test-Path -LiteralPath $zipPath)) {
    throw "Output already exists: $outputPath. Use a fresh version or preserve/rename the previous output first."
}
Push-Location -LiteralPath $repositoryPath
try {
    if (& git status --porcelain --untracked-files=normal) {
        throw 'Publish from a clean commit or an isolated clean worktree.'
    }
    & "$PSScriptRoot/verify-public-tree.ps1"
    & "$PSScriptRoot/prepare-vc-runtime.ps1"
    $serialRuntime = & "$PSScriptRoot/prepare-serial-runtime.ps1"
    & dotnet publish src/Toboo.Desktop -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true `
        "-p:Version=$Version" "-p:RuntimeFrameworkVersion=$runtimeVersion" `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $outputPath
    if ($LASTEXITCODE -ne 0) { throw 'Portable publish failed.' }
    $licensePath = Join-Path $outputPath 'licenses'
    New-Item -ItemType Directory -Force $licensePath | Out-Null
    Get-ChildItem -LiteralPath 'tmp/portable-deps/minimum' -Filter '*.dll_amd64' | ForEach-Object {
        if ((Get-AuthenticodeSignature -LiteralPath $_.FullName).Status -ne 'Valid') { throw "Invalid VC DLL signature: $($_.Name)" }
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $outputPath ($_.Name -replace '_amd64$', ''))
    }
    Copy-Item tmp/portable-deps/ui/u4 (Join-Path $licensePath 'Microsoft-VC-runtime-en.rtf')
    Copy-Item tmp/portable-deps/ui/u28 (Join-Path $licensePath 'Microsoft-VC-runtime-zh-CN.rtf')
    Copy-Item docs/portable.md (Join-Path $outputPath 'README-portable-zh-CN.md')
    Copy-Item -LiteralPath docs -Destination (Join-Path $outputPath 'docs') -Recurse
    New-Item -ItemType Directory -Force (Join-Path $outputPath 'scripts') | Out-Null
    Copy-Item scripts/install-toboo-harness.ps1 (Join-Path $outputPath 'scripts/install-toboo-harness.ps1')
    # Ship only committed firmware sources, never local board logs or tool caches.
    $firmwareFiles = @(& git ls-files firmware)
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate firmware sources.' }
    $firmwareFiles += @('scripts/prepare-firmware.ps1', 'scripts/build-firmware.ps1', 'scripts/build-uno-sensors.ps1', 'scripts/build-uno-lcd.ps1')
    foreach ($relative in $firmwareFiles) {
        $destination = Join-Path $outputPath $relative
        New-Item -ItemType Directory -Force (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $relative -Destination $destination
    }
    $bridgePath = Join-Path $outputPath 'integrations/toboo-harness'
    New-Item -ItemType Directory -Force $bridgePath | Out-Null
    foreach ($file in @('index.mjs', 'engine.mjs', 'lock.mjs', 'server.mjs', 'package.json', 'README.md')) {
        Copy-Item -LiteralPath (Join-Path 'integrations/toboo-harness' $file) -Destination $bridgePath
    }
    Copy-Item THIRD-PARTY-NOTICES.md $outputPath
    Copy-Item licenses/* $licensePath
    $serialDestination = Join-Path $outputPath 'runtime/python'
    New-Item -ItemType Directory -Force (Split-Path -Parent $serialDestination) | Out-Null
    Copy-Item -LiteralPath $serialRuntime -Destination $serialDestination -Recurse
    Copy-Item -LiteralPath (Join-Path $serialDestination 'LICENSE.txt') -Destination (Join-Path $licensePath 'Python-PSF.txt')
    Copy-Item -LiteralPath (Join-Path $serialDestination 'pyserial-LICENSE.txt') -Destination (Join-Path $licensePath 'pyserial-BSD.txt')
    Set-Content -LiteralPath (Join-Path $outputPath 'portable.mode') -Value 'Logs stay beside the app.' -Encoding UTF8

    # Preserve license/notice files from the exact locked NuGet packages too.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $packageCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    $lock = Get-Content src/Toboo.Desktop/packages.lock.json -Raw | ConvertFrom-Json
    foreach ($property in $lock.dependencies.'net8.0'.PSObject.Properties) {
        if ($property.Value.type -eq 'Project') { continue }
        $id = $property.Name.ToLowerInvariant()
        $packageVersion = $property.Value.resolved
        $nupkg = Join-Path $packageCache "$id/$packageVersion/$id.$packageVersion.nupkg"
        $archive = [IO.Compression.ZipFile]::OpenRead($nupkg)
        try {
            foreach ($entry in $archive.Entries) {
                if ($entry.Name -match '(?i)(license|notice|copying)' -and $entry.Length -gt 0) {
                    $name = "$id-$packageVersion-" + ($entry.FullName -replace '[/\\]', '_')
                    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $licensePath $name), $true)
                }
            }
        }
        finally { $archive.Dispose() }
    }
    Copy-Item src/Toboo.Desktop/packages.lock.json (Join-Path $licensePath 'packages.lock.json')
    $commit = (& git rev-parse HEAD).Trim()
    $dirty = [bool](& git status --porcelain --untracked-files=normal)
    @{
        version = $Version; commit = $commit; workingTreeDirty = $dirty; runtime = ".NET $runtimeVersion";
        platform = 'win-x64'; selfContained = $true; vcRuntime = '14.44.35211.0';
        vcInstallerSha256 = (Get-FileHash tmp/portable-deps/vc_redist.x64.exe -Algorithm SHA256).Hash;
        hardwareOutput = 'disabled'; physicalCameraVerified = $false;
        serialRuntime = (Get-Content config/serial-runtime.json -Raw | ConvertFrom-Json)
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputPath 'build-info.json') -Encoding UTF8
    foreach ($required in @('toboo.exe', 'coreclr.dll', 'OpenCvSharpExtern.dll', 'libSkiaSharp.dll', 'vcruntime140.dll', 'msvcp140.dll', 'models/face/face_detection_yunet_2023mar.onnx', 'models/expression/facial_expression_recognition_mobilefacenet_2022july.onnx', 'scripts/install-toboo-harness.ps1', 'integrations/toboo-harness/index.mjs')) {
        if (!(Test-Path -LiteralPath (Join-Path $outputPath $required))) { throw "Missing portable dependency: $required" }
    }
    if (Get-ChildItem -LiteralPath (Join-Path $outputPath 'models') -Filter '*.onnx' -Recurse | Where-Object { $_.Directory.Name -eq 'drone' }) { throw 'Experimental drone weights must not be bundled.' }
    [IO.Compression.ZipFile]::CreateFromDirectory($outputPath, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $true)
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $packageName.zip" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ASCII
    Write-Host "Portable ZIP: $zipPath"
    Write-Host "SHA256: $hash"
}
finally { Pop-Location }
