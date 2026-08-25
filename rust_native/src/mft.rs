// ── Native MFT Scanner (WizTree-style raw $MFT read) ──────────────
//
// Reads the raw $MFT of an NTFS volume directly (bypassing Win32
// enumeration) and returns, for a set of prefix paths, every
// descendant record (rec, parent, size, last-write, flags, name).
//
// Two passes:
//   1. Stream the MFT in 16 MiB chunks through the $DATA run list of
//      record 0 (per-record USA fixups; position-based record numbers).
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
const SECTOR: usize = 512;

const ATTR_ATTRIBUTE_LIST: u32 = 0x20;
const ATTR_FILE_NAME: u32 = 0x30;
const ATTR_DATA: u32 = 0x80;
const ATTR_REPARSE_POINT: u32 = 0xC0;
const ATTR_END: u32 = 0xFFFF_FFFF;

const FILE_ATTR_DIRECTORY: u32 = 0x1000_0000;
const FILE_ATTR_HIDDEN: u32 = 0x2;
const FILE_ATTR_SYSTEM: u32 = 0x4;
const FILE_ATTR_REPARSE: u32 = 0x400;

const F_DIR: u16 = 0x1;
const F_REPARSE: u16 = 0x2;
const F_HIDDEN_SYSTEM: u16 = 0x4;

const PREFIX_NOT_FOUND: u32 = 0xFFFF_FFFF;
const ROOT_REC: u32 = 5;

const CHUNK_SIZE: usize = 16 * 1024 * 1024;
const MAX_RECORDS: u64 = 64 * 1024 * 1024;
const MAX_BPR: usize = 16 * 1024 * 1024;
const MAX_WIDE_CHARS: usize = 32768;

struct Volume {
    handle: isize,
    bpc: u64,
    bpr: usize,
    runs: Vec<(u64, u64)>, // (lcn, len) in clusters; lcn == u64::MAX = sparse
    size: u64,             // MFT size in bytes
}

#[derive(Clone, Copy)]
struct Entry {
    rec: u32,
    parent: u32,
    size: u64,
    last_write: u64,
    flags: u16,
    name_off: u32,
    name_len: u16,
}

