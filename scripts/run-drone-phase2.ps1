param([string]$OutputDirectory, [string]$PythonPath)
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repositoryPath ('artifacts/drone-phase2/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')) }
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh output directory to preserve previous experiments.' }
if (!$PythonPath) { $PythonPath = Join-Path $repositoryPath 'tmp/drone-eval-venv/Scripts/python.exe' }
if (!(Test-Path -LiteralPath $PythonPath)) { throw 'Prepare the optional Python evaluation venv first; see docs/13-drone-phase2.md.' }
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
Push-Location -LiteralPath $repositoryPath
try {
    & "$PSScriptRoot/prepare-drone-evaluation.ps1"
    foreach ($variant in @('baseline', 'candidate', 'yolo11')) { & "$PSScriptRoot/prepare-drone-model.ps1" -Variant $variant }
    $runner = Join-Path $OutputDirectory 'runner'
    & dotnet publish src/PanTiltTracker.App -c Release -r win-x64 --self-contained false -p:RestoreLockedMode=true -o $runner
    if ($LASTEXITCODE -ne 0) { throw 'Experiment snapshot build failed.' }
    $application = Join-Path $runner 'PanTiltTracker.App.dll'
    @{sourceCommit=(& git rev-parse HEAD).Trim(); workingTreeDirty=[bool](& git status --porcelain);
        runnerSha256=(Get-FileHash -LiteralPath $application -Algorithm SHA256).Hash.ToLowerInvariant();
        hardwareOutput='disabled'; note='Immutable local experiment snapshot, not a release.'} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'experiment-build.json') -Encoding utf8
    $manifest = Get-Content docs/samples/halmstad-phase2.json -Raw | ConvertFrom-Json
    $index = @()
    foreach ($sequence in $manifest.sequences) {
        $video = Join-Path $repositoryPath "data/raw/drone-detection/$($sequence.name).mp4"
        $labels = Join-Path $repositoryPath "data/raw/drone-detection/$($sequence.name)_LABELS.mat"
        $reference = Join-Path $OutputDirectory "references/$($sequence.name).csv"
        & $PythonPath "$PSScriptRoot/convert-halmstad-labels.py" --labels $labels --frames $sequence.frames --out $reference
        if ($LASTEXITCODE -ne 0) { throw 'Reference conversion failed.' }
        $modes = @('baseline', 'candidate', 'hybrid')
        if ($sequence.name -like 'V_DRONE_*') { $modes += 'manual-csrt' }
        if ($sequence.split -eq 'development') { $modes += 'yolo11' }
        foreach ($mode in $modes) {
            $experiment = Join-Path $OutputDirectory "$($sequence.name)/$mode"
            if ($mode -eq 'manual-csrt') {
                # This baseline alone receives the official first-frame box. Automatic modes never do.
                $first = Import-Csv -LiteralPath $reference | Select-Object -First 1
                if ($first.present -ne '1') { throw 'Manual baseline requires a visible first frame.' }
                $culture = [Globalization.CultureInfo]::InvariantCulture
                $x = [Math]::Floor([double]::Parse($first.x,$culture)); $y = [Math]::Floor([double]::Parse($first.y,$culture))
                $w = [Math]::Ceiling([double]::Parse($first.x,$culture)+[double]::Parse($first.width,$culture))-$x
                $h = [Math]::Ceiling([double]::Parse($first.y,$culture)+[double]::Parse($first.height,$culture))-$y
                & dotnet $application track-video --input $video --algorithm csrt --roi "$x,$y,$w,$h" --out $experiment | Out-Host
            }
            else {
                $model = switch ($mode) { 'baseline' {'best.onnx'} 'yolo11' {'yolo11-drone-bird.onnx'} default {'best2.onnx'} }
                $arguments = @($application,'detect-drone','--input',$video,'--model',"models/drone/$model",'--out',$experiment)
                if ($mode -eq 'hybrid') { $arguments += '--tracking' }
                & dotnet @arguments | Out-Host
            }
            if ($LASTEXITCODE -ne 0) { throw "Experiment failed: $($sequence.name) / $mode" }
            & dotnet $application evaluate-drone --reference $reference --telemetry (Join-Path $experiment 'telemetry.csv') --fps $sequence.fps --out $experiment | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Evaluation failed. Frame alignment was not repaired automatically.' }
            $index += @{sequence=$sequence.name; split=$sequence.split; algorithm=$mode; report="$($sequence.name)/$mode/evaluation.json"}
            $index | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'index.json') -Encoding utf8
        }
    }
    Write-Host "Reports: $OutputDirectory"
}
finally { Pop-Location }
