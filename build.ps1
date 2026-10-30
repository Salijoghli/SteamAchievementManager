<#
.SYNOPSIS
    Builds SAM. Release produces a single, self-contained upload\SAM.exe that runs on any
    Windows 10/11 PC without installing anything (.NET and the Windows App SDK are inside it).

.EXAMPLE
    .\build.ps1                       # upload\SAM.exe
    .\build.ps1 -Configuration Debug  # normal multi-file debug build under SAM.App\bin
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is not installed. Get it from https://dotnet.microsoft.com/download'
}

if ($Configuration -eq 'Debug') {
    & dotnet build (Join-Path $PSScriptRoot 'SAM.sln') -c Debug -p:Platform=x64 -v:minimal -nologo
    exit $LASTEXITCODE
}

$upload = Join-Path $PSScriptRoot 'upload'
if (Test-Path $upload) {
    # Empty it rather than delete it: the folder itself may be open in Explorer or a terminal.
    Get-ChildItem $upload -Force | Remove-Item -Recurse -Force
}

& dotnet publish (Join-Path $PSScriptRoot 'SAM.App\SAM.App.csproj') `
    -c Release -p:Platform=x64 -r win-x64 --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:EnableMsixTooling=true `
    -p:DebugType=none `
    -o $upload -v:minimal -nologo
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$exe = Get-Item (Join-Path $upload 'SAM.exe')
Write-Host ("Built {0} ({1:N1} MB)" -f $exe.FullName, ($exe.Length / 1MB))
