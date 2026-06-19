#include "disk_io.h"
#include <efilib.h>

extern EFI_HANDLE ImageHandle;
extern EFI_SYSTEM_TABLE *SystemTable;

EFI_STATUS DiskOpenByIndex(UINT8 DiskIndex, EFI_BLOCK_IO_PROTOCOL **OutBlockIo)
{
    EFI_STATUS Status;
    EFI_HANDLE *Handles;
    UINTN HandleCount;
    UINTN found = 0;

    Status = BS->LocateHandleBuffer(ByProtocol, &gEfiBlockIoProtocolGuid, NULL, &HandleCount, &Handles);
    if (EFI_ERROR(Status)) return Status;

    for (UINTN i = 0; i < HandleCount; i++) {
        EFI_BLOCK_IO_PROTOCOL *Bio;
        Status = BS->HandleProtocol(Handles[i], &gEfiBlockIoProtocolGuid, (VOID**)&Bio);
        if (EFI_ERROR(Status)) continue;
        if (Bio->Media->LogicalPartition || !Bio->Media->MediaPresent) continue;
        if (found == DiskIndex) {
            *OutBlockIo = Bio;
            FreePool(Handles);
            return EFI_SUCCESS;
        }
        found++;
    }

    FreePool(Handles);
    return EFI_NOT_FOUND;
}

EFI_STATUS DiskReadSectors(EFI_BLOCK_IO_PROTOCOL *Bio, UINT64 LBA, UINTN Count, VOID *Buffer)
{
    return Bio->ReadBlocks(Bio, Bio->Media->MediaId, LBA, Count * (UINTN)SECTOR_SIZE, Buffer);
}

EFI_STATUS DiskWriteSectors(EFI_BLOCK_IO_PROTOCOL *Bio, UINT64 LBA, UINTN Count, VOID *Buffer)
{
    return Bio->WriteBlocks(Bio, Bio->Media->MediaId, LBA, Count * (UINTN)SECTOR_SIZE, Buffer);
}

/* Simple decimal parser — avoids gnu-efi Atoi issues */
static UINT64 SimpleAtoi(CHAR8 *Str)
{
    UINT64 val = 0;
    while (*Str >= '0' && *Str <= '9') {
        val = val * 10 + (*Str - '0');
        Str++;
    }
    return val;
}

EFI_STATUS EspReadConfig(KITLUGIA_CONFIG *Cfg)
{
    EFI_STATUS Status;
    EFI_SIMPLE_FILE_SYSTEM_PROTOCOL *Sfsp;
    EFI_FILE_PROTOCOL *Root = NULL, *File = NULL;
    EFI_HANDLE *Handles;
    UINTN HandleCount;
    CHAR8 buf[256];
    UINTN readSize = sizeof(buf) - 1;
    CHAR8 *p;

    ZeroMem(Cfg, sizeof(KITLUGIA_CONFIG));
    StrCpy(Cfg->NewLabel, L"KITLUGIA");

    Status = BS->LocateHandleBuffer(ByProtocol, &gEfiSimpleFileSystemProtocolGuid, NULL,
                                    &HandleCount, &Handles);
    if (EFI_ERROR(Status)) return Status;

    for (UINTN i = 0; i < HandleCount; i++) {
        EFI_BLOCK_IO_PROTOCOL *Bio;
        Status = BS->HandleProtocol(Handles[i], &gEfiBlockIoProtocolGuid, (VOID**)&Bio);
        if (EFI_ERROR(Status) || !Bio->Media->LogicalPartition) continue;

        Status = BS->HandleProtocol(Handles[i], &gEfiSimpleFileSystemProtocolGuid, (VOID**)&Sfsp);
        if (EFI_ERROR(Status)) continue;

        Status = Sfsp->OpenVolume(Sfsp, &Root);
        if (EFI_ERROR(Status)) continue;

        Status = Root->Open(Root, &File, (CHAR16*)CFG_FILE_NAME,
                            1, 0);  /* EFI_FILE_MODE_READ = 1 */
        if (!EFI_ERROR(Status)) break;

        Root->Close(Root);
        Root = NULL;
    }

    if (!File) { FreePool(Handles); return EFI_NOT_FOUND; }
    FreePool(Handles);

    ZeroMem(buf, sizeof(buf));
    Status = File->Read(File, &readSize, buf);
    File->Close(File);
    Root->Close(Root);
    if (EFI_ERROR(Status)) return Status;

    buf[readSize] = 0;
    p = buf;

    while (*p) {
        while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n') p++;
        if (!*p || *p == '#') {
            if (*p == '#') while (*p && *p != '\n') p++;
            continue;
        }

        CHAR8 *key = p;
        while (*p && *p != '=' && *p != ' ' && *p != '\t' && *p != '\n') p++;
        if (*p != '=') { while (*p && *p != '\n') p++; continue; }
        *p++ = 0;

        while (*p == ' ' || *p == '\t') p++;
        CHAR8 *val = p;
        while (*p && *p != '\n' && *p != '\r') p++;
        *p++ = 0;

        if (AsciiStrCmp(key, (CHAR8*)"disk") == 0)
            Cfg->DiskIndex = (UINT8)SimpleAtoi(val);
        else if (AsciiStrCmp(key, (CHAR8*)"partition") == 0)
            Cfg->PartitionIndex = (UINT8)SimpleAtoi(val);
        else if (AsciiStrCmp(key, (CHAR8*)"shrink_mb") == 0) {
            UINT64 mb = SimpleAtoi(val);
            Cfg->ShrinkSectors = (mb * MB) / SECTOR_SIZE;
        }
    }

    return EFI_SUCCESS;
}

EFI_STATUS EspWriteMarker(VOID)
{
    EFI_STATUS Status;
    EFI_SIMPLE_FILE_SYSTEM_PROTOCOL *Sfsp;
    EFI_FILE_PROTOCOL *Root = NULL, *File = NULL;
    EFI_HANDLE *Handles;
    UINTN HandleCount;
    CHAR8 data[] = "KITLUGIA_SHRINK_OK\n";
    UINTN writeSize = sizeof(data) - 1;

    Status = BS->LocateHandleBuffer(ByProtocol, &gEfiSimpleFileSystemProtocolGuid, NULL,
                                    &HandleCount, &Handles);
    if (EFI_ERROR(Status)) return Status;

    for (UINTN i = 0; i < HandleCount; i++) {
        EFI_BLOCK_IO_PROTOCOL *Bio;
        Status = BS->HandleProtocol(Handles[i], &gEfiBlockIoProtocolGuid, (VOID**)&Bio);
        if (EFI_ERROR(Status) || !Bio->Media->LogicalPartition) continue;

        Status = BS->HandleProtocol(Handles[i], &gEfiSimpleFileSystemProtocolGuid, (VOID**)&Sfsp);
        if (EFI_ERROR(Status)) continue;

        Status = Sfsp->OpenVolume(Sfsp, &Root);
        if (EFI_ERROR(Status)) continue;

        Status = Root->Open(Root, &File, (CHAR16*)MARKER_FILE_NAME,
                            1 | 2 | 0x8000000000000000ULL, 0);
        /* EFI_FILE_MODE_READ | EFI_FILE_MODE_WRITE | EFI_FILE_MODE_CREATE */
        if (!EFI_ERROR(Status)) break;

        Root->Close(Root);
        Root = NULL;
    }

    if (!File) { FreePool(Handles); return EFI_NOT_FOUND; }
    FreePool(Handles);

    Status = File->Write(File, &writeSize, data);
    File->Close(File);
    Root->Close(Root);
    return Status;
}
