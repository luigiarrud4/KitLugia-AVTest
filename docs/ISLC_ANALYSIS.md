# ISLC 1.0.4.8 — Análise técnica do comportamento real

> Análise feita em 17/10/2026 a partir dos binários em
> `C:\Users\Lugia\Downloads\ISLC v1.0.4.8` (Intelligent Standby List Cleaner, Wagnardsoft).
> Métodos: decompilação do assembly gerenciado com **ILSpy 10.1.1** (os PDBs originais
> estavam presentes, então os nomes de tipos/métodos são os do autor), **IL pelo
> IDA Pro 9.0**, **metadata do PE** (`scripts/pe_metadata_dump`), **recursos
> embutidos/localização**, **string heap + ReadMe** e **medição** das APIs de
> memória na máquina local. O detalhamento de cada instrumento e o que só ele
> revelou está em [`ISLC_BINARY_FORENSICS.md`](ISLC_BINARY_FORENSICS.md).
>
> **Licença:** ISLC é *proprietary freeware* (`LICENSE.txt`, © Wagnardsoft). Este
> documento registra **fatos de comportamento e assinaturas de API** para
> interoperabilidade — não redistribui código-fonte decompilado nem trechos
> copiados. Nada aqui deve ser colado em `KitLugia.Core`.

---

## 1. Identidade dos binários

| Arquivo | Versão | SHA-256 |
|---|---|---|
| `Intelligent standby list cleaner ISLC.exe` | 1.0.4.8 | `11e4ed0b322adced3214c5a73ca1e807140341be6327b2a47a4c52434e73322f` |
| `ISLC.Core.dll` | 1.0.0.0 | `11dc5458940033634e40b855ec117a07edadf78d3c3fe2aca989098a46c590ac` |
| `config.json` (estado do usuário) | — | `22bfb5387dccf369544fa29f2fc59ded75125284139913de3cf4b464f2742534` |

**Stack:** .NET Framework 4.8, WPF (`MainWindowView.xaml` como `StartupUri`),
`RenderOptions.ProcessRenderMode = SoftwareOnly` na inicialização.

Dois assemblies:

- **`Intelligent standby list cleaner ISLC.exe`** — UI WPF + toda a lógica de
  decisão (`MainWindowViewModel`: timer, medição, `PurgeLogic`, comandos, hotkey,
  agendador de tarefas).
- **`ISLC.Core.dll`** — P/Invoke (`NtSetSystemInformation`, `NtSetTimerResolution`,
  `SetProcessInformation`), `SettingsManager` (JSON atômico), `AppMonitorWMI`
  (lista de exclusão) e `Win32_PrivilegeElevation`.

Nada de C++ nativo, nada de driver, nada de serviço: é um app de bandeja que
roda com elevação e chama a API de memória do kernel.

---

## 2. O que o ISLC faz (resumo em uma frase)

A cada `PollingRate` ms ele lê **o tamanho da standby list** e, se ela passou de
`StandbyListValue` **e** a memória física livre está abaixo de `FreeMemoryValue`
**e** nenhum processo da lista de exclusão está rodando, chama
`NtSetSystemInformation(SystemMemoryListInformation = 80, MemoryPurgeStandbyList = 4)`.

Não existe nada por processo: **nenhum** `EmptyWorkingSet`, **nenhum**
`SetProcessWorkingSetSize`, **nenhuma** prioridade de memória. É uma única
martelada global no cache.

---

## 3. Configuração (schema real do `config.json`)

| Campo | Tipo | Default | Papel |
|---|---|---|---|
| `StandbyListValue` | string (MB) | `"1024"` | Dispara o purge quando a standby **≥** este valor |
| `FreeMemoryValue` | string (MB) | `"1024"` | Dispara o purge apenas se a memória livre **<** este valor |
| `PollingRateSTR` | string (ms) | `"1000"` | Intervalo do timer |
| `CustomTimer` | bool | `false` | Liga o pedido de resolução de timer |
| `WantedResolution` | float (ms) | `1.0` | Resolução desejada (`× 10000` → unidades de 100 ns) |
| `ExclusionList` | string[] | `[]` | Nomes de `.exe` que pausam o purge enquanto rodam |
| `StartMinimized` | bool | `false` | Sobe já monitorando |
| `MinimizeOnClose` | bool | `false` | Fechar = bandeja |
| `AlwaysOnTop` | bool | `false` | Topmost |
| `DarkMode` | bool | `false` (usuário tinha `true`) | Tema |
| `HotkeyEnabled` / `HotkeyModifiers` / `HotkeyVKey` | bool/int/int | `false` / `3` (Ctrl+Alt?) / `80` (`VK_P`) | Atalho de purge manual |

