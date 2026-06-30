# Dossiê: Integração 7-MAX no KitLugia

**Data:** 29 de Junho de 2026  
**Projeto:** KitLugia V2 - Windows Optimization Utility  
**Versão:** 2.0.29  
**Framework:** .NET 10.0 / WPF

---

## 1. Sobre o 7-MAX

### 1.1 O que é
Programa de otimização de memória que aumenta performance de aplicações em 10-20% através do uso de Large Pages.

### 1.2 Como Funciona
- Windows usa páginas pequenas de 4 KB por padrão
- 7-MAX força uso de **Large Pages** de 2 MB
- Reduz latência de RAM para aplicações memory-intensive
- Intercepta chamadas de alocação de memória (`VirtualAlloc`)

### 1.3 Detalhes Técnicos
- **Versão atual:** 7-max 24.01 (2024-05-10)
- **Desenvolvedor:** Igor Pavlov (criador do 7-Zip)
- **Licença:** GNU LGPL (gratuito)
- **Plataforma:** Windows x86/x64
- **Tamanho:** 155 KB

### 1.4 Casos de Uso
- Programas de compressão (7-Zip)
- Aplicações que usam muita memória
- Softwares sensíveis à latência de RAM
- Serviços de servidor em 64-bit Windows

### 1.5 Avisos Importantes
- **Pode ser instável** em alguns sistemas
- Requer privilégios elevados (SeLockMemoryPrivilege)
- Memória física deve ser contígua (fragmentação pode impedir alocação)
- Memória é non-pageable (sempre residente)

---

## 2. Implementação Windows API - Large Pages

### 2.1 Requisitos
1. **SeLockMemoryPrivilege** - Privilégio necessário (via `AdjustTokenPrivileges`)
2. **GetLargePageMinimum()** - Obtém tamanho mínimo da large page (geralmente 2 MB)
3. **VirtualAlloc** com flag `MEM_LARGE_PAGES | MEM_COMMIT | MEM_RESERVE`
4. Alinhamento deve ser múltiplo do tamanho da large page

### 2.2 Código Exemplo (C++)
```cpp
// 1. Habilitar privilégio
AdjustTokenPrivileges(hToken, FALSE, &tp, 0, NULL, NULL);

// 2. Obter tamanho mínimo
SIZE_T pageSize = GetLargePageMinimum();

// 3. Alocar com Large Pages
char *largePageMemory = (char*)VirtualAlloc(
    NULL, 
    n_bytes, 
    MEM_COMMIT | MEM_LARGE_PAGES, 
    PAGE_READWRITE
);
```

### 2.3 Limitações Técnicas
- Memória física deve ser contígua
- Fragmentação após longo uptime pode impedir alocação
- Memória é sempre read/write e non-pageable
- Não faz parte do working set (não é pageable)
- Não sujeito a job limits
- Deve ser reservada e cometida em uma única operação
- WOW64 em Intel Itanium não suporta 32-bit applications

---

## 3. Análise do Projeto KitLugia

### 3.1 Estrutura do Projeto
```
KitLugia/
├── KitLugia.Core/           # 70+ módulos de lógica de negócio
│   ├── SystemTweaks.cs      # Registry e configurações de sistema
│   ├── Guardian.cs          # Scanner de vulnerabilidades (291KB)
│   ├── NetworkManager.cs    # Gerenciamento de rede
│   ├── MemoryOptimizer.cs   # Otimização de memória
│   └── ...
├── KitLugia.GUI/            # Interface WPF
│   ├── Pages/
│   │   ├── GameBoostPage.xaml.cs  # Página de GameBoost
│   │   └── TraySettingsPage.xaml.cs
│   ├── Services/
│   │   └── TrayIconService.cs     # Serviço de tray (4286 linhas)
│   └── ...
└── KitLugia.sln
```

### 3.2 Configuração Atual
- **TargetFramework:** net10.0-windows10.0.26100.0
- **SDK Version:** 10.0.301
- **Versão:** 2.0.29
- **Plataforma:** Windows 10 (1903+) / Windows 11

### 3.3 Funcionalidades Relevantes
- **GameBoost:** Otimização de processos em foreground
- **ProBalance:** Gerenciamento de throttling de processos
- **MemoryOptimizer:** Limpeza e otimização de RAM
- **TrayIconService:** Monitoramento contínuo em background

---

## 4. Como GameBoost e TrayIconService Funcionam

### 4.1 GameBoostPage.xaml.cs
**Responsabilidade:** Interface para controle do GameBoost

