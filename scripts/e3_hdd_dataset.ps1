<#
.SYNOPSIS
  E3: relayout + prefetch on the REAL D:\AsterBench dataset (64 files, 2.1 GiB, 4,000-event trace) on the D: HDD.

.DESCRIPTION
  Run ONCE from an elevated (Administrator) PowerShell:
    powershell -NoProfile -ExecutionPolicy Bypass -File D:\Git\AsterCache\scripts\e3_hdd_dataset.ps1

  No virtual disk and no new dataset: D:\AsterBench\dataset_meta.json and trace.json must already exist.
  Every cold run purges the standby list (bench.cache_control); if the purge fails the script stops.
  Order: A before/direct x3 -> B before/prefetch+replay x3 -> SHA-256 manifest -> plan -> every plan path must
  start with D:\AsterBench\ -> probe -> apply --dry-run -> apply --execute -> verify -> purge + manifest again
  (must be identical) -> C after/direct x3 -> D after/prefetch+replay x3 -> E SSD baseline C:\AsterBench x3
  (only if it exists) -> summary.json + README.txt in results\relayout_e3\<yyyyMMdd_HHmmss>.

  Relayout only moves data clusters of the dataset files inside D: (FSCTL_MOVE_FILE). No file outside
  D:\AsterBench is a target. The undo command is written to README.txt and is NOT run.

  -SkipRelayout: the dataset is already relocated. Only C (direct), D (prefetch then replay), F (concurrent: stream and
  replay start together, wall clock) and E (SSD) are measured, with ONE manifest hash that is compared with the newest
  earlier manifest_after.json. Nothing is moved. Also the way to re-measure persistence after some time.
