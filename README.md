# KitLugia V2 - Windows Optimization Utility

**KitLugia** is a Windows optimization and maintenance tool built with .NET and WPF. It provides a centralized interface for system tweaks, cleanup, security analysis, boot media creation, and performance tuning.

[![.NET](https://img.shields.io/badge/.NET-10.0-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Windows-blue.svg)](https://www.microsoft.com/windows)
[![License](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)

---

## Features

### System Optimization
- Registry tweaks for performance and responsiveness
- Power plan management (Bitsum Highest Performance / Ultimate Performance)
- Visual effects adjustment (Extreme profile for max FPS)
- GPU VRAM configuration
- Startup delay remover, shutdown speed optimizer

### Gaming & Performance
- Process priority management (CPU, I/O, page priority)
- Hardware-Accelerated GPU Scheduling (HAGS)
- TCP congestion control optimization (CTCP)
- Memory optimizer (native Windows API)
- Network throttling disable

### Security Analysis (Guardian)
- Scans for misconfigured security settings
- Checks CPU mitigations (Spectre/Meltdown), CFG, DEP, UAC
- SMBv1, AutoRun, Kernel protection verification
- Path environment variable analysis
- Explorer shell integrity checks
- Real vulnerability references: CVE-2026-20805 (DWM info disclosure), CVE-2026-21509, CVE-2026-21513, CVE-2026-21514

### System Cleanup
- Bloatware removal (100+ Windows pre-installed apps)
- Registry cleaner
- Temporary files cleaner
- Disk space analysis
- Duplicate file finder

### Boot & Recovery
- WinPE bootable media creation
- Bootable USB (Rufus-style)
- ISO editing tools
- Easy2Boot integration
- Boot optimization

### Network Tools
- DNS management (Cloudflare, Google, OpenDNS, DHCP)
- TCP/IP optimization
- Network diagnostics
- Latency analysis
- Connection speed test

### System Repairs
- Windows Update fix
- System File Checker (SFC)
- DISM component repair
- Boot recovery tools
- Service configuration

---

## Build

### Prerequisites
- .NET 10.0 SDK or later
- Windows 10 (1903+) or Windows 11
- Visual Studio 2022 (optional)

### Build & Run
```bash
git clone https://github.com/luigiarrud4/KitLugia.git
cd KitLugia
dotnet build --configuration Release
cd KitLugia.GUI/bin/Release/net10.0-windows
KitLugia.GUI.exe
```

### Self-Contained Publish
```bash
dotnet publish KitLugia.GUI -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

---

## Requirements

| Component | Minimum | Recommended |
|-----------|---------|-------------|
| OS | Windows 10 (1903+) | Windows 11 24H2 |
| CPU | Intel i3 / AMD Ryzen 3 | Intel i7 / AMD Ryzen 7 |
| RAM | 4 GB | 16 GB |
| Storage | 2 GB free | 10 GB free (SSD) |

---

## Project Structure
```
KitLugia/
├── KitLugia.Core/           # Business logic (system tweaks, security, network, etc.)
│   ├── Guardian.cs          # Security vulnerability scanner
│   ├── SystemTweaks.cs      # Registry and system configuration tweaks
│   ├── PartitionManager.cs  # Disk partition operations (diskpart + WMI)
│   ├── NetworkManager.cs    # Network and DNS management
│   └── ...                  # 70+ modules
├── KitLugia.GUI/            # WPF user interface
│   ├── Pages/               # 35+ functional pages
│   ├── Services/            # Tray icon, memory monitoring, background tasks
│   └── Controls/            # Custom WPF controls
└── KitLugia.sln
```

---

## Technologies
- **.NET 10.0** - Modern cross-platform runtime
- **WPF** - Desktop UI framework
- **P/Invoke** - Native Windows API access
- **WMI** - System management instrumentation
- **Windows Registry** - Configuration management

---

## License
MIT License - see [LICENSE](LICENSE) for details.

---

*Desenvolvido por [Luigi Arruda](https://github.com/luigiarrud4)*
