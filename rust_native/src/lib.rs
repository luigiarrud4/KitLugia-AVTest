use std::collections::HashSet;
use std::sync::LazyLock;

use regex::Regex;

// ── Wide-string helpers ──────────────────────────────────────────

fn to_rust_string(ptr: *const u16) -> String {
    if ptr.is_null() {
        return String::new();
    }
    let len = (0..).take_while(|&i| unsafe { *ptr.offset(i) } != 0).count();
    let slice = unsafe { std::slice::from_raw_parts(ptr, len) };
    String::from_utf16_lossy(slice)
}

// ── Statics ───────────────────────────────────────────────────────

static GENERIC_WORDS: LazyLock<HashSet<&'static str>> = LazyLock::new(|| {
    [
        "launcher", "player", "app", "apps", "service", "services", "client", "helper",
        "manager", "plugin", "addon", "add-on", "extension", "tool", "tools", "update",
        "updater", "setup", "installer", "config", "configuration", "runtime", "engine",
        "core", "daemon", "agent", "bridge", "connector", "desktop", "portable",
        "sdk", "api", "module", "middleware", "driver", "panel", "control",
        "console", "loader", "monitor", "task", "process", "wrapper",
        "x86", "x64", "win32", "win64", "windows", "32-bit", "64-bit",
    ]
    .iter()
    .copied()
    .collect()
});

static RE_TRIM_PUBLISHER: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"(?i)\s+(Inc|LLC|Ltd|Limited|Corp|Corporation|GmbH|SAS|SRL|SA|Pty|Ltee)\.?$")
        .expect("invalid regex")
});

static RE_PAREN: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"\s*\([^)]*\)$").expect("invalid regex")
});

static RE_TM: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"[™©®]").expect("invalid regex")
});

static RE_WS: LazyLock<Regex> = LazyLock::new(|| {
    Regex::new(r"\s+").expect("invalid regex")
});

// ── Core logic ────────────────────────────────────────────────────

fn sift4_distance(s1: &str, s2: &str, _max_offset: i32) -> i32 {
    if s1.is_empty() {
        return s2.len() as i32;
    }
    if s2.is_empty() {
        return s1.len() as i32;
    }

    let l1 = s1.len();
    let l2 = s2.len();
    let mut lcss = 0i32;
    let mut local_cs = 0i32;

    let b1 = s1.as_bytes();
    let b2 = s2.as_bytes();

    for i in 0..l1.min(l2) {
        if b1[i] == b2[i] {
            local_cs += 1;
        } else {
            lcss += local_cs;
            local_cs = 0;
        }
    }
    lcss += local_cs;

    let max_len = l1.max(l2) as f64;
    (max_len - lcss as f64).round() as i32
}

fn clean_name(name: &str) -> String {
    let name = RE_TRIM_PUBLISHER.replace(name.trim(), "");
    let name = RE_PAREN.replace(&name, "");
    let name = RE_TM.replace(&name, "");
    let name = RE_WS.replace(&name, " ");
    name.trim().to_string()
}

