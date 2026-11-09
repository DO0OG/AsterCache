<#
.SYNOPSIS
  ORBIT-3 offline benchmark (design 4.1): ABBA runs of "aster orbit-bench run", then "aster orbit-bench summarize" (administrator).

.DESCRIPTION
  Runs Pairs*2 times in the order A,B,B,A (A = incumbent prewarm order, B = candidate order).
  Before every run: standby purge (python -m bench.cache_control), which must confirm standby_purge.
  Refuses to start when sc query does not report the AsterCache service as STOPPED or when -GameExe is running.
  This script never stops the service. The CLI refuses again when the game or the service runs.
  Python runs with -B and PYTHONDONTWRITEBYTECODE=1, so no .pyc is written to the measured disk.
  aster.exe is copied once to $env:TEMP and the copy runs, so the binary is not read from the measured drive during runs.
  -LambdaPerMin (optional) is passed to the B arm runs only: the fixed interruption rate per minute used for the candidate when the model has no hazard yet (CLI default 1.0).
  -Out must not be on the drive of -GameRoot (refused); without -GameRoot only a warning is printed.
  Results: <Out>\run_NN_A.json, <Out>\run_NN_B.json (per run), <Out>\summary.json.
#>
param(
    [Parameter(Mandatory = $true)][string]$Game,
    [int]$Pairs = 10,
    [string]$Repo = '',
    [string]$Python = 'python',
    [string]$Out = '',
    [string]$GameExe = '',
    [string]$GameRoot = '',
    [double]$LambdaPerMin = 0
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$env:PYTHONDONTWRITEBYTECODE = '1'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'not_admin: run from an elevated PowerShell' }
# ABBA 묶음은 run 4회 = 쌍 2개이므로 Pairs 는 짝수여야 함
if ($Pairs -lt 2 -or $Pairs % 2 -ne 0) { throw "Pairs must be an even number >= 2, got $Pairs" }
if (-not $Repo) { $Repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) }
$AsterExe = Join-Path $Repo 'csharp\publish\cli\aster.exe'
if (-not (Test-Path -LiteralPath $AsterExe)) { throw "C# CLI not found: $AsterExe" }

# sc query 의 상태 값이 1 STOPPED 인지 확인 (필드 이름은 로캘마다 달라 값 부분으로 찾음, 서비스가 설치되지 않은 오류 1060 은 정지로 봄, 그 밖의 출력은 중단)
$scText = (& sc.exe query 'AsterCache' 2>&1 | Out-String)
if (-not (($scText -match ':\s*1\s+STOPPED') -or ($scText -match '1060'))) { throw "AsterCache service is not stopped (stop it first; this script does not stop it): $($scText.Trim())" }

function Assert-GameClosed([string]$Why) {
    if ($GameExe -and @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($GameExe)) -ErrorAction SilentlyContinue).Count -gt 0) {
        throw "$GameExe is running: $Why must not start while the game runs"
    }
}

function Invoke-Tool([string]$Tag, [string]$Exe, [string[]]$ToolArgs) {
    $outFile = Join-Path $Out "$Tag.txt"; $errFile = Join-Path $Out "$Tag.err.txt"
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $text = (& $Exe @ToolArgs 2>$errFile | Out-String); $code = $LASTEXITCODE } finally { $ErrorActionPreference = $old }
    Set-Content -LiteralPath $outFile -Value $text -Encoding UTF8
    if ($code -ne 0) { throw "step '$Tag' failed (exit $code): see $errFile" }
    return $text
}

function Invoke-Purge([string]$Tag) {
    $cold = Invoke-Tool "$Tag-purge" $Python @('-B', '-m', 'bench.cache_control')
    if ($cold -notmatch 'standby_purge') { throw "standby purge did not confirm ($Tag)" }
}

$order = @('A', 'B', 'B', 'A')
$runCount = $Pairs * 2
$files = @()

Push-Location $Repo
try {
    Assert-GameClosed 'the benchmark'
    # 출력 폴더는 관리자·서비스·게임 확인을 모두 통과한 뒤에 만듭니다 (기본은 C: 쪽 임시 폴더라 측정 디스크에 쓰지 않음)
    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    if (-not $Out) { $Out = Join-Path $env:TEMP "aster_orbit_bench_$stamp" }
    $outRoot = [IO.Path]::GetPathRoot($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out))
    if ($GameRoot) {
        $gameDrive = [IO.Path]::GetPathRoot($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($GameRoot))
        if ($outRoot -eq $gameDrive) { throw "-Out is on the game drive ($gameDrive): choose an -Out on another drive" }
    } else {
        Write-Warning 'GameRoot not given: the -Out drive is not checked against the game drive'
    }
    New-Item -ItemType Directory -Force -Path $Out | Out-Null
    # 측정 전에 CLI 를 한 번 TEMP 복사본으로 옮겨 그 복사본으로 실행합니다
    $BinDir = Join-Path $env:TEMP "aster_orbit_bin_$stamp"
    Copy-Item -LiteralPath (Split-Path -Parent $AsterExe) -Destination $BinDir -Recurse -Force
    $AsterExe = Join-Path $BinDir 'aster.exe'
    for ($r = 1; $r -le $runCount; $r++) {
        $arm = $order[($r - 1) % 4]
        $tag = 'run{0:D2}{1}' -f $r, $arm
        Assert-GameClosed $tag
        Invoke-Purge $tag
        $json = Join-Path $Out ('run_{0:D2}_{1}.json' -f $r, $arm)
        $runArgs = @('orbit-bench', 'run', '--game', $Game, '--arm', $arm, '--out', $json)
        if ($arm -eq 'B' -and $LambdaPerMin -gt 0) { $runArgs += @('--lambda-per-min', $LambdaPerMin.ToString([Globalization.CultureInfo]::InvariantCulture)) }
        Invoke-Tool $tag $AsterExe $runArgs | Out-Null
        $files += $json
    }

    $summary = Join-Path $Out 'summary.json'
    Invoke-Tool 'summarize' $AsterExe (@('orbit-bench', 'summarize', '--in') + $files + @('--out', $summary)) | Out-Null
    Write-Host "Results: $Out"
} finally {
    Pop-Location
}
