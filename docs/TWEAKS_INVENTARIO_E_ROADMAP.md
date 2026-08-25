# TWEAKS — Inventário do SystemTweaks.cs & Roadmap de Faltantes

> Gerado em 24/08. Base: `KitLugia.Core/SystemTweaks.cs` (9.885 linhas, 555 métodos públicos, já é `partial class`).
> Fontes comparadas: Atlas OS (tweaks.yml + Performance.psm1), WinAurex, wintweaks.pro, Microsoft Learn (Recall/Copilot GPOs).

---

## PARTE 1 — O que JÁ TEMOS (inventário por categoria)

### ⚡ Performance / Latência / Gaming
- Win32PrioritySeparation (get/set), SystemResponsiveness gaming, GamingMode
- FullGamingLatencyProfile + Revert; OptimizeGamingLatency; CheckGamingLatencyStatus
- TimerResolution (toggle), TimerCoalescing (disable), GlobalTimerResolution (revert)
- CoreParking (disable), UnparkCpuPowerConfig, PowerThrottling (disable)
- InputLatency (opt/rev), InputQueueSize, NetworkThrottling (disable)
- NagleAlgorithm (dis/rev), TcpIpLatency, NetworkDriverOptimizations, OptimizeNetworkTransfer
- MemoryPrioritizationDse, MemoryUsage toggle, SegmentHeap, LargeCache, RmCacheLoc
- ThirdLevelCache, IoPageLockLimit, AutoCache
- NvMe latency (detect/opt/rev), HddFix
- GPU: VramTweak (+auto+recommended), DpcLatency, FrameQueue low-latency, Preemption,
  IdleSchedule disable, PowerLatency, TdrDelay increase, MPO toggle, MSI mode,
  Nvidia PowerMizer max perf, AMD AntiLag, Intel DynamicTuning, HAGS, GdiScaling
- VisualFX opt/rev, ExtremeVisuals, AnimationEffectMaxMin, IconCache

### 🖥️ Boot / Desligamento
- TurboBoot, VerboseStatus, NoGuiBoot, BootLog
- FastStartup (3 implementações!), FastShutdown, ShutdownSpeed/Acceleration
- StartupDelay opt/rev, ServiceStartup opt/rev/revert
- NoAutoReboot (Windows Update), CrashAutoReboot, AutoEndTasks, AeDebug
- BootMenuTimeout zero, HungAppTimeout, WaitToKillApp/Service, NumLock no boot

### 🔒 Privacidade / Telemetria
- DiagTrack (svc), DiagnosticData, TelemetryScheduledTasks batch
- WebSearch/Bing, MSACloudSearch, AADCloudSearch, DeviceSearchHistory, AutoSuggest, AppendCompletion
- Ads (lock screen), PersonalizedAds, OfferSuggestions, SettingsSuggestions, TipsAndSuggestions,
  TailoredExperiences, WindowsFeedback, StartMenuAppSuggestions
- WCE (Windows Customer Experience), VisualStudioTelemetry, ErrorReporting, CustomInking
- DiagnosticServices, NDU, PCA, GoogleUpdateTask, EdgeUpdateTask, AutoInstallationApps, Autoplay
- RemoteRegAccess, VBSCodeIntegrity
- Sudo (novo Win11), ProtectedPrintMode, LAPS, AppControl, RustKernel, PersonalDataEncryption, WiFi7, BluetoothLE, SHA3 detect

### 🎨 Explorer / UI
- Extensões ocultas/visíveis, Hidden/System, ThisPC default, LaunchTo
- ShortcutText "- Atalho", RecentFiles, FrequentFolders, SyncProviderNotifications
- StartMenu: MostUsed, RecentlyAdded/Opened, Recommendations, AccountNotifications
- Context menu: CmdAdmin, PowerShellAdmin, Notepad, VsCode, CopyAsPath, TakeOwnership,
  ForceClose, ForceStopUnlock, ClassicContextMenu, Win10Context, backup/restore completo
- DarkMode toggle, LockScreen disable, MenuShowDelay, MouseHoverTime, SnippingPrintScreen
- LowDiskSpaceChecks, Autoplay, ClearPageFile, MemoryPagination

