<#
.SYNOPSIS
    AI Usage Dock — 데이터 소스 프로브 (M0 스파이크용)

.DESCRIPTION
    설계도 3장의 두 데이터 경로를 실제로 호출해 검증한다.
      1) 딥시크 잔액  GET https://api.deepseek.com/user/balance
      2) 안티그래비티  POST https://127.0.0.1:<동적포트>/.../GetUserStatus

    비밀값(API 키, CSRF 토큰)은 화면에 출력하지 않는다.

.EXAMPLE
    $env:DEEPSEEK_API_KEY = "sk-..."
    pwsh -File tools\probe.ps1

.EXAMPLE
    pwsh -File tools\probe.ps1 -SkipDeepSeek      # 안티그래비티만
    pwsh -File tools\probe.ps1 -SkipAntigravity   # 딥시크만
#>
[CmdletBinding()]
param(
    [string]$ApiKey = $env:DEEPSEEK_API_KEY,
    [switch]$SkipDeepSeek,
    [switch]$SkipAntigravity
)

$ErrorActionPreference = 'Stop'
$script:fail = 0

function Write-Section([string]$t) {
    Write-Host ""
    Write-Host ("=" * 68) -ForegroundColor DarkGray
    Write-Host " $t" -ForegroundColor Cyan
    Write-Host ("=" * 68) -ForegroundColor DarkGray
}

function Mask([string]$s) {
    if (-not $s) { return "<none>" }
    if ($s.Length -le 10) { return "$($s.Substring(0,2))***" }
    return "$($s.Substring(0,6))...$($s.Substring($s.Length-4))"
}

# Invoke-RestMethod 는 ISO8601 'Z' 문자열을 DateTime(Kind=Utc) 로 바꾼다.
# [datetimeoffset]::Parse(DateTime) 은 Kind 를 무시하고 로컬 오프셋을 붙이므로
# 9시간이 어긋난다. 반드시 Kind 를 보고 변환한다. (설계 5.3.3)
function ConvertTo-LocalStamp([object]$v) {
    if ($null -eq $v) { return '-' }
    if ($v -is [datetime])       { return $v.ToLocalTime().ToString('MM/dd HH:mm') }
    if ($v -is [datetimeoffset]) { return $v.ToLocalTime().ToString('MM/dd HH:mm') }
    $dto = [datetimeoffset]::Parse([string]$v,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind)
    return $dto.ToLocalTime().ToString('MM/dd HH:mm')
}

