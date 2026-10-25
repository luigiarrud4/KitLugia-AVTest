# ISLC 1.0.4.8 — análise por múltiplos métodos (além do IDA)

> Segunda passada na análise do ISLC, feita em 17/10/2026 com **seis instrumentos
> diferentes**, para não depender de um único ponto de vista. O IDA Pro 9.0 abre
> assemblies .NET e entrega IL fiel — mas ele não mostra recursos embutidos,
> strings localizadas, tabelas de metadata, assinatura Authenticode nem o
> comportamento decidindo com números reais. Cada método abaixo cobre uma dessas
> lacunas.
>
> Binários analisados: `Intelligent standby list cleaner ISLC.exe` (1.0.4.8) e
> `ISLC.Core.dll` (1.0.0.0), em `C:\Users\Lugia\Downloads\ISLC v1.0.4.8`.
> Complementos: [`ISLC_ANALYSIS.md`](ISLC_ANALYSIS.md) (comportamento),
> [`ISLC_VS_KITLUGIA_COMPARISON.md`](ISLC_VS_KITLUGIA_COMPARISON.md) (plano) e
> [`IDA_PRO_ISLC_GUIDE.md`](IDA_PRO_ISLC_GUIDE.md) (só a parte do IDA).
>
> **Licença:** ISLC é freeware proprietário (© Wagnardsoft). Aqui ficam apenas
> fatos de comportamento, contagens e assinaturas de API. Nada de código do ISLC
> entra no repositório; os artefatos brutos (`.asm`, `.i64`, C#) ficam em `C:\temp`.

---

## 1. Qual instrumento responde qual pergunta

| # | Método | Ferramenta | Responde | **Só ele mostra** |
|---|---|---|---|---|
| A | Metadata do PE | `System.Reflection.Metadata` (`scripts/pe_metadata_dump`) | contagens, refs, recursos, P/Invoke | superfície nativa **completa** e dependências reais |
| B | IL disassembly | IDA 9.0 (`idat.exe -B -c -A`) | o que o binário executa | xrefs, endereços, prova de que o decompilador não inventou |
| C | Decompilação C# | `ilspycmd` (PDB original) | intenção do autor | nomes de variável, fluxo legível, `async`/state machine |
| D | Recursos embutidos | `pe_metadata_dump res` | BAML, ícones, **textos localizados** | semântica oficial de cada opção (textos do próprio autor) |
| E | String heap + pacote | `pe_metadata_dump strings` + `ReadMe.txt`/`config.json` | URLs, argumentos, defaults | endpoints de rede, CLI documentada |
| F | Execução/medição | `scripts/islc_gate_probe.ps1` | se a decisão realmente dispara | taxa real de disparo nesta máquina (20/20) |

O erro a evitar: tratar o `.asm` do IDA como "a verdade completa". Ele não
contém strings de `.resources` (nem os 72 textos de UI), não lista dependências
não usadas, e não diz **com que frequência** a condição é verdadeira.

---

## 2. Inventário (método A + assinatura)

### 2.1 Identidade e build

| | `Intelligent standby list cleaner ISLC.exe` | `ISLC.Core.dll` |
|---|---|---|
| Tamanho | 1.189.136 B | 49.424 B |
| Assembly | `Intelligent standby list cleaner ISLC` **1.0.4.8** | `ISLC.Core` **1.0.0.0** |
| MVID | `5e71bb6c-95b0-4a9f-b074-c149bfbc5b17` | `a127371b-00fd-4dc8-9221-7205bb255424` |
| Machine / subsystem | `I386` / `WindowsGui` | `I386` / `WindowsCui` |
| `CorFlags` | `ILOnly` | `ILOnly` |
| Timestamp do PE | 1912-02-28 | 1949-03-26 |
| SHA-256 | `11e4ed0b…3322f` | `11dc5458…590ac` |
| Assinatura | **Valid** — `CN=Wagnardsoft, O=Wagnardsoft, S=Quebec, C=CA`, expira 2027-08-13 | idem (mesmo publisher) |

Dois detalhes que importam:

- **`ILOnly`** descarta packing/compressor: não há camada nativa escondida, então
  não faz sentido caçar anti-debug em x86. O binário é IL puro + runtime .NET 4.8.
- **Timestamp de 1912/1949** é o marcador de *build determinístico* (Roslyn
  normaliza o timestamp). Não é data de compilação real — quem datar o binário
  pelo PE timestamp vai errar por um século.

### 2.2 Contagens de metadata (fonte independente do IDA)

| Medida | exe | Core |
|---|---|---|
| `TypeDefs` | 44 (43 reais + `<Module>`) | 32 |
| `MethodDefs` | **388** | 145 |
| `FieldDefs` | 217 | 287 |
| `MemberRefs` | 353 | 179 |
| `AssemblyRefs` | 11 | 6 |
| `ModuleRefs` | 3 | 5 |
| `TypeRefs` | 207 | 99 |
| Recursos de manifesto | 4 | 0 |
| `CustomAttributes` | 252 | 108 |

**Cruzamento com o IDA:** o `.asm` do IDA tem **388** `.method` e **43** `.class`
— exatamente os mesmos números, obtidos por dois caminhos que não compartilham
código. Se algum dos dois estivesse truncando (por exemplo, pulando métodos
`private` ou tipos aninhados), os números divergiriam.

### 2.3 Dependências reais

`ISLC.Core.dll` referencia: `mscorlib`, `System`, `System.Core`,
**`System.Management`** (WMI), **`System.Net.Http`** (rede) e
**`Newtonsoft.Json 13.0.0.0`**.

`System.Net.Http` num "limpador de standby list" é o tipo de sinal que só a
tabela de refs revela — e foi o que levou ao achado da seção 5.1. O
`Newtonsoft.Json` está **embutido como recurso** no exe (ver 4.1), então o app
funciona sem DLLs ao lado.

---

## 3. Método B+C: o mesmo método por dois decompiladores

`PurgeLogic` (o predicado de purge), comparado lado a lado — **três** ferramentas:

| | IDA 9.0 (`.asm`) | ILSpy (`ilspycmd --ilcode`) | `dotnet-ildasm` (Mono.Cecil) |
|---|---|---|---|
| Endereço | RVA `0x4970` | RVA `0x4970` | RVA `0x4970` |
| Tamanho do código | 128 B (`0x80`) | 128 B (`0x80`) | 128 B (`0x80`) |
| Instruções | **46** | **46** | **46** |
| Sequência de opcodes | igual | igual | igual (`diff` vazio) |

O `diff` opcode-a-opcode entre o ILSpy e o `ildasm` sai **vazio** (46 linhas nos dois,
mesma ordem); no IDA a única diferença era de recorte do meu `awk` (o `ret` final
ficava fora da janela), não do binário.

> **Pitfall de contagem (corrigido aqui):** a primeira versão desta tabela dizia
> "40 instruções" — artefato de um regex de rótulo com hex **maiúsculo**
> (`IL_[0-9A-F]{4}`) num listing que usa **minúsculo** (`IL_000c`). Rótulos com
> `a`–`f` não casavam e sumiam da contagem. O número certo é **46** nos três
> instrumentos (e o tamanho de 128 B fecha a conta: último offset `IL_007f`).

Trecho nos dois (o predicado, sem os `try/catch`):

```text
ldloc.0                          // flag = !IsAppsExecuting
brfalse.s  loc_2D2B
call       get_GetSystemCacheValue        // standby MB
ldarg.1
unbox.any  [mscorlib]System.UInt64
blt.un.s   loc_2D2B                       // standby < limiar → sai
call       get_AvailableMemory            // = Zero + Free (não "Available"!)
call       AppSettings::get_FreeMemoryValue
call       [mscorlib]System.UInt64::Parse
bge.un.s   loc_2D2B                       // free >= limiar → sai
call       get_PurgeCommand
callvirt   ICommand::Execute
```

Nada de cooldown, histerese ou verificação pós-purge. E o nome `AvailableMemory`
é **enganoso**: a propriedade recebe `ZeroPageCount + FreePageCount` (seção 6 do
`ISLC_ANALYSIS.md`), ou seja "Livre", não "Disponível". Esse detalhe de nome é
exatamente o que faz alguém concluir errado que o ISLC compara com a RAM
disponível do Gerenciador de Tarefas.

---

## 4. Método D: recursos embutidos (o que o IDA não vê)

### 4.1 Os 4 recursos de manifesto do exe

| Recurso | O que é |
|---|---|
| `Intelligent standby list cleaner ISLC.g.resources` | BAML (UI compilada) |
| `...Properties.Resources.resources` | ícone da bandeja, PNG do Discord, GIF de doação |
| `...Properties.Strings.resources` | **72 textos de UI** (neutro) |
| `...Resources.Newtonsoft.Json.dll` | dependência embutida e resolvida em runtime |

Dentro do `.g.resources` (nomes de stream):

```text
app.baml                      views/mainwindowview.baml     views/exclusionview.baml
themes/darktheme.baml         themes/lighttheme.baml
resources/main.ico            resources/discord-symbol-blurple.png   resources/btn_donate_lg.gif
```

Ou seja: **`app` + 2 views + 2 temas**. Não existe BAML de licença/ativação
improvisado — a superfície de UI é essa. (Isso não prova ausência de checagem de
licença no código: não auditei os 388 métodos procurando por isso.)

### 4.2 Os textos do próprio autor (satélite pt)

O dump confirma **72 chaves** no recurso neutro do exe e **72** no satélite `pt`
(mesmo conjunto, traduzido). O único recurso que não se enumera é o
`Properties.Resources.resources`: ele guarda um ícone via serialização
(`BinaryFormatter`) e o `ResourceReader` do .NET Core responde
`NotSupportedException` — o dump aborta **aquela** lista e segue nas outras (é por
isso que o total de streams do `.g.resources` sai inteiro). Alguns textos que
fixam semântica
(aspas literais do binário):

- **Memória livre:** "Páginas de memória realmente não utilizada (Zero + Free).
  Não inclui Standby. Corresponde ao valor Livre do Gerenciador de tarefas."
- **Memória disponível:** "Livre + Memória de espera. Todas as páginas que o
  Windows pode fornecer a um processo instantaneamente sem acessar o disco.
  Corresponde ao valor Disponível do Gerenciador de tarefas."
- **Limiar de memória livre:** "A limpeza será acionada quando a memória livre
  cair abaixo deste limite (MB). **Usa o valor Livre (não Disponível)**."
- **GlobalTimerResolutionRequests:** "… que no **Windows 11** permite que as
  solicitações de resolução do cronômetro por processo tenham efeito global.
  **Requer uma reinicialização.**"
- **Agendador de tarefas:** "Cria uma entrada do Agendador de Tarefas do Windows
  para iniciar ISLC automaticamente no logon do usuário (elevado)."
- **Atalho:** "Registra um atalho de teclado global para acionar uma limpeza
  imediata … de qualquer lugar (mesmo em jogo)."
- **Exclusão:** "Os processos adicionados à lista evitarão que o ISLC limpe a
  lista de espera enquanto estes estiverem em execução."

Isso é melhor que qualquer dedução: é a documentação do autor, dentro do binário.
Confirma também que a leitura "free = Zero+Free" (método B/C) é intencional.

---

## 5. Método E: strings e arquivos do pacote

### 5.1 Rede: só verificação de versão (dois endpoints)

| Onde | Endpoint | Detalhe |
|---|---|---|
| `CheckUpdate.VersionUpdateAsync` | `https://www.wagnardsoft.com/ISLC/currentversion.txt` | baixa texto, compara e **descarta o resultado** (método é no-op) |
| `UpdateCheck.GetVersionInfoAsync` | `https://www.wagnardsoft.com/api/islc/version.json` | `User-Agent: ISLC/<versão> (Intelligent Standby List Cleaner)`, timeout 5 s, JSON com `LatestVersion`, `ReleaseDate`, `DownloadUrl`, `ReleaseNotesUrl`, `MinimumSupportedVersion` |

Nenhum POST, nenhum envio de dados da máquina, nenhum telemetria/analytics. É
"olá, existe versão nova?" — mas é **chamada de rede**, e isso não aparecia em
nenhum resumo anterior porque a análise estava focada no purge.

Strings de UI/rede no heap: `https://discord.gg/…`, link de doação PayPal,
`https://www.wagnardsoft.com`, `"ISLC is already running…"`,
`"Arguments issues, please check them"`.

### 5.2 CLI — o ReadMe confirma e o código completa

| Argumento | Comportamento verificado |
|---|---|
| `-minimized` | sobe minimizado e já monitorando |
| `-polling <ms>` | sobrescreve `PollingRateSTR` |
| `-listsize <MB>` | sobrescreve `StandbyListValue` |
| `-freememory <MB>` | sobrescreve `FreeMemoryValue` |
| `-runonce` | um poll, decide, sai (ignora `-polling`) |
| `-purge` | `ClearStandbyCache()` e `Exit()` |

O `-purge` é tratado **antes** da checagem de instância única — é o único caminho
que funciona com o ISLC já aberto. `ReadMe.txt` recomenda `LODCTR /R` quando há
erro, o que confirma que o app depende de contadores de desempenho habilitados.

---

## 6. Método F: medir a decisão em vez de só lê-la

`scripts/islc_gate_probe.ps1` (novo) reimplementa **a mesma** leitura do ISLC
(`NtQuerySystemInformation(80)`, soma dos 8 buckets, `Zero+Free`) e aplica o mesmo
predicado com os defaults do `config.json` (1024 MB / 1024 MB).

Rodada real (32 GB, Windows 11, 20 amostras a 700 ms):

| Métrica | Resultado |
|---|---|
| Condição atendida | **20/20 amostras (100 %)** |
| Standby | mín. 10.890 MB, máx. 12.611 MB, média **11.768 MB** |
| "Free" (Zero+Free) | mín. **0 MB**, máx. **256 MB** |
| RAM "Disponível" | mín. 10.923 MB |
| Resolução do timer no momento | 0,977 ms (curioso: alguém já pediu <1 ms) |
| `GlobalTimerResolutionRequests` | não definido |

Leitura: com os **defaults do ISLC**, o gate de memória livre (`free < 1024 MB`)
é verdadeiro em 100 % dos ticks porque "free" é Zero+Free, não "Disponível". Quem
decide, na prática, é só o tamanho da standby list. Ligar o monitoramento com
esses valores nesta máquina = **um purge por segundo**. Isso não é bug do ISLC:
é o que os defaults significam. E é a razão de a própria página oficial pedir
para ajustar os limites.

Também medido: o token desta sessão **não tem** `SeProfileSingleProcessPrivilege`
(nem `SeIncreaseQuotaPrivilege`, nem `SeDebugPrivilege`) — `whoami /priv` lista só
o conjunto padrão de usuário (`SeChangeNotify`, `SeShutdown`, `SeUndock`,
`SeIncreaseWorkingSet`, `SeTimeZone`, `SeAssignPrimaryToken`). Sem esse privilégio
o purge falha em `NtSetSystemInformation`, o que explica a exigência de rodar como
admin.

> Quirk de método: no Git Bash o `whoami` resolve para `/usr/bin/whoami`, que não
> entende `/priv` e imprime "extra operand". Rodar
> `cmd /c "C:\Windows\System32\whoami.exe /priv"` é a única forma de ver a
> tabela de privilégios de verdade — a primeira tentativa devolveu saída vazia e
> quase virou "confirmado por engano".

> Por que medir em vez de só ler: o IL diz "se A e B então purga". Não diz se B
> alguma vez é verdadeiro. A medição responde isso com número, e foi ela que
> mostrou que o segundo limiar é quase decorativo nesta configuração.

---

## 7. Achados que só apareceram fora do IDA

| Achado | Método | Por que importa |
|---|---|---|
| Assinatura Authenticode válida da Wagnardsoft | shell/assinatura | procedência do binário; não é um fork adulterado |
| Build determinístico (timestamp 1912/1949) | A | data do PE não serve como data de build |
| `System.Net.Http` + `System.Management` referenciados | A | levou à descoberta da checagem de update e do WMI |
| 21 declarações de P/Invoke (5 no exe + 16 no Core; `CloseHandle` aparece em 2 tipos) | A | superfície nativa completa, incluindo `RegisterHotKey` e `SetProcessInformation` |
| `Newtonsoft.Json` embutido como recurso | A + D | distribuição single-folder sem DLLs externas |
| 3 views + 2 temas, nenhum BAML de licença/trial | D | não há tela de ativação escondida |
| Semântica oficial de cada opção | D | textos do autor confirmam "Livre (Zero+Free)" e o requisito de reboot |
| Checagem de versão por HTTP (2 endpoints, 5 s) | A → C → E | comportamento de rede não documentado no ReadMe |
| 100 % de disparo com defaults | F | fecha a discussão "purga a cada tick?" com medição |
| Ausência do privilégio de purge no token atual | F | explica a exigência de elevação |

### 7.1 Correções que esses métodos impuseram aos documentos anteriores

1. **Guarda de privilégio.** O `ISLC_ANALYSIS.md` dizia que o `return` silencioso
   é código morto "porque a exceção sobe". O correto: `AdjustTokenPrivileges`
   devolve **TRUE** mesmo quando o privilégio não foi atribuído (o erro real é
   `ERROR_NOT_ALL_ASSIGNED = 1300`, lido só com `GetLastError`). Então
   `SetIncreasePrivileges` retorna `true`, o `return` é pulado, e a falha só
   aparece depois, em `NtSetSystemInformation` → `STATUS_PRIVILEGE_NOT_HELD`.
   O modo de falha existe; o diagnóstico é que estava impreciso.
2. **Resolução de timer.** "Pedido global" valia até o Windows 10 2004. Hoje a
   solicitação é por processo; o ISLC expõe a chave `GlobalTimerResolutionRequests`
   justamente para tentar o efeito global no Windows 11 (e avisa que exige
   reboot). A chave não tem documentação oficial da Microsoft — evidência
   independente aponta Windows 11 22H2+ / Server 2022+.
3. **Personificação SYSTEM no timer.** A doc tratava como "truque para o pedido
   global". `NtSetTimerResolution` não exige privilégio e vale para o processo
   chamador; personificar SYSTEM em volta da chamada não tem efeito documentado
   sobre o alvo. Fica registrado como **suspeito/possivelmente inócuo**, não como
   "assim que se faz global".
4. **"Sem rede"** — não era afirmação explícita, mas a análise anterior não
   mencionava a checagem de versão. Agora está mapeada.

---

## 8. O que continua **não** verificado

- **Não executei o ISLC** nesta passada (nem `-purge`): a taxa de disparo foi
  medida reproduzindo a leitura, não observando o app. Executar exigiria elevação
  e alteraria a standby list da máquina.
- **Nenhuma captura de tráfego** (Wireshark/ETW) foi feita; a conclusão sobre a
  checagem de update vem do código + strings.
- **Só a versão 1.0.4.8** foi analisada. Comparar com builds antigos é possível
  com o mesmo ferramental (é o objetivo da seção 5 do guia do IDA).
- **Nada de ETW/kernel**: não instrumentei o gerenciador de memória para ver o
  purge acontecer; a semântica vem do `Windows Internals`/phnt (ver pesquisa) e
  do próprio header do ISLC.
- O `PurgeLogic` foi conferido instrução por instrução; **não** repeti isso para
  os 388 métodos. A regra de decisão é o que importa para a comparação.

---

## 9. Reprodução

```bash
ISLC="/c/Users/Lugia/Downloads/ISLC v1.0.4.8/Intelligent standby list cleaner ISLC.exe"
CORE="/c/Users/Lugia/Downloads/ISLC v1.0.4.8/ISLC.Core.dll"

# A) metadata (sem decompilador) — dumper versionado no repo
dotnet run --project scripts/pe_metadata_dump -- "$ISLC" summary
dotnet run --project scripts/pe_metadata_dump -- "$ISLC" pinvoke
dotnet run --project scripts/pe_metadata_dump -- "$ISLC" refs
dotnet run --project scripts/pe_metadata_dump -- "$ISLC" strings | grep -i "http\|timer\|purge"
dotnet run --project scripts/pe_metadata_dump -- "$ISLC" res > /c/temp/islc_res.txt
dotnet run --project scripts/pe_metadata_dump -- \
  "/c/Users/Lugia/Downloads/ISLC v1.0.4.8/pt/Intelligent standby list cleaner ISLC.resources.dll" res

# C) C# com símbolos
ilspycmd -p -o C:/temp/islc_src/Gui "$ISLC"
ilspycmd -p -o C:/temp/islc_src/Core "$CORE"

# B) IL pelo IDA (ver docs/IDA_PRO_ISLC_GUIDE.md)
# F) medir a decisão (não executa o ISLC)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/islc_gate_probe.ps1 -Samples 20 -IntervalMs 700
```

`pe_metadata_dump` usa apenas `System.Reflection.Metadata` (in-box no SDK): não
precisa de IDA, ILSpy, dnSpy, Python nem internet. É o caminho mais barato para
responder "quantos métodos/tipos, quais P/Invoke, quais recursos e dependências"
de qualquer assembly gerenciado — inclusive dos binários do próprio KitLugia.
