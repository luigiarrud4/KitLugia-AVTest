# Roadmap: TrayIcon + Download Boost (Network Performance Engine)

## Visão Geral

Evoluir o TrayIconService para o centro de controle de desempenho de processos, incluindo otimização de rede/download por processo usando as técnicas mais recentes do Windows 11 24H2/25H2.

---

## Fase 1 — Base: Per-Process Engine (JÁ FEITO)

- [x] **Per-process CPU Limiter** — Job Object hard cap (CPU rate control)
- [x] **Overlay de configuração** — ProcessEngineConfigOverlay.xaml
- [x] **EcoQoS** — SetProcessInformation (ProcessPowerThrottling)
- [x] **Game Mode** — SetProcessGameClassInfo
- [x] **Timer Boost** — NtSetTimerResolution
- [x] **Thread Efficiency Mode** — P-Cores/E-Cores
- [x] **Memory Priority** — SetProcessInformation (ProcessMemoryPriority)
- [x] **ProBalance** — CPU throttling de background
- [x] **I/O Priority** — SetPriorityClass
- [x] **Page Priority** — SetProcessInformation

---

## Fase 2 — Network Download Boost (NOVA)

### 2.1 Per-Process TCP/IP Tuning

| Técnica | API / Comando | Impacto |
|---------|---------------|---------|
| **TCP congestion provider** | `netsh int tcp set supplemental` | Algoritmo de controle de congestionamento por template (Internet/Datacenter) |
| **TCP auto-tuning** | `netsh int tcp set global autotuninglevel=` | Janela de recepção dinâmica (normal/restricted/disabled) |
| **RSS (Receive Side Scaling)** | `netsh int tcp set global rss=enabled` | Distribui processamento de pacotes entre múltiplos cores |
| **RSC (Receive Segment Coalescing)** | `netsh int tcp set global rsc=enabled` | Agrupa pacotes na NIC — **melhora downloads, piora latência** |
| **Network Throttling Index** | Registry `HKLM\...\SystemProfile\NetworkThrottlingIndex` | Remove limite de banda para apps não-multimídia |
| **System Responsiveness** | Registry `HKLM\...\SystemProfile\SystemResponsiveness` | Prioridade de multimídia vs background |
| **Nagle's Algorithm** | Registry `TcpAckFrequency=1, TCPNoDelay=1, TcpDelAckTicks=0` | Desabilita delayed ACK por interface |
| **TCP Window Scaling** | Registry `Tcp1323Opts=1` | Janelas TCP > 64KB |
| **MaxUserPort** | Registry `MaxUserPort=65534` | Aumenta portas efêmeras disponíveis |
| **TCP Timed Wait Delay** | Registry `TcpTimedWaitDelay=30` | Libera portas TIME_WAIT mais rápido |

### 2.2 Per-Process QoS (Windows Filtering Platform / QoS2)

| Técnica | API | Descrição |
|---------|-----|-----------|
| **QoS2 (Quality of Service v2)** | `QOSCreateHandle`, `QOSAddSocketToFlow` | Prioriza tráfego de sockets específicos no kernel |
| **WFAP (Windows Filtering Platform)** | `Fwps*` | Filtro de pacotes por processo — priorização downstream |
| **Per-Process Bandwidth Limit** | `SetInformationJobObject` com `JobObjectNetRateControlInformation` | Limite de largura de banda por job object |
| **DSCP Marking** | Registry QOS / netsh | Marcação DiffServ para pacotes do processo |
| **Traffic Class** | `SetProcessInformation` (ProcessInformationClass) | Prioridade de tráfego de rede do processo |

### 2.3 Windows 11 24H2/25H2 — Novas Técnicas

| Técnica | Descrição |
|---------|-----------|
| **BBR2 congestion provider** | `netsh int tcp set supplemental template=internet congestionprovider=bbr2` — Controle de congestionamento baseado em bandwidth-delay (Beta no 24H2, estável no 25H2) |
| **Wi-Fi 7 support** | Otimização automática em hardware compatível |
| **ReFS Block Cloning** | Cópia de arquivos grandes até 94% mais rápida (Dev Drive) |
| **PowerThrottling Exclusions** | `SetProcessInformation` com `PROCESS_POWER_THROTTLING_EXECUTION_SPEED` — evitar EcoQoS em downloads críticos |
| **Server Message Block over QUIC** | Otimização SMB para transferências remotas |
| **Network-designed Resolvers** | DNS over HTTPS (DoH) nativo |

---

## Fase 3 — Integração no TrayIconService

### 3.1 Engine de Download Boost

