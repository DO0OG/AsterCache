<#
.SYNOPSIS
  S4 (C#): install, uninstall or test the AsterCache Windows service.

.DESCRIPTION
  Run from an elevated (Administrator) PowerShell, by the user only:
    cs_service.ps1 -Action install   [-ExePath <published AsterCache.Service.exe>] [-DataDir <folder>] [-TestConfig [-TestRoot <folder>] [-Watcher etw|poll]]
    cs_service.ps1 -Action watcher   -Watcher etw|poll     (test config only: switches the process watcher and restarts the service)
    cs_service.ps1 -Action test
    cs_service.ps1 -Action uninstall [-PurgeData]

  install   : requires <DataDir>\config.json, except that -TestConfig writes a placeholder one when the file is missing
              (one game: exe cmd.exe, origin root -TestRoot (default D:\AsterBench), no trace/plan files, watcher -Watcher
              (default poll); an existing config.json is never touched). Creates plans, journals, logs. Registers service AsterCache (LocalSystem,
              automatic start) with --data-dir. the data folder gets ACL: SYSTEM and Administrators full control, Users read only.
              Starts the service.
  watcher   : sets "watcher" in config.json to etw or poll and restarts the service. Refuses unless every game in the file
              is the placeholder game 'astertest' (a real games list is never rewritten by this script).
  test      : service must be running and config.json must list cmd.exe. Starts the self-ending process
              `cmd.exe /c "ping -n 2 127.0.0.1 >nul"` -Samples times (it exits by itself after about 1 s; nothing is killed,
              and cmd.exe processes that are already open are ignored) and matches each new PID to the service.log line
              "cmd.exe started (pid N)". Delay = log time minus the process creation time. Verdict: p95 <= 100 ms.
              The watcher in use (EtwProcessWatcher = ETW, PollingProcessWatcher = polling), the configured one and the route
              of each detection (service.log "via poll|etw-raw|etw-tdh") are written to service_test.json. Run it once per
              watcher (install -TestConfig -Watcher poll, test, watcher -Watcher etw, test) to compare.
  uninstall : stops and deletes the service. Data folder is kept unless -PurgeData is given.
  Results: <repo>\results\csharp_<yyyyMMdd_HHmmss>\ (service_test.json, undo notes in README.txt).
  Each failure stops the script. Nothing here is executed by automation.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('install', 'uninstall', 'test', 'watcher')][string]$Action,
    [string]$ExePath = '',
    [string]$DataDir = (Join-Path $env:ProgramData 'AsterCache'),
    [string]$ServiceName = 'AsterCache',
    [int]$Samples = 20,
    [switch]$PurgeData,
    [switch]$TestConfig,
    [ValidateSet('etw', 'poll')][string]$Watcher = 'poll',
    [string]$TestRoot = 'D:\AsterBench',
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
        $cfgPath = Join-Path $DataDir 'config.json'
        if (-not (Test-Path -LiteralPath $cfgPath) -and -not $TestConfig) { throw "missing $cfgPath : create the games list first (or pass -TestConfig for a placeholder config)" }
        if (Get-AsterService) { throw "service '$ServiceName' already exists: run uninstall first" }
        New-Item -ItemType Directory -Force -Path $plans, (Join-Path $DataDir 'journals'), (Join-Path $DataDir 'logs') | Out-Null
        if (-not (Test-Path -LiteralPath $cfgPath)) {
            # Placeholder config (schema of Core/Settings): watches cmd.exe only; trace and plan files do not exist, which the service treats as "nothing to do yet".
            $label = ''
            try { $label = [string](Get-Volume -DriveLetter $TestRoot.Substring(0, 1) -ErrorAction Stop).FileSystemLabel } catch { $label = '' }
            if (-not $label) { $label = 'AsterBench' }
            $game = [ordered]@{
                name = 'astertest'; exe_names = @('cmd.exe')
                trace = (Join-Path $plans 'astertest.prefetch.json'); plan = (Join-Path $plans 'astertest.plan.json')
                origin_root = $TestRoot; allowed_roots = @($TestRoot); volume_label = $label.Substring(0, [math]::Min(32, $label.Length))
            }
            $json = ConvertTo-Json -InputObject ([ordered]@{ watcher = $Watcher; games = @($game) }) -Depth 6
            [IO.File]::WriteAllText($cfgPath, $json, (New-Object Text.UTF8Encoding($false)))
            Write-Host "wrote test config: $cfgPath (remove it with: uninstall -PurgeData)"
        }
        $bin = '"{0}" --data-dir "{1}"' -f $ExePath, $DataDir
        New-Service -Name $ServiceName -BinaryPathName $bin -DisplayName $ServiceName -StartupType Automatic -Description 'AsterCache game-start prefetch and layout check' | Out-Null
        # The whole data folder (config.json, plans, journals, logs): SYSTEM runs the service and moves files named there. SYSTEM and Administrators: full. Users: read only (the service refuses to reapply if non-admins can write).
        # Owner first (a user who created the folder or config.json earlier would own it, could rewrite the ACL, and AclCheck would disable reapply), then drop ACLs set on children.
        foreach ($step in @(@($DataDir, '/setowner', '*S-1-5-32-544', '/T', '/C'), @($DataDir, '/reset', '/T', '/C'))) {
            $r = Invoke-Native 'icacls.exe' $step
            if ($r.Code -ne 0) { throw "icacls failed (exit $($r.Code)): $($r.Text)" }
        }
        $acl = Invoke-Native 'icacls.exe' @($DataDir, '/inheritance:r', '/grant:r', '*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F', '*S-1-5-32-545:(OI)(CI)RX')
        if ($acl.Code -ne 0) { throw "icacls failed (exit $($acl.Code)): $($acl.Text)" }
        Start-Service -Name $ServiceName
        if ((Get-AsterService).Status -ne 'Running') { throw 'service did not reach Running' }
        Write-Host "installed and running: $ServiceName, data $DataDir"
    }
    'watcher' {
        $cfgPath = Join-Path $DataDir 'config.json'
        if (-not (Test-Path -LiteralPath $cfgPath)) { throw "missing $cfgPath" }
        $cfg = Get-Content -LiteralPath $cfgPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $names = @($cfg.games | ForEach-Object { $_.name })
        if ($names.Count -eq 0 -or @($names | Where-Object { $_ -ne 'astertest' }).Count -gt 0) { throw "config.json is not the placeholder test config (games: $($names -join ', ')); edit the 'watcher' key by hand" }
        if ($cfg.PSObject.Properties.Name -contains 'watcher') { $cfg.watcher = $Watcher } else { $cfg | Add-Member -NotePropertyName watcher -NotePropertyValue $Watcher }
        [IO.File]::WriteAllText($cfgPath, (ConvertTo-Json -InputObject $cfg -Depth 8), (New-Object Text.UTF8Encoding($false)))
        Restart-Service -Name $ServiceName -Force
        if ((Get-AsterService).Status -ne 'Running') { throw 'service did not reach Running' }
        Write-Host "watcher set to $Watcher, service restarted"
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
        if ($cfg -notmatch 'cmd\.exe') { throw 'config.json does not list cmd.exe: add it to the games list (or install with -TestConfig), then run test again' }

        New-Item -ItemType Directory -Force -Path $out | Out-Null
        $delays = @()
        $fromCall = @()
        $routeList = @()
        for ($i = 1; $i -le $Samples; $i++) {
            $before = 0
            if (Test-Path -LiteralPath $logFile) { $before = @(Get-Content -LiteralPath $logFile -Encoding UTF8).Count }
            $t0 = [DateTimeOffset]::Now
            # Self-ending child (about 1 s). It is matched by PID, so a cmd.exe the user already has open is never touched.
            $p = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c "ping -n 2 127.0.0.1 >nul"' -PassThru -WindowStyle Hidden
            $null = $p.Handle  # keeps StartTime/ExitCode readable on PowerShell 5.1
            $started = $p.StartTime
            $pidPattern = 'cmd\.exe started \(pid ' + $p.Id + '\)'
            $found = $null
            for ($w = 0; $w -lt 100 -and -not $found; $w++) {
                Start-Sleep -Milliseconds 50
                if (-not (Test-Path -LiteralPath $logFile)) { continue }
                $new = @(Get-Content -LiteralPath $logFile -Encoding UTF8 | Select-Object -Skip $before)
                $hit = $new | Where-Object { $_ -match $pidPattern } | Select-Object -First 1
                if ($hit) { $found = $hit }
            }
            if (-not $found) { throw "sample ${i}: no 'cmd.exe started (pid $($p.Id))' line in service.log within 5 s" }
            $ts = [DateTimeOffset]::Parse($found.Split(' ')[0])
            $routeList += $(if ($found -match '\) via (\S+)') { $Matches[1] } else { 'unknown' })
            $fromCall += [math]::Round(($ts - $t0).TotalMilliseconds, 1)
            $delays += [math]::Round(($ts - [DateTimeOffset]$started).TotalMilliseconds, 1)
            [void]$p.WaitForExit(10000)
            Start-Sleep -Milliseconds 300
        }
        $sorted = $delays | Sort-Object
        $p95 = $sorted[[int][math]::Ceiling(0.95 * $sorted.Count) - 1]
        $watcherLine = @(Get-Content -LiteralPath $logFile -Encoding UTF8 | Where-Object { $_ -match 'process watcher: ' } | Select-Object -Last 1)
        $watcher = if ($watcherLine.Count -gt 0) { ($watcherLine[0] -replace '^.*process watcher: ', '').Trim() } else { 'unknown' }
        $cfgNow = Get-Content -LiteralPath (Join-Path $DataDir 'config.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $configured = if ($cfgNow.PSObject.Properties.Name -contains 'watcher') { $cfgNow.watcher } else { 'poll (default)' }
        $routes = @($routeList | Group-Object | ForEach-Object { '{0}={1}' -f $_.Name, $_.Count })
        $result = [ordered]@{ samples = $delays; samples_from_start_process_call = $fromCall; p95_ms = $p95; threshold_ms = 100; pass = ($p95 -le 100); watcher = $watcher; configured_watcher = $configured; routes = $routes }
        $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out 'service_test.json') -Encoding UTF8
        Write-Host "p95 = $p95 ms (threshold 100 ms): pass = $($result.pass), watcher = $watcher"
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
