<#
.SYNOPSIS
    Builds the WinToRTSP installer package using Inno Setup Compiler.
#>

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Building WinToRTSP Installer Package                      " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$projectDir = $PSScriptRoot
Set-Location $projectDir

# 1. First build the self-contained portable executable
Write-Host "`n[Step 1/2] Building portable single-file binary..." -ForegroundColor Yellow
& "$projectDir\publish-portable.ps1" -Configuration $Configuration -Runtime $Runtime

$portableExe = Join-Path $projectDir "publish\portable\WinToRTSP.exe"
if (!(Test-Path $portableExe)) {
    Write-Host "[ERROR] Portable executable not found at $portableExe" -ForegroundColor Red
    exit 1
}

# 2. Check for Inno Setup Compiler (iscc.exe)
Write-Host "`n[Step 2/2] Locating Inno Setup compiler (iscc.exe)..." -ForegroundColor Yellow

$isccPath = $null
$candidatePaths = @(
    "iscc.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\iscc.exe",
    "${env:ProgramFiles}\Inno Setup 6\iscc.exe",
    "${env:LOCALAPPDATA}\Programs\Inno Setup 6\iscc.exe"
)

foreach ($path in $candidatePaths) {
    if (Get-Command $path -ErrorAction SilentlyContinue) {
        $isccPath = (Get-Command $path).Source
        break
    }
    if (Test-Path $path) {
        $isccPath = $path
        break
    }
}

if ($isccPath) {
    Write-Host "Found Inno Setup at: $isccPath" -ForegroundColor Green
    $issFile = Join-Path $projectDir "installer\WinToRTSP.iss"

    Write-Host "Compiling installer script: $issFile..." -ForegroundColor Yellow
    & "$isccPath" "$issFile"

    if ($LASTEXITCODE -eq 0) {
        $installerDir = Join-Path $projectDir "publish\installer"
        Write-Host "`n==========================================================" -ForegroundColor Green
        Write-Host " Installer Built Successfully!" -ForegroundColor Green
        Write-Host " Location: $installerDir" -ForegroundColor Green
        Write-Host "==========================================================" -ForegroundColor Green
    } else {
        Write-Host "[ERROR] Inno Setup compilation failed with code $LASTEXITCODE" -ForegroundColor Red
        exit $LASTEXITCODE
    }
} else {
    Write-Host "`n[NOTE] Inno Setup 6 was not detected on this system." -ForegroundColor Yellow
    Write-Host "The portable single-file build is ready at: $portableExe" -ForegroundColor Green
    Write-Host "To generate the setup installer (.exe):" -ForegroundColor Cyan
    Write-Host "  1. Download and install Inno Setup 6 from: https://jrsoftware.org/isdl.php" -ForegroundColor Cyan
    Write-Host "  2. Run: & 'C:\Program Files (x86)\Inno Setup 6\iscc.exe' 'installer\WinToRTSP.iss'" -ForegroundColor Cyan
    Write-Host "  or re-run this script: .\build-installer.ps1" -ForegroundColor Cyan
}
