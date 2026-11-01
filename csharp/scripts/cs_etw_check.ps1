<#
.SYNOPSIS
  S3 (C#): collect a live ETW read trace (C# trace --live and a WPR .etl) while the reference replay runs.

.DESCRIPTION
  Run ONCE from an elevated (Administrator) PowerShell, by the user:
    powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\csharp\scripts\cs_etw_check.ps1

  Steps: start WPR (FileIO profile, file mode) -> start the C# live trace in the background ->
  replay trace.json with the Python reference (bench.replay) -> stop the live trace -> stop WPR -> keep both outputs.
  Results: <repo>\results\csharp_<yyyyMMdd_HHmmss>\ (csharp_live.json, wpr.etl, replay.txt, summary.json).

  NOT YET DONE: the comparison (block coverage >= 99 %, Kendall tau >= 0.99) against the original trace.
  It needs the final C# trace output format. Until then the verdict is written as not_evaluated and the script warns.
  Assumed CLI and WPR names: `aster trace --live --exe <python>.exe --origin-root <Root> --out`, WPR profile FileIO. WPR 프로필 [확인 필요].
#>
[CmdletBinding()]
param(
    [string]$Root = 'D:\AsterBench',
    [string]$Python = 'python',
    [string]$AsterExe = '',
    [string]$Repo = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 1. Elevation first. Nothing below runs, and nothing is changed, without it.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'not_admin: 관리자 필요. 관리자 PowerShell 에서 다시 실행하세요. 아무것도 바꾸지 않았습니다.'
}

if (-not $Repo) { $Repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) }
if (-not $AsterExe) { $AsterExe = Join-Path $Repo 'csharp\publish\cli\aster.exe' }
if (-not (Test-Path -LiteralPath $AsterExe)) { throw "C# CLI not found: $AsterExe (publish the CLI first, see csharp\README.md)" }
foreach ($n in 'dataset_meta.json', 'trace.json') {
    if (-not (Test-Path -LiteralPath (Join-Path $Root $n))) { throw "missing $Root\$n" }
}

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$out = Join-Path $Repo "results\csharp_$stamp"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$etl = Join-Path $out 'wpr.etl'
$liveOut = Join-Path $out 'csharp_live.json'
$summary = [ordered]@{ root = $Root; repo = $Repo; verdict = 'not_evaluated'; reason = 'comparison not implemented yet (needs the final C# trace output format)' }

$wprStarted = $false
$live = $null
try {
    Write-Host '== 1. WPR start (FileIO, file mode)'
    & wpr.exe -start FileIO -filemode | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "wpr -start failed (exit $LASTEXITCODE)" }
    $wprStarted = $true

    Write-Host '== 2. C# live trace in the background'
    $live = Start-Process -FilePath $AsterExe -ArgumentList "trace --live --exe $([IO.Path]::GetFileNameWithoutExtension($Python)).exe --origin-root `"$Root`" --out `"$liveOut`"" -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 3
    if ($live.HasExited) { throw "trace --live exited early (exit $($live.ExitCode)); the ETW session did not start" }

    Write-Host '== 3. replay trace.json (Python reference)'
    Push-Location $Repo
    try {
        $replay = (& $Python -m bench.replay --trace (Join-Path $Root 'trace.json') 2>&1 | Out-String)
        $code = $LASTEXITCODE
    } finally { Pop-Location }
    Set-Content -LiteralPath (Join-Path $out 'replay.txt') -Value $replay -Encoding UTF8
    if ($code -ne 0) { throw "replay failed (exit $code): see replay.txt" }
    Start-Sleep -Seconds 2

    Write-Host '== 4. stop the live trace and WPR'
    if (-not $live.HasExited) { Stop-Process -Id $live.Id -Force }
    $live.WaitForExit()
    & wpr.exe -stop $etl | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "wpr -stop failed (exit $LASTEXITCODE)" }
    $wprStarted = $false
    if (-not (Test-Path -LiteralPath $etl) -or -not (Test-Path -LiteralPath $liveOut)) { throw 'missing wpr.etl or csharp_live.json' }
    $summary.live_bytes = (Get-Item -LiteralPath $liveOut).Length
    $summary.etl_bytes = (Get-Item -LiteralPath $etl).Length

    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding UTF8
    Write-Warning 'verdict not_evaluated: the coverage and Kendall tau comparison is not implemented yet. Raw outputs are kept.'
    Write-Host "Saved: $out"
} finally {
    if ($live -and -not $live.HasExited) { Stop-Process -Id $live.Id -Force -ErrorAction SilentlyContinue }
    if ($wprStarted) { & wpr.exe -cancel | Out-Null }
}
