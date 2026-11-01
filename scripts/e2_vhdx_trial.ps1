<#
.SYNOPSIS
  E2: run the relayout prototype end to end on a throw-away 8 GiB dynamic VHDX (never on a real data volume).

.DESCRIPTION
  Run ONCE from an elevated (Administrator) PowerShell:
    powershell -NoProfile -ExecutionPolicy Bypass -File D:\Git\AsterCache\scripts\e2_vhdx_trial.ps1

  Steps (each failure stops the script): create VHDX -> attach -> NTFS (label AsterTrial) -> drive letter ->
  check the disk is file-backed virtual and the label is right -> dataset + trace -> SHA-256 manifest ->
  cold replay x3 (before) -> relayout plan -> probe (read-only FSCTL check) -> apply --dry-run (fixes the zone,
  moves nothing) -> apply --execute -> check the apply report -> verify -> detach + re-attach the VHDX (drops every
  cache) -> SHA-256 manifest again -> cold replay x3 (after) -> save results to results\relayout_e2 -> detach
  (the VHDX file is NOT deleted).

  Every relayout command also gets --allowed-root <the VHDX folder> and --require-volume-label AsterTrial, so a
  wrong volume is refused with nothing moved.

  The default VHDX lives on C: (as specified), so the replay times show the mechanics and the overhead of the
  tool only. They are NOT evidence of the HDD speedup; for that, rerun with -VhdxPath on the D: HDD.
#>
[CmdletBinding()]
param(
    [string]$VhdxPath = 'C:\AsterVHDX\trial.vhdx',
    [int]$SizeGiB = 8,
    [int]$Files = 16,
    [int]$ZoneMiB = 300,
    [string]$Python = 'python',
    [string]$Repo = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Label = 'AsterTrial'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'not_admin: start PowerShell with "Run as administrator" and run this script again.'
}
if (Test-Path -LiteralPath $VhdxPath) { throw "VHDX already exists, refusing to reuse or overwrite: $VhdxPath" }

# Windows PowerShell 5.1 leaves $PSScriptRoot empty while param defaults are evaluated.
if (-not $Repo) { $Repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$out = Join-Path $Repo 'results\relayout_e2'
New-Item -ItemType Directory -Force -Path $out | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $VhdxPath) | Out-Null
# an earlier run's plan/journal must never be reused or silently overwritten
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
foreach ($n in 'plan.json', 'journal.jsonl') {
    $old = Join-Path $out $n
    if (Test-Path -LiteralPath $old) { Move-Item -LiteralPath $old -Destination (Join-Path $out "old_${stamp}_$n") }
}
$attached = $false
$letter = $null

function Invoke-DiskPart([string[]]$Lines) {
    $ErrorActionPreference = 'Continue'  # PS 5.1 turns native stderr into a terminating error under 'Stop'
    $f = Join-Path $env:TEMP ('aster_dp_{0}.txt' -f [guid]::NewGuid().ToString('N'))
    try {
        Set-Content -LiteralPath $f -Value $Lines -Encoding ASCII
        $text = (& diskpart.exe /s $f 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0 -or $text -match 'DiskPart has encountered an error') { throw "diskpart failed:`n$text" }
        return $text
    } finally { Remove-Item -LiteralPath $f -ErrorAction SilentlyContinue }
}

