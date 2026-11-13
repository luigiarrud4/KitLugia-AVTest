//! Indice persistente do MFT em disco + freshness via USN Journal
//! (o mesmo mecanismo do Everything):
//!
//! - 1o scan: le o $MFT inteiro e grava entries+pool de nomes em
//!   `%LOCALAPPDATA%\KitLugia\mftidx\<volume>.klmft` (~600 MB no C:).
//!   O arquivo e' so' a serializacao do VolumeData — NAO e' cache em
//!   RAM; na leitura e' memory-mapped.
//! - Scans seguintes: FSCTL_QUERY_USN_JOURNAL devolve o estado do
//!   journal; se o NextUsn nao avancou, nada mudou -> emite do indice.
//!   Se avancou, le o DELTA do journal (FSCTL_READ_USN_JOURNAL) e so'
//!   re-escaneia quando alguma mudanca toca a cadeia de pais de um dos
//!   prefixos varridos — cambios fora da area (ex.: Windows, outros
//!   diretorios) nao invalidam o indice.
//! - Journal indisponivel/estourado -> scan completo normal (nunca
//!   devolve resultado velho por engano).
//!
//! Formato do arquivo:
//!   header 64B: magic u64 | version u32 | rsv u32 | record_count u64
//!               | entries_count u64 | pool_len u64 | covered_usn u64
//!               | journal_id u64 | rsv 8B
//!   entries: entries_count x 32B (rec u32, parent u32, size u64,
//!            last_write u64, flags u16, name_off u32, name_len u16)
//!   pool:    pool_len x u16 LE (UTF-16, mesmos offsets de name_off)

use std::collections::HashSet;
use std::io::Write;
use std::path::{Path, PathBuf};
use std::ptr;

use crate::feather_index::mapped::MappedFile;
use crate::mft::{
    emit_blob, open_handle, resolve_prefix_recs, scan_volume_data, volume_device, Entry,
    ProgressFn, VolumeData,
};

const INDEX_MAGIC: u64 = 0x4B4C_4D49_4458_5231; // "KLMIDXR1"
const INDEX_VERSION: u32 = 1;
const HEADER_SIZE: usize = 64;
const ENTRY_SIZE: usize = 32;
const FSCTL_QUERY_USN_JOURNAL: u32 = 0x0009_00F4;
const FSCTL_READ_USN_JOURNAL: u32 = 0x0009_00BB;
const FRN_MASK: u64 = 0x0000_FFFF_FFFF_FFFF; // low 48 bits = MFT record
const JOURNAL_BUF: usize = 4 * 1024 * 1024;
const MAX_DELTA_RECORDS: usize = 4 * 1024 * 1024; // delta gigante -> rescan

unsafe extern "C" {
    fn DeviceIoControl(
        h: isize,
        code: u32,
        inbuf: *const u8,
        inlen: u32,
        outbuf: *mut u8,
        outlen: u32,
        ret: *mut u32,
        ov: *mut u8,
    ) -> i32;
    fn CloseHandle(h: isize) -> i32;
}

// Normaliza a raiz do volume antes de virar nome de arquivo: "C:\\" e "C:"
// precisam mapear para o MESMO indice, senao cada caller (ScanAllVolumes
// passa "C:", FolderSizeMft passa "C:\\") gera um .klmft duplicado de ~600 MB.
fn normalize_root(root: &str) -> String {
    root.trim_end_matches(['\\', '/']).to_string()
}

fn index_dir() -> Option<PathBuf> {
    let base = std::env::var_os("LOCALAPPDATA")?;
    Some(PathBuf::from(base).join("KitLugia").join("mftidx"))
}

fn index_path_in(dir: &Path, root: &str) -> PathBuf {
    let safe: String = normalize_root(root)
        .chars()
        .map(|c| {
            if c.is_ascii_alphanumeric() {
                c
            } else {
                '_'
            }
        })
        .collect();
    dir.join(format!("{safe}.klmft"))
}

