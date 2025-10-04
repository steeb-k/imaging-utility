<#
 Build script for imaging-utility that ensures ALWAYS standalone builds.
 
 This script builds imaging-utility as a standalone executable with no external dependencies.
 
 Usage:
   .\build-standalone.ps1                    # Build for current architecture
   .\build-standalone.ps1 -Architecture x64  # Build for specific architecture
   .\build-standalone.ps1 -AllArchitectures  # Build for both x64 and ARM64
#>
param(
    [string] $Architecture = "x64",
    [switch] $AllArchitectures = $false,
    [string] $Configuration = "Release",
    [switch] $Clean = $false
)

$ErrorActionPreference = 'Stop'

$ProjectPath = "ImagingUtility.csproj"

Write-Host "Building imaging-utility STANDALONE..." -ForegroundColor Cyan
Write-Host "Configuration: $Configuration" -ForegroundColor Cyan

if ($Clean) {
    Write-Host "Cleaning previous builds..." -ForegroundColor Yellow
    dotnet clean $ProjectPath -c $Configuration
}

if ($AllArchitectures) {
    Write-Host "Building for ALL architectures (x64 and ARM64)..." -ForegroundColor Green
    
    # Build for win-x64
    Write-Host "`nBuilding for win-x64..." -ForegroundColor Green
    dotnet publish $ProjectPath -c $Configuration -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None /p:DebugSymbols=false
    
    # Build for win-arm64
    Write-Host "`nBuilding for win-arm64..." -ForegroundColor Green
    dotnet publish $ProjectPath -c $Configuration -r win-arm64 --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None /p:DebugSymbols=false
    
    Write-Host "`nBuild completed for all architectures!" -ForegroundColor Green
} else {
    $rid = "win-$Architecture"
    Write-Host "Building for $rid..." -ForegroundColor Green
    
    dotnet publish $ProjectPath -c $Configuration -r $rid --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None /p:DebugSymbols=false
}

if ($LASTEXITCODE -eq 0) {
    Write-Host "`nStandalone build completed successfully!" -ForegroundColor Green
    Write-Host "Output: bin\$Configuration\net8.0\" -ForegroundColor Cyan
    Write-Host "`nNOTE: This is a STANDALONE build with NO external dependencies!" -ForegroundColor Yellow
} else {
    Write-Host "`nBuild failed!" -ForegroundColor Red
    exit 1
}


