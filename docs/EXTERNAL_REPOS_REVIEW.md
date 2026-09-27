# Revisão de 5 repositórios externos (25/09/2026)

**Alvo da avaliação:** diagnóstico de stutter / DPC / áudio / E/S do TaskManager do KitLugia.
**Método:** README + código real (arquivos buscados direto do GitHub) + verificação local na
máquina do usuário quando o achado era implementável. Nada foi copiado para o projeto: só
técnica, ideia e fato (as licenças estão no fim e mandam nisso).

## Veredito rápido

| Repositório | O que é de fato | Licença | Vale a pena? |
|---|---|---|---|
| `xdr0p/stuttometer` | Flight recorder ETW de micro-stutter, frame pacing e glitch de áudio (C++20, Win32) | GPL-3.0 | **SIM — o achado mais valioso do lote** (técnica, nunca código) |
| `swyongqiang1-stack/WhoLagged` | 7 KB de C#/TraceEvent: culpado por contexto de switch e latência de disco | GPL-3.0 | Parcial (prova de conceito; código não utilizável) |
| `Bmokshith2406/RCA-GENAI-MONITOR` | ETW + estatística (z robusto, Mahalanobis, lead/lag) + Gemini | **sem licença** | Parcial (ideia de ranking presta, matemática não) |
| `faratech/wfdiag` | Suíte de diagnóstico Rust/WinUI3 (49 checks, 37 regras, remediação em tiers) | CC BY-NC-ND 4.0 | Só arquitetura/política (licença proíbe derivar) |
| `Swatto86/eir` | Agente autônomo de reparo com IA (Rust/Tauri, serviço LocalSystem) | MIT | Pouco para o TaskManager; útil como política de segurança |

---

## 1. O achado que muda o diagnóstico de áudio

O stuttometer assina o provider **`Microsoft-Windows-Audio`** para capturar glitches de áudio
sem captura de loopback. Fui verificar nesta máquina: **o Windows já classifica o nosso
problema por nome** — e não é só "perdeu N ms".

```
$ wevtutil gp Microsoft-Windows-Audio /ge:true /gm:true
name: Microsoft-Windows-Audio
guid: ae4bd3be-f36f-45b6-8d21-bdd6fb832853
channels:
  channel: Microsoft-Windows-Audio/GlitchDetection   id: 20
opcodes:
  op_EVT_GLITCH_CM_RENDER              opcode 15  Capture Monitor Render Glitch
  op_EVT_GLITCH_CM_CAPTURE             opcode 16  Capture Monitor Capture Glitch
  op_EVT_GLITCH_APO_FORMAT_CONVERT     opcode 17  APO Glitch: Format Converter INF detected
  op_EVT_GLITCH_CP_CLIENT_INPUT_NO_MESSAGES      opcode 18  Engine Glitch: CP Client Input Endpoint - No Messages in queue
  op_EVT_GLITCH_CP_CLIENT_INPUT_SIZE_MISMATCH    opcode 19  Engine Glitch: Input Queue item does not match requested size
  op_EVT_GLITCH_CP_CLIENT_OUTPUT_SERVER_OVERREAD opcode 20  Engine Glitch: Output Endpoint - Server Overread
  op_EVT_GLITCH_CP_CLIENT_OUTPUT_READ_POINTER_OVERWRITE opcode 21  Engine Glitch: Output Read Pointer Overwrite

$ wevtutil gl Microsoft-Windows-Audio/GlitchDetection
enabled: false            type: Operational       isolation: System
logFileName: %SystemRoot%\System32\Winevt\Logs\Microsoft-Windows-Audio%4GlitchDetection.evtx
maxSize: 1052672          retention: false
channelAccess: O:BAG:SYD:(A;;0xf0007;;;SY)(A;;0x7;;;BA)(A;;0x3;;;BO)(A;;0x5;;;SO)(A;;0x1;;;IU)...
```

**Por que isto importa para o KitLugia.** Hoje o `AudioGlitchMonitor` mede *o quanto*: a flag
oficial `AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY` mais os quadros que faltaram na posição do
dispositivo. Com este canal, o próprio Windows entrega *o porquê* — "Engine Glitch: sem
mensagens na fila do endpoint de entrada", "APO Glitch: conversor de formato", "Capture Monitor
Render Glitch" — com PID/TID e timestamp do evento. É a mesma natureza dos dados que já usamos
(medição do SO, não inferência nossa), então encaixa na regra do Kit sem virar opinião.

**Plano de implementação (sem mexer no que já funciona):**

1. **Habilitar o canal** (`wevtutil sl Microsoft-Windows-Audio/GlitchDetection /e:true`), opt-in
   no card de áudio, exigindo admin — e **reverter ao desligar** (o diário de intervenções do
   `StorageDiagnostics` já é o lugar disso). Habilitar é escrita no log (BA = 0x7).
2. **Ler sem elevação**: o SDDL dá `0x1` (leitura) para *Usuários Interativos* (`IU`), então o
   TaskManager consegue consumir via `EventLogWatcher`/`EventLogReader` mesmo sem admin; buffer
   de ~1 MB basta para horas de uso normal.
