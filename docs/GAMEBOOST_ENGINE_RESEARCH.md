# GameBoost — Pesquisa: o melhor jeito de aplicar o motor AUTOMÁTICO

> Sessao 05/10/2026. Pergunta do usuario: *"pesquise bastante sobre o melhor jeito de
> aplicar o motor automatico; hoje eu uso bastante o V4 e ele funciona bem"*.
>
> Conclusao curta: o **V4 fica exatamente como esta**. O motor **Automatico** passa a
> atacar as duas metades do problema — **elevar o que esta em foco E demover o que
> atrapalha no fundo** — sem usar nenhum botao global. Ver desenho na seccao 5.

---

## 1. Como o Windows decide quem roda (fatos da documentacao oficial)

Prioridade efetiva = **classe do processo** + **nivel da thread**:

| Classe | Base da thread NORMAL |
|---|---|
| IDLE | 4 |
| BELOW_NORMAL | 6 |
| NORMAL | 8 |
| ABOVE_NORMAL | 10 |
| HIGH | 13 |
| REALTIME | 24 |

Regras que a propria Microsoft escreve:

- **"Use HIGH_PRIORITY_CLASS with care."** Se uma thread fica no nivel mais alto por
  muito tempo, as outras nao recebem tempo de CPU; e "the high-priority class should be
  reserved for threads that must respond to time-critical events". O uso *correto* e
  elevar **temporariamente** e baixar depois. → O Kit faz exatamente isso (eleva no foco
  e devolve ao sair). Estamos alinhados com a orientacao oficial.
- **"You should almost never use REALTIME_PRIORITY_CLASS"** — interrompe threads do
  sistema que tratam mouse/teclado e o flush de disco. → O comentario de seguranca do
  `ApplyBoostV4` (03/10) e o `ApplyBoostAuto` (teto HIGH) estao certos. Nao voltar atras.
- `SetPriorityClass` muda o **processo**; `SetThreadPriority` muda a thread *relativa*
  a ele. A recomendacao classica e `THREAD_PRIORITY_ABOVE_NORMAL`/`HIGHEST` na thread de
  input e `BELOW_NORMAL`/`LOWEST` nas threads de trabalho pesado — ou seja, **o mesmo
  principio: subir o que responde ao usuario, baixar o que nao responde**.

Fontes: [Scheduling Priorities](https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities) ·
[SetPriorityClass](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass)

---

## 2. Os cinco "truques" do genero, e o que cada um realmente faz

### 2.1 `Win32PrioritySeparation` (o tweak mais repetido na internet)

Codifica 3 campos de 2 bits: **tamanho do quantum**, **variabilidade** e **boost de
foreground**. `0x26` (38 dec) = quantum *curto* + variável + boost de foreground.
Num desktop Windows o default **já dá prioridade ao foreground** — o que `0x26` muda de
fato é o quantum ficar curto (mais preempcao = menor latência, mais overhead).

⚠️ **Achado no host (medido, nao teorico):**

```
HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl\Win32PrioritySeparation = 0x26
```

O Kit escreve `38 (0x26)` no boost e **`2` no revert**
(`TrayIconService.ShutdownGameBoost`, linha ~4628). Como o valor **original desta
maquina ja e 0x26**, o "revert" **piora** a configuracao em vez de restaurá-la: nao
existe leitura do valor original em lugar nenhum. E um bug de fidelidade de revert,
reproduzivel agora.

**Recomendacao:** no motor Automatico, **nao tocar nesse valor** (e global e nao e do
jogo). Se o Kit continuar a mexe-lo por outro caminho, **ler e guardar o valor original**
antes de escrever e devolver exactamente esse valor.

### 2.2 Timer resolution (`NtSetTimerResolution` / `timeBeginPeriod`) — o mais enganador

Desde **Windows 10 2004** o pedido de resolucao de timer passou a ser **por processo**:
"an application's request only applies to its own sleep and wait operations". O
`timeBeginPeriod` de um processo **deixou de acelerar o `Sleep`/espera dos outros
processos**. Continua a existir um efeito (o *tick* global fica mais rapido, medivel com
`timeGetTime`), mas o ganho que se anunciava — "o jogo acorda mais depressa" — nao vem
por ai: o jogo precisa de chamar `timeBeginPeriod` **ele proprio**.

