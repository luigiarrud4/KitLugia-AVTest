//! Reference port of Feather's persistent NTFS index (nimao33/Feather,
//! feather-core: entry/format/mapped/path/index/index_set/synthetic).
//!
//! Not wired to any FFI export yet — kept compiled as dead code so the
//! machinery is here if a persistent on-disk index is ever needed
//! (e.g. an Everything-style file search inside KitLugia). The MFT
//! *scanner* optimizations from the same project live in `crate::mft`.
#![allow(dead_code)]

pub mod entry;
pub mod format;
pub mod index;
pub mod index_set;
pub mod mapped;
pub mod path;
pub mod synthetic;
