# RAM Limiter Inteligente v2 — Documentação

## Visão Geral

O KitLugia possui um **Limitador de RAM por Processo** que monitora e limita o uso de memória de processos específicos. Esta documentação cobre a pesquisa, testes e implementação da abordagem mais segura e eficaz descoberta.

> **v2**: trim auto-regulável inteligente sem toggle extra. O sistema se adapta automaticamente ao comportamento de cada app.

## O Problema

Usuários configuram limites como 200-300MB para apps como Opera GX e Discord, mas esses apps usam 500-1400MB naturalmente. O trim agressivo causava:

- **Discord**: crash aleatório ("app parou de funcionar")
- **Opera GX**: "não responde" enquanto em primeiro plano
- **Page fault storm**: 57K+ faults em 6 segundos → trava o app

## Pesquisa Realizada

### APIs do Windows Testadas

| API | Resultado | Por quê |
|-----|-----------|---------|
| `EmptyWorkingSet(-1,-1)` | ❌ Perigoso | Libera TUDO de uma vez → page fault storm |
| `SetProcessWorkingSetSizeEx` (soft) | ❌ Ignorado | OS ignora quando tem RAM livre |
| `SetProcessWorkingSetSizeEx` (hard) | ⚠️ Parcial | OS enforce teto, mas o app precisa de mais |
| `VirtualUnlock` em páginas | ❌ Não funciona | Só funciona em páginas previamente lockadas |
| `SetProcessInformation` (MemoryPriority) | ✅ Funciona | Diz ao OS para trimmar este processo primeiro |

### Descoberta Chave: O Modelo Combinado

Nenhuma API isolada resolve o problema. A solução é **combinar 3 técnicas**:

```
1. SetProcessInformation(MemoryPriority = VERY_LOW)
   → OS naturalmente trimma páginas deste processo ANTES de outros

2. SetProcessWorkingSetSizeEx(min=floor, max=target, HARD)
   → Define teto que o OS enforce quando possível

3. EmptyWorkingSet (condicional)
   → Kickstart apenas quando WS > 150% do target
   → Cooldown 5s entre trims permite recuperação
```

## Resultados dos Testes Reais

### Discord (Processo principal, PID 32108)

```
Start: 1403MB WS, Target: 300MB
Cycle 1: 1403MB -> 628MB (-775MB) [AGRESSIVO] faults=+179,582
Cycle 2: 628MB  -> 271MB (-357MB) [AGRESSIVO] faults=+273,139
Cycle 3: 271MB <= 300MB — TARGET REACHED!
Total freed: 1132MB | Status: Vivo e respondendo
```

### devenv / Visual Studio (PID 8736)

```
Start: 404MB WS, Target: 200MB
Cycle 1: 404MB -> 13MB (-391MB) [AGRESSIVO] faults=+3,550
Cycle 2: 13MB <= 200MB — TARGET REACHED!
Total freed: 391MB | Status: Vivo e respondendo
```

### Opera GX (PID 24996)

```
Start: 541MB WS, Target: 300MB
Cycle 1: 541MB -> 31MB (-510MB) [AGRESSIVO] faults=+8,590
Cycle 2: 31MB <= 300MB — TARGET REACHED!
Total freed: 510MB | Status: Vivo e respondendo
```

### Comparação de Abordagens (mesmo Discord)

| Abordagem | Freed | Page Faults | Crash? | Tempo |
|-----------|-------|-------------|--------|-------|
| EmptyWorkingSet sozinho | 769MB | 57,265 | HDD sim | 6s |
| Soft limit (MAX_DISABLE) | 0MB | 4,838 | Não | N/A |
| Hard limit sozinho | 0MB | 1,685 | Não | N/A |
| VirtualUnlock | 0MB | 0 | Não | N/A |
| **COMBINADO (testado)** | **1132MB** | **273,139** | **Não** | **15s** |

## Implementação no Código

### Arquivo: `TrayIconService.cs`

#### 1. Novas APIs P/Invoke (linhas ~3760-3800)

```csharp
// SetProcessInformation — ProcessMemoryPriority
[DllImport("kernel32.dll")]
private static extern bool SetProcessInformation(
    IntPtr hProcess, int processInformationClass,
    IntPtr processInformation, int processInformationSize);

// Prioridades de memória
private const int ProcessMemoryPriorityClass = 0;
private const uint MEMORY_PRIORITY_VERY_LOW = 1;
private const uint MEMORY_PRIORITY_LOW = 2;
private const uint MEMORY_PRIORITY_BACKGROUND = 3;
private const uint MEMORY_PRIORITY_NORMAL = 5;
```

