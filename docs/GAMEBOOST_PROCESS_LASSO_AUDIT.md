# Auditoria: o motor GameBoost vs. a referência (Process Lasso / Bitsum)

**Pergunta:** o motor do KitLugia está a fazer algo de errado ou prejudicial ao sistema?
**Método:** decompilação do motor de aplicação do Process Lasso (18.4.0.48, Bitsum LLC)
com IDA Pro 9.0, e comparação linha a linha com o nosso.

> O Process Lasso é a referência porque foi escrito pela mesma empresa que documenta
> o escalonador do Windows (Bitsum, Mark Russinovich). E porque a documentação oficial
> do Bitsum é explícita sobre o que **não** fazer.

---

## 1. Como foi feito

- Binário analisado: `C:\Program Files\Process Lasso\ProcessGovernor.exe` (1,3 MB, C++ nativo)
- Ferramenta: IDA Pro 9.0 em batch (`idat.exe -A -S<script>`), `ida_auto.auto_wait()` + Hex-Rays
- Ficheiros: `ProcessGovernor.i64`, script `pl_governor.py`, saída `pl_governor_out.txt` (em `%TEMP%`)

**Nota de método:** uma primeira leitura por *strings* das importações deu `SetProcessInformation`
como "ausente". **Era falso negativo** — o IDA mostrou que a API é resolvida em tempo de
execução por `GetProcAddress`, e portanto não aparece na tabela de importações. Qualquer
conclusão baseada só em strings de importação é pouco fiável para binários nativos.

---

## 2. O que o Process Lasso faz (evidência do IDA)

### 2.1 Prioridade de CPU — teto HIGH

```c
v6 = OpenProcess(0x200u, 0, *(_DWORD *)(v4 + 7));   // 0x200 = PROCESS_SET_INFORMATION
if (v6) { SetPriorityClass(v6, v5); CloseHandle(v6); }
```

```c
SetPriorityClass(ProcessInformation.hProcess, 0x20u);   // 0x20 = HIGH_PRIORITY_CLASS
```

**Achado:** o teto usado pelo watchdog do Bitsum é **HIGH (0x20)** — nunca RealTime.
É exactamente o mesmo teto que impusemos no motor (e que valeu o bloco de `realtime`).

### 2.2 Guardar e restaurar a prioridade original — o nosso padrão, confirmado

```c
if ( PriorityClass == *(_DWORD *)(a3 + 312) )
{
    v13 = *(_DWORD *)(a3 + 308);          // <- prioridade ORIGINAL, guardada
    v14 = OpenProcess(0x200u, 0, ...);
    if ( v14 ) { SetPriorityClass(v14, v13); CloseHandle(v15); }
}
```

**Achado:** o Bitsum **guarda a prioridade original** numa estrutura e **restaura-a** quando
o processo deixa de estar na regra. É literalmente o mesmo desenho do nosso
`_originalPriorities` + `RevertBoost`. **O nosso padrão está correcto e é o de referência.**

### 2.3 EcoQoS / Power Throttling — mesma estrutura, mesmos valores

```c
v17[2] = v5 == 1;                                                   // StateMask
SetProcessInformation(v6, 4LL, v17, 12LL);                          // class 4, 12 bytes
```

`class 4` = `ProcessPowerThrottling`; `12` = `sizeof(PROCESS_POWER_THROTTLING_STATE)`
(`{Version, ControlMask, StateMask}`); e `StateMask` recebe **apenas o bit 0**
(`EXECUTION_SPEED`).

**Achado crucial:** o Bitsum **nunca toca no bit 1** (`PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION`).
Isto **valida a decisão** de não mexer nesse bit: o nosso `SetPowerThrottling` deixa-o a 0
(honrar o timer que o jogo pede), que é o que o motor de referência também faz por omissão.

### 2.4 Disciplina de handle

`OpenProcess(PROCESS_SET_INFORMATION)` → operação → `CloseHandle`, sempre.
É o mesmo que fazemos. (Foi exactamente este o erro do meu harness: devolver `Process.Handle`
depois do `using` dar um handle já fechado — erro 6.)