# ---------------------------------------------------------------- DeepSeek
function Test-DeepSeek {
    Write-Section "1) DeepSeek / user/balance"

    if (-not $ApiKey) {
        Write-Host "  SKIP: DEEPSEEK_API_KEY 가 없습니다. -ApiKey 로 전달하세요." -ForegroundColor Yellow
        return
    }
    Write-Host "  key: $(Mask $ApiKey)"

    try {
        $r = Invoke-RestMethod -Uri "https://api.deepseek.com/user/balance" `
            -Headers @{ Authorization = "Bearer $ApiKey"; Accept = "application/json" } `
            -TimeoutSec 15
    }
    catch {
        $code = $_.Exception.Response.StatusCode.value__
        Write-Host "  FAIL: HTTP $code — $($_.Exception.Message)" -ForegroundColor Red
        $script:fail++
        return
    }

    Write-Host "  is_available : $($r.is_available)"
    if (-not $r.balance_infos) {
        Write-Host "  FAIL: balance_infos 가 비어 있음" -ForegroundColor Red
        $script:fail++
        return
    }
    foreach ($b in $r.balance_infos) {
        # 금액은 문자열 → decimal 로 파싱해야 한다 (설계 5.2)
        $total = [decimal]::Parse($b.total_balance, [Globalization.CultureInfo]::InvariantCulture)
        Write-Host ("  {0}  total={1:N2}  granted={2}  topped_up={3}" -f `
            $b.currency, $total, $b.granted_balance, $b.topped_up_balance)
    }
    Write-Host "  OK" -ForegroundColor Green
}

# ------------------------------------------------------------ Antigravity
function Invoke-AntigravityStatus {
    param(
        [int]$Port,
        [string]$Csrf,
        [int]$TimeoutSec = 3
    )

    $uri = "https://127.0.0.1:$Port/exa.language_server_pb.LanguageServerService/GetUserStatus"
    $body = '{"metadata":{"ideName":"antigravity","extensionName":"antigravity","ideVersion":"unknown","locale":"en"}}'

    try {
        $r = Invoke-RestMethod -Uri $uri -Method Post -SkipCertificateCheck -TimeoutSec $TimeoutSec `
            -ContentType 'application/json' `
            -Headers @{ 'x-codeium-csrf-token' = $Csrf; 'Connect-Protocol-Version' = '1' } `
            -Body $body
        if ($r -and $null -ne $r.userStatus) { return $r }
        return $null
    }
    catch {
        return $null
    }
}

function Get-AntigravityEndpoint {
    $procs = Get-CimInstance Win32_Process -Filter "Name='language_server.exe'" -ErrorAction SilentlyContinue
    $proc = $procs | Where-Object { $_.CommandLine -match '--app_data_dir\s+antigravity' } | Select-Object -First 1
    if (-not $proc) { throw "language_server.exe (antigravity) 프로세스가 없습니다. 앱을 실행하세요." }

    $csrf = [regex]::Match($proc.CommandLine, '--csrf_token\s+(\S+)').Groups[1].Value
    if (-not $csrf) { throw "커맨드라인에서 --csrf_token 을 찾지 못했습니다." }

    $candidatePorts = @()

    # a) main.log 에 기록된 로컬 UI 서버 포트
    $log = Join-Path $env:APPDATA 'Antigravity\logs\main.log'
    if (Test-Path $log) {
        $m = Select-String -Path $log -Pattern 'Local:\s+https://127\.0\.0\.1:(\d+)/' -ErrorAction SilentlyContinue |
             Select-Object -Last 1
        if ($m) { $candidatePorts += [int]$m.Matches[0].Groups[1].Value }
    }

    # b) language_server.exe 가 실제로 소유한 127.0.0.1 포트만 수집한다.
    #    Antigravity.exe 의 CDP(1906)/HTTP bridge(1907) 를 잘못 집지 않게 하기 위함.
    try {
        $tcpPorts = Get-NetTCPConnection -OwningProcess $proc.ProcessId -ErrorAction SilentlyContinue |
            Where-Object { $_.LocalAddress -eq '127.0.0.1' } |
            Select-Object -ExpandProperty LocalPort -Unique |
            Sort-Object
        if ($tcpPorts) { $candidatePorts += $tcpPorts }
    }
    catch { }

    # c) 위에서 아무 후보도 못 찾았을 때만 예전 범위를 사용한다.
    if (-not $candidatePorts) { $candidatePorts += 1908..1916 }

    $uniquePorts = New-Object System.Collections.Generic.List[int]
    $seen = New-Object System.Collections.Generic.HashSet[int]
    foreach ($p in $candidatePorts) {
        $pi = [int]$p
        if ($seen.Add($pi)) { $uniquePorts.Add($pi) }
    }

    $port = $null
    $response = $null
    foreach ($p in $uniquePorts) {
        $r = Invoke-AntigravityStatus -Port $p -Csrf $csrf -TimeoutSec 3
        if ($r -and $null -ne $r.userStatus) {
            $port = $p
            $response = $r
            break
        }
    }

    if (-not $port) {
        throw "안티그래비티 로컬 포트를 찾지 못했습니다. 후보: $($uniquePorts -join ', ')"
    }

    [pscustomobject]@{ Port = $port; Csrf = $csrf; Pid = $proc.ProcessId; Response = $response }
}

function Test-Antigravity {
    Write-Section "2) Antigravity / GetUserStatus"

    try { $ep = Get-AntigravityEndpoint }
    catch { Write-Host "  FAIL: $($_.Exception.Message)" -ForegroundColor Red; $script:fail++; return }

    Write-Host "  pid=$($ep.Pid)  port=$($ep.Port)  csrf=$(Mask $ep.Csrf)"

    $r = $ep.Response
    if (-not $r -or $null -eq $r.userStatus) {
        Write-Host "  FAIL: GetUserStatus 응답이 비어 있음" -ForegroundColor Red
        $script:fail++
        return
    }

    $us = $r.userStatus
    $plan = $us.planStatus.planInfo.planName
    Write-Host "  plan : $plan"
    Write-Host "  tier : $($us.userTier.id)"

    $models = $us.cascadeModelConfigData.clientModelConfigs
    if (-not $models) { Write-Host "  FAIL: clientModelConfigs 없음" -ForegroundColor Red; $script:fail++; return }

    Write-Host ""
    Write-Host ("  {0,-30} {1,8}  {2}" -f 'label', 'remain', 'reset (local)')
    Write-Host ("  {0,-30} {1,8}  {2}" -f ('-'*30), ('-'*8), ('-'*22))

    $groups = @{}
    foreach ($m in $models) {
        $q = $m.quotaInfo
        $frac = $q.remainingFraction
        $reset = ConvertTo-LocalStamp $q.resetTime
        $pct = if ($null -ne $frac) { "{0:N1}%" -f ($frac * 100) } else { '-' }
        Write-Host ("  {0,-30} {1,8}  {2}" -f $m.label, $pct, $reset)

        $key = "$frac|$($q.resetTime)"
        if (-not $groups.ContainsKey($key)) { $groups[$key] = @() }
        $groups[$key] += $m.label
    }

    Write-Host ""
    Write-Host "  models=$($models.Count)  quota-groups=$($groups.Count)" -ForegroundColor Green
    foreach ($k in $groups.Keys) {
        Write-Host ("    group [{0}] -> {1}" -f ($k.Split('|')[0]), ($groups[$k] -join ', ')) -ForegroundColor DarkGray
    }
    Write-Host "  OK" -ForegroundColor Green
}

# -------------------------------------------------------------------- main
Write-Host "AI Usage Dock — probe ($(Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))" -ForegroundColor White

if (-not $SkipDeepSeek)    { Test-DeepSeek }
if (-not $SkipAntigravity) { Test-Antigravity }

Write-Host ""
if ($script:fail -gt 0) {
    Write-Host "결과: $($script:fail)개 실패" -ForegroundColor Red
    exit 1
}
Write-Host "결과: 모두 성공" -ForegroundColor Green