<#
.SYNOPSIS
  S5 (C#) SKELETON: relayout with the window-group plan (3 절 (c)②). Not adopted, not implemented.

.DESCRIPTION
  Only run if the window-group option is adopted after the (c) measurements in the design note. Until then this
  script does the elevation check and then stops with an explicit message. It changes nothing.
  Planned steps when adopted: VHDX trial (as S1) with the window-grouped plan -> stream and concurrent-mode timing
  -> verdict against 3 절 (c): stream <= 1.2 s, concurrent wall clock worse by <= 5 %.
#>
[CmdletBinding()]
param(
    [string]$Root = 'D:\AsterBench',
    [int]$WindowMiB = 4
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'not_admin: 관리자 필요. 관리자 PowerShell 에서 다시 실행하세요. 아무것도 바꾸지 않았습니다.'
}

throw 'S5 is a skeleton: the window-group option is not adopted. Nothing was changed. See csharp\README.md (미채택 항목).'