fn confidence_generate_impl(display_name: &str, folder_name: &str) -> i32 {
    if display_name.is_empty() || folder_name.is_empty() {
        return 0;
    }

    let folder_trimmed = folder_name.trim().trim_matches(|c: char| c == '.' || c == ' ');
    if GENERIC_WORDS.contains(folder_trimmed) {
        return 0;
    }
    if folder_trimmed.len() < 4 {
        return 0;
    }

    if display_name.eq_ignore_ascii_case(folder_name) {
        return 100;
    }
    if !display_name.is_empty()
        && display_name
            .get(..folder_name.len())
            .is_some_and(|p| p.eq_ignore_ascii_case(folder_name))
    {
        return 90;
    }
    if !folder_name.is_empty()
        && folder_name
            .get(..display_name.len())
            .is_some_and(|f| f.eq_ignore_ascii_case(display_name))
    {
        return 85;
    }

    let clean_display = clean_name(display_name);
    let clean_folder = clean_name(folder_name);

    if clean_display.is_empty() || clean_folder.is_empty() {
        return 0;
    }
    if clean_display.eq_ignore_ascii_case(&clean_folder) {
        return 80;
    }

    let display_lower = clean_display.to_lowercase();
    let folder_lower = clean_folder.to_lowercase();

    let dir_to_name = folder_lower.contains(&display_lower);
    let name_to_dir = display_lower.contains(&folder_lower);

    let dist = sift4_distance(&display_lower, &folder_lower, 5);
    let max_len = display_lower.len().max(folder_lower.len());
    if max_len == 0 {
        return 0;
    }

    if dir_to_name || name_to_dir {
        let ratio = 1.0 - dist as f64 / max_len as f64;
        if ratio >= 0.8 {
            return 70;
        }
        if dist < max_len as i32 / 3 {
            let score = ((1.0 - dist as f64 / max_len as f64) * 65.0) as i32;
            return score.max(50);
        }
        return 50;
    }

    let sift_ratio = 1.0 - dist as f64 / max_len as f64;
    if sift_ratio >= 0.8 {
        return 70;
    }
    if sift_ratio >= 0.6 && dist < max_len as i32 / 3 {
        return 60;
    }

    0
}

// ── SHA256 ─────────────────────────────────────────────────────────

use sha2::{Digest, Sha256};
use std::io::Read;

#[unsafe(no_mangle)]
pub extern "C" fn sha256_file_ffi(
    path: *const u16,
    out_buf: *mut u8,
    out_capacity: i32,
) -> i32 {
    let path_str = to_rust_string(path);
    let mut file = match std::fs::File::open(&path_str) {
        Ok(f) => f,
        Err(_) => return -1,
    };

    if out_capacity < 65 {
        return -2;
    }

    let mut hasher = Sha256::new();
    let mut chunk = [0u8; 65536];
    loop {
        match file.read(&mut chunk) {
            Ok(0) => break,
            Ok(n) => hasher.update(&chunk[..n]),
            Err(_) => return -3,
        }
    }

    let hash = hasher.finalize();
    let hex_str = {
        use std::fmt::Write;
        let mut s = String::with_capacity(64);
        for b in hash.iter() {
            write!(s, "{:02x}", b).unwrap();
        }
        s
    };
    let bytes = hex_str.as_bytes();
    let len = bytes.len().min(out_capacity as usize - 1);
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), out_buf, len);
        *out_buf.add(len) = 0;
    }
    0
}

// ── Search scoring ─────────────────────────────────────────────────

fn search_score_impl(title: &str, desc: &str, query: &str) -> i32 {
    if title.is_empty() || query.is_empty() {
        return -1;
    }

    let title_lower = title.to_lowercase();
    let desc_lower = desc.to_lowercase();
    let title_bytes = title_lower.as_bytes();
    let mut total = 0i32;

    for word in query.split_whitespace() {
        let word_lower = word.to_lowercase();
        let word_bytes = word_lower.as_bytes();

        let score = if title_lower == word_lower {
            100
        } else if title_lower.starts_with(&word_lower) {
            80
        } else if word_bytes.len() < title_bytes.len()
            && title_bytes
                .windows(word_bytes.len() + 1)
                .any(|w| w[0] == b' ' && &w[1..] == word_bytes)
        {
            60
        } else if title_lower.contains(&word_lower) {
            40
        } else if desc_lower.starts_with(&word_lower) {
            30
        } else if desc_lower.contains(&word_lower) {
            15
        } else {
            return -1;
        };

        total += score;
    }

    total
}

#[unsafe(no_mangle)]
pub extern "C" fn search_score_ffi(
    title: *const u16,
    desc: *const u16,
    query: *const u16,
) -> i32 {
    let t = to_rust_string(title);
    let d = to_rust_string(desc);
    let q = to_rust_string(query);
    search_score_impl(&t, &d, &q)
}

// ── PATH analysis ──────────────────────────────────────────────────