Windows 11 tem uma chave para restaurar o comportamento antigo
(`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\GlobalTimerResolutionRequests=1`).

**Verificado nesta maquina:** a chave **nao existe** → os pedidos de timer resolution
estao no modo **por processo**.

→ O `Win32Api.BoostTimerResolution()` do Kit roda **no processo do Kit** e chama
`NtSetTimerResolution` — logo **nao acelera os timers do jogo**. O que ele faz de fato:
aumenta o tick global (custo de energia e de latência de DPC/ISR, que e a explicacao
plausivel dos "estouros em audio virtual" que o proprio Kit documenta). **Custo sem
ganho para o jogo.**

**O jeito correto de ajudar com timer:** limpar `PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION`
no processo do jogo (`StateMask = 0` = "sempre honrar os pedidos de timer resolution do
processo"). Documentacao: *"By default in Windows 11 if a window owning process becomes
fully occluded, minimized, or otherwise non-visible... Windows may automatically ignore
the timer resolution request"*. Ou seja: o jogo pede o timer que quer e o Kit garante que
esse pedido e **respeitado**, sem mexer no global.

Fontes: [Windows Timer Resolution: The Great Rule Change (Bruce Dawson)](https://randomascii.wordpress.com/2020/10/04/windows-timer-resolution-the-great-rule-change/) ·
[timeBeginPeriod](https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod) ·
[SetProcessInformation](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation)

### 2.3 EcoQoS / PowerThrottling — ganho real, barato e reversivel

`PROCESS_POWER_THROTTLING_EXECUTION_SPEED` ligado = processo marcado **EcoQoS**: o
sistema baixa frequencia e empurra para os E-cores. A propria MS diz que EcoQoS **nao**
deve ser usado para "performance critical or foreground user experiences". Desligar em
quem esta em foco e correto e a doc recomenda nao deixa-lo ligado em foreground.

O Kit ja faz (`SetEcoQoS(handle,false)`) — manter sempre no Automatico.

### 2.4 Memory/page priority — escala documentada 1..5, **5 = NORMAL (default)**

| Valor | Significado |
|---|---|
| 1 | VERY_LOW |
| 2 | LOW |
| 3 | MEDIUM |
| 4 | BELOW_NORMAL |
| 5 | **NORMAL — default de todo processo e thread** |

⚠️ **Rotulos do Kit estao invertidos.** A caixa de "Settings" e os comentarios chamam
`5` de *"Page: Maximum (5)"* e `2` de *"very high: roubava RAM"*. Na escala real, `5` e o
**default** e `4` e **abaixo do normal**. Consequencia pratica no Automatico de hoje:
`PagePriorityLevel = foreground ? 4 : 5` faz o **jogo em foco ficar com page priority
ABAIXO do default** e o processo de fundo ficar no default — o inverso da intencao.

**Recomendacao:** usar **5 (default) para quem esta em foco** e **nao descer abaixo disso
para o jogo**; se quiser ser agressivo no fundo, o simulador e o ProBalance (2.5), nao a
page priority. Corrigir os rotulos da UI (dizer "Normal (5)" e nao "Maximum (5)").

Fonte: [MEMORY_PRIORITY_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-memory_priority_information)

### 2.5 ProBalance (Bitsum / Process Lasso) — **a metade que falta no motor Automatico**

Citacao directa do autor do Process Lasso:

> **"Don't set your important processes to 'High' or 'Real-Time'. This can cause
> complications and is usually ineffective at improving application responsiveness.
> ProBalance works from the other direction (lowering priority classes) for a reason."**

> "ProBalance will improve system responsiveness during high CPU loads... it dynamically
> lowers the priority class of **problematic background** processes."

Detalhes do algoritmo que importam:

- Age **só sob carga alta** do sistema (o problema e o *stall*, nao a prioridade em si).
- **Exclui o processo em foreground** por omissao (os dois mecanismos sao complementares).
- Ajuste **marginal, temporario** e **revertido** quando a condicao muda.
- "Pro Tip: ProBalance also has a number of exclusionary parameters, such as avoiding
  the foreground process... These are configurable."

Fonte: [ProBalance Optimization Algorithm](https://bitsum.com/how-probalance-works/)

### 2.6 MMCSS (`AvSetMmThreadCharacteristics`) — **nao e aplicavel de fora**

O MMCSS da "rajadas curtas de prioridade muito alta" a threads de audio/video sem o
risco do REALTIME. Mas `AvSetMmThreadCharacteristics` marca **a thread que a chama** —
um programa externo nao pode registar a thread de outro processo. Os jogos (ou o
`audiodg`) registam-se sozinhos. Um "MMCSS tweaker" que edita as chaves
`...\Multimedia\SystemProfile\Tasks\Games` so muda o perfil por omissao (prioridade
relativa/quantum) e nao transforma uma thread qualquer numa thread MMCSS.

→ **Nada a implementar aqui.** Registar para nao cair na tentacao de "adicionar MMCSS".

---

## 3. Achados concretos no codigo actual (com a evidencia)

| # | Item | Evidencia | Veredito |
|---|---|---|---|
| A | `BoostTimerResolution()` global no boost | `NtSetTimerResolution` chamado pelo processo do Kit; `GlobalTimerResolutionRequests` **ausente** no host → modo per-process | Nao ajuda o jogo; adiciona carga de DPC. Candidato a **substituir** pela flag de timer do jogo (2.2) |
| B | `SetWin32PrioritySeparation(false)` escreve **2** no revert | Original do host = **0x26**; nunca e lido/guardado | **Bug de revert** (reproduzivel). Nao mexer no Automatico; corrigir a fidelidade do revert |
| C | `ApplyBoostAuto` tem `ProBalance = false` | linha do `cfg` no `ApplyBoostAuto` | Falta a metade que mais rende segundo o autor do Process Lasso. **Ganho principal proposto** |
| D | Rotulos de page priority | `"Page: Maximum (5)"` na UI; doc: 5 = NORMAL/default, 4 = below normal | Rotulo errado; valor do Automatico no foco esta **abaixo** do default |
| E | `ApplyBoostCustom` chama `SetThreadEfficiencyForAllThreads` em **toda** reaplicacao | `if (config.ThreadEfficiencyMode == false) { CheckPcoreBenefit(pid); SetThreadEfficiencyForAllThreads(pid,false); }` — e o Automatico tem `ThreadEfficiencyMode=false` sempre | Enumerar/abrir **todas** as threads do jogo a cada 15 s e a cada troca de foco; so tem efeito em build ≥ 26100. Aplicar **uma vez por pid** |
| F | Deteccao por `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` + polling 250 ms | `TryRegisterWinEventHook`/`CheckForegroundWindow` | Desenho correcto. Falta `WINEVENT_SKIPOWNPROCESS` (0x0002) para ignorar as proprias janelas |
| G | `_heavyAppIndicators` e `IsFullScreen` | 0 chamadas | Codigo morto; o motor **nao** precisa de "detetar jogo" |
| H | `_userExceptions` (15 nomes) | removido em 05/10 | OK — a barreira e `_protectedProcesses` (41 nomes) |

### Sobre "detetar jogo" (item G)

O Automatico funciona hoje com **qualquer** app em foreground — e isso nao e defeito: e
a mesma logica do V4 ("o app que eu escolhi por em foco e o que deve ficar rapido"). A
heuristica `unreal/unity/steam/game/lobby` nunca foi usada e nao deve voltar: classificar
mal (bloquear o boost de um jogo, ou dar boost a um antivírus) e pior que nao classificar.
A alternativa certa e **tres baldes**, sem adivinhar genero:

1. **protegido** (`_protectedProcesses`, o proprio kit) → ignora;
2. **candidato** (tem janela top-level visivel, nao protegido) → eleva;
3. **ja em boost e perdeu o foco** → mantem na faixa Sustentado.

---

## 4. Medicao local usada como prova

- `_protectedProcesses` = **41 nomes** (unica barreira depois da remocao da lista de
  excecoes).
- Harness `%TEMP%\gbauto` (caminho REAL `CheckForegroundWindow`), 05/10: **8/8 OK** —
  foreground → `HIGH` + `GameBoost AUTO` no log; perdeu foco → `AboveNormal` (Sustentado);
  `RevertCurrentBoost` → `NORMAL`; `ShouldBoostProcess`=false para o proprio kit e para
  `explorer`, `true` para app comum.
- Limpeza verificada: `SystemResponsiveness=0x14 (20)`, `NetworkThrottlingIndex=0xa (10)`,
  `last_engine.json = {fixed, 5}` (Auto), sem processos de teste.

---

## 5. Desenho recomendado do motor AUTOMATICO (v2)

**Principio:** o ganho nao vem de "apertar o jogo". Vem de (i) garantir que o que esta em
foco **nao e travado** por nada (EcoQoS, timer ignorado, page priority rebaixada) e
(ii) **tirar CPU de quem abusa do fundo enquanto a maquina esta sob carga**. Nada global.

### 5.1 Pirâmide de accoes

```
1. DEMOVER O FUNDO        <- maior ganho, menor risco (Bitsum)
   processo de fundo, nao protegido, CPU alta, E sistema sob carga
   -> PriorityClass = BELOW_NORMAL (+ restaurar em cooldown/ociosidade)

2. ELEVAR O FOCO          <- o que ja existe
   -> PriorityClass = HIGH (nunca REALTIME)

3. ESTADOS POR PROCESSO   <- barato e reversivel, no proprio jogo
   EcoQoS OFF · honrar timer resolution · GameClassInfo ON · I/O High no loading

4. NADA GLOBAL            <- retirar do caminho automatico
   sem NtSetTimerResolution, sem Win32PrioritySeparation, sem tweak de rede
```

### 5.2 Valores propostos (só o Automatico; V4 intocado)

| Parametro | Foco | Loading (CPU>75%) | Sustentado (perdeu foco) |
|---|---|---|---|
| PriorityClass | HIGH | HIGH | ABOVE_NORMAL |
| I/O priority | 3 (High) | 3 (High) | 2 (Normal) |
| Page priority | **5 (Normal/default)** | 5 | 5 |
| Thread memory | 5 (Normal) | 5 | 5 |
| EcoQoS | OFF | OFF | OFF |
| Honrar timer resolution | SIM | SIM | SIM |
| GameClassInfo | true | true | false |
| Timer global | **nao** | **nao** | **nao** |
| Win32PrioritySeparation | **nao mexer** | **nao mexer** | **nao mexer** |
| NetworkBoost | nao | nao | nao |
| ProBalance (sistema) | activo c/ gate de carga | activo | activo |

### 5.3 Gate do ProBalance (a diferenca para o V2/V3 existentes)

O ProBalance actual (`ApplyProBalanceCore`) decide **so** pelo CPU do processo
(`>8%` no V2, `>3%` no V3), sem olhar a carga do sistema. Isso pode rebaixar um render
legitimo numa maquina ociosa. O Process Lasso so age **sob carga alta**. Proposta:

```
demover(pid) SOMENTE SE
    pid != foreground
    && !_protectedProcesses.Contains(nome)
    && cargaTotalCpu > 70%                (gate novo — o ponto do Bitsum)
    && cpuDoProcesso > limite            (janela de N amostras consecutivas)
    && pid nao esta em cooldown
-> BELOW_NORMAL, guardando o valor original para restaurar
```

### 5.4 Custo (o que fazer para nao pesar)

- Aplicar `SetThreadEfficiencyForAllThreads` **uma vez por pid** (guardar em
  `_threadEfficiencyApplied`), nao em cada reaplicacao — hoje corre em cada troca de foco
  e a cada ~15 s (item E).
- Reavaliar o perfil (`folgado` ↔ `carregando`) no **tick de 3 s** que ja existe, mas
  re-aplicar **so quando a faixa muda** (nao a cada tick).
- ProBalance no seu proprio tick, com o estado em cache (o kit ja tem
  `GetCachedProcesses()`).

### 5.5 Deteccao

- `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, ..., WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS)`
  e manter o polling de 250 ms como fallback (ja existe).
- Remover `_heavyAppIndicators` e `IsFullScreen` (mortos).
- Opcional: reagir tambem a `EVENT_SYSTEM_MINIMIZESTART` para tratar "minimizou" como
  "perdeu o foco" no mesmo instante.

---

## 6. V4 x Automatico (v1 actual) x Automatico (v2 proposto)

| | **V4 ExtremePro** | **Auto v1** | **Auto v2 (proposto)** |
|---|---|---|---|
| PriorityClass | HIGH | HIGH | HIGH |
| I/O | 3 sempre | 2 ou 3 | 3 / 3 / 2 |
| Page priority | 4 | 4 (foco) / 5 | **5 sempre** (default) |
| Timer global | **ON sempre** | ON no loading | **OFF** (flag por processo) |
| Tweak de rede | **ON** | OFF | OFF |
| Win32PrioritySeparation | **ON** | ON no foco | **nao mexer** |
| ProBalance | OFF | **OFF** | **ON com gate de carga** |
| Risco de travada/BSOD | medio (ja mitigado em 03/10) | baixo | baixo |
| "Fica mais rápido no loading" | sim | parcial | sim |

**V4 nao muda.** Ele e o motor que o usuario conhece e confia; os itens "globais" dele
(Timer ON, rede ON) sao exactamente os que a pesquisa aponta como fracos, mas **mudá-los
muda a experiencia que funciona** — fica registado como observacao, nao como alteracao.

---

## 7. Plano de validacao (antes de tocar no motor)

1. **Harness de comportamento** (`%TEMP%\gbauto`, ja existe): foco → HIGH; perdeu foco →
   AboveNormal; revert → Normal; protegido → sem boost. Adaptar para o Auto v2 e exigir
   0 falhas.
2. **Harness de ProBalance** (novo): processo sintetico a queimar CPU em background +
   gate de carga → confirmar BELOW_NORMAL sob carga e **nada** com a maquina ociosa;
   restaurar ao terminar.
3. **Medicao de custo**: contagem de chamadas a `SetThreadEfficiencyForAllThreads` por
   minuto (log) antes/depois — deve cair de ~4-8/min para 1 por processo.
4. **Fidelidade de revert** (item B): ler `Win32PrioritySeparation` antes do boost,
   comparar com o valor depois do revert; os dois tem de ser **iguais ao original**.
5. **Audio/DPC**: medir `xperf`/`LatencyMon`-style com o timer global ON x OFF para
   confirmar que retirar o timer do Automatico nao causa regressao de estouros.
6. Só depois: `docs/CORE_API_AUDIT.md`-style, actualizar `AGENTS.md`.

---

## 8. Fontes

- [Scheduling Priorities — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities)
- [SetPriorityClass — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setpriorityclass)
- [SetProcessInformation (EcoQoS / PowerThrottling / IGNORE_TIMER_RESOLUTION) — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation)
- [MEMORY_PRIORITY_INFORMATION — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/ns-processthreadsapi-memory_priority_information)
- [timeBeginPeriod — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod)
- [Multimedia Class Scheduler Service — Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service)
- [Windows Timer Resolution: The Great Rule Change — Bruce Dawson](https://randomascii.wordpress.com/2020/10/04/windows-timer-resolution-the-great-rule-change/)
- [ProBalance Optimization Algorithm — Bitsum](https://bitsum.com/how-probalance-works/)
- [Process Lasso FAQ (don't set important processes to High) — Bitsum](https://bitsum.com/process-lasso-faq/)
- [Win32PrioritySeparation — entendimento do valor 0x26 — xbitlabs](https://www.xbitlabs.com/blog/win32priorityseparation-performance)
