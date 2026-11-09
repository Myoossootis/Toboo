param([string]$HarnessHome = (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.dsh'))
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
$pluginPath = (Join-Path $repositoryPath 'integrations/toboo-harness/index.mjs').Replace('\','/')
$overlayPath = Join-Path $HarnessHome 'profiles/desktop/cordis.patch.yml'
if (!(Test-Path -LiteralPath $overlayPath)) { throw 'Start DeepSeek Harness once to create its desktop profile.' }
$taskText = [IO.File]::ReadAllText($overlayPath)
$taskId = 'toboo-companion-bridge'
$taskMatches = [regex]::Matches($taskText, '(?m)^\s*-\s+id:\s*toboo-companion-bridge\s*$')
if ($taskMatches.Count -gt 0) {
    if ($taskMatches.Count -ne 1 -or !$taskText.Contains('name: ' + ($pluginPath | ConvertTo-Json -Compress))) {
        throw 'The toboo bridge ID is occupied or has an unexpected path; no configuration changed.'
    }
    Write-Host 'toboo Harness bridge already installed.'
} else {
    $taskBackupPath = Join-Path $repositoryPath ('artifacts/harness-install/' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff'))
    New-Item -ItemType Directory -Path $taskBackupPath -Force | Out-Null
    Copy-Item -LiteralPath $overlayPath -Destination (Join-Path $taskBackupPath 'cordis.patch.yml')
    $taskOriginalHash = (Get-FileHash -LiteralPath $overlayPath).Hash
    $taskNewText = $taskText + "`n- insert:`n    - id: $taskId`n      name: " + ($pluginPath | ConvertTo-Json -Compress) + "`n"
    if ((Get-FileHash -LiteralPath $overlayPath).Hash -ne $taskOriginalHash) { throw 'Harness configuration changed during installation; retry.' }
    [IO.File]::WriteAllText($overlayPath + '.toboo-tmp', $taskNewText, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath ($overlayPath + '.toboo-tmp') -Destination $overlayPath -Force
    Write-Host "Installed independent toboo plugin. Backup: $taskBackupPath"
}
$taskPrivatePath = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.toboo-harness'
New-Item -ItemType Directory -Path $taskPrivatePath -Force | Out-Null
$taskIdentity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
& icacls $taskPrivatePath '/inheritance:r' '/grant:r' ($taskIdentity + ':(OI)(CI)F') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not restrict the toboo credential/history directory.' }
Write-Host 'Restart Harness to load the plugin. Trilium entries and global model selections were not changed.'