fn expand_env(s: &str) -> String {
    let mut result = String::with_capacity(s.len());
    let mut chars = s.chars().peekable();
    while let Some(c) = chars.next() {
        if c == '%' {
            let mut var = String::new();
            for c2 in chars.by_ref() {
                if c2 == '%' { break; }
                var.push(c2);
            }
            match std::env::var(&var) {
                Ok(val) => result.push_str(&val),
                Err(_) => { result.push('%'); result.push_str(&var); result.push('%'); }
            }
        } else {
            result.push(c);
        }
    }
    result
}

fn analyze_path_problems_impl(path_value: &str) -> i32 {
    if path_value.is_empty() {
        return 0;
    }

    let entries: Vec<&str> = path_value.split(';').filter(|e| !e.trim().is_empty()).collect();
    let mut flags = 0i32;

    if entries.len() > 50 { flags |= 16; }      // TooManyEntries
    if path_value.len() > 2048 { flags |= 1024; } // PathTooLong

    let mut seen = HashSet::new();

    for entry in &entries {
        let clean = entry.trim().trim_matches('"').trim().to_string();
        if clean.is_empty() { continue; }

        // Duplicate
        if !seen.insert(clean.to_lowercase()) { flags |= 1; }

        // Relative path
        if clean.starts_with('.') { flags |= 32; }

        // Unquoted space
        if clean.contains(' ') && !clean.starts_with('"') { flags |= 64; }

        // Temp path
        let lower_clean = clean.to_lowercase();
        if lower_clean.contains("\\temp\\") || lower_clean.contains("\\tmp\\") {
            flags |= 128;
        }

        // User path without %USERPROFILE%
        if lower_clean.contains("\\users\\") && !lower_clean.contains("%userprofile%") {
            flags |= 256;
        }

        // Development junk
        if lower_clean.contains("\\node_modules\\") || lower_clean.contains("\\vendor\\")
            || lower_clean.contains("\\.git\\") || lower_clean.contains("\\dotnet\\sdk\\")
        {
            flags |= 512;
        }

        // Syntax error
        if clean.contains(',') || clean.contains("\"\"") || clean.contains("\\\\") {
            flags |= 4;
        }

        // Long path
        if clean.len() > 260 { flags |= 8; }

        // Non-ASCII characters
        if clean.chars().any(|c| c as u32 > 127) { flags |= 2048; }

        // Missing directory
        let expanded = expand_env(&clean);
        if !std::path::Path::new(&expanded).exists() {
            flags |= 2;
        }
    }

    flags
}

#[unsafe(no_mangle)]
pub extern "C" fn analyze_path_problems_ffi(path_value: *const u16) -> i32 {
    let p = to_rust_string(path_value);
    analyze_path_problems_impl(&p)
}

// ── Blake3 hashing ────────────────────────────────────────────────

#[unsafe(no_mangle)]
pub extern "C" fn blake3_file_ffi(
    path: *const u16,
    out_buf: *mut u8,
    out_capacity: i32,
) -> i32 {
    let path_str = to_rust_string(path);
    let mut file = match std::fs::File::open(&path_str) {
        Ok(f) => f,
        Err(_) => return -1,
    };

    if out_capacity < 65 {
        return -2;
    }

    let mut hasher = blake3::Hasher::new();
    let mut chunk = [0u8; 65536];
    loop {
        match file.read(&mut chunk) {
            Ok(0) => break,
            Ok(n) => { hasher.update(&chunk[..n]); }
            Err(_) => return -3,
        }
    }

    let hash = hasher.finalize();
    let hex_str = {
        use std::fmt::Write;
        let mut s = String::with_capacity(64);
        for b in hash.as_bytes() {
            write!(s, "{:02x}", b).unwrap();
        }
        s
    };
    let bytes = hex_str.as_bytes();
    let len = bytes.len().min(out_capacity as usize - 1);
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), out_buf, len);
        *out_buf.add(len) = 0;
    }
    0
}

