#ifndef KITLUGIA_TYPES_H
#define KITLUGIA_TYPES_H

#include <efi.h>
#include <efilib.h>

#pragma pack(1)

#define GPT_SIGNATURE        "EFI PART"
#define CFG_FILE_NAME        L"kitlugia.conf"
#define MARKER_FILE_NAME     L"kitlugia_done.txt"

#define MB (1024ULL * 1024ULL)
#define SECTOR_SIZE 512ULL

typedef struct {
    UINT8  Signature[8];
    UINT32 Revision;
    UINT32 HeaderSize;
    UINT32 HeaderCRC32;
    UINT32 Reserved;
    UINT64 MyLBA;
    UINT64 AlternateLBA;
    UINT64 FirstUsableLBA;
    UINT64 LastUsableLBA;
    EFI_GUID DiskGUID;
    UINT64 PartitionEntryLBA;
    UINT32 NumberOfPartitionEntries;
    UINT32 SizeOfPartitionEntry;
    UINT32 PartitionEntryArrayCRC32;
} GPT_HEADER;

typedef struct {
    EFI_GUID PartitionTypeGUID;
    EFI_GUID UniquePartitionGUID;
    UINT64   StartingLBA;
    UINT64   EndingLBA;
    UINT64   Attributes;
    CHAR16   PartitionName[36];
} GPT_ENTRY;

/* --------- NTFS structures --------- */

typedef struct {
    UINT8  Jump[3];
    UINT8  OEMID[8];
    UINT16 BytesPerSector;
    UINT8  SectorsPerCluster;
    UINT16 ReservedSectors;
    UINT8  NumFATs;
    UINT16 RootEntries;
    UINT16 TotalSectors16;
    UINT8  Media;
    UINT16 FATSize16;
    UINT16 SectorsPerTrack;
    UINT16 NumHeads;
    UINT32 HiddenSectors;
    UINT32 TotalSectors32;
    UINT8  Reserved[4];
    UINT64 TotalSectors;
    UINT64 MFTClusterNumber;
    UINT64 MFTMirrorClusterNumber;
    UINT8  ClustersPerMFTRecord;
    UINT8  Reserved2[3];
    UINT8  ClustersPerIndexBlock;
    UINT8  Reserved3[3];
    UINT64 VolumeSerialNumber;
    UINT32 Checksum;
} NTFS_BOOT_SECTOR;

#define MFT_RECORD_SIGNATURE  0x454C4946  /* "FILE" */

typedef struct {
    UINT32 Signature;       /* "FILE" */
    UINT16 USNOffset;
    UINT16 USNCount;
    UINT8  LogFileSeqNum[8];
    UINT16 SequenceNumber;
    UINT16 LinkCount;
    UINT16 AttrOffset;
    UINT16 Flags;
    UINT32 BytesInUse;
    UINT32 BytesAllocated;
    UINT64 BaseMFTRecord;
    UINT16 NextAttrID;
    UINT16 Padding;
    UINT32 RecordNumber;
} MFT_RECORD;

#define ATTR_TYPE_STANDARD_INF   0x10
#define ATTR_TYPE_ATTRIBUTE_LIST 0x20
#define ATTR_TYPE_FILE_NAME      0x30
#define ATTR_TYPE_VOLUME_NAME    0x60
#define ATTR_TYPE_VOLUME_INFO    0x70
#define ATTR_TYPE_DATA           0x80
#define ATTR_TYPE_INDEX_ROOT     0x90
#define ATTR_TYPE_INDEX_ALLOC    0xA0
#define ATTR_TYPE_BITMAP         0xB0

/* Common attribute header — first 16 bytes */
typedef struct {
    UINT32 Type;             /* 0x00 */
    UINT32 Length;           /* 0x04 */
    UINT8  NonResident;      /* 0x08 */
    UINT8  NameLength;       /* 0x09 */
    UINT16 NameOffset;       /* 0x0A */
    UINT16 Flags;            /* 0x0C */
    UINT16 AttributeNumber;  /* 0x0E */
} ATTR_COMMON;

/* Non-resident attribute (NonResident == 1) */
typedef struct {
    ATTR_COMMON H;           /* 0x00-0x0F (16 bytes) */
    UINT64      StartingVCN; /* 0x10 */
    UINT64      LastVCN;     /* 0x18 */
    UINT16      RunlistOffset;   /* 0x20 */
    UINT8       CompressionUnit; /* 0x22 */
    UINT8       Pad[5];         /* 0x23-0x27 */
    UINT64      AllocatedSize;   /* 0x28 */
    UINT64      DataSize;        /* 0x30 */
    UINT64      InitializedSize; /* 0x38 */
} ATTR_NONRES;

/* Resident attribute (NonResident == 0) */
typedef struct {
    ATTR_COMMON H;            /* 0x00-0x0F */
    UINT32      AttrLength;  /* 0x10 */
    UINT16      AttrOffset;  /* 0x14 */
    UINT8       IndexedFlag; /* 0x16 */
    UINT8       Reserved;    /* 0x17 */
} ATTR_RES;

/* --------- Config --------- */

typedef struct {
    UINT8  DiskIndex;
    UINT8  PartitionIndex;
    UINT64 ShrinkSectors;
    CHAR16 NewLabel[36];
} KITLUGIA_CONFIG;

#pragma pack()

#define LOG(fmt, ...) Print(L"[SHRINK] " fmt L"\n", ##__VA_ARGS__)
#define LOGA(fmt)     Print(L"[SHRINK] " L ## fmt L"\n")

#endif
