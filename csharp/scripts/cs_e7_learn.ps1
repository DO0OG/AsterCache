<#
.SYNOPSIS
  E7 (C#): effect of the first-run read-ahead (aster learn) on a cold replay: A = no learn, B = aster learn running during the replay.

.DESCRIPTION
  Run ONCE from an elevated PowerShell (the caller starts it with -Verb RunAs):
    powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\csharp\scripts\cs_e7_learn.ps1 -Mode frostpunk
  Results: <repo>\results\csharp_e7_<stamp>\ (raw output of every step, progress.log, summary.json rewritten after every run).

  Per run: standby purge -> (A) bench.replay cold, or (B) aster learn --exe python.exe --root <Root> --json started first, then the same bench.replay;
  the learn JSON (read_ahead_bytes, hit_ratio) and the replay time are kept. Order alternates A,B,B,A,A,B,... for -Runs runs of each.
  python.exe is the stand-in "game", so learn follows EVERY python process: close all other python programs during the measurement.

  NOTE - Frostpunk is currently relaid out (the 42% zone), so it is NOT a "scattered first run". The script never undoes that. To measure a scattered
  first run, undo first (elevated, only while Frostpunk is closed; preview without --execute); the exact command is in
  results\csharp_e4_20261102_111921\README.txt:
    & "<repo>\csharp\publish\cli\aster.exe" revert --journal "<repo>\results\csharp_e4_20261102_111921\journal.jsonl" --allowed-root "D:\Frostpunk" --require-volume-label "<label>" [--execute]
  After the measurement the script prints the command that restores the relaid-out layout.
  -Mode synthetic uses a scattered synthetic dataset on D: (default D:\AsterBench; make it with: python -m bench.dataset --root D:\AsterBench)
  and needs -Trace pointing to a trace of that dataset.
#>
[CmdletBinding()]
param(
    [string]$Trace = '',
    [string]$Root = '',
    [int]$Runs = 3,
    [string]$Aster = '',
    [string]$Python = 'python',
    [ValidateSet('synthetic', 'frostpunk')][string]$Mode = 'frostpunk',
    [int]$Seconds = 120,
    [string]$OutPrefix = 'csharp_e7',
    [ValidateSet('saturated', 'paced')][string]$Replay = 'saturated',
    [double]$ThinkMs = 20,
    [int]$ThinkEvery = 1,
    [string]$LearnMode = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'not_admin: run from an elevated PowerShell. Nothing was changed.' }
$Repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
if (-not $Aster) { $Aster = Join-Path $Repo 'csharp\publish\gui\aster.exe' }
if (-not $Root) { $Root = if ($Mode -eq 'synthetic') { 'D:\AsterBench' } else { 'D:\Frostpunk' } }
if (-not $Trace -and $Mode -eq 'frostpunk') { $Trace = Join-Path $Repo 'results\csharp_e4_20261102_111921\trace.json' }
if (-not (Test-Path -LiteralPath $Aster)) { throw "aster.exe not found: $Aster" }
if (-not $Trace -or -not (Test-Path -LiteralPath $Trace)) { throw "trace not found: '$Trace' (-Trace is required for -Mode synthetic)" }
if (-not (Test-Path -LiteralPath $Root)) { throw "root not found: $Root (synthetic: python -m bench.dataset --root $Root)" }
$Root = $Root.TrimEnd('\')
$learnModeArgs = @(); if ($LearnMode) { $learnModeArgs = @('--mode', $LearnMode) }
$otherPython = @(Get-Process -Name 'python*' -ErrorAction SilentlyContinue).Count  # other python programs may run: learn only reads files under -Root, but then it ends at the -Seconds cap instead of when the replay ends
if (@(Get-Process -Name 'Frostpunk' -ErrorAction SilentlyContinue).Count -gt 0) { throw 'Frostpunk is running' }

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$out = Join-Path $Repo "results\$($OutPrefix)_$stamp"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$log = Join-Path $out 'progress.log'
$summary = [ordered]@{ learn_mode = $LearnMode; replay = $Replay; think_ms = $ThinkMs; think_every = $ThinkEvery; other_python_processes = $otherPython; mode = $Mode; root = $Root; trace = $Trace; aster = (Split-Path -Leaf $Aster); runs = $Runs; learn_seconds_cap = $Seconds; rows = @() }
function Log([string]$m) { $l = "{0} {1}" -f (Get-Date -Format 'HH:mm:ss'), $m; Add-Content -LiteralPath $log -Value $l -Encoding UTF8; Write-Host $l }
function Save-Summary { $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding UTF8 }
function Get-Median($v) { return @($v | Sort-Object)[[int][math]::Floor(@($v).Count / 2)] }

function Invoke-Tool([string]$Name, [string]$Exe, [string[]]$ToolArgs) {
    $ErrorActionPreference = 'Continue'
    $errFile = Join-Path $out "$Name.err.txt"
    $text = (& $Exe @ToolArgs 2>$errFile | Out-String)
    $code = $LASTEXITCODE
    Set-Content -LiteralPath (Join-Path $out "$Name.txt") -Value $text -Encoding UTF8
    if ($code -ne 0) { throw "step '$Name' failed (exit $code):`n$text`n$(Get-Content -LiteralPath $errFile -Raw -ErrorAction SilentlyContinue)" }
    return $text
}
function Invoke-Purge([string]$Tag) {
    $t = Invoke-Tool "$Tag-purge" $Python @('-m', 'bench.cache_control')
    if ($t -notmatch 'standby_purge') { throw "standby purge not confirmed ($Tag): $t" }
}
function Invoke-Replay([string]$Tag) {
    # paced: bench.replay_paced busy-waits $ThinkMs before every $ThinkEvery-th request; total_s stays the sum of the read latencies
    $cmd = if ($Replay -eq 'paced') { @('-X', 'utf8', '-m', 'bench.replay_paced', '--trace', $Trace, '--think-ms', "$ThinkMs", '--every', "$ThinkEvery") } else { @('-X', 'utf8', '-m', 'bench.replay', '--trace', $Trace) }
    return (Invoke-Tool "$Tag-replay" $Python $cmd) | ConvertFrom-Json
}

# A,B,B,A,A,B,... : alternate the order so that drift hits both arms equally.
$order = @(); for ($i = 0; $i -lt $Runs; $i++) { if ($i % 2 -eq 0) { $order += 'A', 'B' } else { $order += 'B', 'A' } }

Push-Location $Repo
try {
    Log "mode=$Mode root=$Root trace=$Trace runs=$Runs order=$($order -join ',')"
    $n = @{ A = 0; B = 0 }
    foreach ($arm in $order) {
        $n[$arm]++
        $tag = "$arm$($n[$arm])"
        Invoke-Purge $tag
        $row = [ordered]@{ arm = $arm; run = $n[$arm] }
        if ($arm -eq 'A') {
            $row.replay_s = [double](Invoke-Replay $tag).total_s
        } else {
            $learnOut = Join-Path $out "$tag-learn.txt"; $learnErr = Join-Path $out "$tag-learn.err.txt"
            $lp = Start-Process -FilePath $Aster -ArgumentList (@('learn', '--exe', 'python.exe', '--root', "`"$Root`"", '--seconds', "$Seconds", '--json') + $learnModeArgs) `
                -RedirectStandardOutput $learnOut -RedirectStandardError $learnErr -PassThru -WindowStyle Hidden
            Start-Sleep -Seconds 3   # let the trace session start before the "game" begins
            try { $row.replay_s = [double](Invoke-Replay $tag).total_s }
            finally {
                # learn stops when every python process is gone (the replay just ended) or at the seconds cap.
                if (-not $lp.WaitForExit(($Seconds + 30) * 1000)) { Stop-Process -Id $lp.Id -Force; Log "learn $tag did not exit in time, killed" }
            }
            $j = Get-Content -LiteralPath $learnOut -Raw -Encoding UTF8 | ConvertFrom-Json
            foreach ($pr in $j.PSObject.Properties) { $row[$pr.Name] = $pr.Value }  # every field of the learn JSON (read_ahead_bytes, hit_ratio, resolved_reads, sequential_files, ...)
        }
        $extra = ''; if ($arm -eq 'B') { $extra = "read_ahead=$($row.read_ahead_bytes) hit=$($row.hit_ratio)" }
        Log ("{0}: replay {1:N2} s {2}" -f $tag, $row.replay_s, $extra)
        $summary.rows += [pscustomobject]$row
        Save-Summary
    }
    # one extra B run with a DiskIo capture (game reads vs learn reads)
    $wprFile = Join-Path $Repo ('csharp' + [char]92 + 'scripts' + [char]92 + 'aster_diskio.wprp')
    $etl = Join-Path $out 'diskio_B.etl'
    $wprOk = $false
    try {
        $ErrorActionPreference = 'Continue'
        & wpr.exe -cancel 2>&1 | Out-Null
        & wpr.exe -start "$wprFile!AsterDiskIO" -filemode 2>&1 | Out-String | Set-Content -LiteralPath (Join-Path $out 'wpr_start.txt')
        if ($LASTEXITCODE -ne 0) { throw "wpr -start failed (exit $LASTEXITCODE)" }
        $wprOk = $true
        Invoke-Purge 'W'
        $learnOut = Join-Path $out 'W-learn.txt'; $learnErr = Join-Path $out 'W-learn.err.txt'
        $lp = Start-Process -FilePath $Aster -ArgumentList (@('learn', '--exe', 'python.exe', '--root', "`"$Root`"", '--seconds', "$Seconds", '--json') + $learnModeArgs) -RedirectStandardOutput $learnOut -RedirectStandardError $learnErr -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 3
        try { $summary.wpr_replay_s = [double](Invoke-Replay 'W').total_s } finally { if (-not $lp.WaitForExit(($Seconds + 30) * 1000)) { Stop-Process -Id $lp.Id -Force } }
        Log ("W (DiskIo capture): replay {0:N2} s" -f $summary.wpr_replay_s)
    } catch { $summary.wpr_error = "$_"; Log "DiskIo capture failed: $_" }
    finally { $ErrorActionPreference = 'Continue'; if ($wprOk) { & wpr.exe -stop $etl 2>&1 | Out-String | Set-Content -LiteralPath (Join-Path $out 'wpr_stop.txt') } }
    $a = @($summary.rows | Where-Object arm -eq 'A' | ForEach-Object replay_s); $b = @($summary.rows | Where-Object arm -eq 'B' | ForEach-Object replay_s)
    $summary.median_a_s = Get-Median $a; $summary.median_b_s = Get-Median $b
    $summary.speedup = [math]::Round($summary.median_a_s / $summary.median_b_s, 3)
    Save-Summary
    Log ("median A (no learn) {0:N2} s, B (learn) {1:N2} s, A/B = {2}" -f $summary.median_a_s, $summary.median_b_s, $summary.speedup)
    Write-Host "Done: $out\summary.json"
    Write-Host 'This script changed nothing on disk. If you undid the relayout before this run, restore it with (elevated, Frostpunk closed):'
    Write-Host "  & `"$Repo\csharp\publish\cli\aster.exe`" apply --plan `"$Repo\results\csharp_e4_20261102_111921\plan.json`" --journal <new journal.jsonl> --allowed-root `"$Root`" --execute"
} finally { Pop-Location }
