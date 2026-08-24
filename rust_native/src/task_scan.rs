//! Ultra-fast process enumeration for Kit Task Manager.
//!
//! Uses `NtQuerySystemInformation` (ntdll) for the process list and
//! `OpenProcess` + `QueryFullProcessImageNameW` (kernel32) for paths.
//! Returns a flat array of `ProcessInfo` structs — the C# side only needs
//! one P/Invoke call and zero managed allocations during the scan.

use std::mem;
use std::ptr;

#[cfg(windows)]
mod win {
    use std::ffi::c_void;

    pub type HANDLE = *mut c_void;
    pub type BOOL = i32;
    pub type DWORD = u32;
    pub type ULONG = u32;

    #[repr(C)]
    #[allow(non_snake_case)]
    pub struct UNICODE_STRING {
        pub Length: u16,
        pub MaximumLength: u16,
        pub Buffer: *const u16,
    }

    #[repr(C)]
    #[allow(non_snake_case)]
    pub struct CLIENT_ID {
        pub UniqueProcess: HANDLE,
        pub UniqueThread: HANDLE,
    }

    #[repr(C)]
    #[allow(non_snake_case)]
    pub struct SYSTEM_PROCESS_INFORMATION {
        pub NextEntryOffset: u32,
        pub NumberOfThreads: u32,
        pub WorkingSetPrivateSize: i64,
        pub HardFaultCount: u32,
        pub NumberOfThreadsHighWatermark: u32,
        pub CycleTime: u64,
        pub CreateTime: i64,
        pub UserTime: i64,
        pub KernelTime: i64,
        pub ImageName: UNICODE_STRING,
        pub BasePriority: i32,
        pub UniqueProcessId: HANDLE,
        pub InheritedFromUniqueProcessId: HANDLE,
        pub HandleCount: u32,
        pub SessionId: u32,
        pub UniqueProcessKey: usize,
        pub PeakVirtualSize: usize,
        pub VirtualSize: usize,
        pub PageFaultCount: u32,
        // We don't need the rest — we read at NextEntryOffset
    }

    unsafe extern "system" {
        pub fn NtQuerySystemInformation(
            system_information_class: u32,
            system_information: *mut c_void,
            system_information_length: u32,
            return_length: *mut u32,
        ) -> i32;

        pub fn OpenProcess(
            dwDesiredAccess: DWORD,
            bInheritHandle: BOOL,
            dwProcessId: DWORD,
        ) -> HANDLE;

        pub fn CloseHandle(hObject: HANDLE) -> BOOL;

        pub fn QueryFullProcessImageNameW(
            hProcess: HANDLE,
            dwFlags: DWORD,
            lpExeName: *mut u16,
            lpdwSize: *mut DWORD,
        ) -> BOOL;
    }

    pub const PROCESS_QUERY_LIMITED_INFORMATION: DWORD = 0x1000;
    pub const PROCESS_QUERY_INFORMATION: DWORD = 0x0400;
    pub const PROCESS_QUERY_LIMITED_INFORMATION_FAST: DWORD = 0x1000;
}

/// C-compatible struct returned to C# via P/Invoke.
/// Packed to avoid padding waste in the array.
#[repr(C)]
#[derive(Clone, Copy, Default)]
pub struct ProcessInfoRaw {
    pub pid: u32,
    pub parent_pid: u32,
    pub session_id: u32,
    pub handle_count: u32,
    pub base_priority: i32,
    pub is_system: u8, // 1 if pid < 100
    pub _pad: [u8; 3],
}

/// Maximum number of processes we support in a single scan.
const MAX_PROCESSES: usize = 2048;

