<#
.SYNOPSIS
  E4 (C#): first real game. Live ETW trace of a game load, relayout of the hot blocks, cold replay and prefetch before/after.

.DESCRIPTION
  Run ONCE from an elevated (Administrator) PowerShell, by the user:
    powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\csharp\scripts\cs_e4_game.ps1

  Steps: live trace (the script starts it, then WAITS for you to start the game, load the first map and quit the game) ->
  plan (zone sized from the hot bytes) -> SHA-256 of the planned files -> cold replay x Runs (standby purge before each run) ->
  probe -> apply --dry-run -> apply --execute -> verify -> purge + SHA-256 again (must be identical) ->
  cold replay x Runs, cold prefetch + replay x Runs (prefetch and replay seconds kept apart).
  -SkipApply: trace, plan and the "before" measurement only; nothing is moved.
  The game must not be running when anything is measured or moved; apply refuses to start while the process exists.
  Results: <repo>\results\csharp_e4_<yyyyMMdd_HHmmss>\summary.json and README.txt (revert command, NOT run by the script).

  Trace format: aster trace writes a list of {path, offset, length, cost}, the same rows bench.replay reads; the script
  checks the rows after the trace. The file is UTF-8, so bench.replay is started with python -X utf8.
  The prefetch input is <plan>.prefetch.json (the plan itself has no "ranges" list), with --budget-mib set to the zone size.
#>
[CmdletBinding()]
param(
    [string]$GameExe = 'D:\Frostpunk\Frostpunk.exe',
    [string]$Root = 'D:\Frostpunk',
    [int]$WaitGameSeconds = 600,
    [int]$MaxZoneMiB = 1536,
    [int]$Runs = 3,
    [string]$Python = 'python',
    [string]$AsterExe = '',
    [string]$Repo = '',
    [switch]$SkipApply
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 0. Elevation first. Nothing below runs, and nothing is changed, without it.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'not_admin: 관리자 필요. 관리자 PowerShell 에서 다시 실행하세요. 아무것도 바꾸지 않았습니다.'
}

if (-not $Repo) { $Repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) }
if (-not $AsterExe) { $AsterExe = Join-Path $Repo 'csharp\publish\cli\aster.exe' }
if (-not (Test-Path -LiteralPath $AsterExe)) { throw "C# CLI not found: $AsterExe (publish the CLI first, see csharp\README.md)" }
$Root = $Root.TrimEnd('\')
if ($Root -notmatch '^[A-Za-z]:\\[^\\]+$') { throw "Root must look like X:\Folder, got '$Root'" }
if (-not (Test-Path -LiteralPath $GameExe -PathType Leaf)) { throw "game exe not found: $GameExe" }
if (-not $GameExe.StartsWith("$Root\", [StringComparison]::OrdinalIgnoreCase)) { throw "GameExe must be inside ${Root}: $GameExe" }
if ($Runs -lt 1 -or $MaxZoneMiB -lt 16 -or $WaitGameSeconds -lt 30) { throw 'Runs >= 1, MaxZoneMiB >= 16, WaitGameSeconds >= 30 required' }
$letter = $Root.Substring(0, 1)
$exeName = Split-Path -Leaf $GameExe
$gameName = [System.IO.Path]::GetFileNameWithoutExtension($GameExe)

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$out = Join-Path $Repo "results\csharp_e4_$stamp"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$traceJson = Join-Path $out 'trace.json'
$stopFile = Join-Path $out 'live.stop'
$liveOut = Join-Path $out 'trace-live-stdout.txt'
$liveErr = Join-Path $out 'trace-live-stderr.txt'
$liveIn = Join-Path $out 'trace-live-stdin.txt'
$plan = Join-Path $out 'plan.json'
$prefetchJson = "$plan.prefetch.json"
$journal = Join-Path $out 'journal.jsonl'
$Mib = 1MB
$summary = [ordered]@{ game = $GameExe; root = $Root; runs = $Runs; max_zone_mib = $MaxZoneMiB; repo = $Repo; skip_apply = [bool]$SkipApply }
$live = $null
$label = ''
$exitCode = 0

function Invoke-Tool([string]$Name, [string]$Exe, [string[]]$ToolArgs) {
    $ErrorActionPreference = 'Continue'  # PS 5.1 turns native stderr into a terminating error under 'Stop'
    $errFile = Join-Path $out "$Name.err.txt"  # stderr apart from stdout: one warning line must not break ConvertFrom-Json
    $text = (& $Exe @ToolArgs 2>$errFile | Out-String)
    $code = $LASTEXITCODE
    Set-Content -LiteralPath (Join-Path $out "$Name.txt") -Value $text -Encoding UTF8
    if ($code -ne 0) { throw "step '$Name' failed (exit $code; aster: 3 = not admin, 2 = bad arguments):`n$text`n$(Get-Content -LiteralPath $errFile -Raw -ErrorAction SilentlyContinue)" }
    return $text
}

function Invoke-Purge([string]$Tag) {
    $cold = Invoke-Tool "$Tag-purge" $Python @('-m', 'bench.cache_control')
    if ($cold -notmatch 'standby_purge') { throw "standby purge did not confirm ($Tag): stop, the run would not be cold" }
}

function Get-Median($Values) { return @($Values | Sort-Object)[[int][math]::Floor($Runs / 2)] }

function Get-GameProcess { return @(Get-Process -Name $gameName -ErrorAction SilentlyContinue) }

function Assert-GameClosed([string]$Why) {
    if (@(Get-GameProcess).Count -gt 0) { throw "$gameName is running: $Why must not start while the game uses ${letter}:" }
}

# Cold replay x Runs (standby purge before each). Prefetch (if asked) runs after the purge, before the replay; both times are kept apart.
function Measure-Cold([string]$Tag, [bool]$Prefetch) {
    $rows = @()
    for ($i = 1; $i -le $Runs; $i++) {
        Assert-GameClosed "measurement $Tag"
        Invoke-Purge "$Tag-cold$i"
        $pfS = 0.0; $pfB = 0
        if ($Prefetch) {
            $pf = Invoke-Tool "$Tag-prefetch$i" $AsterExe @('prefetch', '--plan', $prefetchJson, '--origin-root', $Root,
                                                          '--budget-mib', "$($script:zoneMiB)", '--method', 'pvm', '--execute') | ConvertFrom-Json
            if ($pf.errors -ne 0 -or $pf.bytes -le 0) { throw "prefetch read errors=$($pf.errors) bytes=$($pf.bytes)" }
            $pfS = [double]$pf.seconds; $pfB = [int64]$pf.bytes
        }
        $r = Invoke-Tool "$Tag-replay$i" $Python @('-X', 'utf8', '-m', 'bench.replay', '--trace', $traceJson) | ConvertFrom-Json
        $rows += [pscustomobject]@{ prefetch_s = $pfS; prefetch_bytes = $pfB; replay_s = [double]$r.total_s; total_s = $pfS + [double]$r.total_s }
    }
    return [ordered]@{
        prefetch = $Prefetch; runs = $rows
        median_total_s = Get-Median ($rows | ForEach-Object { $_.total_s })
        median_prefetch_s = Get-Median ($rows | ForEach-Object { $_.prefetch_s })
        median_replay_s = Get-Median ($rows | ForEach-Object { $_.replay_s })
    }
}

function Get-Manifest([string[]]$Paths) {
    $m = [ordered]@{}
    foreach ($p in $Paths) { $m[$p] = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash }
    return $m
}

function New-Plan([int]$ZoneMib) {
    return Invoke-Tool "plan-z$ZoneMib" $AsterExe @('plan', '--trace', $traceJson, '--origin-root', $Root, '--zone-mib', "$ZoneMib", '--out', $plan) | ConvertFrom-Json
}

function Save-Readme {
    $labelPart = ''; if ($label) { $labelPart = " --require-volume-label `"$label`"" }
    $revert = "& `"$AsterExe`" revert --journal `"$journal`" --allowed-root `"$Root`"$labelPart"
    @(
        "E4 result folder: $out",
        '',
        "Undo (not run by the script; run from an elevated PowerShell, only while $gameName is closed).",
        'Preview first (no --execute, moves nothing):',
        $revert,
        'Then the real undo:',
        "$revert --execute",
        '',
        '4단계(probe, apply, verify, SHA 재계산) 중에는 Ctrl+C 를 누르지 마십시오. 이동 도중 중단되면 위 undo 로 저널을 되돌립니다.',
        '트레이스(1단계)를 강제 종료했다면 남은 ETW 세션을 닫으십시오: logman stop AsterCacheTrace -ets',
        '',
        "The relayout only moves data clusters of files inside $Root on ${letter}: , file contents stay identical (SHA-256 of the planned files compared).",
        'The data is safe without undoing; undo only restores the old physical layout. Without a journal file nothing was moved.'
    ) | Set-Content -LiteralPath (Join-Path $out 'README.txt') -Encoding UTF8
}

Push-Location $Repo
try {
    Write-Host '== 0. python, volume, CLI'
    $pv = [int]((& $Python -c 'import sys; print(sys.version_info[0]*100+sys.version_info[1])') | Out-String).Trim()
    if ($pv -lt 309) { throw "Python 3.9+ required (found $pv)" }
    $vol = Get-Volume -DriveLetter $letter
    $label = "$($vol.FileSystemLabel)"
    if ($vol.FileSystem -ne 'NTFS') { throw "${letter}: is $($vol.FileSystem), NTFS required" }
    $diskModel = $null; $busType = $null
    try { $d = Get-Partition -DriveLetter $letter | Get-Disk; $diskModel = "$($d.FriendlyName)"; $busType = "$($d.BusType)" } catch { $diskModel = "unknown: $_" }
    $labelArgs = @(); if ($label) { $labelArgs = @('--require-volume-label', $label) }
    $common = @('--allowed-root', $Root) + $labelArgs
    $summary.volume = "${letter}:"; $summary.label = $label; $summary.disk_model = $diskModel; $summary.bus_type = $busType
    Write-Host "volume ${letter}: label='$label' disk='$diskModel' bus=$busType"
    Assert-GameClosed 'the trace'

    Write-Host '== 1. live trace'
    $argText = (@('trace', '--live', '--exe', $exeName, '--origin-root', $Root, '--out', $traceJson, '--stop-file', $stopFile) |
        ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    [System.IO.File]::WriteAllBytes($liveIn, [byte[]]@())  # empty stdin: an Enter keypress must not stop the trace
    $live = Start-Process -FilePath $AsterExe -ArgumentList $argText -RedirectStandardInput $liveIn -RedirectStandardOutput $liveOut -RedirectStandardError $liveErr -PassThru -WindowStyle Hidden
    $null = $live.Handle  # PS 5.1 loses ExitCode of a -PassThru process unless the handle is read right away
    Start-Sleep -Seconds 3
    if ($live.HasExited) {
        throw "live trace exited right away (exit $($live.ExitCode)): $((Get-Content -LiteralPath $liveOut, $liveErr -Raw -ErrorAction SilentlyContinue) -join ' ')"
    }
    Write-Host ''
    Write-Host '################################################################' -ForegroundColor Yellow
    Write-Host "  지금 $gameName 를 실행해 첫 맵 로딩까지 간 뒤 게임을 종료하십시오" -ForegroundColor Yellow
    Write-Host "  (최대 $WaitGameSeconds 초 안에 시작, 시작 뒤에도 같은 시간 안에 종료)" -ForegroundColor Yellow
    Write-Host '################################################################' -ForegroundColor Yellow
    Write-Host ''
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while (@(Get-GameProcess).Count -eq 0) {
        if ($live.HasExited) { throw 'live trace stopped by itself while waiting for the game' }
        if ($sw.Elapsed.TotalSeconds -ge $WaitGameSeconds) { throw "game_not_started: no $gameName process within $WaitGameSeconds s" }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "$gameName started. Waiting for it to exit..."
    $sw.Restart()
    while (@(Get-GameProcess).Count -gt 0) {
        if ($live.HasExited) { throw 'live trace stopped by itself while the game was running' }
        if ($sw.Elapsed.TotalSeconds -ge $WaitGameSeconds) { throw "game_still_running after $WaitGameSeconds s: close it and run the script again" }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "$gameName exited. Stopping the trace in 5 s..."
    Start-Sleep -Seconds 5
    while (@(Get-GameProcess).Count -gt 0) {  # the game came back (launcher restart): keep waiting
        if ($live.HasExited) { throw 'live trace stopped by itself while the game was running' }
        if ($sw.Elapsed.TotalSeconds -ge $WaitGameSeconds) { throw "game_still_running after $WaitGameSeconds s: close it and run the script again" }
        Start-Sleep -Milliseconds 500
    }
    New-Item -ItemType File -Path $stopFile | Out-Null
    if (-not $live.WaitForExit(120000)) { throw 'live trace did not finish within 120 s after the stop file' }
    if ($live.ExitCode -ne 0) { throw "live trace failed (exit $($live.ExitCode)): $((Get-Content -LiteralPath $liveOut, $liveErr -Raw -ErrorAction SilentlyContinue) -join ' ')" }
    $liveText = Get-Content -LiteralPath $liveOut -Raw -Encoding UTF8
    $lost = $null
    if ($liveText -match "'events_lost': (\d+)") { $lost = [int64]$Matches[1] }
    $liveLine = @($liveText -split "`r?`n" | Where-Object { $_ -match '^live \{' }) | Select-Object -Last 1
    $unionLine = @($liveText -split "`r?`n" | Where-Object { $_ -match '^\{"union"' }) | Select-Object -Last 1
    if (-not (Test-Path -LiteralPath $traceJson) -or -not $unionLine) { throw "trace.json was not written: see $liveOut" }
    $union = $unionLine | ConvertFrom-Json
    $rows = Get-Content -LiteralPath $traceJson -Raw -Encoding UTF8 | ConvertFrom-Json
    $rows = @($rows)  # PS 5.1: a JSON array comes back as ONE object; @() on the variable keeps the array
    if ($rows.Count -eq 0) { throw 'trace has no events: the game was not seen or no read was inside the root' }
    $r0 = $rows[0]
    foreach ($k in 'path', 'offset', 'length') { if (-not ($r0.PSObject.Properties.Name -contains $k)) { throw "trace row has no '$k': bench.replay cannot read it" } }
    $traceBytes = [int64]($rows | Measure-Object -Property length -Sum).Sum
    $rows = $null
    $summary.trace = [ordered]@{ file = $traceJson; file_bytes = (Get-Item -LiteralPath $traceJson).Length; events = [int64]$union.union_events
                                 read_bytes = $traceBytes; events_lost = $lost; live_line = $liveLine
                                 format = 'list of {path,offset,length,cost}: read as is by bench.replay (python -X utf8)' }
    Write-Host "trace: $($union.union_events) events, $traceBytes bytes, events_lost=$lost"
    if ($lost -gt 0) { Write-Warning "events_lost=$lost : some reads may be missing from the trace" }

    Write-Host '== 2. plan (zone sized from the hot bytes)'
    $p1 = New-Plan $MaxZoneMiB
    $hot = [int64]$p1.stats.hot_bytes
    $summary.hot_bytes = $hot; $summary.plan_stats_max_zone = $p1.stats
    $willApply = -not $SkipApply
    if ($hot -le 0) { throw 'plan has no hot bytes' }
    if ($hot -gt ($MaxZoneMiB * $Mib) -or [int64]$p1.stats.overflow_bytes -gt 0) {
        $summary.stopped = 'hot_set_too_large'
        Write-Warning "핫 집합 과대: hot_bytes=$hot > $MaxZoneMiB MiB. Apply is skipped; only the before measurement runs."
        $willApply = $false
        $script:zoneMiB = $MaxZoneMiB
    } else {
        $script:zoneMiB = [int][math]::Min([math]::Ceiling($hot * 1.1 / (16 * $Mib)) * 16, $MaxZoneMiB)
        $p2 = New-Plan $script:zoneMiB
        $summary.plan_stats = $p2.stats
    }
    $summary.zone_mib = $script:zoneMiB
    $summary.apply_skipped = -not $willApply

    $pj = Get-Content -LiteralPath $plan -Raw -Encoding UTF8 | ConvertFrom-Json
    $prefix = "$Root\"
    $paths = @(@($pj.tasks | ForEach-Object { $_.path }) + @($pj.order | ForEach-Object { $_.path }) + @($pj.fingerprints.PSObject.Properties.Name)) | Sort-Object -Unique
    if ($paths.Count -eq 0) { throw 'plan has no paths' }
    $bad = @($paths | Where-Object { -not $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $_.Contains('..') })
    if ($bad.Count -gt 0) { throw "plan targets files outside ${prefix}: $(($bad | Select-Object -First 5) -join ', ')" }
    if (-not "$($pj.origin_root)".Equals($Root, [StringComparison]::OrdinalIgnoreCase)) { throw "plan origin_root '$($pj.origin_root)' != $Root" }
    $summary.plan_paths_checked = $paths.Count
    $pj = $null
    $before = $null
    if ($willApply) {
        Write-Host "== 2b. SHA-256 of the $($paths.Count) planned files (before)"
        $before = Get-Manifest $paths
        $before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'manifest_before.json') -Encoding UTF8
    }

    Write-Host "== 3. before relayout: cold replay x$Runs (trace.json)"
    $summary.before_direct = Measure-Cold 'before-direct' $false
    $summary.before_median_s = $summary.before_direct.median_total_s

    if ($willApply) {
        Write-Host '== 4. probe, dry-run, apply, verify'
        Save-Readme  # the undo line exists before anything moves
        Assert-GameClosed 'probe'
        $probe = Invoke-Tool 'probe' $AsterExe (@('probe', '--volume', "${letter}:", '--plan', $plan, '--zone-mib', "$($script:zoneMiB)") + $labelArgs) | ConvertFrom-Json
        if (-not $probe.free_match) { throw "bitmap free clusters ($($probe.free_clusters_bitmap)) != NTFS FreeClusters ($($probe.free_clusters_ntfs)): stop" }
        if ($null -eq $probe.zone_candidate_lcn) { throw 'no contiguous free zone candidate on the volume' }
        $summary.probe = $probe
        $dry = Invoke-Tool 'apply-dry-run' $AsterExe (@('apply', '--plan', $plan, '--journal', $journal, '--dry-run') + $common) | ConvertFrom-Json
        $summary.dry_run = $dry
        if ($dry.tasks_todo -le 0) { throw 'dry-run found nothing to move' }
        if (@($dry.skipped_files.PSObject.Properties).Count -gt 0) { throw "dry-run skipped files (nothing moved): $($dry.skipped_files | ConvertTo-Json -Compress)" }

        Assert-GameClosed 'apply --execute'
        $applySw = [System.Diagnostics.Stopwatch]::StartNew()
        $applied = Invoke-Tool 'apply' $AsterExe (@('apply', '--plan', $plan, '--journal', $journal, '--execute') + $common) | ConvertFrom-Json
        $applySw.Stop()
        $summary.apply = $applied
        $summary.apply_seconds = [math]::Round($applySw.Elapsed.TotalSeconds, 3)
        $summary.moved_bytes = [int64]$applied.moved_bytes
        if ($applySw.Elapsed.TotalSeconds -gt 0) { $summary.apply_mb_s = [math]::Round(($applied.moved_bytes / 1MB) / $applySw.Elapsed.TotalSeconds, 2) }
        if (@($applied.failed).Count -ne 0) { throw "apply reported failed tasks: $($applied.failed | ConvertTo-Json -Compress). Revert command is in README.txt" }
        if ($null -ne $applied.stopped) { throw "apply stopped early: $($applied.stopped). Revert command is in README.txt" }
        if ($applied.PSObject.Properties['sha_mismatch'] -and @($applied.sha_mismatch).Count -ne 0) { throw "SHA-256 sample mismatch: $($applied.sha_mismatch -join ', '). Revert command is in README.txt" }
        if ($applied.moved_bytes -ne $dry.bytes_todo) { throw "moved $($applied.moved_bytes) bytes, dry-run promised $($dry.bytes_todo). Revert command is in README.txt" }
        $verify = Invoke-Tool 'verify' $AsterExe @('verify', '--plan', $plan) | ConvertFrom-Json
        $summary.verify = $verify
        if ($verify.verdict -ne 'contiguous') { throw "verify verdict is '$($verify.verdict)'. Revert command is in README.txt" }

        Write-Host '== 4b. purge, then SHA-256 of the planned files (after) must be identical'
        Invoke-Purge 'manifest-cold'
        $after = Get-Manifest $paths
        $after | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'manifest_after.json') -Encoding UTF8
        $same = (($before | ConvertTo-Json -Compress) -eq ($after | ConvertTo-Json -Compress))
        $summary.manifest_identical = $same
        if (-not $same) { throw 'SHA-256 manifest differs after relayout: stop. Revert command is in README.txt' }

        Write-Host "== 5. after relayout: cold replay x$Runs, then cold prefetch + replay x$Runs"
        $summary.after_direct = Measure-Cold 'after-direct' $false
        $summary.after_prefetch = Measure-Cold 'after-prefetch' $true
        $summary.after_median_s = $summary.after_direct.median_total_s
        $summary.after_prefetch_median_s = $summary.after_prefetch.median_total_s
    }
    Write-Host "Saved: $(Join-Path $out 'summary.json')"
} catch {
    $summary.error = "$_"
    $exitCode = 1
    Write-Host "FAILED: $_" -ForegroundColor Red
} finally {
    Pop-Location
    if ($live -and -not $live.HasExited) {
        # Let the trace session close cleanly before giving up on it.
        if (-not (Test-Path -LiteralPath $stopFile)) { New-Item -ItemType File -Path $stopFile -ErrorAction SilentlyContinue | Out-Null }
        if (-not $live.WaitForExit(30000)) { Stop-Process -Id $live.Id -Force -ErrorAction SilentlyContinue }
    }
    Save-Readme
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding UTF8
}
Write-Host "Results: $out"
exit $exitCode