#[unsafe(no_mangle)]
pub extern "C" fn blake3_bytes_ffi(
    data: *const u8,
    length: i32,
    out_buf: *mut u8,
    out_capacity: i32,
) -> i32 {
    if data.is_null() || length <= 0 {
        return -1;
    }
    if out_capacity < 65 {
        return -2;
    }

    let slice = unsafe { std::slice::from_raw_parts(data, length as usize) };
    let hash = blake3::hash(slice);
    let hex_str = {
        use std::fmt::Write;
        let mut s = String::with_capacity(64);
        for b in hash.as_bytes() {
            write!(s, "{:02x}", b).unwrap();
        }
        s
    };
    let bytes = hex_str.as_bytes();
    let len = bytes.len().min(out_capacity as usize - 1);
    unsafe {
        std::ptr::copy_nonoverlapping(bytes.as_ptr(), out_buf, len);
        *out_buf.add(len) = 0;
    }
    0
}

// ── Glob matching ─────────────────────────────────────────────────

#[unsafe(no_mangle)]
pub extern "C" fn glob_match_ffi(pattern: *const u16, path: *const u16) -> i32 {
    let pat_str = to_rust_string(pattern);
    let path_str = to_rust_string(path);
    if pat_str.is_empty() || path_str.is_empty() {
        return 0;
    }
    let glob = match globset::Glob::new(&pat_str) {
        Ok(g) => g,
        Err(_) => return 0,
    };
    let matcher = glob.compile_matcher();
    matcher.is_match(&path_str) as i32
}

// ── Regex helpers ─────────────────────────────────────────────────

#[unsafe(no_mangle)]
pub extern "C" fn regex_match_ffi(text: *const u16, pattern: *const u16) -> i32 {
    let text_str = to_rust_string(text);
    let pat_str = to_rust_string(pattern);
    if text_str.is_empty() || pat_str.is_empty() {
        return 0;
    }
    let re = match Regex::new(&pat_str) {
        Ok(r) => r,
        Err(_) => return 0,
    };
    re.is_match(&text_str) as i32
}

#[unsafe(no_mangle)]
pub extern "C" fn regex_replace_ffi(
    text: *const u16,
    pattern: *const u16,
    replacement: *const u16,
    out_buf: *mut u16,
    out_capacity: i32,
) -> i32 {
    let text_str = to_rust_string(text);
    let pat_str = to_rust_string(pattern);
    let repl_str = to_rust_string(replacement);
    if text_str.is_empty() || pat_str.is_empty() {
        return -1;
    }
    let re = match Regex::new(&pat_str) {
        Ok(r) => r,
        Err(_) => return -1,
    };
    let result = re.replace_all(&text_str, repl_str.as_str());
    let result_utf16: Vec<u16> = result.encode_utf16().collect();
    let len = result_utf16.len();
    if len >= out_capacity as usize {
        return -(len as i32) - 1;
    }
    unsafe {
        std::ptr::copy_nonoverlapping(result_utf16.as_ptr(), out_buf, len);
        *out_buf.add(len) = 0;
    }
    len as i32
}

#[unsafe(no_mangle)]
pub extern "C" fn regex_capture_ffi(
    text: *const u16,
    pattern: *const u16,
    case_insensitive: bool,
    out_buf: *mut u16,
    out_capacity: i32,
) -> i32 {
    let text_str = to_rust_string(text);
    let pat_str = to_rust_string(pattern);
    if text_str.is_empty() || pat_str.is_empty() {
        return -1;
    }
    let final_pat: String = if case_insensitive {
        format!("(?i){}", pat_str)
    } else {
        pat_str
    };
    let re = match Regex::new(&final_pat) {
        Ok(r) => r,
        Err(_) => return -1,
    };
    let caps = match re.captures(&text_str) {
        Some(c) => c,
        None => return 0,
    };

    let count = caps.len();
    let capacity = out_capacity as usize;
    let mut pos: usize = 0;

    for i in 0..count {
        if let Some(m) = caps.get(i) {
            let utf16: Vec<u16> = m.as_str().encode_utf16().collect();
            let needed = utf16.len() + 1;
            if pos + needed > capacity {
                return -(count as i32);
            }
            unsafe {
                std::ptr::copy_nonoverlapping(utf16.as_ptr(), out_buf.add(pos), utf16.len());
                *out_buf.add(pos + utf16.len()) = 0;
            }
            pos += needed;
        }
    }

    count as i32
}