Detalhe: o default do autor é **1024 MB fixo** para os dois limiares. Em máquina
de 32 GB isso é agressivo (a standby list de um Windows 11 ocioso já fica na casa
de vários GB).

---

## 4. Linha de comando (ReadMe oficial, confirmada nos argumentos)

| Argumento | Efeito |
|---|---|
| `-minimized` | Sobe minimizado e já monitorando |
| `-polling <ms>` | Sobrescreve o intervalo |
| `-listsize <MB>` | Sobrescreve `StandbyListValue` |
| `-freememory <MB>` | Sobrescreve `FreeMemoryValue` |
| `-runonce` | Um ciclo de poll e sai |
| `-purge` | Purge e sai (funciona mesmo com outra instância aberta) |

`-listsize` e `-freememory` "trabalham em par": se um não for passado, vale o do
config. `-polling` é ignorado com `-runonce`.

---

## 5. Medição — como o ISLC lê a standby list

Constantes e estruturas (todas em `MainWindowViewModel`, não em `ISLC.Core`):

```
SystemMemoryListInformation = 80        // classe para NtQuerySystemInformation
SYSTEM_MEMORY_LIST_INFORMATION          // 22 UIntPtr
  ZeroPageCount, FreePageCount, ModifiedPageCount, ModifiedNoWritePageCount,
  BadPageCount, PageCountByPriority_0..7, RepurposedPagesByPriority_0..7,
  ModifiedPageCountPageFile
```

Pseudo-código fiel do que roda a cada tick:

```
NtQuerySystemInformation(80, &info, sizeof(info), &ret)
standbyMB = (P0+P1+P2+P3+P4+P5+P6+P7) * 4096 >> 20
freeMB    = (ZeroPageCount + FreePageCount) * 4096 >> 20
```

Ou seja:

- **standby = soma dos 8 buckets de prioridade** (inclusive o de baixa prioridade).
- **"free memory" = páginas zeradas + páginas livres de verdade.** *Não* é
  "Available" do Gerenciador de Tarefas — Available inclui standby reclamável.
  Essa distinção é a parte que mais confunde quem compara os dois apps.
- Página fixa em 4096 (não usa `GetSystemInfo`). Funciona na prática em x64.

Também lê pagefile (`NtQuerySystemInformation` classe 18) só para exibir
"usado / total (%)".

### 5.1 Medição comparativa na máquina local (32 GB, Windows 11)

`scripts/standby_probe.ps1` (novo neste repo) lê as duas fontes no mesmo instante:

Primeira rodada (21:21):

| Hora | Available | `SystemCache` (psapi) | Standby (NtQuery 80) | FreePages | Diff |
|---|---|---|---|---|---|
| 21:21:05 | 17.087 MB | 16.833 MB | 17.078 MB | 8 MB | −245 MB |
| 21:21:06 | 17.045 MB | 16.844 MB | 17.045 MB | 0 MB | −201 MB |
| 21:21:07 | 17.033 MB | 16.871 MB | 17.031 MB | 1 MB | −160 MB |
| 21:21:08 | 17.031 MB | 16.915 MB | 17.027 MB | 3 MB | −112 MB |
| 21:21:09 | 17.017 MB | 16.928 MB | 17.015 MB | 1 MB | −87 MB |

Segunda rodada, dois minutos depois (21:23) — **o sinal da diferença inverteu**:

| Hora | Available | `SystemCache` (psapi) | Standby (NtQuery 80) | FreePages | Diff |
|---|---|---|---|---|---|
| 21:23:26 | 17.626 MB | 18.158 MB | 17.617 MB | 7 MB | **+541 MB** |
| 21:23:27 | 17.640 MB | 18.165 MB | 17.635 MB | 5 MB | +530 MB |
| 21:23:28 | 17.550 MB | 18.046 MB | 17.474 MB | 75 MB | +572 MB |
| 21:23:29 | 17.552 MB | 18.062 MB | 17.489 MB | 62 MB | +573 MB |
| 21:23:30 | 17.600 MB | 18.079 MB | 17.553 MB | 46 MB | +526 MB |

Conclusões medidas (não estimadas):