### 💾 Serviços & Sistema
- MSDefender on/off, PrintSpooler, WindowsSearch, Hibernate, HybridSleep, Sleep,
  SystemRestore, ScheduledDefrag, AutoDefragIdle, PrefetchParameters, BootOptimize
- BackgroundApps (GlobalUserDisabled), GameBar/GameDVR, AUOptions, AutoWindowsUpdates
- SetServiceStartup genérico, GetServiceStartMode
- Power plans: UltimatePerformance, BitsumHighest, ImportAndActivatePowerPlan
- Bloatware: Get status, remove deep, reinstall; OneDrive uninstall

### 🌐 Rede
- DNS servers set, DNS diagnostics, AdapterAutoTune, Ethernet reset, MAC (via AdapterManager)

---

## PARTE 2 — FALTANTES (comparação com Atlas OS / guias 2025) — para debater

Legenda: 🟢 recomendo adicionar | 🟡 opcional/debatível | 🔴 evito (placebo ou quebra algo)

### Alta prioridade (impacto real comprovado)

| # | Tweak | Fonte | Por quê |
|---|-------|-------|---------|
| 1 | 🟢 Disable Sleep Study (`wevtutil set-log "Microsoft-Windows-SleepStudy/Diagnostic" /e:false` + task AnalyzeSystem) | Atlas | Loga uso de energia continuamente; I/O constante em notebooks Modern Standby |
| 2 | 🟢 NTFS disablelastaccess + disable8dot3 (`fsutil behavior set`) | Atlas/wintweaks | Reduz escrita a cada acesso a arquivo. SSD e HDD ganham |
| 3 | 🟢 Fault Tolerant Heap off (`HKLM\SOFTWARE\Microsoft\FTH, EnableFTH=0`) | Atlas | Monitor de crashes aplica shims automáticos = overhead sem função p/ usuário final |
| 4 | 🟢 Recall/WindowsAI off (`HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI`: DisableAIDataAnalysis=1, AllowRecallEnablement=0) | MS Learn | Copilot+ PCs tiram screenshots contínuos da tela. Privacidade crítica em 24H2+ |
| 5 | 🟢 Delivery Optimization (DODownloadMode=0/1 + bandwidth cap) | WinAurex/Atlas | Upload P2P de updates engole banda durante jogos |
| 6 | 🟢 Activity Feed/Timeline off (EnableActivityFeed=0, UploadUserActivities=0) | Atlas | Sincroniza histórico de atividades — telemetria pura |
| 7 | 🟢 App Launch Tracking off (Start_TrackProgs=0) | Atlas | Rastreia abertura de apps para Timeline |
| 8 | 🟢 Online Speech Recognition off (HasAccepted=0) | Atlas | Envia voz para nuvem se ativado |
| 9 | 🟢 Lock screen camera off | Atlas | Câmera acessível na tela de bloqueio = risco físico |
| 10 | 🟢 PerfTrack off (Enabled=0) | Atlas | Rastreia performance de software para a MS |
| 11 | 🟢 Experimentation off (Experiments=0) | Atlas | Permite a MS testar features remotamente na máquina |
| 12 | 🟢 RSOP Logging off | Atlas | Policy logging corporativo = telemetria |
| 13 | 🟢 WMP telemetry off (SendUserGUID=0) | Atlas | Envia GUID único ao tocar mídia |
| 14 | 🟢 Automatic Maintenance config (RandomDelay + wakeup disabled) | Atlas | Roda defrag/scans em idle acordando o PC |
| 15 | 🟢 Storage Sense config (off ou agressivo) | Atlas | Limpeza automática pode deletar coisas inesperadas |
| 16 | 🟢 Reserved Storage off (`DISM /Online /Set-ReservedStorageState /State:Disabled`) | Atlas debloat | Libera ~7GB mantendo WU funcionando |

### Média prioridade (gaming específico — debater)

