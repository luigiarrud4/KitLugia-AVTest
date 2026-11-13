// ── Native MFT Scanner (WizTree-style raw $MFT read) ──────────────
//
// Reads the raw $MFT of an NTFS volume directly (bypassing Win32
// enumeration) and returns, for a set of prefix paths, every
// descendant record (rec, parent, size, last-write, flags, name).
//
// Techniques ported from Feather (nimao33/Feather, feather-core/mft.rs):
//   - $BITMAP of $MFT: free record ranges are skipped, not read.
//   - FILE_FLAG_NO_BUFFERING + sector-aligned reads: the table is read
//     once, so it bypasses the file cache instead of copying it.
//   - Slices cut along the $MFT run extents are parsed by worker
//     threads, each with its own volume handle (a synchronous handle
//     runs one request at a time).
//   - Extension records (ATTR_LIST children) contribute their name,
//     parent, timestamps and size to their base record.
//
// Two passes:
//   1. Stream the used parts of the MFT through the $DATA run list of
//      record 0 (per-record USA fixups; record numbers by position).
//   2. Resolve the prefixes from record 5 (root), then BFS over the
//      children adjacency (CSR built from a (parent, rec) sort),
//      collecting the allowed set; emit the matching records.
//
// Blob layout:
//   u32 prefix_count
//   u32 rec × prefix_count          (0xFFFFFFFF = prefix not found)
//   per entry:
//     u64 rec | u64 parent | u64 size | u64 last_write(FILETIME)
//     u16 flags | u16 name_len | u16 name...
//   flags: 0x1 directory | 0x2 reparse | 0x4 hidden/system
//
// Return codes:
//   >= 0        bytes written (== blob length)
//   <= -1000    buffer too small; needed = -rc - 1000
//   -1..-999    volume failure (caller falls back to Win32 enumeration)
//
// Rules: NO panics (panic = "abort" kills the host process). All
// dynamic indexing goes through get()/checked math.

use std::ptr;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::time::Instant;

// Progress callback (mft_scan_ffi_ex): stage 0 = parsing MFT records
// (done/total = slice records processed), stage 1 = indexing/emitting.
// Called from the caller's thread; must be cheap and must not unwind.
pub type ProgressFn = Option<unsafe extern "C" fn(u32, u64, u64)>;

#[link(name = "Kernel32")]
unsafe extern "system" {
    fn CreateFileW(
        path: *const u16,
        desired_access: u32,
        share_mode: u32,
        security: *mut u8,
        creation: u32,
        flags: u32,
        template: isize,
    ) -> isize;
    fn CloseHandle(h: isize) -> i32;
    fn ReadFile(h: isize, buf: *mut u8, bytes: u32, read: *mut u32, ov: *mut u8) -> i32;
    fn SetFilePointerEx(h: isize, dist: i64, new_pos: *mut i64, method: u32) -> i32;
}

const GENERIC_READ: u32 = 0x8000_0000;
const FILE_SHARE_READ: u32 = 1;
const FILE_SHARE_WRITE: u32 = 2;
const FILE_SHARE_DELETE: u32 = 4;
const OPEN_EXISTING: u32 = 3;
const INVALID_HANDLE: isize = -1;
const FILE_BEGIN: u32 = 0;
const FILE_FLAG_NO_BUFFERING: u32 = 0x2000_0000;
const SECTOR: usize = 512;

const ATTR_FILE_NAME: u32 = 0x30;
const ATTR_DATA: u32 = 0x80;
const ATTR_BITMAP: u32 = 0xB0;
const ATTR_REPARSE_POINT: u32 = 0xC0;
const ATTR_END: u32 = 0xFFFF_FFFF;

const FILE_ATTR_DIRECTORY: u32 = 0x1000_0000;
const FILE_ATTR_HIDDEN: u32 = 0x2;
const FILE_ATTR_SYSTEM: u32 = 0x4;
const FILE_ATTR_REPARSE: u32 = 0x400;

const F_DIR: u16 = 0x1;
const F_REPARSE: u16 = 0x2;
const F_HIDDEN_SYSTEM: u16 = 0x4;

pub(crate) const PREFIX_NOT_FOUND: u32 = 0xFFFF_FFFF;
const ROOT_REC: u32 = 5;

// Unbuffered I/O needs sector-aligned offsets, lengths and memory;
// 4 KiB covers both 512-byte and 4 KiB sectors.
const IO_ALIGN: usize = 4096;
// Slices of at most this many bytes of $MFT are parsed per work unit.
const SLICE_BYTES: u64 = 4 * 1024 * 1024;
// A run of this many unused records ends a read slice, so empty parts
// of the table are not read.
const SKIP_GAP: usize = 64;

const MAX_RECORDS: u64 = 64 * 1024 * 1024;
const MAX_BPR: usize = 16 * 1024 * 1024;
const MAX_WIDE_CHARS: usize = 32768;

struct Volume {
    handle: isize,
    bpc: u64,
    bpr: usize,
    runs: Vec<(u64, u64)>, // (lcn, len) in clusters; lcn == u64::MAX = sparse
    records: u64,          // total MFT records (logical space)
    bitmap: Vec<u8>,       // $MFT $BITMAP: bit set = record in use
}

#[derive(Clone, Copy)]
pub(crate) struct Entry {
    pub rec: u32,
    pub parent: u32,
    pub size: u64,
    pub last_write: u64,
    pub flags: u16,
    pub name_off: u32,
    pub name_len: u16,
    pub name_rank: u8,
}

/// Estado completo de um volume depois do parse: entradas ordenadas por
/// (parent, rec), pool de nomes UTF-16 e os indices derivados (CSR +
/// rec->idx). E o que o indice persistente grava em disco.
pub(crate) struct VolumeData {
    pub entries: Vec<Entry>,
    pub pool: Vec<u16>,
    pub start: Vec<u32>,
    pub rec_to_idx: Vec<i32>,
    pub record_count: usize,
}

