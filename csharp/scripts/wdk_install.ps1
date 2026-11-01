<#
.SYNOPSIS
  S6: install the Windows Driver Kit (WDK) only. Installs no driver, signs nothing, loads nothing.

.DESCRIPTION
  Run ONLY after the user has decided to install WDK (large download, administrator, several GB on C:):
    powershell -NoProfile -ExecutionPolicy Bypass -File <repo>\csharp\scripts\wdk_install.ps1 -AcceptWdkInstall

  Without -AcceptWdkInstall the script changes nothing and stops.
  Steps: elevation check -> check whether the WDK km folder already exists -> free space on C: (printed) ->
  winget show (ID must resolve) -> winget install -> check the km folder again.
  Verdict: km folder exists. Building AsterFlt.sys is not part of this script (WPK1).
  [확인 필요] winget package ID (Microsoft.WindowsWDK) and the install size are not verified here.
  Results: <repo>\results\csharp_<yyyyMMdd_HHmmss>\wdk_install.txt
#>
[CmdletBinding()]
param(
    [switch]$AcceptWdkInstall,
    [string]$WingetId = 'Microsoft.WindowsWDK',
    [string]$Repo = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 1. Elevation first. Nothing below runs, and nothing is changed, without it.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'not_admin: 관리자 필요. 관리자 PowerShell 에서 다시 실행하세요. 아무것도 바꾸지 않았습니다.'
}
if (-not $AcceptWdkInstall) {
    throw 'WDK install needs -AcceptWdkInstall (user decision). Nothing was changed.'
}

if (-not $Repo) { $Repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) }
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$out = Join-Path $Repo "results\csharp_$stamp"
New-Item -ItemType Directory -Force -Path $out | Out-Null
$log = Join-Path $out 'wdk_install.txt'

function Get-KmFolders {
    $base = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Include'
    if (-not (Test-Path -LiteralPath $base)) { return @() }
    return @(Get-ChildItem -LiteralPath $base -Directory | ForEach-Object { Join-Path $_.FullName 'km' } | Where-Object { Test-Path -LiteralPath $_ })
}

function Write-Log([string]$Text) {
    Write-Host $Text
    Add-Content -LiteralPath $log -Value $Text -Encoding UTF8
}

$before = Get-KmFolders
Write-Log "km folders before: $(if ($before) { $before -join '; ' } else { 'none' })"
if ($before.Count -gt 0) { Write-Log 'WDK already present: nothing installed.'; return }

$drive = Get-PSDrive -Name C
Write-Log ("C: free {0:N1} GB" -f ($drive.Free / 1GB))

$show = (& winget.exe show --id $WingetId --exact --accept-source-agreements 2>&1 | Out-String)
Write-Log $show
if ($LASTEXITCODE -ne 0) { throw "winget show failed for '$WingetId' (exit $LASTEXITCODE): confirm the package ID first" }

$inst = (& winget.exe install --id $WingetId --exact --silent --accept-package-agreements --accept-source-agreements 2>&1 | Out-String)
Write-Log $inst
if ($LASTEXITCODE -ne 0) { throw "winget install failed (exit $LASTEXITCODE)" }

$after = Get-KmFolders
Write-Log "km folders after: $(if ($after) { $after -join '; ' } else { 'none' })"
if ($after.Count -eq 0) { throw 'km folder not found after install: WDK is not usable yet' }
Write-Log 'verdict: km folder present (WDK installed). No driver was loaded or signed.'