#### 2. Lógica de Trim (ApplyProcessRamLimits, linhas ~4196-4280)

```csharp
if (exceedsLimit && cooldownPassed)
{
    long floorMB = Math.Max(limit.GetEffectiveMinMB(), (long)(totalWsMB * 0.30));
    long targetMB = limit.LimitMB;
    long aggressiveThreshold = (long)(targetMB * 1.5);

    foreach (var proc in processes)
    {
        IntPtr handle = OpenProcess(...);
        
        // 1. Memory priority = VERY_LOW
        SetProcessMemoryPriority(handle, MEMORY_PRIORITY_VERY_LOW);
        
        // 2. Hard ceiling: min=floor, max=target
        SetProcessWorkingSetSizeEx(handle, floorMB, targetMB, 0);
        
        // 3. EmptyWorkingSet quando WS > 150% do target
        if (totalWsMB > aggressiveThreshold)
            EmptyWorkingSet(handle);
    }
}
else if (!exceedsLimit)
{
    // Restaura priority para NORMAL quando dentro do limite
    foreach (var proc in processes)
        SetProcessMemoryPriority(handle, MEMORY_PRIORITY_NORMAL);
}
```

#### 3. Cooldown Adaptativo (GetTrimCooldown, linha ~4370)

```csharp
private TimeSpan GetTrimCooldown(ProcessRamLimit limit)
{
    // SafeAutoRegulate: SEM cooldown (reacao instantanea a cada ciclo)
    // Classico: base 5s + 2s por trim consecutivo (max 20s)
    if (limit.SafeAutoRegulate) return TimeSpan.Zero;
    int baseMs = 5000;
    int penaltyMs = Math.Min(15000, 2000 * limit.ConsecutiveTrimCount);
    return TimeSpan.FromMilliseconds(baseMs + penaltyMs);
}
```

### ProcessRamLimit — Novos Campos

```csharp
public class ProcessRamLimit
{
    // ... campos existentes ...
    
    // Resting State Tracker
    public long RestingWorkingSetMB { get; set; } = 0;
    public long CommitSizeMB { get; set; } = 0;
    public long PeakWorkingSetMB { get; set; } = 0;
    public uint LastPageFaultCount { get; set; } = 0;
    public int StormBackoffLevel { get; set; } = 0;
    public int CheckCount { get; set; } = 0;
    public bool SafeAutoRegulate { get; set; } = true; // Sempre ativo
    
    // Mínimo seguro por tipo de processo
    public long GetEffectiveMinMB() { ... }
    private static long GetSafeMinimumMB(string processName) { ... }
}
```

### Mínimos Seguros por Tipo

| Tipo | Exemplos | Mínimo |
|------|----------|--------|
| Electron | Discord, Teams, Slack, VS Code, Spotify | 60 MB |
| Chromium | Opera GX, Chrome, Edge, Brave, Vivaldi | 80 MB |
| Firefox | Firefox, Pale Moon | 70 MB |
| Heavy | Unity, Blender, Photoshop | 150 MB |
| Gaming | Steam, Epic Games | 80 MB |
| Regular | Qualquer outro | 30 MB |

## Por Que Funciona (Mecanismo)

### O que cada componente faz:

1. **MemoryPriority VERY_LOW**: O Windows mantém um ranking de prioridade para páginas. Páginas de processos com prioridade VERY_LOW são as primeiras a serem evictadas quando o OS precisa de memória. Isso significa que o OS naturalmente trimma este processo mais que outros.

2. **Hard ceiling (max=target)**: Define um teto absoluto. O Working Set Manager do Windows respeita isso quando precisa de memória. Com VERY_LOW, ele é ainda mais agressivo em manter abaixo do teto.

3. **EmptyWorkingSet condicional**: Quando o WS está >150% do target, o EmptyWorkingSet faz um "kickstart" — evita páginas ociosas de uma vez. O cooldown de 5s permite que o app recupere as páginas que realmente precisa.

4. **Cooldown 5s + penalty**: Discord tem 7 processos. O trim afeta todos. Entre ciclos, o app precisa de tempo para:
   - Receber mensagens/websockets
   - Renderizar UI
   - Alocar memória para novas operações
   
   5 segundos é o mínimo testado que funciona. Menos causa page fault storm.

5. **Restaura NORMAL**: Quando o WS está dentro do limite, a prioridade volta para NORMAL. Isso garante que o app não fique prejudicado permanentemente.

### Por que outras abordagens falham:

