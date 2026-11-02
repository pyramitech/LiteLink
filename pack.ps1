<#
.SYNOPSIS
Builds, optionally tests, and packs every NuGet package of the solution into artifacts\packages.

.DESCRIPTION
Runs `dotnet pack` on the solution in Release configuration. Projects that opt out of packing
(tests, benchmarks) are skipped automatically. Package version and metadata come from
Directory.Build.props; pass -Version to override the version for a single run.

.EXAMPLE
.\pack.ps1                       # Release packages into artifacts\packages
.\pack.ps1 -Version 1.0.1
.\pack.ps1 -SkipTests
.\pack.ps1 -Push -ApiKey $env:NUGET_API_KEY
#>
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$Push,
    [string]$Source = "https://api.nuget.org/v3/index.json",
    [string]$ApiKey
)

$ErrorActionPreference = "Stop"
$solution = Join-Path $PSScriptRoot "LiteLink.sln"
$output = Join-Path $PSScriptRoot "artifacts\packages"

$versionArgs = @()
if ($Version) { $versionArgs = @("-p:Version=$Version") }

if (Test-Path $output) { Remove-Item -Recurse -Force $output }

Write-Host "Building $Configuration" -ForegroundColor Cyan
dotnet build $solution -c $Configuration --nologo @versionArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not $SkipTests) {
    Write-Host "Testing" -ForegroundColor Cyan
    dotnet test $solution -c $Configuration --no-build --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "Packing into $output" -ForegroundColor Cyan
dotnet pack $solution -c $Configuration --no-build --nologo @versionArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-ChildItem $output -Filter *.nupkg | ForEach-Object { Write-Host "  $($_.Name)" }

if ($Push) {
    if (-not $ApiKey) { throw "-Push requires -ApiKey" }
    Write-Host "Pushing to $Source" -ForegroundColor Cyan
    foreach ($package in Get-ChildItem $output -Filter *.nupkg) {
        dotnet nuget push $package.FullName --source $Source --api-key $ApiKey --skip-duplicate
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}

Write-Host "Done" -ForegroundColor Green