/// Name/parent/size found in an extension record, applied to its base
/// record after all slices are merged.
struct Extension {
    base: u32,
    rank: u8,
    parent: u32,
    name: Vec<u16>,
    size: Option<u64>,
    last_write: u64,
}

/// Everything one worker thread produced for its slices.
struct Part {
    entries: Vec<Entry>,
    names: Vec<u16>,
    extensions: Vec<Extension>,
    records: u64,
}

fn read_raw(h: isize, off: u64, buf: &mut [u8]) -> bool {
    if unsafe { SetFilePointerEx(h, off as i64, ptr::null_mut(), FILE_BEGIN) } == 0 {
        return false;
    }
    let mut done = 0usize;
    while done < buf.len() {
        let n = (buf.len() - done).min(0x7FFF_FFFF) as u32;
        let mut rd: u32 = 0;
        if unsafe { ReadFile(h, buf[done..].as_mut_ptr(), n, &mut rd, ptr::null_mut()) } == 0 {
            return false;
        }
        if rd == 0 {
            return false;
        }
        done += rd as usize;
    }
    true
}

/// Reads `len` bytes at `offset` through an unbuffered handle, widening
/// the request to aligned bounds inside `buf`, and leaves exactly the
/// requested bytes at `buf[..len]`. `buf` must have room for
/// `len + 3 * IO_ALIGN` bytes: alignment padding on the pointer, the
/// offset skip, and rounding the length up to IO_ALIGN.
fn read_aligned(h: isize, offset: u64, len: usize, buf: &mut [u8]) -> bool {
    if buf.len() < len + 3 * IO_ALIGN {
        return false;
    }
    let start = offset & !(IO_ALIGN as u64 - 1);
    let skip = (offset - start) as usize;
    let total = (skip + len).div_ceil(IO_ALIGN) * IO_ALIGN;
    let pad = buf.as_ptr().align_offset(IO_ALIGN);
    if pad + total > buf.len() {
        return false;
    }
    let ptr = unsafe { buf.as_mut_ptr().add(pad) };
    let window = unsafe { std::slice::from_raw_parts_mut(ptr, total) };
    if !read_raw(h, start, window) {
        return false;
    }
    buf.copy_within(pad + skip..pad + skip + len, 0);
    true
}

fn le16(b: &[u8], off: usize) -> Option<u16> {
    Some(u16::from_le_bytes(b.get(off..off + 2)?.try_into().ok()?))
}

fn le32(b: &[u8], off: usize) -> Option<u32> {
    Some(u32::from_le_bytes(b.get(off..off + 4)?.try_into().ok()?))
}

fn le64(b: &[u8], off: usize) -> Option<u64> {
    Some(u64::from_le_bytes(b.get(off..off + 8)?.try_into().ok()?))
}

// USA fixup on a single record buffer (in place). Returns false if the
// record is corrupt (bad signature/USN) — caller skips it.
fn fixup_record(buf: &mut [u8]) -> bool {
    if buf.len() < SECTOR || &buf[..4] != b"FILE" {
        return false;
    }
    let usa_off = match le16(buf, 4) {
        Some(v) => v as usize,
        None => return false,
    };
    let usa_count = match le16(buf, 6) {
        Some(v) => v as usize,
        None => return false,
    };
    if usa_off == 0 || usa_count == 0 || buf.len() % SECTOR != 0 {
        return false;
    }
    let end = usa_off.saturating_add(usa_count * 2);
    if end > buf.len() || usa_count != buf.len() / SECTOR + 1 {
        return false;
    }
    let usn = match le16(buf, usa_off) {
        Some(v) => v,
        None => return false,
    };
    for i in 1..usa_count {
        let tail = i * SECTOR - 2;
        if le16(buf, tail) != Some(usn) {
            return false;
        }
        let v = match le16(buf, usa_off + 2 * i) {
            Some(v) => v,
            None => return false,
        };
        buf[tail] = (v & 0xFF) as u8;
        buf[tail + 1] = (v >> 8) as u8;
    }
    true
}

// Parses the data-run list of a non-resident attribute whose header
// starts at attr_start. Returns (lcn, len) runs in clusters.
fn parse_runs(buf: &[u8], attr_start: usize) -> Option<Vec<(u64, u64)>> {
    let run_off = le16(buf, attr_start + 0x20)? as usize;
    let mut runs = Vec::new();
    let mut pos = attr_start + run_off;
    let mut lcn: i64 = 0;
    loop {
        let h = *buf.get(pos)?;
        if h == 0 {
            break;
        }
        let len_size = (h & 0x0F) as usize;
        let off_size = (h >> 4) as usize;
        if len_size == 0 || len_size > 8 || off_size > 8 {
            return None;
        }
        pos += 1;
        let mut len: u64 = 0;
        for i in 0..len_size {
            len |= (*buf.get(pos + i)? as u64) << (8 * i);
        }
        pos += len_size;
        if len == 0 {
            return None;
        }
        if off_size == 0 {
            runs.push((u64::MAX, len));
            continue;
        }
        let mut raw: u64 = 0;
        for i in 0..off_size {
            raw |= (*buf.get(pos + i)? as u64) << (8 * i);
        }
        pos += off_size;
        let shift = 64 - 8 * off_size;
        let rel = ((raw << shift) as i64) >> shift;
        lcn = lcn.checked_add(rel)?;
        if lcn < 0 {
            return None;
        }
        runs.push((lcn as u64, len));
    }
    if runs.is_empty() {
        return None;
    }
    Some(runs)
}

// Name-space preference, shared by base records and extension records:
// Win32AndDos > Win32 > POSIX > DOS.
fn name_rank(space: u8) -> u8 {
    match space {
        3 => 4,
        1 => 3,
        0 => 2,
        _ => 1,
    }
}