#>
[CmdletBinding()]
param(
    [string]$Root = 'D:\AsterBench',
    [string]$SsdRoot = 'C:\AsterBench',
    [int]$ZoneMiB = 300,
    [string]$Python = 'python',
    [string]$Repo = '',
    [switch]$SkipRelayout
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'not_admin: start PowerShell with "Run as administrator" and run this script again.'
}
$Root = $Root.TrimEnd('\')
if ($Root -notmatch '^[A-Za-z]:\\[^\\]+$') { throw "Root must look like X:\Folder, got '$Root'" }
$letter = $Root.Substring(0, 1)
foreach ($n in 'dataset_meta.json', 'trace.json') {
    if (-not (Test-Path -LiteralPath (Join-Path $Root $n))) { throw "missing $Root\$n : this script never creates the dataset" }
}
if (-not (Test-Path -LiteralPath (Join-Path $Root 'data'))) { throw "missing $Root\data" }

# Windows PowerShell 5.1 leaves $PSScriptRoot empty while param defaults are evaluated.
if (-not $Repo) { $Repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$out = Join-Path $Repo "results\relayout_e3\$stamp"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$script:purges = 0

function Invoke-Py([string]$Name, [string[]]$PyArgs) {
    $ErrorActionPreference = 'Continue'  # PS 5.1 turns native stderr into a terminating error under 'Stop'
    Write-Host ">> python $($PyArgs -join ' ')"
    $text = (& $Python @PyArgs 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "step '$Name' failed (exit $LASTEXITCODE):`n$text" }
    Set-Content -LiteralPath (Join-Path $out "$Name.txt") -Value $text -Encoding UTF8
    return $text
}

function Invoke-Cold([string]$Name) {
    $t = Invoke-Py $Name @('-m', 'bench.cache_control')
    if ($t -notmatch "'cold_method': 'standby_purge'") { throw "standby purge failed, cold cache not achieved: $t" }
    $script:purges++
}

function Get-Median($Values) { return @($Values | Sort-Object)[1] }

# 3 cold runs. Prefetch (if asked) runs after the purge and before the replay; both times are kept apart.
function Measure-Scenario([string]$Dir, [string]$Tag, [bool]$Prefetch) {
    $trace = Join-Path $Dir 'trace.json'
    $runs = @()
    for ($i = 1; $i -le 3; $i++) {
        Invoke-Cold "$Tag-cold$i"
        $pfS = 0.0; $pfB = 0
        if ($Prefetch) {
            $pf = Invoke-Py "$Tag-prefetch$i" @('-m', 'prefetch.cli', '--trace', $trace, '--origin-root', $Dir,
                                                '--strategy', 'physical', '--execute') | ConvertFrom-Json
            if ($pf.errors -ne 0 -or $pf.bytes -le 0) { throw "prefetch read errors=$($pf.errors) bytes=$($pf.bytes)" }
            $pfS = [double]$pf.seconds; $pfB = [int64]$pf.bytes
        }
        $r = Invoke-Py "$Tag-replay$i" @('-m', 'bench.replay', '--trace', $trace) | ConvertFrom-Json
        $runs += [pscustomobject]@{ prefetch_s = $pfS; prefetch_bytes = $pfB; replay_s = [double]$r.total_s; total_s = $pfS + [double]$r.total_s }
    }
    return [ordered]@{
        dir = $Dir; prefetch = $Prefetch; runs = $runs
        median_total_s = Get-Median ($runs | ForEach-Object { $_.total_s })
        median_prefetch_s = Get-Median ($runs | ForEach-Object { $_.prefetch_s })
        median_replay_s = Get-Median ($runs | ForEach-Object { $_.replay_s })
    }
}

function Get-Manifest([string]$Dir) {
    $m = [ordered]@{}
    Get-ChildItem -LiteralPath (Join-Path $Dir 'data') -File | Sort-Object Name | ForEach-Object {
        $m[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    return $m
}

function Get-Ratio($A, $B) {
    if ($null -ne $A -and $null -ne $B -and $B -gt 0) { return [math]::Round($A / $B, 3) }
    return $null
}

Push-Location $Repo
try {
    Write-Host '== 0. python, volume'
    $pv = [int]((& $Python -c 'import sys; print(sys.version_info[0]*100+sys.version_info[1])') | Out-String).Trim()
    (& $Python -c 'import sys; print(sys.executable, sys.version)') | Out-File -LiteralPath (Join-Path $out 'python.txt') -Encoding UTF8
    if ($pv -lt 309) { throw "Python 3.9+ required (found $pv)" }
    $vol = Get-Volume -DriveLetter $letter
    $label = "$($vol.FileSystemLabel)"
    if ($vol.FileSystem -ne 'NTFS') { throw "${letter}: is $($vol.FileSystem), NTFS required" }
    $diskModel = $null; $busType = $null
    try { $d = Get-Partition -DriveLetter $letter | Get-Disk; $diskModel = "$($d.FriendlyName)"; $busType = "$($d.BusType)" } catch { $diskModel = "unknown: $_" }
    $common = @('--allowed-root', $Root)
    if ($label) { $common += @('--require-volume-label', $label) }
    Write-Host "volume ${letter}: label='$label' disk='$diskModel' bus=$busType"

    if (-not $SkipRelayout) {
        Write-Host '== A. before relayout, cold, direct replay x3'
        $A = Measure-Scenario $Root 'A-before-direct' $false
        Write-Host '== B. before relayout, cold, prefetch + replay x3'
        $B = Measure-Scenario $Root 'B-before-prefetch' $true

        Write-Host '== manifest before (SHA-256 of every data file)'
        $before = Get-Manifest $Root
        $before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'manifest_before.json') -Encoding UTF8

        Write-Host '== plan'
        $plan = Join-Path $out 'plan.json'
        Invoke-Py 'plan' @('-m', 'relayout.cli', 'plan', '--trace', (Join-Path $Root 'trace.json'), '--origin-root', $Root,
                           '--zone-mib', $ZoneMiB, '--out', $plan) | Out-Null
        Invoke-Py 'verify-before' @('-m', 'relayout.cli', 'verify', '--plan', $plan) | Out-Null

        Write-Host "== plan path check: every path must start with $Root\"
        $pj = Get-Content -LiteralPath $plan -Raw -Encoding UTF8 | ConvertFrom-Json
        $prefix = "$Root\"
        $paths = @($pj.tasks | ForEach-Object { $_.path }) + @($pj.order | ForEach-Object { $_.path }) + @($pj.fingerprints.PSObject.Properties.Name)
        if ($paths.Count -eq 0) { throw 'plan has no paths' }
        $bad = @($paths | Where-Object { -not $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $_.Contains('..') })
        if ($bad.Count -gt 0) { throw "plan targets files outside ${prefix}: $(($bad | Select-Object -First 5) -join ', ')" }
        if (-not "$($pj.origin_root)".Equals($Root, [StringComparison]::OrdinalIgnoreCase)) { throw "plan origin_root '$($pj.origin_root)' != $Root" }
        $pathCount = $paths.Count

        Write-Host '== probe (read-only)'
        $probeArgs = @('-m', 'relayout.cli', 'probe', '--volume', "${letter}:", '--plan', $plan)
        if ($label) { $probeArgs += @('--require-volume-label', $label) }
        $probe = Invoke-Py 'probe' $probeArgs | ConvertFrom-Json
        if (-not $probe.free_match) { throw "bitmap free clusters ($($probe.free_clusters_bitmap)) != NTFS FreeClusters ($($probe.free_clusters_ntfs)): stop, the bitmap decoding is suspect" }
        if ($null -eq $probe.zone_candidate_lcn) { throw 'no contiguous free zone candidate on the volume' }

        Write-Host '== apply --dry-run (fixes the zone in the plan, moves nothing)'
        $journal = Join-Path $out 'journal.jsonl'
        # written before anything moves, so the undo line exists even if a later step fails
        @(
            "E3 result folder: $out",
            '',
            "Undo (not run by the script; run from an elevated PowerShell in $Repo):",
            "python -m relayout.cli revert --journal `"$journal`" --allowed-root $Root --execute",
            '',
            "The relayout only moved the dataset files' data clusters inside ${letter}: , the file contents are identical",
            '(SHA-256 manifest compared). The data is safe without undoing; undo only restores the old physical layout.'
        ) | Set-Content -LiteralPath (Join-Path $out 'README.txt') -Encoding UTF8
        $dry = Invoke-Py 'apply-dry-run' (@('-m', 'relayout.cli', 'apply', '--plan', $plan, '--journal', $journal, '--dry-run') + $common) | ConvertFrom-Json
        if ($dry.tasks_todo -le 0) { throw 'dry-run found nothing to move' }
        if (@($dry.skipped_files.PSObject.Properties).Count -gt 0) { throw "dry-run skipped files: $($dry.skipped_files | ConvertTo-Json -Compress)" }

        Write-Host '== apply --execute'
        $applied = Invoke-Py 'apply' (@('-m', 'relayout.cli', 'apply', '--plan', $plan, '--journal', $journal, '--execute') + $common) | ConvertFrom-Json
        if (@($applied.failed).Count -ne 0) { throw "apply reported failed tasks: $($applied.failed | ConvertTo-Json -Compress)" }
        if ($null -ne $applied.stopped) { throw "apply stopped early: $($applied.stopped)" }
        if (@($applied.sha_mismatch).Count -ne 0) { throw "SHA-256 sample mismatch: $($applied.sha_mismatch -join ', ')" }
        if ($applied.moved_bytes -ne $dry.bytes_todo) { throw "moved $($applied.moved_bytes) bytes, dry-run promised $($dry.bytes_todo)" }

        Write-Host '== verify'
        $verify = Invoke-Py 'verify-after' @('-m', 'relayout.cli', 'verify', '--plan', $plan) | ConvertFrom-Json
        if ($verify.verdict -ne 'contiguous') { throw "verify verdict is '$($verify.verdict)': $($verify | ConvertTo-Json -Compress)" }

        Write-Host '== purge, then manifest after (must be identical)'
        Invoke-Cold 'manifest-cold'
        $after = Get-Manifest $Root
        $same = (($before | ConvertTo-Json -Compress) -eq ($after | ConvertTo-Json -Compress))
        $after | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'manifest_after.json') -Encoding UTF8
        if (-not $same) { throw 'SHA-256 manifest differs after relayout: stop. Revert command is in README.txt' }
    } else {
        $A = $null; $B = $null; $probe = $null; $dry = $null; $applied = $null; $verify = $null; $pathCount = 0
        Write-Host '== SkipRelayout: purge, then manifest once (compared with the newest earlier manifest_after.json)'
        Invoke-Cold 'manifest-cold'
        $after = Get-Manifest $Root
        $after | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'manifest_skip.json') -Encoding UTF8
        $prev = Get-ChildItem -LiteralPath (Join-Path $Repo 'results\relayout_e3') -Filter 'manifest_after.json' -Recurse -File |
            Where-Object { $_.DirectoryName -ne $out } | Sort-Object LastWriteTime | Select-Object -Last 1
        $same = $null
        if ($prev) {
            $old = Get-Content -LiteralPath $prev.FullName -Raw -Encoding UTF8 | ConvertFrom-Json | ConvertTo-Json -Compress
            $same = ($old -eq ($after | ConvertTo-Json -Compress))
            if (-not $same) { throw "SHA-256 manifest differs from $($prev.FullName): the data changed" }
        } else { Write-Warning 'no earlier manifest_after.json: integrity not compared' }
        "SkipRelayout run, nothing moved. Earlier manifest compared: $($prev.FullName)" | Set-Content -LiteralPath (Join-Path $out 'README.txt') -Encoding UTF8
    }

    Write-Host '== C. after relayout, cold, direct replay x3'
    $C = Measure-Scenario $Root 'C-after-direct' $false
    Write-Host '== D. after relayout, cold, prefetch + replay x3'
    $D = Measure-Scenario $Root 'D-after-prefetch' $true

    Write-Host '== F. after relayout, cold, concurrent (stream and replay start together), wall clock'
    $fJson = Join-Path $out 'F-concurrent.json'
    Invoke-Py 'F-concurrent' @('-m', 'bench.concurrent_run', '--root', $Root, '--repeat', '3', '--think-us', '0', '250', '1000', '--out', $fJson) | Out-Null
    $F = Get-Content -LiteralPath $fJson -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($sr in $F.series.PSObject.Properties) { foreach ($r in $sr.Value.runs) { if ($r.cold_method -ne 'standby_purge') { throw "concurrent run was not purged: $($r.cold_method)" } } }

    $E = $null
    if ((Test-Path -LiteralPath (Join-Path $SsdRoot 'trace.json')) -and (Test-Path -LiteralPath (Join-Path $SsdRoot 'data'))) {
        Write-Host "== E. SSD baseline $SsdRoot, cold, direct replay x3"
        $E = Measure-Scenario $SsdRoot 'E-ssd-direct' $false
    } else { Write-Warning "no SSD mirror at $SsdRoot : scenario E skipped" }

    $eMed = $null
    if ($E) { $eMed = $E.median_total_s }
    $summary = [ordered]@{
        root = $Root; volume = "${letter}:"; label = $label; label_enforced = [bool]$label; disk_model = $diskModel; bus_type = $busType
        zone_mib = $ZoneMiB; purge_ok = $true; purge_runs = $script:purges; plan_paths_checked = $pathCount
        A_before_direct = $A; B_before_prefetch = $B; C_after_direct = $C; D_after_prefetch = $D; F_concurrent = $F; E_ssd_direct = $E; skip_relayout = [bool]$SkipRelayout
        ratios = [ordered]@{
            D_total_over_E = Get-Ratio $D.median_total_s $eMed
            F_wall_over_E = Get-Ratio $F.series.'0'.median_wall_s $eMed
            C_over_E = Get-Ratio $C.median_total_s $eMed
            A_over_E = Get-Ratio $(if ($A) { $A.median_total_s } else { $null }) $eMed
            D_total_over_A = Get-Ratio $D.median_total_s $(if ($A) { $A.median_total_s } else { $null })
        }
        moved_bytes = $(if ($applied) { $applied.moved_bytes }); tasks_total = $(if ($applied) { $applied.tasks_total }); tasks_todo = $(if ($applied) { $applied.tasks_todo })
        probe = $probe; dry_run = $dry; apply = $applied; verify = $verify
        manifest_identical = $same
        note = 'Times are medians of 3 cold runs (standby purge). Prefetch+replay total = prefetch seconds + replay total_s. F is the wall clock of stream and replay started together (series key = per-request think microseconds).'
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding UTF8

    Write-Host "Saved: $out\summary.json"
    Write-Host ($summary.ratios | ConvertTo-Json)
} finally {
    Pop-Location
}
