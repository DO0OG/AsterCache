<#
.SYNOPSIS
  Streams the recorded hot set into RAM (prefetch.cli, zone order) and starts the game at the same moment.

.DESCRIPTION
  Normal user, no Administrator needed. Run from any folder:
    powershell -NoProfile -ExecutionPolicy Bypass -File D:\Git\AsterCache\scripts\game_launch.ps1 `
      -Trace D:\AsterWork\trace.json -OriginRoot D:\Games\MW4 -Game D:\Games\MW4\MW4.exe
  -Game may also be a URI (battlenet://..., steam://rungameid/...): it is opened through the shell association.
  Order: prefetch.cli --execute is started first as a hidden background process (its output goes to a temp file),
  the game starts immediately after, then the script waits for the stream to end and prints seconds and bytes.
  -NoPrefetch only starts the game (use it for the comparison run). The game keeps running after the script ends.
  -Trace is the union trace.json written by "astercache_v07.py trace" (or plan.json from "astercache_v07.py plan").
  -FullFileRatio 2 (default) reads only the recorded blocks. After a relayout the rest of a file is NOT in the hot
  zone, so reading a file whole just because 30% of it is hot (the prefetch.cli default) would waste time.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Trace,
    [Parameter(Mandatory = $true)][string]$OriginRoot,
    [Parameter(Mandatory = $true)][string]$Game,
    [string[]]$GameArgs = @(),
    [switch]$NoPrefetch,
    [double]$FullFileRatio = 2,
    [string]$Python = 'python',
    [string]$Repo = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 leaves $PSScriptRoot empty while param defaults are evaluated.
if (-not $Repo) { $Repo = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path) }
$isUri = $Game -match '^[A-Za-z][A-Za-z0-9+.-]+://'
if (-not $isUri -and -not (Get-Command $Game -ErrorAction SilentlyContinue)) { throw "-Game '$Game' is neither a file nor a URI" }

function Start-Game {
    $sp = @{ FilePath = $Game; PassThru = $true }
    if (@($GameArgs).Count -gt 0) { $sp.ArgumentList = $GameArgs }
    return Start-Process @sp
}

if ($NoPrefetch) {
    $t0 = Get-Date
    $g = Start-Game
    Write-Host ("game started without prefetch at {0:HH:mm:ss.fff} (pid {1})" -f $t0, $g.Id)
    return
}

if (-not (Test-Path -LiteralPath $Trace -PathType Leaf)) { throw "-Trace file not found: $Trace" }
$root = $OriginRoot.TrimEnd('\')
if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "-OriginRoot not found: $OriginRoot" }
if (-not (Get-Command $Python -ErrorAction SilentlyContinue)) { throw "python not found ('$Python'): install Python 3.11+ or pass -Python" }

$outFile = Join-Path $env:TEMP ("aster_prefetch_{0}.json" -f [guid]::NewGuid().ToString('N'))
$errFile = "$outFile.err"
$pyArgs = '-m prefetch.cli --trace "{0}" --origin-root "{1}" --full-file-ratio {2} --execute' -f $Trace, $root, $FullFileRatio.ToString([Globalization.CultureInfo]::InvariantCulture)
$sw = [Diagnostics.Stopwatch]::StartNew()
$pf = Start-Process -FilePath $Python -ArgumentList $pyArgs -WorkingDirectory $Repo -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput $outFile -RedirectStandardError $errFile
$null = $pf.Handle  # keeps the handle so ExitCode is readable after the process ended
$g = Start-Game
$gameDelay = $sw.Elapsed.TotalSeconds
Write-Host ("prefetch started (pid {0}), game started {1:N2} s later (pid {2})" -f $pf.Id, $gameDelay, $g.Id)
$pf.WaitForExit()
$streamWall = $sw.Elapsed.TotalSeconds
$text = if (Test-Path -LiteralPath $outFile) { (Get-Content -LiteralPath $outFile -Raw) } else { '' }
if ($pf.ExitCode -ne 0 -or -not $text) {
    $err = if (Test-Path -LiteralPath $errFile) { Get-Content -LiteralPath $errFile -Raw } else { '' }
    throw "prefetch failed (exit $($pf.ExitCode)): $err"
}
$r = $text | ConvertFrom-Json
Remove-Item -LiteralPath $outFile, $errFile -ErrorAction SilentlyContinue
Write-Host ("stream done: {0:N2} s since launch (read time {1:N2} s), {2:N1} MiB, {3} requests, errors {4}, {5:N0} MiB/s" -f `
        $streamWall, $r.seconds, ($r.bytes / 1MB), $r.requests, $r.errors, ($r.bytes / 1MB / [Math]::Max($r.seconds, 0.001)))
if ($r.errors -ne 0) { Write-Warning 'some reads failed: the trace may be stale (game patched?). Check with relayout verify.' }