// Parses one fixed-up record buffer into compact data. The name is
// pushed into `pool`; the returned name_off indexes it. Returns
// (parent, size, last_write, flags, name_off, name_len, name_rank).
// Returns None to skip the record (free, extension, or nameless).
fn parse_record(buf: &[u8], pool: &mut Vec<u16>) -> Option<(u32, u64, u64, u16, u32, u16, u8)> {
    if buf.len() < 0x42 {
        return None;
    }
    let hdr_flags = le16(buf, 0x16)?;
    if hdr_flags & 1 == 0 {
        return None; // not in use
    }
    let base = le64(buf, 0x20)?;
    if base & 0xFFFF_FFFF_FFFF != 0 {
        return None; // extension record
    }
    let first_attr = le16(buf, 0x14)? as usize;
    let mut pos = first_attr;
    let mut best: Option<(u32, u64, u64, u16, u32, u16, u8)> = None;
    let mut best_rank = 0u8;
    let mut has_reparse = false;
    while pos + 8 <= buf.len() {
        let atype = le32(buf, pos)?;
        if atype == ATTR_END {
            break;
        }
        let alen = le32(buf, pos + 4)? as usize;
        if alen < 16 || pos + alen > buf.len() {
            break;
        }
        if atype == ATTR_REPARSE_POINT {
            has_reparse = true;
        }
        let non_res = buf.get(pos + 8).copied()? != 0;
        if !non_res && atype == ATTR_FILE_NAME {
            let vlen = le32(buf, pos + 0x10)? as usize;
            let voff = le16(buf, pos + 0x14)? as usize;
            let vs = pos + voff;
            if vlen >= 0x42 && vs + vlen <= buf.len() {
                // FILE_NAME value layout (5 timestamps):
                // parent 0x00, creation 0x08, lastmod 0x10, lastchange 0x18,
                // lastaccess 0x20, allocated 0x28, realsize 0x30, flags 0x38,
                // reparse 0x3C, name_len 0x40, namespace 0x41, name 0x42.
                let parent = le64(buf, vs)?;
                let last_write = le64(buf, vs + 0x10)?;
                let size = le64(buf, vs + 0x30)?;
                let ns = buf.get(vs + 0x41).copied()? as u8;
                let nlen = buf.get(vs + 0x40).copied()? as usize;
                let rank = name_rank(ns);
                if rank > best_rank && vs + 0x42 + nlen * 2 <= buf.len() {
                    if pool.len() >= u32::MAX as usize {
                        return None; // pool overflow guard
                    }
                    let name_off = pool.len() as u32;
                    let mut i = 0;
                    while i < nlen {
                        pool.push(le16(buf, vs + 0x42 + i * 2)?);
                        i += 1;
                    }
                    let fflags = le32(buf, vs + 0x38)?;
                    if fflags & FILE_ATTR_REPARSE != 0 {
                        has_reparse = true;
                    }
                    let mut fl: u16 = 0;
                    if fflags & FILE_ATTR_DIRECTORY != 0 {
                        fl |= F_DIR;
                    }
                    if fflags & (FILE_ATTR_HIDDEN | FILE_ATTR_SYSTEM) != 0 {
                        fl |= F_HIDDEN_SYSTEM;
                    }
                    let parent_u32 = (parent & 0x0000_0000_FFFF_FFFF) as u32;
                    best_rank = rank;
                    best = Some((parent_u32, size, last_write, fl, name_off, nlen as u16, rank));
                }
            }
        }
        pos += alen;
    }
    if let Some((parent, size, last_write, mut fl, name_off, name_len, rank)) = best {
        if has_reparse {
            fl |= F_REPARSE;
        }
        Some((parent, size, last_write, fl, name_off, name_len, rank))
    } else {
        None
    }
}

// The size of the unnamed $DATA stream, from its first piece.
fn data_size(record: &[u8], offset: usize) -> Option<u64> {
    if record.get(offset + 9) != Some(&0) {
        return None;
    }
    if record.get(offset + 8) == Some(&0) {
        le32(record, offset + 0x10).map(u64::from)
    } else if le64(record, offset + 0x10) == Some(0) {
        le64(record, offset + 0x30)
    } else {
        None
    }
}

// Parses an extension record (base reference != 0) into the name,
// parent and size it contributes to its base record.
fn parse_extension(buf: &[u8]) -> Option<Extension> {
    if buf.len() < 0x42 {
        return None;
    }
    let hdr_flags = le16(buf, 0x16)?;
    if hdr_flags & 1 == 0 {
        return None;
    }
    let base = (le64(buf, 0x20)? & 0xFFFF_FFFF_FFFF) as u32;
    if base == 0 {
        return None;
    }
    let mut ext = Extension {
        base,
        rank: 0,
        parent: 0,
        name: Vec::new(),
        size: None,
        last_write: 0,
    };
    let mut pos = le16(buf, 0x14)? as usize;
    while pos + 8 <= buf.len() {
        let atype = le32(buf, pos)?;
        if atype == ATTR_END {
            break;
        }
        let alen = le32(buf, pos + 4)? as usize;
        if alen < 16 || pos + alen > buf.len() {
            break;
        }
        let non_res = buf.get(pos + 8).copied()? != 0;
        if !non_res && atype == ATTR_FILE_NAME {
            let vlen = le32(buf, pos + 0x10)? as usize;
            let voff = le16(buf, pos + 0x14)? as usize;
            let vs = pos + voff;
            if vlen >= 0x42 && vs + vlen <= buf.len() {
                let parent = le64(buf, vs)?;
                let last_write = le64(buf, vs + 0x10)?;
                let ns = buf.get(vs + 0x41).copied()? as u8;
                let nlen = buf.get(vs + 0x40).copied()? as usize;
                let rank = name_rank(ns);
                if rank > ext.rank && vs + 0x42 + nlen * 2 <= buf.len() {
                    ext.rank = rank;
                    ext.parent = (parent & 0x0000_0000_FFFF_FFFF) as u32;
                    ext.last_write = last_write;
                    ext.name = (0..nlen)
                        .filter_map(|i| le16(buf, vs + 0x42 + i * 2))
                        .collect();
                    if ext.name.len() != nlen {
                        ext.name.clear();
                        ext.rank = 0;
                    }
                }
            }
        } else if atype == ATTR_DATA && ext.size.is_none() {
            if let Some(size) = data_size(buf, pos) {
                ext.size = Some(size);
            }
        }
        pos += alen;
    }
    if ext.rank > 0 || ext.size.is_some() {
        Some(ext)
    } else {
        None
    }
}

