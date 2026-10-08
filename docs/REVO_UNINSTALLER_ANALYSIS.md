﻿﻿# Revo Uninstaller — Análise Binária (o que ele REALMENTE faz)

> **Data:** 05/10/2026 · **Ferramentas:** IDA Pro 9.0 (`idat.exe`, batch, sem PDB),
> parser de PE próprio em Python 3.11.
> **Alvos:** `RevoUnin.exe` (22.099.240 B), `RevoUninHelper.exe` (4.031.240 B),
> `RevoSrp.exe`, `WinAppsDll.dll`, `RevoProcessDetector.sys`.
> **Objetivo:** entender de facto o motor de limpeza do Revo para comparar com o
> Deep Uninstall do Microsoft PC Manager (`PC_MANAGER_DEEP_UNINSTALL_ANALYSIS.md`)
> e informar o `DeepUninstaller` do KitLugia.

## ⚠️ Legenda de confiança (ler antes de tudo)

Este documento está deliberadamente escrito em dois níveis. **Não confundir.**

| Nível | Significado | Como foi obtido |
|---|---|---|
| ✅ **CONFIRMADO** | Descompilado pelo IDA **e** confirmado por parsing independente da tabela de imports do PE | código real à vista |
| 🟡 **INFERIDO** | A string existe no binário mas nenhuma referência de código a ela foi localizada, ou a lógica interna não foi descompilada | só strings |
| ❌ **REFUTADO** | Afirmação de sessões/análises anteriores que a evidência desta sessão **desmentiu** | ver secção 8 |

