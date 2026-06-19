# KitLugia rEFInd Customization
#
# === LOG ===
# 2026-05-30 — Fork setup instructions + custom config template
#
# The main customization is in refind_kitlugia.conf (injected into
# ESP/EFI/refind/refind.conf at deploy time).
#
# For a full custom rEFInd binary with KitLugia branding:
#
# 1. Fork rEFInd source:
#    git clone --depth 1 https://github.com/rodsbooks/refind.git
#    OR (SourceForge upstream):
#    svn co https://svn.code.sf.net/p/refind/code/
#
# 2. Key files to modify:
#    - refind/config.c        — default_selection logic, timeout
#    - refind/screen.c        — UI rendering (background, text)
#    - refind/refind.conf     — default config template
#    - icons/                 — add KitLugia icons
#
# 3. Build (requires gnu-efi + TianoCore EDK2):
#    cd refind
#    make
#    (or use the EFI build scripts in the refind/mk*.sh)
#
# 4. Deploy:
#    Copy refind_x64.efi to ESP/EFI/refind/
#    Copy refind_kitlugia.conf as refind.conf
#    Copy kitlugia_shrink.efi to ESP/EFI/KitLugia/
#    Copy kitlugia.conf to ESP:/kitlugia.conf
#
# === Patches applied to rEFInd fork ===
#
# Patch 1: refind/config.c
#   - Add "kitlugia" boot entry type
#   - Auto-detect kitlugia_shrink.efi in ESP/EFI/KitLugia/
#   - If found AND kitlugia.conf exists → add "KitLugia Recovery" entry
#
# Patch 2: refind/screen.c
#   - KitLugia-branded theme (black background, green text)
#   - Hide all entries except "KitLugia Recovery" and "Windows"
#   - timeout=0 when kitlugia.conf present
#
# Patch 3: refind/main.c
#   - After booting KitLugia Recovery, on return check kitlugia_done.txt
#   - If present, remove kitlugia.conf and restore normal config
#