fn lower_unit(u: u16) -> u16 {
    if u <= 0x7F {
        if (0x41..=0x5A).contains(&u) {
            return u + 0x20;
        }
        return u;
    }
    match char::from_u32(u as u32) {
        Some(c) => c
            .to_lowercase()
            .next()
            .map(|l| l as u32 as u16)
            .unwrap_or(u),
        None => u,
    }
}

// Case-insensitive compare of a pool name vs a component.
fn pool_name_eq(pool: &[u16], off: usize, len: usize, comp: &[u16]) -> bool {
    if len != comp.len() {
        return false;
    }
    for i in 0..len {
        let a = match pool.get(off + i) {
            Some(v) => *v,
            None => return false,
        };
        if lower_unit(a) != lower_unit(comp[i]) {
            return false;
        }
    }
    true
}

// Reads a NUL-terminated UTF-16 string from a raw pointer.
unsafe fn read_wide(ptr: *const u16) -> Option<Vec<u16>> {
    if ptr.is_null() {
        return None;
    }
    let mut v = Vec::new();
    for i in 0..MAX_WIDE_CHARS {
        let u = unsafe { *ptr.add(i) };
        if u == 0 {
            return Some(v);
        }
        v.push(u);
    }
    None
}

pub(crate) fn volume_device(root: &str) -> String {
    let trimmed = root.trim_end_matches(['\\', '/']);
    if trimmed.starts_with("\\\\") {
        trimmed.to_string()
    } else {
        format!("\\\\.\\{}", trimmed)
    }
}

// Opens the volume. Tries FILE_FLAG_NO_BUFFERING first (the table is
// read once, so caching it would only add a copy); falls back to a
// normal buffered handle, where the aligned reads still work.
pub(crate) fn open_handle(device: &str) -> Result<isize, i32> {
    let w: Vec<u16> = device.encode_utf16().chain(std::iter::once(0)).collect();
    for flags in [FILE_FLAG_NO_BUFFERING, 0] {
        let h = unsafe {
            CreateFileW(
                w.as_ptr(),
                GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                ptr::null_mut(),
                OPEN_EXISTING,
                flags,
                0,
            )
        };
        if h != INVALID_HANDLE {
            return Ok(h);
        }
    }
    Err(-2)
}

struct Boot {
    cluster: u64,
    mft_offset: u64,
    record_size: usize,
}

fn parse_boot(sector: &[u8]) -> Result<Boot, i32> {
    if sector.get(3..11) != Some(b"NTFS    ".as_slice()) {
        return Err(-3);
    }
    let bytes_per_sector = u64::from(le16(sector, 0x0b).unwrap_or(0));
    let raw_spc = sector[0x0d];
    // Clusters above 64 KiB store the power of two as a negative number.
    let sectors_per_cluster = if raw_spc > 0x80 {
        1u64 << (256 - u32::from(raw_spc))
    } else {
        u64::from(raw_spc)
    };
    let cluster = bytes_per_sector * sectors_per_cluster;
    let mft_lcn = le64(sector, 0x30).unwrap_or(0);
    let raw_record = sector[0x40] as i8;
    let record_size = if raw_record > 0 {
        raw_record as u64 * cluster
    } else {
        1u64 << (-(raw_record as i32)) as u32
    };
    if cluster == 0
        || !(256..=65_536).contains(&record_size)
        || bytes_per_sector < 512
        || record_size > MAX_BPR as u64
    {
        return Err(-4);
    }
    Ok(Boot {
        cluster,
        mft_offset: mft_lcn * cluster,
        record_size: record_size as usize,
    })
}

// A contiguous run of records on disk, read and parsed by one worker.
struct Slice {
    first: usize,
    count: usize,
    /// Physical offset of the first record; `None` for sparse runs.
    offset: Option<u64>,
}

