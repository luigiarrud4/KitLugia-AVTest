# GameBoost — lista de verificação para fechar o motor

Documento de trabalho. Cada item diz **o que falta**, **como verificar** e **qual é o critério
de "passa"**. Nada aqui é uma opinião: os itens saíram de ler o motor e de medir.

Estado atual: **build 0 erros / 145 avisos**; regressões Auto 25/25, V4 7/7, fiabilidade 13/13,
sondagem 9/9, todas exit 0. Esta lista é o que falta para poder dizer "o motor está fechado".

**Actualizado após o harness `gbknobs`:** GameClassInfo e Thread Efficiency foram medidos e
são **recusados pelo Windows** (itens 1.1 e 1.2 passaram de "nunca medido" para "inerte").
O timer foi medido e é **seguro nesta máquina** (item 1.4).

Máquina de referência dos testes: Windows 11 build **28000** (Win11 24H2/25H2).

---

## Legenda de estado

| Marca | Significado |
|---|---|
| ✅ | Provado por medição nesta sessão |
| 🔶 | Implementado, **nunca medido** |
| ⛔ | Descobrimos que **não tem efeito** — falta decidir o que fazer |
| ❌ | Falta implementar |

---

## 1. Knobs cujo efeito **nunca foi provado**

O motor tem 9 ajustes por processo. Só 3 estão provados por leitura de volta. Os outros são
"chamamos a API e esperamos que sirva" — que foi exactamente como o bug da memória priority
passe despercebido durante anos.

### ⛔ 1.1 `SetProcessGameClassInfo` (classe 13, GameMode) — RECUSADO pelo Windows

**Medido** (harness `gbknobs`, build 28000, elevado): `SetProcessInformation(cls 13)` devolve
`err=87` (INVALID_PARAMETER) em **todos os tamanhos** (4, 8, 12, 16), tanto no `charmap` como no
próprio processo do Kit, e `GetProcessInformation(13)` também falha.

Como falha em todos os tamanhos, **não é "tamanho errado": a classe não está implementada
neste build.** É inerte.

- **Feito:** `ReportarClasseRecusada(nome, infoClass)` + `_classesRecusadas` (HashSet) — avisa
  **uma vez por classe** e depois cala-se. **Testado** (`gbavis`, exit 0): 1 aviso na 1.ª
  chamada, **0 avisos em 200 chamadas**, e a 2.ª classe tem o seu próprio aviso (1, não 1 por
  thread). Comentário do V4 corrigido.
- **Impacto:** V2/V3/V4 perdem *menos um* ajuste. O GameMode nunca fez nada nesta maquina.
- **Passa se:** o log disser uma vez que a classe foi recusada, e o comentário estiver certo.

### 🔶 1.2 `SetThreadEfficiencyMode` (classe 5, thread-level, P-cores)

**Medido:** `SetThreadInformation(cls 26)` e `SetProcessInformation(cls 5)` devolvem ambos
`err=87` em todos os tamanhos. **Também recusado neste build.**

- **Falta:** remover ou deixar inerte com aviso; confirmar se é específico deste build ou geral.
- **Nota:** não existe API pública para **ler** thread efficiency, portanto mesmo quando é aceite
  só se pode dizer "chamada aceite", nunca "aplicado". Isto é uma limitação permanente a
  registar, não uma falha a corrigir.

### 🔶 1.2 `SetThreadEfficiencyMode` (classe 5, thread-level, P-cores)

- **Falta:** confirmar que a classe 5 faz alguma coisa e que o ganho é real.
- **Como:** aplicar a uma thread de `charmap`, reler o estado. Só com um **jogo real** se
  mede o ganho; o que se pode medir sem jogo é "a API foi aceite ou recusada".
- **Passa se:** o estado relido corresponde ao pedido, ou fica registado que é no-op.
- **Cuidado:** `THREAD_EFFICIENCY_MODE.UseEfficiencyClass` é 1 byte, e o código passa
  `Marshal.SizeOf` — confirmar que o tamanho certo está a ser enviado.

### 🔶 1.3 EcoQoS (`SetEcoQoS` / `SetPowerThrottling`)

- **Já medido:** `GetProcessInformation(ProcessPowerThrottling)` **não expõe o EcoQoS** nesta
  build (devolve −1/FALSE). O bug do revert só se prova **por leitura do fonte**.
- **Falta:** um caminho de prova que não dependa do fonte. Opções: ETW/kernel trace, ou aceitar
  definitivamente a prova por fonte e registar isso como limitação conhesciente.
- **Passa se:** existir uma forma de ler o estado, **ou** a limitação ficar escrita e aceite.

### ✅ 1.4 `BoostTimerResolution` / `RestoreTimerResolution` — seguro nesta máquina

**Medido** (com a semântica certa: em `NtQueryTimerResolution`, `MinimumResolution` é o **maior**
atraso e `MaximumResolution` o **menor**):

```
antes       : maior atraso=15,62ms  menor atraso=0,50ms
apos motor  : granularidade=0,50ms   (pede 1ms -> Windows MANTEM 0,50ms)
apos restore: granularidade=0,50ms
```

