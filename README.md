# 🐉 KitLugia

<div align="center">

**Ferramenta desktop para manutenção, diagnóstico, otimização e recuperação do Windows.**

Interface WPF · Núcleo em .NET 10 · Rotinas nativas em Rust · APIs Win32 diretas

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![C# 13](https://img.shields.io/badge/C%23-13-239120?logo=csharp)](https://learn.microsoft.com/dotnet/csharp/)
[![Rust](https://img.shields.io/badge/Rust-FF4500?logo=rust)](https://www.rust-lang.org/)
[![WPF](https://img.shields.io/badge/UI-WPF-9B4DCA)](https://learn.microsoft.com/dotnet/desktop/wpf/)
[![Platform](https://img.shields.io/badge/Windows-10%2B-0078D4?logo=windows)](https://www.microsoft.com/windows)
[![License](https://img.shields.io/badge/License-MIT-green)](LICENSE)
[![SDK](https://img.shields.io/badge/SDK-10.0.301-blue)](global.json)

</div>

---

O KitLugia reúne, em uma única interface, operações que normalmente ficam espalhadas pelo Registro, PowerShell, ferramentas administrativas e utilitários de recuperação do Windows. Dependendo da função escolhida, ele pode ler e alterar configurações do sistema, remover arquivos e pacotes, executar ferramentas nativas, reconfigurar a rede, preparar discos ou criar mídia de boot.

> ⚠️ **Projeto em desenvolvimento ativo.** Algumas áreas são experimentais e podem exigir privilégios de administrador.

---

## 🚀 Funcionalidades

<details open>
<summary><b>⚙️ Otimização do Sistema</b></summary>

- Ajustes de desempenho e responsividade via Registro (user + machine hives)
- Gerenciamento de planos de energia: Bitsum Highest Performance, Ultimate Performance, Power Saver
- Configuração de VRAM de GPU e HAGS (Hardware-Accelerated GPU Scheduling)
- Controle de prioridade de processos (CPU, I/O, page priority) com reversão automática
- Remoção de delay de inicialização e otimização de shutdown
- Desativação de network throttling e otimização TCP/CTCP, RSS e offload
- Otimizador de memória via `GlobalMemoryStatusEx`, `EmptyWorkingSet` e `SetProcessWorkingSetSize`
- Gerenciamento de efeitos visuais (perfil Extreme para máximos FPS)
- Configuração de GPU VRAM e agendamento de CPU

**Arquivos centrais:** `SystemTweaks.cs`, `OptimizationOrchestrator.cs`, `PowerPlanManager.cs`, `AdvancedTweaksManager.cs`

</details>

<details open>
<summary><b>🛡️ Guardian — Diagnóstico de Segurança e Integridade</b></summary>

- Verificação de mitigações de CPU (Spectre/Meltdown), CFG, DEP e UAC
- Checagem de SMBv1, AutoRun e proteções do kernel
- Análise de variável PATH e integridade do shell do Explorer
- Diagnóstico de serviços, BCD e chaves de Registro
- Referências a CVEs reais: CVE-2026-20805, CVE-2026-21509, CVE-2026-21513, CVE-2026-21514
- Detecção de configurações frágeis ou inconsistentes
- Reparo de configurações, alteração de serviços (`sc.exe`), atualização de boot (`bcdedit`)

**Arquivos centrais:** `Guardian.cs`

</details>

<details open>
<summary><b>🧹 Limpeza e Manutenção</b></summary>

- Remoção de bloatware (100+ apps pré-instalados do Windows via `Get-AppxPackage`)
- Limpeza de temporários (usuário + Windows), cache do Windows Update e shaders da GPU
- Limpeza de DNS, Prefetch, Lixeira, logs de diagnóstico e thumbnails
- CompactOS para compactação de arquivos do sistema
- Scanner de apps portáteis via leitura nativa da MFT (WizTree-style)
- Cache de browsers: Chrome, Edge, Firefox, Opera, Brave, Vivaldi
- Registry cleaner para chaves órfãos (COM, SharedDLLs, AppPaths)

**Arquivos centrais:** `CleanupManager.cs`, `BloatwareManager.cs`, `RegistryCleaner.cs`, `BrowserCacheManager.cs`

</details>

<details open>
<summary><b>🌐 Ferramentas de Rede</b></summary>

- Troca de DNS: Cloudflare, Google, OpenDNS, Quad9, DHCP
- Otimização TCP/IP, CTCP, RSS, offload e Nagle
- Diagnósticos de adaptador e conectividade
- Reset de Winsock, TCP/IP e ARP
- Análise de latência e benchmark de DNS
- Gerenciamento de adaptadores de rede (WMI + netsh)
- Download Boost com detecção automática de tráfego
- Monitor de tráfego de rede em tempo real

**Arquivos centrais:** `NetworkManager.cs`, `AdapterManager.cs`, `DnsBenchmark.cs`, `LatencyAnalyzer.cs`, `DownloadBoostEngine.cs`

</details>
<details open>
<summary><b>🔧 Reparos do Windows</b></summary>

- Interface assistida para SFC e DISM
- Correções de Windows Update
- Reparos de componentes, serviços e boot
- Verificação e restauração de arquivos protegidos
- Repair point creation antes de operações de reparo
- Diagnósticos de sistema e componente

**Arquivos centrais:** `SystemRepair.cs`, `GeneralRepairManager.cs`, `WindowsUpdateManager.cs`, `DiagnosticsManager.cs`

</details>

<details open>
<summary><b>💾 Boot, Partições e Mídia Inicializável</b></summary>

- Criação de mídia bootável: USB, ISO, WinPE
- Edição e montagem de imagens ISO
- Gerenciamento de BCD, bcdboot, bootsect e diskpart
- Suporte a MBR, GPT, UEFI e Legacy BIOS
- WinPE personalizado com injeção de drivers e scripts
- Edição de ISO com wimlib (injeção de scripts, WinXShell, bridge)
- Multi-ISO com rEFInd e Easy2Boot
- Shrink de disco via WinPE com persistência de logs
- Preparação de VALOS (Validation OS) com shell personalizado
- Recuperação de boot: EmergencyBoot, EmergencyUEFI, EmergencyWinRE

**Arquivos centrais:** `BootableMediaManager.cs`, `WinpeBuilder.cs`, `WinbootManager.cs`, `IsoEditorManager.cs`, `PartitionManager.cs`, `EmergencyBcdBootManager.cs`

</details>

<details open>
<summary><b>🎮 GameBoost</b></summary>

- Prioridade de processo em foreground com reversão automática
- Perfil de boost personalizado: Normal / High / RealTime
- Download Boost com detecção automática de tráfego e reverte quando idle
- Monitor de processos com alertas inteligentes (RAM > 2GB, CPU > 80%, não responsivo)
- Otimizações da comunidade (Reddit): SmartScreen, EdgeUpdate, CompatTelRunner, SearchIndexer, TextInputHost
- GameBarPresenceWriter: rename para .bak no startup
- Per-process RAM limiter (estilo Firemin) com EmptyWorkingSet
- ProBalance: equilíbrio automático de prioridades

**Arquivos centrais:** `TrayIconService.cs`, `GameBoostPage.xaml.cs`

</details>

<details open>
<summary><b>🔧 Deep Uninstall (Revo-style)</b></summary>

- Pré-scan → desinstalação → pós-scan → diff (confirmed vs heuristic)
- 3 modos de scan: Safe, Moderate, Advanced
- Classificação de segurança: Safe (🟢), Moderate (🟡), Uncertain (🔴)
- Scanner de registry em paralelo (10 buckets)
- Backup .reg antes de deletar chaves
- Forced uninstall para programas quebrados
- Hunter Mode para detectar programas por janela
- Deletion log para rastreamento de operações
- Histórico de desinstalações com persistência JSON

**Arquivos centrais:** `DeepUninstaller.cs`, `HunterWindow.xaml.cs`, `UninstallHistory.cs`

</details>

<details open>
<summary><b>📊 Monitoramento e Processos</b></summary>

- Monitor de processos com stats em tempo real (CPU, RAM, threads, handles)
- Dashboard com indicadores de sistema
- Gerenciador de serviços e tarefas agendadas
- Explorer de PATH com indexador nativo USN/MFT
- Scanner de apps portáteis via MFT
- Detecção de apps instalados via Registro + filesystem
- Análise de integridade do sistema
- Profiler de memória e leak detection

**Arquivos centrais:** `ProcessMonitorPage.xaml.cs`, `ServicesPage.xaml.cs`, `PathRepair.cs`, `PortableAppScanner.cs`, `NativeUsn.cs`

</details>

<details open>
<summary><b>🔒 Privacidade e Configurações</b></summary>

- 130+ configurações de privacidade estilo O&O ShutUp10++
- Configurações de Windows Update (pausar, canal, downgrade de build)
- Explorer de configurações do Windows
- Gerenciamento de drivers
- Quick Install para instalação rápida de ferramentas (winget)
- Reinstall Preserve: reinstalação com preservação de dados

**Arquivos centrais:** `OOShutUpManager.cs`, `WindowsUpdatePage.xaml.cs`, `DriversPage.xaml.cs`

</details>

<details open>
<summary><b>🌐 Rede e Conectividade</b></summary>

- Servidor de conexão LAN para compartilhamento local
- Túneis: Hole Punching, PlayIT, Virtual Adapter
- Adaptadores de rede virtuais
- Gerenciamento de exposição de rede
- Proxy TCP e relay client/server

**Arquivos centrais:** `LanConnectionManager.cs`, `TunnelManager.cs`, `VirtualNetworkAdapter.cs`, `HolePunchingManager.cs`

</details>
---

## 🦀 Biblioteca Nativa em Rust

O módulo Rust (`rust_native/`) não é uma segunda aplicação completa. Ele fornece rotinas nativas para tarefas específicas, compiladas como DLL `cdylib` e integradas via FFI:

| Função | Descrição | FFI |
|--------|-----------|-----|
| **Sift4 Distance** | Distância de edição de strings (alternativa leve ao Levenshtein) | `sift4_distance_ffi` |
| **Confidence Score** | Scoring de confiança para matching nome-pasta | `confidence_generate_ffi` |
| **SHA-256** | Hash de arquivos em streaming (64KB chunks) | `sha256_file_ffi` |
| **BLAKE3** | Hash de arquivos e bytes em streaming | `blake3_file_ffi`, `blake3_bytes_ffi` |
| **Glob Matching** | Correspondência de padrões glob (via `globset`) | `glob_match_ffi` |
| **Regex** | Match, Replace e Capture (via `regex`) | `regex_match_ffi`, `regex_replace_ffi`, `regex_capture_ffi` |
| **Search Scoring** | Scoring de relevância para busca global | `search_score_ffi` |
| **PATH Analysis** | Detecção de problemas na variável PATH | `analyze_path_problems_ffi` |
| **MFT Scanner** | Leitura raw do $MFT de volumes NTFS (estilo WizTree) | `mft_scan_ffi` |
| **Registry Scanner** | Enumeração nativa do Registro via Win32 APIs | `reg_scan_ffi` |

> A leitura raw da MFT bypassa a enumeração Win32 padrão, resultando em scans ~10x mais rápidos para grandes volumes. Requer elevação (admin).

---

## 📦 Stack Tecnológica

| Camada | Tecnologia | Detalhes |
|--------|-----------|----------|
| Interface | C# 13 + WPF | Tema escuro, DataGrid virtualizado, controles customizados |
| Runtime | .NET 10 | SDK 10.0.301 (definido em `global.json`) |
| Núcleo | KitLugia.Core | 90+ módulos de operação |
| Nativo | Rust (DLL cdylib) | Sift4, SHA-256, BLAKE3, MFT, Regex, Registro |
| APIs do Sistema | Win32 P/Invoke | Registro, WMI, Powercfg, BCD, diskpart, DISM, SFC |
| Ferramentas | PowerShell, netsh, sc.exe, schtasks, bcdedit, wimlib | Integração com ferramentas nativas do Windows |

---

## 🏗️ Estrutura do Projeto

```
KitLugia/
├── KitLugia.Core/                  # Núcleo: operações, diagnósticos e integrações
│   ├── Guardian.cs                 # Scanner de segurança (CVEs, mitigações, config)
│   ├── SystemTweaks.cs             # Ajustes de sistema via Registro
│   ├── OptimizationOrchestrator.cs # Orquestrador de otimizações
│   ├── PowerPlanManager.cs         # Gerenciamento de planos de energia
│   ├── WinpeBuilder.cs             # Construtor de WinPE personalizado
│   ├── WinbootManager.cs           # Gerenciamento de boot (BCD, bootsect)
│   ├── PartitionManager.cs         # Operações de disco (diskpart + WMI)
│   ├── NetworkManager.cs           # Gerenciamento de rede e DNS
│   ├── BloatwareManager.cs         # Remoção de bloatware (UWP)
│   ├── DeepUninstaller.cs          # Deep uninstall estilo Revo (3700+ linhas)
│   ├── CleanupManager.cs           # Limpeza do sistema
│   ├── RegistryCleaner.cs          # Scanner de registry órfão
│   ├── NativeUsn.cs                # Indexador USN/MFT nativo (540 linhas)
│   ├── NativeMft.cs                # MFT scanner via Rust FFI
│   ├── PathRepair.cs               # Diagnóstico e reparo de PATH
│   ├── PortableAppScanner.cs       # Scanner de apps portáteis
│   └── ...                         # 90+ módulos
├── KitLugia.GUI/                   # Interface WPF
│   ├── Pages/                      # 40+ páginas funcionais
│   │   ├── DashboardPage           # Visão geral do sistema
│   │   ├── OptimizationPage        # Otimizações de sistema
│   │   ├── IntegrityPage           # Guardian / segurança
│   │   ├── CleanupPage             # Limpeza
│   │   ├── NetworkPage             # Rede
│   │   ├── GameBoostPage           # GameBoost
│   │   ├── AppsPage                # Programas + bloatware + resíduos
│   │   ├── ServicesPage            # Serviços e tarefas agendadas
│   │   ├── ProcessMonitorPage      # Monitor de processos
│   │   ├── WinbootPage             # Boot e partições
│   │   ├── IsoEditorPage           # Editor de ISO
│   │   ├── PrivacyPage             # Privacidade (130+ settings)
│   │   ├── QuickInstallPage        # Instalação rápida (winget)
│   │   └── ...                     # +20 páginas adicionais
│   ├── Services/                   # serviços em background
│   │   ├── TrayIconService.cs      # Tray icon + monitoramento + GameBoost
│   │   └── ...                     # MemoryDiagnostics, LeakProfiler
│   ├── Windows/                    # Janelas auxiliares
│   │   ├── HunterWindow            # Hunter Mode (Revo-style)
│   │   ├── PathExplorerWindow      # Explorer de PATH
│   │   └── ...                     # DeepUninstall, BcdCleaner, Preset
│   └── Helpers/                    # AppIconHelper, ProgramIconHelper
├── rust_native/                    # Biblioteca nativa Rust
│   └── src/
│       ├── lib.rs                  # FFI exports (1000+ linhas)
│       └── mft.rs                  # MFT scanner raw (920+ linhas)
├── KitLugia.Updater/               # Aplicação de atualização
├── docs/                           # Documentação complementar (19 arquivos)
├── Deploy.ps1                      # Build, empacotamento e hash SHA-256
├── KitLugia.sln                    # Solução principal
└── global.json                     # Versão do SDK .NET
```---

## 🔨 Build e Execução

### Pré-requisitos

- [Git](https://git-scm.com/)
- [.NET SDK 10.0](https://dotnet.microsoft.com/) (`global.json` define a versão exata: 10.0.301)
- Windows 10 (1903+) ou Windows 11
- **Rustup** + toolchain MSVC (para compilar o módulo nativo)
- **Visual Studio 2022** ou Build Tools com ferramentas C++ (opcional)

### Clonar e compilar

```bash
git clone https://github.com/luigiarrud4/KitLugia-AVTest.git
cd KitLugia-AVTest

# Compilar módulo Rust (recomendado)
cargo build --manifest-path rust_native/Cargo.toml --release

# Compilar e executar
dotnet restore KitLugia.sln
dotnet build KitLugia.sln --configuration Release
dotnet run --project KitLugia.GUI --configuration Release
```

### Publicar (self-contained)

```bash
dotnet publish KitLugia.GUI -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

### Deploy automatizado

```powershell
.\Deploy.ps1
gh release upload v2.0.20 ./Publish/KITLUGIA2.zip ./Publish/KITLUGIA2.zip.sha256 --clobber
```

> Execute como administrador somente quando a operação exigir.

---

## 📋 Requisitos

| Componente | Mínimo | Recomendado |
|-----------|--------|-------------|
| OS | Windows 10 (1903+) | Windows 11 24H2 |
| CPU | Intel i3 / AMD Ryzen 3 | Intel i7 / AMD Ryzen 7 |
| RAM | 4 GB | 16 GB |
| Armazenamento | 2 GB livres | 10 GB livres (SSD) |
| Rustup | Opcional | Requerido para funcionalidade completa |
| Permissões | Usuário padrão | Administrador |

---

## ⚠️ Aviso de Segurança

O KitLugia pode alterar o Registro, serviços, plano de energia, configurações de rede, componentes de boot, arquivos do sistema, partições e aplicativos instalados.

**Antes de usar funções de alteração:**
1. Crie um ponto de restauração e mantenha um backup
2. Confirme o disco, partição, adaptador ou pacote selecionado
3. Teste em uma máquina de desenvolvimento antes de usar em produção
4. Esteja preparado para reiniciar o Windows ou usar mídia de recuperação
5. Leia o resultado da operação: algumas mudanças não são totalmente reversíveis

| Classificação | Significado |
|--------------|-------------|
|🟢 Baixo | Leituras, diagnósticos e operações geralmente reversíveis |
|🟡 Médio | Altera configurações do sistema, rede ou serviços |
|🔴 Alto | Remove pacotes, altera segurança/boot ou escreve em mídia física |

---

## 🧩 O que o KitLugia **não** é

- **Não é antivirus nem EDR** — o Guardian detecta configurações frágeis, mas não substitui proteção em tempo real
- **Não é uma garantia automática de FPS ou latência** — os ajustes dependem do hardware e drivers
- **Não substitui backup ou ponto de restauração** — use sempre antes de operações de risco alto
- **Não corrige qualquer corrupção** — depende da imagem do Windows e das ferramentas do sistema
- **Não é totalmente portátil** — algumas funcionalidades variam entre versões e edições do Windows

---

## 🤝 Contribuindo

Antes de abrir um pull request:

1. **Teste** as alterações em uma instalação de desenvolvimento do Windows
2. **Explique** quais arquivos, chaves, serviços ou ferramentas externas são envolvidos
3. **Informe** se a mudança exige administrador, reinicialização ou mídia de recuperação
4. **Descreva** como reverter a alteração quando ela tocar Registro, boot, rede ou segurança
5. **Não inclua** binários gerados, pastas `bin/obj` ou artefatos locais
6. **Documente** qualquer nova dependência ou ferramenta externa necessária

---

## 📄 Documentação

| Documento | Descrição |
|----------|-----------|
| [DEPLOY.md](DEPLOY.md) | Build, empacotamento e publicação |
| [AGENTS.md](AGENTS.md) | Contexto e orientações de desenvolvimento |
| [ROADMAP_REVO.md](ROADMAP_REVO.md) | Roadmap de features Revo-style |
| [EXM_TWEAKS_REFERENCE.md](EXM_TWEAKS_REFERENCE.md) | Referência completa de ajustes |
| [docs/MULTIISO_BOOT_ARCH.md](docs/MULTIISO_BOOT_ARCH.md) | Arquitetura de boot Multi-ISO |
| [docs/KITLUGIA_WINPE.md](docs/KITLUGIA_WINPE.md) | Documentação do WinPE builder |
| [docs/DOWNGRADE_BUILD_PLAN.md](docs/DOWNGRADE_BUILD_PLAN.md) | Plano de downgrade de build |
| [docs/ISO_EDITOR_WIMLIB_PLAN.md](docs/ISO_EDITOR_WIMLIB_PLAN.md) | Editor de ISO com wimlib |
| [docs/PATH_EXPLORER_GUIDE.md](docs/PATH_EXPLORER_GUIDE.md) | Guia do Explorer de PATH |
| [LICENSE](LICENSE) | Licença MIT |

---

## 📜 Licença

Distribuído sob a [Licença MIT](LICENSE).

---

<div align="center">

Desenvolvido por [Luigi Arruda](https://github.com/luigiarrud4)

</div>