/// Opens the volume and reads record 0: the $MFT run list, its real
/// size, and its $BITMAP (which records are in use).
fn read_table(device: &str) -> Result<Volume, i32> {
    let h = open_handle(device)?;
    let mut scratch = vec![0u8; 512 + 3 * IO_ALIGN];
    if !read_aligned(h, 0, 512, &mut scratch) {
        let _ = unsafe { CloseHandle(h) };
        return Err(-6);
    }
    let boot = match parse_boot(&scratch[..512]) {
        Ok(b) => b,
        Err(code) => {
            let _ = unsafe { CloseHandle(h) };
            return Err(code);
        }
    };

    // Record 0 describes $MFT itself: its size, where its pieces are,
    // and which records are allocated ($BITMAP).
    let mut rec0 = vec![0u8; boot.record_size + 3 * IO_ALIGN];
    if !read_aligned(h, boot.mft_offset, boot.record_size, &mut rec0) {
        let _ = unsafe { CloseHandle(h) };
        return Err(-6);
    }
    let rec0 = &mut rec0[..boot.record_size];
    if !fixup_record(rec0) {
        let _ = unsafe { CloseHandle(h) };
        return Err(-5);
    }

    let mut extents = Vec::new();
    let mut mft_size = 0u64;
    let mut bitmap = Vec::new();
    let mut bitmap_runs = Vec::new();
    let mut bitmap_size = 0u64;
    let rec0_ref: &[u8] = rec0;
    let mut pos = le16(rec0_ref, 0x14).unwrap_or(0) as usize;
    while pos + 16 <= rec0_ref.len() {
        let Some(kind) = le32(rec0_ref, pos) else { break };
        if kind == ATTR_END {
            break;
        }
        let Some(length) = le32(rec0_ref, pos + 4) else { break };
        let length = length as usize;
        if length < 16 || pos + length > rec0_ref.len() {
            break;
        }
        let unnamed = rec0_ref.get(pos + 9) == Some(&0);
        let non_resident = rec0_ref.get(pos + 8) == Some(&1);
        if unnamed && non_resident {
            if kind == ATTR_DATA {
                // lowest_vcn == 0 marks the fragment holding the total
                // real size; later fragments (attribute list) only add runs.
                if le64(rec0_ref, pos + 0x10) == Some(0) {
                    mft_size = le64(rec0_ref, pos + 0x30).unwrap_or(0);
                }
                if let Some(r) = parse_runs(rec0_ref, pos) {
                    extents.extend(r);
                }
            } else if kind == ATTR_BITMAP {
                bitmap_size = le64(rec0_ref, pos + 0x30).unwrap_or(0);
                if let Some(r) = parse_runs(rec0_ref, pos) {
                    bitmap_runs = r;
                }
            }
        } else if unnamed && kind == ATTR_BITMAP {
            if let Some(vlen) = le32(rec0_ref, pos + 0x10) {
                let voff = le16(rec0_ref, pos + 0x14).unwrap_or(0) as usize;
                let vs = pos + voff;
                let vlen = vlen as usize;
                if vs + vlen <= rec0_ref.len() {
                    bitmap = rec0_ref[vs..vs + vlen].to_vec();
                    bitmap_size = vlen as u64;
                }
            }
        }
        pos += length;
    }
    if extents.is_empty() || mft_size == 0 {
        let _ = unsafe { CloseHandle(h) };
        return Err(-5);
    }

    // $MFT's $BITMAP has one bit per record: set when the record is in
    // use. Read it through its own run list (physical extents).
    if !bitmap_runs.is_empty() && bitmap_size > 0 {
        let mut piece = vec![0u8; 3 * IO_ALIGN];
        for (lcn, clusters) in &bitmap_runs {
            if bitmap.len() as u64 >= bitmap_size {
                break;
            }
            let want = ((*clusters * boot.cluster) as usize)
                .min((bitmap_size - bitmap.len() as u64) as usize);
            // Sparse runs hold zeros: the records they cover are free.
            let Some(lcn_phys) = lcn.checked_mul(boot.cluster) else {
                bitmap.resize(bitmap.len() + want, 0);
                continue;
            };
            if piece.len() < want + 3 * IO_ALIGN {
                piece = vec![0u8; want + 3 * IO_ALIGN];
            }
            if !read_aligned(h, lcn_phys, want, &mut piece) {
                let _ = unsafe { CloseHandle(h) };
                return Err(-6);
            }
            bitmap.extend_from_slice(&piece[..want]);
        }
        bitmap.truncate(bitmap_size as usize);
    }

    let records = mft_size / boot.record_size as u64;
    if records == 0 || records > MAX_RECORDS {
        let _ = unsafe { CloseHandle(h) };
        return Err(-7);
    }
    if boot.cluster % boot.record_size as u64 != 0 {
        // Records can straddle pieces of $MFT when clusters are smaller
        // than records; such volumes are rare, so they use the folder walk.
        let _ = unsafe { CloseHandle(h) };
        return Err(-3);
    }
    Ok(Volume {
        handle: h,
        bpc: boot.cluster,
        bpr: boot.record_size,
        runs: extents,
        records,
        bitmap,
    })
}

// Cuts the table into slices of at most SLICE_BYTES along its extents,
// leaving out stretches of unused records (per $BITMAP). Without a
// bitmap, every record is read.
fn build_slices(v: &Volume) -> Vec<Slice> {
    let record_size = v.bpr as u64;
    let records = v.records as usize;
    let in_use = |record: usize| {
        v.bitmap
            .get(record / 8)
            .is_none_or(|byte| byte & (1 << (record % 8)) != 0)
    };
    let per_slice = (SLICE_BYTES / record_size) as usize;
    let mut slices = Vec::new();
    let mut next = 0usize;
    for (lcn, clusters) in &v.runs {
        let in_extent = (*clusters * v.bpc / record_size) as usize;
        let extent_start = next;
        let extent_end = (next + in_extent).min(records);
        let Some(lcn) = lcn.checked_mul(v.bpc) else {
            next = extent_end;
            continue;
        };
        while next < extent_end {
            while next < extent_end && !in_use(next) {
                next += 1;
            }
            if next >= extent_end {
                break;
            }
            let start = next;
            let mut last_used = next;
            while next < extent_end && next - start < per_slice {
                if in_use(next) {
                    last_used = next;
                } else if next - last_used >= SKIP_GAP {
                    break;
                }
                next += 1;
            }
            slices.push(Slice {
                first: start,
                count: last_used - start + 1,
                offset: Some(lcn + (start - extent_start) as u64 * record_size),
            });
        }
        next = extent_end;
    }
    slices
}

fn scan_volume(root: &str, prefixes: &[Vec<u16>], progress: ProgressFn) -> Result<Vec<u8>, i32> {
    let data = scan_volume_data(root, progress)?;
    emit_blob(&data, prefixes)
}