3. **Cruzar com o nosso glitch**: casar por timestamp (o nosso evento tem hora e duração; o do
   canal tem hora e motivo) e exibir "por que" ao lado de "quanto". Motivo vindo do canal =
   fato; o cruzamento temporal continua sendo apresentado como indício.

**Antes de codar, validar (não fazemos nada sem isso):**

- O manifesto desta máquina expõe **opcodes**, não "Event ID 11" — a afirmação do README do
  stuttometer não bate com o que o Windows daqui declara. Precisa de uma sessão ETW real para
  ver o que chega de verdade (id/opcode + template) antes de escrever o parser.
- Canal com `isolation: System` e 1 MB de teto: em máquina com glitch constante o
  arquivo roda rápido — o leitor precisa tolerar perda de eventos antigos.
- Habilitar canal altera configuração do Windows: obrigatório ser reversível e visível.

### O resto do stuttometer que vale anotar

- **Flight recorder em anel pré-alocado** (262.144 slots × 64 B, seqlock por slot, registro
  normalizado de 56 B) com flush ETW a cada 100 ms e ressincronização UTC↔QPC a cada ≤3 s: nada
  de alocar enquanto mede — o mesmo princípio que os nossos timers de 1 s respeitam ao não
  alocar por tick. É o desenho a seguir se um dia guardarmos eventos contínuos.
- **Deduplicação** de glitches do compositor em janelas de 50 ms (`DWM_GLITCH_DEDUPLICATED`) —
  a mesma classe de problema que a janela de "provocado" resolve na nossa recuperação de áudio
  (evento ecoado pelo próprio reset não conta duas vezes).
- **Limiares adaptativos ao hardware**: escala com o vblank do monitor (4,17 ms @240 Hz …
  16,67 ms @60 Hz) em vez de ms fixo — relevante se entrarmos em frame pacing.
- **Judder por mediana móvel + multiplicador relativo**, não por limiar fixo: pega o micro-stutter
  que nunca cruza um valor absoluto (o nosso "tempo ocupado do disco" tem o mesmo ponto cego).
- Relatório com **causas ranqueadas + score de confiança + linha do tempo de evidências** — o
  nosso relatório de IA tem hipóteses, mas não um número explícito de confiança.
- **`--redact`**: opção de exportar relatório sem nomes de processo/caminho/usuário. Barato e
  útil para o nosso "copiar relatório" ir para fórum sem vazar dados pessoais.

---

## 2. RCA-GENAI-MONITOR — separando a ideia boa da matemática ruim

