<#
.SYNOPSIS
  E6-5b (C#): after the high-band trial, move the hot set back to the lowest free zone (the 42% zone was taken meanwhile), verify, re-measure.

.DESCRIPTION
  Needs <Out>	race.json, <Out>\manifest_hb_before.json (SHA-256 before any move). Log: <Out>\progress_back.log, summary: <Out>\summary_back.json.
  Steps: free-space runs, probe (lowest free zone candidate), plan --zone-start-lcn <candidate>, apply, verify, SHA-256 must equal manifest_hb_before.json,
  raw read of the zone, cold pvm x Runs (+ replay), cold direct replay x Runs. Nothing is reverted.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Out,
    [string]$Root = 'D:\Frostpunk',
    [string]$AsterExe = '',
    [string]$Repo = '',
    [string]$Python = 'python',
    [double]$ZoneStartPct = 90.0,
    [long]$Prev42Lcn = 205810188,
    [double]$Baseline42Median = 12.0567,
    [int]$ZoneMiB = 2048,
    [int]$Runs = 3,
    [int]$IdleSeconds = 600
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'not_admin: run from an elevated PowerShell. Nothing was changed.' }
if (-not $Repo) { $Repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) }
if (-not $AsterExe) { $AsterExe = Join-Path $Repo 'csharp\publish\cli\aster.exe' }
$TraceJson = Join-Path $Out 'trace.json'
foreach ($f in $AsterExe, $TraceJson, (Join-Path $Out 'lba_curve.json')) { if (-not (Test-Path -LiteralPath $f)) { throw "not found: $f" } }
$Root = $Root.TrimEnd('\')
$letter = $Root.Substring(0, 1)
$gameName = 'Frostpunk'
$log = Join-Path $Out 'progress_back.log'
$summary = [ordered]@{ zone_start_pct = $ZoneStartPct; prev42_lcn = $Prev42Lcn; baseline42_median_s = $Baseline42Median; stages = [ordered]@{} }
$exitCode = 0

function Log([string]$m) { $l = "{0} {1}" -f (Get-Date -Format 'HH:mm:ss'), $m; Add-Content -LiteralPath $log -Value $l -Encoding UTF8; Write-Host $l }
function Save-Summary { $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Out 'summary_back.json') -Encoding UTF8 }
function Invoke-Tool([string]$Name, [string]$Exe, [string[]]$ToolArgs) {
    $ErrorActionPreference = 'Continue'
    $errFile = Join-Path $Out "$Name.err.txt"
    $text = (& $Exe @ToolArgs 2>$errFile | Out-String)
    $code = $LASTEXITCODE
    Set-Content -LiteralPath (Join-Path $Out "$Name.txt") -Value $text -Encoding UTF8
    if ($code -ne 0) { throw "step '$Name' failed (exit $code):`n$text`n$(Get-Content -LiteralPath $errFile -Raw -ErrorAction SilentlyContinue)" }
    return $text
}
function Invoke-Purge([string]$Tag) {
    $t = Invoke-Tool "$Tag-purge" $Python @('-m', 'bench.cache_control')
    if ($t -notmatch 'standby_purge') { throw "standby purge not confirmed ($Tag): $t" }
}
function Assert-GameClosed { if (@(Get-Process -Name $gameName -ErrorAction SilentlyContinue).Count -gt 0) { throw "$gameName is running" } }
function Get-Median($v) { return @($v | Sort-Object)[[int][math]::Floor(@($v).Count / 2)] }
function Get-Manifest([string[]]$Paths) { $m = [ordered]@{}; foreach ($p in $Paths) { $m[$p] = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }; return $m }
function Read-Json([string]$f) { return (Get-Content -LiteralPath $f -Raw -Encoding UTF8 | ConvertFrom-Json) }
function Run-Stage([string]$Name, [scriptblock]$Body) {
    Log "== stage $Name start"
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $ok = $true
    try { & $Body | Out-Null } catch {
        $ok = $false
        if ($script:summary.stages.Contains($Name)) { $script:summary.stages[$Name]['error'] = "$_" } else { $script:summary.stages[$Name] = [ordered]@{ error = "$_" } }
        Log "stage $Name FAILED: $_"
    }
    Log ("== stage $Name end ok={0} {1:N0}s" -f $ok, $sw.Elapsed.TotalSeconds)
    if (-not $ok) { $script:exitCode = 1 }
    Save-Summary
    return $ok
}

