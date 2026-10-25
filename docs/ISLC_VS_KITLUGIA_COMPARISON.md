# ISLC x KitLugia — análise concluída, correções e comparação verificada

> **3ª passada (17/10/2026, 23:17–23:40).** As duas primeiras passadas
> ([`ISLC_ANALYSIS.md`](ISLC_ANALYSIS.md), [`ISLC_BINARY_FORENSICS.md`](ISLC_BINARY_FORENSICS.md))
> analisaram **o ISLC**. Esta passou faz o que elas deixaram explicitamente em aberto
> (§8 do forensics: *"Não executei o ISLC… nada de ETW/kernel: não instrumentei
> o gerenciador de memória para ver o purge acontecer"*) e, ao fazer isso,
> **corrige um número errado da passada anterior e fecha a lacuna que ela
> declarava aberta**. Também registra uma correção desta sessão: a primeira redação
> deste documento tratava a elevação como falha ativa; era inferência não
> verificada, e o `app.manifest` desmente (ver §1).
>
> Instrumento novo: `scripts/islc_probe/` (console .NET, fora da solução) que
> (a) pergunta ao token se `SeProfileSingleProcessPrivilege` está habilitada,
> (b) repete o `AdjustTokenPrivileges` do KitLugia e lê `GetLastError`,
> (c) chama `NtSetSystemInformation(80)` **lendo o NTSTATUS** (que o kit descarta),
> (d) mede standby antes/depois chamando o `MemoryOptimizer` real por reflexão.
> Executado **elevado** e **não-elevado** (via Task Scheduler `RunLevel=Limited`,
> que é como o app sobe pelo Registry Run).
>
> **Licença:** ISLC é *proprietary freeware* (© Wagnardsoft). Aqui só fatos de
> comportamento e assinaturas de API. Nada de código do ISLC no repositório.

---

## 1. RESULTADO PRINCIPAL — o purge funciona; a falha é latente, não atual

> **Correção de uma versão anterior deste documento.** A primeira redação deste
> capítulo afirmava que *"o purge do KitLugia nunca funcionou sem elevação"* e
> tratava isso como achado crítico. **Isso estava errado**: foi inferido do
> mecanismo de autostart (`HKCU\...\Run` + `.lnk`, que não elevam) em vez de
> verificado no manifesto. `KitLugia.GUI\app.manifest:12` declara
> `<requestedExecutionLevel level="requireAdministrator" />` (referenciado por
> `KitLugia.GUI.csproj:22`), então **toda** execução do app é elevada (UAC no
> start). O purge funciona. O texto abaixo é a redação corrigida.

### 1.1 O que foi medido

| Passo | **Elevado** (= como o Kit roda) | Não elevado |
|---|---|---|
| `SeProfileSingleProcessPrivilege` habilitado antes | `False` | `False` |
| …**depois** do `AdjustTokenPrivileges` | **`True`** | **`False`** |
| retorno de `AdjustTokenPrivileges` | `True` | `True` ← engana |
| `GetLastError()` | `0` | **`1300`** ← `ERROR_NOT_ALL_ASSIGNED` |
| **ajuste efetivo** | **`True`** | `False` |
| `NtSetSystemInformation(80, purge=4)` | **`0x00000000`** ✅ | `0xC0000061` |
| `PurgeStandbyList()` ×3, standby antes→depois | **cai** | 480→480→480 |

NTSTATUS de falha em decimal: `-1073741727` = `0xC0000061` =
`STATUS_PRIVILEGE_NOT_HELD`.

**No caminho real do app (elevado), o purge executa.** Esta era a lacuna mais
importante que os dois primeiros documentos deixaram aberta
(`ISLC_BINARY_FORENSICS.md` §8: *"não instrumentei o gerenciador de memória
para ver o purge acontecer"*). Está fechada.

### 1.2 O que permanece: a pilha não reporta falha nenhuma

Mesmo com o app elevado, **nenhuma das três camadas diz se o purge funcionou**:

1. `MemoryOptimizer.EnablePrivilege` (`MemoryOptimizer.cs:268`) chama
   `AdjustTokenPrivileges` e **ignora o retorno e o `GetLastError`**. A API
   devolve `TRUE` mesmo quando o privilégio não é atribuído — quem denuncia é o
   `1300`. O método é `void`.
2. `MemoryOptimizer.ExecuteMemoryCommand` (linha 297) **descarta o NTSTATUS** de
   `NtSetSystemInformation` e envolve tudo em `catch { }` (comentário no código:
   *"Ignore individual failures, try to proceed"*).
3. `TrayIconService.CheckAndCleanStandby` (linha 5702) faz `_ = Task.Run(...)` e
   incrementa `IslcCleanCount++` **dentro do try**, sem medir o depois.

Ou seja: **se** o purge falhar — manifesto removido numa build, `Core` usado por
outro processo sem elevação, política de UAC da máquina mudada, privilégio
removido do token — o KitLugia **não emite um único byte de log**, e o badge da
UI continua mostrando `Standby: N MB` com a cor de "acima do threshold". O
usuário vê um sistema aparentemente HEALTHY e um recurso que não roda.

É um defeito **latente e mascarado**, não um defeito ativo. Vale corrigir porque
é barato, e porque a medição provou que o modo de falha é *completamente
mudo* — que é o pior tipo.

### 1.3 Vantagem que o ISLC não tem: elevação já é garantida

O ISLC resolve o privilégio com **tarefa agendada elevada** criada pelo próprio
app (`ISLC_ANALYSIS.md` §12.3). O KitLugia **não precisa disso**: o
`requireAdministrator` do manifesto cobre qualquer forma de lançamento — duplo
clique, Registry Run, atalho, ou a linha de comando. É estritamente mais
robusto, e deve ser registrado como vantagem na comparação, não como lacuna.

### 1.4 O `TOKEN_PRIVILEGES` já está correto

`MemoryOptimizer.cs:55` usa `Pack = 4`, o layout nativo (DWORD + LUID alinhado a
4 + DWORD = 16 B). **Verificado nesta passada:** com elevação o ajuste funciona
(`ANTES=False → DEPOIS=True`). O bug de layout de LUID documentado no
`AGENTS.md` (que afetava 5 arquivos do projeto) **não afeta este caminho**. O
que falta aqui não é o layout da struct — é checar o retorno da API.

---

## 2. CORREÇÃO — "o gate de memória livre é quase sempre verdadeiro" está ERRADO

A seção 5.1 do `ISLC_ANALYSIS.md` e a G7 da comparação afirmam, com base em
medição das 21:21–21:23, que `FreePages` (Zero+Free) fica em **0–256 MB** e que
o gate dispararia em **20/20** amostras. Disso vem a conclusão "o segundo limiar
é quase decorativo".

**Medido de novo hoje, 23:17, mesma máquina, 20 amostras:**

| Métrica | Passada anterior (21:21) | **Agora (23:17)** |
|---|---|---|
| standby | 10.890 – 12.611 MB | **3.620 – 3.622 MB** |
| free (Zero+Free) | **0 – 256 MB** | **8.763 – 9.004 MB** |
| RAM disponível | 10.923 MB mín. | 12.385 MB mín. |
| gate dispararia | **20/20 (100 %)** | **0/20 (0 %)** |

```
[  1] standby=  3620 MB  free= 8856 MB  avail= 12476 MB  -> -
[ 12] standby=  3622 MB  free= 9004 MB  avail= 12627 MB  -> -
resumo: 0/20 amostras disparariam o purge (0%)
```

**Consequência para o plano:** `Zero+Free` **não** é um "quase sempre 0". É uma
grandia que depende do histórico recente de memória (quanto foi tocado e não
re-paginado) e varia ordens de grandeza em horas. Consequências:

- A **G7 cai**: não dá para "calibrar o default pela mesma medição" como o doc
  propunha, porque a medição não é estável entre si.
- O `FreeMemoryValue = 1024` do ISLC **não é decorativo por acidente**: numa
  máquina com free alto ele bloqueia corretamente (0/20). Ele só vira
  decorativo quando o free está naturalmente baixo. Nos dois casos está fazendo
  o trabalho que se espera dele.
- Para o KitLugia isso é **um argumento a favor** do gate (a G1 continua válida
  e agora está mais bem justificada): sem ele, o purge dispara com 12 GB
  disponíveis.

---

## 3. CORREÇÃO — o erro do `GetPerformanceInfo().SystemCache` é ABSOLUTO, não relativo

`ISLC_ANALYSIS.md` §5.1 conclui que `SystemCache` "acompanha a standby list de
perto (±3 %)". Medido hoje:

| Momento | standby (NtQuery 80) | `SystemCache` (psapi) | erro absoluto | erro % |
|---|---|---|---|---|
| 23:17 (standby alta) | 3.622 MB | 3.795 MB | **+173 MB** | +4,8 % |
| 23:40 (standby baixa) | 480 MB | 818 MB | **+338 MB** | **+70 %** |

O erro **absoluto** é da ordem de 175–340 MB e não encolhe junto com a standby —
`SystemCache` é "páginas no cache do sistema" (standby **+** system working set),
que é outra grandeza. **Logo o erro percentual cresce conforme a standby
diminui** e vira irrelevante justamente quando se precisa da leitura (standby
baixa = perto do threshold = decisão).

**Isso decide a Fase 1 item 1 do plano: a troca para `NtQuerySystemInformation(80)`
não é refinamento, é correção de uma leitura que pode errar por 70 % no ponto
crítico.**

---

## 4. Novas lacunas no KitLugia (não estavam em G1–G8)

### G9 — O purge falha em silêncio se algum dia rodar sem elevação — **latente**
Provado na §1.1: o modo de falha sem elevação existe e é **completamente mudo**
(retorno de `AdjustTokenPrivileges` engolido, NTSTATUS descartado, contador
incrementado dentro do `try`, UI mostrando "saudável"). Hoje o
`requireAdministrator` do manifesto (§1.3) torna isso **impossível no app
real** — mas o `KitLugia.Core` é uma biblioteca e não depende do manifesto de
quem a chama. Corrigir custa pouco e transforma um erro invisível em um erro
visível.

### G10 — Purges empilhados sem limite (o ISLC tem guarda, o Kit não)
`CheckAndCleanStandby` dispara `_ = Task.Run(...)` a **cada tick de 3 s**
enquanto `standbyMB > IslcThresholdMB`, e **não aguarda nem guarda**. O ISLC tem
exatamente a proteção que o Kit não tem:

```
CanExecutePurge => !_isPurging          // ISLC_ANALYSIS.md §6
```

No caminho normal (elevado + purge funcionando) a standby cai e a condição deixa
de valer, então o ciclo não se acumula. **Mas** ele se acumula em qualquer falha
silenciosa, e a §1.2 mostra que falha aqui é **indetectável**: um
`Task.Run` a cada 3 s, para sempre, sem log. É a combinação das duas lacunas que
transforma um erro em loop.

### G11 — `IslcLastCleanTime` é estado morto
Declarado em `TrayIconService.cs:1121`, atribuído em `:5707` e **nunca lido** em
nenhum lugar do repositório (grep confirmado). É a evidência direta de que **não
existe cooldown** — o plano da Fase 1 item 3 (`IslcMinInterval`) cria a leitura
que o código já sinaliza e nunca fez.

### G12 — A UI só compara contra o threshold
`TraySettingsPage.UpdateIslcStatus` (linha 309) faz
`exceeded = standbyMB > tray.IslcThresholdMB` e só isso. Não mostra memória
livre, não mostra se os dois critérios do ISLC bateriam, e não distingue
"acima do threshold" de "pronto para purgar". Combinado com G9, um badge âmbar
animado é exatamente o pior resultado possível: **indica atividade onde não há
nenhuma**.

---

## 5. Estado verificado do código — **ATUALIZADO 17/10, implemented**

> **As Fases 0–4 foram implementadas depois desta análise.** A tabela abaixo
> reflete o código **antes** da mudança; o bloco seguinte, o estado de hoje.
> Tudo validado por build + dois harnesses (ver §9).

| Item do plano (Fase 1) | Estado **antes** | Evidência |
|---|---|---|
| `GetStandbyListSizeMB()` → `NtQuerySystemInformation(80)` | ❌ não feito | usava `GetPerformanceInfo().SystemCache` |
| retornar `-1`/nullable em falha | ❌ não feito | linha 149 `return 0` |
| `GetStandbyListByPriority()` (8 buckets) | ❌ não existe | — |
| `GetFreePagesMB()` | ❌ não existe | — |
| predicado com gate de memória livre | ❌ não feito | só comparava standby |
| respeitar foreground | ❌ não feito | sem guarda |
| `IslcMinInterval` / cooldown | ❌ não feito | `IslcLastCleanTime` nunca lido (G11) |
| medir antes/depois e contar só se caiu | ❌ não feito | `IslcCleanCount++` sem medir (G6) |
| relatar falha do purge | ❌ não existe | retorno e NTSTATUS descartados (G9) |
| guarda de purge concorrente | ❌ não existe | `_ = Task.Run` sem flag (G10) |
| ~~política de elevação~~ | ✅ já resolvida | `app.manifest` = `requireAdministrator` (§1.3) |

### 5.1 O que foi implementado (Fases 0–3)

**`KitLugia.Core\MemoryOptimizer.cs`**

| Mudança | Detalhe |
|---|---|
| `SYSTEM_MEMORY_LIST_INFORMATION` | struct com os 22 campos `SIZE_T` (176 B em x64) |
| `NtQuerySystemInformation` | P/Invoke da classe 80 |
| `TryGetStandbyListSizeMB(out long)` | **métrica do recurso** (soma dos 8 buckets); `false` = falha, distinta de standby vazia |
| `TryGetStandbyListByPriorityMB(out long[8])` | os 8 buckets, em MB |
| `TryGetFreePagesMB(out long)` | `ZeroPageCount + FreePageCount` — o "free" do ISLC |
| `GetStandbyListSizeMB()` | mantém a assinatura antiga, agora com o caminho novo + `GetPerformanceInfo` como fallback |
| `TryPurgeStandbyList(out int ntStatus)` | **`bool` + NTSTATUS**; `ntStatus == 0xC0000061` = sem privilégio |
| `PurgeStandbyList()` | wrapper compatível (não breaking) |
| `EnablePrivilege` → **`bool`** | checa `GetLastError() != 1300` (`ERROR_NOT_ALL_ASSIGNED`) |
| `ExecuteMemoryCommand` → **`int`** | devolve o NTSTATUS em vez de `catch {}` engolido |

**`KitLugia.GUI\Services\TrayIconService.cs`**

| Mudança | Detalhe |
|---|---|
| `IslcFreeThresholdMB` (1024) | **o gate que faltava** (G1) |
| `IslcMinIntervalSeconds` (30) | cooldown explícito (G11) |
| `IslcRespectForeground` (true) | não purga com app em foco |
| `IslcLastSkipReason` | **por que não agiu** — o badge para de mentir |
| `IslcLastNtStatus` | diagnóstico do purge |
| `_isPurgingStandby` + `IsPurgeStandbyInFlight` | guarda de reentrância com `Interlocked.CompareExchange` (G10) |
| `CheckAndCleanStandby` reescrito | predicado de 5 critérios + purge em `Task.Run` que **mede antes/depois** |
| Contador | só incrementa quando `standbyDepois < standbyAntes` (G6) |
| Log | `🧹 Standby: 2731 → 29 MB (liberou 2702 MB, livre 10996 → …)` |
| Persistência | as 3 preferências novas em `SaveSettings`/`LoadSettings` com os leitores defensivos já existentes |

**`KitLugia.GUI\Pages\TraySettingsPage.xaml(.cs)`**

- Painel de parâmetros (3 campos numéricos + checkbox) que só aparece com o toggle ligado.
- Badge mostra **os dois** critérios (`Standby N / livre M MB`) com 3 estados: verde (não chegou no standby), azul (standby alta mas RAM livre — **não vai limpar**, G1 visível), âmbar (pronto para limpar).
- Linha de estado honesta: `Aguardando: memória livre 13352 MB ≥ limite 1024 MB — não precisa liberar cache`, ou `N limpeza(s) nesta sessão · última standby: 29 MB`.

---

## 6. Revisão do plano — como ficou depois da implementação

A ordem original (medir melhor → melhorar a decisão → UI → harness) continua
sendo executável, mas a Fase 0 entra na frente porque **uma decisão melhor sobre
um purge que não reporta sucesso continua não verificável**: sem ela, o harness
não tem o que afirmar.

### Fase 0 (nova) — tornar o purge honesto
1. `MemoryOptimizer.PurgeStandbyList()` → **`bool TryPurgeStandbyList(out int ntStatus)`**:
   ler o retorno de `AdjustTokenPrivileges` + `GetLastError() != 1300`, e devolver
   o NTSTATUS de `NtSetSystemInformation`. Nada de `catch {}` engolindo.
2. `EnablePrivilege` → devolver `bool` e checar `1300`.
3. `CheckAndCleanStandby` → medir standby antes/depois; `IslcCleanCount++` e log
   **só** quando `after < before`. Registrar o `ntStatus` quando falhar. Adicionar
   a guarda de reentrância (`!_isPurging`) do §4/G10.
4. ~~Política de elevação~~ — **já resolvida**: `app.manifest` é
   `requireAdministrator` (§1.3). O ISLC precisa de tarefa agendada elevada para
   isso; o KitLugia não. Nenhuma decisão de produto é necessária aqui.
5. `UI`: o badge passa a mostrar "antes → depois" e, em caso de falha, o motivo
   (privilegio ausente / NTSTATUS) em vez de um número que parece saudável.

### Fase 1 (medição) — agora vale a pena
6. `GetStandbyListSizeMB()` → `NtQuerySystemInformation(80)` + os 8 buckets, com
   `long?` para distinguir falha de zero (§3 mostra que `SystemCache` erra 70 %
   no ponto crítico). Manter `GetPerformanceInfo` como fallback.
7. `GetFreePagesMB()` — a leitura que a decisão precisa (§2 mostra que ela varia
   e que o gate é o que segura).

### Fase 2 (decisão)
8. Predicado = ISLC + proteções do kit, **e** só depois da Fase 0:
   `standby >= IslcThresholdMB` **&&** `free < IslcFreeThresholdMB`
   **&&** `!foreground` **&&** intervalo mínimo **&&** `!purgeEmVoo` (G10).

### Fase 3 (UI)
9. Campos editáveis de standby e de free (§2: default de free calibrado pela
   mesma medição **não** é mais advice confiável — escolher e deixar explícito).
10. Badge com os dois critérios e o estado de elevação.

### Fase 4 (harness)
11. `scripts/islc_probe/` vira teste versionado: asserções para
    `TOKEN_PRIVILEGES` com `Pack=4`, retorno de `AdjustTokenPrivileges`,
    leitura de NTSTATUS, e `free`/`standby` com os dois caminhos.

---

## 7. O que continua em aberto depois desta passada

- **Não houve ETW/kernel** nem captura de telemetria do gerenciador de memória
  para observar o purge de dentro do kernel. O `NTSTATUS 0x00000000` e a queda
  medida de standby já provam o efeito na prática; falta a instrumentação
  se alguém quiser o porquê.
- **Só a 1.0.4.8** foi analisada do ISLC. Comparar com builds antigos continua
  viável com o ferramental do `IDA_PRO_ISLC_GUIDE.md`.
- **O comportamento do purge ao longo do tempo** (quanto de pagefault ele causa,
  se há reclaim do cache) não foi medido. O plano original citava
  "alguns ms" sem número.
- A **elevação deixa de ser pendência** — resolvida pelo manifesto (§1.3).
- **Como o probe se comporta dentro do processo real** continua em aberto: as
  medições usaram um console separado chamando `MemoryOptimizer` por reflexão.
  Ainda não foi observado o `CheckAndCleanStandby` do `TrayIconService` com o
  app completo em execução (o `TrayIconService` não estava rodando na medição).
- `scripts/islc_gate_probe.ps1` e `scripts/standby_probe.ps1` continuam sendo as
  sondas read-only; o `scripts/islc_probe/` novo é o único que **age** (chama
  purge) e por isso fica fora da solução e é opt-in.

---

## 8. Reprodução

```powershell
# Sondas read-only (não alteram a máquina)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\islc_gate_probe.ps1 -Samples 20 -IntervalMs 700
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\standby_probe.ps1

# Probe que AGE (chama purge) — fora da solução, em TEMP
cd $env:TEMP\opencode\islcprobe
dotnet build -c Release
.\bin\Release\net10.0-windows10.0.26100.0\islcprobe.exe            # como admin
# e via Task Scheduler RunLevel=Limited para reproduzir o caminho real do Kit
```

O caminho **não elevado** é o que reproduz o defeito G9. Rodar o probe como
admin **não** o reproduz (o purge funciona) — foi exatamente por isso que a
medição precisou de duas execuções.

---

## 9. Validação da implementação (17/10, depois das Fases 0–3)

Três verificações, todas por execução — nenhuma por leitura de código.

### 9.1 `KitLugia.sln` — build

```
Compilação com êxito.  136 Aviso(s)  0 Erro(s)   BUILD_EXIT=0
Scripts\CheckUiThreading.ps1 → Padroes NOVOS: 0, exit 0
```

### 9.2 Harness do Core (leituras + purge + predicado) — **11 OK / 0 FALHA**

Executado contra o `KitLugia.Core.dll` compilado, chamando o código real:

```
OK   TryGetStandbyListSizeMB              (standby = 177 MB)
OK   TryGetFreePagesMB                    (free(Zero+Free) = 13354 MB)
OK   TryGetStandbyListByPriorityMB        ([0, 5, 3, 0, 20, 146, 0, 0])
OK   soma dos 8 buckets == standby        (tolerância 8 MB de arredondamento)
OK   free <= RAM disponível               (13354 <= 13529)
OK   métrica nova <= SystemCache
OK   RAM sobrando: o predicado NOVO segura (standby 177 > 64 mas free 13352 >= 1024 -> não age)
OK   o predicado ANTIGO teria limpado    (o defeito, G1)
OK   TryPurgeStandbyList reporta NTSTATUS  (0x00000000)
OK   purge consistente com o retorno
OK   purge bem-sucedido reduziu a standby (177 -> 0 MB)
```

**A prova da G1** está nas duas linhas do predicado, com leituras reais da
máquina: `standby 177 MB > limiar 64 MB` **e** `free 13352 MB >= 1024 MB`.
O predicado antigo dispararia; o novo não. É exatamente o defeito "purga cache
útil com a RAM sobrando", agora fechado e medido.

### 9.3 `tests\IslcPageVis` — XAML e ligação dos controles — **26 OK / 0 FALHA**

O build **não** pega erro de XAML em runtime (dois filhos num `Border`,
`x:Name` errado, estilo inexistente), então a página real é instanciada:

```
OK   Generic.xaml carregado no Application.Resources
OK   TraySettingsPage instanciou (XAML parseou)
OK   controle 'TxtIslcThreshold' existe        (TextBox)
OK   controle 'TxtIslcFreeThreshold' existe    (TextBox)
OK   controle 'TxtIslcMinInterval' existe      (TextBox)
OK   controle 'ChkIslcRespectForeground' existe(CheckBox)
OK   controle 'PnlIslcSettings' existe         (StackPanel)
OK   controle 'TxtIslcLastAction' existe       (TextBlock)
OK   PnlIslcSettings inicia Collapsed
OK   TxtIslcThreshold_LostFocus aguenta entrada inválida sem exceção
OK   UpdateIslcStatus() executou sem exceção
OK   handler ChkIslcRespectForeground_Changed existe
```

> Quirk do harness (mesmo do `NetPageVis`): a página usa `StaticResource` do
> `App.xaml`, então sem `Themes/Generic.xaml` em `Application.Resources` o XAML
> rebenta com `XamlParseException`. Carregado por URI `pack://` do próprio
> assembly.

### 9.4 `tests\TrayIslcLab` — o `TrayIconService` real — **19 OK / 0 FALHA**

O build não pega lógica de decisão, então o `CheckAndCleanStandby` foi exercitado
de verdade. Chave do isolamento: `new TrayIconService()` (o construtor é trivial —
só cria o `DispatcherTimer`, **não** chama `Initialize()`, que sobe `NotifyIcon`,
watchdog, `LoadSettings` e auto-start) + reflexão no método privado.

Segurança (regra do `AGENTS.md`, depois do incidente em que um harness apagou os
limites de RAM do usuário):

- o cenário padrão **nunca** chama `SaveSettings` — só leitura;
- backup de `HKCU\Software\KitLugia` (`reg export`) + 350 arquivos de
  `%LocalAPPDATA%\KitLugia` (JSON + logs, porque o `LogStore` trunca o log no
  primeiro uso) **antes de qualquer coisa**;
- restore como **último passo**, inclusive em caminho de exceção.

| Cenário | Verifica | Resultado |
|---|---|---|
| S1 | standby abaixo do limiar → não age | `count=0`, sem motivo de erro |
| S2 | **gate de RAM livre segura (G1)** | `free=13021 MB, limite=0` → `count=0`, motivo `'memória livre 13021 MB ≥ limite 0 MB'` |
| S3 | **purge real, medido antes/depois** | `476 → 0 MB`, `count=1`, `ntStatus=0x00000000` |
| S3 | `IslcLastCleanMB` é o valor **depois** | `0` (antes era o valor de antes — G2) |
| S4 | intervalo mínimo segura | motivo `'intervalo mínimo (3600s)'` |
| S5 | foreground bloqueia | motivo `'app em primeiro plano'` |
| S6 | guarda de reentrância segura (G10) | motivo `'purge anterior em andamento'` |
| S6 | soltar a guarda devolve o purge | sentinela em `IslcLastNtStatus` substituída → a task rodou |
| F2 | persistência das 4 preferências | `1234 / 777 / 45 / 0` gravados e lidos de volta |

Restore verificado de fora: `IslcThresholdMB=4065` antes e depois da fase 2, e as
3 chaves novas ausentes de novo — o backup devolve o estado exato.

### 9.5 BUG QUE O HARNESS PEGOU (e que o build não pegaria)

**A guarda de reentrância ficava presa em `1` para sempre.**

Eu tinha escrito `Interlocked.CompareExchange(ref _isPurgingStandby, 1, 0)`
**antes** dos filtros, e cada `return` de filtro abandonava a guarda ligada. O
único lugar que limpava era o `finally` da `Task.Run` — que nem chegava a ser
agendada. Resultado: no **primeiro tick sem ação** (standby abaixo do limiar, que
acontece em quase toda máquina), o purge morria em silêncio até o app reiniciar.

Sintoma no harness: a partir do segundo cenário, todo motivo era
`'purge anterior em andamento'`, e o S3 retornava `ntStatus=0xFFFFFFFF` (o valor
inicial, porque `TryPurgeStandbyList` nunca era chamado).

**Correção:** reservar a guarda só **depois** de todos os filtros, imediatamente
antes de despachar a task. Registrado em comentário no próprio método.

> É exatamente a classe de defeito que a §1.2 descreve (falha silenciosa) —
> introzida por mim ao "consertar" a G10. Build limpo, 15 asserções do Core e 26
> da página: **nenhum delas pegaria**. Só exercitando o método real apareceu.

### 9.6 O que ainda não foi validado

O ciclo completo do `MonitorTick` (que chama `CheckAndCleanStandby` dentro de
`RunMonitorCycle`, junto com boost, Firemin, anti-leak) não é exercitado pelo
harness — ele exige o `Initialize()` completo. **Falta** confirmar no app real:
ligar o toggle e ver no log `🧹 Standby: A → B MB (liberou C MB)` e o badge com
`Aguardando: <motivo>`.