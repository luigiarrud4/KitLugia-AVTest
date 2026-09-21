# KitLugia — Diagnóstico de Armazenamento (aba "Disco" do TMOG)

> "Meu disco está em 100%. **O que exatamente está fazendo isso?**"
>
> Não é um monitor de "Disco 100%": o Kit separa **FATO → HIPÓTESE → PROVA → AÇÃO**.
> Nenhuma conclusão é afirmada sem medição.

## Onde vive o quê

| Peça | Arquivo |
|---|---|
| Motor (amostragem, ranking, impacto, suspender/retomar, investigar, serviço por PID, segurança) | `KitLugia.Core/TaskManager/StorageDiagnostics.cs` |
| Aba (UI, ações, relatório IA, medidores, histórico) | `KitLugia.GUI/Windows/TaskManager/KitTaskManager.Storage.cs` |
| XAML da aba + botão na sidebar | `KitLugia.GUI/Windows/TaskManager/KitTaskManagerWindow.xaml` |
| E/S por processo (leitura/escrita/ops separadas) | `KitTaskManagerWindow.xaml.cs` (campos `DiskRead/WriteBytesPerSec`, `DiskOpsPerSec` do `ProcessRow`) |

O motor **não** enumera processos nem relê contadores de E/S: os contadores do kernel
são **destrutivos** (a segunda leitura no mesmo tick devolve 0 porque o delta já foi
consumido). A aba consome o snapshot que o refresh de 1 s do TMOG já produziu.

## Fontes de dados (todas validadas nesta máquina pt-BR)

| Métrica | Fonte | Por quê |
|---|---|---|
| Atividade %, fila, latência, R/W, ops/s por disco físico | `PerformanceCounter("PhysicalDisk", ...)` persistente | Funciona em pt-BR (validado: categoria "PhysicalDisk" resolve com nomes em inglês). Instâncias vêm de `GetInstanceNames()` ("0 D:", "1 C:"). |
| Modelo / meio (HDD·SSD·SCM) / barramento (NVMe·SATA·USB) / saúde | `MSFT_PhysicalDisk` (`root\Microsoft\Windows\Storage`) | Única fonte confiável do **tipo**: `Win32_DiskDrive` reporta NVMe como "SCSI". `MediaType` 3=HDD 4=SSD 5=SCM; `BusType` 11=SATA 17=NVMe. |
| Serviço hospedado no PID | `Win32_Service WHERE ProcessId > 0` (via `NativeHardware.Cim`, cache 30 s) | Permite oferecer **reiniciar/parar o serviço** — a intervenção correta — em vez de matar o processo. Um PID pode ter vários (svchost). |
| E/S por processo | Snapshot de 1 s do TMOG (`NativeMetricsHelper` + `ProcessIoHelper`) | `IO_COUNTERS` do kernel: leitura/escrita/ops por PID, sem `OpenProcess` extra. |
| Elevação do processo | `OpenProcessToken` + `TokenElevation` | Mostra no dossiê se o processo está elevado. |
| Disco físico do executável | letra da unidade ∩ letras da instância do disco | "C:\...\app.exe → Disco 1 (C:) · SSD" sem consulta extra. |

## Decisões de medição que mudaram o resultado

### 1. Atividade NÃO vem de `% Idle Time`

`% Idle Time` é `PERF_100NSEC_TIMER_INV`. Quando o provedor de performance **não
atualiza** o valor (disco ocioso), o delta vira 0 e a fórmula devolve **100% de
atividade falsa** — exatamente o oposto do que o usuário precisa saber. Isso foi
reproduzido: um HDD 100% ocioso lia "ativ=100,0% R=0 B/s fila=0,00".

```csharp
// Atividade = tempo ocupado / tempo decorrido = latência × transferências
if (rate.TransfersPerSec <= 0) rate.ActivityPct = 0;
else rate.ActivityPct = Clamp(rate.LatencyMs / 1000.0 * rate.TransfersPerSec * 100.0, 0, 100);
```

É o **mesmo cálculo** do "Tempo ativo" do Gerenciador de Tarefas do Windows, mas
derivado de dois contadores de taxa que degradam para 0 (nunca para 100% falso).
Validação cruzada: latência 0,2 ms × 16 ops/s = 0,32% ↔ medido 0,3%.

