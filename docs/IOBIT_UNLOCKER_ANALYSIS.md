# IObit Unlocker x Force Stop — o que o IObit tem que o Kit nao tem

Analise com **IDA Pro 9.0** (`idat.exe -A -S`) dos 4 binarios em
`C:\Program Files (x86)\IObit\IObit Unlocker`. Originais intocados; copias de
analise em `%TEMP%\opencode\iobit\`.

| Binario | Arq | Tamanho | Build | Observacao |
|---|---|---|---|---|
| `IObitUnlocker.exe` | x86 PE32 | 2.636 KB | 2022-08-17 | **Nao e .NET** (sem CLI header; tem RichEdit) — nativo |
| `IObitUnlocker.dll` | x86 PE32 | 80 KB | 2022-08-17 | — |
| `IObitUnlockerExtension.dll` | x64 PE32+ | 109 KB | 2020-10-10 | shell extension (menu de contexto) |
| **`IObitUnlocker.sys`** | **x64 PE32+** | **52 KB** | **2026-04-27** | **DRIVER DE KERNEL** |

PDB vazado pelo binario confirma a origem:
`D:\worker\HUOQI\ZhangHaitaoSVN\Project\Driver\WDK10\IObitUnlocker\x64\Release\IObitUnlocker.pdb`

## Resposta curta

O que o IObit Unlocker tem e o Kit **nao tem** e que o Force Stop **nao pode
replicar em user-mode**: **um driver de kernel**. Ele expoe 4 operacoes por IOCTL e faz
a enumeração de handles e a reescrita de permissao **dentro do kernel**. O Kit,
por decisao de seguranca documentada, nao instala driver de kernel.

---

## 1. O driver: as 4 operacoes (IOCTLs)

Strings de debug do handler de IRP revelam o contrato:

```
ULKDeleteFile: 0x%08x, %wZ
ULKDeleteDirectoryItSelf: 0x%08x, %wZ
ULKRenameFile: 0x%08x
ULKRenameDirectory: 0x%08x
```

Handler `ULKDeleteFile` decompilado (`sub_140004EA8`):

1. `ZwCreateFile` com `DesiredAccess = 0x80` (**DELETE**) — o arquivo e aberto
   direto no kernel, sem passar pela checagem de ACL do usuario.
2. `ObReferenceObjectByHandle(Handle, DELETE, IoFileObjectType, ...)` → obtem o
   `FILE_OBJECT`.
3. `IoGetRelatedDeviceObject(FileObject)` → o device object do **volume**.
4. **Monta um IRP sintetico** com `IoAllocateIrp`:
   - `MajorFunction = 6` (`IRP_MJ_SET_INFORMATION`)
   - `Parameters.Read.Length = 1`
   - `Parameters.Create.Options = 13` = `DELETE | FORCE_IMAGE_SECTION_CHECK | ON_CLOSE`
   - `CompletionRoutine` + `KeInitializeEvent`/`KeWaitForSingleObject` para sincronizar
5. `IofCallDriver` injeta esse IRP **diretamente na pilha do volume**.

Destaques:

- **`ULKDeleteDirectoryItSelf`** — apaga a propria pasta (nao so o conteudo). E o que
  resolve "pasta nao vazia", porque a remocao fica pendente ate o ultimo handle fechar.
- **`FORCE_IMAGE_SECTION_CHECK`** permite apagar um arquivo carregado como imagem
  (EXE/DLL mapeado) — exatamente o caso "o programa esta rodando".
- **Renomear e operacao de primeira classe** (`ULKRenameFile`, `ULKRenameDirectory`),
  nao um fallback.

## 2. Enumeração de handles NO KERNEL

`sub_140002354` e `sub_14000273C` (os dois "quem tem este arquivo aberto"):

```c
ZwQuerySystemInformation(SystemHandleInformation, buf, 0x2000, &ReturnLength);  // classe 16
ZwQuerySystemInformation(SystemProcessInformation, buf, 0x2000, &ReturnLength); // classe 5
PsLookupProcessByProcessId(pid, &Process);
KeStackAttachProcess(Process, &ApcState);   // <-- ANEXA AO ESPAÇO DE ENDEREÇO
... (leitura da tabela de handles / PEB do processo alvo)
KeUnstackDetachProcess(ApcState);
PsGetProcessPeb(...)
```

`KeStackAttachProcess` + `KeUnstackDetachProcess` e o que o Kit **nao tem**: ele
consegue entrar no espaco de endereco de **qualquer processo, inclusive protegidos
(PPL)**, e ler a tabela de handles direto. O Kit faz isso em user-mode
(`NtQuerySystemInformation` + `NtQueryObject`) e **nao enxerga** processos
protegidos — por isso existe o fallback `handle64.exe` e por isso o Force Stop
reporta "travado" para PPL.

## 3. Reescrita de DACL no kernel

`sub_14000B2C0` -> `ZwSetSecurityObject`, `sub_14000B62C` -> `SeCaptureSecurityDescriptor`,
`sub_14000C378` -> `RtlSetDaclSecurityDescriptor`:

```c
SeCaptureSecurityDescriptor(...)                 // captura o SD do objeto
RtlInitUnicodeString(&s, L"SeConvertStringSecurityDescriptorToSecurityDescriptor");
RtlCreateSecurityDescriptor(sd, 1);
RtlAddAccessAllowedAce(...);                     // concede acesso total
RtlSetDaclSecurityDescriptor(sd, 1, dacl, 0);
WORD1(sd[0]) |= flags;                           // SE_DACL_PRESENT | ...
ZwSetSecurityObject(Handle, 0, sd);              // aplica no objeto
```

Note que `SeConvertStringSecurityDescriptorToSecurityDescriptor` **nao esta na
tabela de import** — e resolvido pelo nome (`RtlInitUnicodeString` + a string) para
esconder a API da analise estatica. O efeito: monta um SDDL e grava o DACL
**do kernel**, o que ignora a checagem de `WRITE_DAC` que barra o user-mode.

O Kit faz o equivalente user-mode (`SetNamedSecurityInfo` OWNER + DACL em
`FileTakeOwnership.SetOwnerAndDaclNative`), que funciona, mas depende de conseguir
`WRITE_DAC` — e nao alcanca o que o kernel alcanca.

O driver tambem importa `ZwCreateKey` / `ZwSetValueKey` / `ZwQueryValueKey` /
`ZwOpenKey` (registro direto do kernel) e `ZwDuplicateObject`.

## 4. Kill de processo: whitelist + caso especial `explorer.exe`

`sub_140002ED4` (o unico chamador de `ZwTerminateProcess`):

```c
ZwQueryInformationFile(...);                 // descobre o nome do executavel
wcsicmp(nome, "IObitUnlocker.exe")  &&
wcsicmp(nome, "System")             &&
wcsicmp(nome, "csrss.exe")          &&
wcsicmp(nome, "lsass.exe")          &&
wcsicmp(nome, "lsm.exe")            &&
wcsicmp(nome, "smss.exe")           &&
wcsicmp(nome, "System Idle Process")&&
wcsicmp(nome, "wininit.exe")        &&
wcsicmp(nome, "winlogon.exe")       &&
wcsicmp(nome, "userinit.exe")
   -> se TODOS forem diferentes: PODE MATAR
   -> se for explorer.exe: *flags |= 1u     <<< CASO ESPECIAL
