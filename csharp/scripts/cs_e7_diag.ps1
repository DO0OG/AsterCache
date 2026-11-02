<#
.SYNOPSIS
  E7 diagnosis (elevated): one cold replay with aster learn (--out trace) under a DiskIo capture, to see whether the ETW read events reach the engine.
  Usage: cs_e7_diag.ps1 -Out <folder> [-Root D:\AsterBench_e7] [-Trace <trace.json>] [-Seconds 45]. Writes diag-*.txt/json and diskio_B.etl into <Out>.
#>
param([Parameter(Mandatory)][string]$Out, [string]$Root = 'D:\AsterBench_e7', [string]$Trace = '', [int]$Seconds = 45, [string]$Python = 'python')
$ErrorActionPreference = 'Continue'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'not_admin' }
$Repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
$Aster = Join-Path $Repo 'csharp\publish\gui\aster.exe'
if (-not $Trace) { $Trace = Join-Path $Root 'trace.json' }
$wprp = Join-Path $Repo ('csharp' + [char]92 + 'scripts' + [char]92 + 'aster_diskio.wprp')
$etl = Join-Path $Out 'diskio_B.etl'
Set-Location $Repo
& $Python -m bench.cache_control | Set-Content (Join-Path $Out 'diag-purge.txt')
& wpr.exe -cancel 2>&1 | Out-Null
& wpr.exe -start "$wprp!AsterDiskIO" -filemode 2>&1 | Out-String | Set-Content (Join-Path $Out 'diag-wpr-start.txt')
$wprOk = ($LASTEXITCODE -eq 0)
$lp = Start-Process -FilePath $Aster -ArgumentList @('learn', '--exe', 'python.exe', '--root', "`"$Root`"", '--seconds', "$Seconds", '--json', '--out', (Join-Path $Out 'diag_learn_trace.json')) `
    -RedirectStandardOutput (Join-Path $Out 'diag-learn.txt') -RedirectStandardError (Join-Path $Out 'diag-learn.err.txt') -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 3
& $Python -X utf8 -m bench.replay --trace $Trace | Set-Content (Join-Path $Out 'diag-replay.txt')
if (-not $lp.WaitForExit(($Seconds + 30) * 1000)) { Stop-Process -Id $lp.Id -Force }
if ($wprOk) { & wpr.exe -stop $etl 2>&1 | Out-String | Set-Content (Join-Path $Out 'diag-wpr-stop.txt') }
Set-Content (Join-Path $Out 'diag.DONE') 'ok'