```csharp
public class DownloadBoostEngine
{
    // Aplicado por processo quando em foreground ou configurado manualmente
    void ApplyDownloadBoost(int pid, DownloadBoostConfig config)
    {
        // 1. TCP global: congestion provider BBR2, auto-tuning normal, RSS, RSC
        // 2. Por processo: QoS flow (QoS2 API), DSCP marking, job object
        // 3. Registry: NetworkThrottlingIndex=0xFFFFFFFF, SystemResponsiveness=10
        // 4. Desabilitar EcoQoS se ativo (força execution speed máxima)
        // 5. Aumentar memory priority para cache de rede
    }

    void RevertDownloadBoost(int pid) { /* restaurar defaults */ }
}
```

### 3.2 Configuração no Overlay

Adicionar ao `ProcessEngineConfigOverlay.xaml`:

- **Download Boost** (toggle on/off)
- **Modo de Rede**: Jogos (latência) / Downloads (throughput) / Automático
- **Congestion Provider**: CUBIC / CTCP / BBR2 (se disponível)
- **Limite de Banda** (opcional): via Job Object Net Rate Control

### 3.3 Monitoramento

- **Network Usage per process** — Get-NetAdapterStatistics por processo
- **Detecção de download ativo** — Monitorar tráfego de rede por PID via `GetTcpTable2` / `GetExtendedTcpTable`
- **Auto-engage**: Se processo em foreground está fazendo download pesado (>5MB/s), ativar Download Boost automaticamente

---

## Fase 4 — Arquitetura

```
TrayIconService
├── GameBoost Engine (existente)
│   ├── V1_Balanced
│   ├── V2_StableFPS
│   ├── V3_Extreme
│   └── Custom (per-process overlay)
├── CPU Limiter Engine (novo - Fase 1 ✅)
│   └── Job Object hard cap
└── Download Boost Engine (novo - Fase 2)
    ├── TCP/IP Tuning (global)
    ├── QoS Per-Process (WFAP/QoS2)
    ├── Registry Tweaks
    └── Auto-detection (download traffic monitor)
```

### Fluxo de Monitoramento

```
MonitorTick (500ms-2s)
  → GetForegroundProcessId()
  → Verificar Engine atual (GameBoost / Custom)
  → Se DownloadBoost ativo:
       → Medir tráfego de rede do processo (GetTcpTable2)
       → Se >5MB/s: ApplyDownloadBoost()
       → Se <1MB/s por 10s: RevertDownloadBoost()
  → Se GameBoost ativo:
       → Aplicar otimizações normais (prioridade, I/O, etc.)
```

---

## Fase 5 — Técnicas Avançadas (Futuro)

| Técnica | Descrição | Status |
|---------|-----------|--------|
| **NIC Driver Optimization** | Ajustar InterruptModeration, RSS queues, MSI-X via registry da NIC | Pesquisa |
| **TCP Option SACK** | `Netsh int tcp set global sack=on` — Selective ACK | Global |
| **TCP Initial RTT** | `Netsh int tcp set global initialRtt=100` | Global |
| **NonBestEffortLimit** | Registry QoS `NonBestEffortLimit=0` — remove limite de banda QoS | Global |
| **Delivery Optimization** | Cache management, peer-to-peer tuning | Background |
| **DNS over HTTPS** | Forçar DoH no processo via política | Windows 11+ |
| **HTTP/3 (QUIC)** | Priorizar conexões QUIC via QoS | Pesquisa |
| **Programmatic Packet Prioritization** | `SetSocketOption(IP_PKTINFO, DSCP)` via hook | Experimental |

---

## Comparação com Concorrentes

| Ferramenta | Download Boost | Per-Process | TCP Tuning | QoS | Interface |
|------------|---------------|-------------|------------|-----|-----------|
| **KitLugia (visado)** | ✅ Tray + Auto | ✅ Sim | ✅ Avançado | ✅ WFAP | WPF |
| **TCP Optimizer** | ❌ Só global | ❌ | ✅ Completo | ❌ | WinForms |
| **NetLimiter** | ✅ Limit/Boost | ✅ | ❌ | ✅ traffic shaping | WPF |
| **CFosSpeed** | ✅ Traffic shaping | ✅ | ❌ | ✅ | WinForms |
| **WINSPAR** | ⚠️ CLI only | ❌ | ✅ | ❌ | CLI |
| **cFos EVE** | ✅ | ✅ | ⚠️ | ✅ | WinForms |

**Diferenciais do KitLugia:** Integração com GameBoost + ProBalance + CPU Limiter num único serviço de tray com overlay por processo.

---

## Referências

- [Windows QoS2 API](https://learn.microsoft.com/en-us/windows/win32/qos2/quality-of-service)
- [SetProcessInformation](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation)
- [Job Object Net Rate Control](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_net_rate_control_information)
- [Netsh TCP Commands](https://learn.microsoft.com/en-us/windows-server/networking/technologies/netsh/netsh-interface-tcp)
- [Windows Filtering Platform](https://learn.microsoft.com/en-us/windows/win32/fwp/windows-filtering-platform-start-page)
- [BBR2 on Windows](https://techcommunity.microsoft.com/t5/networking-blog/bbr-congestion-control-arrives-on-windows/ba-p/4127198)