> Erros meus de sessões anteriores (filtro de substring que casou `"adcu"` dentro de
> `Lo**adCu**rsorW`, `"clsid"` dentro de `CLSIDFromString`, e descompilação de zlib/SQLite
> como se fossem código do Revo) estão registados em [8](#8-o-que-a-sessão-anterior-errou).

---

## 1. Resumo executivo

O Revo Uninstaller **não usa snapshot pré/pós-global de arquivos** — igual ao PC Manager
e ao KitLugia. O scan de resíduos é **pós-desinstalação** e é dirigido por
**linha de comando** (`/leftovers`), por três vias de alvo:

1. **Caminhos gravados ANTES da desinstalação** em marcadores próprios
   (`ADCU` / `ADAU` = AppData Current User / All Users, por app).
2. **O caminho de instalação** do app.
3. **Scanner de Registry Classes** (`SOFTWARE\Classes\*`).

Três correções importantes a um leitor apressado:

- ✅ **CONFIRMADO:** a "lixeira" é implementada com **`SHFileOperationW`** e
  `fFlags = 1044` (0x414) quando `DelToBin=1` — ou seja
  `FOF_ALLOWUNDO | FOF_NOERRORUI | FOF_NODIRFLAGS`. Sem `DelToBin`, usa `1108` (0x454),
  que **acrescenta `FOF_RENAMEONCOLLISION`** e perde o `ALLOWUNDO`.
  Isto é a diferença real entre "apagar para a lixeira" e "apagar de vez".
- ✅ **CONFIRMADO:** o Revo **sabe escrever em chaves de registo protegidas** —
  função que faz `SetEntriesInAclW` (Everyone `GENERIC_READ`, Admins `GENERIC_ALL`) +
  `SetNamedSecurityInfoW`, com retry de privilégio quando recebe `ERROR_ACCESS_DENIED`.
- ❌ **REFUTADO:** o Revo **NÃO faz TxF (Transactional File System)**. Ver [3](#3-o-mito-do-txf-refutado-por-imports).

---

## 2. Como a análise foi feita (reproduzível)

O parser de PE próprio (`peinfo.py` / `gen_script.py`) é a fonte determinística dos
endereços: calcula `EA = imagebase + (file_offset - section_ptr + section_va)` a partir
dos *section headers*, e só depois o IDA faz os xrefs. Isto eliminou a classe de
falsos positivos vinda de procurar strings "solto no binário".

```
peinfo.py      -> lista real de imports por DLL (Import Directory Table)
gen_script.py  -> calcula EAs dos nomes de API/flags e emite o script IDA
scan5.py       -> xrefs + decompile dos chamadores (RevoUnin.exe)
scan7.py       -> idem para RevoUninHelper.exe
```

> ⚠️ **Armadilha IDA vivida nesta sessão:** procurar strings pelo `idautils.Strings()`
> só devolveu **5.004** strings num binário com **30.283** reais, e as strings de UI
> (`StopRunExe`, `Maximize uninstall wizard`, `Select leftovers by default`) dão
> zero hits com xref — porque vivem em strings de **recursos** (`.rsrc`), não em `.rdata`.
> Já as chaves de config (`DelToBin`, `RegExclude`, `Junk Files\...`) são `.rdata`
> UTF-16LE e referenciadas por código. A distinção importa: **string em `.rsrc` =
> texto de UI; string em `.rdata` com xref = lógica.**

---

## 3. O mito do TxF — REFUTADO por imports

Esta é a correção mais importante desta sessão. Numa análise anterior foi afirmado que
o Revo usava *"Windows TxF (Transactional File System) com rollback atómico"*.

**A afirmação está errada.** O que existe é uma **camada de abstração de wrappers**,
não um motor de transações.

### O que de facto existe ✅ CONFIRMADO

`RevoUnin.exe` tem, no `.rdata`, nomes resolvidos por `GetProcAddress` em runtime:

| API | EA em `RevoUnin.exe` | Wrapper IDA | Tamanho |
|---|---|---|---|
| `CreateFileTransactedW` | `0x1406BA188` | `sub_1401D10B8` | 269 B |
| `DeleteFileTransactedW` | `0x1406BA1A0` | `sub_1401D11C8` | 122 B |
| `MoveFileTransactedW` | `0x1406BA1B8` | `sub_1401D1448` | 160 B |
| `FindFirstFileTransactedW` | `0x1406BA798` | `sub_1401D248C` | 169 B |
| `GetFileAttributesTransactedW` | `0x1406BD008` | `sub_1401E020C` | 164 B |
| `RegOpenKeyTransactedW` | `0x1406BCF78` | `sub_1401DFD90` | 205 B |
| `RegCreateKeyTransactedW` | `0x1406BCF90` | `sub_1401DFC48` | 325 B |
| `RegDeleteKeyTransactedW` | `0x1406BD078` | `sub_1401E0C20` | 161 B |

O mesmo conjunto existe no `RevoUninHelper.exe` (`sub_1400AD244`, `sub_1400AD33C`,
`sub_1400AD598`, `sub_1400ADF78`, `sub_1400BA33C`, `sub_1400BA478`, `sub_1400BA508`).

### O formato dos wrappers (idêntico nos dois binários)

```c
// sub_1401D11C8 - DeleteFileTransactedW, 122 bytes, .text
int __fastcall sub_1401D11C8(__int64 a1, const WCHAR *a2)
{
  if ( *(_QWORD *)a1 ) {                                  // há handle de transação?
    HMODULE h = GetModuleHandleW(L"kernel32.dll");
    if ( h ) {
      FARPROC p = GetProcAddress(h, "DeleteFileTransactedW");
      if ( p ) return p(a2, *(_QWORD *)a1);               // passa o handle
    }
  }
  else if ( *(_DWORD *)(a1 + 8) )                          // flag "sem transação"
    return DeleteFileW(a2);                               // fallback simples
  return 0;
}
```

Ou seja, `a1` é um **contexto de operação** com layout `{ HANDLE hTrans; DWORD plainMode; ... }`.
É uma **abstração de API** que permite dois modos de operação, nada mais.

### Porque é que nunca é usada uma transação real ✅ CONFIRMADO

Varredura byte a byte de **todos** os binários da instalação:

| Procura | `RevoUnin.exe` | `RevoUninHelper.exe` | `RevoSrp.exe` | `WinAppsDll.dll` |
|---|---|---|---|---|
| `CreateTransactionW` / `CreateTransaction` | **0** | **0** | **0** | **0** |
| `CommitTransactionW` | **0** | **0** | **0** | **0** |
| `RollbackTransactionW` | **0** | **0** | **0** | **0** |
| `GetTransactionId` | **0** | **0** | **0** | **0** |
| `ktmutil` | **0** | **0** | **0** | **0** |

E a **tabela real de imports** (879 imports em 23 DLLs para o `RevoUnin.exe`;
712 em 23 DLLs para o helper) **não contém nenhuma** dessas APIs — nem por nome,
nem por ordinal, nem como delay-load.

> **Conclusão:** sem `CreateTransaction*` em lado nenhum, o `HANDLE` em `*(_QWORD*)a1`
> **nunca pode ser válido**. O ramo `*(_DWORD *)(a1 + 8)` (fallback para
> `DeleteFileW` / `MoveFileW` / `RegDeleteKeyW` / `RegOpenKeyExW`) é **o caminho real**.
> O caminho transaccionado é **código morto**.
>
> Isto é consistente com a idade do produto: o TxF foi **descontinuado no Windows 11 24H2**
> e removido do sistema de ficheiros. O Revo manteve os wrappers por compatibilidade,
> mas não tem forma de abrir uma transação.

---

## 4. A lixeira — `SHFileOperationW` (o mecanismo real) ✅ CONFIRMADO

`sub_14003BA80` (767 B) é a rotina que apaga o item selecionado. Decompilado:

```c
unsigned int DelToBin = 1;                                   // default = lixeira
if ( !sub_14017F960(L"Uninstaller\\", L"DelToBin", &DelToBin) )
    sub_14017FB20(L"Uninstaller\\", L"DelToBin", DelToBin);   // persiste se ainda não existe

for ( cada item da lista ) {
  if ( !WaitForSingleObject(qword_14093FDC0, 0) ) break;      // <-- respeita o Cancel
  if ( PathFileExistsW(path) ) {
    if ( PathIsDirectoryW(path) ) { /* vai para a fila de pastas */ }
    else {
      struct _SHFILEOPSTRUCTW FileOp;
      FileOp.hwnd = 0;
      memset(&FileOp.wFunc + 1, 0, 44);
      FileOp.wFunc = 3;                                      // FO_DELETE
      FileOp.fFlags = (DelToBin == 1) ? 1044 : 1108;         // <-- A DIFFERENÇA
      FileOp.pFrom  = pFrom;                                 // NUL-terminated, duplo NUL
      if ( sub_140163020(&FileOp) ) {                        // SHFileOperationW
        if ( !sub_140013330(&FileOp) ) {                     // aborted?
          MoveFileExW(lpExistingFileName, NULL, 4);           // MOVEFILE_DELAY_UNTIL_REBOOT
          *(DWORD*)(a1 + 752) = 1;                           // "ficou para o reboot"
        }
      }
    }
  }
}
SetEvent(qword_14093FDD0);
```

### Decodificação das flags

| `fFlags` | Hex | Flags | Significado |
|---|---|---|---|
| **1044** | `0x414` | `FOF_ALLOWUNDO` + `FOF_NOERRORUI` + `FOF_NODIRFLAGS` | apaga **para a Lixeira** |
| **1108** | `0x454` | `0x414` **+ `FOF_RENAMEONCOLLISION`** | apaga **permanente**; colisão → renomeia em vez de falhar |

Curiosidade de engenharia: `FOF_RENAMEONCOLLISION` no modo permanente **não é um bug
aparente** — sem lixeira não há para onde colidir, e o Windows faz o "rename to `.1`"
para não perder ficheiros. É uma salvaguarda, não um acidente.

O **mesmo padrão repete-se** em `sub_14003CDA0` (1187 B), que lê a chave
`Junk Files\General\Delete to bin` (note: nome diferente, chave diferente) e faz o
`SHFileOperationW` também com cancel-check por `WaitForSingleObject`.

**Para o KitLugia:** isto é o padrão mais facilmente replicável de todo o Revo.
`Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(..., RecycleOption)` faz o mesmo
em .NET; `DeleteFileW` + `FOF_ALLOWUNDO` faz o mesmo em C.

---

## 5. Forçar escrita em chaves de registo protegidas ✅ CONFIRMADO

`sub_1401614B0` (919 B) — a função mais interessante do ponto de vista de segurança.
Toma um enum de raiz de registo (`HKEY_MACHINE=-2147483646`, `HKEY_CURRENT_USER`,
`HKEY_CLASSES_ROOT`, `HKEY_USERS`) e uma sub-chave, e:

```c
// entrada 0: Everyone (S-1-1-0), grfAccessPermissions = 0x80000000
//            = GENERIC_READ, grfAccessMode = 2 (SET_ACCESS), sem herança,
//            TrusteeType = TRUSTEE_IS_WELL_KNOWN_GROUP
// entrada 1: Administrators (S-1-5-32-544), grfAccessPermissions = 0x10000000
//            = GENERIC_ALL, SET_ACCESS, grfInheritance = 3
//            (CONTAINER_INHERIT_ACE | OBJECT_INHERIT_ACE), TrusteeType = TRUSTEE_IS_GROUP
AllocateAndInitializeSid(...) x2      // SECURITY_NT_AUTHORITY: S-1-1-0 e S-1-5-32-544
SetEntriesInAclW(2, pListOfExplicitEntries, NULL, &NewAcl)
SetNamedSecurityInfoW(pObjectName, objType, 4 /*DACL_SECURITY_INFORMATION*/,
                      NULL, NULL, NewAcl, NULL)

if ( falhou com ERROR_ACCESS_DENIED (5) ) {
    OpenProcessToken(GetCurrentProcess(), 0x20 /*TOKEN_ADJUST_PRIVILEGES*/, &hTok);
    if ( !sub_140161B60(hTok) ) {
        "You must be logged on as Administrator.\n"
    }
    SetNamedSecurityInfoW(pObjectName, objType, 1 /*OWNER_SECURITY_INFORMATION*/,
                          psidOwner, NULL, NULL, NULL);
    // e volta a aplicar o DACL
    SetNamedSecurityInfoW(pObjectName, objType, 4 /*DACL_SECURITY_INFORMATION*/, ...);
}
```

> ⚠️ **Correção de rigor:** uma versão anterior deste texto dizia "Everyone →
> `GENERIC_ALL`". Os bytes dizem `0x80000000`, que é **`GENERIC_READ`**, não
> `GENERIC_ALL` (`0x10000000`). A leitura correcta é: **Everyone recebe só LEITURA**
> (o mínimo para enumerar), e quem recebe `GENERIC_ALL` com herança são os
> **Administradores**. Isto torna a operação **menos agressiva** do que parecia —
> a leitura pela Everyone já é o bastante para o scan.

Isto é **take-ownership de chaves de registo** para conseguir apagar. É legitimately
necessário para limpar chaves de apps mal-desinstaladas, mas é também o tipo de operação
que **deve ser restrita e registada**. O Revo regista o erro no log (`implog`), mas não
pede confirmação separada.

---

## 6. Dispatcher de linha de comando ✅ CONFIRMADO (código real)

`sub_140178EB0` — **5.012 bytes**, `__fastcall (CWinApp *a1)`, é o handler de
`CommandLineToArgvW`. Estrutura reconstruída:

```c
int v7 = 0;                                       // modo
for (v35 = 1; v35 < pNumArgs; ++v35, ++v36) {      // v36 = &argv[v35]
  if ( !wcsicmp(*v36, L"/update") )                v7 = 3;
  else if ( !wcsicmp(*v36, L"/leftovers") && pNumArgs > 4 )  v7 = 2;   // exige 5+ args
  else if ( !wcsicmp(*v36, L"/continue") )         v7 = 4;
  else if ( !wcsicmp(*v36, L"/chactivation") )     v7 = 5;
  else if ( !wcsicmp(*v36, L"/settings") )         v7 = 1;
  else if ( !wcsicmp(*v36, L"/updatesubscription") ) v7 = 6;
  ...
  if ( pNumArgs > 5 ) {
     // compara 10 WCHAR == 10 bytes de 'K','e','e','p','F','i','l','e','s','\0'
     if ( memcmp(argv[v35+1], aKeepfiles, 10) == 0 ) { v8 = 1; v79 = 1; }
  }
  if ( !wcsicmp(*v36, L"/hunter") )      v80 = 1;      // caça-indicator
  if ( !wcsicmp(*v36, L"/forcedfolder") ) break;        // o PRÓXIMO arg é a pasta
}
```

Confirmado byte a byte: `KeepFiles` está guardado como wide string de **9 caracteres +
NUL** (`"KeepFiles\x00\x00\x00"`), e o dispatcher compara exactamente **10 `WCHAR`** —
isto é, string + terminador. `memcmp` de 10 bytes, sem `strlen`, sem normalização.

Pelo menos 4 xrefs para as strings vêm de uma **tabela de ponteiros** (`off_1408188D0`,
terminada em `off_140818900`) usada por `wcsstr` para um pré-dispatch genérico de
comandos, em vez de uma cadeia de `strcmp`.

> `/leftovers` exige **`pNumArgs > 4`** — isto é, `exe + /leftovers + 3 alvos + KeepFiles`.
> Fica documentado o formato: `RevoUnin.exe /leftovers <arg2> <arg3> <arg4> [KeepFiles]`.

---

## 7. O que ainda NÃO está confirmado

Tudo o que segue permanece 🟡 **INFERIDO** — a string existe, a lógica interna não foi
lida. **Não usar como base de decisão.**

| Mecanismo | Evidência | Falta descompilar |
|---|---|---|
| Marcadores `ADCU`/`ADAU` | 1 ocorrência UTF-16 de cada, **0 xrefs de código** | quem escreve e quem lê |
| `RegExclude` (`Uninstaller\RegExclude`) | 0 xrefs (`CDlgAddTracedRegExclude` é só nome RTTI) | como a exclusão é aplicada |
| `Junk Files\Exclude` / `\Include` | 7+ xrefs, todos em código de UI | a lógica de filtragem |
| Scanner de `Registry Classes` | `sub_14018D6C0` (~9 KB, 51 strings CLSID/…) | **não descompilado** |
| SQLite embutido | `sqlite3_trace_v2` ligado; `SCAN %S` | esquema de tabelas |
| Cleaner de browsers (`places.sqlite`, `cookies.sqlite`) | strings presentes | limpeza real |
| Ponto de restauro do sistema | **nenhum** `SRSetRestorePointA/W`, `SystemRestore`, `root\default`, `vssadmin`, `wmic` em nenhum binário | ⚠️ ver nota |
| `RevoProcessDetector.sys` (driver) | `sc stop revoprocessdetector` confirmado no helper (`sub_14001B710`) | o que o driver faz |
| Arquivo de autoruns | strings em sessão anterior | a classificação |

> ⚠️ **Ponto de restauro — atenção.** Não encontrei API de System Restore em **nenhum**
> binário (nem ANSI, nem WIDE, nem WMI `root\default:SystemRestore`, nem `vssadmin`).
> A opção "Create System Restore Pont" da UI (nota: erro de grafia da VS, "Pont")
> pode ser: (a) implementada via COM `ISystemRestore` sem strings reconhecíveis, ou
> (b) removida nesta build. **Não afirmar ao utilizador que o Revo cria ponto de restauro.**

### Achado extra: código-fonte original exposto no helper ✅ CONFIRMADO

O `RevoUninHelper.exe` ainda embute os **caminhos de compilação originais**:

- `C:\Work\VSRevo\Windows\Projects\Revo Helper\Revo Helper\Sources\BasicGUIControls.cpp:249`
  → *"Cannot delete the image temp file. Last Error = %d"*
- `C:\Work\VSRevo\Windows\Projects\Revo Helper\Revo Helper\Sources\NotificationDlgs.cpp:214`
  → *"Cannot delete onrestart.dat, error code = %d"*

Isto confirma que o helper é um projeto MFC com **logging por `__FILE__`/`__LINE__`**
(`sub_14004C9A0(criticidade, ficheiro, linha, formato, ...)`) — ou seja, **o Revo tem um
sistema de log estruturado com origem de cada mensagem**. É mais sofisticado do que
os `MessageBox` que parece. Boa referência de UI para o nosso desinstalador.

---

## 8. O que a sessão anterior errou (registo honesto)

Para que ninguém repita estes erros:

1. **TxF / rollback atómico** — ❌ refutado por imports (§3). Era uma leitura de nomes
   de API resolvidos dinamicamente, não de uso.
2. **Filtro de substring sem âncora** — casou `"adcu"` dentro de `Lo**adCu**rsorW` e
   `"clsid"` dentro de `CLSIDFromString`. Nenhuma das descompilações iniciais era código do Revo.
3. **zlib e SQLite como código do Revo** — a comparação de strings "IDAT" e `memdb`
  vzieram de bibliotecas linkadas; descompilá-las produziu thousands de linhas sem
   qualquer relação com desinstalação.
4. **`idautils.Strings()` como fonte completa** — só 5.004 de 30.283 strings
   (recursos em `.rsrc` não entram no índice de strings C).

**Método que funciona:** parsing de PE para EAs determinísticos → xrefs no IDA →
decompile. Confirmei que `peinfo.py` e os `scan*.py` estão reproduzíveis em
`%TEMP%\opencode\revo2\`.

---

## 9. Comparação Revo × PC Manager × KitLugia (2026-10-05)

| Aspecto | PC Manager (Microsoft) | Revo Uninstaller | KitLugia (atual) |
|---|---|---|---|
| Tipo | .NET (ilspycmd) | Nativo x64 MFC + SQLite | .NET |
| Snapshot global | ❌ | ❌ | ❌ (`captureBaseline:false`) |
| Scan pós-uninstall | ✅ `ResidualRule` | ✅ CLI `/leftovers` | ✅ `ScanUwpLeftovers` |
| Alvos de ficheiros | `InstalledPath` (BFS depth 9) | InstallPath + ADCU/ADAU | — |
| AppData por app | ❌ (não varre global) | 🟡 marcadores ADCU/ADAU | ❌ |
| Registry | Só a chave `Uninstall` própria | 🟡 `Classes\*` completo | — |
| **Lixeira** | — | ✅ `SHFileOperationW` FOF_ALLOWUNDO | — |
| **Take-ownership de registo** | — | ✅ Everyone leitura + Admin `GENERIC_ALL` | ❌ (e é uma decisão de projeto) |
| **Transacção / rollback** | ❌ | ❌ **código morto** (§3) | ❌ |
| Allowlist por app | ✅ `ResidualRule` | 🟡 `RegExclude` (não confirmada) | ❌ |
| Exclusões | — | 🟡 `RegExclude`, `Junk Files\Exclude` | ❌ |
| Filtro de tempo | — | "Ignore files accessed in last 24h" (🟡 UI string) | ❌ |
| **Log estruturado** | — | ✅ `__FILE__`/`__LINE__` por mensagem | ⚠️ log simples |
| Ponto de restauro | ❌ | ⚠️ **não encontrado nesta build** | ❌ |
| Proteção de caminhos | ✅ allowlist por app | 🟡 blocklist de 8 caminhos (`RevoUninHelper`) | ✅ `IsTooBroadAsInstallRoot` + `IsDirectlyInsideCriticalRoot` |

### Leitura honesta para o KitLugia

O Revo continua a dar-nos **quatro coisas concretas** e uma lição:

1. **Lixeira via `SHFileOperationW` com `FOF_ALLOWUNDO`** — o KitLugia apaga de vez.
   É a melhoria de segurança mais óbvia (o utilizador pode recuperar). Custo: ~30 linhas.
2. **Ciclo de vida do scan em CLI separada** (`/leftovers`) — permite que o motor
   rode com privilégio no helper e a UI só desenhe. Separa política de apresentação.
3. **Log com origem (`ficheiro:linha`)** — permite diagnosticar "não apagou" sem adivinhações.
4. **Não é transaccional.** A nossa untenção de que o Revo tinha rollback atómico estava errada.
   Isto **valida a nossa decisão** de não implementar TxF (obsoleto no Win11 24H2+).

E uma **lição de segurança**: alterar owner e DACL de chaves de registo alheias é
agressivo, mesmo que a Everyone só fique com leitura. O nosso `IsDirectlyInsideCriticalRoot`
— que bloqueia qualquer item cujo pai directo seja uma raiz de sistema — é **mais
conservador** do que o Revo neste ponto específico.

---

## 10. Fontes e artefactos

**Instalação analisada (só leitura):**
`C:\Program Files\VS Revo Group\Revo Uninstaller\`

```
RevoUnin.exe          22.099.240   MFC x64, 35.882 funções, 23 DLLs, 879 imports
RevoUninHelper.exe     4.031.240   MFC x64, 23 DLLs, 712 imports
RevoSrp.exe              356.144   (System Restore Protection)
WinAppsDll.dll            38.184
RevoProcessDetector.sys   32.464   driver
```

**Análise (regenerável):** `%TEMP%\opencode\revo2\`
- `peinfo.py` — parser de Import Directory Table (a fonte determinística)
- `gen_script.py` — calcula EAs de APIs/flags e emite o script IDA
- `scan5.py` + `out5.txt` — xrefs/decompile das APIs de ficheiro e TxF (`RevoUnin.exe`)
- `scan6.py` + `out6.txt` — dispatcher de CLI, `DelToBin`, config (`RevoUnin.exe`)
- `scan7.py` + `out7.txt` — wrappers, `sc stop`, logs (`RevoUninHelper.exe`)

**Documentos relacionados:**
- [PC_MANAGER_DEEP_UNINSTALL_ANALYSIS.md](PC_MANAGER_DEEP_UNINSTALL_ANALYSIS.md)
- [REVO_UNINSTALLER_PLANO.md](REVO_UNINSTALLER_PLANO.md) — o que falta para fechar a análise
- `KitLugia.Core/DeepUninstaller.cs` — o motor a comparar