**Presta (vale implementar em C#):**

- **Lead/lag por correlação cruzada** (`_lead_lag_score`): recompensa o PID que **sobe antes** do
  evento — `lag_factor` 1,0 se o processo lidera, 0,8 se simultâneo, 0,5 se atrasado. Hoje o Kit
  responde "quem estava no topo no instante X"; isto responde "quem subiu **antes** de X", que é
  a pergunta causal.
- **Z-score robusto** (mediana + MAD, com piso) sobre baseline rolante em vez de limiar fixo —
  os nossos limiares de disco (fila ≥ 4, atividade ≥ 85%, latência ≥ 25 ms) são fixos.
- **Confirmação sustentada + cooldown** (30 s acima do limiar, 45 s de cooldown) para não
  disparar em pico isolado — exatamente a filosofia já validada no gatilho do áudio
  (2 confirmados em 90 s, cooldown de 60 s).

**NÃO presta (e é importante dizer):**

- `cosine_similarity(pid_vec, spike_vec)` com `spike_vec` sendo um vetor de constantes
  `[spike_cpu, spike_ram, 1,1,1,1,1,1,1]` — isso mede magnitude/norma, não correlação. Termo
  praticamente inútil.
- `final_score = raw / max(raw)`: **sempre elege alguém com score 1,0**, mesmo quando nada está
  errado. Isso contraria a regra do Kit (fato ≠ indício ≠ hipótese): um ranker que nunca pode
  dizer "ninguém" é um gerador de falso culpado.
- `psutil.cpu_percent(interval=None)` devolve 0,0 na primeira chamada — amostra inválida usada
  como base de "energia".

Se implementarmos algo daqui: score **sem** normalização pelo máximo (limiar explícito em vez
disso) e sempre apresentado como *indício*, com a lista de features que o puxaram para cima.

---

## 3. WhoLagged — 7 KB, um arquivo, três lições

O README promete "análise de DPC em nível de kernel"; o `Program.cs` inteiro tem 7 KB e faz:

```csharp
using var session = new TraceEventSession(KernelTraceEventParser.KernelSessionName, Create);
session.EnableKernelProvider(KernelTraceEventParser.Keywords.ContextSwitch | KernelTraceEventParser.Keywords.DiskIO);
// CSwitch → conta contexto de switch por OldProcessID
// DiskIORead/Write → soma DiskIOTraceData.ElapsedTimeMSec quando > 1 ms
```

Depois, veredito por limiar grosseiro em 10 s de amostragem (contexto de switch > 5000/s, disco
> 500 ms acumulados) e, se o culpado é o PID 4, "é driver, use o LatencyMon".

**Leitura honesta:** é um demo, não uma ferramenta de RCA — o README superdimensiona e não há
medição de DPC nenhuma no código. Ainda assim ensina:

1. `Microsoft.Diagnostics.Tracing.TraceEvent` faz sessão de kernel em .NET **sem P/Invoke** (nós
   escrevemos a nossa na mão no `LatencyMonitor`).
2. **Latência real de E/S por processo** (`DiskIOTraceData.ElapsedTimeMSec`) é algo que o Kit
   não tem: temos quem faz E/S em B/s e a latência/fila do disco *como um todo*. Medir "quem
   causou a espera" em vez de inferir seria um upgrade direto no card de disco.
3. **Sessão de kernel é recurso único global**: se LatencyMon/WPR/PerfView estiver rodando, o
   `StartTrace` falha (o código trata isso com mensagem própria). O nosso `LatencyMonitor` já
   usa a sessão "NT Kernel Logger" (flags DPC|INTERRUPT) e já convive com esse conflito.
   **Consequência de projeto: não abrir uma segunda sessão de kernel para o áudio** — o provider
   `Microsoft-Windows-Audio` é de user-mode e pede sessão normal própria (ou, melhor ainda, o
   canal de Event Log descrito acima).

---

## 4. wfdiag — arquitetura para copiar, código proibido

Licença **CC BY-NC-ND 4.0** (não comercial, sem derivados): nada de código, só ideia. As ideias,
no entanto, são muito alinhadas ao que o Kit já defende:

- Regras determinísticas com resultado **explícito** `verificado / detectado / desconhecido` —
  é a nossa tríade fato / indício / hipótese, formalizada na saída de cada checagem.
- **Toda correção é reverificada recoletando a evidência** depois do fix ("every fix is verified
  by re-collecting its evidence") — exatamente o que a observação pós-recuperação de áudio
  implementada hoje faz (eficaz / não resolveu).
- Remediação em **tiers** com confirmação **forçada no backend**, não só na UI (um cliente
  alterado não consegue pular a confirmação).
- **Trilha de auditoria** em JSONL de toda ação *e de toda decisão de automação* — o nosso diário
  de intervenções é a mesma ideia, hoje só em memória.
- Comparação entre execuções ("virou crítico desde o último scan") — daria um "o que mudou desde
  a última vez" no Resumo do TaskManager.
- Segurança de subprocesso: **allowlist fechada** de comandos com argumentos validados, janela
  oculta e timeout; histórico cifrado com DPAPI; "modo redigido" para exportar.

---

## 5. eir — pouco para o TaskManager, alguma coisa para o resto do Kit

MIT (dá para reusar com atribuição), mas é Rust/Tauri + serviço LocalSystem + IA externa: nada
disso encaixa no TaskManager. O que vale como desenho de segurança:

- **Fila de aprovação persistente**: sobrevive ao reinício; se o serviço caiu entre o clique e a
  confirmação, o item **volta para nova aprovação** em vez de repetir uma ação possivelmente já
  feita. É o mesmo cuidado que o nosso diário de intervenções tenta ter, mas persistido.
- **Aprendizado que só pode reduzir ou reordenar ações**, nunca deixar o agente mais agressivo, e
  todo fato aprendido é visível com Pin / Disable / Forget.
- Garantia dura de "nunca desinstalar" e explicação, em texto simples, do que cada ação fará e se
  é reversível antes de executar.
- Bounded em toda entrada (respostas de IA, anexos, frames do pipe, saída de comando) para um
  provedor ruim não crescer memória sem limite — relevante para o nosso relatório de IA.

---

## Próximos passos sugeridos (por valor/risco)

1. **Alto valor, baixo risco** — causa do glitch de áudio pelo canal `GlitchDetection`: validar
   com uma sessão ETW real (id/opcode/template) e então enriquecer o card de áudio com "por quê",
   mantendo o "quanto" que já medimos.
2. **Médio** — trocar limiar fixo por **baseline rolante + z-score robusto** e favorecer quem
   **lidera** a subida no "quem causou" do disco/latência.
3. **Médio** — **latência de E/S por processo** via sessão de kernel (medida, não inferida),
   respeitando o conflito de sessão única.
4. **Baixo** — score de confiança explícito no relatório de IA e opção "copiar sem dados
   pessoais" (redact).

## Licenças (resumo prático)

| Repositório | Licença | Pode reusar código? |
|---|---|---|
| `xdr0p/stuttometer` | GPL-3.0 | **Não** (copyleft). Técnica/fatos: sim |
| `swyongqiang1-stack/WhoLagged` | GPL-3.0 | **Não**. Técnica: sim |
| `Bmokshith2406/RCA-GENAI-MONITOR` | sem arquivo de licença | **Não** (todos os direitos reservados). Ideias: sim |
| `faratech/wfdiag` | CC BY-NC-ND 4.0 | **Não** (sem derivados). Arquitetura: sim |
| `Swatto86/eir` | MIT | Sim, com atribuição |
