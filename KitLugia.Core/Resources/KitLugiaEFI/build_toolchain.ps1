# KitLugia UEFI toolchain build script (Windows — MSYS2/Mingw-w64)
#
# === LOG ===
# 2026-05-30 — Initial port from bash
#
# Prerequisites:
#   1. Install MSYS2 from https://www.msys2.org/
#   2. In MSYS2 UCRT64 terminal:
#      pacman -S mingw-w64-ucrt-x86_64-gcc make git
#   3. Run this script from MSYS2 UCRT64 terminal
#      OR use WSL with build_toolchain.sh
#
# Usage:
#   .\build_toolchain.ps1          # Interactive (uses WSL if available)
#   .\build_toolchain.ps1 -WSL     # Force WSL mode
#   .\build_toolchain.ps1 -MSYS2   # Force MSYS2 mode (run from MSYS2 terminal)
#

param(
    [switch]$WSL,
    [switch]$MSYS2
)

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

$useWSL = $WSL
$useMSYS2 = $MSYS2

# Auto-detect: prefer WSL if available
if (-not $useWSL -and -not $useMSYS2) {
    try {
        $wslCheck = wsl --list --verbose 2>$null
        if ($wslCheck -match "Ubuntu|Default") {
            $useWSL = $true
            Write-Host "Auto-detected WSL with Ubuntu. Using WSL mode." -ForegroundColor Green
        } else {
            $useMSYS2 = $true
            Write-Host "WSL available but no distro. Using MSYS2 mode." -ForegroundColor Yellow
        }
    } catch {
        $useMSYS2 = $true
        Write-Host "WSL not detected. Using MSYS2 mode." -ForegroundColor Yellow
    }
}

if ($useWSL) {
    Write-Host "=== Building via WSL ===" -ForegroundColor Cyan
    $shPath = "./build_toolchain.sh".Replace('\', '/')
    wsl -d Ubuntu -- bash -c "cd '$(pwd -Path)'.Replace('\', '/') && chmod +x build_toolchain.sh && ./build_toolchain.sh --no-install"
    if ($LASTEXITCODE -eq 0) {
        Write-Host "=== BUILD COMPLETE via WSL ===" -ForegroundColor Green
    } else {
        Write-Host "=== BUILD FAILED ===" -ForegroundColor Red
        Write-Host "Run 'wsl -d Ubuntu' to see WSL errors, then run build_toolchain.sh manually."
    }
    return
}

if ($useMSYS2) {
    # Check if we're inside MSYS2
    $inMsys2 = [Environment]::GetEnvironmentVariable("MSYSTEM")
    if (-not $inMsys2) {
        Write-Host "WARNING: Not running inside MSYS2!" -ForegroundColor Red
        Write-Host "Either:" -ForegroundColor Yellow
        Write-Host "  1. Run this script from MSYS2 UCRT64 terminal" -ForegroundColor Yellow
        Write-Host "  2. Or use WSL mode: .\build_toolchain.ps1 -WSL" -ForegroundColor Yellow
        Write-Host "  3. Or follow manual steps in build_toolchain.ps1 comments" -ForegroundColor Yellow
        return
    }

    Write-Host "=== MSYS2 mode ===" -ForegroundColor Cyan

    # Check for required tools
    $missing = @()
    foreach ($cmd in @("gcc", "make", "git", "ld", "objcopy")) {
        $found = Get-Command $cmd -ErrorAction SilentlyContinue
        if (-not $found) { $missing += $cmd }
    }

    if ($missing.Count -gt 0) {
        Write-Host "Missing tools: $($missing -join ', ')" -ForegroundColor Red
        Write-Host "Install with: pacman -S mingw-w64-ucrt-x86_64-gcc make git" -ForegroundColor Yellow
        return
    }

    Write-Host "All tools found. Running Make..." -ForegroundColor Green
    $env:GNUEFI_DIR = "deps/gnu-efi"

    # Clone deps if needed
    if (-not (Test-Path "deps/gnu-efi/Makefile")) {
        Write-Host "Cloning gnu-efi..." -ForegroundColor Cyan
        git clone --depth 1 https://github.com/ncroxon/gnu-efi.git deps/gnu-efi
    }
    if (-not (Test-Path "deps/gnu-efi/x86_64/gnuefi/crt0-efi-x86_64.o")) {
        Write-Host "Building gnu-efi..." -ForegroundColor Cyan
        cd deps/gnu-efi
        make
        cd $ScriptDir
    }

    Write-Host "Building kitlugia_shrink.efi..." -ForegroundColor Cyan
    make clean 2>$null
    make all 2>&1

    if ($LASTEXITCODE -eq 0 -and (Test-Path "bin/kitlugia_shrink.efi")) {
        Write-Host "=== BUILD COMPLETE ===" -ForegroundColor Green
        Write-Host "Output: $((Get-Item bin/kitlugia_shrink.efi).FullName)"
    } else {
        Write-Host "=== BUILD FAILED ===" -ForegroundColor Red
    }
}