ZwOpenProcess(pid, 512 /*PROCESS_TERMINATE*/, ...);
ZwTerminateProcess(handle, 0);
```

O IObit **mata o `explorer.exe`** e sinaliza com uma flag (usada depois para
reiniciar o shell). O Force Stop do Kit **nao tem tratamento nenhum de
`explorer.exe`** (grep vazio em `ForceStopUnlockService.cs` e `DriverUnlockService.cs`)
— ele aparece so na lista de risco que pede confirmacao.

---

## Comparativo direto

| Capacidade | IObit Unlocker | Force Stop (Kit) | Repete em user-mode? |
|---|---|---|---|
| Apagar arquivo travado | IRP sintetico `FileDisposition` no device do volume | `NtSetInformationFile(FileDispositionInformation)` | **Sim** (mesmo mecanismo, user-mode) |
| Apagar **a propria pasta** | `ULKDeleteDirectoryItSelf` | `RemoveDirectory` / recursivo | Sim |
| Apagar EXE/DLL carregado | `FORCE_IMAGE_SECTION_CHECK` |依赖 o processo fechar / reboot | Parcial |
| Renomear | IOCTL proprio, 1a classe | Metodo 5 de 6 do `RobustDeleteFile` (so como fallback ao apagar) | Sim |
| Ver handles de processo **protegido (PPL)** | `KeStackAttachProcess` + `SystemHandleInformation` | `NtQueryObject` (falha em PPL) + `handle64.exe` | **Nao** |
| Reescrever DACL de arquivo protegido | `ZwSetSecurityObject` no kernel | `SetNamedSecurityInfo` (precisa `WRITE_DAC`) | **Nao** |
| Matar processo | `ZwTerminateProcess` (kernel), whitelist + flag `explorer` | RM + `Process.Kill` + `NtTerminateProcess`; explorer so com confirmacao | Sim |
| Matar driver `.sys` | — | SCM stop/delete + `NtUnloadDriver` | — |
| Agendar no boot | — | `MoveFileEx(DELAY_UNTIL_REBOOT)` | Sim |

## Veredito

O Force Stop cobre o **campo de batalha de usuario**: Restart Manager, handles
nativos, driver `.sys` pelo SCM, 6 metodos de delecao, agendamento no boot,
ownership e DACL. O que o IObit ganha e **exclusivo do driver**:

1. enxergar handle de processo protegido;
2. gravar DACL sem `WRITE_DAC`;
3. abrir com `DELETE` ignorando ACL;
4. matar `explorer.exe` e restaurar o shell.

Adotar (1) e (2) exigiria assinar/instalar um driver — **decisao de seguranca que o
Kit ja tomou de nao hacerlo**. O item **4 (caso do `explorer.exe`) e o unico
diferencial barato e adotavel**: matar o explorer, apagar, e reiniciar o shell e
tecnica de user-mode. Nao foi implementado ainda — e a recomendacao deste doc.

## Artefatos

`%TEMP%\opencode\iobit\` — binarios copiados, `drv_v4.txt` (imports do driver),
`drv_funcs.txt` (decompilacao dos 12 handlers-chave), `drv_v5.txt` (mapa de quem
chama qual API de kernel), scripts `drv_*.py` reutilizaveis.

**Quirks do IDA 9 desta sessao** (custaram tempo): `idc.INF_IS_64BIT`,
`ida_nalt.get_name_ea_simple` e `idautils.EnumerateImports` **nao existem**; o
`ida_entry.get_entry_ordinal` retorna `int` e nao tupla; e sem `ida_pro.qexit(0)`
o `idat.exe` **fica pendurado depois do script** (timeout de 15 min) mesmo com o
script terminando em 7 s. Compativel: `ida_ida.inf_is_64bit()`, `ida_hexrays.decompile()`,
nomes via `idc.get_name()` no segmento `.idata`, enderecos de import ja conhecidos
para `idautils.XrefsTo()`.

---

## Adenda 08/10 — "por que nao perguntar as DLLs do proprio Windows?"

Ideia levantada pelo usuario: o Windows ja tem ntdll e o kernel; por que escrever um
driver quando da para **mandar os componentes existentes** fazerem a operacao?

### O que ja fazemos (a ideia ja esta 70% aplicada)

`ntdll.dll` **e** a camada de syscalls do Windows em user-mode. Tudo que o Kit faz de
"avancado" passa por la:

| Chamada | Onde no Kit |
|---|---|
| `NtQuerySystemInformation(SystemHandleInformation)` | `ForceStopUnlockService` (scan de handles) |
| `NtQueryObject` | `ForceStopUnlockService` (nome do handle) |
| `NtSetInformationFile(FileDispositionInformation)` | metodo 4 do `RobustDeleteFile` |
| `NtTerminateProcess` | fallback do kill em cascata |
| `NtSetInformationProcess` (classes 33/39) | prioridade de I/O e page no motor |
| `NtQueryDirectoryFile`, `ZwQuerySystemInformation` classe 8/23 | LatencyMonitor, TaskManager |

Ou seja: **o "IOCTL sintetico" do IObit e, em user-mode, o `NtSetInformationFile` que o
Kit ja usa**. O mecanismo e o mesmo.

### Por que o ntdll NAO alcanca o que o driver alcanca

O stub `Zw*` do ntdll chama **o mesmo syscall** que o driver chama. A diferenca nao e
a API: e o **contexto de seguranca**. O kernel aplica as checagens de acesso contra o
**token do chamador**. Mesmo syscall, credenciais diferentes — e nao existe "peca para o
ntdll fazer isso com mais privilegio".

Confirmado na pesquisa (tiraniddo, *The Art of Becoming TrustedInstaller*):
> "The Debug privilege only bypasses the security checks on Process and Thread objects,
> it doesn't do anything on Tokens."

### A versao que FUNCIONA da ideia: emprestar o token de um processo privilegiado

Existe sim — e e exatamente o que **NSudo** (2.2k stars), **AdvancedRun**,
**RunAsTrustedInstaller** e **CMDT** fazem. A ideia fica assim:

1. `SeDebugPrivilege` habilitada;
2. abrir uma thread de `TrustedInstaller.exe` (ou de `winlogon.exe`, que e SYSTEM);
3. `NtImpersonateThread` nessa thread — a thread passa a ter o **SID de servico
   `NT SERVICE\TrustedInstaller`** habilitado;
4. abrir/apagar o arquivo: os ACLs que concedem acesso ao TI passam sem reescrita de DACL
   e sem tomar ownership.

**Limites honestos (por que NAO foi implementado no Kit):**

- **Exige Administrador.** Nao escala um usuario comum e nao burla UAC. O nosso pipeline
  ja pede 1 UAC, entao nao We'd gain elevation — so *quem* faz a operacao.
- **O token do TI concede so `TOKEN_QUERY` a Administrators.** Por isso da para
  *impersonar* (`NtImpersonateThread`, que so precisa `THREAD_DIRECT_IMPERSONATION`, que o
  SeDebug concede) mas nao da para `DuplicateTokenEx` direto.
- **A impersonacao e POR THREAD e fragil.** Num app WPF com `async/await` e thread pool,
  o contexto pode vazar para codigo nao relacionado ou se perder no meio da operacao
  (o proprio autor avisa: *"as long as something else doesn't set the main thread's
  impersonation token and you don't do anything on a separate thread"*). Num app com
  dezenas de threads isso e receita de bug intermitente.
- **O token do TI e recriado quando o servico reinicia** (o Windows Update reinicia
  constantemente) — nao e um estado estavel.
- **Nao compra handle de PPL.** Isso exige `KeStackAttachProcess`, que e driver.
- **E a mesma tecnica que malware usa** para escalar privilegio; EDR/AV sinalizam.

### Veredito

A ideia e **tecnicamente correta** e bem nomeada — e o "caminho do meio" entre
user-mode e driver. Mas para o Force Stop ela nao troca nada: o ganho unico seria
apagar arquivo do `TrustedInstaller` sem reescrever DACL, e **isso o Kit ja faz** com
`SetOwnerAndDaclNative` (testado de verdade no `C:\Windows.old`, 121/121 itens). O
custo (impersonacao por thread em app async, instabilidade, depuracao) e maior que o
beneficio. Fica documentado como opcao futura se algum dia precisarmos deletar algo que
o ownership nao alcanca.

## Adenda 08/10 — Implementado: caso especial do explorer.exe

O unico diferencial do IObit **adotavel em user-mode** foi implementado:

- `ForceStopUnlockService.GetExplorerShellPid()` — PID do shell via `GetShellWindow()`
  (nao "o primeiro explorer.exe": existe explorer avulso de atalho/outra sessao).
- `IsExplorerShell(pid)` — distingue o shell de um explorer.exe avulso.
- `RestartExplorerShell()` — encerra e relanca, com **3 tentativas** e verificacao de que
  o shell **voltou** (PID diferente); se nao voltar, lanca de novo e reporta com
  instrucao de recuperacao (Ctrl+Shift+Esc > Reiniciar).
- Fase nova **4.5**, entre "fechar handles" e "deletar": so roda se o bloqueador for o
  shell da sessao. Remove as entradas do explorer do lista (o shell morreu, os handles
  sumiram) e segue para a delecao.
- `BlockingProcessInfo.IsShellBlocker` marca o culpado; `UnlockResult.ExplorerRestarted`
  informa o resultado; a UI **pede confirmacao** explicando o que vai acontecer.
- Deliberadamente **nao** agenda remocao no boot (reiniciar shell nao deleta nada) e
  **nao** usa o metodo "gentil" (WM_CLOSE), que no Win11 nao encerra o explorer.

Seguranca (pesquisado 08/10/2026): e a acao que o proprio Windows oferece — o Gerenciador
de Tarefas tem "Reiniciar" para Windows Explorer, e o equivalente por linha de comando e
`taskkill /f /im explorer.exe` + `start explorer.exe`.barra de tarefas, area de trabalho
e icones somem por 1-2 s e voltam; **janelas abertas do Explorador de Arquivos
fecham**; os outros programas nao sao afetados; nao e reboot e nao mexe em driver,
servico ou politica. O unico risco real (shell nao volta) tem fallback e aviso.

Harness: `tests/ExplorerShellTest` — diagnostico por padrao (nada de invasivo) e
reinicio real so com `--restart`. Validado no host: shell detectado (PID 6124, sessao 1,
246 MB), detector nao confunde avulso, API e fase 4.5 presentes, **13 checks OK**.