// Le o $MFT e devolve o VolumeData (entradas+pool+indices). Separado da
// emissao para o indice persistente poder gravar/reusar este estado sem
// repetir a leitura do disco.
pub(crate) fn scan_volume_data(root: &str, progress: ProgressFn) -> Result<VolumeData, i32> {
    let device = volume_device(root);
    let v = read_table(&device)?;

    // Pass 1: parse the used slices on worker threads. Each worker has
    // its own handle and hands over each slice as soon as it is parsed,
    // so slices never pile up.
    let slices = build_slices(&v);
    let total_records: u64 = slices.iter().map(|s| s.count as u64).sum();
    let claimed = AtomicUsize::new(0);
    // ~75% dos nucleos: o parse satura banda de memoria e CPU; reservar
    // folga evita que a UI/render do processo hospedeiro fiquem sem
    // scheduler (travada de ~2s durante o scan).
    let workers = std::thread::available_parallelism()
        .map(|n| (n.get() * 3) / 4)
        .unwrap_or(4)
        .max(2)
        .min(slices.len())
        .max(1);
    let mut entries: Vec<Entry> = Vec::new();
    let mut pool: Vec<u16> = Vec::new();
    let mut extensions: Vec<Extension> = Vec::new();
    let bpr = v.bpr;
    let per_slice_bytes = (SLICE_BYTES / bpr as u64) as usize * bpr;

    std::thread::scope(|scope| {
        let (sender, parts) = std::sync::mpsc::sync_channel::<Result<Part, i32>>(16);
        for _ in 0..workers {
            let (slices, claimed, sender, device) = (&slices, &claimed, sender.clone(), &device);
            scope.spawn(move || {
                let read = || -> Result<(), i32> {
                    let h = open_handle(device)?;
                    // Room for a slice plus alignment on both ends.
                    let mut buffer = vec![0u8; per_slice_bytes + 3 * IO_ALIGN];
                    loop {
                        let index = claimed.fetch_add(1, Ordering::Relaxed);
                        let Some(slice) = slices.get(index) else {
                            return Ok(());
                        };
                        let mut part = Part {
                            entries: Vec::new(),
                            names: Vec::new(),
                            extensions: Vec::new(),
                            records: 0,
                        };
                        if let Some(offset) = slice.offset {
                            let bytes = slice.count * bpr;
                            if bytes > per_slice_bytes {
                                return Err(-6);
                            }
                            if !read_aligned(h, offset, bytes, &mut buffer) {
                                return Err(-6);
                            }
                            for (index, record) in buffer[..bytes]
                                .chunks_exact_mut(bpr)
                                .enumerate()
                            {
                                if !fixup_record(record) {
                                    continue;
                                }
                                let rec = (slice.first + index) as u32;
                                if le64(record, 0x20)
                                    .map(|base| base & 0xFFFF_FFFF_FFFF != 0)
                                    .unwrap_or(false)
                                {
                                    if let Some(ext) = parse_extension(record) {
                                        part.extensions.push(ext);
                                    }
                                    continue;
                                }
                                if let Some((parent, size, last_write, flags, name_off, name_len, rank)) =
                                    parse_record(record, &mut part.names)
                                {
                                    part.entries.push(Entry {
                                        rec,
                                        parent,
                                        size,
                                        last_write,
                                        flags,
                                        name_off,
                                        name_len,
                                        name_rank: rank,
                                    });
                                }
                            }
                        }
                        part.records = slice.count as u64;
                        if sender.send(Ok(part)).is_err() {
                            return Ok(());
                        }
                    }
                };
                // A panic in a worker must not kill the host process:
                // report it as a volume failure instead.
                let result = std::panic::catch_unwind(std::panic::AssertUnwindSafe(read))
                    .unwrap_or(Err(-1000));
                if let Err(code) = result {
                    let _ = sender.send(Err(code));
                }
            });
        }
        drop(sender);
        let mut done_records: u64 = 0;
        let mut last_report = Instant::now() - std::time::Duration::from_millis(120);
        for part in parts {
            // Dropping the receiver on an error stops the other readers.
            let mut part = part?;
            done_records += part.records;
            if let Some(cb) = progress {
                if last_report.elapsed() >= std::time::Duration::from_millis(120) {
                    last_report = Instant::now();
                    unsafe { cb(0, done_records, total_records) };
                }
            }
            let base = pool.len() as u32;
            for e in &mut part.entries {
                e.name_off = e.name_off.wrapping_add(base);
            }
            pool.append(&mut part.names);
            entries.append(&mut part.entries);
            extensions.append(&mut part.extensions);
        }
        Ok::<_, i32>(())
    })?;
    if let Some(cb) = progress {
        unsafe { cb(1, 0, 0) }; // indexacao/emissao comecando
    }
    let _ = unsafe { CloseHandle(v.handle) };
    if entries.is_empty() {
        return Err(-7);
    }

    // Extension records contribute their best name/parent/timestamps
    // and size to their base record. Only the base records the
    // extensions point at are located, so no table-sized map is built.
    if !extensions.is_empty() {
        let wanted: std::collections::HashSet<u32> =
            extensions.iter().map(|x| x.base).collect();
        let mut base_pos: std::collections::HashMap<u32, usize> =
            std::collections::HashMap::with_capacity(wanted.len());
        for (i, e) in entries.iter().enumerate() {
            if wanted.contains(&e.rec) {
                base_pos.insert(e.rec, i);
            }
        }
        for ext in extensions {
            let pos = match base_pos.get(&ext.base) {
                Some(p) => *p,
                None => continue,
            };
            let e = match entries.get_mut(pos) {
                Some(e) => e,
                None => continue,
            };
            if ext.rank > e.name_rank
                && !ext.name.is_empty()
                && ext.name.len() <= u16::MAX as usize
                && pool.len() < u32::MAX as usize
            {
                e.name_rank = ext.rank;
                e.parent = ext.parent;
                e.last_write = ext.last_write;
                e.name_off = pool.len() as u32;
                e.name_len = ext.name.len() as u16;
                pool.extend_from_slice(&ext.name);
            }
            if let Some(size) = ext.size {
                e.size = size;
            }
        }
    }

    // Pass 2: CSR over (parent, rec) sorted entries.
    let record_count = v.records as usize;
    entries.sort_unstable_by(|a, b| a.parent.cmp(&b.parent).then(a.rec.cmp(&b.rec)));

    let mut cnt = vec![0u32; record_count + 1];
    for e in &entries {
        if e.parent < record_count as u32 {
            cnt[e.parent as usize + 1] += 1;
        }
    }
    let mut start = vec![0u32; record_count + 1];
    let mut acc: u64 = 0;
    for i in 0..record_count {
        acc += cnt[i] as u64;
        if acc > u32::MAX as u64 {
            return Err(-6);
        }
        start[i + 1] = acc as u32;
    }

    // rec_to_idx for prefix resolution and BFS.
    let mut rec_to_idx = vec![-1i32; record_count];
    for (i, e) in entries.iter().enumerate() {
        if (e.rec as usize) < record_count {
            rec_to_idx[e.rec as usize] = i as i32;
        }
    }

    // Fim da coleta: o estado fica pronto para gravar no indice em disco.
    Ok(VolumeData {
        entries,
        pool,
        start,
        rec_to_idx,
        record_count,
    })
}

