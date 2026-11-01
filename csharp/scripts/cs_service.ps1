<#
.SYNOPSIS
  S4 (C#): install, uninstall or test the AsterCache Windows service.

.DESCRIPTION
  Run from an elevated (Administrator) PowerShell, by the user only:
    cs_service.ps1 -Action install   [-ExePath <published AsterCache.Service.exe>] [-DataDir <folder>]
    cs_service.ps1 -Action test
    cs_service.ps1 -Action uninstall [-PurgeData]

  install   : requires <DataDir>\config.json (the games list is never written by this script). Creates plans, journals,
              logs. Registers service AsterCache (LocalSystem, automatic start) with --data-dir. plans gets ACL:
              SYSTEM and Administrators full control, Users read only. Starts the service.
  test      : service must be running and config.json must list notepad.exe. Closes all notepad processes first
              (refuses if any are open, so the start event is real). Starts notepad 20 times and measures the time from
              start to the "notepad.exe started" line in service.log. Verdict: p95 <= 100 ms.
  uninstall : stops and deletes the service. Data folder is kept unless -PurgeData is given.
  Results: <repo>\results\csharp_<yyyyMMdd_HHmmss>\ (service_test.json, undo notes in README.txt).
  Each failure stops the script. Nothing here is executed by automation.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('install', 'uninstall', 'test')][string]$Action,
    [string]$ExePath = '',
    [string]$DataDir = (Join-Path $env:ProgramData 'AsterCache'),
    [string]$ServiceName = 'AsterCache',
    [int]$Samples = 20,
    [switch]$PurgeData,
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
if (-not $ExePath) { $ExePath = Join-Path $Repo 'csharp\publish\service\AsterCache.Service.exe' }
$DataDir = [IO.Path]::GetFullPath($DataDir).TrimEnd('\')
$plans = Join-Path $DataDir 'plans'
$logFile = Join-Path $DataDir 'logs\service.log'
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$out = Join-Path $Repo "results\csharp_$stamp"

function Get-AsterService {
    return Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
}

function Invoke-Native([string]$Exe, [string[]]$NativeArgs) {
    $text = (& $Exe @NativeArgs 2>&1 | Out-String)
    return [pscustomobject]@{ Code = $LASTEXITCODE; Text = $text }
}

switch ($Action) {
    'install' {
        if (-not (Test-Path -LiteralPath $ExePath)) { throw "service binary not found: $ExePath (publish it first, see csharp\README.md)" }
        if (-not (Test-Path -LiteralPath (Join-Path $DataDir 'config.json'))) { throw "missing $DataDir\config.json : create the games list first" }
        if (Get-AsterService) { throw "service '$ServiceName' already exists: run uninstall first" }
        New-Item -ItemType Directory -Force -Path $plans, (Join-Path $DataDir 'journals'), (Join-Path $DataDir 'logs') | Out-Null
        $bin = '"{0}" --data-dir "{1}"' -f $ExePath, $DataDir
        New-Service -Name $ServiceName -BinaryPathName $bin -DisplayName $ServiceName -StartupType Automatic -Description 'AsterCache game-start prefetch and layout check' | Out-Null
        # SYSTEM and Administrators: full. Users: read only (the service refuses to reapply if non-admins can write).
        $acl = Invoke-Native 'icacls.exe' @($plans, '/inheritance:r', '/grant:r', '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F', '*S-1-5-32-545:(OI)(CI)RX')
        if ($acl.Code -ne 0) { throw "icacls failed (exit $($acl.Code)): $($acl.Text)" }
        Start-Service -Name $ServiceName
        if ((Get-AsterService).Status -ne 'Running') { throw 'service did not reach Running' }
        Write-Host "installed and running: $ServiceName, data $DataDir"
    }
    'uninstall' {
        $svc = Get-AsterService
        if ($svc) {
            if ($svc.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
            $del = Invoke-Native 'sc.exe' @('delete', $ServiceName)
            if ($del.Code -ne 0) { throw "sc delete failed (exit $($del.Code)): $($del.Text)" }
        } else { Write-Host "service '$ServiceName' not present" }
        if ($PurgeData) {
            if (Test-Path -LiteralPath $DataDir) { Remove-Item -LiteralPath $DataDir -Recurse -Force }
            Write-Host "data folder removed: $DataDir"
        } else { Write-Host "data folder kept (use -PurgeData to remove): $DataDir" }
        if (Get-AsterService) { throw "service '$ServiceName' still present after delete" }
        Write-Host 'uninstalled'
    }
    'test' {
        $svc = Get-AsterService
        if (-not $svc -or $svc.Status -ne 'Running') { throw "service '$ServiceName' is not running" }
        $cfg = Get-Content -LiteralPath (Join-Path $DataDir 'config.json') -Raw
        if ($cfg -notmatch 'notepad\.exe') { throw 'config.json does not list notepad.exe: add it to the games list, then run test again' }
        if (Get-Process -Name notepad -ErrorAction SilentlyContinue) { throw 'close every notepad window first (an already-open process gives no start event)' }

        New-Item -ItemType Directory -Force -Path $out | Out-Null
        $delays = @()
        for ($i = 1; $i -le $Samples; $i++) {
            $before = 0
            if (Test-Path -LiteralPath $logFile) { $before = @(Get-Content -LiteralPath $logFile -Encoding UTF8).Count }
            $t0 = [DateTimeOffset]::Now
            $p = Start-Process -FilePath 'notepad.exe' -PassThru
            $found = $null
            for ($w = 0; $w -lt 100 -and -not $found; $w++) {
                Start-Sleep -Milliseconds 50
                if (-not (Test-Path -LiteralPath $logFile)) { continue }
                $new = @(Get-Content -LiteralPath $logFile -Encoding UTF8 | Select-Object -Skip $before)
                $hit = $new | Where-Object { $_ -match 'notepad\.exe started' } | Select-Object -First 1
                if ($hit) { $found = $hit }
            }
            Stop-Process -Name notepad -Force -ErrorAction SilentlyContinue
            if (-not $found) { throw "sample ${i}: no 'notepad.exe started' line in service.log within 5 s" }
            $ts = [DateTimeOffset]::Parse($found.Split(' ')[0])
            $delays += [math]::Round(($ts - $t0).TotalMilliseconds, 1)
            Start-Sleep -Milliseconds 300
        }
        $sorted = $delays | Sort-Object
        $p95 = $sorted[[int][math]::Ceiling(0.95 * $sorted.Count) - 1]
        $result = [ordered]@{ samples = $delays; p95_ms = $p95; threshold_ms = 100; pass = ($p95 -le 100) }
        $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out 'service_test.json') -Encoding UTF8
        Write-Host "p95 = $p95 ms (threshold 100 ms): pass = $($result.pass)"
    }
}

if ($Action -eq 'install') {
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    $undo = @(
        'S4 undo (not run by the script)',
        "stop and remove the service: powershell -File `"$PSCommandPath`" -Action uninstall",
        "remove the data folder as well: ... -Action uninstall -PurgeData (deletes plans, journals, logs)"
    )
    Set-Content -LiteralPath (Join-Path $out 'README.txt') -Value $undo -Encoding UTF8
}