// Remove indices legados que foram gravados com uma variante diferente da
// raiz (ex.: "C__.klmft" ao lado de "C_.klmft") para nao deixar duplicatas
// orfas ocupando centenas de MB. So' apaga arquivos cujo nome saneado bate
// com uma das variantes da raiz atual e que nao seja o arquivo canonico.
fn cleanup_legacy_index_files_in(dir: &Path, root: &str, canonical: &Path) {
    let Ok(entries) = std::fs::read_dir(dir) else {
        return;
    };
    let canonical_name = match canonical.file_name().and_then(|n| n.to_str()) {
        Some(n) => n.to_string(),
        None => return,
    };
    let trimmed = normalize_root(root);
    let variants = [root.to_string(), trimmed.clone(), format!("{}\\", trimmed)];
    let mut legacy_names: HashSet<String> = HashSet::new();
    for v in &variants {
        let safe: String = v
            .chars()
            .map(|c| if c.is_ascii_alphanumeric() { c } else { '_' })
            .collect();
        legacy_names.insert(format!("{safe}.klmft"));
    }
    for entry in entries.flatten() {
        let name = entry.file_name();
        let Some(name_str) = name.to_str() else { continue };
        if !name_str.ends_with(".klmft") || name_str == canonical_name {
            continue;
        }
        if legacy_names.contains(name_str) {
            let _ = std::fs::remove_file(entry.path());
        }
    }
}

struct JournalState {
    id: u64,
    lowest_valid: u64,
    next_usn: u64,
}

// FSCTL_QUERY_USN_JOURNAL: USN_JOURNAL_DATA = 7 x DWORDLONG.
// Retorna None se o volume nao tiver journal ou o ioctl falhar.
fn query_usn_journal(h: isize) -> Option<JournalState> {
    let mut buf = [0u8; 56];
    let mut ret: u32 = 0;
    let ok = unsafe {
        DeviceIoControl(
            h,
            FSCTL_QUERY_USN_JOURNAL,
            ptr::null(),
            0,
            buf.as_mut_ptr(),
            buf.len() as u32,
            &mut ret,
            ptr::null_mut(),
        )
    };
    if ok == 0 || ret < 40 {
        return None;
    }
    Some(JournalState {
        id: le_u64(&buf, 0),
        lowest_valid: le_u64(&buf, 24),
        next_usn: le_u64(&buf, 16),
    })
}

// Le os registros do journal entre start_usn (exclusivo) e end_usn
// (inclusivo). Devolve (frn_rec, parent_rec) por registro — so' os
// campos necessarios para a invalidacao. None = falha de leitura.
fn read_journal_delta(h: isize, journal_id: u64, start: u64, end: u64) -> Option<Vec<(u64, u64)>> {
    let mut out = Vec::new();
    let mut cursor = start;
    let mut buffer = vec![0u8; JOURNAL_BUF];

    while cursor < end {
        // READ_USN_JOURNAL_DATA: StartUsn u64 | ReasonMask u32 |
        // ReturnOnlyOnClose u32 | Timeout u64 | BytesToWaitFor u64 |
        // UsnJournalID u64 = 40 bytes.
        let mut input = [0u8; 40];
        input[0..8].copy_from_slice(&cursor.to_le_bytes());
        input[8..12].copy_from_slice(&0xFFFF_FFFFu32.to_le_bytes()); // todas as reasons
        input[32..40].copy_from_slice(&journal_id.to_le_bytes());

        let mut ret: u32 = 0;
        let ok = unsafe {
            DeviceIoControl(
                h,
                FSCTL_READ_USN_JOURNAL,
                input.as_ptr(),
                input.len() as u32,
                buffer.as_mut_ptr(),
                buffer.len() as u32,
                &mut ret,
                ptr::null_mut(),
            )
        };
        if ok == 0 || ret < 8 {
            return None;
        }
        let buf = &buffer[..ret as usize];
        let next = le_u64(buf, 0);
        if next == cursor {
            break; // sem progresso — fim do journal
        }

        // Registros comecam no offset 8. RecordLength@0, Major@4.
        let mut off = 8usize;
        while off + 8 <= buf.len() {
            let len = le_u32(buf, off) as usize;
            if len < 60 || off + len > buf.len() {
                break;
            }
            let major = le_u16(buf, off + 4);
            let (frn, parent) = match major {
                2 => (le_u64(buf, off + 8), le_u64(buf, off + 16)),
                3 | 4 => (le_u64(buf, off + 8), le_u64(buf, off + 24)),
                _ => {
                    off += len;
                    continue;
                }
            };
            out.push((frn & FRN_MASK, parent & FRN_MASK));
            if out.len() > MAX_DELTA_RECORDS {
                return None; // delta gigante: mais barato re-escanear
            }
            off += len;
        }
        cursor = next;
        if next >= end {
            break;
        }
    }
    Some(out)
}

