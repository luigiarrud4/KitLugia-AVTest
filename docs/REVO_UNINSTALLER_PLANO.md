﻿﻿# Revo Uninstaller — Plano de análise restante

> Complementa [REVO_UNINSTALLER_ANALYSIS.md](REVO_UNINSTALLER_ANALYSIS.md).
> Data: 05/10/2026. Estado: **Fase 0 concluída**; Fase 1/2/3 por fazer (§3 ao §7).

## Objetivo

Responder com evidência de código (não de strings) o que o Revo Uninstaller realmente
faz, para fechar a comparação com o PC Manager e decidir o que vale implementar no
`KitLugia.Core/DeepUninstaller.cs`.

## Regra de método (o erro a não repetir)

**Nunca confiar em substring solta no binário.** O processo obrigatório é:

1. Parser de PE → calcular `EA = imagebase + (off - sec.ptr + sec.va)` dos *section headers*.
2. No IDA: `idautils.XrefsTo(EA)` → confirmar que há referência de **código**.
3. `ida_hexrays.decompile()` da função chamadora.
4. Se (2) der 0 xrefs, marcar 🟡 INFERIDO — **não** concluir que existe.

Filtros por substring já produziram 4 conclusões erradas em sessões anteriores
(ver §8 da análise). Ferramentas prontas em `%TEMP%\opencode\revo2\`:
`peinfo.py`, `gen_script.py`, `scan5.py`, `scan6.py`, `scan7.py`.

---

## Fase 0 — Fundações (✅ concluída)

| Tarefa | Estado |
|---|---|
| Imports reais por PE parser (879 / 712 imports) | ✅ |
| Secções e imagebase | ✅ |
| EAs determinísticos para nomes de API | ✅ |
| Mapear wrappers de TxF (e refutar o mito) | ✅ |
| `SHFileOperationW` + flags `DelToBin` | ✅ |
| Take-ownership de chaves de registo | ✅ |
| Dispatcher de CLI (`/leftovers`, `KeepFiles`) | ✅ |

## Fase 1 — Mecanismo de limpeza (pendente)

Alvos prioritários, por impacto no nosso motor:

### 1.1 Scanner de Registry Classes — `sub_14018D6C0` (~9 KB) 🔴 **prioritário**
A maior diferença entre o Revo e nós: ele varre `SOFTWARE\Classes\*` completo
(`CLSID`, `Interface`, `TypeLib`, `AppID`, `Mime`, `ActivatableClasses`, + `WOW6432Node`)
à procura de referências ao diretório do app desinstalado.
**Pergunta a responder:** dá falso positivo? Como limita a profundidade?
Custo de performance? O nosso `KeyHasValueReferencing` (`DeepUninstaller.cs:3395`) só
segue valores — nunca chaves filhas.

### 1.2 Marcadores `ADCU`/`ADAU` 🔴 **prioritário**
0 xrefs de código confirmadas. **Perguntas:** quem escreve (instalação? primeiro scan?),
formato do conteúdo (só o path? lista de paths?), e como o pós-scan os consome.
Se for só "o path de AppData do app", é barato de portar e vale a pena.

### 1.3 `RegExclude` / `Junk Files\Exclude` / `\Include` 🟡
`Uninstaller\RegExclude` tem 0 xrefs (`CDlgAddTracedRegExclude` é só RTTI de classe MFC).
**Pergunta:** exclusão por app guardada como chave, valor, ou lista? É o `ResidualRule`
do PC Manager reinventado.

### 1.4 Blocklist de caminhos do helper — `sub_14000E6E0` (análise anterior) 🟡
Blocklist de **8 caminhos exactos**: `%UserProfile%`, `%ProgramFiles%`,
`%ProgramFiles(x86)%`, `%SystemRoot%`, `%AppData%`, `%LocalAppdata%`,
`...\VS Revo Group\Revo Uninstaller Pro\`, `RevoUnin.exe`. Aloca 256, insere, ordena.
**Pergunta:** a comparação é exacta ou por prefixo? Se for exacta, é a mesma lacuna
que tínhamos antes do `IsDirectlyInsideCriticalRoot`.

## Fase 2 — Persistência e contexto (pendente)

### 2.1 Esquema SQLite 🟡
O Revo linka `sqlite3` (`sqlite3_trace_v2` confirmado por `SCAN %S`).
**Perguntas:** que tabelas? Guarda resultados de scan? Já teria um "orphan index"
(estado anterior guardado) — se sim, é essencialmente o `captureBaseline` que dissemos
que o Revo não faz, e vale a pena nuancear o documento.

### 2.2 `RevoProcessDetector.sys` (32 KB, driver) 🔴
O helper corre `sc stop revoprocessdetector` + `FilterUnload` ao fechar
(`sub_14001B710`). **Perguntas:** o driver vigia que processos bloqueiam a remoção?
É o mecanismo de "esperar pelo processo X"? Analisar no IDA é rápido (32 KB).

### 2.3 Log estruturado 🟡
`sub_14004C9A0(criticidade, __FILE__, __LINE__, formato, ...)` — já tem caminhos
decompilados (`BasicGUIControls.cpp:249`, `NotificationDlgs.cpp:214`).
**Pergunta:** formato do ficheiro de log? É sempre escrevível? Nós só temos log simples.

## Fase 3 — Base de comparação (pendente)

### 3.1 Corrigir afirmações sobre "ponto de restauro" 🔴
Não encontrei `SRSetRestorePointA/W`, `SystemRestore`, `root\default`, `vssadmin` ou `wmic`
em nenhum binário. **A confirmar:** a UI tem a opção e o código está lá (via COM
`ISystemRestore` sem strings reconhecíveis) ou está morta nesta build?
**Não dizer ao utilizador que o Revo cria ponto de restauro sem isto confirmado.**

### 3.2 Validação dinâmica (só com autorização explícita)
Comparar lado a lado no mesmo PC: instalar uma app, desinstalar no Revo, depois no
KitLugia, e comparar listas. **Destrutivo — só com pedido explícito do utilizador.**

---

## 4. Tabela rápida de endereços (RevoUnin.exe)

| Endereço | Tamanho | Papel | Confiança |
|---|---|---|---|
| `sub_140178EB0` | 5012 | Dispatcher CLI | ✅ |
| `sub_1401D11C8` | 122 | Wrapper `DeleteFileTransactedW` | ✅ |
| `sub_1401D10B8` | 269 | Wrapper `CreateFileTransactedW` | ✅ |
| `sub_1401D1448` | 160 | Wrapper `MoveFileTransactedW` | ✅ |
| `sub_1401D248C` | 169 | Wrapper `FindFirstFileTransactedW` | ✅ |
| `sub_1401E020C` | 164 | Wrapper `GetFileAttributesTransactedW` | ✅ |
| `sub_1401DFC48` | 325 | Wrapper `RegCreateKeyTransactedW` | ✅ |
| `sub_1401DFD90` | 205 | Wrapper `RegOpenKeyTransactedW` | ✅ |
| `sub_1401E0C20` | 161 | Wrapper `RegDeleteKeyTransactedW` | ✅ |
| `sub_14003BA80` | 767 | Apagar item — `SHFileOperationW` + `DelToBin` | ✅ |
| `sub_14003C220` | 1187 | Apagar item (variante) | ✅ |
| `sub_14003CDA0` | 851 | Junk Files — `Delete to bin` | ✅ |
| `sub_1401614B0` | 919 | Take-ownership de chave de registo | ✅ |
| `sub_140161B60` | — | Ativar privilégio (SE_RESTORE/SE_BACKUP) | 🟡 |
| `sub_14017F960` / `sub_14017FB20` | — | Ler/escrever DWORD de config | ✅ (assinatura) |
| `sub_140163020` | — | `SHFileOperationW` wrapper | ✅ (chamada) |
| `sub_14018D6C0` | ~9000 | Scanner de Registry Classes | 🟡 |
| `sub_14001ACAAC` | — | Singleton de recursos/strings | ✅ (chamada) |

## 5. Tabela rápida de endereços (RevoUninHelper.exe)

| Endereço | Tamanho | Papel | Confiança |
|---|---|---|---|
| `sub_1400AD244` | 248 | Wrapper `CreateFileTransactedW` | ✅ |
| `sub_1400AD33C` | 105 | Wrapper `DeleteFileTransactedW` | ✅ |
| `sub_1400AD598` | 143 | Wrapper `MoveFileTransactedW` | ✅ |
| `sub_1400ADF78` | 146 | Wrapper `GetFileAttributesTransactedW` | ✅ |
| `sub_1400BA33C` | 314 | Wrapper `RegCreateKeyTransactedW` | ✅ |
| `sub_1400BA478` | 144 | Wrapper `RegDeleteKeyTransactedW` | ✅ |
| `sub_1400BA508` | 187 | Wrapper `RegOpenKeyTransactedW` | ✅ |
| `sub_14000EA30` | 454 | Arranque (init thread-local) | ✅ |
| `sub_14000E6E0` | — | Blocklist de caminhos | ✅ (sessão anterior) |
| `sub_14000A350` | 280 | Limpeza de ficheiro temporário + log | ✅ |
| `sub_14001B710` | 234 | Destructor: `TerminateThread` + `sc stop revoprocessdetector` | ✅ |
| `sub_14001DED0` | 469 | Apaga `onrestart.dat` + log | ✅ |
| `sub_14003AD80` | 832 | Special folders (`Programs`/`Startup`/`Favorites`) | ✅ |
| `sub_14004C9A0` | — | Log estruturado (`__FILE__`, `__LINE__`) | ✅ |
| `sub_14002E4E0` | 2588 | Rotina grande (por identificar) | 🟡 |

## 6. Decisões pendentes para o KitLugia

| # | Proposta | Custo | Benefício | Recomendação |
|---|---|---|---|---|
| 1 | Apagar para **lixeira** via `SHFileOperationW` FOF_ALLOWUNDO | ~30 linhas | Utilizador recupera enganos | ✅ **fazer** |
| 2 | Log com origem (`ficheiro:linha`) | médio | Diagnóstico de "não apagou" | 🟡 opcional |
| 3 | Marcadores `ADCU`/`ADAU` por app | médio | Alvos AppData precisos sem varrer tudo | 🟡 após confirmar 1.2 |
| 4 | Scanner de `Classes\*` | alto | Apanha mais resíduos | ❌ caro e ruidoso para o nosso perfil |
| 5 | Take-ownership de registo | ~80 linhas | Limpa chaves órfãs | ❌ **não**: agressivo, e o nosso guard já é mais conservador |
| 6 | TxF | — | — | ❌ **nunca**: código morto no Revo + obsoleto no Win11 24H2+ |

---

## 7. Registo de problemas IDA (para não perder tempo)

| Sintoma | Causa | Solução |
|---|---|---|
| `ModuleNotFoundError: ida_strings` | Removido no IDA 9 | Não importar |
| `IDA could not open the database: access denied` | Base `.id0/.id1` obsoleta | Apagar antes de reanalisar |
| Crash `0xC0000005` ao abrir | Base parcial de run anterior | `rm -f *.id0 *.id1 *.id2 *.nam *.til` |
| `idautils.Entries()` devolve 2-tuples no IDA 9 | API mudou | Iterar por `ord` e `name`, guard `kind` |
| `ida_idaapi.cvar_t` não existe | API removida no 9 | Usar `ida_search.find_first(pat, 0, <cvar>)` — **mas** também falhou; a via fiável é `ida_bytes.get_bytes(seg)` + `.find()` em Python |
| `ida_segment.get_segm_end` não existe | API removida | `ida_segment.getseg(ea).end_ea` |
| Script termina com exit=1 sem erro | Exceção engolida | Envolver tudo em `try/except` + `traceback.format_exc()` para ficheiro |
| `find_all` devolve "AUSENTE" para tudo | `cvar_t` em falta | Confirmei com parser de PE: os EAs **existem** |

> **Nota de execução:** `-S` precisa de caminho explícito; o output do script tem de ir
> para ficheiro com escrita **progressiva** (`flush()` a cada linha), senão perde-se tudo
> se o IDA crashing a meio. `nohup idat.exe ... &` sobrevive ao fim da chamada do shell.
