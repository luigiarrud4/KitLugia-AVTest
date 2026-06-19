# KitLugia rEFInd Fork — Patches Documentation

## Overview

These patches modify rEFInd (https://www.rodsbooks.com/refind/) to add a
built-in "KitLugia Recovery" tool entry. When selected from the rEFInd menu,
it chainloads `kitlugia_shrink.efi` from `\EFI\KitLugia\` on the ESP.

No configuration file changes needed — the entry is compiled into the binary.

---

## Patch 1: `refind/global.h` — Add TAG_KITLUGIA

Adds a new tag constant for the KitLugia tool entry and increases the
tool count array.

```
 Lines 77-78:
-#define TAG_GDISK            (16)
-#define NUM_TOOLS            (17)
+#define TAG_GDISK            (16)
+#define TAG_KITLUGIA         (17)
+#define NUM_TOOLS            (18)
```

---

## Patch 2: `refind/main.c` — Add KitLugia to default ShowTools

Includes KitLugia in the default tool set so it appears without config.

```
 Lines 145-146:
-  { TAG_SHELL, TAG_MEMTEST, TAG_GDISK, TAG_APPLE_RECOVERY, TAG_WINDOWS_RECOVERY, TAG_MOK_TOOL,
-    TAG_ABOUT, TAG_SHUTDOWN, TAG_REBOOT, TAG_FIRMWARE, 0, 0, 0, 0, 0, 0 }
+  { TAG_SHELL, TAG_MEMTEST, TAG_GDISK, TAG_KITLUGIA, TAG_APPLE_RECOVERY, TAG_WINDOWS_RECOVERY,
+    TAG_MOK_TOOL, TAG_ABOUT, TAG_SHUTDOWN, TAG_REBOOT, TAG_FIRMWARE, 0, 0, 0, 0, 0, 0 }
```

---

## Patch 3: `refind/main.c` — KitLugia entry in ScanForTools

Adds a case to `ScanForTools()` that checks for `\EFI\KitLugia\kitlugia_shrink.efi`
on the ESP. If found, it creates a LOADER_ENTRY with the KitLugia icon.

```
 After case TAG_GDISK, before case TAG_APPLE_RECOVERY:

+ case TAG_KITLUGIA:
+     FileName = StrDuplicate(L"\\EFI\\KitLugia\\kitlugia_shrink.efi");
+     if (FileExists(SelfRootDir, FileName)) {
+        AddToolEntry(SelfLoadedImage->DeviceHandle, FileName,
+                     L"KitLugia Recovery",
+                     BuiltinIcon(BUILTIN_ICON_TOOL_KITLUGIA), 'K', FALSE);
+     }
+     MyFreePool(FileName);
+     FileName = NULL;
+     break;
```

---

## Patch 4: `refind/main.c` — KitLugia action handler

Handles the TAG_KITLUGIA case in the main boot loop by calling StartTool().

```
 After case TAG_FIRMWARE, before closing switch:

+ case TAG_KITLUGIA: // KitLugia Recovery
+     StartTool((LOADER_ENTRY *)ChosenEntry);
+     break;
```

---

## Patch 5: `refind/icns.h` — Add BUILTIN_ICON_TOOL_KITLUGIA

Registers a new built-in icon for the KitLugia tool.

```
 Lines 69-73:
-#define BUILTIN_ICON_TOOL_MEMTEST          (11)
-#define BUILTIN_ICON_VOL_INTERNAL          (12)
+#define BUILTIN_ICON_TOOL_MEMTEST          (11)
+#define BUILTIN_ICON_TOOL_KITLUGIA         (12)
+#define BUILTIN_ICON_VOL_INTERNAL          (13)
  ...
-#define BUILTIN_ICON_COUNT                 (15)
+#define BUILTIN_ICON_COUNT                 (16)
```

---

## Patch 6: `refind/icns.c` — KitLugia icon table entry

Adds the icon filename mapping for the tool.

```
 After tool_memtest entry:

+ { NULL, L"tool_kitlugia", ICON_SIZE_SMALL },
```

---

## Patch 7: Icons

Create `icons/tool_kitlugia.png` (128x128) and optionally a small variant.
The icon is a green "K" on transparent background.

---

## Patch 8: `refind/Makefile` — Build support

Add `kitlugia_embed.o` to the OBJS list so the KitLugia entry logic
is compiled into rEFInd.

---

## Build Instructions

```bash
# 1. Get clean rEFInd
git clone https://sourceforge.net/p/refind/code/ci/master/tree/ refind
cd refind

# 2. Apply patches
patch -p1 < refind_fork/patches/0001-global-h-add-tag-kitlugia.patch
patch -p1 < refind_fork/patches/0002-main-c-scan-tools-kitlugia.patch
# ... apply remaining patches

# 3. Copy icons
cp refind_fork/icons/tool_kitlugia.png icons/

# 4. Build
cd refind
make

# 5. Deploy
# - Copy refind_x64.efi to ESP/EFI/refind/
# - Copy EFI/KitLugia/kitlugia_shrink.efi to ESP/EFI/KitLugia/
# - Copy kitlugia.conf to ESP:/
```

## Files Modified

| File | Change |
|---|---|
| `refind/global.h` | +TAG_KITLUGIA, +NUM_TOOLS |
| `refind/main.c` | ShowTools init, ScanForTools case, action switch case |
| `refind/icns.h` | +BUILTIN_ICON_TOOL_KITLUGIA, +BUILTIN_ICON_COUNT |
| `refind/icns.c` | +tool_kitlugia icon table entry |
| `refind/Makefile` | +OBJS kitlugia_embed.o |

## New Files

| File | Purpose |
|---|---|
| `icons/tool_kitlugia.png` | KitLugia 128x128 icon |
| `icons/tool_kitlugia_small.png` | KitLugia 48x48 icon |
| `refind/kitlugia_embed.c` | KitLugia built-in entry support |