| # | Tweak | Fonte | Debate |
|---|-------|-------|--------|
| 17 | 🟡 Disable Memory Compression (Disable-MMAgent -mc) | wintweaks | 16GB+: menos CPU. 8GB-: MANTER ligado. Precisa ser condicional à RAM |
| 18 | 🟡 Meltdown/Spectre mitigations off (FeatureSettingsOverride=3) | wintweaks | +5-15% kernel-heavy MAS segurança drasticamente reduzida. Só PC dedicado a jogo |
| 19 | 🟡 csrss.exe Realtime priority | wintweaks | Reduz micro-stutter MAS risco BSOD se csrss ficar starved |
| 20 | 🟡 Service Host Split off (SvcHostSplitThresholdInKB = RAM total) | Atlas | Menos processos svchost MAS perde isolamento de crash |
| 21 | 🟡 Fullscreen Optimizations off global | comunidade | Evidências mistas; placebo pós-DX12 segundo alguns. Jogos antigos ganham |
| 22 | 🟡 Windowed Game Optimization / Flip model (UserGpuPreferences DXGIPreferDXGIFlipModel) | WinAurex | Latência melhor em windowed DX10/11 + AutoHDR neles |
| 23 | 🟡 Xbox services manual/off (XblGameSave, XboxNetApiSvc) | vários | Só quem não usa loja/Game Pass |
| 24 | 🟡 LLMNR off (EnableMulticast=0) | Atlas | Segurança de rede local (spoofing); impacto zero doméstico |
| 25 | 🟡 SMB bandwidth throttling off | Atlas | Evita throttle de cópias SMB |
| 26 | 🟢 Edge preloading/background off (StartupBoostEnabled=0, BackgroundModeEnabled=0) | WinAurex | ~200MB RAM invisíveis mesmo fechado |
| 27 | 🟢 Sticky/Filter Keys prompt off (Flags accessibility) | QoL clássico | Popup ao apertar Shift 5x em jogo é irritante |
| 28 | 🟡 Mouse acceleration off (EPP=0) | gaming | Preferência pessoal; FPS competitivo quer off |
| 29 | 🟢 Taskview/Meet Now hide | Atlas QoL | Cosmético mas pedido constante |
| 30 | 🟢 Dynamic Lighting off (policy) | Atlas 2025 | RGB do Windows consome recursos |
| 31 | 🟢 Auto App Archival off (policy) | Atlas 2025 | Store arquiva apps não usados automaticamente |
| 32 | 🟢 Fax service disabled | serviços clássicos | Herança, ninguém usa |
| 33 | 🟢 Cloud Optimized Content off | Atlas taskbar | Widgets/sugestões consumindo rede |

### 🔴 Evitar (decisão consciente documentada)

| Tweak | Por que NÃO |
|---|---|
| DisablePagingExecutive universal | Atlas removeu dos defaults: evidência fraca, paging storm em 8GB |
| Superfetch/SysMain off universal | Em HDD ainda ajuda; melhor condicional ao tipo de disco |
| Timer Resolution 0.5ms fixo global | Já temos ToggleTimerResolution sob demanda |
| Debloat agressivo via lista gigante de services | Quebra WU/Store; Guardian já lida com o que importa |
| Kernel mitigations além de Meltdown (CFG/XFG off) | Segurança comprometida sem ganho mensurável |

---

## PARTE 3 — Vale a pena separar o SystemTweaks.cs?

**SIM — e o arquivo já é `partial class`, então o split é mecânico (zero mudança de assinatura).**

Proposta:

```
KitLugia.Core/Tweaks/
├── SystemTweaks.cs                 (fica: P/Invoke power, helpers, ToggleRegistryTweak, RevertPolicyTweak)
├── SystemTweaks.Gaming.cs          (~2.500 linhas: Gaming/Latency/GPU/NvMe/Nvidia/Amd/Intel/InputQueue/FrameQueue/MemoryPrioritization)
├── SystemTweaks.BootShutdown.cs    (~1.200: TurboBoot, FastStartup x3, VerboseStatus, NoGuiBoot, BootLog, Shutdown*, StartupDelay, MenuTimeout, NumLock)
├── SystemTweaks.Privacy.cs         (~2.000: telemetry/search/ads/cloud/experiments/DiagTrack/WCE/VS-telemetry)
├── SystemTweaks.ExplorerUI.cs      (~1.800: context menu x10, StartMenu x8, Explorer x10, DarkMode, LockScreen, Snipping, ShortcutText...)
├── SystemTweaks.Services.cs        (~900: Defender, Spooler, Search, Hibernate/Sleep, SystemRestore, Defrag, Prefetch, Bloatware, OneDrive)
└── SystemTweaks.Network.cs         (~700: DNS, adapters, Nagle, TcpIp, Throttling, TransferOptimize, diagnostics)
```

