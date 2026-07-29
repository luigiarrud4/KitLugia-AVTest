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
    if display_name.len() >= folder_name.len()
        && display_name[..folder_name.len()].eq_ignore_ascii_case(folder_name)
    {
        return 90;
    }
    if folder_name.len() >= display_name.len()
        && folder_name[..display_name.len()].eq_ignore_ascii_case(display_name)
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
