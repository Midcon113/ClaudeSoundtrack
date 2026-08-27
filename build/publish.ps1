<#
.SYNOPSIS
    Builds ClaudeSoundtrack as a single standalone .exe.

.DESCRIPTION
    Produces one file that runs on a clean Windows machine with no .NET
    installed and no loose DLLs beside it. That costs about 65MB, which is the
    right trade for an app people are meant to download and run rather than
    build.

    Trimming is deliberately off. ATL and the CUETools encoder both resolve
    types by reflection, and the trimmer cannot see those references - it
    produces a smaller binary that fails at runtime when it first tries to read
    a tag.

.PARAMETER Runtime
    Target runtime identifier. win-x64 covers essentially every Windows 11 PC;
    win-arm64 is there for Snapdragon machines.

.PARAMETER Output
    Where to write the .exe. Defaults to artifacts\standalone under the repo.

.EXAMPLE
    .\build\publish.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [string]$Output
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'src\ClaudeSoundtrack.App\ClaudeSoundtrack.App.csproj'

if (-not $Output) {
    $Output = Join-Path $repoRoot "artifacts\standalone\$Runtime"
}

Write-Host "Publishing ClaudeSoundtrack ($Runtime)..." -ForegroundColor Cyan

dotnet publish $project `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    --output $Output `
    --nologo

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed with exit code $LASTEXITCODE."
}

$exe = Join-Path $Output 'ClaudeSoundtrack.exe'
if (-not (Test-Path $exe)) {
    throw "Publish reported success but $exe is missing."
}

$sizeMb = [Math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  $exe"
Write-Host "  $sizeMb MB, self-contained - no .NET runtime needed on the target machine."