// ── Exported FFI ──────────────────────────────────────────────────

#[unsafe(no_mangle)]
pub extern "C" fn sift4_distance_ffi(
    s1: *const u16,
    s2: *const u16,
    max_offset: i32,
) -> i32 {
    let a = to_rust_string(s1);
    let b = to_rust_string(s2);
    sift4_distance(&a, &b, max_offset)
}

#[unsafe(no_mangle)]
pub extern "C" fn confidence_generate_ffi(
    display_name: *const u16,
    folder_name: *const u16,
) -> i32 {
    let display = to_rust_string(display_name);
    let folder = to_rust_string(folder_name);
    confidence_generate_impl(&display, &folder)
}

// ── Native Registry Scanner (wimlib-style substitute for RegistryKey) ────────
//
// Mirrors the C# semantics of ScanSoftwareRecursive / ScanHiveForNames /
// ScanHiveByValues in DeepUninstaller.cs, but enumerates the hive directly with
// the Win32 RegEnumKeyExW / RegEnumValueW / RegQueryValueExW APIs:
//   * skips SystemFolderNames + caller exclusions (case-insensitive)
//   * name match uses Rust confidence_generate_impl (>= 70)
//   * value match reads ONLY REG_SZ/REG_EXPAND_SZ data (skips binary blobs that
//     .NET's GetValue() reads just to cast to string — a large chunk of the 151 ms)
//     and reuses the same install-location / filename-confidence logic.
//   * recursively descends for mode 1 (same depth guard: > 2 stops).

#[cfg(windows)]
mod native_registry {
    use super::confidence_generate_impl;

    #[link(name = "Advapi32")]
    unsafe extern "system" {
        fn RegOpenKeyExW(
            key: isize,
            subkey: *const u16,
            options: u32,
            desired: u32,
            result: *mut isize,
        ) -> i32;
        fn RegCloseKey(key: isize) -> i32;
        fn RegEnumKeyExW(
            key: isize,
            index: u32,
            name: *mut u16,
            name_len: *mut u32,
            reserved: *mut u32,
            class: *mut u16,
            class_len: *mut u32,
            last_write: *mut i64,
        ) -> i32;
        fn RegEnumValueW(
            key: isize,
            index: u32,
            value_name: *mut u16,
            value_name_len: *mut u32,
            reserved: *mut u32,
            vtype: *mut u32,
            data: *mut u8,
            data_len: *mut u32,
        ) -> i32;
        fn RegQueryValueExW(
            key: isize,
            value_name: *const u16,
            reserved: *mut u32,
            vtype: *mut u32,
            data: *mut u8,
            data_len: *mut u32,
        ) -> i32;
        fn ExpandEnvironmentStringsW(src: *const u16, dst: *mut u16, dst_len: u32) -> u32;
    }

    const ERROR_SUCCESS: i32 = 0;
    const ERROR_MORE_DATA: i32 = 234;
    const ERROR_NO_MORE_ITEMS: i32 = 259;
    const ERROR_INSUFFICIENT_BUFFER: i32 = 122;
    const KEY_READ: u32 = 0x20019;
    const REG_SZ: u32 = 1;
    const REG_EXPAND_SZ: u32 = 2;
    const HKEY_CLASSES_ROOT: isize = 0x8000_0000;
    const HKEY_CURRENT_USER: isize = 0x8000_0001;
    const HKEY_LOCAL_MACHINE: isize = 0x8000_0002;
    const HKEY_USERS: isize = 0x8000_0003;

