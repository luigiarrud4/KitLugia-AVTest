# pe_metadata_dump

Dumper de metadados de assembly .NET **sem decompilador**: lê o PE e as tabelas do
metadata com `System.Reflection.Metadata` (in-box no SDK, nenhuma dependência).

Serve para auditar qualquer binário gerenciado — inclusive os do próprio KitLugia —
com uma fonte independente do ILSpy/IDA/dnSpy.

## Uso

```bash
dotnet run --project scripts/pe_metadata_dump -- <assembly> <modo>
```

| Modo | Saída |
|---|---|
| `summary` | identidade, `CorFlags`, timestamps, contagens de tabelas |
| `types` | tipo → nº de métodos/campos/P-Invoke, flags |
| `pinvoke` | **toda** a superfície nativa: `dll!entrypoint`, método gerenciado, flags |
| `refs` | assemblies referenciados + versão |
| `resources` | recursos de manifesto (embedded/linked) |
| `strings` | user string heap (literais `ldstr`) |
| `res` | conteúdo dos `.resources` (BAML, ícones, strings localizadas) |

## Exemplos

```bash
dotnet run --project scripts/pe_metadata_dump -- bin/KitLugia.GUI.dll summary
dotnet run --project scripts/pe_metadata_dump -- bin/KitLugia.GUI.dll pinvoke
dotnet run --project scripts/pe_metadata_dump -- bin/KitLugia.GUI.dll strings | grep -i "http"
```

## Notas

- `totalmethods` do modo `summary` é a contagem da tabela `MethodDefs` — o mesmo
  número que o IDA conta como `.method` no `.asm` (serve de contraprova).
- `res` materializa o stream pelo próprio runtime (`AssemblyLoadContext`); mapear
  RVA na mão devolve bytes deslocados e o `ResourceReader` acusa arquivo corrompido.
- O modo `res` aborta a listagem (com `ABORT`) quando encontra um objeto binário que
  o .NET Core não materializa (ícone/imagem) — o resto do dump continua válido.
- Não executa o assembly: só lê metadata e recursos.