fn le_u16(b: &[u8], off: usize) -> u16 {
    u16::from_le_bytes([b[off], b[off + 1]])
}
fn le_u32(b: &[u8], off: usize) -> u32 {
    u32::from_le_bytes(b[off..off + 4].try_into().unwrap_or([0; 4]))
}
fn le_u64(b: &[u8], off: usize) -> u64 {
    u64::from_le_bytes(b[off..off + 8].try_into().unwrap_or([0; 8]))
}

// Reconstroi start (CSR) e rec_to_idx — mesma logica do fim de
// scan_volume_data, so' que sobre dados vindos do disco.
fn rebuild_indices(entries: &[Entry], record_count: usize) -> Option<(Vec<u32>, Vec<i32>)> {
    if record_count == 0 || record_count > (u32::MAX as usize) {
        return None;
    }
    let mut cnt = vec![0u32; record_count + 1];
    for e in entries {
        let p = e.parent as usize;
        if p < record_count {
            cnt[p + 1] += 1;
        }
    }
    let mut start = vec![0u32; record_count + 1];
    let mut acc: u64 = 0;
    for i in 0..record_count {
        acc += cnt[i] as u64;
        if acc > u32::MAX as u64 {
            return None;
        }
        start[i + 1] = acc as u32;
    }
    let mut rec_to_idx = vec![-1i32; record_count];
    for (i, e) in entries.iter().enumerate() {
        if (e.rec as usize) < record_count {
            rec_to_idx[e.rec as usize] = i as i32;
        }
    }
    Some((start, rec_to_idx))
}

struct IndexHeader {
    covered_usn: u64,
    journal_id: u64,
    record_count: usize,
    entries_count: usize,
    pool_len: usize,
}

fn read_header(map: &MappedFile) -> Option<IndexHeader> {
    let b = map.as_slice();
    if b.len() < HEADER_SIZE {
        return None;
    }
    if le_u64(b, 0) != INDEX_MAGIC || le_u32(b, 8) != INDEX_VERSION {
        return None;
    }
    let record_count = le_u64(b, 16) as usize;
    let entries_count = le_u64(b, 24) as usize;
    let pool_len = le_u64(b, 32) as usize;
    let covered_usn = le_u64(b, 40);
    let journal_id = le_u64(b, 48);
    let entries_off = HEADER_SIZE;
    let pool_off = entries_off.checked_add(entries_count.checked_mul(ENTRY_SIZE)?)?;
    let expected = pool_off.checked_add(pool_len.checked_mul(2)?)?;
    if b.len() < expected {
        return None;
    }
    if entries_count > u32::MAX as usize || pool_len > i32::MAX as usize {
        return None;
    }
    Some(IndexHeader {
        covered_usn,
        journal_id,
        record_count,
        entries_count,
        pool_len,
    })
}

// Le o arquivo inteiro para Vecs nativos. O mmap evita re-ler do disco,
// mas o VolumeData e' consumido como Vec de qualquer forma.
fn load_index(path: &Path) -> Option<(IndexHeader, VolumeData)> {
    let map = MappedFile::open(path).ok()?;
    let header = read_header(&map)?;
    let b = map.as_slice();
    let entries_off = HEADER_SIZE;
    let pool_off = entries_off + header.entries_count * ENTRY_SIZE;

    let mut entries = Vec::with_capacity(header.entries_count);
    for i in 0..header.entries_count {
        let o = entries_off + i * ENTRY_SIZE;
        entries.push(Entry {
            rec: le_u32(b, o),
            parent: le_u32(b, o + 4),
            size: le_u64(b, o + 8),
            last_write: le_u64(b, o + 16),
            flags: le_u16(b, o + 24),
            name_off: le_u32(b, o + 26),
            name_len: le_u16(b, o + 30),
            name_rank: 0,
        });
    }
    let mut pool = Vec::with_capacity(header.pool_len);
    for i in 0..header.pool_len {
        pool.push(le_u16(b, pool_off + i * 2));
    }
    let (start, rec_to_idx) = rebuild_indices(&entries, header.record_count)?;
    let record_count = header.record_count;
    Some((
        header,
        VolumeData {
            entries,
            pool,
            start,
            rec_to_idx,
            record_count,
        },
    ))
}