1. `GetPerformanceInfo().SystemCache` **acompanha** a standby list de perto
   (±3 %, e o sinal varia entre rodadas), mas é outra fonte: "páginas no cache do
   sistema", não "soma dos buckets de standby". Serve como aproximação; não é a
   mesma grandeza. Para decidir purge com precisão (e para separar prioridade),
   a leitura do ISLC é a correta.
2. **`FreePages` é praticamente zero** (0–75 MB) mesmo com ~17 GB "Available".
   Consequência prática: o gate de memória livre do ISLC é **quase sempre
   verdadeiro** — no fim, quem decide é só o tamanho da standby list. O segundo
   limiar evita purgar quando a máquina realmente tem páginas livres sobrando,
   não quando "tem RAM disponível".
3. `RepurposedPages` somava 39 GB e depois 53 GB na rodada seguinte: é contador
   **acumulado** de reaproveitamento desde o boot, não tamanho de lista. Não
   serve como métrica de estado.
4. `Available` (Task Manager) ≈ standby nesta máquina: quase toda a RAM
   "disponível" está em standby reclamável. É por isso que comparar
   "Available" com "standby" dá a impressão de que os dois números são o mesmo.

---

## 6. Decisão de purge (lógica exata)

```
flag = !Monitor.IsAppsExecuting            // nenhum .exe da exclude list rodando
if (flag && standbyMB >= StandbyListValue && freeMB < FreeMemoryValue)
    Purge()
```

Notas de comportamento:

- **Sem cooldown, sem histerese, sem detecção de storm.** A proteção contra
  repetição é emergente: depois de purgar, `standbyMB` cai perto de zero e a
  condição deixa de valer até o cache reconstruir.
- **`-runonce`** usa dois flags internos (`_runOnceChk1`/`_runOnceChk2`) para
  garantir que saia só depois de ter medido e decidido.
- **`PurgeCommand` é assíncrono** (`Task.Run` + `_isPurging`), então o tick não
  bloqueia a UI. `CanExecutePurge => !_isPurging`.
- **`AppState.IsActive`** separa "monitorando" de "coletando números": a UI
  atualiza sempre; o purge só com Start pressionado.

### 6.1 O purge em si

```
SetIncreasePrivileges("SeProfileSingleProcessPrivilege")   // token atual
NtSetSystemInformation(80, &(int)4, 4)                      // MemoryPurgeStandbyList
```

`SeProfileSingleProcessPrivilege` é obrigatório — sem ele a chamada volta
`STATUS_PRIVILEGE_NOT_HELD`. O `NtSetSystemInformation` retorna NTSTATUS (0 = ok);
o ISLC lança `Win32Exception` em falha.

**Falha silenciosa no guarda de privilégio (medida na 1.0.4.8):**
`AdjustTokenPrivileges` devolve **TRUE** mesmo quando o privilégio não foi
atribuído ao token — o erro real é `ERROR_NOT_ALL_ASSIGNED` (**1300**), que só se
lê com `GetLastError`, e o código não lê. Consequência: `SetIncreasePrivilege`
não lança, `SetIncreasePrivileges` devolve `true`, o `if (!...) return;` é pulado
e a falha **só aparece depois**, em `NtSetSystemInformation`
(`STATUS_PRIVILEGE_NOT_HELD`). O erro reportado então culpa a API errada.
Fora da elevação esse é o caminho normal: o purge não acontece e quem lê o log não
sabe que faltou privilégio, e sim que "NtSetSystemInformation falhou".

---

## 7. Timer e resolução de timer

- `System.Threading.Timer(TimerCallback, null, PollingRate, PollingRate)`,
  recriado (`RestartTimer`) sempre que `PollingRate` muda. Default **1000 ms**.
- `PollingRate` inválido/0 cai para 1000.
- **`WantedResolution` → `NtSetTimerResolution(WantedResolution × 10000, true, &cur)`**
  — pedido de resolução. Desde o **Windows 10 2004** o pedido vale para o
  **processo chamador**, não para o sistema (a Microsoft mudou o comportamento de
  `timeBeginPeriod`; a mesma lógica vale aqui). Para tentar o efeito global no
  **Windows 11**, o ISLC expõe a chave `GlobalTimerResolutionRequests = 1`
  (`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel`), que **exige
  reinicialização** — é o próprio tooltip do app que diz isso. Essa chave não tem
  documentação oficial da Microsoft; evidência independente aponta Windows 11
  22H2+/Server 2022+. Resolução mais fina aumenta interrupções do clock e
  consumo de energia. Liberado com `ReleaseMaxTimerResolution` ao parar.