// Resolve cada prefixo (componentes separados por '/') a partir do rec 5.
// Compartilhado por emit_blob e pelo cheque de invalidacao do indice
// persistente (mft_index precisa saber QUAIS recs sao as raizes varridas
// para decidir se uma mudanca do journal toca a area indexada).
pub(crate) fn resolve_prefix_recs(data: &VolumeData, prefixes: &[Vec<u16>]) -> Vec<u32> {
    let entries = &data.entries;
    let pool = &data.pool;
    let start = &data.start;
    let rec_to_idx = &data.rec_to_idx;
    let mut prefix_recs: Vec<u32> = Vec::with_capacity(prefixes.len());
    for comps in prefixes {
        let mut cur = ROOT_REC;
        let mut ok = true;
        for comp in split_wide(comps, b'/' as u16) {
            if comp.is_empty() {
                continue;
            }
            let idx = match rec_to_idx.get(cur as usize) {
                Some(v) => *v,
                None => {
                    ok = false;
                    break;
                }
            };
            if idx < 0 {
                ok = false;
                break;
            }
            let s = start[cur as usize + 1] as usize;
            let e = start[cur as usize + 2] as usize;
            let mut found: Option<u32> = None;
            for j in s..e {
                let c = match entries.get(j) {
                    Some(c) => c,
                    None => break,
                };
                if pool_name_eq(pool, c.name_off as usize, c.name_len as usize, comp) {
                    found = Some(c.rec);
                    break;
                }
            }
            match found {
                Some(r) => cur = r,
                None => {
                    ok = false;
                    break;
                }
            }
        }
        prefix_recs.push(if ok { cur } else { PREFIX_NOT_FOUND });
    }
    prefix_recs
}

// Resolve prefixos, faz BFS e emite o blob — opera sobre um VolumeData ja
// coletado (scan novo OU indice persistente carregado do disco).
pub(crate) fn emit_blob(data: &VolumeData, prefixes: &[Vec<u16>]) -> Result<Vec<u8>, i32> {
    let entries = &data.entries;
    let pool = &data.pool;
    let start = &data.start;
    let rec_to_idx = &data.rec_to_idx;
    let record_count = data.record_count;

    let prefix_recs = resolve_prefix_recs(data, prefixes);

    // BFS from each resolved prefix.
    let mut allowed = vec![false; record_count];
    let mut stack: Vec<u32> = Vec::new();
    for pr in &prefix_recs {
        if *pr != PREFIX_NOT_FOUND && (*pr as usize) < record_count && !allowed[*pr as usize] {
            allowed[*pr as usize] = true;
            stack.push(*pr);
        }
    }
    while let Some(r) = stack.pop() {
        let idx = match rec_to_idx.get(r as usize) {
            Some(v) => *v,
            None => continue,
        };
        if idx < 0 {
            continue;
        }
        let s = start[r as usize + 1] as usize;
        let e = start[r as usize + 2] as usize;
        for j in s..e {
            let c = match entries.get(j) {
                Some(c) => c,
                None => break,
            };
            if (c.rec as usize) < record_count && !allowed[c.rec as usize] {
                allowed[c.rec as usize] = true;
                stack.push(c.rec);
            }
        }
    }

    // Emit blob.
    let mut needed: u64 = 4 + 4 * prefix_recs.len() as u64;
    for e in entries.iter() {
        if e.rec >= 16 && allowed[e.rec as usize] {
            needed += 36 + 2 * e.name_len as u64;
        }
    }
    if needed > i32::MAX as u64 {
        return Err(-6);
    }
    let mut out: Vec<u8> = Vec::with_capacity(needed as usize);
    out.extend_from_slice(&(prefix_recs.len() as u32).to_le_bytes());
    for pr in &prefix_recs {
        out.extend_from_slice(&pr.to_le_bytes());
    }
    for e in entries.iter() {
        if e.rec >= 16 && allowed[e.rec as usize] {
            out.extend_from_slice(&(e.rec as u64).to_le_bytes());
            out.extend_from_slice(&(e.parent as u64).to_le_bytes());
            out.extend_from_slice(&e.size.to_le_bytes());
            out.extend_from_slice(&e.last_write.to_le_bytes());
            out.extend_from_slice(&e.flags.to_le_bytes());
            out.extend_from_slice(&e.name_len.to_le_bytes());
            let s = e.name_off as usize;
            for i in 0..e.name_len as usize {
                match pool.get(s + i) {
                    Some(u) => out.extend_from_slice(&u.to_le_bytes()),
                    None => return Err(-6),
                }
            }
        }
    }
    Ok(out)
}

fn split_wide<'a>(s: &'a [u16], sep: u16) -> Vec<&'a [u16]> {
    let mut parts = Vec::new();
    let mut start = 0usize;
    for (i, u) in s.iter().enumerate() {
        if *u == sep {
            if i > start {
                parts.push(&s[start..i]);
            }
            start = i + 1;
        }
    }
    if start < s.len() {
        parts.push(&s[start..]);
    }
    parts
}

// FFI export. prefixes: ';'-separated relative paths (empty entry =
// whole drive), '/' separators, NUL-terminated UTF-16.
//
// Este e o export que LÊ DADOS NAO-CONFIADOS (a $MFT do disco pode estar
// corrompida, com registros malformados). Com panic=abort no profile
// release, um panic aqui derrubava o processo .NET INTEIRO. Agora o corpo
// real esta em mft_scan_impl e o export so envolve em catch_unwind,
// devolvendo -1000 (erro) em vez de matar o KitLugia.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mft_scan_ffi(
    volume_root: *const u16,
    prefixes: *const u16,
    out_buf: *mut u8,
    out_capacity: i32,
) -> i32 {
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| unsafe {
        mft_scan_impl(volume_root, prefixes, out_buf, out_capacity, None, false)
    })) {
        Ok(v) => v,
        Err(_) => -1000,
    }
}

