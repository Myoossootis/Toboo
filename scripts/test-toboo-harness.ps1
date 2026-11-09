param([switch]$Live, [string]$Provider = 'deepseek-official', [string]$Model = 'deepseek-flash')
$ErrorActionPreference = 'Stop'
$repositoryPath = Split-Path -Parent $PSScriptRoot
$taskFile = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.toboo-harness/connection.json'
$taskConnection = [IO.File]::ReadAllText($taskFile) | ConvertFrom-Json
if ($taskConnection.service -ne 'toboo-chat' -or $taskConnection.protocol -ne 1 -or $taskConnection.port -ne 37848 -or $taskConnection.token -notmatch '^[a-f0-9]{64}$') { throw 'Incompatible toboo connection metadata.' }
function Invoke-TobooRpc([object]$Body) {
    $taskResponse = Invoke-RestMethod -Uri 'http://127.0.0.1:37848/rpc' -Method Post -Headers @{Authorization=$taskConnection.token} -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 8 -Compress))) -TimeoutSec 15
    if ($taskResponse.error) { throw ('Bridge rejected request: ' + $taskResponse.error.code) }
    return $taskResponse.result
}
$taskStatus = Invoke-TobooRpc @{action='status'}
if (!$taskStatus.compatible -or $taskStatus.version -ne '1.0.2' -or $taskStatus.tools -or $taskStatus.isolation -ne 'owned-transcripts-direct-llm') { throw 'Bridge isolation/compatibility check failed; fully quit and restart Harness after installing updates.' }
$taskModels = Invoke-TobooRpc @{action='models'}
Write-Host "PASS: independent protocol, tools disabled, $(@($taskModels.groups).Count) provider groups."
if (!$Live) { Write-Host 'Read-only check; no model requests sent.'; return }
$taskRoute = @($taskModels.groups | Where-Object id -eq $Provider | ForEach-Object models | Where-Object id -eq $Model)
if ($taskRoute.Count -ne 1) { throw 'The explicitly selected test route is not available.' }
$taskSession = [Guid]::NewGuid().ToString()
$taskCreated = Invoke-TobooRpc @{action='new';sessionId=$taskSession}
$taskCode = 'TOBOO-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$taskWatch = [Diagnostics.Stopwatch]::StartNew()
function Wait-TobooTurn([string]$RequestId) {
    $taskDeadline = [Diagnostics.Stopwatch]::StartNew()
    while ($taskDeadline.Elapsed.TotalSeconds -lt 100) {
        $taskView = Invoke-TobooRpc @{action='view';sessionId=$taskSession}
        $taskTurn = @($taskView.turns | Where-Object requestId -eq $RequestId)[0]
        if ($taskTurn -and $taskTurn.state -notin @('running','cancelling')) { return $taskTurn }
        Start-Sleep -Milliseconds 400
    }
    throw 'Live test timed out.'
}
$taskOne = @{action='send';sessionId=$taskSession;requestId=[Guid]::NewGuid().ToString();text=('这是 toboo 独立桥接联调。请记住本会话代号 ' + $taskCode + '，仅回复“已记录”。');provider=$Provider;model=$Model;style='concise'}
$taskAck = Invoke-TobooRpc $taskOne
$taskRetry = Invoke-TobooRpc $taskOne
if (!$taskAck.accepted -or !$taskRetry.duplicate) { throw 'Request replay was not deduplicated.' }
$taskFirst = Wait-TobooTurn $taskOne.requestId
if ($taskFirst.state -ne 'completed' -or !$taskFirst.reply.Trim()) { throw ('First live turn failed: ' + $taskFirst.state + '/' + $taskFirst.error.code) }
$taskTwo = @{action='send';sessionId=$taskSession;requestId=[Guid]::NewGuid().ToString();text='刚才要求记住的本会话代号是什么？仅回复代号。';provider=$Provider;model=$Model;style='concise'}
$null = Invoke-TobooRpc $taskTwo
$taskSecond = Wait-TobooTurn $taskTwo.requestId
if ($taskSecond.state -ne 'completed' -or !$taskSecond.reply.Contains($taskCode)) { throw ('Context verification failed: ' + $taskSecond.state + '/' + $taskSecond.error.code) }
$taskThree = @{action='send';sessionId=$taskSession;requestId=[Guid]::NewGuid().ToString();text='这是停止生成联调。请从1开始逐个列出整数，直到1000。';provider=$Provider;model=$Model;style='concise'}
$null = Invoke-TobooRpc $taskThree
$null = Invoke-TobooRpc @{action='stop';sessionId=$taskSession;requestId=$taskThree.requestId}
$taskStopped = Wait-TobooTurn $taskThree.requestId
if ($taskStopped.state -ne 'cancelled') { throw ('Cancellation check failed: ' + $taskStopped.state) }
$taskOutput = Join-Path $repositoryPath 'artifacts/harness-live'
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
@{source='actual Harness/model integration; dedicated generated test conversation';harnessVersion='0.2.1-rc.5';provider=$Provider;model=$Model;sessionId=$taskSession;deduplicated=$taskRetry.duplicate;firstTurn=$taskFirst.state;contextMatched=$true;cancelled=($taskStopped.state -eq 'cancelled');elapsedSeconds=$taskWatch.Elapsed.TotalSeconds;cameraUsed=$false;hardwareOutput='disabled'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskOutput 'validation.json') -Encoding utf8
Write-Host 'PASS: real model reply, same-conversation recall, exact-ID replay and live cancellation. No camera or actuator used.'