    fn wide(s: &str) -> Vec<u16> {
        let mut v: Vec<u16> = s.encode_utf16().collect();
        v.push(0);
        v
    }

    fn hive_base(prefix: &str) -> isize {
        match prefix {
            "HKEY_CLASSES_ROOT" => HKEY_CLASSES_ROOT,
            "HKEY_CURRENT_USER" => HKEY_CURRENT_USER,
            "HKEY_LOCAL_MACHINE" => HKEY_LOCAL_MACHINE,
            "HKEY_USERS" => HKEY_USERS,
            _ => 0,
        }
    }

    fn open_key(parent: isize, sub_path: &str) -> isize {
        let sub_wide = wide(sub_path);
        let mut handle: isize = 0;
        let rc =
            unsafe { RegOpenKeyExW(parent, sub_wide.as_ptr(), 0, KEY_READ, &mut handle) };
        if rc != ERROR_SUCCESS || handle == 0 {
            return 0;
        }
        handle
    }

    fn close_key(h: isize) {
        if h != 0 {
            unsafe { RegCloseKey(h) };
        }
    }

    fn enumerate_children(key: isize) -> Vec<String> {
        let mut names = Vec::new();
        let mut idx: u32 = 0;
        loop {
            let mut buf = [0u16; 256];
            let mut len: u32 = buf.len() as u32;
            let rc = unsafe {
                RegEnumKeyExW(
                    key,
                    idx,
                    buf.as_mut_ptr(),
                    &mut len,
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                    std::ptr::null_mut(),
                )
            };
            if rc == ERROR_NO_MORE_ITEMS {
                break;
            }
            if rc == ERROR_MORE_DATA || rc == ERROR_INSUFFICIENT_BUFFER {
                idx += 1;
                continue;
            }
            if rc != ERROR_SUCCESS {
                break;
            }
            if len > 0 {
                names.push(String::from_utf16_lossy(&buf[..len as usize]));
            }
            idx += 1;
        }
        names
    }

    fn expand_value(raw: &[u16]) -> String {
        let mut out = vec![0u16; 32768];
        let rc = unsafe { ExpandEnvironmentStringsW(raw.as_ptr(), out.as_mut_ptr(), out.len() as u32) };
        if rc > 0 {
            let n = (rc as usize - 1).min(out.len());
            String::from_utf16_lossy(&out[..n])
        } else {
            String::from_utf16_lossy(raw)
        }
    }

    fn stem_of(data: &str) -> String {
        let last = match data.rfind(['\\', '/']) {
            Some(i) => &data[i + 1..],
            None => data,
        };
        match last.rfind('.') {
            Some(i) if i > 0 => last[..i].to_string(),
            _ => last.to_string(),
        }
    }

    fn dir_of(data: &str) -> String {
        match data.rfind(['\\', '/']) {
            Some(i) => data[..i].to_string(),
            None => String::new(),
        }
    }

