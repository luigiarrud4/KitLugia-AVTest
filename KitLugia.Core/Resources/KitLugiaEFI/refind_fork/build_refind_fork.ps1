# build_refind_fork.ps1 — KitLugia rEFInd Fork Build (Windows)
# Uses WSL to build since rEFInd requires a POSIX toolchain.
#
# Prerequisites:
#   - WSL with Ubuntu distro installed
#   - In WSL: sudo apt install gnu-efi gcc make git patch
#
# Usage:
#   .\build_refind_fork.ps1           # Full build (rEFInd fork + shrink)
#   .\build_refind_fork.ps1 -NoShrink # Only rEFInd fork
#   .\build_refind_fork.ps1 -NoRefind # Only shrink EFI app
#   .\build_refind_fork.ps1 -WslDist Ubuntu-22.04  # Custom WSL distro

param(
    [switch]$NoShrink,
    [switch]$NoRefind,
    [string]$WslDist = "Ubuntu"
)

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$KitRoot = Split-Path -Parent $ScriptDir  # KitLugiaEFI root

Write-Host "=== KitLugia rEFInd Fork Build (Windows/WSL) ===" -ForegroundColor Cyan
Write-Host "WSL Distro: $WslDist" -ForegroundColor Gray
Write-Host "Build shrink: $(-not $NoShrink)" -ForegroundColor Gray
Write-Host "Build refind: $(-not $NoRefind)" -ForegroundColor Gray
Write-Host ""

# Convert Windows paths to WSL paths
$wslRoot = $KitRoot.Replace('\', '/').Replace('C:', '/mnt/c')
$wslScriptDir = $ScriptDir.Replace('\', '/').Replace('C:', '/mnt/c')
$wslBuildSh = "$wslScriptDir/build_refind_fork.sh".Replace('\', '/')

# Build arguments for the bash script
$buildArgs = @()
if ($NoShrink) { $buildArgs += "--no-shrink" }
if ($NoRefind) { $buildArgs += "--no-refind" }

# Ensure the bash script is executable
wsl -d $WslDist -- chmod +x "$wslBuildSh" 2>$null

# Run the build inside WSL
Write-Host "Running build_refind_fork.sh in WSL..." -ForegroundColor Yellow
$result = wsl -d $WslDist -- bash -c "cd '$wslScriptDir' && ./build_refind_fork.sh $($buildArgs -join ' ')"

if ($LASTEXITCODE -eq 0) {
    Write-Host "=== BUILD COMPLETE ===" -ForegroundColor Green

    # Check outputs
    $shrinkEfi = "$KitRoot/bin/kitlugia_shrink.efi"
    $forkEfi = "$ScriptDir/bin/refind_x64.efi"

    if (-not $NoShrink -and (Test-Path $shrinkEfi)) {
        $size = (Get-Item $shrinkEfi).Length
        Write-Host "  kitlugia_shrink.efi: $([math]::Round($size/1KB, 1)) KB" -ForegroundColor Green
    }
    if (-not $NoRefind -and (Test-Path $forkEfi)) {
        $size = (Get-Item $forkEfi).Length
        Write-Host "  refind_x64.efi: $([math]::Round($size/1KB, 1)) KB" -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "Deploy with EmergencyUEFIManager:" -ForegroundColor Cyan
    Write-Host "  ESP/EFI/refind/refind_x64.efi         (custom rEFInd)"
    Write-Host "  ESP/EFI/KitLugia/kitlugia_shrink.efi  (shrink tool)"
    Write-Host "  ESP:/kitlugia.conf                    (config)"
} else {
    Write-Host "=== BUILD FAILED ===" -ForegroundColor Red
    Write-Host "Check WSL output above for errors." -ForegroundColor Yellow
    Write-Host "Common issues:" -ForegroundColor Yellow
    Write-Host "  1. WSL distro '$WslDist' not found — run 'wsl --list' to see available distros" -ForegroundColor Yellow
    Write-Host "  2. gnu-efi not installed — run: sudo apt install gnu-efi gcc make git" -ForegroundColor Yellow
}