# plan (explicit zone start), apply dry-run, apply, verify; returns an ordered summary. $Tag names the files.
function Move-HotSet([string]$Tag, [long]$StartLcn) {
    $st = [ordered]@{ zone_start_lcn = $StartLcn }
    $plan = Join-Path $Out "plan_$Tag.json"
    $journal = Join-Path $Out "journal_$Tag.jsonl"
    $p = Invoke-Tool "plan_$Tag" $AsterExe @('plan', '--trace', $TraceJson, '--origin-root', $Root, '--zone-mib', "$ZoneMiB", '--preserve-mib', '4096', '--zone-start-lcn', "$StartLcn", '--out', $plan) | ConvertFrom-Json
    $st.plan_stats = $p.stats
    $labelArgs = @(); if ($script:label) { $labelArgs = @('--require-volume-label', $script:label) }
    $common = @('--allowed-root', $Root) + $labelArgs
    $dry = Invoke-Tool "apply_dry_$Tag" $AsterExe (@('apply', '--plan', $plan, '--journal', $journal, '--dry-run') + $common) | ConvertFrom-Json
    $st.apply_dry_run = $dry
    if ($dry.tasks_todo -le 0) { throw 'dry-run found nothing to move' }
    $labelPart = ''; if ($script:label) { $labelPart = " --require-volume-label `"$($script:label)`"" }
    Add-Content -LiteralPath (Join-Path $Out 'README_undo_back.txt') -Encoding UTF8 -Value @("[$Tag] undo (NOT run): & `"$AsterExe`" revert --journal `"$journal`" --allowed-root `"$Root`"$labelPart [--execute]")
    Assert-GameClosed
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $applied = Invoke-Tool "apply_$Tag" $AsterExe (@('apply', '--plan', $plan, '--journal', $journal, '--execute') + $common) | ConvertFrom-Json
    $st.apply = $applied
    $st.apply_seconds = [math]::Round($sw.Elapsed.TotalSeconds, 2)
    $script:summary.stages["move_$Tag"] = $st
    Save-Summary
    if (@($applied.failed).Count -ne 0 -or $null -ne $applied.stopped) { throw "apply failed/stopped: $($applied | ConvertTo-Json -Compress). Undo: README_undo_back.txt" }
    if ($applied.moved_bytes -ne $dry.bytes_todo) { throw "moved $($applied.moved_bytes) != dry-run $($dry.bytes_todo)" }
    $st.verify = Invoke-Tool "verify_$Tag" $AsterExe @('verify', '--plan', $plan) | ConvertFrom-Json
    if ($st.verify.verdict -ne 'contiguous') { throw "verify verdict '$($st.verify.verdict)'" }
    $pj = Get-Content -LiteralPath $plan -Raw -Encoding UTF8 | ConvertFrom-Json
    $st.paths = @(@($pj.tasks | ForEach-Object { $_.path }) + @($pj.order | ForEach-Object { $_.path }) + @($pj.fingerprints.PSObject.Properties.Name)) | Sort-Object -Unique
    return $st
}

function Raw-Read([string]$Name) {
    Invoke-Tool $Name $Python @('-X', 'utf8', '-m', 'bench.raw_hot_read', '--trace', $TraceJson, '--volume', "${letter}:", '--read', '1', '--out', (Join-Path $Out "$Name.json")) | Out-Null
    $c = Read-Json (Join-Path $Out "$Name.json")
    return [pscustomobject]@{ runs = $c.runs; lcn_pct = @($c.min_lcn_pct, $c.max_lcn_pct); seconds = $c.t_raw_median_s; mb_s = $c.raw_reps[0].mb_s }
}
function Pvm-Once([string]$Name, [string]$PrefetchJson) {
    Assert-GameClosed
    Invoke-Purge $Name
    $pf = Invoke-Tool "$Name-prefetch" $AsterExe @('prefetch', '--plan', $PrefetchJson, '--origin-root', $Root, '--budget-mib', "$ZoneMiB", '--method', 'pvm', '--execute') | ConvertFrom-Json
    $rp = Invoke-Tool "$Name-replay" $Python @('-X', 'utf8', '-m', 'bench.replay', '--trace', $TraceJson) | ConvertFrom-Json
    Log ("{0}: pvm {1:N2}s {2:N1} MB/s replay {3:N2}s" -f $Name, $pf.seconds, ($pf.bytes / 1e6 / $pf.seconds), $rp.total_s)
    return [pscustomobject]@{ prefetch_s = [double]$pf.seconds; mb_s = [math]::Round($pf.bytes / 1e6 / $pf.seconds, 1); replay_after_s = [double]$rp.total_s }
}

Push-Location $Repo
try {
    Assert-GameClosed
    $vol = Get-Volume -DriveLetter $letter
    $script:label = "$($vol.FileSystemLabel)"
    $labelArgs = @(); if ($script:label) { $labelArgs = @('--require-volume-label', $script:label) }
    $manifest = Join-Path $Out 'manifest_hb_before.json'
    if (-not (Test-Path -LiteralPath $manifest)) { throw "not found: $manifest" }
    $ok = Run-Stage 'back' {
        Invoke-Tool 'free_runs_back' $Python @('-X', 'utf8', '-m', 'bench.free_runs', '--out', (Join-Path $Out 'free_runs_back.json')) | Out-Null
        $probe = Invoke-Tool 'probe_back' $AsterExe (@('probe', '--volume', "${letter}:", '--plan', (Join-Path $Out 'plan_hb.json'), '--zone-mib', "$ZoneMiB") + $labelArgs) | ConvertFrom-Json
        if ($null -eq $probe.zone_candidate_lcn) { throw 'no contiguous free zone candidate' }
        $start = [int64]$probe.zone_candidate_lcn
        Log ("lowest free zone candidate lcn={0} = {1:N3}%" -f $start, ($start / $probe.total_clusters * 100))
        $st = Move-HotSet 'back' $start
        $paths = $st.paths
        $st.Remove('paths')
        $st.zone_pct_of_volume = [math]::Round($start / $probe.total_clusters * 100, 3)
        $script:summary.stages.back = $st
        Invoke-Purge 'backsha'
        $after = Get-Manifest $paths
        $before = Read-Json $manifest
        $same = $true
        foreach ($p in $paths) { if ($before.PSObject.Properties[$p].Value -ne $after[$p]) { $same = $false } }
        $st.sha_identical = $same
        $script:summary.stages.back = $st
        Save-Summary
        if (-not $same) { throw 'SHA-256 differs after the move back: STOP. Undo: README_undo_back.txt' }
    }
    if ($ok) {
        [void](Run-Stage 'measure' {
            $st = [ordered]@{ pvm = @(); direct = @() }
            $pf = Join-Path $Out 'plan_back.json.prefetch.json'
            $st.raw = Raw-Read 'raw_back'
            Log ("raw: {0:N2}s {1:N1} MB/s" -f $st.raw.seconds, $st.raw.mb_s)
            for ($i = 1; $i -le $Runs; $i++) { $st.pvm += Pvm-Once "backpvm$i" $pf }
            for ($i = 1; $i -le $Runs; $i++) {
                Assert-GameClosed
                Invoke-Purge "backdirect$i"
                $rp = Invoke-Tool "backdirect$i-replay" $Python @('-X', 'utf8', '-m', 'bench.replay', '--trace', $TraceJson) | ConvertFrom-Json
                $st.direct += [pscustomobject]@{ replay_s = [double]$rp.total_s }
                Log ("backdirect{0}: {1:N2}s" -f $i, $rp.total_s)
            }
            $st.median_pvm_s = Get-Median ($st.pvm | ForEach-Object { $_.prefetch_s })
            $st.median_direct_s = Get-Median ($st.direct | ForEach-Object { $_.replay_s })
            $script:summary.stages.measure = $st
        })
    }
} catch {
    $summary.error = "$_"
    $exitCode = 1
    Log "FAILED: $_"
} finally {
    Pop-Location
    Save-Summary
    Log "done exit=$exitCode"
}
exit $exitCode