**Fluxo:**
1. Usuário ativa toggle `TglGameBoost_Checked`
2. Chama `TrayService.InitializeGameBoost()`
3. Define `TrayService.GamePriorityEnabled = true`
4. Persiste configurações em JSON e Registry
5. Atualiza indicador de status (verde/vermelho)

**Configurações salvas:**
- `gameBoostEnabled`
- `trayEnabled`
- `autoStartEnabled`
- `unparkCpuEnabled`
- `closeToTray`
- `proBalance`

### 4.2 TrayIconService.cs
**Responsabilidade:** Monitoramento contínuo em background

**Componentes principais:**
- `DispatcherTimer _monitorTimer` - Timer de monitoramento
- `SetWinEventHook` - Detecção instantânea de foreground
- `EnableSeDebugPrivilege()` - Privilégios elevados
- P/Invoke APIs: ntdll, kernel32, user32

**Funcionalidades implementadas:**
- Monitoramento de processo em foreground
- Ajuste de prioridade de CPU/I/O/Page
- EcoQoS (Windows 11 Power Throttling)
- Thread Memory Priority
- Thread Efficiency Mode (P-Cores Only, Win11 24H2+)
- Game Mode (SetProcessGameClassInfo)
- Timer Resolution (NtSetTimerResolution)
- Win32PrioritySeparation Registry

**Fluxo de monitoramento:**
1. Timer dispara a cada X segundos
2. `GetForegroundWindow()` obtém janela ativa
3. `GetWindowThreadProcessId()` obtém PID
4. Aplica otimizações via P/Invoke
5. Cache de processos para performance

---

## 5. Integração Proposta: 7-MAX no KitLugia

### 5.1 Arquitetura Sugerida

#### Fase 1: Large Pages para KitLugia (Conservadora)
**Objetivo:** Usar Large Pages apenas internamente

**Implementação:**
```csharp
// KitLugia.Core/LargePageManager.cs
public static class LargePageManager
{
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetLargePageMinimum();
    
    [DllImport("kernel32.dll")]
    private static extern IntPtr VirtualAlloc(IntPtr lpAddress, IntPtr dwSize, 
        uint flAllocationType, uint flProtect);
    
    private const uint MEM_LARGE_PAGES = 0x20000000;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint PAGE_READWRITE = 0x04;
    
    private static bool _privilegeEnabled = false;
    
    public static bool EnableLargePagesPrivilege()
    {
        // Usar EnableSeDebugPrivilege existente no TrayIconService
        // Adicionar SeLockMemoryPrivilege
    }
    
    public static IntPtr AllocateWithLargePages(IntPtr size)
    {
        if (!_privilegeEnabled) return IntPtr.Zero;
        
        IntPtr pageSize = GetLargePageMinimum();
        if (pageSize == IntPtr.Zero) return IntPtr.Zero;
        
        // Alinhar tamanho ao múltiplo da página
        IntPtr alignedSize = (size + pageSize - 1) & ~(pageSize - 1);
        
        return VirtualAlloc(
            IntPtr.Zero,
            alignedSize,
            MEM_COMMIT | MEM_RESERVE | MEM_LARGE_PAGES,
            PAGE_READWRITE
        );
    }
}
```

**Uso em MemoryOptimizer.cs:**
```csharp
// Substituir alocações de memória internas
if (LargePageManager.EnableLargePagesPrivilege())
{
    IntPtr buffer = LargePageManager.AllocateWithLargePages(bufferSize);
    if (buffer != IntPtr.Zero)
    {
        // Usar buffer com Large Pages
    }
}
```

#### Fase 2: Hook em Processos Específicos (Experimental)
**Objetivo:** Aplicar Large Pages em processos configuráveis

**Implementação:**
```csharp
public static class LargePageManager
{
    private static HashSet<string> _whitelistedProcesses = new()
    {
        "7z", "7zFM", "winrar", "chrome", "firefox"
    };
    
    public static bool EnableLargePagesForProcess(int processId)
    {
        try
        {
            Process proc = Process.GetProcessById(processId);
            if (!_whitelistedProcesses.Contains(proc.ProcessName.ToLower()))
                return false;
            
            // DLL injection para hook VirtualAlloc
            InjectLargePageHook(processId);
            return true;
        }
        catch { return false; }
    }
    
    private static void InjectLargePageHook(int processId)
    {
        // Implementar DLL injection
        // Hookar ntdll!NtAllocateVirtualMemory
        // Substituir flags para incluir MEM_LARGE_PAGES
    }
}
```