### 2. `100%` sozinho não significa nada

O texto do diagnóstico distingue três situações **antes** de culpar alguém:

- **Transferência alta** (≥ 300 MB/s): trabalho pesado legítimo (copiar/instalar).
- **Dispositivo lento** (latência ≥ 25 ms **e** vazão < 40 MB/s com atividade alta):
  o disco não dá conta — HDD fragmentado, unidade cheia/defeituosa, driver do
  controlador. Aqui **nenhum processo é o culpado**.
- **Disco ocupado** (o caso geral): atividade/fila/latência altas com vazão normal.

A tela mostra sempre `atividade`, `fila` e `latência` juntas justamente para o caso
"100% com 18 MB/s, fila 14,7, latência 42 ms" não ser lido como "algo transferindo muito".

### 3. Seleção do disco em foco

A faixa **segue o disco mais ocupado** enquanto o usuário não clicar num chip. Travar
no primeiro render mostraria o disco errado (o primeiro tick pega um instante
qualquer) e a faixa contradiria o diagnóstico. Ao clicar num chip, a escolha fica
fixa e o diagnóstico passa a descrever aquele disco — avisando se **outro** estiver
mais ocupado.

### 4. Ops/s revelam o que o MB/s esconde

E/S aleatória pequena satura o disco com poucos MB/s. Por isso a coluna "Ops/s" é
real (vem de `io.ReadOpsPerSec + io.WriteOpsPerSec`), não decorativa.

## Teste de impacto — de hipótese a correlação

O processo é **suspenso de verdade** (`NtSuspendProcess`), o disco é medido de novo e
o processo é **sempre retomado** (`finally`, inclusive em cancelamento/erro).

- Baseline padrão 5 s; teste 8 s (varredura usa 4 s + 6 s por candidato).
- O disco alvo é escolhido no início e **mantido** nas duas fases — comparar discos
  diferentes invalidaria o teste.
- O processo precisa ter gerado E/S no baseline (`> 64 KB` no total acumulado),
  senão o teste responde "não estava realizando E/S suficiente" **sem suspender nada**.
- O disco precisa estar saturado antes (`atividade ≥ 25%` **ou** `fila ≥ 2` **ou**
  `latência ≥ 15 ms`), senão o veredito é INCONCLUSIVO — "caiu de 2% para 1%" não prova nada.
- Índice de correlação = queda ponderada (atividade 50%, fila 30%, latência 20%).
- Vereditos: **FORTE** (≥60% e fila/latência ≥40%) · **MODERADA** (≥30%) ·
  **FRACA** (≥12%) · **NENHUMA** (<12% → "não é a causa") · **INCONCLUSIVO**.

**Varredura automática** ("continua testando até encontrar"): testa os 5 maiores
consumidores em sequência, para no primeiro **FORTE**, e no fim mostra o resumo com o
veredito de cada um. Candidatos do núcleo do Windows e de risco conhecido **não entram**
(a varredura suspende automaticamente e não há como confirmar caso a caso).

## Segurança — dois níveis, porque um é reversível

| Nível | O que é | Comportamento |
|---|---|---|
| **CRÍTICO** (bloqueio rígido) | `System`(4), `Registry`, `Memory Compression`, `smss`, `csrss`, `wininit`, `winlogon`, `services`, `lsass`, `lsaiso`, `dwm`, `fontdrvhost`, `audiodg`, `LogonUI` | Suspender **e** Force Stop ficam desabilitados; a dica mostra o motivo em português. |
| **RISCO** (aviso + confirmação) | `svchost`, `SearchIndexer`, `MsMpEng`, `NisSrv`, `explorer`, `TrustedInstaller`, `spoolsv`, `SecurityHealthService`, `WmiPrvSE` | Permitido (é reversível e é o caso de uso real — ex.: `SearchIndexer`), mas com diálogo explicando o que o usuário vai perceber. |

Além disso: `ResumeAll()` roda no `Closing` da janela — **nunca** fica processo
suspenso para trás. Force Stop é sempre por último (`FindBlockingProcesses` + `Unlock`,
mesmo fluxo dos outros botões do TMOG).

## Relatório para IA

