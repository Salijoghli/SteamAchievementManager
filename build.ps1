<#
.SYNOPSIS
    Builds SAM.sln with the .NET SDK. Release output goes to .\upload, Debug output to .\bin.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is not installed. Get it from https://dotnet.microsoft.com/download'
}

& dotnet build (Join-Path $PSScriptRoot 'SAM.sln') -c $Configuration -v:minimal -nologo
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
