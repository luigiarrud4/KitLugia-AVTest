# Como usar o IDA Pro 9.0 no ISLC (e o que fazer com o resultado)

> Guia reprodutível, escrito depois de rodar o IDA no binário real
> (`Intelligent standby list cleaner ISLC.exe`, 1.0.4.8, SHA-256
> `11e4ed0b…3322f`). Tudo abaixo foi verificado; o que não foi está marcado.
>
> Complemento: [`docs/ISLC_ANALYSIS.md`](ISLC_ANALYSIS.md) (o que o ISLC faz) e
> [`docs/ISLC_VS_KITLUGIA_COMPARISON.md`](ISLC_VS_KITLUGIA_COMPARISON.md) (o que
> vale implementar no KitLugia).

---

## 0. Antes de tudo: o IDA 9.0 abre .NET?

**Sim.** Diferente de versões antigas, o IDA 9.0 traz um loader de assemblies
gerenciados. Ao abrir o ISLC, o cabeçalho gerado diz:

```
// Format      : Microsoft.Net assembly
// Processor       : cli
.assembly 'Intelligent standby list cleaner ISLC'
{
  .hash algorithm 0x00008004
  .ver 1:0:4:8
}
.module extern user32.dll
.module extern kernel32.dll
.module extern ntdll.dll
```

E a listagem não é x86: é **IL/MSIL**, com os nomes originais vindos do metadata
(o `.pdb` ajuda, mas o metadata já basta). No `.asm` gerado:

| Medida | Valor |
|---|---|
| `.method` encontrados | **388** |
| `.class` encontrados | **43** |
| Métodos com nome legível | sim (ex.: `MainWindowViewModel__PurgeLogic`) |

Esses números foram confirmados por um **terceiro** caminho independente (leitura
das tabelas de metadata com `System.Reflection.Metadata`, que não usa IDA nem
ILSpy): `TypeDefs = 44` (43 + `<Module>`) e `MethodDefs = 388`. Três instrumentos,
mesmo resultado — ver [`ISLC_BINARY_FORENSICS.md`](ISLC_BINARY_FORENSICS.md).

Ou seja: o pedido "usar o IDA Pro no ISLC" é viável — o IDA serve tanto para
**ver o IL** quanto para navegar por xrefs. O ILSpy continua sendo melhor para
**ler C#**, e os dois se complementam (seção 4). Mas IDA não é o único
instrumento, e o guia diz onde ele para (seção 4.2).

> Detalhe do binário: é .NET Framework 4.8 com WPF
> (`grep -a "mscoree.dll\|BSJB"` no exe confirma). Não há código C++ nativo
> além do stub do CLR — o `ISLC.Core.dll` é o outro assembly e contém o P/Invoke.

---

## 1. Rodar em batch (linha de comando)

```bash
IDA="/c/Users/Lugia/Downloads/IDA Professional 9.0/IDA Professional 9.0/idat.exe"
mkdir -p /c/temp/ida_islc && cd /c/temp/ida_islc
cp "/c/Users/Lugia/Downloads/ISLC v1.0.4.8/Intelligent standby list cleaner ISLC.exe" ./ISLC.exe

"$IDA" -B -c -A ISLC.exe
```

Flags:

| Flag | Efeito |
|---|---|
| `-B` | Modo batch: gera `.asm` + `.i64` e sai |
| `-c` | Disassembly (não deixa o loader "pensar" em outra opção) |
| `-A` | Autonomia total: não pergunta nada (sem diálogos) |

Saída em `/c/temp/ida_islc`:

```
ISLC.exe.asm    (~526 KB, IL legível + xrefs)
ISLC.exe.i64    (~664 KB, banco do IDA — abrir no GUI depois)
```

Resultado da execução real: exit code 0, sem prompts.

**Quirk desta instalação:** só existe `idat.exe` (não há `idat64.exe`). Usar
`idat64.exe` dá "No such file or directory".