fn write_index(path: &Path, data: &VolumeData, journal: &JournalState) -> std::io::Result<()> {
    if let Some(parent) = path.parent() {
        std::fs::create_dir_all(parent)?;
    }
    let tmp = path.with_extension("tmp");
    // BufWriter obrigatorio: sem ele cada write_all(32B) virava um
    // WriteFile — 4 milhoes de syscalls = ~50s so' pra gravar o indice.
    let file = std::fs::File::create(&tmp)?;
    let mut f = std::io::BufWriter::with_capacity(1 << 20, file);

    let mut header = [0u8; HEADER_SIZE];
    header[0..8].copy_from_slice(&INDEX_MAGIC.to_le_bytes());
    header[8..12].copy_from_slice(&INDEX_VERSION.to_le_bytes());
    header[16..24].copy_from_slice(&(data.record_count as u64).to_le_bytes());
    header[24..32].copy_from_slice(&(data.entries.len() as u64).to_le_bytes());
    header[32..40].copy_from_slice(&(data.pool.len() as u64).to_le_bytes());
    header[40..48].copy_from_slice(&journal.next_usn.to_le_bytes());
    header[48..56].copy_from_slice(&journal.id.to_le_bytes());
    f.write_all(&header)?;

    let mut rec = [0u8; ENTRY_SIZE];
    for e in &data.entries {
        rec[0..4].copy_from_slice(&e.rec.to_le_bytes());
        rec[4..8].copy_from_slice(&e.parent.to_le_bytes());
        rec[8..16].copy_from_slice(&e.size.to_le_bytes());
        rec[16..24].copy_from_slice(&e.last_write.to_le_bytes());
        rec[24..26].copy_from_slice(&e.flags.to_le_bytes());
        rec[26..30].copy_from_slice(&e.name_off.to_le_bytes());
        rec[30..32].copy_from_slice(&e.name_len.to_le_bytes());
        f.write_all(&rec)?;
    }
    // Alvos Windows sao little-endian: o pool pode ir como bytes crus.
    if !data.pool.is_empty() {
        let bytes =
            unsafe { std::slice::from_raw_parts(data.pool.as_ptr() as *const u8, data.pool.len() * 2) };
        f.write_all(bytes)?;
    }
    f.flush()?;
    f.get_ref().sync_all()?;
    drop(f);
    std::fs::rename(&tmp, path)?;
    Ok(())
}

// Anda a cadeia de pais de `start_rec` (record number) subindo ate a
// raiz. Devolve true se algum ancestral (ou o proprio record) esta num
// dos prefix_recs — ou seja, a mudanca aconteceu dentro da area varrida.
fn ancestors_hit_prefix(data: &VolumeData, start_rec: u64, prefix_set: &HashSet<u32>) -> bool {
    let mut cur = start_rec & FRN_MASK;
    for _ in 0..256 {
        if cur >= data.record_count as u64 {
            return false;
        }
        if prefix_set.contains(&(cur as u32)) {
            return true;
        }
        let idx = data.rec_to_idx[cur as usize];
        if idx < 0 {
            return false;
        }
        let e = &data.entries[idx as usize];
        let parent = e.parent as u64;
        if parent == cur {
            return false; // self-parent (raiz) — ja foi checado acima
        }
        cur = parent;
    }
    false
}

/// Scan com indice persistente. Qualquer duvida (journal off, delta
/// grande, IO falho, indice corrompido) cai no scan completo — o pior
/// caso e' o comportamento antigo, nunca resultado velho.
pub(crate) fn scan_volume_indexed(
    root: &str,
    prefixes: &[Vec<u16>],
    progress: ProgressFn,
) -> Result<Vec<u8>, i32> {
    scan_volume_indexed_at(root, prefixes, progress, None)
}

