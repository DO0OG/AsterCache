<#
.SYNOPSIS
  E6-5 (C#): move the whole hot set into a zone in the high LBA band (default 90% of the volume), compare with the zone at 42%.

.DESCRIPTION
  Run ONCE from an elevated PowerShell:
    powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\csharp\scripts\cs_e6_highband.ps1 -Out <repo>\results\cold_limit_<stamp>
  Needs <Out>\trace.json, <Out>\lba_curve.json (from cs_e6_cold_limit.ps1). Stages (summary_hb.json is rewritten after every stage; log: <Out>\progress_hb.log):
    points   used fraction of every measured curve point (bench.free_runs --points)
    relayout plan --zone-start-lcn (ZoneStartPct of the volume) --preserve-mib 4096, apply dry-run, SHA-256 before, apply, verify
    measure  raw read of the zone right after the write, cold pvm prefetch x Runs (+ replay), cold direct replay x Runs,
             then IdleSeconds idle, one more cold pvm prefetch and raw read
    finish   SHA-256 after (must be identical); if the median pvm is NOT faster than -Baseline42Median the hot set is moved back to the
             42% zone (-Prev42Lcn) with the same steps. Nothing else is reverted.
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
$log = Join-Path $Out 'progress_hb.log'
$summary = [ordered]@{ zone_start_pct = $ZoneStartPct; prev42_lcn = $Prev42Lcn; baseline42_median_s = $Baseline42Median; stages = [ordered]@{} }
$exitCode = 0

function Log([string]$m) { $l = "{0} {1}" -f (Get-Date -Format 'HH:mm:ss'), $m; Add-Content -LiteralPath $log -Value $l -Encoding UTF8; Write-Host $l }
function Save-Summary { $summary | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Out 'summary_hb.json') -Encoding UTF8 }
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
    Add-Content -LiteralPath (Join-Path $Out 'README_undo_hb.txt') -Encoding UTF8 -Value @("[$Tag] undo (NOT run): & `"$AsterExe`" revert --journal `"$journal`" --allowed-root `"$Root`"$labelPart [--execute]")
    Assert-GameClosed
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $applied = Invoke-Tool "apply_$Tag" $AsterExe (@('apply', '--plan', $plan, '--journal', $journal, '--execute') + $common) | ConvertFrom-Json
    $st.apply = $applied
    $st.apply_seconds = [math]::Round($sw.Elapsed.TotalSeconds, 2)
    $script:summary.stages["move_$Tag"] = $st
    Save-Summary
    if (@($applied.failed).Count -ne 0 -or $null -ne $applied.stopped) { throw "apply failed/stopped: $($applied | ConvertTo-Json -Compress). Undo: README_undo_hb.txt" }
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
    Log "volume ${letter}: label='$($script:label)'"

    [void](Run-Stage 'points' {
        Invoke-Tool 'points_used' $Python @('-X', 'utf8', '-m', 'bench.free_runs', '--points', (Join-Path $Out 'lba_curve.json'), '--out', (Join-Path $Out 'points_used.json')) | Out-Null
        $summary.stages.points = [ordered]@{ file = 'points_used.json' }
    })

    $script:paths = $null; $script:before = $null; $script:hbMedian = $null
    $hbPlanPrefetch = Join-Path $Out 'plan_hb.json.prefetch.json'
    $moved = Run-Stage 'relayout' {
        $st0 = Invoke-Tool 'probe_hb' $AsterExe (@('probe', '--volume', "${letter}:") + $(if ($script:label) { @('--require-volume-label', $script:label) } else { @() })) | ConvertFrom-Json
        if (-not $st0.free_match) { throw 'bitmap free clusters != NTFS FreeClusters' }
        $total = [int64]$st0.total_clusters
        $start = [int64]([math]::Floor($total * $ZoneStartPct / 100.0 / 4.0) * 4)
        Log "high-band zone start lcn=$start ($ZoneStartPct% of $total)"
        # SHA before needs the plan's file list: plan once (no move) to get it
        Invoke-Tool 'plan_hb_files' $AsterExe @('plan', '--trace', $TraceJson, '--origin-root', $Root, '--zone-mib', "$ZoneMiB", '--preserve-mib', '4096', '--zone-start-lcn', "$start", '--out', (Join-Path $Out 'plan_hb_files.json'))  | Out-Null
        $pj = Get-Content -LiteralPath (Join-Path $Out 'plan_hb_files.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $script:paths = @(@($pj.tasks | ForEach-Object { $_.path }) + @($pj.order | ForEach-Object { $_.path }) + @($pj.fingerprints.PSObject.Properties.Name)) | Sort-Object -Unique
        $pj = $null
        $bad = @($script:paths | Where-Object { -not $_.StartsWith("$Root\", [StringComparison]::OrdinalIgnoreCase) -or $_.Contains('..') })
        if ($bad.Count -gt 0) { throw "plan targets files outside ${Root}: $(($bad | Select-Object -First 5) -join ', ')" }
        Log "SHA-256 before ($($script:paths.Count) files)"
        $script:before = Get-Manifest $script:paths
        $script:before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Out 'manifest_hb_before.json') -Encoding UTF8
        $st = Move-HotSet 'hb' $start
        $st.Remove('paths')
        $script:summary.stages.relayout = $st
    }
    if (-not $moved) { Log 'relayout failed: stopping (layout state unknown, see README_undo_hb.txt)'; throw 'relayout failed' }

    $hb = $null
    [void](Run-Stage 'measure' {
        $st = [ordered]@{ pvm = @(); direct = @() }
        $st.raw_after_write = Raw-Read 'raw_hb_fresh'
        Log ("raw right after write: {0:N2}s {1:N1} MB/s" -f $st.raw_after_write.seconds, $st.raw_after_write.mb_s)
        for ($i = 1; $i -le $Runs; $i++) { $st.pvm += Pvm-Once "hbpvm$i" $hbPlanPrefetch }
        for ($i = 1; $i -le $Runs; $i++) {
            Assert-GameClosed
            Invoke-Purge "hbdirect$i"
            $rp = Invoke-Tool "hbdirect$i-replay" $Python @('-X', 'utf8', '-m', 'bench.replay', '--trace', $TraceJson) | ConvertFrom-Json
            $st.direct += [pscustomobject]@{ replay_s = [double]$rp.total_s }
            Log ("hbdirect{0}: {1:N2}s" -f $i, $rp.total_s)
        }
        $st.median_pvm_s = Get-Median ($st.pvm | ForEach-Object { $_.prefetch_s })
        $st.median_direct_s = Get-Median ($st.direct | ForEach-Object { $_.replay_s })
        $script:summary.stages.measure = $st
        Save-Summary
        Log "idle $IdleSeconds s"
        Start-Sleep -Seconds $IdleSeconds
        $st.pvm_after_idle = Pvm-Once 'hbidle' $hbPlanPrefetch
        $st.raw_after_idle = Raw-Read 'raw_hb_idle'
        Log ("raw after idle: {0:N2}s {1:N1} MB/s" -f $st.raw_after_idle.seconds, $st.raw_after_idle.mb_s)
        $script:summary.stages.measure = $st
        $script:hbMedian = $st.median_pvm_s
    })

    [void](Run-Stage 'finish' {
        $st = [ordered]@{}
        Invoke-Purge 'hbsha'
        $after = Get-Manifest $script:paths
        $st.sha_identical = (($script:before | ConvertTo-Json -Compress) -eq ($after | ConvertTo-Json -Compress))
        if (-not $st.sha_identical) { $script:summary.stages.finish = $st; throw 'SHA-256 manifest differs after the high-band relayout: STOP. Undo: README_undo_hb.txt' }
        $faster = ($null -ne $script:hbMedian) -and ($script:hbMedian -lt $Baseline42Median)
        $st.high_band_faster = $faster
        $st.hb_median_pvm_s = $script:hbMedian
        if ($faster) {
            Log "high band is faster ($($script:hbMedian) s < $Baseline42Median s): keeping it"
            $script:summary.stages.finish = $st
            return
        }
        Log "high band NOT faster ($($script:hbMedian) s vs $Baseline42Median s): moving back to the 42% zone"
        $back = Move-HotSet 'back42' $Prev42Lcn
        $back.Remove('paths')
        $st.back42 = $back
        Invoke-Purge 'back42sha'
        $after2 = Get-Manifest $script:paths
        $st.back42_sha_identical = (($script:before | ConvertTo-Json -Compress) -eq ($after2 | ConvertTo-Json -Compress))
        $st.raw_back42 = Raw-Read 'raw_back42'
        $st.pvm_back42 = Pvm-Once 'back42pvm' (Join-Path $Out 'plan_back42.json.prefetch.json')
        $script:summary.stages.finish = $st
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
