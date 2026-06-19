#!/bin/sh
# KitLugia rEFInd Fork — Full Build Script
# Builds:
#   1. Custom rEFInd (refind_x64.efi) with built-in KitLugia entry
#   2. kitlugia_shrink.efi (standalone shrink EFI app)
#
# Usage:
#   ./build_refind_fork.sh                    # Full build
#   ./build_refind_fork.sh --no-shrink        # Only rEFInd fork
#   ./build_refind_fork.sh --no-refind        # Only shrink EFI app
#
# Prerequisites:
#   - gnu-efi (apt install gnu-efi or build from source)
#   - gcc, make, git, patch
#
# === LOG ===
# 2026-05-30 — v1.0: Initial build script
#

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

BUILD_REFIND=true
BUILD_SHRINK=true
REFIND_SRC="deps/refind"
REFIND_URL="https://sourceforge.net/p/refind/code/ci/master/tree/"
JOBS="${JOBS:-4}"

if [ "$1" = "--no-shrink" ]; then BUILD_SHRINK=false; fi
if [ "$1" = "--no-refind" ]; then BUILD_REFIND=false; fi

echo "=== KitLugia rEFInd Fork Build ==="
echo "Build rEFInd: $BUILD_REFIND"
echo "Build shrink: $BUILD_SHRINK"
echo "Jobs: $JOBS"

# ---------- Step 1: Build kitlugia_shrink.efi ----------
if [ "$BUILD_SHRINK" = true ]; then
    echo ""
    echo "[1/2] Building kitlugia_shrink.efi..."
    cd "$SCRIPT_DIR/.."  # Go to KitLugiaEFI root
    make clean 2>/dev/null || true
    make all -j"$JOBS"
    echo "  kitlugia_shrink.efi: bin/kitlugia_shrink.efi"
    cd "$SCRIPT_DIR"
fi

# ---------- Step 2: Build custom rEFInd ----------
if [ "$BUILD_REFIND" = true ]; then
    echo ""
    echo "[2/2] Building custom rEFInd with KitLugia entry..."

    # Clone rEFInd if not present
    if [ ! -d "$REFIND_SRC" ]; then
        echo "  Cloning rEFInd source..."
        git clone --depth 1 "$REFIND_URL" "$REFIND_SRC"
    fi

    # Apply patches
    echo "  Applying KitLugia patches..."
    cd "$REFIND_SRC"

    for patch in "$SCRIPT_DIR/patches/"[0-9]*.patch; do
        echo "    Applying $(basename "$patch")..."
        patch -p1 < "$patch" 2>/dev/null || echo "    (already applied or skipped)"
    done

    # Copy icons
    if [ -d "$SCRIPT_DIR/icons" ]; then
        echo "  Copying KitLugia icons..."
        cp "$SCRIPT_DIR/icons/"*.png "icons/" 2>/dev/null || true
    fi

    # Build
    echo "  Building rEFInd..."
    cd refind
    make clean 2>/dev/null || true
    make -j"$JOBS"
    echo "  refind_x64.efi: refind/refind_x64.efi"
    cd "$SCRIPT_DIR"

    # Copy result
    mkdir -p "$SCRIPT_DIR/bin"
    cp "$REFIND_SRC/refind/refind_x64.efi" "$SCRIPT_DIR/bin/refind_x64.efi"
fi

echo ""
echo "=== BUILD COMPLETE ==="
echo "Output files:"
if [ "$BUILD_SHRINK" = true ]; then
    ls -lh "$SCRIPT_DIR/../bin/kitlugia_shrink.efi"
fi
if [ "$BUILD_REFIND" = true ]; then
    ls -lh "$SCRIPT_DIR/bin/refind_x64.efi"
fi
echo ""
echo "Deploy:"
echo "  ESP/EFI/refind/refind_x64.efi         (custom rEFInd)"
echo "  ESP/EFI/KitLugia/kitlugia_shrink.efi  (shrink EFI tool)"
echo "  ESP:/kitlugia.conf                    (config: disk, partition, shrink_mb)"
