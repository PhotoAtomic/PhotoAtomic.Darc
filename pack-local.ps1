#Requires -Version 7.0
<#
.SYNOPSIS
	Builds and packs PhotoAtomic.Darc locally, always generating a unique version.

.DESCRIPTION
	Computes a VersionSuffix of the form "build.<yyyyMMdd>.<HHmmss>" so that
	every invocation produces a distinct NuGet package version (e.g. 1.0.0-build.20250602.130045).
	Output packages are placed in ./nupkgs, which is also registered as a local NuGet feed
	named "PhotoAtomic-local" so Visual Studio can find the package without any manual steps.

.PARAMETER Configuration
	Build configuration (default: Release).

.PARAMETER FeedName
	Name of the local NuGet feed entry (default: PhotoAtomic-local).
#>
param(
	[string] $Configuration = "Release",
	[string] $FeedName      = "PhotoAtomic-local"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$buildDate   = Get-Date -Format "yyyyMMdd"
$buildTime   = Get-Date -Format "HHmmss"
# Prefix the time with a letter so the SemVer pre-release identifier is alphanumeric.
# A purely-numeric identifier with a leading zero (e.g. "094556") is invalid SemVer.
$versionSuffix = "build.$buildDate.t$buildTime"

$repoRoot   = $PSScriptRoot
$project    = Join-Path $repoRoot "src\PhotoAtomic.Darc\PhotoAtomic.Darc.csproj"
$outputDir  = Join-Path $repoRoot "nupkgs"

Write-Host ""
Write-Host "PhotoAtomic.Darc — local pack" -ForegroundColor Cyan
Write-Host "  Configuration  : $Configuration"
Write-Host "  VersionSuffix  : $versionSuffix"
Write-Host "  Output         : $outputDir"
Write-Host ""

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

# --- Registra il feed locale in NuGet se non esiste già ---
$existingSources = dotnet nuget list source --format short 2>$null
$feedAlreadyRegistered = $existingSources | Where-Object { $_ -match [regex]::Escape($outputDir) }

if (-not $feedAlreadyRegistered) {
	Write-Host "Registering local NuGet feed '$FeedName' -> $outputDir" -ForegroundColor Yellow
	dotnet nuget add source $outputDir --name $FeedName
	if ($LASTEXITCODE -ne 0) {
		Write-Error "Failed to register local NuGet feed."
		exit $LASTEXITCODE
	}
	Write-Host "Feed registered. It will appear in Visual Studio under: Tools > NuGet Package Manager > Package Sources" -ForegroundColor Yellow
} else {
	Write-Host "Local feed '$FeedName' already registered." -ForegroundColor DarkGray
}
Write-Host ""

# --- Rimuove i package locali precedenti di PhotoAtomic.Darc ---
$oldPackages = @(Get-ChildItem $outputDir -Filter "PhotoAtomic.Darc*.nupkg" -ErrorAction SilentlyContinue) +
			   @(Get-ChildItem $outputDir -Filter "PhotoAtomic.Darc*.snupkg" -ErrorAction SilentlyContinue)
if ($oldPackages.Count -gt 0) {
	Write-Host "Removing $($oldPackages.Count) old package(s)..." -ForegroundColor DarkGray
	$oldPackages | Remove-Item -Force
}
Write-Host ""

dotnet pack $project `
	--configuration $Configuration `
	-p:VersionSuffix=$versionSuffix `
	--output $outputDir

if ($LASTEXITCODE -ne 0) {
	Write-Error "dotnet pack failed with exit code $LASTEXITCODE"
	exit $LASTEXITCODE
}

Write-Host ""
Write-Host "Package created successfully in: $outputDir" -ForegroundColor Green
Get-ChildItem $outputDir -Filter "PhotoAtomic.Darc*$buildDate*" | ForEach-Object {
	Write-Host "  $($_.Name)" -ForegroundColor Green
}
Write-Host ""
Write-Host "In Visual Studio: Tools > NuGet Package Manager > Manage Packages for Solution" -ForegroundColor Cyan
Write-Host "  Select feed '$FeedName', search for 'PhotoAtomic.Darc' and tick 'Include prerelease'." -ForegroundColor Cyan
