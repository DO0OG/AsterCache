# AsterFlt 빌드: NuGet WDK 복원 -> msbuild(서명·카탈로그·설치·적재 없음) -> infverif.
# 사용: powershell -File kernel\build.ps1 [-Configuration Release|Debug] [-Analyze] [-Nuget <nuget.exe 경로>]
# 관리자 권한이 필요 없습니다. VS 에 "Windows Driver Kit" 구성 요소가 없으면 toolset-shim 을 VC 타깃 복사본에 얹어 씁니다.
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Analyze,
    [string]$Nuget = 'nuget'
)
$ErrorActionPreference = 'Stop'
$kernel = $PSScriptRoot
$packages = Join-Path $kernel 'packages'
$project = Join-Path $kernel 'AsterFlt\AsterFlt.vcxproj'

& $Nuget restore (Join-Path $kernel 'AsterFlt\packages.config') -PackagesDirectory $packages -NonInteractive
if ($LASTEXITCODE -ne 0) { throw 'nuget restore 실패' }

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -version '[17.0,18.0)' -requires Microsoft.Component.MSBuild -property installationPath
if (-not $vs) { $vs = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath }
if (-not $vs) { throw 'Visual Studio(MSBuild)를 찾지 못했습니다' }
# 64비트 MSBuild 여야 WDK 도구를 x64 폴더에서 고릅니다 (32비트면 x86 폴더를 찾아 ApiValidator·InfVerif 가 실패)
$msbuild = Join-Path $vs 'MSBuild\Current\Bin\amd64\MSBuild.exe'
$vc = Get-ChildItem (Join-Path $vs 'MSBuild\Microsoft\VC') -Directory | Where-Object { $_.Name -match '^v\d+$' } | Sort-Object Name | Select-Object -Last 1

$msbuildArgs = @('-t:Rebuild', "-p:Configuration=$Configuration", '-p:Platform=x64', '-nologo', '-v:m')
$realToolset = Join-Path $vc.FullName 'Platforms\x64\PlatformToolsets\WindowsKernelModeDriver10.0\Toolset.props'
if (-not (Test-Path $realToolset)) {
    $shim = Join-Path $packages 'vctargets'
    if (Test-Path $shim) { Remove-Item $shim -Recurse -Force }
    Copy-Item $vc.FullName $shim -Recurse
    Copy-Item (Join-Path $kernel 'toolset-shim\WindowsKernelModeDriver10.0') (Join-Path $shim 'Platforms\x64\PlatformToolsets') -Recurse
    $msbuildArgs += "-p:VCTargetsPath=$shim\"
}
if ($Analyze) {
    $wdk = (Get-ChildItem (Join-Path $packages 'Microsoft.Windows.WDK.x64.*\c') -Directory | Select-Object -First 1).FullName
    $msbuildArgs += '-p:RunCodeAnalysis=true', '-p:CodeAnalysisTreatWarningsAsErrors=true', "-p:CodeAnalysisRuleSet=$wdk\CodeAnalysis\DriverRecommendedRules.ruleset"
}
& $msbuild $project @msbuildArgs
if ($LASTEXITCODE -ne 0) { throw 'msbuild 실패' }

$infverif = (Get-ChildItem (Join-Path $packages 'Microsoft.Windows.WDK.x64.*\c\tools\*\x64\infverif.exe') | Select-Object -First 1).FullName
$inf = Join-Path $kernel "AsterFlt\x64\$Configuration\AsterFlt.inf"
& $infverif /v /u $inf          # 프로젝트 대상(Universal) 기준. 실패하면 빌드 실패
if ($LASTEXITCODE -ne 0) { throw 'infverif /u 실패' }
& $infverif /v /w $inf          # Windows Driver 기준: 드라이버 격리 규칙(1322·1323) 때문에 실패할 수 있어 참고용
$global:LASTEXITCODE = 0
