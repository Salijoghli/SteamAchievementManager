<#
.SYNOPSIS
    Builds SAM.sln. Release output goes to .\upload, Debug output to .\bin.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw 'vswhere.exe not found. Install Visual Studio or the Visual Studio Build Tools.'
}

$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
    Select-Object -First 1
if (-not $msbuild) {
    throw 'MSBuild not found. Install the Visual Studio Build Tools with the MSBuild component.'
}

# A Build Tools install without the ".NET SDK" component can't resolve Microsoft.NET.Sdk.
# Fall back to the SDK that ships with the standalone dotnet CLI.
$vsSdk = Join-Path (Split-Path $msbuild) '..\..\Sdks\Microsoft.NET.Sdk'
if (-not (Test-Path $vsSdk)) {
    $dotnetSdk = & dotnet --list-sdks 2>$null |
        ForEach-Object { if ($_ -match '^(\S+) \[(.+)\]$') { Join-Path $Matches[2] $Matches[1] } } |
        Select-Object -Last 1
    if (-not $dotnetSdk) {
        throw 'No .NET SDK found. Install the .NET SDK or add the ".NET SDK" component to Visual Studio.'
    }
    Write-Host "Using .NET SDK from $dotnetSdk"
    $env:MSBuildSDKsPath = Join-Path $dotnetSdk 'Sdks'
    $env:MSBuildEnableWorkloadResolver = 'false'
}

& $msbuild (Join-Path $PSScriptRoot 'SAM.sln') -restore "-p:Configuration=$Configuration" -v:minimal -nologo
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