    // Mirrors KeyHasValueReferencing in C#: only REG_SZ/REG_EXPAND_SZ values can be
    // cast to string; binary/multi-reg values are skipped without reading data.
    fn key_has_value_referencing(key: isize, install: &str, display: &str) -> bool {
        let mut idx: u32 = 0;
        loop {
            let mut name_buf = vec![0u16; 256];
            let mut name_len: u32 = name_buf.len() as u32;
            let mut vtype: u32 = 0;
            let mut data_len: u32 = 0;
            let rc = unsafe {
                RegEnumValueW(
                    key,
                    idx,
                    name_buf.as_mut_ptr(),
                    &mut name_len,
                    std::ptr::null_mut(),
                    &mut vtype,
                    std::ptr::null_mut(),
                    &mut data_len,
                )
            };
            idx += 1; // always advance so ERROR_MORE_DATA/error can't loop forever
            if rc == ERROR_NO_MORE_ITEMS {
                break;
            }
            if rc == ERROR_MORE_DATA || rc == ERROR_INSUFFICIENT_BUFFER {
                // value name too long for our buffer — re-attempt not feasible here;
                // skip this entry and continue enumeration.
                continue;
            }
            if rc != ERROR_SUCCESS {
                break;
            }
            if vtype != REG_SZ && vtype != REG_EXPAND_SZ {
                continue;
            }
            // Also skip the (Default)? No — C# GetValue("") returns default; but we
            // only need value name from the enumeration; data query handles the rest.
            let mut data = vec![0u8; data_len as usize];
            let mut real_len: u32 = data_len;
            let rc2 = unsafe {
                RegQueryValueExW(
                    key,
                    name_buf.as_ptr(),
                    std::ptr::null_mut(),
                    &mut vtype,
                    if data.is_empty() { std::ptr::null_mut() } else { data.as_mut_ptr() },
                    &mut real_len,
                )
            };
            if rc2 != ERROR_SUCCESS {
                continue;
            }
            if real_len == 0 {
                continue;
            }
            let word_count = (real_len as usize) / 2;
            let raw: Vec<u16> = data[..word_count * 2]
                .chunks_exact(2)
                .map(|c| u16::from_le_bytes([c[0], c[1]]))
                .collect();
            let text = if vtype == REG_EXPAND_SZ {
                expand_value(&raw)
            } else {
                String::from_utf16_lossy(&raw)
            };
            let text: String = text.trim_end_matches('\0').to_string();
            if text.is_empty() {
                continue;
            }
            if !install.is_empty() {
                let norm = install.trim_end_matches('\\');
                if !norm.is_empty() {
                    let tl = text.to_lowercase();
                    let nl = norm.to_lowercase();
                    if tl.contains(&nl) {
                        return true;
                    }
                    let dir = dir_of(&text).to_lowercase();
                    if !dir.is_empty() && dir.contains(&nl) {
                        return true;
                    }
                }
            }
            if !display.is_empty() {
                let stem = stem_of(&text);
                if !stem.is_empty() && confidence_generate_impl(display, &stem) >= 85 {
                    return true;
                }
            }
        }
        false
    }

    const SYSTEM_FOLDERS: &[&str] = &[
        "Microsoft", "Windows", "WinSxS", "System32", "SysWOW64", "Common Files",
        "MSBuild", "Reference Assemblies", "WindowsApps", "Windows NT",
        "WindowsPowerShell", "dotnet", "Assembly", "PackageManagement",
        "Temporary Internet Files", "Temp", "Templates", "Start Menu", "Desktop",
        "Favorites", "Fonts", "Installer", "Microsoft.NET", "Microsoft Shared",
        "ModifiableWindowsApps", "Resources", "servicing", "VSS", "Help", "inf",
        "L2Schemas", "Logs", "Media", "ModemLogs", "en-US", "Branding", "Cursors",
        "Debug", "ImmersiveControlPanel", "Registration", "rescache", "SchCache",
        "security", "ServicePackFiles", "Skin", "SoftwareDistribution", "Speech",
        "systemprofile", "ConfigMsi", "Msi", "mui", "OCR", "ras", "twain_32", "Web",
        "winsxs", "IME", "InputMethod", "DirectX", "VulkanRT", "CRT", "MFC", "ATL",
    ];

    fn name_skipped(name: &str, exclusions: &[String]) -> bool {
        if name.is_empty() || name.len() < 2 {
            return true;
        }
        if name.starts_with('.') || name.starts_with('_') {
            return true;
        }
        if SYSTEM_FOLDERS.iter().any(|s| name.eq_ignore_ascii_case(s)) {
            return true;
        }
        if exclusions.iter().any(|e| name.eq_ignore_ascii_case(e)) {
            return true;
        }
        false
    }

    // Opens a key at the given FULL hive path (e.g. "HKEY_LOCAL_MACHINE\\SOFTWARE\\Classes").
    // The root hive handle is reused for perf; the rest is opened via the key path.
    fn open_full_path(full: &str) -> isize {
        let (prefix, sub) = match full.find('\\') {
            Some(i) => (&full[..i], &full[i + 1..]),
            None => (full, ""),
        };
        let base = hive_base(prefix);
        if base == 0 {
            return 0;
        }
        if sub.is_empty() {
            return base;
        }
        open_key(base, sub)
    }

