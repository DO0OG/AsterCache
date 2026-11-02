<#
.SYNOPSIS
  S2 (C#): probe, plan, dry-run, verify and cold stream comparison on the REAL D:\AsterBench dataset.

.DESCRIPTION
  Run ONCE from an elevated (Administrator) PowerShell, by the user:
    powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\csharp\scripts\cs_e3_check.ps1

  Steps: C# probe vs Python probe (read-only) -> new plan -> every plan path must start with the dataset root ->
  apply --dry-run (moves nothing) -> verify -> cold stream, Python and C#, 3 runs each, for mode (a) pvm and
  mode (b) overlapped read (2 requests in flight), alternating, with a standby purge before every run ->
  SHA-256 manifest must equal the one taken before the stream runs.
  Verdict: the C# median must be <= the Python median for each mode. Verdicts are recorded, not enforced.
  Nothing is moved: no apply --execute, no revert. Only one D: measurement runs at a time.
  Results: <repo>\results\csharp_<yyyyMMdd_HHmmss>\summary.json

  CLI argument names mirror relayout/cli.py and prefetch/cli.py. CLI 완성 후 재확인 (C# CLI still being written).
#>
[CmdletBinding()]
param(
    [string]$Root = 'D:\AsterBench',
    [int]$ZoneMiB = 300,
    [int]$Runs = 3,
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
$Root = $Root.TrimEnd('\')
if ($Root -notmatch '^[A-Za-z]:\\[^\\]+$') { throw "Root must look like X:\Folder, got '$Root'" }
foreach ($n in 'dataset_meta.json', 'trace.json') {
    if (-not (Test-Path -LiteralPath (Join-Path $Root $n))) { throw "missing $Root\$n : this script never creates the dataset" }
}
if (-not (Test-Path -LiteralPath (Join-Path $Root 'data'))) { throw "missing $Root\data" }

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$out = Join-Path $Repo "results\csharp_$stamp"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$plan = Join-Path $out 'plan.json'
$journal = Join-Path $out 'journal.jsonl'
$summary = [ordered]@{ root = $Root; zone_mib = $ZoneMiB; runs = $Runs; repo = $Repo }

function Invoke-Tool([string]$Name, [string]$Exe, [string[]]$ToolArgs) {
    $text = (& $Exe @ToolArgs 2>&1 | Out-String)
    $code = $LASTEXITCODE
    Set-Content -LiteralPath (Join-Path $out "$Name.txt") -Value $text -Encoding UTF8
    if ($code -ne 0) { throw "step '$Name' failed (exit $code):`n$text" }
    return $text
}

function Invoke-Timed([string]$Name, [string]$Exe, [string[]]$ToolArgs) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Invoke-Tool $Name $Exe $ToolArgs | Out-Null
    $sw.Stop()
    return [math]::Round($sw.Elapsed.TotalSeconds, 3)
}

function Invoke-Purge([string]$Tag) {
    $cold = Invoke-Tool "$Tag-purge" $Python @('-m', 'bench.cache_control')
    if ($cold -notmatch 'standby_purge') { throw "standby purge did not confirm ($Tag): stop, the run would not be cold" }
}

function Get-Manifest([string]$Dir) {
    $m = [ordered]@{}
    Get-ChildItem -LiteralPath (Join-Path $Dir 'data') -File | Sort-Object Name | ForEach-Object {
        $m[$_.Name] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    return $m
}

# Collects every string value in a parsed JSON tree.
function Get-JsonStrings($Node) {
    if ($null -eq $Node) { return }
    if ($Node -is [string]) { $Node; return }
    if ($Node -is [System.Collections.IDictionary]) { foreach ($v in $Node.Values) { Get-JsonStrings $v }; return }
    if ($Node -is [System.Management.Automation.PSCustomObject]) { foreach ($p in $Node.PSObject.Properties) { Get-JsonStrings $p.Value }; return }
    if ($Node -is [System.Collections.IEnumerable]) { foreach ($v in $Node) { Get-JsonStrings $v } }
}

Push-Location $Repo
try {
    Write-Host '== 1. probe, C# vs Python (read-only)'
    $pyProbe = Invoke-Tool 'probe-python' $Python @('-m', 'relayout.cli', 'probe', '--volume', $Root.Substring(0, 2)) | ConvertFrom-Json
    $csProbe = Invoke-Tool 'probe-csharp' $AsterExe @('probe', '--volume', $Root.Substring(0, 2)) | ConvertFrom-Json
    $probeSame = ($pyProbe.free_clusters_bitmap -eq $csProbe.free_clusters_bitmap) -and ($pyProbe.zone_candidate_lcn -eq $csProbe.zone_candidate_lcn)
    $summary.probe_same = $probeSame
    if (-not $probeSame) { throw 'C# probe differs from Python probe (free clusters or zone candidate): stop' }

    Write-Host '== 2. manifest before, plan, path check, dry-run, verify'
    $before = Get-Manifest $Root
    $before | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'manifest_before.json') -Encoding UTF8
    Invoke-Tool 'plan' $AsterExe @('plan', '--trace', (Join-Path $Root 'trace.json'), '--origin-root', $Root,
                                   '--zone-mib', "$ZoneMiB", '--out', $plan) | Out-Null
    $planObj = Get-Content -LiteralPath $plan -Raw | ConvertFrom-Json
    $bad = @(Get-JsonStrings $planObj | Where-Object { $_ -match '^[A-Za-z]:\\' -and -not ($_ -ieq $Root) -and -not $_.StartsWith("$Root\", [StringComparison]::OrdinalIgnoreCase) })
    if ($bad.Count -gt 0) { throw "plan path outside ${Root}: $($bad[0])" }
    $allowed = @('--allowed-root', $Root)
    $dry = Invoke-Tool 'apply-dry-run' $AsterExe (@('apply', '--plan', $plan, '--journal', $journal, '--dry-run') + $allowed) | ConvertFrom-Json
    if ($dry.tasks_todo -le 0) { throw 'dry-run found nothing to move' }
    Invoke-Tool 'verify' $AsterExe @('verify', '--plan', $plan) | Out-Null
    $summary.dry_run = $dry

    Write-Host '== 3. cold stream: Python and C#, modes (a) pvm and (b) overlapped read, alternating'
    $modes = @(
        @{ name = 'a_pvm'; flags = @('--method', 'pvm') },
        @{ name = 'b_read_qd2'; flags = @('--method', 'read', '--threads', '2') }
    )
    $summary.modes = [ordered]@{}
    foreach ($mode in $modes) {
        $py = @(); $cs = @()
        for ($i = 1; $i -le $Runs; $i++) {
            Invoke-Purge "$($mode.name)-py$i"
            $py += Invoke-Timed "$($mode.name)-py$i" $Python (@('-m', 'prefetch.cli', '--plan', $plan, '--origin-root', $Root, '--execute') + $mode.flags)
            Invoke-Purge "$($mode.name)-cs$i"
            $cs += Invoke-Timed "$($mode.name)-cs$i" $AsterExe (@('prefetch', '--plan', $plan, '--origin-root', $Root, '--execute') + $mode.flags)
        }
        $pyMed = ($py | Sort-Object)[[int][math]::Floor($Runs / 2)]
        $csMed = ($cs | Sort-Object)[[int][math]::Floor($Runs / 2)]
        $summary.modes[$mode.name] = [ordered]@{ python_s = $py; csharp_s = $cs; python_median_s = $pyMed; csharp_median_s = $csMed; csharp_not_slower = ($csMed -le $pyMed) }
    }

    Write-Host '== 4. manifest after the stream must equal the manifest before'
    $after = Get-Manifest $Root
    $same = (($before | ConvertTo-Json -Compress) -eq ($after | ConvertTo-Json -Compress))
    $summary.manifest_identical = $same
    if (-not $same) { throw 'SHA-256 manifest changed during the stream check: stop' }

    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $out 'summary.json') -Encoding UTF8
    Write-Host "Saved: $(Join-Path $out 'summary.json')"
    foreach ($k in $summary.modes.Keys) { Write-Host "$k : C# not slower = $($summary.modes[$k].csharp_not_slower)" }
} finally {
    Pop-Location
}