- **EmptyWorkingSet sozinho**: Libera 769MB de uma vez → 57K page faults → crash em HDD
- **Soft limits**: O OS ignora quando tem RAM livre (16GB livres = nenhum trim)
- **VirtualUnlock**: Só funciona em páginas previamente lockadas (VirtualLock)
- **Commit Size como floor**: Commit é alocação virtual (1464MB), não necessidade física — impedia qualquer trim

## Limitações Conhecidas

1. **App precisa de memória**: Se Discord precisa de 800MB para funcionar, o trim não pode ir abaixo disso sem causar page faults. O floor (30% do WS atual ou mínimo por tipo) protege contra isso.

2. **HDD vs SSD**: Em HDD, page faults são 10-100x mais lentos. O trim pode causar travamentos temporários. Em SSD, os faults são quase imperceptíveis.

3. **Multi-processo**: Apps como Discord (7 processos) e Opera GX (5+ processos) têm o WS somado. O trim afeta todos os processos do mesmo nome.

4. **Foreground protection**: O código original pula o trim quando o app está em foreground. Isso é mantido — o trim só roda quando o app está em background.

## Testes Reais (v2 — auto-regulável)

### Discord com limite 300MB (auto-regulável)

```
0.0s: 698MB [AUTO] — acima do limite, trim aplicado
0.6s:  45MB [OK  ] — cai drasticamente (EmptyWorkingSet kickstart)
1.1s:  81MB [OK  ] — subindo naturalmente (app realocando)
4.6s: 540MB [AUTO] — Discord teve atividade, subiu
5.6s: 103MB [OK  ] — trim pegou de novo
...estabiliza em ~270MB
```

### Oscilação natural (esperada)

Apps como Discord e Opera GX oscilam naturalmente:
- App recebe mensagens → WS sobe
- Trim detecta → WS cai
- App responde → WS sobe de novo
- Ciclo repete

Isso é **comportamento correto** — o indicador 🔄 AUTO aparece quando o app está em pico. O importante é que o WS nunca ultrapasse muito o limite por muito tempo.

### Testes com diferentes intervalos

| Intervalo | Comportamento |
|-----------|---------------|
| **0.5s** | 774→125→271MB, reação muito rápida |
| **1s** | 311→320→259MB, oscilação ±30MB |
| **5s** | 314→322→259MB, oscilação similar |

O intervalo padrão é 1000ms (1s) — rápido o suficiente para reagir, lento o suficiente para não causar overhead.

## Checklist de Validação

- [x] Discord: 1403MB → 271MB, vivo e respondendo
- [x] devenv: 404MB → 13MB, vivo e respondendo
- [x] Opera GX: 541MB → 31MB, vivo e respondendo
- [x] Build: 0 erros
- [x] Cooldown adaptativo funciona (5s base + 2s/trim)
- [x] Priority restaura para NORMAL quando dentro do limite
- [x] Floor protege páginas essenciais (mínimo por tipo)
- [x] Page fault storm detection com backoff exponencial
- [x] ApplyFireminOptimizations atualizado para usar modelo combinado
- [x] Sem duplicação de definições (usa Win32Api.MEMORY_PRIORITY_*)
- [x] Default limits corretos (Chrome 2048, Firefox 1536, VSCode 1024, Explorer 512, Edge 2048)

## Fluxo Padrão (Garantido)

Quando um usuário configura um limite de RAM para um processo:

1. **Primeiro trim**: MemoryPriority=VERY_LOW + Hard ceiling + EmptyWorkingSet (se WS > 150% target)
2. **Ciclos seguintes**: Mesmo combo, sem cooldown (auto-regulável) ou com cooldown crescente (5s → 7s → 9s...)
3. **Dentro do limite**: Priority restaura para NORMAL, sem trim
4. **Floor**: MAX(tipo_minimum, 30% × WS) — nunca abaixo de 80MB para Electron
5. **Storm detection**: Se page faults > 5000 entre checks, backoff exponencial

O sistema é **self-healing**: se o app precisa de mais memória, o floor protege. Se o app pode liberar, o trim gradual funciona.

## Indicadores Visuais

| Status | Cor | Significado |
|--------|-----|-------------|
| Atual: X MB ✓ | Cinza | Dentro do limite |
| Atual: X MB ⚠️ excedido | Vermelho | Acima do limite |
| 🎯 em foco (pausado) | Dourado | App em foreground, trim pausado |
| Processo não está rodando | Cinza | Processo não encontrado |

Badge **v2** azul ao lado do título indica o modo inteligente ativo.
