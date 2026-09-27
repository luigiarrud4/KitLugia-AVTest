# Recuperação automática de áudio — revisão final de robustez

**Data:** 25/09/2026 · **Escopo:** `KitLugia.Core\TaskManager\AudioGlitchMonitor.cs`
(+ `StorageDiagnostics` para identidade de processo) · **Sem mudança de comportamento
validado:** gatilho, cooldown, limite por hora e o próprio reset continuam como estavam.

## Como isto foi verificado

Os testes automatizados que o projeto tinha **não existiam** para este módulo: as
tabelas de decisão (`ShouldAutoRecover`, `Classify`) tinham sido escritas "puras, para
poderem ser provadas por asserção", mas nenhum projeto de teste as exercitava. Este
harness foi criado seguindo a convenção de `tests/` (mesmo padrão do `PrivacyDryRun`):

```bash
dotnet run --project tests/AudioRecoveryAudit            # tudo (precisa de admin p/ a fase 2)
dotnet run --project tests/AudioRecoveryAudit -- phase1  # só a fase 1
```

- Roda contra o **código real** do Core (reflection nos membros privados), não contra
  uma cópia da lógica.
- **Fase 1:** tabelas de decisão, isolamento dos eventos provocados, cooldown/limite pelo
  caminho real, PID reciclado, identidade do processo e a observação pós-recuperação.
- **Fase 2:** recuperação **real** — suspende e retoma o `audiodg` de verdade (~300 ms).
  Sem privilégio de administrador ela é saltada (`SKIP`) e nada é tocado no áudio.
- A fase 1 também executa **um** reset real de propósito (é onde se prova que o critério
  fecha e o reset acontece de ponta a ponta).

**Resultado: 53 asserções, 53 passaram, 0 falharam** (execução elevada, host do usuário).

## Veredito por ponto do checklist

### 1. Diferenciar REAL / INDÍCIO / PROVOCADO — OK

`Classify` (pura) devolve o rótulo e a explicação:

| Situação | Rótulo |
|---|---|
| flag oficial do motor + Kit em dia | `CONFIRMADO` |
| flag + Kit atrasado (`SelfStarvation`) | `SUSPEITO` |
| flag + arranque/troca de stream | `SUSPEITO` |
| erro de timestamp do stream | `SINALIZADO` |
| salto de posição sem flag | `SUSPEITO` (indício, não prova) |
| dentro da janela pós-reset | `PROVOCADO` |

Provado por asserção para as 6 combinações. O rótulo `CONFIRMADO` só sai quando o
Windows levantou `AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY` **e** o leitor do Kit não
tem como ter causado aquilo.

### 2. Evento provocado nunca entra nas estatísticas — OK

- `GlitchCount` e `AudibleConfirmedCount` filtram `!ProvokedByKit` (provado no caminho
  real de `Report`, não só por leitura de código).
- O evento provocado **continua visível na lista** (transparência) — some das contagens,
  não da tela nem do relatório (que tem seção própria para ele).
- **Não dispara outra recuperação:** o filtro de gatilho usa `Kind == "CONFIRMADO" &&
  !ProvokedByKit`; provado com 1 evento provocado no histórico → `MaybeAutoRecover`
  não executou nada.
- **Não dispara enquanto o reset está em andamento:** a própria janela de provocado
  vira o motivo `"há um reset do próprio Kit em andamento"` — testado com o histórico
  contendo 2 provocados e a janela aberta (nenhum reset empilhado em cima do outro).
- **Não altera a conclusão sobre a estabilidade:** `BuildConclusion`, `BuildAiReport`,
  o card de áudio da aba Latência, o card de áudio da aba Disco e o cruzamento de
  áudio do "Testar impacto" (`BuildAudioImpactCrossCheck`) todos filtram
  `ProvokedByKit`/`Kind == CONFIRMADO`. Nenhum caminho de código reconta a lista crua.

### 3. Diferenciar executada / eficaz / sem efeito / recusada — OK (implementado agora)

Antes só existiam "executada" e "recusada/falhou" (`_lastRecoveryText`). Foram
acrescentados, **sem tocar em nenhuma decisão**:

- `executada` — `"motor de áudio sincronizado de volta (motivo: …)"`.
- `recusada/falhou` — `"não executada: o Windows recusou o reset (0x…)"`,
  `"sem acesso ao motor de áudio (rode o Kit como administrador)"`,
  `"cancelada: o PID do motor trocou antes do reset"`, `"Falha na recuperação: …"`.
- `eficaz` / `sem efeito` — veredito da observação (ponto 4).

### 4. Janela de observação posterior, sem declarar resolvido — OK (implementado agora)

`RecoveryAssessSeconds = 90` (a mesma janela do gatilho). A observação começa quando o
reset deixa de ser "provocado" (`RecoverySuspendMs + 1500 ms`) e, ao vencer, o Kit
registra:

- `"Avaliação 90 s depois: nenhum estalo novo — a recuperação foi EFICAZ."`
- `"Avaliação 90 s depois: N estalo(s) novo(s) — a recuperação NÃO resolveu a causa, que
  continua agindo (o limite por hora evita resetar em vão)."`

Regras de honestidade verificadas por teste:

