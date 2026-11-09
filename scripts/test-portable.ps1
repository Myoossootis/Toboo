param([Parameter(Mandatory = $true)][string]$ZipPath, [int]$CameraIndex = -1, [string]$BaseZipPath, [string]$LcdPort)
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
$executable = Join-Path $appPath 'toboo.exe'
$buildInfo = Get-Content -LiteralPath (Join-Path $appPath 'build-info.json') -Raw | ConvertFrom-Json
if ($buildInfo.workingTreeDirty -or $buildInfo.commit -notmatch '^[0-9a-f]{40}$') { throw 'Release must identify a clean source commit.' }
foreach ($required in @('README-portable-zh-CN.md', 'docs/harness.md', 'docs/lcd.md', 'System.IO.Ports.dll', 'firmware/uno_lcd/uno_lcd.ino', 'scripts/install-toboo-harness.ps1', 'integrations/toboo-harness/index.mjs', 'integrations/toboo-harness/engine.mjs', 'integrations/toboo-harness/server.mjs', 'integrations/toboo-harness/lock.mjs')) {
    if (!(Test-Path -LiteralPath (Join-Path $appPath $required))) { throw "Missing packaged feature dependency: $required" }
}
$unexpected = @(Get-ChildItem -LiteralPath $appPath -Recurse -Force -File | Where-Object {
    $_.FullName.Substring($appPath.Length + 1) -match '^(local|hardware|output|data|calibration|tmp|artifacts)[\\/]|(^|[\\/])(connection|client)\.json$|(^|[\\/])AGENTS\.md$'
})
if ($unexpected.Count) { throw 'Local-only files found in the portable package.' }
$runtimeConfig = Get-Content -LiteralPath (Join-Path $appPath 'toboo.runtimeconfig.json') -Raw | ConvertFrom-Json
if (!$runtimeConfig.runtimeOptions.includedFrameworks -or $runtimeConfig.runtimeOptions.framework) {
    throw 'Expected a self-contained runtime configuration.'
}
$originalRoot = $env:DOTNET_ROOT
$originalLookup = $env:DOTNET_MULTILEVEL_LOOKUP
$originalPath = $env:PATH
$originalPythonHome = $env:PYTHONHOME
$originalPythonPath = $env:PYTHONPATH
try {
    $env:DOTNET_ROOT = Join-Path $testPath 'no-installed-dotnet-here'
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:PATH = Join-Path $env:SystemRoot 'System32'
    $env:PYTHONHOME = Join-Path $testPath 'wrong-python-home'
    $env:PYTHONPATH = Join-Path $testPath 'wrong-python-modules'
    $uiArguments = if ($CameraIndex -ge 0) { "--smoke-test-ui --camera-index $CameraIndex" } else { '--smoke-test-ui' }
    foreach ($argument in @('--self-test', '--serial-runtime-self-test', $uiArguments)) {
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
    $serialTest = Get-Content -LiteralPath (Join-Path $appPath 'diagnostics/serial-runtime-self-test.json') -Raw | ConvertFrom-Json
    if (!$serialTest.success -or !$serialTest.bundledRuntime -or $serialTest.serialOpened) { throw 'Bundled serial runtime self-test failed.' }
    if (!$selfTest.success -or !$selfTest.faceModelLoaded -or $selfTest.faceDetector -ne 'yunet_2023mar' -or !$selfTest.faceBlankFramePassed -or
        !$selfTest.expressionModelLoaded -or !$uiTest.success -or $uiTest.frames -lt 12) { throw 'Portable smoke checks did not pass.' }
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
    Write-Host 'PASS: bundled servo, face-follow and sensor helper imports with system Python unavailable and hostile Python environment variables.'
    Write-Host "Reports: $appPath/diagnostics"
    if ($LcdPort) {
        if ($LcdPort -notmatch '^COM[1-9][0-9]*$') { throw 'Specify the confirmed UNO COM port explicitly.' }
        $process = Start-Process -FilePath $executable -ArgumentList "--lcd-self-test --lcd-port $LcdPort" -WorkingDirectory $appPath -WindowStyle Hidden -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw 'Packaged native LCD serial check failed. See diagnostics/startup-error.json.' }
        $lcd = Get-Content -LiteralPath (Join-Path $appPath 'diagnostics/lcd-self-test.json') -Raw | ConvertFrom-Json
        if (!$lcd.success -or !$lcd.nativeSerial -or $lcd.pythonRequired) { throw 'Packaged LCD check did not pass.' }
        Write-Host 'PASS: native LCD handshake and display acknowledgements; no source checkout or Python runtime required.'
    }
}
finally {
    $env:DOTNET_ROOT = $originalRoot
    $env:DOTNET_MULTILEVEL_LOOKUP = $originalLookup
    $env:PATH = $originalPath
    $env:PYTHONHOME = $originalPythonHome
    $env:PYTHONPATH = $originalPythonPath
}