- O app também marca a si mesmo com `ProcessPowerThrottling`
  (`ControlMask = 4`, `StateMask = 0` → não ignorar o pedido de timer) via
  `SetProcessInformation`, e roda com `PriorityClass.Idle` para não competir com
  o jogo.
- Em volta desse pedido o ISLC **personifica o token de um processo SYSTEM**
  (`ImpersonateUser.Taketoken()`: varre `Process.GetProcesses()`, abre token com
  `SeDebugPrivilege`, duplica, `ImpersonateLoggedOnUser`, `RevertToSelf` depois).
  É o truque mais invasivo do programa. **Não há evidência de que isso mude o
  efeito do timer**: `NtSetTimerResolution` não exige privilégio e a solicitação é
  do processo chamador, não do token da thread. Fica registrado como
  *possivelmente inócuo* — e como motivo suficiente para não copiar o modelo.
- O próprio processo roda com `PriorityClass.Idle` — não compete com o jogo.

---

## 8. Lista de exclusão (como "pausa quando o jogo está aberto")

- Normaliza cada nome para minúsculo e garante sufixo `.exe`.
- `AppEnumeratorApi` enumera os que já estão rodando → contador interno.
- Assina WMI `Win32_ProcessStartTrace` / `Win32_ProcessStopTrace` com
  `ProcessName='a.exe' OR ProcessName='b.exe'` e incrementa/decrementa o contador.
- `IsAppsExecuting => NumberOfProcess != 0`.

Fragilidades conhecidas do modelo de contador:

- Se um evento de stop se perder (WMI reiniciando, processo morto de forma
  anormal), o contador **nunca volta a zero** e o purge fica pausado para sempre.
  Não há reconciliação periódica com o estado real.
- Reiniciar o app zera o contador e reenumera — remedia o caso acima só no restart.

---

## 9. Persistência e estatísticas

- `config.json` ao lado do executável (`AppDomain.CurrentDomain.BaseDirectory`).
- Escrita **atômica**: grava `config.json.tmp`, `Flush(flushToDisk:true)`,
  `File.Replace` (ou `Move` se não existir). `SaveIfChanged` compara o JSON
  serializado com o último escrito, então o tick não regrava à toa.
- `OnExit`/`SessionEnding` salvam se ainda não salvou. Com `-runonce` não salva.
- UI mostra "N / MB" da sessão (`NumberOfTime` / `_sessionMemoryFreedMB`).

**Bug de métrica:** `_sessionMemoryFreedMB += beforeMB` — soma o tamanho da
standby list **antes** do purge (que é justamente o que se espera liberar), sem
medir o depois. Se o purge falhar ou liberar parcialmente, o número continua
subindo igual. Não é "MB liberados", é "MB elegíveis".

**Assinatura suspeita:** `PurgeLogic(object StandbyListEnteredSize)` recebe o
limiar como `object` e faz cast para `ulong` — funciona, mas é o tipo de coisa
que denuncia refactor inacabado.

---

## 10. O que o ISLC **não** tem

Isso importa para não "igualar" o KitLugia a um app mais simples:

- Nada por processo (sem `EmptyWorkingSet`, sem teto de working set, sem
  `MEMORY_PRIORITY`).
- Nenhuma leitura de page faults, CPU por processo, commit ou pico de WS.
- Nenhuma proteção de foreground para o purge (só a lista de exclusão manual).
- Nenhum anti-storm/backoff, nenhuma histerese.
- Nenhum modo "direto vs governador".
- Nenhuma visão de "o que foi liberado de fato".
- Sem serviço/watchdog: fechou, acabou (auto-start via Task Scheduler é opcional,
  criado pelo próprio app).

---

## 11. Reprodução (comandos exatos)

```bash
# 1. Confirmar que é .NET (não nativo)
grep -aoE "mscoree\.dll|BSJB|\.NETFramework,Version=v[0-9.]+" \
  "C:/Users/Lugia/Downloads/ISLC v1.0.4.8/Intelligent standby list cleaner ISLC.exe" | sort -u

# 2. Decompilar com símbolos
ilspycmd -p -o C:/temp/islc_src/Core "C:/Users/Lugia/Downloads/ISLC v1.0.4.8/ISLC.Core.dll"
ilspycmd -p -o C:/temp/islc_src/Gui "C:/Users/Lugia/Downloads/ISLC v1.0.4.8/Intelligent standby list cleaner ISLC.exe"

# 3. Medir standby por prioridade vs SystemCache
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/standby_probe.ps1
```

Arquivos que concentram a lógica (após o passo 2):

