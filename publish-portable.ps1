<#
.SYNOPSIS
    Compiles WinToRTSP into a single-file, self-contained portable executable.
#>

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = ".\publish\portable"
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Building Portable Single-File Executable: WinToRTSP       " -ForegroundColor Cyan
Write-Host " Target Runtime: $Runtime | Config: $Configuration        " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$projectDir = $PSScriptRoot
Set-Location $projectDir

if (Test-Path $OutputDir) {
    Remove-Item -Recurse -Force $OutputDir
}

Write-Host "Running dotnet publish..." -ForegroundColor Yellow

dotnet publish `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $OutputDir

if ($LASTEXITCODE -eq 0) {
    $exePath = Join-Path $OutputDir "WinToRTSP.exe"
    if (Test-Path $exePath) {
        $sizeMb = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
        Write-Host ""
        Write-Host "==========================================================" -ForegroundColor Green
        Write-Host " Portable Build Successful!" -ForegroundColor Green
        Write-Host " Executable: $exePath ($sizeMb MB)" -ForegroundColor Green
        Write-Host "==========================================================" -ForegroundColor Green
    }
} else {
    Write-Host "Publish failed with error code $LASTEXITCODE" -ForegroundColor Red
    exit $LASTEXITCODE
}