Ganhos: navegação (9.885 → arquivos < 2.500), merge conflicts menores, grep mais rápido.
Custo: zero em runtime (partial class compila igual). Mesma DLL, mesmas assinaturas.

---

## PARTE 4 — Perguntas para o debate

1. **Meltdown/Spectre**: adicionamos como toggle com warning grande, ou ficamos fora?
2. **Memory Compression**: adicionar condicional à RAM (< 12GB mantém ligado)?
3. **csrss realtime**: entra ou consideramos perigoso demais?
4. **Recall/AI**: aplicar por padrão na "Otimização Inteligente" ou só toggle manual?
5. **Xbox services**: detectar jogos instalados antes de oferecer o toggle?
6. A ordem do split acima faz sentido pra você?


---

## PARTE 5 — DECISÕES (24/08, pós-debate com o dono)

1. **Meltdown/Spectre**: FICA FORA. Guardian já classifica como mito; manter mensagem consistente entre páginas.
2. **Memory Compression**: FICA FORA. Já deu muita dor de cabeça no passado quando desativada — não reincidentir.
3. **csrss Realtime**: FORA (nem lembrado pelo dono = sem demanda real; risco de BSOD não compensa).
4. **Recall/AI off**: APROVADO — sempre off é bom. ✅ Implementado no TweakPage.
5. **Xbox services**: indefinido — deixar para sessão futura se houver demanda.
6. **Ordem do split**: decisão técnica fica a cargo do agente (proposta da PARTE 3 aprovada em princípio).

### Status dos 16 top achados: TODOS implementados no TweakPage (seção "TOP ACHADOS 2025", no rodapé da página)
- Sleep Study, NTFS Perf, FTH, Recall/AI, Delivery Optimization, Activity Feed, App Launch Tracking,
  Speech Online, Lock Screen Camera, PerfTrack, Experimentation, RSoP, WMP Telemetry,
  Automatic Maintenance, Storage Sense, Reserved Storage
- Todos com InfoButton explicativo + toggle + verificação de estado no load + reversão completa.

### Nota sobre Reserved Storage (pedido de explicação)
Reserva de ~7GB criada no Win10 1903 para garantir que Windows Update nunca fique sem espaço.
Desativar libera os 7GB imediatamente via DISM; o WU continua funcionando (em disco cheio ele pede
limpeza manual em vez de falhar). Só faz sentido desativar em discos bem gerenciados.


---

## PARTE 6 — AUDITORIA DO SYSTEMTWEAKS.CS (24/08, continuação)

### Números
- 555 métodos públicos | **99 MORTOS** (nunca chamados em GUI nem Core) | 245 usados só pelo WinTunePage

### 🔴 FAKES DETECTADOS — métodos que escrevem chaves de registro INEXISTENTES (placebo puro)

| Método falso | Chave inventada | Realidade |
|---|---|---|
| `OptimizeRustKernel` | `HKLM\...\Kernel: RustOptimization=1` | **Não existe.** A MS está portando win32k/DirectWrite para Rust (win32kbase_rs.sys), mas não há chave de "otimização". Valor escrito é ignorado pelo Windows |
| `OptimizeWiFi7` | `HKLM\SOFTWARE\Microsoft\WlanSvc\Parameters: WiFi7Optimization=1` | **Não existe documentada em lugar nenhum.** O driver Wi-Fi 7 se auto-configura |
| `ConfigureLAPS` (como está) | `LAPS: PostAuthenticationActions/PasswordComplexity` | LAPS real usa AD + `Windows LAPS` com schema diferente; essas chaves soltas não configuram nada sem infraestrutura de domínio |
| `EnablePersonalDataEncryption` | `HKCU\...\Policies\DataProtection: PersonalDataEncryption=1` | EDP (Enterprise Data Protection) foi descontinuado; a criptografia real de pastas no Win11 usa EFS/bitlocker-to-go via UI |

**Ação recomendada:** deletar esses 4 métodos + seus Is*Enabled (nunca expostos em página nenhuma — ninguém sentirá).