**Quirk da licença:** se o IDA reclamar de aceite, as chaves `EULA 90`…`EULA 93`
em `HKCU:\Software\Hex-Rays\IDA` resolvem (mesmo procedimento já usado no
trabalho do `setupcompat.dll`).

**Quirk do banco:** se você reprocessar o mesmo arquivo e o `.i64` já existir, o
IDA pode reutilizar/ignorar. Apagar o `.i64` antes quando quiser análise nova.

---

## 2. Abrir no GUI e navegar

```bash
"/c/Users/Lugia/Downloads/IDA Professional 9.0/IDA Professional 9.0/ida.exe" \
  C:/temp/ida_islc/ISLC.exe.i64
```

Atalhos que importam aqui:

| Atalho | Uso |
|---|---|
| `Shift+F12` | Strings (mostra thresholds, mensagens, nomes de campo) |
| `Ctrl+P` / Functions window | Lista de métodos **com nome** — filtrar por `Purge`, `Timer`, `Standby` |
| `Ctrl+X` | Xrefs da função/campo selecionado (quem chama `PurgeLogic`) |
| `F5` | Nomes (renomear para anotar a análise — fica no `.i64`) |
| `Ctrl+F` | Buscar texto/símbolo na listagem |

O IDA nomeia os métodos com **namespace + `__`** no lugar do ponto:

```
Intelligent_standby_list_cleaner_ISLC.ViewModels.MainWindowViewModel__PurgeLogic
Intelligent_standby_list_cleaner_ISLC.ViewModels.MainWindowViewModel__TimerCallback
Intelligent_standby_list_cleaner_ISLC.ViewModels.MainWindowViewModel__ClearStandbyListAsync
Intelligent_standby_list_cleaner_ISLC.Win32_NtSetSystemInformation__ClearStandbyCache
```

---

## 3. Mapa: onde olhar para cada pergunta

| Pergunta | Método/tipo no IDA | Assembly |
|---|---|---|
| Quando ele decide purgar? | `MainWindowViewModel__PurgeLogic` | exe |
| De quanto em quanto tempo? | `MainWindowViewModel__TimerCallback` / `__RestartTimer` | exe |
| Como ele mede a standby? | `MainWindowViewModel__GetMemoryInfo` | exe |
| O que ele chama para purgar? | `Win32_NtSetSystemInformation__ClearStandbyCache` | Core |
| Como pede privilégio? | `Win32_PrivilegeElevation__SetIncreasePrivileges` | Core |
| Resolução de timer | `Win32_NtSetTimerResolution__SetMaxTimerResolution` | Core |
| Lista de exclusão | `AppMonitorWMI__StartMonitoring` / `__Update` | Core |
| Config | `SettingsManager__Load` / `__WriteAtomic` | Core |
| Timer resolution via token SYSTEM | `ImpersonateUser__Taketoken` | Core |

### 3.1 Exemplo real de leitura (IL do `PurgeLogic`)

Trecho extraído do `.asm` (só o predicado):

```
ldloc.0                                  // flag = !IsAppsExecuting
brfalse.s loc_2D2B
call     get_GetSystemCacheValue         // standby MB
ldarg.1
unbox.any [mscorlib]System.UInt64
blt.un.s loc_2D2B                        // se standby < limiar → sai
call     get_AvailableMemory
call     AppSettings::get_FreeMemoryValue
call     [mscorlib]System.UInt64::Parse
bge.un.s loc_2D2B                        // se free >= limiar → sai
call     get_PurgeCommand
callvirt ICommand::Execute
```

Lê-se direto: **três condições** (`!excluded && standby >= X && free < Y`), sem
cooldown, sem histerese, e `PurgeCommand.Execute` é o `async` que chama
`ClearStandbyCache`. Confere com o C# decompilado — por isso a seção 4 importa.

---

## 4. IDA + ILSpy: use os dois

