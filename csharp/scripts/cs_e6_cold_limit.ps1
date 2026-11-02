<#
.SYNOPSIS
  E6 (C#): why is the cold prefetch of a relaid-out game only ~135 MB/s? Raw LBA curve, raw read of the hot set, full relayout (no preserve rule), re-measure.

.DESCRIPTION
  Run ONCE from an elevated PowerShell (the caller starts it with -Verb RunAs):
    powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\csharp\scripts\cs_e6_cold_limit.ps1 -Out <repo>\results\cold_limit_<stamp>

  Stages (each logs to <Out>\progress.log, summary.json is rewritten after every stage; a failed measuring stage does not stop the later ones):
    free0   free-space runs before (bench.free_runs)
    sweep   raw read of the hot set with several queue depths / request sizes (reader sanity)
    curve   E6-0a raw LBA transfer-rate curve (bench.lba_curve, read only)
    raw0    E6-0b raw read of the hot set in LCN order, current layout (bench.raw_hot_read --read)
    relayout E6-2 plan with --preserve-mib 4096 (everything moves), probe, apply dry-run, SHA-256 before, apply --execute, verify, SHA-256 after
    raw1    raw read of the hot set after the relayout
    measure E6-3 cold pvm prefetch x Runs (+ replay after each), cold direct replay x Runs, one DiskIo ETW capture of a pvm prefetch
  -Skip <stage,stage>: skip stages. -TraceJson: trace to use (default <Out>\trace.json). Game must not be running; nothing is reverted by the script.
  The undo command is written to <Out>\README_undo.txt and NOT run.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Out,
    [string]$Root = 'D:\Frostpunk',
    [string]$AsterExe = '',
    [string]$Repo = '',
    [string]$Python = 'python',
    [string]$TraceJson = '',
    [int]$ZoneMiB = 2048,
    [int]$PreserveMiB = 4096,
    [int]$Runs = 3,
    [string[]]$Skip = @()
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'not_admin: run from an elevated PowerShell. Nothing was changed.' }
if (-not $Repo) { $Repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) }
if (-not $AsterExe) { $AsterExe = Join-Path $Repo 'csharp\publish\cli\aster.exe' }
if (-not (Test-Path -LiteralPath $AsterExe)) { throw "aster.exe not found: $AsterExe" }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
if (-not $TraceJson) { $TraceJson = Join-Path $Out 'trace.json' }
if (-not (Test-Path -LiteralPath $TraceJson)) { throw "trace not found: $TraceJson" }
$Root = $Root.TrimEnd('\')
$letter = $Root.Substring(0, 1)
$gameName = 'Frostpunk'
$plan = Join-Path $Out 'plan.json'
$prefetchJson = "$plan.prefetch.json"
$journal = Join-Path $Out 'journal.jsonl'
$log = Join-Path $Out 'progress.log'
$summary = [ordered]@{ root = $Root; out = $Out; aster = $AsterExe; trace = $TraceJson; zone_mib = $ZoneMiB; preserve_mib = $PreserveMiB; runs = $Runs; stages = [ordered]@{} }
$exitCode = 0

function Log([string]$m) { $l = "{0} {1}" -f (Get-Date -Format 'HH:mm:ss'), $m; Add-Content -LiteralPath $log -Value $l -Encoding UTF8; Write-Host $l }  # file first: a paused console (QuickEdit) must not hide progress
function Save-Summary { $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Out 'summary.json') -Encoding UTF8 }

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
function Run-Stage([string]$Name, [scriptblock]$Body) {
    if ($Skip -contains $Name) { Log "stage $Name skipped"; return $true }
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
function Read-Json([string]$f) { return (Get-Content -LiteralPath $f -Raw -Encoding UTF8 | ConvertFrom-Json) }

Push-Location $Repo
try {
    Assert-GameClosed
    $vol = Get-Volume -DriveLetter $letter
    $label = "$($vol.FileSystemLabel)"
    $labelArgs = @(); if ($label) { $labelArgs = @('--require-volume-label', $label) }
    $common = @('--allowed-root', $Root) + $labelArgs
    try { $d = Get-Partition -DriveLetter $letter | Get-Disk; $summary.disk = "$($d.FriendlyName) bus=$($d.BusType)" } catch { $summary.disk = "unknown: $_" }
    Log "volume ${letter}: label='$label' disk=$($summary.disk)"
    Save-Summary

    [void](Run-Stage 'free0' {
        Invoke-Tool 'free_runs_before' $Python @('-X', 'utf8', '-m', 'bench.free_runs', '--out', (Join-Path $Out 'free_runs_before.json')) | Out-Null
        $summary.stages.free0 = [ordered]@{ file = 'free_runs_before.json' }
    })

    [void](Run-Stage 'sweep' {
        Assert-GameClosed
        $rows = @()
        foreach ($cfg in @(@(1, 4), @(2, 4), @(4, 4), @(8, 4), @(4, 1), @(4, 16))) {
            $f = Join-Path $Out ("sweep_qd{0}_c{1}.json" -f $cfg[0], $cfg[1])
            Invoke-Tool ("sweep_qd{0}_c{1}" -f $cfg[0], $cfg[1]) $Python @('-X', 'utf8', '-m', 'bench.raw_hot_read', '--trace', $TraceJson, '--volume', "${letter}:", '--read', '1', '--qd', "$($cfg[0])", '--chunk-mib', "$($cfg[1])", '--out', $f) | Out-Null
            $c = Read-Json $f
            $rows += [pscustomobject]@{ qd = $cfg[0]; chunk_mib = $cfg[1]; seconds = $c.t_raw_median_s; mb_s = $c.raw_reps[0].mb_s }
            Log ("sweep qd={0} chunk={1}MiB: {2:N2}s {3:N1} MB/s" -f $cfg[0], $cfg[1], $c.t_raw_median_s, $c.raw_reps[0].mb_s)
        }
        $summary.stages.sweep = [ordered]@{ rows = $rows }
    })

    [void](Run-Stage 'curve' {
        $r = Invoke-Tool 'lba_curve' $Python @('-X', 'utf8', '-m', 'bench.lba_curve', '--volume', "${letter}:", '--out', (Join-Path $Out 'lba_curve.json'))
        $c = Read-Json (Join-Path $Out 'lba_curve.json')
        $summary.stages.curve = [ordered]@{ file = 'lba_curve.json'; mbps_0_10pct = $c.mbps_0_10pct; mbps_90_100pct = $c.mbps_90_100pct; ratio = $c.ratio_0_10_over_90_100; spearman = $c.spearman_frac_vs_mbps }
    })

    [void](Run-Stage 'raw0' {
        Assert-GameClosed
        Invoke-Tool 'raw_hot_before' $Python @('-X', 'utf8', '-m', 'bench.raw_hot_read', '--trace', $TraceJson, '--volume', "${letter}:", '--read', "$Runs", '--out', (Join-Path $Out 'raw_hot_before.json')) | Out-Null
        $c = Read-Json (Join-Path $Out 'raw_hot_before.json')
        $summary.stages.raw0 = [ordered]@{ file = 'raw_hot_before.json'; runs = $c.runs; hot_bytes = $c.hot_bytes; t_raw_median_s = $c.t_raw_median_s; reps = $c.raw_reps }
    })

    $relayoutOk = Run-Stage 'relayout' {
        Assert-GameClosed
        $st = [ordered]@{}
        $dry = Invoke-Tool 'plan-dry' $AsterExe @('plan', '--trace', $TraceJson, '--origin-root', $Root, '--zone-mib', "$ZoneMiB", '--preserve-mib', "$PreserveMiB", '--dry-run') | ConvertFrom-Json
        $st.plan_dry_stats = $dry.stats
        $p = Invoke-Tool 'plan' $AsterExe @('plan', '--trace', $TraceJson, '--origin-root', $Root, '--zone-mib', "$ZoneMiB", '--preserve-mib', "$PreserveMiB", '--out', $plan) | ConvertFrom-Json
        $st.plan_stats = $p.stats
        $labelPart = ''; if ($label) { $labelPart = " --require-volume-label `"$label`"" }
        $revert = "& `"$AsterExe`" revert --journal `"$journal`" --allowed-root `"$Root`"$labelPart"
        Set-Content -LiteralPath (Join-Path $Out 'README_undo.txt') -Encoding UTF8 -Value @("Undo (NOT run by the script; elevated PowerShell, only while $gameName is closed). Preview first:", $revert, 'Real undo:', "$revert --execute")
        if ([int64]$p.stats.moved_bytes -le 0) { throw 'plan moves nothing' }
        $probe = Invoke-Tool 'probe' $AsterExe (@('probe', '--volume', "${letter}:", '--plan', $plan, '--zone-mib', "$ZoneMiB") + $labelArgs) | ConvertFrom-Json
        $st.probe = $probe
        if (-not $probe.free_match) { throw 'bitmap free clusters != NTFS FreeClusters' }
        if ($null -eq $probe.zone_candidate_lcn) { throw 'no contiguous free zone candidate on the volume' }
        $st.zone_pct_of_volume = [math]::Round($probe.zone_candidate_lcn / $probe.total_clusters * 100, 3)
        Log "zone candidate lcn=$($probe.zone_candidate_lcn) = $($st.zone_pct_of_volume)% of the volume"
        $dryApply = Invoke-Tool 'apply-dry-run' $AsterExe (@('apply', '--plan', $plan, '--journal', $journal, '--dry-run') + $common) | ConvertFrom-Json
        $st.apply_dry_run = $dryApply
        if ($dryApply.tasks_todo -le 0) { throw 'dry-run found nothing to move' }
        $pj = Get-Content -LiteralPath $plan -Raw -Encoding UTF8 | ConvertFrom-Json
        $paths = @(@($pj.tasks | ForEach-Object { $_.path }) + @($pj.order | ForEach-Object { $_.path }) + @($pj.fingerprints.PSObject.Properties.Name)) | Sort-Object -Unique
        $bad = @($paths | Where-Object { -not $_.StartsWith("$Root\", [StringComparison]::OrdinalIgnoreCase) -or $_.Contains('..') })
        if ($bad.Count -gt 0) { throw "plan targets files outside ${Root}: $(($bad | Select-Object -First 5) -join ', ')" }
        $pj = $null
        Log "SHA-256 before ($($paths.Count) files)"
        $before = Get-Manifest $paths
        Assert-GameClosed
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $applied = Invoke-Tool 'apply' $AsterExe (@('apply', '--plan', $plan, '--journal', $journal, '--execute') + $common) | ConvertFrom-Json
        $sw.Stop()
        $st.apply = $applied
        $st.apply_seconds = [math]::Round($sw.Elapsed.TotalSeconds, 2)
        $st.apply_mb_s = [math]::Round(($applied.moved_bytes / 1MB) / $sw.Elapsed.TotalSeconds, 2)
        $script:summary.stages.relayout = $st
        Save-Summary
        if (@($applied.failed).Count -ne 0 -or $null -ne $applied.stopped) { throw "apply failed/stopped: $($applied | ConvertTo-Json -Compress). Undo: README_undo.txt" }
        if ($applied.moved_bytes -ne $dryApply.bytes_todo) { throw "moved $($applied.moved_bytes) != dry-run $($dryApply.bytes_todo)" }
        $st.verify = Invoke-Tool 'verify' $AsterExe @('verify', '--plan', $plan) | ConvertFrom-Json
        Invoke-Purge 'manifest'
        $after = Get-Manifest $paths
        $st.sha_identical = (($before | ConvertTo-Json -Compress) -eq ($after | ConvertTo-Json -Compress))
        $st.files_hashed = $paths.Count
        $script:summary.stages.relayout = $st
        Save-Summary
        if (-not $st.sha_identical) { throw 'SHA-256 manifest differs after relayout: STOP. Undo: README_undo.txt' }
        if ($st.verify.verdict -ne 'contiguous') { throw "verify verdict '$($st.verify.verdict)'" }
        Invoke-Tool 'free_runs_after' $Python @('-X', 'utf8', '-m', 'bench.free_runs', '--out', (Join-Path $Out 'free_runs_after.json')) | Out-Null
    }
    if (-not $relayoutOk -and -not ($Skip -contains 'relayout')) { Log 'relayout failed: later stages are skipped (layout state unknown)'; $Skip += @('raw1', 'measure') }

    [void](Run-Stage 'raw1' {
        Assert-GameClosed
        Invoke-Tool 'raw_hot_after' $Python @('-X', 'utf8', '-m', 'bench.raw_hot_read', '--trace', $TraceJson, '--volume', "${letter}:", '--read', "$Runs", '--out', (Join-Path $Out 'raw_hot_after.json')) | Out-Null
        $c = Read-Json (Join-Path $Out 'raw_hot_after.json')
        $summary.stages.raw1 = [ordered]@{ file = 'raw_hot_after.json'; runs = $c.runs; hot_bytes = $c.hot_bytes; t_raw_median_s = $c.t_raw_median_s; reps = $c.raw_reps }
    })

    [void](Run-Stage 'measure' {
        $st = [ordered]@{ pvm = @(); direct = @() }
        for ($i = 1; $i -le $Runs; $i++) {
            Assert-GameClosed
            Invoke-Purge "pvm$i"
            $pf = Invoke-Tool "pvm$i-prefetch" $AsterExe @('prefetch', '--plan', $prefetchJson, '--origin-root', $Root, '--budget-mib', "$ZoneMiB", '--method', 'pvm', '--execute') | ConvertFrom-Json
            $rp = Invoke-Tool "pvm$i-replay" $Python @('-X', 'utf8', '-m', 'bench.replay', '--trace', $TraceJson) | ConvertFrom-Json
            $st.pvm += [pscustomobject]@{ prefetch_s = [double]$pf.seconds; bytes = [int64]$pf.bytes; requests = $pf.requests; errors = $pf.errors; mb_s = [math]::Round($pf.bytes / 1e6 / $pf.seconds, 1); replay_after_s = [double]$rp.total_s }
            Log ("pvm{0}: {1:N2}s {2:N1} MB/s replay {3:N2}s" -f $i, $pf.seconds, ($pf.bytes / 1e6 / $pf.seconds), $rp.total_s)
        }
        for ($i = 1; $i -le $Runs; $i++) {
            Assert-GameClosed
            Invoke-Purge "direct$i"
            $rp = Invoke-Tool "direct$i-replay" $Python @('-X', 'utf8', '-m', 'bench.replay', '--trace', $TraceJson) | ConvertFrom-Json
            $st.direct += [pscustomobject]@{ replay_s = [double]$rp.total_s; mib_s = $rp.throughput_mib_s }
            Log ("direct{0}: {1:N2}s" -f $i, $rp.total_s)
        }
        $st.median_pvm_prefetch_s = Get-Median ($st.pvm | ForEach-Object { $_.prefetch_s })
        $st.median_direct_s = Get-Median ($st.direct | ForEach-Object { $_.replay_s })
        $script:summary.stages.measure = $st
        Save-Summary
        # one DiskIo capture of a pvm prefetch
        $etl = Join-Path $Out 'diskio_pvm.etl'
        $wprOk = $false
        try {
            $ErrorActionPreference = 'Continue'
            & wpr.exe -cancel 2>&1 | Out-Null
            & wpr.exe -start "$(Join-Path $Repo 'csharp\scripts\aster_diskio.wprp')!AsterDiskIO" -filemode 2>&1 | Out-String | Set-Content -LiteralPath (Join-Path $Out 'wpr_start.txt')
            if ($LASTEXITCODE -ne 0) { throw "wpr -start failed (exit $LASTEXITCODE)" }
            $wprOk = $true
            Invoke-Purge 'etw'
            $pf = Invoke-Tool 'etw-prefetch' $AsterExe @('prefetch', '--plan', $prefetchJson, '--origin-root', $Root, '--budget-mib', "$ZoneMiB", '--method', 'pvm', '--execute') | ConvertFrom-Json
            $st.etw_prefetch_s = [double]$pf.seconds
        } catch { $st.etw_error = "$_"; Log "ETW capture failed: $_" }
        finally {
            if ($wprOk) { & wpr.exe -stop $etl 2>&1 | Out-String | Set-Content -LiteralPath (Join-Path $Out 'wpr_stop.txt') }
        }
        $script:summary.stages.measure = $st
    })
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
