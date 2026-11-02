<#
.SYNOPSIS
  S1 (C#): relayout trial on a throw-away VHDX with the C# CLI (plan, probe, apply, verify, revert).

.DESCRIPTION
  Run ONCE from an elevated (Administrator) PowerShell, by the user:
    powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\csharp\scripts\cs_e2_vhdx.ps1

  Dataset and replay timing still use the Python reference (bench.dataset, bench.replay, bench.cache_control).
  Every relayout command is the C# CLI (aster.exe). Each failure stops the script.
  Interrupt test: apply --execute is started and killed 5 times at random delays, then resumed from the journal.
  One forced detach (VHDX detached with the journal still open) happens inside the 3rd interruption cycle.
  Revert runs at the end and the manifest must return to the pre-move hashes.
  Results: <repo>\results\csharp_<yyyyMMdd_HHmmss>\ ; undo commands are written to README.txt (not run).

  CLI argument names mirror relayout/cli.py. CLI 완성 후 재확인 (C# CLI still being written).
#>
[CmdletBinding()]
param(
    [string]$VhdxPath = 'C:\AsterVHDX\cs_trial.vhdx',
    [int]$SizeGiB = 8,
    [int]$Files = 16,
    [int]$ZoneMiB = 300,
    [int]$InterruptRuns = 5,
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
if (Test-Path -LiteralPath $VhdxPath) { throw "VHDX already exists, refusing to reuse or overwrite: $VhdxPath" }

$Label = 'AsterCsTrial'
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$out = Join-Path $Repo "results\csharp_$stamp"
New-Item -ItemType Directory -Force -Path $out, (Split-Path -Parent $VhdxPath) | Out-Null
$attached = $false
$letter = $null
$root = $null
$journal = Join-Path $out 'journal.jsonl'
$plan = Join-Path $out 'plan.json'

function Invoke-DiskPart([string[]]$Lines) {
    $f = Join-Path $env:TEMP ('aster_dp_{0}.txt' -f [guid]::NewGuid().ToString('N'))
    try {
        Set-Content -LiteralPath $f -Value $Lines -Encoding ASCII
        $text = (& diskpart.exe /s $f 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0 -or $text -match 'DiskPart has encountered an error') { throw "diskpart failed:`n$text" }
        return $text
    } finally { Remove-Item -LiteralPath $f -ErrorAction SilentlyContinue }
}

function Invoke-Py([string]$Name, [string[]]$PyArgs) {
    $text = (& $Python @PyArgs 2>&1 | Out-String)
    $code = $LASTEXITCODE
    Set-Content -LiteralPath (Join-Path $out "$Name.txt") -Value $text -Encoding UTF8
    if ($code -ne 0) { throw "step '$Name' failed (exit $code):`n$text" }
    return $text
}

function Invoke-Aster([string]$Name, [string[]]$AsterArgs) {
    $text = (& $AsterExe @AsterArgs 2>&1 | Out-String)
    $code = $LASTEXITCODE
    Set-Content -LiteralPath (Join-Path $out "$Name.txt") -Value $text -Encoding UTF8
    if ($code -ne 0) { throw "aster $Name failed (exit $code; 3 = not admin, 2 = bad arguments):`n$text" }
    return $text
}

# Starts the CLI without waiting, so the run can be killed in the middle of a move.
function Start-AsterBackground([string[]]$AsterArgs) {
    $line = ($AsterArgs | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    return Start-Process -FilePath $AsterExe -ArgumentList $line -PassThru -WindowStyle Hidden
}

function Wait-Drive {
    for ($i = 0; $i -lt 40; $i++) {
        if (Test-Path -LiteralPath "${letter}:\") { return }
        Start-Sleep -Milliseconds 500
    }
    throw "volume ${letter}: did not appear"
}

# The volume must be the file-backed virtual disk of THIS vhdx, with the expected label. Anything else: stop.
function Assert-TrialVolume {
    Wait-Drive
    $part = Get-Partition -DriveLetter $letter
    $disk = Get-Disk -Number $part.DiskNumber
    if ("$($disk.BusType)" -notmatch 'File\s*Backed\s*Virtual') { throw "${letter}: is not a file-backed virtual disk (BusType=$($disk.BusType))" }
    if ($disk.Location -and ($disk.Location -ne $VhdxPath)) { throw "${letter}: belongs to '$($disk.Location)', not '$VhdxPath'" }
    $vol = Get-Volume -DriveLetter $letter
    if ($vol.FileSystemLabel -ne $Label -or $vol.FileSystem -ne 'NTFS') { throw "${letter}: label/fs is '$($vol.FileSystemLabel)'/'$($vol.FileSystem)'" }
}

function Mount-Trial {
    Invoke-DiskPart @("select vdisk file=`"$VhdxPath`"", 'attach vdisk') | Out-Null
    $script:attached = $true
    try { Wait-Drive } catch {
        Invoke-DiskPart @("select vdisk file=`"$VhdxPath`"", 'select partition 1', "assign letter=$letter") | Out-Null
        Wait-Drive
    }
    Assert-TrialVolume
}

function Dismount-Trial {
    Invoke-DiskPart @("select vdisk file=`"$VhdxPath`"", 'detach vdisk') | Out-Null
    $script:attached = $false
}

function Get-Manifest([string]$Dir) {
    $m = [ordered]@{}
    Get-ChildItem -LiteralPath (Join-Path $Dir 'data') -File | Sort-Object Name | ForEach-Object {
        $m[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    return $m
}

function Measure-ColdReplay([string]$Dir, [string]$Tag) {
    $times = @()
    for ($i = 1; $i -le 3; $i++) {
        $cold = Invoke-Py "$Tag-cold$i" @('-m', 'bench.cache_control')
        if ($cold -notmatch 'standby_purge') { throw "standby purge did not confirm ($Tag): stop" }
        $r = Invoke-Py "$Tag-replay$i" @('-m', 'bench.replay', '--trace', (Join-Path $Dir 'trace.json')) | ConvertFrom-Json
        $times += [double]$r.total_s
    }
    $sorted = $times | Sort-Object
    return [ordered]@{ runs_s = $times; median_s = $sorted[1] }
}

$summary = [ordered]@{ vhdx = $VhdxPath; label = $Label; zone_mib = $ZoneMiB; files = $Files; repo = $Repo }
Push-Location $Repo
try {
    Write-Host '== 0. python and CLI'
    $pv = [int]((& $Python -c 'import sys; print(sys.version_info[0]*100+sys.version_info[1])') | Out-String).Trim()
    if ($pv -lt 309) { throw "Python 3.9+ required (found $pv)" }

    Write-Host '== 1. create, attach, format'
    $used = (Get-PSDrive -PSProvider FileSystem).Name
    $letter = ('Z','Y','X','W','V','U','T','S','R') | Where-Object { $used -notcontains $_ } | Select-Object -First 1
    if (-not $letter) { throw 'no free drive letter between R and Z' }
    $root = "${letter}:\AsterBench"
    $script:attached = $true  # set before diskpart: a partial success is detached in finally
    Invoke-DiskPart @(
        "create vdisk file=`"$VhdxPath`" maximum=$($SizeGiB * 1024) type=expandable",
        "select vdisk file=`"$VhdxPath`"", 'attach vdisk',
        'create partition primary', "format fs=ntfs unit=4096 quick label=$Label", "assign letter=$letter"
    ) | Out-Null
    Assert-TrialVolume
    $common = @('--allowed-root', $root, '--require-volume-label', $Label)

    Write-Host '== 2. dataset (Python reference) and manifest'
    Invoke-Py 'dataset' @('-m', 'bench.dataset', '--root', $root, '--files', $Files, '--min-mib', 16, '--max-mib', 48) | Out-Null
    $before = Get-Manifest $root
    $before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'manifest_before.json') -Encoding UTF8
    $tBefore = Measure-ColdReplay $root 'before'
    $summary.replay_before = $tBefore

    Write-Host '== 3. plan, probe, dry-run'
    Invoke-Aster 'plan' @('plan', '--trace', (Join-Path $root 'trace.json'), '--origin-root', $root,
                          '--zone-mib', "$ZoneMiB", '--out', $plan) | Out-Null
    $probe = Invoke-Aster 'probe' @('probe', '--volume', "${letter}:", '--plan', $plan, '--zone-mib', "$ZoneMiB",
                                    '--require-volume-label', $Label) | ConvertFrom-Json
    if (-not $probe.free_match) { throw "bitmap free clusters ($($probe.free_clusters_bitmap)) != NTFS FreeClusters ($($probe.free_clusters_ntfs)): stop" }
    if ($null -eq $probe.zone_candidate_lcn) { throw 'no free zone candidate on the volume' }
    $dry = Invoke-Aster 'apply-dry-run' (@('apply', '--plan', $plan, '--journal', $journal, '--dry-run') + $common) | ConvertFrom-Json
    if ($dry.tasks_todo -le 0) { throw 'dry-run found nothing to move' }
    $summary.probe = $probe; $summary.dry_run = $dry

    Write-Host "== 4. apply --execute: kill $InterruptRuns times, resume from the journal"
    $applyArgs = @('apply', '--plan', $plan, '--journal', $journal, '--execute') + $common
    $resumed = 0
    for ($k = 1; $k -le $InterruptRuns; $k++) {
        $p = Start-AsterBackground $applyArgs
        Start-Sleep -Milliseconds (Get-Random -Minimum 2000 -Maximum 6000)
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force; $p.WaitForExit() }
        $killed = $p.HasExited -and ($p.ExitCode -ne 0)
        if (-not $killed) { throw "interrupt ${k}: the run finished before the kill (no interruption was tested)" }
        if ($k -eq 3) {
            Write-Host '== 4b. forced detach while the journal is open, then re-attach'
            Dismount-Trial
            Mount-Trial
        }
        $resumed++
    }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $applied = Invoke-Aster 'apply-final' $applyArgs | ConvertFrom-Json
    $sw.Stop()
    if (@($applied.failed).Count -ne 0) { throw "apply reported failed tasks: $($applied.failed | ConvertTo-Json -Compress)" }
    if (@($applied.sha_mismatch).Count -ne 0) { throw "SHA-256 sample mismatch: $($applied.sha_mismatch -join ', ')" }
    $mbps = [math]::Round(($applied.moved_bytes / 1MB) / $sw.Elapsed.TotalSeconds, 2)
    # Not a pass/fail gate: FSCTL_MOVE_FILE on an expandable VHDX nested on NTFS runs at a few MB/s for both the C# and the
    # Python applier (Python E2 journal: 194 MB in 686 s). The number is recorded for comparison only.
    if ($mbps -lt 20) { $summary.throughput_note = "final run $mbps MB/s (below the 20 MB/s guess; compare with the Python E2 journal)" }
    $summary.interrupts = [ordered]@{ kills = $InterruptRuns; resumed = $resumed; forced_detach_at = 3 }
    $summary.apply = $applied; $summary.throughput_mb_s = $mbps

    Write-Host '== 5. verify, chkdsk (read-only scan)'
    $verify = Invoke-Aster 'verify' @('verify', '--plan', $plan) | ConvertFrom-Json
    if ($verify.verdict -ne 'contiguous') { throw "verify verdict is '$($verify.verdict)'" }
    $chk = (& chkdsk.exe "${letter}:" /scan 2>&1 | Out-String)
    Set-Content -LiteralPath (Join-Path $out 'chkdsk.txt') -Value $chk -Encoding UTF8
    if ($LASTEXITCODE -ne 0) { throw "chkdsk /scan reported errors (exit $LASTEXITCODE): see chkdsk.txt" }

    Write-Host '== 6. detach, re-attach (drops every cache), manifest must match'
    Dismount-Trial
    Mount-Trial
    $after = Get-Manifest $root
    $same = (($before | ConvertTo-Json -Compress) -eq ($after | ConvertTo-Json -Compress))
    $after | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'manifest_after.json') -Encoding UTF8
    if (-not $same) { throw 'SHA-256 manifest differs after relayout: keep the VHDX for inspection' }
    $summary.manifest_identical = $same
    $summary.replay_after = Measure-ColdReplay $root 'after'

    Write-Host '== 7. revert --execute, manifest must match the pre-move hashes'
    Invoke-Aster 'revert' (@('revert', '--journal', $journal, '--allowed-root', $root, '--require-volume-label', $Label, '--execute')) | Out-Null
    $reverted = Get-Manifest $root
    $summary.manifest_after_revert_identical = (($before | ConvertTo-Json -Compress) -eq ($reverted | ConvertTo-Json -Compress))
    if (-not $summary.manifest_after_revert_identical) { throw 'manifest differs after revert' }

    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding UTF8
    Write-Host "Saved: $(Join-Path $out 'summary.json')"
} finally {
    Pop-Location
    if ($script:attached) {
        try { Dismount-Trial } catch { Write-Warning "detach failed; run diskpart: select vdisk file=`"$VhdxPath`" then detach vdisk`n$_" }
    }
    $undo = @(
        'S1 undo (not run by the script)',
        "revert: `"$AsterExe`" revert --journal `"$journal`" --allowed-root `"$root`" --require-volume-label $Label --execute",
        "detach: diskpart, select vdisk file=`"$VhdxPath`", detach vdisk",
        "delete the throw-away disk by hand when done: $VhdxPath (the script never deletes it)"
    )
    Set-Content -LiteralPath (Join-Path $out 'README.txt') -Value $undo -Encoding UTF8
}