- Janela aberta → **nenhum veredito é inventado** (o texto continua "Observando os
  próximos 90 s…").
- Só eventos **provocados** na janela (o eco do próprio reset) → continua `EFICAZ`.
- `Clear()` no meio da observação **cancela** em vez de declarar eficaz
  ("Avaliação cancelada: a lista de eventos foi limpa antes da janela terminar").
- A observação é concluída mesmo se o usuário desligar o `AutoRecover` no meio
  (a avaliação roda no tick do loop, fora do guard do `AutoRecover`).
- O Kit **não declara o problema resolvido** só porque o `audiodg` voltou: a
  mensagem de execução diz explicitamente que o estalo que vier agora é o próprio reset.

### 5. Cooldown e limite por hora sob todos os cenários — OK

| Cenário | Como é protegido | Testado |
|---|---|---|
| eventos rápidos consecutivos | cooldown de 60 s a partir de `_recoveries[^1]` | sim |
| exceções | `finally { NtResumeProcess(h); }` no mesmo handle + try/catch externo | por inspeção¹ |
| reinicialização do Kit (processo) | `_recoveries` é do processo: **um Kit novo recomeça a contagem** | risco aceito² |
| reinicialização/troca do `audiodg` | `SameProcess` antes de suspender; handle retomado no `finally` | sim (identidade) |
| PID reciclado | `GetIdentity` = PID + **hora de criação**; divergência → recusa | sim |
| falha parcial | cada recusa tem texto próprio e **não** entra em `_recoveries` | sim |
| recuperação em andamento | janela de provocado bloqueia o gatilho; avaliação é síncrona no thread do loop | sim |

¹ Não é possível forçar uma exceção dentro de `NtSuspendProcess` de fora; o que o
harness prova é que a retomada está no `finally` com o **mesmo** handle e que uma
exceção posterior cai no `catch` externo sem deixar o motor congelado (o `audiodg`
continuou vivo e com o **mesmo PID** após os resets reais).
² O limite de 3/hora é por sessão do Kit. Reiniciar o Kit zera a contagem (não há
persistência em disco). Mantido assim de propósito: o limite existe para impedir um
loop infinito **dentro** de uma sessão, não para sobreviver a um reinício deliberado
do usuário. Se um dia quiser persistir, o lugar é o mesmo JSON/registry dos outros
toggles do TaskManager.

### 6. Limite atingido → pausado, mas continuando a diagnosticar — OK (surfacing novo)

`ShouldAutoRecover` devolve `"limite de 3 recuperações por hora atingido — resetar
repetidamente não resolve se a causa persiste"` e o motor **não tenta de novo**. O que
foi acrescentado é o **motivo visível**: o relatório de IA ganhou a seção
`--- RECUPERAÇÃO AUTOMÁTICA DO ÁUDIO ---` com ligada/desligada, resets na sessão, resets
na última hora, o estado agora (critério fechado ou o motivo de não resetar) e a última
recuperação. Provado que, com o limite atingido, os estalos reais **continuam sendo
registrados** (o diagnóstico não para).

### 7. Caminho "recuperação → provocado → detecção → nova recuperação" — fechado

Três barreiras independentes, as três testadas:

1. `Report` marca `ProvokedByKit`/`Kind = "PROVOCADO"` em tudo que chega dentro de
   `ResetSuspend + 1,5 s`.
2. O gatilho conta apenas `CONFIRMADO && !ProvokedByKit`.
3. A tabela de decisão recusa recuperar enquanto houver reset "pendente".

### 8. Não alterar o comportamento já validado — OK

Nada mudou no que já funcionava: gatilho (2 confirmados audíveis em 90 s), cooldown
(60 s), limite (3/hora), duração do congelamento (300 ms), marcação de provocado
(`+1,5 s`), `Clear()`, classificação, contagens. O que foi acrescentado é **só leitura
de estado** (uma janela de observação e uma seção de texto no relatório) — nenhuma
decisão nova, nenhum timer novo, nenhum thread novo.

## Riscos residuais (honestos)

1. **Identidade desconhecida falha-aberta.** Se `GetIdentity` não conseguir ler a hora
   de criação (sem permissão), `SameProcess` devolve `true` e o reset prossegue. É
   deliberado ("travar o diagnóstico por falta de leitura seria pior"), mas é o único
   ponto em que a proteção contra PID reciclado pode não agir. Testado e documentado.
2. **Limite por sessão** (ver nota ² acima).
3. **O veredito da observação mede o que o Kit vê.** Se nada estiver tocando durante os
   90 s, "EFICAZ" significa "nenhum estalo confirmado apareceu" — não significa
   "o som ficou bom" (não havia som). A janela de silêncio já é tratada no resto do
   módulo (`Audible`, `_audioActive`), mas o veredito não a distingue hoje.
4. **`RecoveryAssessSeconds` = 90 s** foi escolhido igual à janela do gatilho. Um
   problema que só reaparece depois de 2 minutos aparece como "eficaz" e volta na
   renovação do gatilho — o que é o comportamento correto, mas convém saber.

## Ficheiros

- `KitLugia.Core\TaskManager\AudioGlitchMonitor.cs` — janela de observação
  (`RecoveryAssessSeconds`, `_assessFrom/_assessAfter`, `AssessLastRecovery`),
  cancelamento honesto no `Clear()` e a seção de recuperação no `BuildAiReport()`.
- `tests\AudioRecoveryAudit\` — harness novo (csproj + Program.cs), fora da solução
  (como o `PrivacyDryRun`), 53 asserções.
- `KitLugia.GUI\Windows\TaskManager\KitTaskManager.Audio.cs` — **sem mudanças**: o card
  já mostra `LastRecoveryText`, que agora carrega o veredito.
