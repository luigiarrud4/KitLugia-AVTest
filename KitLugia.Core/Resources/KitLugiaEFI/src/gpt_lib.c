#include "gpt_lib.h"
#include <efilib.h>

static UINT32 Crc32Table[256];
static BOOLEAN Crc32Initialized = FALSE;

static VOID InitCrc32(VOID)
{
    for (UINT32 i = 0; i < 256; i++) {
        UINT32 crc = i;
        for (UINT32 j = 0; j < 8; j++)
            crc = (crc >> 1) ^ (crc & 1 ? 0xEDB88320 : 0);
        Crc32Table[i] = crc;
    }
    Crc32Initialized = TRUE;
}

static UINT32 CalcCrc32(const UINT8 *Data, UINTN Size)
{
    if (!Crc32Initialized) InitCrc32();
    UINT32 crc = 0xFFFFFFFF;
    for (UINTN i = 0; i < Size; i++)
        crc = Crc32Table[(crc ^ Data[i]) & 0xFF] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFF;
}

EFI_STATUS GptOpen(EFI_HANDLE *DiskHandle, GPT_CONTEXT *Ctx)
{
    EFI_STATUS Status;
    EFI_HANDLE *Handles;
    UINTN HandleCount;

    if (DiskHandle) {
        Status = BS->HandleProtocol(DiskHandle, &gEfiBlockIoProtocolGuid, (VOID**)&Ctx->BlockIo);
        if (EFI_ERROR(Status)) return Status;
    } else {
        Status = BS->LocateHandleBuffer(ByProtocol, &gEfiBlockIoProtocolGuid, NULL, &HandleCount, &Handles);
        if (EFI_ERROR(Status)) return Status;

        for (UINTN i = 0; i < HandleCount; i++) {
            EFI_BLOCK_IO_PROTOCOL *Bio;
            Status = BS->HandleProtocol(Handles[i], &gEfiBlockIoProtocolGuid, (VOID**)&Bio);
            if (EFI_ERROR(Status)) continue;
            if (Bio->Media->LogicalPartition || !Bio->Media->MediaPresent) continue;
            Ctx->BlockIo = Bio;
            break;
        }
        FreePool(Handles);
    }

    if (!Ctx->BlockIo) return EFI_NOT_FOUND;
    return GptRefresh(Ctx);
}

EFI_STATUS GptRefresh(GPT_CONTEXT *Ctx)
{
    UINT8 Buffer[SECTOR_SIZE];
    GPT_HEADER *Hdr = (GPT_HEADER*)Buffer;

    EFI_STATUS Status = Ctx->BlockIo->ReadBlocks(Ctx->BlockIo, Ctx->BlockIo->Media->MediaId,
                                       1, SECTOR_SIZE, Buffer);
    if (EFI_ERROR(Status)) return Status;

    if (CompareMem(Hdr->Signature, GPT_SIGNATURE, 8) != 0)
        return EFI_MEDIA_CHANGED;

    CopyMem(&Ctx->GptHeader, Hdr, sizeof(GPT_HEADER));

    Ctx->EntryCount = Hdr->NumberOfPartitionEntries;
    Ctx->EntrySize  = Hdr->SizeOfPartitionEntry;
    Ctx->Entries    = AllocatePool(Ctx->EntryCount * Ctx->EntrySize);
    if (!Ctx->Entries) return EFI_OUT_OF_RESOURCES;

    UINTN EntrySizeBytes = Ctx->EntryCount * Ctx->EntrySize;
    UINT64 EntryLBA = Hdr->PartitionEntryLBA;

    for (UINTN offset = 0; offset < EntrySizeBytes; offset += SECTOR_SIZE) {
        UINT64 lba = EntryLBA + (offset / SECTOR_SIZE);
        Status = Ctx->BlockIo->ReadBlocks(Ctx->BlockIo, Ctx->BlockIo->Media->MediaId,
                                           lba, SECTOR_SIZE,
                                           (UINT8*)Ctx->Entries + offset);
        if (EFI_ERROR(Status)) {
            FreePool(Ctx->Entries);
            Ctx->Entries = NULL;
            return Status;
        }
    }

    return EFI_SUCCESS;
}

EFI_STATUS GptFindPartition(GPT_CONTEXT *Ctx, UINT8 PartitionIndex, GPT_ENTRY **OutEntry)
{
    EFI_GUID NullGUID = { 0, 0, 0, { 0, 0, 0, 0, 0, 0, 0, 0 } };
    UINT32 found = 0;

    for (UINT32 i = 0; i < Ctx->EntryCount; i++) {
        GPT_ENTRY *E = (GPT_ENTRY*)((UINT8*)Ctx->Entries + i * Ctx->EntrySize);

        if (CompareMem(&E->PartitionTypeGUID, &NullGUID, sizeof(EFI_GUID)) == 0)
            continue;

        if (found == PartitionIndex) {
            *OutEntry = E;
            return EFI_SUCCESS;
        }
        found++;
    }

    return EFI_NOT_FOUND;
}