fn read_at(h: isize, off: u64, buf: &mut [u8]) -> bool {
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

// Reads record N (MFT logical space through the run list) and applies
// the USA fixup.
fn read_record(v: &Volume, rec: u64, buf: &mut [u8]) -> bool {
    if buf.len() < v.bpr || rec >= MAX_RECORDS {
        return false;
    }
    if !read_mft_logical(v, rec * v.bpr as u64, buf) {
        return false;
    }
    fixup_record(buf)
}

// Extracts the $DATA run list of the $MFT from record 0, merging
// attribute-list fragments when present.
fn mft_runs(v: &Volume, record0: &[u8]) -> Option<Vec<(u64, u64)>> {
    let mut first_data: Option<Vec<(u64, u64)>> = None;
    let mut fragments: Vec<(u64, Vec<(u64, u64)>)> = Vec::new(); // (vcn, runs)
    let mut attr_list: Option<Vec<u8>> = None;

    let mut pos = le16(record0, 0x14)? as usize;
    while pos + 8 <= record0.len() {
        let atype = le32(record0, pos)?;
        if atype == ATTR_END {
            break;
        }
        let alen = le32(record0, pos + 4)? as usize;
        if alen < 16 || pos + alen > record0.len() {
            break;
        }
        let non_res = record0.get(pos + 8).copied()? != 0;
        if non_res && atype == ATTR_DATA && first_data.is_none() {
            if let Some(runs) = parse_runs(record0, pos) {
                first_data = Some(runs);
            }
        } else if !non_res && atype == ATTR_ATTRIBUTE_LIST {
            let vlen = le32(record0, pos + 0x10)? as usize;
            let voff = le16(record0, pos + 0x14)? as usize;
            let vs = pos + voff;
            if vs + vlen <= record0.len() {
                attr_list = Some(record0[vs..vs + vlen].to_vec());
            }
        }
        pos += alen;
    }

    let list = match attr_list {
        Some(l) => l,
        None => {
            return first_data;
        }
    };
    let mut entries: Vec<(u64, u64, u32)> = Vec::new(); // (vcn, rec, attr_id)
    let mut p = 0usize;
    while p + 0x40 <= list.len() {
        let atype = le32(&list, p)?;
        let alen = le16(&list, p + 4)? as usize;
        if alen == 0 {
            break;
        }
        if atype == ATTR_DATA {
            let vcn = le64(&list, p + 0x08)?; // lowest_vcn
            let rref = le64(&list, p + 0x10)?; // file_reference
            let id = le32(&list, p + 0x18)?; // attribute_id
            entries.push((vcn, rref & 0xFFFF_FFFF_FFFF, id));
        }
        p += alen;
    }
    if entries.is_empty() {
        return first_data;
    }
    let single = entries.len() == 1;
    entries.sort_unstable_by_key(|e| e.0);
    for (_vcn, rec, id) in entries {
        let mut rb = vec![0u8; v.bpr];
        if !read_record(v, rec, &mut rb) {
            return None;
        }
        let mut pos = le16(&rb, 0x14)? as usize;
        while pos + 8 <= rb.len() {
            let atype = le32(&rb, pos)?;
            if atype == ATTR_END {
                break;
            }
            let alen = le32(&rb, pos + 4)? as usize;
            if alen < 16 || pos + alen > rb.len() {
                break;
            }
            if rb.get(pos + 8).copied()? != 0 && atype == ATTR_DATA {
                let aid = le16(&rb, pos + 0x0E)? as u32;
                if aid == id || single {
                    if let Some(r) = parse_runs(&rb, pos) {
                        fragments.push((_vcn, r));
                    }
                    break;
                }
            }
            pos += alen;
        }
    }
    if fragments.is_empty() {
        return None;
    }
    fragments.sort_unstable_by_key(|f| f.0);
    let mut merged: Vec<(u64, u64)> = Vec::new();
    for (_, r) in &fragments {
        merged.extend_from_slice(r);
    }
    Some(merged)
}

// Parses one fixed-up record buffer into compact data. Name is pushed
// into the shared pool; returns (parent, size, last_write, flags,
// name_off, name_len). Returns None to skip the record.
fn parse_record(buf: &[u8], pool: &mut Vec<u16>) -> Option<(u32, u64, u64, u16, u32, u16)> {
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
    let mut best: Option<(u32, u64, u64, u16, u32, u16)> = None;
    let mut best_ns = -1i32;
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
                let ns = buf.get(vs + 0x41).copied()? as i32;
                let nlen = buf.get(vs + 0x40).copied()? as usize;
                let prio = match ns {
                    3 => 4, // Win32AndDos
                    1 => 3, // Win32
                    0 => 2, // Posix
                    _ => 1, // Dos
                };
                if prio > best_ns && vs + 0x42 + nlen * 2 <= buf.len() {
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
                    best_ns = prio;
                    best = Some((parent_u32, size, last_write, fl, name_off, nlen as u16));
                }
            }
        }
        pos += alen;
    }
    if let Some((parent, size, last_write, mut fl, name_off, name_len)) = best {
        if has_reparse {
            fl |= F_REPARSE;
        }
        Some((parent, size, last_write, fl, name_off, name_len))
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

// Logical (MFT byte space) read through the run list, handling sparse
// runs and run boundaries. Chunks are bpr-aligned so records never
// straddle read boundaries.
fn read_mft_logical(v: &Volume, off: u64, buf: &mut [u8]) -> bool {
    let mut done = 0usize;
    while done < buf.len() {
        let abs = off + done as u64;
        let cluster = abs / v.bpc;
        let within = (abs % v.bpc) as usize;
        let mut found = false;
        let mut run_start = 0u64;
        for (lcn, len) in &v.runs {
            let run_end = run_start + len;
            if cluster >= run_start && cluster < run_end {
                let run_bytes = run_end * v.bpc;
                let avail = (run_bytes - abs) as usize;
                let n = avail.min(buf.len() - done);
                if *lcn == u64::MAX {
                    for b in buf[done..done + n].iter_mut() {
                        *b = 0;
                    }
                } else {
                    let phys = *lcn + (cluster - run_start);
                    let poff = phys * v.bpc + within as u64;
                    let ok = read_at(v.handle, poff, &mut buf[done..done + n]);
                    if !ok {
                        return false;
                    }
                }
                done += n;
                found = true;
                break;
            }
            run_start = run_end;
        }
        if !found {
            return false;
        }
    }
    true
}

fn open_volume(root: &str) -> Result<Volume, i32> {
    let trimmed = root.trim_end_matches(['\\', '/']);
    let device = if trimmed.starts_with("\\\\") {
        trimmed.to_string()
    } else {
        format!("\\\\.\\{}", trimmed)
    };
    let w: Vec<u16> = device.encode_utf16().chain(std::iter::once(0)).collect();
    let h = unsafe {
        CreateFileW(
            w.as_ptr(),
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            ptr::null_mut(),
            OPEN_EXISTING,
            0,
            0,
        )
    };
    if h == INVALID_HANDLE {
        return Err(-2);
    }
    let mut boot = [0u8; SECTOR];
    if !read_at(h, 0, &mut boot) {
        let _ = unsafe { CloseHandle(h) };
        return Err(-6);
    }
    if &boot[3..11] != b"NTFS    " {
        let _ = unsafe { CloseHandle(h) };
        return Err(-3);
    }
    let bps = le16(&boot, 0x0B).unwrap_or(0) as u64;
    let spc = boot.get(0x0D).copied().unwrap_or(0) as u64;
    if bps == 0 || spc == 0 || bps > 4096 || spc > 255 {
        let _ = unsafe { CloseHandle(h) };
        return Err(-4);
    }
    let bpc = bps * spc;
    let mft_lcn = le64(&boot, 0x30).unwrap_or(0);
    let bpr_raw = boot.get(0x40).copied().unwrap_or(0) as i8;
    let bpr = if bpr_raw < 0 {
        1usize << (-bpr_raw as u32)
    } else {
        (bpr_raw as usize) * (bpc as usize)
    };
    if bpr < SECTOR || bpr > MAX_BPR || bpr % SECTOR != 0 || bpc == 0 || mft_lcn == 0 {
        let _ = unsafe { CloseHandle(h) };
        return Err(-4);
    }

    let mut rec0 = vec![0u8; bpr];
    let ok_read = read_at(h, mft_lcn * bpc, &mut rec0);
    let ok_fix = ok_read && fixup_record(&mut rec0);
    if !ok_read || !ok_fix {
        let _ = unsafe { CloseHandle(h) };
        return Err(-5);
    }
    let runs = match mft_runs(&Volume { handle: h, bpc, bpr, runs: Vec::new(), size: 0 }, &rec0) {
        Some(r) => r,
        None => {
            let _ = unsafe { CloseHandle(h) };
            return Err(-5);
        }
    };
    let mut size: u64 = 0;
    for (lcn, len) in &runs {
        if *lcn != u64::MAX {
            let add = match len.checked_mul(bpc) {
                Some(a) => a,
                None => {
                    let _ = unsafe { CloseHandle(h) };
                    return Err(-6);
                }
            };
            size = match size.checked_add(add) {
                Some(s) => s,
                None => {
                    let _ = unsafe { CloseHandle(h) };
                    return Err(-6);
                }
            };
        }
    }
    if size == 0 || size / bpr as u64 == 0 {
        let _ = unsafe { CloseHandle(h) };
        return Err(-7);
    }
    if size / bpr as u64 > MAX_RECORDS {
        let _ = unsafe { CloseHandle(h) };
        return Err(-6);
    }
    Ok(Volume { handle: h, bpc, bpr, runs, size })
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

fn scan_volume(root: &str, prefixes: &[Vec<u16>]) -> Result<Vec<u8>, i32> {
    let v = open_volume(root)?;

    // Pass 1: stream the MFT.
    let mut entries: Vec<Entry> = Vec::new();
    let mut pool: Vec<u16> = Vec::new();
    let mut chunk = vec![0u8; CHUNK_SIZE];
    let mut off: u64 = 0;
    while off < v.size {
        let n = ((v.size - off) as usize).min(CHUNK_SIZE);
        if !read_mft_logical(&v, off, &mut chunk[..n]) {
            let _ = unsafe { CloseHandle(v.handle) };
            return Err(-6);
        }
        let rec_start = (off / v.bpr as u64) as u32;
        let count = n / v.bpr;
        for i in 0..count {
            let rec = rec_start + i as u32;
            let s = i * v.bpr;
            let e = s + v.bpr;
            if !fixup_record(&mut chunk[s..e]) {
                continue;
            }
            let parsed = match parse_record(&chunk[s..e], &mut pool) {
                Some(p) => p,
                None => continue,
            };
            let (parent, size, last_write, flags, name_off, name_len) = parsed;
            entries.push(Entry {
                rec,
                parent,
                size,
                last_write,
                flags,
                name_off,
                name_len,
            });
        }
        off += n as u64;
    }
    let _ = unsafe { CloseHandle(v.handle) };
    if entries.is_empty() {
        return Err(-7);
    }

    // Pass 2: CSR over (parent, rec) sorted entries.
    let record_count = (v.size / v.bpr as u64) as usize;
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

    // Resolve prefixes from rec 5.
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
                if pool_name_eq(&pool, c.name_off as usize, c.name_len as usize, comp) {
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
    for e in &entries {
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
    for e in &entries {
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

// FFI export. prefixes: ';'-separated relative paths (empty entry =
// whole drive), '/' separators, NUL-terminated UTF-16.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn mft_scan_ffi(
    volume_root: *const u16,
    prefixes: *const u16,
    out_buf: *mut u8,
    out_capacity: i32,
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

    let blob = match scan_volume(&root, &prefix_parts) {
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