### 2.5 O que o Bitsum **não** faz

| API | Bitsum | KitLugia |
|---|---|---|
| `NtSetInformationProcess` (I/O priority, classe 33) | **não usa** | **usa** |
| `NtSetInformationProcess` (page priority, classe 39) | **não usa** | **usa** |
| `timeBeginPeriod` | não usa | não usa |
| Bit `IGNORE_TIMER_RESOLUTION` | não toca | não toca |

O governor do Bitsum **não mexe em I/O priority nem em page priority** — os knobs que nós
acrescentámos. Não é um erro: são knobs legítimos e dentro de valores seguros
(I/O 3 = High; page 5 = NORMAL = default). Mas são o nosso **único desvio** face à referência.

---

## 3. Veredicto

**Não estamos a fazer nada de errado nem de prejudicial.**

| Princípio | Bitsum | KitLugia | Estado |
|---|---|---|---|
| Teto de prioridade | HIGH (0x20) | HIGH (RealTime bloqueado) | ✅ igual |
| Guardar a prioridade original | sim | sim (`_originalPriorities`) | ✅ igual |
| Reverter para a original ao sair | sim | sim (`RevertBoost`) | ✅ igual |
| EcoQoS: bit de execução | bit 0 | bit 0 | ✅ igual |
| EcoQoS: bit de timer | não toca | não toca | ✅ igual |
| Abrir/fechar handle por operação | sim | sim | ✅ igual |
| Estrutura de power throttling | class 4, 12 bytes | class 4, 12 bytes | ✅ igual |
| I/O + page priority | não usa | usa (ntdll) | ⚠️ desvio nosso |

---

## 4. O desvio: I/O e page priority via ntdll

Usamos `NtSetInformationProcess(handle, 33, ...)` (I/O) e `(handle, 39, ...)` (page) — APIs
**não documentadas**, com números de classe fixos, e chamadas dentro de `ConditionalLog.Try`,
ou seja, **um falhanço é silencioso**.

Risco real: se um build futuro do Windows mudar estes números, ou se o acesso for negado, o
motor continua a "funcionar" sem dizer nada. Não é prejudicial — pior caso é um ajuste que
não é aplicado — mas é silencioso, que é o pior defeito para um motor que se diz fiável.

**Recomendação:** sondear a capacidade **uma vez** e, se a escrita não pegar, registar no log
uma vez e parar de tentar. Isto não muda o comportamento em machines normais; só transforma
uma falha silenciosa numa falha visível.

Nota: a prioridade de **memória** já está bem — usa `SetProcessInformation(ProcessMemoryPriority)`,
que é **documentada**, e foi corrigida nesta sessão (ver AGENTS.md). Para I/O não existe API
documentada no userland, pelo que a via ntdll é a prática comum (é o que o Process Hacker faz).

---

## 5. Citações do Bitsum que sustentam o desenho

> "Don't set your important processes to High or Real-Time... ProBalance works from the other
> direction (lowering priority classes) for a reason."

Isto é exactamente o que o **Automático v2** faz: HIGH no foco e **rebaixar o fundo sob carga**
(o `ApplyAutoProBalance` com gate de 70%), em vez de mexer globalmente.

## 6. Ficheiros

- `ProcessGovernor.i64` — base de dados IDA (em `%TEMP%\plwork`)
- `pl_governor.py` / `pl_governor_out.txt` — script e saída
- O binário original **não foi alterado** (só cópia para análise)
---

## 7.adição — A sondagem foi implementada, TESTADA, e corrigiu 3 coisas

O harness `gbprobe` (mede, não lê) fechou a lacuna da recomendação acima. O que encontrou
**não era o que se esperava**, e vale registar porque muda a forma como o motor se deve
comportar.

### 7.1 Não existe via documentada para I/O nem para page priority

Medido com `SetProcessInformation` (a API pública, classes 0..5), com handle válido **e**
inválido:

