#ifndef DISK_IO_H
#define DISK_IO_H

#include "kitlugia_types.h"

EFI_STATUS DiskOpenByIndex(UINT8 DiskIndex, EFI_BLOCK_IO_PROTOCOL **OutBlockIo);
EFI_STATUS DiskReadSectors(EFI_BLOCK_IO_PROTOCOL *Bio, UINT64 LBA, UINTN Count, VOID *Buffer);
EFI_STATUS DiskWriteSectors(EFI_BLOCK_IO_PROTOCOL *Bio, UINT64 LBA, UINTN Count, VOID *Buffer);

EFI_STATUS EspReadConfig(KITLUGIA_CONFIG *Cfg);
EFI_STATUS EspWriteMarker(VOID);

#endif