O Windows **recusa** descer abaixo de 0,50 ms, e o motor pede 1 ms, portanto **nesta máquina não
piora o timer** (a chamada e' um no-op). O restore é simétrico.

- **Nota para a checklist, não é um bug desta máquina:** o motor foi escrito a assumir 15,6 ms de
  fundo. Numa máquina nesse perfil, pedir 1 ms **é** uma melhoria. Aqui é inerte. O teste
  apenas confirma que não há regressão nem consumo de bateria.

### 🔶 1.5 `Win32PrioritySeparation`

- **✅ já corrigido e provado** (38 → 38 ao desligar). Manter o teste de regressão; não há
  trabalho novo, só não quebrar.

---

## 2. O que está **inerte** e precisa de decisão

### ⛔ 2.1 I/O priority "High" nos perfis V1–V4

- **Provado:** `hint=3` devolve `0xC0000061`; `hint=0` e `hint=2` passam. Elevado ou não,
  é igual.
- **Falta decidir:** os perfis anunciam "I/O: High (3)". Opções:
  1. mudar o default para Normal (2) — honesto, e é o que o Windows permite;
  2. deixar como está e manter o aviso no log.
- **Recomendação:** **opção 1.** Um perfil que pede algo que o Windows recusa é código morto
  com efeito nulo.

### 🔶 2.2 Perfis V1/V2/V3 — nunca foram medidos um a um

Só o **V4** e o **Automático** têm harness dedicado (7/7 e 25/25).
- **Falta:** harness para V1, V2, V3, com o mesmo critério — aplicar, verificar cada knob,
  reverter, confirmar que **nada fica preso**.
- **Passa se:** os três engines passam a mesma bateria que o V4.

---

## 3. Fiabilidade — o que um motor "fechado" tem de ter

### ❌ 3.1 Soak test (ligar/desligar durante horas)

- **Falta:** o teste mais importante que falta. Alternar o motor milhares de vezes.
- **Verificar:** nenhum processo fica preso em prioridade alterada; `Win32PrioritySeparation`
  volta sempre a 38; nenhum `boost_rescue.txt` fica pendurado; o log não cresce sem limite.
- **Passa se:** 10.000 ciclos sem uma única fuga.

### 🔶 3.2 Ficheiro de resgate em crash

- **✅ provado** (`gbcrash`: 4/4 verificações, com `TerminateProcess` real).
- **Falta:** testar **outros dois cenários de morte abrupta** — power loss simulado (matar
  o processo durante a escrita atómica do ficheiro) e OOM/terminate durante `ApplyBoostAuto`.
- **Passa se:** o ficheiro nunca fica truncado (a escrita tmp+move tem de ser provada sob
  morte a meio).

### ❶ 3.3 What-if: o motor corre elevado?

- **Falta:** quase todos os harnesses correram **não elevados** (o shell desta sessão é
  elevado, mas os processos filhos podem não herdar). Testar a matriz {elevado, não elevado}
  a essencialmente.
- **Porquê importa:** a recusa da I/O priority **pode** diferir, e o EcoQoS também.

### 🔶 3.4 Idempotência sob carga

- **Falta:** o motor passa por `ApplyBoostAuto` a cada evento de janela. Chamar 10.000 vezes
  seguidas no mesmo PID e confirmar que não há degradação nem fuga de handles.
- **Como:** contar handles do processo antes/depois (`Process.HandleCount`).

---

## 4. Correção e decisões pendentes

### 🔶 4.1 Corrigir comentários que mentem

- O comentário junto ao GameClassInfo afirma um ganho que **pode não existir**.
- Corrigir **depois** de medir 1.1, não antes.

### 🔶 4.2 O "Maximum" de page priority no perfil

A escala oficial é 1..5 com **5 = NORMAL** e não existe "maximum". Já foi corrigido em
4 sítios — confirmar que **não sobrou nenhum** `ComboBox.SelectedIndex` a ser usado
directamente como valor.

### ❌ 4.3 Guardian de regressão no CI

- **Falta:** os harnesses vivem em `%TEMP%` e morrem com a sessão. Deviam ir para `tests/`
  (como o `gbbench`), com um runner.
- **Passa se:** `dotnet test` (ou script) corre a bateria sem intervenção manual.

---

## 5. O que **não** é preciso fazer (para não perder tempo)

Registado para não voltar a investigar:

- **I/O priority via API documentada:** não existe. `SetProcessInformation` só aceita 0, 1 e 4.
- **Guardar handle inválido:** `GetProcessId` e `GetExitCodeProcess` **mentem** com
  `0xFFFFFFFF`. Não tentar outra vez — a sonda é feita contra o próprio processo.
- **AProcessLasso mexer em I/O:** não mexe. O Bitsum só usa `SetPriorityClass` + EcoQoS.

---

## 6. Ordem sugerida

1. **1.1 GameClassInfo** — barato, e o comentário do código depende dele.
2. **1.4 Timer + 1.2 ThreadEfficiency** — os dois knobs com maior risco de ficar *ligados*.
3. **3.1 Soak test** — é o que dá a confiança que falta.
4. **2.1 Decidir o I/O** — é uma linha, mas é uma decisão de produto.
5. **2.2 Harnesses V1/V2/V3** — fecha o paridade entre engines.
6. **4.3 Mover os testes para o repositório**.

Os itens 1.1, 1.2 e 1.4 são os que podem mudar o que o motor **faz**. O resto é robustez.