function Invoke-Py([string]$Name, [string[]]$PyArgs) {
    $ErrorActionPreference = 'Continue'
    Write-Host ">> python $($PyArgs -join ' ')"
    $text = (& $Python @PyArgs 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "step '$Name' failed (exit $LASTEXITCODE):`n$text" }
    Set-Content -LiteralPath (Join-Path $out "$Name.txt") -Value $text -Encoding UTF8
    return $text
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

function Get-Manifest([string]$Root) {
    $m = [ordered]@{}
    Get-ChildItem -LiteralPath (Join-Path $Root 'data') -File | Sort-Object Name | ForEach-Object {
        $m[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    return $m
}

function Measure-ColdReplay([string]$Root, [string]$Tag) {
    $times = @()
    for ($i = 1; $i -le 3; $i++) {
        $cold = Invoke-Py "$Tag-cold$i" @('-m', 'bench.cache_control')
        $flag = @(); if ($cold -notmatch 'standby_purge') { $flag = @('--unbuffered') }
        $r = Invoke-Py "$Tag-replay$i" (@('-m', 'bench.replay', '--trace', (Join-Path $Root 'trace.json')) + $flag) | ConvertFrom-Json
        $times += [double]$r.total_s
    }
    $sorted = $times | Sort-Object
    return [ordered]@{ runs_s = $times; median_s = $sorted[1]; purge = [bool]($cold -match 'standby_purge') }
}

Push-Location $Repo
try {
    Write-Host '== 0. python'
    $pv = [int]((& $Python -c 'import sys; print(sys.version_info[0]*100+sys.version_info[1])') | Out-String).Trim()
    (& $Python -c 'import sys; print(sys.executable, sys.version)') | Out-File -LiteralPath (Join-Path $out 'python.txt') -Encoding UTF8
    if ($pv -lt 309) { throw "Python 3.9+ required (found $pv)" }

    $used = (Get-PSDrive -PSProvider FileSystem).Name
    $letter = ('Z','Y','X','W','V','U','T','S','R') | Where-Object { $used -notcontains $_ } | Select-Object -First 1
    if (-not $letter) { throw 'no free drive letter between R and Z' }

    Write-Host "== 1. create, attach, format ($VhdxPath -> ${letter}:)"
    $attached = $true  # set before diskpart: any partial success is detached in finally
    Invoke-DiskPart @(
        "create vdisk file=`"$VhdxPath`" maximum=$($SizeGiB * 1024) type=expandable",
        "select vdisk file=`"$VhdxPath`"", 'attach vdisk',
        'create partition primary', "format fs=ntfs unit=4096 quick label=$Label", "assign letter=$letter"
    ) | Out-Null
    Assert-TrialVolume
    $root = "${letter}:\AsterBench"
    $common = @('--allowed-root', $root, '--require-volume-label', $Label)

    Write-Host '== 2. dataset + trace'
    Invoke-Py 'dataset' @('-m', 'bench.dataset', '--root', $root, '--files', $Files, '--min-mib', 16, '--max-mib', 48) | Out-Null
    $before = Get-Manifest $root
    $before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'manifest_before.json') -Encoding UTF8

    Write-Host '== 3. replay before'
    $tBefore = Measure-ColdReplay $root 'before'

    Write-Host '== 4. plan'
    $plan = Join-Path $out 'plan.json'
    Invoke-Py 'plan' @('-m', 'relayout.cli', 'plan', '--trace', (Join-Path $root 'trace.json'), '--origin-root', $root,
                       '--zone-mib', $ZoneMiB, '--out', $plan) | Out-Null
    Invoke-Py 'verify-before' @('-m', 'relayout.cli', 'verify', '--plan', $plan) | Out-Null

    Write-Host '== 5. probe (read-only FSCTL check: volume data, bitmap)'
    $probe = Invoke-Py 'probe' @('-m', 'relayout.cli', 'probe', '--volume', "${letter}:", '--plan', $plan,
                                 '--require-volume-label', $Label) | ConvertFrom-Json
    if (-not $probe.free_match) { throw "bitmap free clusters ($($probe.free_clusters_bitmap)) != NTFS FreeClusters ($($probe.free_clusters_ntfs)): stop, the bitmap decoding is suspect" }
    if ($null -eq $probe.zone_candidate_lcn) { throw 'no free zone candidate on the volume' }

    Write-Host '== 6. apply --dry-run (fixes the zone in the plan, moves nothing)'
    $journal = Join-Path $out 'journal.jsonl'
    $dry = Invoke-Py 'apply-dry-run' (@('-m', 'relayout.cli', 'apply', '--plan', $plan, '--journal', $journal, '--dry-run') + $common) | ConvertFrom-Json
    if ($dry.tasks_todo -le 0) { throw 'dry-run found nothing to move' }
    if (@($dry.skipped_files.PSObject.Properties).Count -gt 0) { throw "dry-run skipped files: $($dry.skipped_files | ConvertTo-Json -Compress)" }

    Write-Host '== 7. apply --execute'
    $applied = Invoke-Py 'apply' (@('-m', 'relayout.cli', 'apply', '--plan', $plan, '--journal', $journal, '--execute') + $common) | ConvertFrom-Json
    if (@($applied.failed).Count -ne 0) { throw "apply reported failed tasks: $($applied.failed | ConvertTo-Json -Compress)" }
    if ($null -ne $applied.stopped) { throw "apply stopped early: $($applied.stopped)" }
    if (@($applied.sha_mismatch).Count -ne 0) { throw "SHA-256 sample mismatch: $($applied.sha_mismatch -join ', ')" }
    if ($applied.moved_bytes -ne $dry.bytes_todo) { throw "moved $($applied.moved_bytes) bytes, dry-run promised $($dry.bytes_todo)" }

    Write-Host '== 8. verify'
    $verify = Invoke-Py 'verify-after' @('-m', 'relayout.cli', 'verify', '--plan', $plan) | ConvertFrom-Json
    if ($verify.verdict -ne 'contiguous') { throw "verify verdict is '$($verify.verdict)': $($verify | ConvertTo-Json -Compress)" }

    Write-Host '== 9. detach + re-attach (drops every cache), then full manifest'
    Dismount-Trial
    Mount-Trial
    $after = Get-Manifest $root
    $same = (($before | ConvertTo-Json -Compress) -eq ($after | ConvertTo-Json -Compress))
    $after | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'manifest_after.json') -Encoding UTF8
    if (-not $same) { throw 'SHA-256 manifest differs after relayout: stop and keep the VHDX for inspection' }
    $verify2 = Invoke-Py 'verify-after-remount' @('-m', 'relayout.cli', 'verify', '--plan', $plan) | ConvertFrom-Json
    if ($verify2.verdict -ne 'contiguous') { throw "verdict after re-attach is '$($verify2.verdict)'" }

    Write-Host '== 10. replay after'
    $tAfter = Measure-ColdReplay $root 'after'

    $summary = [ordered]@{
        vhdx = $VhdxPath; drive = "${letter}:"; label = $Label; zone_mib = $ZoneMiB; files = $Files
        probe = $probe; dry_run = $dry; apply = $applied; verify = $verify; verify_after_remount = $verify2
        manifest_identical = $same; replay_before = $tBefore; replay_after = $tAfter
        note = 'VHDX on C: (SSD) unless -VhdxPath points at the HDD: timing is not HDD evidence.'
    }
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding UTF8
    Write-Host "Saved: $out\summary.json"
    Write-Host ($summary | ConvertTo-Json -Depth 8)
} finally {
    Pop-Location
    if ($attached) {
        Write-Host '== detach VHDX (file is kept)'
        try { Dismount-Trial }
        catch { Write-Warning "detach failed, run: diskpart  select vdisk file=`"$VhdxPath`"  detach vdisk`n$_" }
    }
}