**Integração no TrayIconService:**
```csharp
public bool LargePagesEnabled { get; set; } = false;

private void MonitorTick(object sender, EventArgs e)
{
    if (LargePagesEnabled && GamePriorityEnabled)
    {
        var foregroundPid = GetForegroundProcessId();
        LargePageManager.EnableLargePagesForProcess(foregroundPid);
    }
}
```

#### Fase 3: Integração Completa com GameBoost (Avançada)
**Objetivo:** Detecção automática e monitoramento

**Features:**
- Detecção automática de aplicações memory-intensive
- Monitoramento de fragmentação de memória
- Toggle na GameBoostPage
- Fallback automático se falhar
- Logging detalhado

---

## 6. Prós e Contras da Integração

### 6.1 Prós
- **Performance real:** 10-20% de ganho em aplicações memory-intensive
- **Diferencial competitivo:** Poucos softwares oferecem Large Pages
- **Base técnica sólida:** TrayIconService já tem privilégios, timers, monitoramento
- **Arquitetura compatível:** Encaixa perfeitamente com GameBoost/ProBalance
- **Consistência:** Usuário já acostumado com controles avançados

### 6.2 Contras
- **Risco de instabilidade:** O próprio 7-MAX avisa sobre instabilidade
- **Fragmentação de memória:** Após longo uptime pode falhar
- **Complexidade técnica:** DLL injection pode ser detectada como malware
- **Overhead de UI:** Mais uma opção em software já repleto de features
- **Debugging difícil:** Hook em processos externos é complexo

---

## 7. Recomendação de Implementação

### 7.1 Abordagem em 3 Fases

**Fase 1 (Conservadora) - Recomendada iniciar aqui:**
- Large Pages apenas para KitLugia próprio
- Usar em `MemoryOptimizer.cs` para alocações internas
- Sem hook em processos externos
- **Risco:** Baixo
- **Benefício:** Moderado
- **Tempo estimado:** 1-2 semanas

**Fase 2 (Experimental):**
- Hook em processos específicos (configurável pelo usuário)
- Lista branca de processos (7-Zip, jogos conhecidos)
- Avisos claros sobre instabilidade
- Fallback automático se falhar
- **Risco:** Médio
- **Benefício:** Alto
- **Tempo estimado:** 3-4 semanas

**Fase 3 (Avançada):**
- Integração completa com GameBoost
- Detecção automática de aplicações memory-intensive
- Monitoramento de fragmentação de memória
- Toggle na GameBoostPage
- **Risco:** Alto
- **Benefício:** Muito alto
- **Tempo estimado:** 6-8 semanas

### 7.2 Conclusão
Sim, vale a pena implementar. Comece pela Fase 1 para validar a abordagem sem riscos. Se funcionar bem, expanda gradualmente. O KitLugia já tem uma base sólida para isso.

---

## 8. Referências

### 8.1 Documentação Oficial
- [Large-Page Support - Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/memory/large-page-support)
- [7-max Official Website](https://7-max.com)
- [7-max at SourceForge](https://sourceforge.net/projects/sevenmax/)

### 8.2 APIs Windows
- `GetLargePageMinimum()` - memoryapi.h
- `VirtualAlloc()` - memoryapi.h
- `AdjustTokenPrivileges()` - securitybaseapi.h
- `NtAllocateVirtualMemory()` - ntdll.h

### 8.3 Flags e Constantes
- `MEM_LARGE_PAGES = 0x20000000`
- `MEM_COMMIT = 0x1000`
- `MEM_RESERVE = 0x2000`
- `PAGE_READWRITE = 0x04`
- `SeLockMemoryPrivilege`

---

## 9. Anexos

### 9.1 Estrutura de Arquivos do KitLugia
- `KitLugia.Core/KitLugia.Core.csproj` - Configuração do projeto Core
- `KitLugia.GUI/KitLugia.GUI.csproj` - Configuração do projeto GUI
- `KitLugia.GUI/Pages/GameBoostPage.xaml.cs` - Interface GameBoost
- `KitLugia.GUI/Services/TrayIconService.cs` - Serviço de tray
- `global.json` - Configuração do SDK .NET

### 9.2 Configurações Atuais
```json
// global.json
{
  "sdk": {
    "version": "10.0.301"
  }
}
```

```xml
<!-- KitLugia.Core.csproj -->
<TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
<AllowUnsafeBlocks>true</AllowUnsafeBlocks>
```

---

**Fim do Dossiê**