EFI_STATUS GptShrinkPartition(GPT_CONTEXT *Ctx, UINT8 PartitionIndex, UINT64 NewLastLBA)
{
    GPT_ENTRY  *Entry = NULL;
    EFI_GUID   NullGUID = { 0, 0, 0, { 0, 0, 0, 0, 0, 0, 0, 0 } };
    UINT32     found = 0;

    for (UINT32 i = 0; i < Ctx->EntryCount; i++) {
        GPT_ENTRY *E = (GPT_ENTRY*)((UINT8*)Ctx->Entries + i * Ctx->EntrySize);
        if (CompareMem(&E->PartitionTypeGUID, &NullGUID, sizeof(EFI_GUID)) == 0) continue;
        if (found == PartitionIndex) { Entry = E; break; }
        found++;
    }
    if (!Entry) return EFI_NOT_FOUND;

    if (NewLastLBA <= Entry->StartingLBA) return EFI_INVALID_PARAMETER;
    if (NewLastLBA >= Entry->EndingLBA) return EFI_INVALID_PARAMETER;

    Entry->EndingLBA = NewLastLBA;

    return EFI_SUCCESS;
}

EFI_STATUS GptCreatePartition(GPT_CONTEXT *Ctx, UINT64 StartLBA, UINT64 EndLBA,
                              EFI_GUID *Type, CHAR16 *Name)
{
    EFI_GUID NullGUID = { 0, 0, 0, { 0, 0, 0, 0, 0, 0, 0, 0 } };

    for (UINT32 i = 0; i < Ctx->EntryCount; i++) {
        GPT_ENTRY *E = (GPT_ENTRY*)((UINT8*)Ctx->Entries + i * Ctx->EntrySize);
        if (CompareMem(&E->PartitionTypeGUID, &NullGUID, sizeof(EFI_GUID)) == 0) {
            CopyMem(&E->PartitionTypeGUID, Type, sizeof(EFI_GUID));
            E->StartingLBA = StartLBA;
            E->EndingLBA   = EndLBA;
            E->Attributes  = 0;
            StrnCpy(E->PartitionName, Name, 36);
            return EFI_SUCCESS;
        }
    }

    return EFI_OUT_OF_RESOURCES;
}

EFI_STATUS GptCommit(GPT_CONTEXT *Ctx)
{
    EFI_STATUS Status;
    GPT_HEADER Hdr;
    UINTN      EntriesBytes = Ctx->EntryCount * Ctx->EntrySize;

    CopyMem(&Hdr, &Ctx->GptHeader, sizeof(GPT_HEADER));

    Hdr.PartitionEntryArrayCRC32 = CalcCrc32((UINT8*)Ctx->Entries, EntriesBytes);
    Hdr.HeaderCRC32 = 0;
    Hdr.HeaderCRC32 = CalcCrc32((UINT8*)&Hdr, Hdr.HeaderSize);

    UINT64 EntryLBA = Ctx->GptHeader.PartitionEntryLBA;

    for (UINTN offset = 0; offset < EntriesBytes; offset += SECTOR_SIZE) {
        UINT64 lba = EntryLBA + (offset / SECTOR_SIZE);
        UINTN len = EntriesBytes - offset;
        if (len > SECTOR_SIZE) len = SECTOR_SIZE;
        UINT8 block[SECTOR_SIZE];
        ZeroMem(block, SECTOR_SIZE);
        CopyMem(block, (UINT8*)Ctx->Entries + offset, len);

        Status = Ctx->BlockIo->WriteBlocks(Ctx->BlockIo, Ctx->BlockIo->Media->MediaId,
                                           lba, SECTOR_SIZE, block);
        if (EFI_ERROR(Status)) return Status;
    }

    Status = Ctx->BlockIo->WriteBlocks(Ctx->BlockIo, Ctx->BlockIo->Media->MediaId,
                                       1, SECTOR_SIZE, &Hdr);
    if (EFI_ERROR(Status)) return Status;

    UINT64 LastLBA = Ctx->BlockIo->Media->LastBlock;
    GPT_HEADER AltHdr;
    ZeroMem(&AltHdr, SECTOR_SIZE);
    Status = Ctx->BlockIo->ReadBlocks(Ctx->BlockIo, Ctx->BlockIo->Media->MediaId,
                                      LastLBA, SECTOR_SIZE, &AltHdr);
    if (!EFI_ERROR(Status)) {
        Hdr.MyLBA = LastLBA;
        Hdr.AlternateLBA = 1;
        Hdr.HeaderCRC32 = 0;
        Hdr.HeaderCRC32 = CalcCrc32((UINT8*)&Hdr, Hdr.HeaderSize);
        Status = Ctx->BlockIo->WriteBlocks(Ctx->BlockIo, Ctx->BlockIo->Media->MediaId,
                                           LastLBA, SECTOR_SIZE, &Hdr);
    }

    Status = Ctx->BlockIo->FlushBlocks(Ctx->BlockIo);
    return Status;
}

VOID GptClose(GPT_CONTEXT *Ctx)
{
    if (Ctx->Entries) FreePool(Ctx->Entries);
    ZeroMem(Ctx, sizeof(GPT_CONTEXT));
}