O IDA é ótimo para **provar** o que o binário faz (IL fiel, xrefs, sem heurística
de decompilador). O ILSpy é ótimo para **entender rápido** (C# idiomático, nomes
de variável do PDB).

```bash
# Assembly do núcleo (P/Invoke, settings, WMI)
ilspycmd -p -o C:/temp/islc_src/Core "C:/Users/Lugia/Downloads/ISLC v1.0.4.8/ISLC.Core.dll"

# UI + lógica de decisão
ilspycmd -p -o C:/temp/islc_src/Gui \
  "C:/Users/Lugia/Downloads/ISLC v1.0.4.8/Intelligent standby list cleaner ISLC.exe"
```

Como cruzar os dois:

1. No ILSpy, localize a regra (ex.: `PurgeLogic`).
2. No IDA, `Ctrl+F` pelo nome e confira o **IL** — é o que garante que o
   decompilador não "inventou" nada.
3. Se o ILSpy mostrar `if/else` estranho, o IL diz a verdade.

Diferenças que você vai notar:

| IDA (IL) | ILSpy (C#) |
|---|---|
| `unbox.any UInt64` no limiar | `(ulong)StandbyListEnteredSize` |
| `brfalse.s` / `bge.un.s` | `if (cond && cond)` |
| nomes `Namespace__Metodo` | `Namespace.Metodo` |
| sem corpos de `async` (vira state machine) | mostra `async Task` bonito |

### 4.1 Cruzamento medido (o teste que vale a pena fazer)

Não basta "os dois parecem iguais". Rode o mesmo método nos dois e compare:

```bash
# ILSpy com IL
ilspycmd --ilcode -t "Intelligent_standby_list_cleaner_ISLC.ViewModels.MainWindowViewModel" \
  "C:/Users/Lugia/Downloads/ISLC v1.0.4.8/Intelligent standby list cleaner ISLC.exe" > vm.il

# IDA
sed -n '6037,6110p' /c/temp/ida_islc/ISLC.exe.asm   # bloco do PurgeLogic
```

Resultado real do `PurgeLogic`: três ferramentas, mesmo RVA `0x4970`, mesmo
tamanho de 128 B (`0x80`) e **46 instruções** com a sequência de opcodes idêntica
(IDA, ILSpy e `dotnet-ildasm`/Mono.Cecil).

> **Cuidado ao contar**: um regex de rótulo com hex maiúsculo
> (`IL_[0-9A-F]{4}`) **perde** rótulos como `IL_000c` e dá 40 num método de 46 —
> erro que eu mesmo cometi aqui antes do terceiro instrumento. Use
> `IL_[0-9A-Fa-f]{4}`, ou melhor, compare as listas de opcodes com `diff` em vez
> de confiar em contagem.

Se um método divergir, a suspeita vai para o **decompilador**, não para o
binário — e o IL é o desempate.

### 4.2 Onde o IDA para (e o que usar no lugar)

| Pergunta | IDA resolve? | Use |
|---|---|---|
| Quantos métodos/tipos existem? | sim, contando `.method` no `.asm` | melhor: `scripts/pe_metadata_dump` (tabela oficial) |
| Quais P/Invoke existem? | só os que aparecem no IL | dumper (mostra **todos**, com flags de marshalling) |
| Quais assemblies ele referencia? | `.module extern` só lista DLLs nativas | dumper `refs` (mostra `System.Net.Http`, `Newtonsoft.Json`…) |
| O que tem dentro de `.resources`? | **não** | dumper `res` (BAML, ícones, 72 textos localizados) |
| Há tela/feature escondida? | não dá para saber só do IL | nomes de BAML em `*.g.resources` |
| Qual é a assinatura do binário? | não | `Get-AuthenticodeSignature` |
| **Com que frequência** a decisão dispara? | **não** — IL não tem números do runtime | `scripts/islc_gate_probe.ps1` |
| Qual é a URL de update? | aparece nas strings do heap (`Shift+F12`) | dumper `strings` + leitura do C# |

Isso não diminui o IDA: ele é o instrumento que **prova** a execução. Só não é
o que responde "o que o app baixa", "quais textos ele mostra" ou "o quanto isso
dispara na prática".

---

## 5. Exportar dados para comparar versões (idasm + grep)

Não é preciso IDAPython para o essencial: o `.asm` é texto estável e o `.i64`
guarda os nomes que você renomeou.

```bash
cd /c/temp/ida_islc

# Todos os métodos (nome + assinatura)
grep -n "^\s*\.method" ISLC.exe.asm > methods.txt

# Chamadas para APIs de memória
grep -nE "NtSetSystemInformation|NtQuerySystemInformation|EmptyWorkingSet|SetProcessInformation" \
  ISLC.exe.asm

# Constantes/limiares no IL
grep -nE "ldc\.i4(\.s)? +[0-9]+" ISLC.exe.asm | head -40
```

Se precisar de **structured export** (JSON), o script IDAPython abaixo funciona
em assemblies .NET porque usa só a API genérica do IDA:

```python
# C:/temp/export_islc.py  — rodar com: idat.exe -A -S"C:/temp/export_islc.py" ISLC.exe
import idaapi, idautils, idc, json, os

out = r"C:\temp\islc_export"
os.makedirs(out, exist_ok=True)

data = {"functions": [], "strings": []}
for ea in idautils.Functions():
    data["functions"].append({"ea": hex(ea), "name": idaapi.get_name(ea)})

for s in idautils.Strings(default_setup=True):
    try:
        data["strings"].append({"ea": hex(s.ea), "value": str(s)})
    except Exception:
        pass

with open(os.path.join(out, "islc.json"), "w", encoding="utf-8") as f:
    json.dump(data, f, ensure_ascii=False, indent=2)

idaapi.qexit(0)
```

Quirks de batch do IDA já documentados neste projeto (valem igual aqui):
`ida_auto.auto_wait()` quando o script precisa da análise pronta; `-S` com aspas
explícitas; apagar `.i64` obsoleto antes.

> **Licença:** o ISLC é freeware proprietário. `.asm`, `.i64` e o C# decompilado
> ficam em `C:/temp` para estudo — **não** entram no repositório do KitLugia.
> Se quiser versionar evidência, versione só as conclusões (docs) e o SHA-256.

---

## 6. Bônus: rodar o mesmo fluxo no próprio KitLugia

Serve para auditar o que o compilador gerou (ex.: confirmar que o `SetProcessMemoryPriority`
recebeu handle com `0x0200`, que o `EmptyWorkingSet` está no caminho direto, que
não sobrou `Process.GetProcesses()` na thread de UI).

```bash
cp "KitLugia.GUI/bin/Debug/net10.0-windows.../KitLugia.GUI.dll" /c/temp/ida_kit/
cd /c/temp/ida_kit && "$IDA" -B -c -A KitLugia.GUI.dll
grep -n "EmptyWorkingSet\|SetProcessWorkingSetSizeEx\|SetProcessInformation" KitLugia.GUI.dll.asm
```

Útil quando o comportamento em runtime não bate com o que o código-fonte sugere
(trim aplicado onde não devia, P/Invoke falhando mudo etc.).

---

## 7. Checklist rápido

- [ ] Confirmar que é .NET (loader diz `Microsoft.Net assembly`)
- [ ] `idat.exe -B -c -A` no exe **e** no `ISLC.Core.dll`
- [ ] Abrir o `.i64` no GUI (`ida.exe`)
- [ ] `Ctrl+P` → filtrar `PurgeLogic`, `TimerCallback`, `ClearStandbyCache`
- [ ] `Ctrl+X` no campo `_standbyListEnteredSize` → ver quem alimenta o limiar
- [ ] ILSpy para o C# dos mesmos métodos
- [ ] Anotar no doc de análise (não no repo com código do ISLC)