| Arquivo | O que tem |
|---|---|
| `Gui/.../ViewModels/MainWindowViewModel.cs` | timer, medição, `PurgeLogic`, `ClearStandbyListAsync` |
| `Core/.../Win32_NtSetSystemInformation.cs` | enum + `ClearStandbyCache` (comando 4) |
| `Core/.../Win32_NtSetTimerResolution.cs` | pedido/liberação de resolução |
| `Core/.../SettingsManager.cs` | JSON atômico |
| `Core/.../AppMonitorWMI.cs` | lista de exclusão via WMI |
| `Core/.../ImpersonateUser.cs` | token SYSTEM p/ resolução de timer |
| `Gui/.../Views/MainWindowView.cs` | `-purge`, instância única, hotkey, agendador, QoS |
| `Core/.../UpdateCheck.cs`, `CheckUpdate.cs` | checagem de versão por HTTP |

---

## 12. O que mais o app faz (fora do purge)

Estas quatro coisas não aparecem em nenhum resumo focado só na standby list, e
são justamente as que dão "cara de produto" ao ISLC:

### 12.1 Checagem de versão por HTTP

- `UpdateCheck.GetVersionInfoAsync` → `GET https://www.wagnardsoft.com/api/islc/version.json`,
  `User-Agent: ISLC/<versão> (Intelligent Standby List Cleaner)`, timeout de 5 s.
- `CheckUpdate.VersionUpdateAsync` → `GET https://www.wagnardsoft.com/ISLC/currentversion.txt`.
  Método é **no-op**: baixa, converte e descarta o resultado (não compara, não avisa).
- Nenhum POST, nenhum envio de dados da máquina. É consulta de versão, não telemetria.

### 12.2 Hotkey global de purge

- `RegisterHotKey(hwnd, 9001, modifiers, vk)`; `WM_HOTKEY` (0x0312) → `PurgeCommand`.
- Default de fábrica: `HotkeyModifiers = 3` (Ctrl+Alt) e `HotkeyVKey = 80` (`VK_P`),
  mas **desligado** (`HotkeyEnabled = false`).
- O usuário redefine capturando a próxima combinação em um TextBox; regrava e
  re-registra sem reiniciar.
- Funciona "mesmo em jogo" porque é hotkey global do Windows, não hook de teclado.

### 12.3 Auto-start via Agendador de Tarefas (elevado)

- `CbTaskScheduler` cria/atualiza uma tarefa via COM
  (`CLSID 0F87369F-A4E5-4CFC-BD3E-73E6154572DD`) apontando para o próprio exe, no
  logon do usuário, **com elevação** (o tooltip diz isso explicitamente).
- Desmarcar chama `DeleteSchedule()`. Não é chave `Run` do registro: é tarefa,
  porque o purge precisa de token elevado.

### 12.4 Detalhes de processo

- Instância única: se já existe processo com o mesmo nome, mostra aviso e fecha.
  (O `-purge` é tratado **antes** dessa checagem, então ele funciona com o app aberto.)
- `PriorityClass.Idle` no próprio processo + `ProcessPowerThrottling` com
  `ControlMask = 4 / StateMask = 0` (não ignorar o pedido de timer) e
  `RenderOptions.ProcessRenderMode = SoftwareOnly`.
- Leitura de página de troca por `NtQuerySystemInformation` classe 18 só para exibir.

---

## 13. Medição do gate com os defaults (prova, não leitura)

`scripts/islc_gate_probe.ps1` (novo) reproduce **a mesma** leitura do ISLC e
aplica o mesmo predicado. Rodada real nesta máquina (32 GB, Windows 11, 20
amostras a 700 ms):

| Métrica | Resultado |
|---|---|
| Condição de purge atendida | **20/20 (100 %)** |
| Standby | 10.890–12.611 MB (média 11.768) |
| "Free" (Zero+Free) | **0–256 MB** |
| RAM "Disponível" | mín. 10.923 MB |
| `SeProfileSingleProcessPrivilege` no token atual | **ausente** |

Conclusão: com os defaults (`1024`/`1024`), o segundo limiar é verdadeiro em
todos os ticks — quem decide é só o tamanho da standby list. Ligar o
monitoramento assim = 1 purge por segundo nesta máquina. Não é bug, é o que o
default significa; e é a razão de a página oficial mandar calibrar os limites.

O detalhamento dos instrumentos usados (metadata, IL cruzado, recursos, medição)
está em [`ISLC_BINARY_FORENSICS.md`](ISLC_BINARY_FORENSICS.md).