### 🟡 MORTOS LEGÍTIMOS (funcionam mas ninguém chama)
- **Context menu completo** (AddCmdAdmin/AddPowerShell/TakeOwnership/CopyAsPath/Notepad/VsCode/ForceClose/
  ForceStopUnlock + Remove*/Is*Added + BackupUserContextMenu/RestoreUserContextMenu): ~30 métodos prontos e funcionais,
  nunca ganharam UI. CANDIDATO PERFEITO para uma futura sub-aba "Menu de Contexto" no TweaksPage.
- **Gaming/GPU órfãos**: ToggleNvMeLatency, ToggleThirdLevelCache, ToggleVramTweak, ToggleGpuDpcLatency,
  ToggleNvidiaPowerMizer, ToggleMemoryPrioritizationDse, ToggleTcpIpLatencyTweak, ToggleAmdAntiLag,
  ToggleBootLog, ToggleNoGuiBoot + seus Is*. Os Apply/Revert correspondentes SÃO usados (GameBoostPage),
  os Toggle são versões redundantes. CANDIDATOS A DELEÇÃO após confirmar paridade com os Apply usados.
- **Diversos**: OptimizeMemory, OptimizeShutdownSpeed, OptimizeHungAppTimeout/WaitToKillApp (gêmeos dos já
  expostos), SetDnsServers+RunNetworkDiagnostics (NetworkPage tem os dela), AutoTuneNetworkAdapter,
  CreateDelayed/ElevatedStartupTask, ApplyUltimatePerformanceSettings/ApplyBitsumHighest (power plans órfãos).
- **Sudo (Win11 24H2)**: EnableSudo/IsSudoEnabled funcionais e órfãos — bom candidato a entrar no TweakPage futuro.

### ✅ Conclusão da auditoria
O SystemTweaks.cs tem ~18% de código morto (99/555), incluindo 4 tweaks PLACEBOS que escrevem
chaves inexistentes. Limpeza recomendada mas não urgente — nada quebra por estarem ali.



---

## PARTE 7 — IMPLEMENTADO (24/08, sessão noturna)

### ✅ Gerenciador do Menu de Contexto — NOVO
- **`KitLugia.Core/Tweaks/ContextMenuManager.cs`** (novo): enumera verbos estáticos (`*\shell`, `Directory\shell`,
  `Directory\Background\shell`, `DesktopBackground`, `Drive`, `AllFileSystemObjects`, `Folder`) + handlers COM
  (`shellex\ContextMenuHandlers` → CLSID → InprocServer32/LocalServer32).
  - Classificação Sistema vs Terceiros: DLL resolvida dentro de `%SystemRoot%` = Windows; senão = terceiro.
  - Desabilitar NÃO-destrutivo: verbos → valor vazio `LegacyDisable` na sombra HKCU\Software\Classes (merge HKCU>HKLM);
    handlers COM → CLSID em `Shell Extensions\Blocked` (mecanismo oficial).
  - Habilitar = remover a marcação. Deletar = exporta backup .reg ANTES (Backups\ContextMenu) e depois apaga.
  - `RestoreAllKitChanges()` desfaz globalmente bloqueios + sombras criados pelo Kit.
- **`Pages/WindowsSettings/ContextMenuPage.xaml(.cs)`** (nova página):
  - Lista virtualizada com badges WINDOWS/TERCEIROS/OFF, busca, filtros (todos/sistema/terceiros/desabilitados/habilitados).
  - **PRÉVIA VISUAL DO MENU** simulado no painel direito (itens cinza = desabilitados), separadores por escopo.
  - Painel de detalhe: nome, origem, escopo, tipo, CLSID, comando; botões Habilitar/Desabilitar e Deletar c/ backup.
  - Botão "Restaurar tudo (desfazer KitLugia)".
- **WindowsPage**: card verde "Menu de Contexto" logo abaixo do card Force Stop Unlock.
- **Navegação**: novo `PageType.ContextMenuManager` + factory em MainWindow.

### ✅ Fakes removidos do SystemTweaks.cs
Deletados (escreviam chaves de registro INEXISTENTES — placebo puro):
`OptimizeRustKernel` (Kernel\RustOptimization), `OptimizeWiFi7` (WiFi7Optimization),
`ConfigureLAPS` (sem AD não faz nada), `EnablePersonalDataEncryption` (EDP descontinuado)
+ seus Is*Enabled. Zero referências restantes no projeto. Build: 0 erros.