// Variante com callback de progresso (stage, done, total): stage 0 =
// registros MFT parseados, stage 1 = indexacao/emissao. O callback e
// opcional; passar NULL equivale a mft_scan_ffi.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mft_scan_ffi_ex(
    volume_root: *const u16,
    prefixes: *const u16,
    out_buf: *mut u8,
    out_capacity: i32,
    progress: Option<unsafe extern "C" fn(u32, u64, u64)>,
) -> i32 {
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| unsafe {
        mft_scan_impl(volume_root, prefixes, out_buf, out_capacity, progress, false)
    })) {
        Ok(v) => v,
        Err(_) => -1000,
    }
}

// Variante com indice persistente em disco: se o USN do volume nao mudou
// desde o ultimo scan, o blob sai do arquivo mapeado em <1s. Assinatura
// identica a mft_scan_ffi_ex — o C# prefere este export e cai no _ex se
// a DLL for antiga.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mft_scan_index_ffi(
    volume_root: *const u16,
    prefixes: *const u16,
    out_buf: *mut u8,
    out_capacity: i32,
    progress: Option<unsafe extern "C" fn(u32, u64, u64)>,
) -> i32 {
    match std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| unsafe {
        mft_scan_impl(volume_root, prefixes, out_buf, out_capacity, progress, true)
    })) {
        Ok(v) => v,
        Err(_) => -1000,
    }
}

unsafe fn mft_scan_impl(
    volume_root: *const u16,
    prefixes: *const u16,
    out_buf: *mut u8,
    out_capacity: i32,
    progress: ProgressFn,
    use_index: bool,
) -> i32 {
    if volume_root.is_null() || prefixes.is_null() || out_capacity < 0 {
        return -1;
    }
    if out_capacity == 0 && !out_buf.is_null() {
        return -1;
    }
    let root = match unsafe { read_wide(volume_root) } {
        Some(r) => match String::from_utf16(&r) {
            Ok(s) => s,
            Err(_) => return -1,
        },
        None => return -1,
    };
    let all = match unsafe { read_wide(prefixes) } {
        Some(p) => p,
        None => return -1,
    };
    let mut prefix_parts: Vec<Vec<u16>> = Vec::new();
    for raw in split_wide(&all, b';' as u16) {
        let mut comps: Vec<u16> = Vec::new();
        for c in split_wide(raw, b'/' as u16) {
            if !c.is_empty() {
                comps.extend_from_slice(c);
                comps.push(b'/' as u16);
            }
        }
        if !comps.is_empty() {
            comps.pop(); // trailing sep
        }
        prefix_parts.push(comps);
    }
    if prefix_parts.is_empty() {
        prefix_parts.push(Vec::new()); // root only
    }

    let blob = if use_index {
        crate::mft_index::scan_volume_indexed(&root, &prefix_parts, progress)
    } else {
        scan_volume(&root, &prefix_parts, progress)
    };
    let blob = match blob {
        Ok(b) => b,
        Err(code) => return code,
    };
    let needed = blob.len();
    if needed as i32 > out_capacity {
        return -(needed as i32 + 1000);
    }
    if needed > 0 {
        unsafe { std::ptr::copy_nonoverlapping(blob.as_ptr(), out_buf, needed) };
    }
    needed as i32
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scan_real_c() {
        let root: Vec<u16> = "C:".encode_utf16().chain(std::iter::once(0)).collect();
        let pre: Vec<u16> = "Users/Lugia;Program Files;Windows/System32/config"
            .encode_utf16()
            .chain(std::iter::once(0))
            .collect();
        let mut buf = vec![0u8; 512 * 1024 * 1024];
        let rc = unsafe { mft_scan_ffi(root.as_ptr(), pre.as_ptr(), buf.as_mut_ptr(), buf.len() as i32) };
        assert!(rc > 0, "rc={}", rc);
        let cnt = u32::from_le_bytes(buf[..4].try_into().unwrap());
        assert_eq!(cnt, 3);
        let mut pos = 4 + 4 * cnt as usize;
        let mut n: usize = 0;
        let mut dirs: usize = 0;
        let mut reps: usize = 0;
        while pos + 36 <= rc as usize {
            let fl = u16::from_le_bytes(buf[pos + 32..pos + 34].try_into().unwrap());
            let nlen = u16::from_le_bytes(buf[pos + 34..pos + 36].try_into().unwrap()) as usize;
            pos += 36 + nlen * 2;
            n += 1;
            if fl & F_DIR != 0 {
                dirs += 1;
            }
            if fl & F_REPARSE != 0 {
                reps += 1;
            }
        }
        println!("entries={} dirs={} reparse={} bytes={}", n, dirs, reps, rc);
        assert!(n > 1000, "expected many entries under the prefixes, got {}", n);
    }

    #[test]
    fn buffer_too_small() {
        let root: Vec<u16> = "C:".encode_utf16().chain(std::iter::once(0)).collect();
        let pre: Vec<u16> = "Users".encode_utf16().chain(std::iter::once(0)).collect();
        let mut buf = vec![0u8; 64];
        let rc = unsafe { mft_scan_ffi(root.as_ptr(), pre.as_ptr(), buf.as_mut_ptr(), 64) };
        assert!(rc <= -1000, "rc={}", rc);
        let needed = -rc - 1000;
        println!("needed={}", needed);
        assert!(needed > 64);
    }

    #[test]
    fn nonexistent_volume() {
        let root: Vec<u16> = "Q:".encode_utf16().chain(std::iter::once(0)).collect();
        let pre: Vec<u16> = "Users".encode_utf16().chain(std::iter::once(0)).collect();
        let mut buf = vec![0u8; 1024 * 1024];
        let rc = unsafe { mft_scan_ffi(root.as_ptr(), pre.as_ptr(), buf.as_mut_ptr(), buf.len() as i32) };
        assert!(rc < 0 && rc > -1000, "rc={}", rc);
        println!("rc={}", rc);
    }
}