| classe | significado | handle válido | handle inválido |
|---|---|---|---|
| 0 `MemoryPriority` | ✅ | ok=True | ok=True |
| 1 `MemoryCompression` | ❌ err 87 | — | — |
| **2 `IoPriority`** | ❌ err 87 | — | — |
| **3 `PagePriority`** | ❌ err 87 | — | — |
| 4 `PowerThrottling` | ✅ (EcoQoS, precisa 12 bytes) | — | — |

Ou seja: `SetProcessInformation` **só aceita escrita** para MemoryPriority, MemoryCompression
e PowerThrottling. I/O e page priority **não têm via documentada** — daí as classes privadas
da ntdll (33 e 39), que é a prática comum mas não é suportada.

### 7.2 A page priority funciona mesmo (provado por leitura de volta)

Sondagem feita com `NtQueryInformationProcess` classe 39 (leitura):

```
defini 1 -> lido de volta = 1   APLICOU
defini 5 -> lido de volta = 5   APLICOU
```

Portanto o knob de page priority **é real e funciona**. Não havia prova disto antes.

### 7.3 A I/O priority: só VeryLow e Normal passam — High é recusado

Este é o achado mais importante. Medido **com o Kit a correr ELEVADO** (comprovado por
`WindowsPrincipal.IsInRole(Administrator)` dentro do próprio harness):

| hint | significado | resultado |
|---|---|---|
| 0 | VeryLow | ✅ `0x00000000` |
| 2 | Normal | ✅ `0x00000000` |
| **3** | **High** | ❌ **`0xC0000061` STATUS_PRIVILEGE_NOT_HELD** |

O mesmo acontece no processo do próprio Kit e no charmap — **não é do processo, é do valor**.
O Windows impede subir a I/O de um processo alheio acima de Normal.

**Consequência honesta:** o "I/O: High" que os motores V1–V4 anunciavam **nunca teve efeito**
nesta máquina. Não é um problema do kit nem um bug de programação — é uma restrição do
Windows que não tem documentação pública para a via que usamos. Elevar **não resolve**
(foi medido elevado).

### 7.4 A sondagem mentia — corrigido

A primeira versão da sondagem decidia "disponível/indisponível" a partir da **primeira chamada
a um processo qualquer**. Isso dá um **SIM falso**, porque a classe 39 devolve sucesso **mesmo
com handle inválido**.

Duas tentativas de guarda por handle **falharam ambas**, e é importante registar porque
qualquer pessoa vai tentar o mesmo:
- `GetProcessId(0xFFFFFFFF)` devolveu `31736` — um PID qualquer (slot reutilizado).
- `GetExitCodeProcess(0xFFFFFFFF)` devolveu `ok=True, 259` — **falso STILL_ACTIVE**.

Um handle inventado **não é detectável**. A sonda passou então a ser feita **uma vez, contra o
próprio processo do Kit** (`GetCurrentProcess`), que é um handle que nos compete de facto:
- elimina a corrida do "processo que morreu entre listar e aplicar";
- e, com o valor **real** que o motor quer aplicar (hint 3 para I/O), dá a resposta verdadeira
  em vez de um "gentil" que mentiria.

### 7.5 Como está agora

`SondaCapacidadeNtdll` corre uma vez, com o valor que o motor quer de facto:
- devolveu `0xC0000061` para I/O → marcada indisponível → log **uma vez**, e o motor fica
  no Normal. O resto do motor não é afetado;
- confirmou classe 39 → page priority continua a ser aplicada normalmente.

Testes (`gbprobe`, exit 0): estado coerente com a API em ambos os casos, **sem SIM falso**,
49 chamadas com handle inválido → **0 avisos no log**, e depois de marcada indisponível **não
volta a tentar**.

### 7.6 Nota sobre a UI

`BtnSettings_Click` deixou de anunciar "I/O: High" como se funcionasse, e passou a dizer o que
é verdade. Um painel que promete um ajuste que o Windows recusa é pior do que um que o omite.
