#Requires -Version 7.0
<#
.SYNOPSIS
	Builds, packs and pushes PhotoAtomic.Darc to NuGet.org (or a custom feed).

.DESCRIPTION
	Computes a VersionSuffix of the form "build.<yyyyMMdd>.<HHmmss>" so that
	every invocation produces a distinct NuGet package version (e.g. 1.0.0-build.20250602.130045).
	After packing, pushes both the .nupkg and the .snupkg (symbols) to the target feed.

.PARAMETER ApiKey
	NuGet API key. Can also be supplied via the NUGET_API_KEY environment variable.

.PARAMETER Source
	NuGet feed URL (default: https://api.nuget.org/v3/index.json).

.PARAMETER Configuration
	Build configuration (default: Release).

.EXAMPLE
	.\push-nuget.ps1 -ApiKey "oy2abc..."
	.\push-nuget.ps1   # reads NUGET_API_KEY from environment
#>
param(
	[string] $ApiKey        = $env:NUGET_API_KEY,
	[string] $Source        = "https://api.nuget.org/v3/index.json",
	[string] $Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
	Write-Error "No API key provided. Pass -ApiKey or set the NUGET_API_KEY environment variable."
	exit 1
}

$buildDate     = Get-Date -Format "yyyyMMdd"
$buildTime     = Get-Date -Format "HHmmss"
$versionSuffix = "build.$buildDate.$buildTime"

$repoRoot  = $PSScriptRoot
$project   = Join-Path $repoRoot "src\PhotoAtomic.Darc\PhotoAtomic.Darc.csproj"
$outputDir = Join-Path $repoRoot "nupkgs"

Write-Host ""
Write-Host "PhotoAtomic.Darc — pack & push to NuGet" -ForegroundColor Cyan
Write-Host "  Configuration  : $Configuration"
Write-Host "  VersionSuffix  : $versionSuffix"
Write-Host "  Feed           : $Source"
Write-Host "  Output         : $outputDir"
Write-Host ""

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

# --- Pack ---
dotnet pack $project `
	--configuration $Configuration `
	-p:VersionSuffix=$versionSuffix `
	--output $outputDir

if ($LASTEXITCODE -ne 0) {
	Write-Error "dotnet pack failed with exit code $LASTEXITCODE"
	exit $LASTEXITCODE
}

# --- Locate the freshly built packages ---
$nupkg  = Get-ChildItem $outputDir -Filter "PhotoAtomic.Darc*$buildDate.$buildTime*.nupkg"  | Select-Object -First 1
$snupkg = Get-ChildItem $outputDir -Filter "PhotoAtomic.Darc*$buildDate.$buildTime*.snupkg" | Select-Object -First 1

if (-not $nupkg) {
	Write-Error "Could not find the generated .nupkg in $outputDir"
	exit 1
}

Write-Host ""
Write-Host "Pushing: $($nupkg.Name)" -ForegroundColor Yellow

# --- Push .nupkg ---
dotnet nuget push $nupkg.FullName `
	--api-key $ApiKey `
	--source $Source `
	--skip-duplicate

if ($LASTEXITCODE -ne 0) {
	Write-Error "dotnet nuget push (.nupkg) failed with exit code $LASTEXITCODE"
	exit $LASTEXITCODE
}

# --- Push .snupkg (symbols) if present ---
if ($snupkg) {
	Write-Host "Pushing symbols: $($snupkg.Name)" -ForegroundColor Yellow
	dotnet nuget push $snupkg.FullName `
		--api-key $ApiKey `
		--source $Source `
		--skip-duplicate

	if ($LASTEXITCODE -ne 0) {
		Write-Error "dotnet nuget push (.snupkg) failed with exit code $LASTEXITCODE"
		exit $LASTEXITCODE
	}
}

Write-Host ""
Write-Host "Published successfully: $($nupkg.Name)" -ForegroundColor Green
