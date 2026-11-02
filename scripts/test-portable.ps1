param([Parameter(Mandatory = $true)][string]$ZipPath, [int]$CameraIndex = -1, [string]$BaseZipPath)
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
$resolvedZip = (Resolve-Path -LiteralPath $ZipPath).Path
$testPath = Join-Path $repositoryPath ('artifacts/portable-tests/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $testPath | Out-Null
if ($BaseZipPath) {
    Expand-Archive -LiteralPath (Resolve-Path -LiteralPath $BaseZipPath).Path -DestinationPath $testPath
}
else { Expand-Archive -LiteralPath $resolvedZip -DestinationPath $testPath }
$appPath = (Get-ChildItem -LiteralPath $testPath -Directory | Select-Object -First 1).FullName
if ($BaseZipPath) {
    $patchPath = Join-Path $testPath 'patch'
    Expand-Archive -LiteralPath $resolvedZip -DestinationPath $patchPath
    $manifest = Get-Content -LiteralPath (Join-Path $patchPath 'update-manifest.json') -Raw | ConvertFrom-Json
    $base = Get-Content -LiteralPath (Join-Path $appPath 'build-info.json') -Raw | ConvertFrom-Json
    if ($base.version -ne $manifest.baseVersion -or
        (Get-FileHash -LiteralPath $BaseZipPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.baseArchiveSha256) {
        throw 'Update does not match the supplied base ZIP.'
    }
    Copy-Item -Path (Join-Path $patchPath '*') -Destination $appPath -Recurse -Force
    foreach ($file in $manifest.targetFiles) {
        $path = Join-Path $appPath $file.path
        if (!(Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
            throw "Updated installation differs from the full package: $($file.path)"
        }
    }
    Write-Host "PASS: applied $($manifest.baseVersion) -> $($manifest.targetVersion); all target file hashes match."
}
$executable = Join-Path $appPath 'PanTiltTracker.Desktop.exe'
$runtimeConfig = Get-Content -LiteralPath (Join-Path $appPath 'PanTiltTracker.Desktop.runtimeconfig.json') -Raw | ConvertFrom-Json
if (!$runtimeConfig.runtimeOptions.includedFrameworks -or $runtimeConfig.runtimeOptions.framework) {
    throw 'Expected a self-contained runtime configuration.'
}
$originalRoot = $env:DOTNET_ROOT
$originalLookup = $env:DOTNET_MULTILEVEL_LOOKUP
try {
    $env:DOTNET_ROOT = Join-Path $testPath 'no-installed-dotnet-here'
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $uiArguments = if ($CameraIndex -ge 0) { "--smoke-test-ui --camera-index $CameraIndex" } else { '--smoke-test-ui' }
    foreach ($argument in @('--self-test', $uiArguments)) {
        $process = Start-Process -FilePath $executable -ArgumentList $argument -WorkingDirectory $appPath -WindowStyle Hidden -PassThru
        $null = $process.Handle
        if (!$process.WaitForExit(30000)) {
            Stop-Process -Id $process.Id
            throw "Portable test timed out: $argument"
        }
        $process.Refresh()
        if ($process.ExitCode -ne 0) { throw "Portable test failed ($argument). See $appPath/diagnostics." }
        $process.Dispose()
    }
    $selfTest = Get-Content -LiteralPath (Join-Path $appPath 'diagnostics/self-test.json') -Raw | ConvertFrom-Json
    $uiTest = Get-Content -LiteralPath (Join-Path $appPath 'diagnostics/ui-smoke-test.json') -Raw | ConvertFrom-Json
    if (!$selfTest.success -or !$selfTest.faceModelLoaded -or !$selfTest.faceBlankFramePassed -or
        !$uiTest.success -or $uiTest.frames -lt 12) { throw 'Portable smoke checks did not pass.' }
    if (!$selfTest.runtimeDirectory.StartsWith($appPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The executable did not use its app-local .NET runtime.'
    }
    foreach ($library in $selfTest.nativeLibraries) {
        if (!$library.path.StartsWith($appPath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Native dependency was not loaded from the portable directory: $($library.name)"
        }
    }
    $mode = if ($CameraIndex -ge 0) { "physical camera $CameraIndex" } else { 'synthetic scene' }
    Write-Host "PASS: extracted ZIP, app-local $($selfTest.framework), native OpenCV, and $($uiTest.frames) desktop frames from $mode."
    Write-Host 'Actuator output is disabled. Other camera/computer combinations remain unverified.'
    Write-Host "Reports: $appPath/diagnostics"
}
finally {
    $env:DOTNET_ROOT = $originalRoot
    $env:DOTNET_MULTILEVEL_LOOKUP = $originalLookup
}
