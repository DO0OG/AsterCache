<#
.SYNOPSIS
  E4 follow-up: compare the relocated HDD game against an SSD baseline and against a RAM prewarm (administrator).

.DESCRIPTION
  1. cs_e4_game.ps1 -TraceJson <trace> -MaxZoneMiB <ZoneMiB>: re-plan so the whole hot set fits the zone, apply, measure.
  2. bench.shadow_copy: sparse copy of the traced ranges to an SSD folder (same sizes, same offsets), trace rewritten.
  3. SSD baseline: standby purge, then bench.replay of the shadow trace, x Runs.
  4. RAM prewarm: standby purge, aster prefetch (zone order, pvm), wait PrewarmGapSeconds, then bench.replay, x Runs.
  Results: <repo>\results\csharp_e4cmp_<stamp>\summary.json. Nothing is deleted; the shadow folder stays on the SSD.
#>
[CmdletBinding()]
param(
    [string]$TraceJson = '',
    [string]$Root = 'D:\Frostpunk',
    [string]$GameExe = 'D:\Frostpunk\Frostpunk.exe',
    [string]$ShadowRoot = 'C:\AsterShadow\Frostpunk',
    [int]$ZoneMiB = 2048,
    [int]$Runs = 3,
    [int]$PrewarmGapSeconds = 60,
    [switch]$SkipE4,
    [string]$Python = 'python',
    [string]$AsterExe = '',
    [string]$Repo = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'not_admin: run from an elevated PowerShell' }
if (-not $Repo) { $Repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) }
if (-not $AsterExe) { $AsterExe = Join-Path $Repo 'csharp\publish\cli\aster.exe' }
if (-not (Test-Path -LiteralPath $AsterExe)) { throw "C# CLI not found: $AsterExe" }
function Get-LastE4Folder {
    $f = Get-ChildItem -LiteralPath (Join-Path $Repo 'results') -Directory -Filter 'csharp_e4_*' | Sort-Object Name | Select-Object -Last 1
    if (-not $f) { throw 'no results\csharp_e4_* folder: run cs_e4_game.ps1 first' }
    return $f.FullName
}
if (-not $TraceJson) { $TraceJson = Join-Path (Get-LastE4Folder) 'trace.json' }
if (-not (Test-Path -LiteralPath $TraceJson)) { throw "trace not found: $TraceJson" }
$gameName = [IO.Path]::GetFileNameWithoutExtension($GameExe)

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$out = Join-Path $Repo "results\csharp_e4cmp_$stamp"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$summary = [ordered]@{ trace = $TraceJson; root = $Root; shadow_root = $ShadowRoot; zone_mib = $ZoneMiB; runs = $Runs; prewarm_gap_s = $PrewarmGapSeconds }

function Invoke-Tool([string]$Tag, [string]$Exe, [string[]]$ToolArgs) {
    $outFile = Join-Path $out "$Tag.txt"; $errFile = Join-Path $out "$Tag.err.txt"
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $text = (& $Exe @ToolArgs 2>$errFile | Out-String); $code = $LASTEXITCODE } finally { $ErrorActionPreference = $old }
    Set-Content -LiteralPath $outFile -Value $text -Encoding UTF8
    if ($code -ne 0) { throw "step '$Tag' failed (exit $code): see $errFile" }
    return $text
}
function Assert-GameClosed([string]$Why) {
    if (@(Get-Process -Name $gameName -ErrorAction SilentlyContinue).Count -gt 0) { throw "$gameName is running: $Why must not start while the game runs" }
}
function Invoke-Purge([string]$Tag) {
    $cold = Invoke-Tool "$Tag-purge" $Python @('-m', 'bench.cache_control')
    if ($cold -notmatch 'standby_purge') { throw "standby purge did not confirm ($Tag)" }
}
function Get-Median([double[]]$v) { $s = $v | Sort-Object; return $s[[int][math]::Floor($s.Count / 2)] }

Push-Location $Repo
try {
    Assert-GameClosed 'the comparison'
    if (-not $SkipE4) {
        Write-Host "== 1. cs_e4_game.ps1 with the whole hot set in a $ZoneMiB MiB zone (re-plan, apply, measure)"
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $Repo 'csharp\scripts\cs_e4_game.ps1') -TraceJson $TraceJson -MaxZoneMiB $ZoneMiB -Runs $Runs -Root $Root -GameExe $GameExe -Repo $Repo
        if ($LASTEXITCODE -ne 0) { throw "cs_e4_game.ps1 failed (exit $LASTEXITCODE)" }
    }
    $e4Folder = Get-LastE4Folder
    $e4Plan = Join-Path $e4Folder 'plan.json'
    $summary.e4_folder = $e4Folder
    $summary.e4 = Get-Content -LiteralPath (Join-Path $e4Folder 'summary.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not (Test-Path -LiteralPath "$e4Plan.prefetch.json")) { throw "prefetch json not found: $e4Plan.prefetch.json" }

    Write-Host '== 2. sparse shadow copy of the traced ranges to the SSD'
    $shadowTrace = Join-Path $out 'trace_shadow.json'
    $summary.shadow = Invoke-Tool 'shadow_copy' $Python @('-X', 'utf8', '-m', 'bench.shadow_copy', '--trace', $TraceJson, '--src-root', $Root, '--dst-root', $ShadowRoot, '--out', $shadowTrace) | ConvertFrom-Json

    Write-Host "== 3. SSD baseline: cold replay of the shadow trace x $Runs"
    $ssd = @()
    for ($i = 1; $i -le $Runs; $i++) {
        Invoke-Purge "ssd-cold$i"
        $r = Invoke-Tool "ssd-replay$i" $Python @('-X', 'utf8', '-m', 'bench.replay', '--trace', $shadowTrace) | ConvertFrom-Json
        $ssd += [double]$r.total_s
    }
    $summary.ssd_cold_replay_s = $ssd; $summary.ssd_cold_median_s = Get-Median $ssd

    Write-Host "== 4. RAM prewarm: purge, zone prefetch, wait $PrewarmGapSeconds s, replay x $Runs"
    $pre = @(); $warm = @()
    for ($i = 1; $i -le $Runs; $i++) {
        Assert-GameClosed 'prewarm'
        Invoke-Purge "prewarm-cold$i"
        $pf = Invoke-Tool "prewarm-prefetch$i" $AsterExe @('prefetch', '--plan', "$e4Plan.prefetch.json", '--origin-root', $Root, '--budget-mib', "$ZoneMiB", '--method', 'pvm', '--execute') | ConvertFrom-Json
        if ($pf.errors -ne 0) { throw "prefetch errors=$($pf.errors)" }
        $pre += [double]$pf.seconds
        Start-Sleep -Seconds $PrewarmGapSeconds
        $r = Invoke-Tool "prewarm-replay$i" $Python @('-X', 'utf8', '-m', 'bench.replay', '--trace', $TraceJson) | ConvertFrom-Json
        $warm += [double]$r.total_s
    }
    $summary.prewarm_prefetch_s = $pre; $summary.prewarm_prefetch_median_s = Get-Median $pre
    $summary.prewarmed_replay_s = $warm; $summary.prewarmed_replay_median_s = Get-Median $warm
    $summary.prewarmed_over_ssd = [math]::Round((Get-Median $warm) / (Get-Median $ssd), 3)
    $summary.ok = $true
} catch {
    $summary.error = "$_"
    throw
} finally {
    Pop-Location
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding UTF8
    Write-Host "Results: $out"
}
