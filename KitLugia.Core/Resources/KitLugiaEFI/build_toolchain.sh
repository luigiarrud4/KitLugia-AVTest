#!/bin/sh
# KitLugia UEFI toolchain build script (Linux/WSL)
# Installs dependencies, fetches gnu-efi + ntfs-3g, and builds kitlugia_shrink.efi
#
# === LOG ===
# 2026-05-30 — Initial script
#
# Usage:
#   chmod +x build_toolchain.sh
#   sudo ./build_toolchain.sh          # One-shot: install deps + build
#   ./build_toolchain.sh --no-install  # Only build, skip package install
#

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

echo ""
echo "=== KitLugia UEFI Toolchain Build ==="
echo ""

# ---------- Step 1: Install system deps ----------
if [ "$1" != "--no-install" ]; then
    echo "[1/4] Installing build dependencies..."
    if command -v apt-get >/dev/null 2>&1; then
        sudo apt-get update -qq
        sudo apt-get install -y -qq build-essential git uuid-dev openssl 2>&1 | tail -1
    elif command -v pacman >/dev/null 2>&1; then
        sudo pacman -S --noconfirm base-devel git 2>&1 | tail -1
    elif command -v dnf >/dev/null 2>&1; then
        sudo dnf install -y gcc git make binutils 2>&1 | tail -1
    else
        echo "  WARNING: Unknown package manager. Install gcc, make, git, ld, objcopy manually."
    fi
    echo "  OK"
else
    echo "[1/4] SKIP: System package install"
fi

# ---------- Step 2: Fetch submodules ----------
echo "[2/4] Checking dependencies..."

if [ ! -d "deps/gnu-efi" ]; then
    echo "  Cloning gnu-efi..."
    git clone --depth 1 https://github.com/ncroxon/gnu-efi.git deps/gnu-efi
else
    echo "  gnu-efi: already present"
fi

# ---------- Step 3: Build gnu-efi ----------
echo "[3/4] Building gnu-efi libraries..."
if [ ! -f "deps/gnu-efi/x86_64/gnuefi/crt0-efi-x86_64.o" ]; then
    make -C deps/gnu-efi 2>&1 | tail -3
    echo "  gnu-efi built"
else
    echo "  gnu-efi already built"
fi

# ---------- Step 4: Build kitlugia_shrink.efi ----------
echo "[4/4] Building kitlugia_shrink.efi (with -MMD -MP dependency tracking)..."
make clean 2>/dev/null || true
make all
echo ""
echo "=== BUILD COMPLETE ==="
echo "Output file: bin/kitlugia_shrink.efi"
ls -lh bin/kitlugia_shrink.efi 2>/dev/null || echo "Build failed!"
