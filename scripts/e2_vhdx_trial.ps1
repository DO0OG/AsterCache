<#
.SYNOPSIS
  E2: run the relayout prototype end to end on a throw-away 8 GiB dynamic VHDX (never on a real data volume).

.DESCRIPTION
  Run ONCE from an elevated (Administrator) PowerShell:
    powershell -NoProfile -ExecutionPolicy Bypass -File D:\Git\AsterCache\scripts\e2_vhdx_trial.ps1

  Steps (each failure stops the script): create VHDX -> attach -> NTFS -> drive letter -> dataset + trace ->
  SHA-256 manifest -> cold replay x3 (before) -> relayout plan -> apply --execute -> verify -> manifest again ->
  cold replay x3 (after) -> save results to results\relayout_e2 -> detach the VHDX (the file is NOT deleted).

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
    [string]$Repo = (Split-Path -Parent $PSScriptRoot)
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'not_admin: start PowerShell with "Run as administrator" and run this script again.'
}
if (Test-Path -LiteralPath $VhdxPath) { throw "VHDX already exists, refusing to reuse or overwrite: $VhdxPath" }

$out = Join-Path $Repo 'results\relayout_e2'
New-Item -ItemType Directory -Force -Path $out | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $VhdxPath) | Out-Null
$attached = $false

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
    $used = (Get-PSDrive -PSProvider FileSystem).Name
    $letter = ('Z','Y','X','W','V','U','T','S','R') | Where-Object { $used -notcontains $_ } | Select-Object -First 1
    if (-not $letter) { throw 'no free drive letter between R and Z' }

    Write-Host "== 1. create, attach, format ($VhdxPath -> ${letter}:)"
    Invoke-DiskPart @(
        "create vdisk file=`"$VhdxPath`" maximum=$($SizeGiB * 1024) type=expandable",
        "select vdisk file=`"$VhdxPath`"", 'attach vdisk',
        'create partition primary', 'format fs=ntfs unit=4096 quick label=AsterTrial', "assign letter=$letter"
    ) | Out-Null
    $attached = $true
    $root = "${letter}:\AsterBench"
    if (-not (Test-Path -LiteralPath "${letter}:\")) { throw "volume ${letter}: did not appear" }

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

    Write-Host '== 5. apply --execute'
    $journal = Join-Path $out 'journal.jsonl'
    Invoke-Py 'apply' @('-m', 'relayout.cli', 'apply', '--plan', $plan, '--journal', $journal, '--execute') | Out-Null

    Write-Host '== 6. verify'
    $verify = Invoke-Py 'verify-after' @('-m', 'relayout.cli', 'verify', '--plan', $plan) | ConvertFrom-Json
    $after = Get-Manifest $root
    $same = (($before | ConvertTo-Json -Compress) -eq ($after | ConvertTo-Json -Compress))
    $after | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'manifest_after.json') -Encoding UTF8
    if (-not $same) { throw 'SHA-256 manifest differs after relayout: stop and keep the VHDX for inspection' }

    Write-Host '== 7. replay after'
    $tAfter = Measure-ColdReplay $root 'after'

    $summary = [ordered]@{
        vhdx = $VhdxPath; drive = "${letter}:"; zone_mib = $ZoneMiB; files = $Files
        manifest_identical = $same; verify = $verify
        replay_before = $tBefore; replay_after = $tAfter
        note = 'VHDX on C: (SSD) unless -VhdxPath points at the HDD: timing is not HDD evidence.'
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding UTF8
    Write-Host "Saved: $out\summary.json"
    Write-Host ($summary | ConvertTo-Json -Depth 6)
} finally {
    Pop-Location
    if ($attached) {
        Write-Host '== detach VHDX (file is kept)'
        try { Invoke-DiskPart @("select vdisk file=`"$VhdxPath`"", 'detach vdisk') | Out-Null }
        catch { Write-Warning "detach failed, run: diskpart  select vdisk file=`"$VhdxPath`"  detach vdisk`n$_" }
    }
}