"Cobrir relatório" gera um bloco com: discos físicos (modelo/meio/barramento/números),
diagnóstico textual, a lista de processos com E/S (CSV `;`), o teste de impacto
antes/depois (quando existir), o dossiê de investigação (quando aberto) e uma seção
**METODOLOGIA** explicando os números para a IA não inventar interpretação.

## Armadilhas encontradas (para não repetir)

1. **`% Idle Time` mente** quando o provedor não atualiza — ver acima.
2. **Contadores de E/S são destrutivos**: ler duas vezes no mesmo tick devolve 0. O
   ranking só consome o snapshot do refresh.
3. **`ObservableCollection.Clear()` mata a seleção**: o `DataGrid` limpa a seleção e
   dispara `SelectionChanged(null)`. Reconstruir a lista a cada 1 s fazia a linha
   marcada "desmarcar", os botões piscarem e a dica voltar para "marque um processo".
   A reconciliação agora é por `Move`/`Insert`/`RemoveAt` e **nunca** `Clear()`.
4. **Estado suspenso só existe depois do tick assíncrono**: sem chamar
   `UpdateStorageActionButtons()` quando o estado muda no merge, o botão "Retomar"
   ficava cinza mesmo com o processo já suspenso.
5. **`Argument` de linha de comando com barra final escapa a aspa** (`"...\pasta\"`) —
   ao spawnar um processo auxiliar de teste, use `TrimEnd('\\')`.
6. **`MessageBox` é ambíguo** no projeto GUI (`UseWindowsForms=true`): use
   `System.Windows.MessageBox` explicitamente.

## Validação usada (e reproduzível)

- **Harness de Core** (`.tmp-storage-harness`): discos, ranking, dossiê, bloqueio de
  segurança, teste de impacto ponta a ponta contra um processo queimador real
  (155 MB/s → suspenso → 0,2%), suspender/retomar manual e `ResumeAll`. **ALL-PASS**.
- **Harness de UI** (`.tmp-storage-ui`, técnica do `TASKMANAGER_UI_DEBUG.md`): abre a
  **janela real**, clica no botão da aba, confere métricas, seleciona linha, clica em
  Investigar/Suspender/Retomar, valida a estabilidade da seleção entre refreshes,
  renderiza um cenário sintético (100%/18 MB/s/42 ms/14,7) e o relatório. **ALL-PASS**.

## Escuta de stutters de áudio (cruzamento com o disco)

O Kit abre uma captura em **loopback** do dispositivo de saída padrão e pergunta ao motor
de áudio do Windows, pacote por pacote, se ele precisou pular quadros
(`AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY` — a API oficial de detecção de glitch desde o
Windows 7). Só leitura do buffer: nada é gravado, nenhum driver é instalado.

### Honestidade do rótulo (fato ≠ indício)

| Rótulo | Quando |
|---|---|
| **CONFIRMADO** | a flag de descontinuidade veio do próprio Windows **com som audível tocando** |
| **INDÍCIO** | descontinuidade sem som tocando (invisível para o usuário) ou salto pequeno de posição |
| **DESCARTÁVEL** | artefato de arranque/troca de stream — não entra como prova |

Além disso, o monitor se **auto-observa**: se o próprio Kit estiver atrasado no instante
do evento (`SelfStarvation`, atraso do próprio loop de leitura), o evento é rebaixado —
o Kit não acusa a cadeia de áudio de um problema que pode ser dele.

### Correlação com o disco

Cada estalo carrega o snapshot do instante: atividade/fila/latência/meio do disco em
foco, top E/S por processo, quem tocava áudio (PIDs), CPU, DPC e paginação. O card da
aba Disco conclui em português — incluindo o caso honesto "os discos estavam calmos
nesse instante: o travamento **provavelmente NÃO** foi causado pelo armazenamento"
(e aponta para drivers/DPC na aba Latência).

### Ciclo de vida (overhead próprio controlado)

- **Liga sozinha** ao abrir a aba Disco ou Latência — o recurso não depende de o usuário
  achar um botão.
- **Respeita escolha do usuário**: se ele desligar a escuta, o auto-start não religa.
- **Para ao fechar a janela** (teardown no `Closing`): sem thread zumbi a ~100 Hz lendo
  o buffer depois que a medição saiu da tela.
