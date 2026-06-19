#ifndef GPT_LIB_H
#define GPT_LIB_H

#include "kitlugia_types.h"

#define EFI_PARTITION_TYPE_ESP \
    { 0xC12A7328, 0xF81F, 0x11D2, { 0xBA, 0x4B, 0x00, 0xA0, 0xC9, 0x3E, 0xC9, 0x3B } }
#define EFI_PARTITION_TYPE_MSR \
    { 0xE3C9E316, 0x0B5C, 0x4DB8, { 0x81, 0x7D, 0xF9, 0x2D, 0xF0, 0x02, 0x15, 0xAE } }
#define EFI_PARTITION_TYPE_BASIC_DATA \
    { 0xEBD0A0A2, 0xB9E5, 0x4433, { 0x87, 0xC0, 0x68, 0xB6, 0xB7, 0x26, 0x99, 0xC7 } }

typedef struct {
    EFI_BLOCK_IO_PROTOCOL *BlockIo;
    GPT_HEADER            GptHeader;
    GPT_ENTRY             *Entries;
    UINT32                EntryCount;
    UINT32                EntrySize;
} GPT_CONTEXT;

EFI_STATUS GptOpen(EFI_HANDLE *DiskHandle, GPT_CONTEXT *Ctx);
EFI_STATUS GptRefresh(GPT_CONTEXT *Ctx);
EFI_STATUS GptFindPartition(GPT_CONTEXT *Ctx, UINT8 PartitionIndex, GPT_ENTRY **OutEntry);
EFI_STATUS GptShrinkPartition(GPT_CONTEXT *Ctx, UINT8 PartitionIndex, UINT64 NewLastLBA);
EFI_STATUS GptCreatePartition(GPT_CONTEXT *Ctx, UINT64 StartLBA, UINT64 EndLBA,
                              EFI_GUID *Type, CHAR16 *Name);
EFI_STATUS GptCommit(GPT_CONTEXT *Ctx);
VOID      GptClose(GPT_CONTEXT *Ctx);

#endif