    // Mirrors ScanSoftwareRecursive / ScanHiveForNames / ScanHiveByValues.
    // mode: 0 = flat name+value (ScanHiveForNames), 1 = recursive name+value
    // (ScanSoftwareRecursive), 2 = flat value-only (ScanHiveByValues).
    fn scan_key(
        full: &str,
        display: &str,
        install: &str,
        exclusions: &[String],
        mode: u32,
        depth: u32,
        results: &mut Vec<String>,
    ) {
        if mode == 1 && depth > 2 {
            return;
        }
        let handle = open_full_path(full);
        if handle == 0 {
            return;
        }
        let names = enumerate_children(handle);
        for name in names {
            // mode 2 (ScanHiveByValues) only skips empty + SystemFolderNames.
            let skipped = if mode == 2 {
                name.is_empty()
                    || SYSTEM_FOLDERS.iter().any(|s| name.eq_ignore_ascii_case(s))
            } else {
                name_skipped(&name, exclusions)
            };
            if skipped {
                continue;
            }
            let child_full = format!("{}\\{}", full, name);
            let mut name_match = false;
            if mode != 2 && !display.is_empty() {
                name_match = confidence_generate_impl(display, &name) >= 70;
            }
            let mut value_match = false;
            if !install.is_empty() && (mode == 2 || !name_match) {
                let child = open_key(handle, &name);
                if child != 0 {
                    value_match = key_has_value_referencing(child, install, display);
                    close_key(child);
                }
            }
            if name_match || value_match {
                results.push(child_full.clone());
            }
            if mode == 1 {
                scan_key(&child_full, display, install, exclusions, mode, depth + 1, results);
            }
        }
        close_key(handle);
    }

    // Writes a result as a NUL-terminated UTF-16 string into the buffer.
    fn write_result(buf: &mut [u16], pos: &mut usize, s: &str) -> bool {
        let encoded: Vec<u16> = s.encode_utf16().collect();
        if *pos + encoded.len() + 1 > buf.len() {
            return false;
        }
        buf[*pos..(*pos + encoded.len())].copy_from_slice(&encoded);
        *pos += encoded.len();
        buf[*pos] = 0;
        *pos += 1;
        true
    }

    #[unsafe(no_mangle)]
    pub extern "C" fn reg_scan_ffi(
        root_path: *const u16,  // e.g. "HKEY_LOCAL_MACHINE\\SOFTWARE" or "HKEY_CURRENT_USER\\..."
        display_name: *const u16,
        install_location: *const u16,
        exclusions: *const u16,  // ';'-separated, may be empty
        mode: i32,               // 0 = flat names+value, 1 = recursive names+value, 2 = value-only flat
        out_buf: *mut u16,
        out_capacity: i32,
    ) -> i32 {
        let root = super::to_rust_string(root_path);
        let display = super::to_rust_string(display_name);
        let install = super::to_rust_string(install_location);
        let ex_str = super::to_rust_string(exclusions);
        let exclusions: Vec<String> = ex_str
            .split(';')
            .map(|s| s.trim().to_string())
            .filter(|s| !s.is_empty())
            .collect();

        let mut results: Vec<String> = Vec::new();
        scan_key(
            &root,
            &display,
            &install,
            &exclusions,
            mode as u32,
            0,
            &mut results,
        );

        // Serialize results as NUL-terminated UTF-16 into the caller's buffer.
        let buf = unsafe { std::slice::from_raw_parts_mut(out_buf, out_capacity as usize) };
        let mut pos: usize = 0;
        for r in &results {
            if !write_result(buf, &mut pos, r) {
                return -(results.len() as i32 + 1); // buffer too small
            }
        }
        results.len() as i32
    }
}