- Custo: 1 leitura de buffer a cada 10 ms (thread `AboveNormal`), sem E/S de disco.

### Ensaio do "pior caso"

O card exibe o pior caso **priorizando os CONFIRMADOS pelo Windows**; indícios não
confirmados aparecem rotulados como "maior indício (sem confirmação do Windows)" — não
dão título de "pior caso" a algo que o próprio Kit pode ter causado.

### Validação (reproduzível)

- **Core** (`.tmp-audio-harness`): tom real em loop + estalo forçado congelando o
  `audiodg.exe` por 400 ms → flag lida de verdade, "motor parado por ~400 ms" reportado,
  classificação CONFIRMADO, tabela de decisão (4 ramos) por asserção. **39 OK / 0 FALHA**.
- **UI** (`.tmp-ui-harness`, técnica do `TASKMANAGER_UI_DEBUG.md`): auto-start na aba
  Disco, toggle respeitado, estalo real na tabela de Eventos da aba Latência, card
  cruzando com disco, relatório combinado (latência + áudio + metodologia), diário de
  reversão limpo e encerramento da escuta ao fechar a janela. **26 OK / 0 FALHA**.

## Recuperação automática do áudio (o "desligar/ligar" que resolve, sem o usuário mexer)

O usuário provou empiricamente: desligar/ligar a saída de áudio zera os estalos — é o
reset do motor de áudio (`audiodg`). O Kit pode fazê-lo sozinho, **imperceptível**:
congela o motor por ~300 ms e retoma pelo mesmo handle ("sincronizar de volta").

### Gatilho conservador (`ShouldAutoRecover` — função pura, provada por asserção)

- **2+ estalos CONFIRMADOS e AUDÍVEIS em 90 s** (um estalo isolado não justifica mexer);
- cooldown de **60 s** entre recuperações;
- máximo de **3 por hora** (resetar repetidamente não resolve causa persistente);
- nunca durante um reset em andamento (sem loop de auto-recuperação).

Avaliado ~1x/s dentro do próprio loop do monitor (custo desprezível). Desligado por
padrão; o usuário liga no checkbox do card de áudio ("Recuperar sozinho").

### Separando evento real de evento provocado (regra do ChatGPT)

O reset do Kit **provoca** uma descontinuidade real no stream. Sem marcação, o Kit
"se recuperaria" em loop do próprio reset. Por isso:

- todo evento dentro da janela de recuperação (~1,8 s) sai como **PROVOCADO** —
  na tabela de Eventos aparece com gravidade verde **"Recuperação"**, nunca
  "Grave"/"Indício";
- `GlitchCount`, `AudibleConfirmedCount` e o gatilho **excluem** os provocados;
- o relatório IA tem seções separadas: **EVENTOS REAIS** (estatísticas) e
  **EVENTOS PROVOCADOS PELO TESTE/RECUPERAÇÃO** (transparência);
- o texto da linha diz: "O estalo que vier agora foi o PRÓPRIO reset, não é problema novo".

### Segurança da execução

- Identidade do PID validada antes (`GetIdentity`/`SameProcess` — imune a PID reciclado);
- retomada pelo **mesmo handle** no `finally`: ou é o processo que congelamos, ou o
  handle já não é dele (nunca retoma o PID errado);
- sem acesso/admin ou Windows recusando → mensagem clara, nenhuma intervenção parcial;
- cada recuperação entra no diário de intervenções (revertido por natureza).

### Validação (reproduzível)

- **Core** (`.tmp-recover-harness`): 6 ramos da tabela de decisão por asserção; reset
  **real** com som tocando — 2 estalos semeados congelando o motor, o loop do monitor
  executou a recuperação sozinho, o som voltou, o evento do reset saiu PROVOCADO e fora
  das estatísticas, sem re-gatilho. **17 OK / 0 FALHA**.
- **UI** (`.tmp-recover-ui`, técnica do `TASKMANAGER_UI_DEBUG.md`): checkbox do card liga/
  desliga no motor, a recuperação reflete na linha de estado da UI e os eventos do reset
  aparecem como "Recuperação" (verde) na tabela. **7 OK / 0 FALHA**.
