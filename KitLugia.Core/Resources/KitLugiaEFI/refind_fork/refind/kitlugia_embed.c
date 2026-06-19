/* kitlugia_embed.c
 * KitLugia built-in support for rEFInd fork.
 *
 * This source is compiled directly into the custom rEFInd binary.
 * The real KitLugia entry handling is in main.c (TAG_KITLUGIA in
 * ScanForTools + action dispatch). This file provides a placeholder
 * for future direct-embedded shrink logic if desired.
 *
 * Currently: rEFInd chainloads kitlugia_shrink.efi via StartTool().
 * Future: Shrink logic compiled directly into rEFInd via this file.
 *
 * === LOG ===
 * 2026-05-30 — v1.0: Initial stub
 */

#ifdef __MAKEWITH_GNUEFI
#include <efi.h>
#include <efilib.h>
#else
#include "../include/tiano_includes.h"
#endif

#include "global.h"
#include "lib.h"

// Placeholder for direct embedded KitLugia shrink logic.
// To enable direct execution (no separate .efi needed):
//   1. Include ../kitlugia_shrink/src/* (gpt_lib.c, disk_io.c)
//   2. Define KitLugiaEmbeddedMain() below
//   3. Change TAG_KITLUGIA action in main.c from StartTool()
//      to RunKitLugiaEmbedded()
