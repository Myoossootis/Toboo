param()
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repositoryPath
try {
    # Inspect the public working tree (staged plus new files), never ignored archives.
    $paths = @(& git -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate public source files.' }
    $paths = @($paths | Sort-Object -Unique | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
    $privatePaths = @($paths | Where-Object {
        $_ -match '^(local|hardware|output|data|calibration|hw|course|coursework|references|artifacts|tmp)/' -or
        $_ -eq 'AGENTS.md' -or $_ -match '(^|/)(\.toboo-harness|\.dsh)/' -or
        $_ -match '^docs/(samples/|\d\d-)' -or $_ -match '(^|/)PanTiltTracker\.[^/]+/'
    })
    if ($privatePaths.Count) { throw ('Local-only or obsolete paths in public source: ' + ($privatePaths -join ', ')) }
    $textPaths = @($paths | Where-Object { [IO.Path]::GetExtension($_) -in '.md', '.cs', '.axaml', '.json', '.ps1', '.mjs', '.yml', '.csv' })
    # Construct the markers so this guard does not match its own source.
    $markers = @('EESM' + '5840', 'Eric' + 'Dictionary', '期' + '末', '[A-Za-z]:[/\\]+(?:Users|Microsensor)[/\\]', 'wxid' + '_')
    $matches = @($textPaths | Select-String -Pattern $markers)
    if ($matches.Count) {
        throw ('Private material markers in public source: ' + (($matches | ForEach-Object { $_.Path + ':' + $_.LineNumber }) -join ', '))
    }
    Write-Host "PASS: $($paths.Count) public source files; local archives and known private markers excluded."
}
finally { Pop-Location }