/// Single-call process enumeration.
/// Writes up to `MAX_PROCESSES` `ProcessInfoRaw` structs into `buffer`.
/// Returns the actual number of processes found, or -1 on error.
///
/// # Safety
/// `buffer` must point to at least `MAX_PROCESSES * size_of::<ProcessInfoRaw>()` bytes.
#[cfg(windows)]
#[unsafe(no_mangle)]
pub extern "C" fn enumerate_processes_fast(
    buffer: *mut ProcessInfoRaw,
    max_count: i32,
) -> i32 {
    if buffer.is_null() || max_count <= 0 {
        return -1;
    }

    let cap = (max_count as usize).min(MAX_PROCESSES);
    let buf_bytes = cap * mem::size_of::<ProcessInfoRaw>();
    let mut sys_buf: Vec<u8> = vec![0u8; 256 * 1024]; // 256 KB initial

    // Retry with larger buffer if needed
    for attempt in 0..3 {
        let mut return_len: u32 = 0;
        let status = unsafe {
            win::NtQuerySystemInformation(
                5, // SystemProcessInformation
                sys_buf.as_mut_ptr() as *mut _,
                sys_buf.len() as u32,
                &mut return_len,
            )
        };

        if status == 0 {
            break; // success
        }
        if status == 0xC0000004u32 as i32 || status == 0x80000005u32 as i32 {
            // STATUS_INFO_LENGTH_MISMATCH or STATUS_BUFFER_TOO_SMALL
            let needed = (return_len as usize).max(sys_buf.len() * 2);
            sys_buf.resize(needed, 0);
            continue;
        }
        return -1; // unexpected error
    }

    let out = unsafe { std::slice::from_raw_parts_mut(buffer, cap) };
    let mut count: usize = 0;
    let mut offset: u32 = 0;

    loop {
        if offset as usize >= sys_buf.len() {
            break;
        }

        let entry = unsafe {
            &*(sys_buf.as_ptr().add(offset as usize) as *const win::SYSTEM_PROCESS_INFORMATION)
        };

        let pid = entry.UniqueProcessId as u32;
        let parent_pid = entry.InheritedFromUniqueProcessId as u32;

        if count < cap {
            // Read image name from UNICODE_STRING (best-effort, don't crash)
            let _session_id = entry.SessionId;
            let _handle_count = entry.HandleCount;

            out[count] = ProcessInfoRaw {
                pid,
                parent_pid,
                session_id: entry.SessionId,
                handle_count: entry.HandleCount,
                base_priority: entry.BasePriority,
                is_system: if pid < 100 { 1 } else { 0 },
                _pad: [0; 3],
            };
            count += 1;
        }

        if entry.NextEntryOffset == 0 {
            break;
        }
        offset += entry.NextEntryOffset;
    }

    count as i32
}

/// Get a process executable path safely using OpenProcess + QueryFullProcessImageNameW.
/// Writes the path as a null-terminated UTF-16 string into `out_buf`.
/// Returns the number of characters written (excluding null), or negative on error.
///
/// # Safety
/// `out_buf` must point to at least `out_capacity` u16 elements.
#[cfg(windows)]
#[unsafe(no_mangle)]
pub extern "C" fn get_process_path_safe(
    pid: u32,
    out_buf: *mut u16,
    out_capacity: i32,
) -> i32 {
    if out_buf.is_null() || out_capacity <= 0 {
        return -1;
    }
    if pid == 0 || pid == 4 {
        return 0; // System/Idle — no path
    }

    let h = unsafe {
        win::OpenProcess(
            win::PROCESS_QUERY_LIMITED_INFORMATION,
            0,
            pid,
        )
    };
    if h.is_null() {
        return -2; // access denied
    }

    let result = unsafe {
        let mut size: u32 = out_capacity as u32;
        let ok = win::QueryFullProcessImageNameW(h, 0, out_buf, &mut size);
        if ok != 0 {
            size as i32
        } else {
            -3 // query failed
        }
    };

    unsafe { win::CloseHandle(h); }
    result
}

/// Non-windows stub
#[cfg(not(windows))]
#[unsafe(no_mangle)]
pub extern "C" fn enumerate_processes_fast(
    _buffer: *mut ProcessInfoRaw,
    _max_count: i32,
) -> i32 {
    0
}

#[cfg(not(windows))]
#[unsafe(no_mangle)]
pub extern "C" fn get_process_path_safe(
    _pid: u32,
    _out_buf: *mut u16,
    _out_capacity: i32,
) -> i32 {
    -1
}