/// `dir_override` existe para os testes: aponta os `.klmft` para um diretorio
/// temporario em vez de `%LOCALAPPDATA%\KitLugia\mftidx`.
fn scan_volume_indexed_at(
    root: &str,
    prefixes: &[Vec<u16>],
    progress: ProgressFn,
    dir_override: Option<&Path>,
) -> Result<Vec<u8>, i32> {
    let device = volume_device(root);
    let h = match open_handle(&device) {
        Ok(h) => h,
        Err(code) => return Err(code),
    };
    let journal = query_usn_journal(h);
    unsafe {
        CloseHandle(h);
    }
    let Some(journal) = journal else {
        // Sem journal: nao da' pra provar frescor -> scan puro.
        let data = scan_volume_data(root, progress)?;
        return emit_blob(&data, prefixes);
    };
    let Some(dir) = dir_override.map(|d| d.to_path_buf()).or_else(index_dir) else {
        let data = scan_volume_data(root, progress)?;
        return emit_blob(&data, prefixes);
    };
    let path = index_path_in(&dir, root);

    // Descarta indices orfos de variantes antigas da raiz (ex.: C__.klmft
    // convivendo com C_.klmft).
    cleanup_legacy_index_files_in(&dir, root, &path);

    // Carrega UMA vez: um arquivo que nao satisfaz o proprio cabecalho (o
    // D_.klmft antigo, por exemplo) volta None e e' apagado em vez de ficar
    // ocupando disco e sendo rejeitado em todo scan.
    let loaded = load_index(&path);
    if loaded.is_none() {
        if let Ok(meta) = std::fs::metadata(&path) {
            if meta.len() > 0 {
                let _ = std::fs::remove_file(&path);
            }
        }
    }

    if let Some((header, data)) = loaded {
        if header.journal_id == journal.id {
            let fresh = if header.covered_usn >= journal.next_usn {
                true // nada foi escrito desde o ultimo scan
            } else if header.covered_usn < journal.lowest_valid {
                false // journal ja cobriu o periodo — delta indisponivel
            } else {
                // Delta disponivel: le as mudancas e verifica se tocam
                // as areas dos prefixos.
                let h2 = match open_handle(&device) {
                    Ok(h) => h,
                    Err(_) => isize::MAX, // forca rescan abaixo
                };
                let delta = if h2 != isize::MAX {
                    let d = read_journal_delta(h2, journal.id, header.covered_usn, journal.next_usn);
                    unsafe {
                        CloseHandle(h2);
                    }
                    d
                } else {
                    None
                };
                match delta {
                    Some(recs) => {
                        let prefix_recs = resolve_prefix_recs(&data, prefixes);
                        let prefix_set: HashSet<u32> = prefix_recs
                            .iter()
                            .copied()
                            .filter(|r| *r != crate::mft::PREFIX_NOT_FOUND)
                            .collect();
                        let mut touched = false;
                        for (frn, parent) in recs {
                            if ancestors_hit_prefix(&data, frn, &prefix_set)
                                || ancestors_hit_prefix(&data, parent, &prefix_set)
                            {
                                touched = true;
                                break;
                            }
                        }
                        !touched
                    }
                    None => false,
                }
            };
            if fresh {
                if let Some(cb) = progress {
                    unsafe { cb(1, 0, 0) };
                }
                return emit_blob(&data, prefixes);
            }
        }
    }

    // Miss/stale: scan completo e regrava o indice.
    let data = scan_volume_data(root, progress)?;
    let blob = emit_blob(&data, prefixes)?;
    let _ = write_index(&path, &data, &journal);
    Ok(blob)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashSet;

    fn entry(rec: u32, parent: u32) -> Entry {
        Entry { rec, parent, size: 0, last_write: 0, flags: 0, name_off: 0, name_len: 0, name_rank: 0 }
    }

    fn data(entries: Vec<Entry>, record_count: usize) -> VolumeData {
        let (start, rec_to_idx) = rebuild_indices(&entries, record_count).unwrap();
        VolumeData { entries, pool: Vec::new(), start, rec_to_idx, record_count }
    }

    #[test]
    fn ancestors_hit_walks_parent_chain() {
        let d = data(vec![entry(6, 5), entry(7, 6), entry(8, 5)], 16);
        let set: HashSet<u32> = [6u32].into_iter().collect();
        assert!(ancestors_hit_prefix(&d, 7, &set));
        assert!(ancestors_hit_prefix(&d, 6, &set));
        assert!(!ancestors_hit_prefix(&d, 8, &set));
    }

    #[test]
    fn ancestors_miss_outside_tree() {
        let d = data(vec![entry(6, 5), entry(7, 6)], 16);
        let set: HashSet<u32> = [6u32].into_iter().collect();
        assert!(!ancestors_hit_prefix(&d, 5, &set));
        assert!(!ancestors_hit_prefix(&d, 100, &set));
    }

    #[test]
    fn journal_delta_reads_real_c() {
        let device = crate::mft::volume_device("C:\\");
        let h = crate::mft::open_handle(&device).expect("open C:");
        let st = query_usn_journal(h).expect("journal query");
        eprintln!("journal id={:x} next={:x} lowest={:x}", st.id, st.next_usn, st.lowest_valid);
        let start = st.next_usn.saturating_sub(8192);
        let delta = read_journal_delta(h, st.id, start, st.next_usn);
        unsafe { CloseHandle(h) };
        let delta = delta.expect("delta read");
        eprintln!("delta records: {}", delta.len());
        assert!(!delta.is_empty() || st.next_usn == start);
    }

    fn temp_dir(name: &str) -> PathBuf {
        let dir = std::env::temp_dir().join(format!(
            "kl-mftidx-{}-{}-{}",
            name,
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos()
        ));
        std::fs::create_dir_all(&dir).expect("create temp dir");
        dir
    }

    // "C:" (ScanAllVolumes) e "C:\\" (FolderSizeMft/GetPathRoot) precisam cair no
    // MESMO .klmft — antes geravam C_.klmft e C__.klmft, ~600 MB cada.
    #[test]
    fn both_root_spellings_point_at_one_index_file() {
        let dir = temp_dir("naming");
        let without_sep = index_path_in(&dir, "C:");
        let with_sep = index_path_in(&dir, "C:\\");
        assert_eq!(without_sep, with_sep);
        assert_eq!(
            without_sep.file_name().and_then(|n| n.to_str()),
            Some("C_.klmft")
        );
        let _ = std::fs::remove_dir_all(&dir);
    }

    // Estado deixado pelo bug: o canonico do C: convivendo com o orfao da
    // grafia com barra. A limpeza roda para as duas grafias e no fim so' o
    // canonico fica (e o indice de outra unidade nao e' tocado).
    #[test]
    fn cleanup_keeps_only_the_canonical_index_file() {
        let dir = temp_dir("cleanup");
        for name in ["C_.klmft", "C__.klmft", "D_.klmft"] {
            std::fs::write(dir.join(name), b"x").expect("write fixture");
        }
        let mut left: Vec<String> = Vec::new();
        for root in ["C:", "C:\\"] {
            let canonical = index_path_in(&dir, root);
            cleanup_legacy_index_files_in(&dir, root, &canonical);
            left = std::fs::read_dir(&dir)
                .expect("read_dir")
                .flatten()
                .map(|e| e.file_name().to_string_lossy().into_owned())
                .collect();
            left.sort();
        }
        assert_eq!(left, vec!["C_.klmft".to_string(), "D_.klmft".to_string()]);
        let _ = std::fs::remove_dir_all(&dir);
    }

    // End-to-end real: le o $MFT do C: e grava o indice num diretorio
    // temporario. Duas chamadas com grafias diferentes da raiz precisam
    // deixar UM unico .klmft. Fora do default porque le o volume de verdade
    // (lento e precisa de admin): rode com `cargo test -- --ignored`.
    #[test]
    #[ignore = "le o $MFT real do C: (lento, precisa de admin)"]
    fn real_c_scan_leaves_one_index_file() {
        let dir = temp_dir("real-c");
        let prefixes: Vec<Vec<u16>> = vec!["Windows/System32/config".encode_utf16().collect()];
        for root in ["C:", "C:\\"] {
            scan_volume_indexed_at(root, &prefixes, None, Some(&dir)).expect("scan C:");
        }
        let mut names: Vec<String> = std::fs::read_dir(&dir)
            .expect("read_dir")
            .flatten()
            .map(|e| e.file_name().to_string_lossy().into_owned())
            .filter(|n| n.ends_with(".klmft"))
            .collect();
        names.sort();
        assert_eq!(names, vec!["C_.klmft".to_string()]);
        let _ = std::fs::remove_dir_all(&dir);
    }
}
