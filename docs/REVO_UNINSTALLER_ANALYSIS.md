# Revo Uninstaller — Análise Binária (Deep Scan / Leftovers)

> Análise feita em 05/08/2026 via IDA Pro 9.0 (idat.exe batch, sem PDB) sobre
> `RevoUnin.exe` (22.099.240 bytes, x64 nativo, MFC 7-14 + Prof-UIS, SQLite embutido).
> Complementada com parsing off-line do `.asm` (Python 3.11) e xrefs de strings.
> Objetivo: comparar com o Deep Uninstall do Microsoft PC Manager
> (ver `PC_MANAGER_DEEP_UNINSTALL_ANALYSIS.md`) e informar o KitLugia.

## Resumo executivo

O Revo Uninstaller **NÃO usa snapshot pré/pós-global de arquivos** — igual ao
PC Manager. O scan de leftovers acontece DEPOIS da desinstalação, dirigido por
linha de comando (`/leftovers`), com três fontes de alvo:

1. **Caminhos gravados ANTES da desinstalação** em chaves de registro próprias
   (marcadores `ADCU` / `ADAU` = AppData Current User / All Users, por app).
2. **O caminho de instalação** do app (InstallLocation / dir do uninstaller).
3. **Scanner de Registry Classes** (`SOFTWARE\Classes\*` completo: CLSID,
   Interface, TypeLib, AppID, Mime, Applications, OpenWithProgIds, etc.)

A lista de resultados é persistida num banco **SQLite embutido** (o Revo linka
o sqlite3 — strings `SCAN %S`, `SCAN %d CONSTANT ROW%s` são do
`sqlite3_trace_v2`), e a deleção é feita **por item** com opção de ir para a
Recycle Bin (config `DelToBin`).

## Arquitetura (funções-chave)

| Endereço | Papel |
|---|---|
| `sub_140178EB0` (582381-580561) | **Dispatcher de linha de comando**: `/leftovers`, `/continue`, `/chactivation`, `/hunter`, `/forcedfolder`, `/update`, `/implog`, `/settings`, `/updatesubscription`, `SC` |
| `sub_14017F960` / `sub_14017FB20` | Leitura/escrita de configuração no registro (chave `Uninstaller\`) |
| `sub_14018D6C0` (610949..619993) | **Scanner de Registry Classes** (~51 strings: CLSID/Interface/Applications/TypeLib/AppID/Mime/SystemFileAssociations/Record/Media Type/Local Settings/ActivatableClasses, + WOW6432Node, InprocServer32/LocalServer32/DefaultIcon/OpenWithProgIds) |
| `sub_1400292C0` (63279..74089) | **Junk Files cleaner de navegadores** (Firefox/Chrome/Edge/Opera: history/cookies/cache via SQLite: places.sqlite, cookies.sqlite, formhistory.sqlite...) |
| `sub_1400FB980` | Motor de queries SQLite (trace `SCAN %S`) |
| `sub_1400017700` (linha 38133) | Arquivador de autoruns ("Registry: HKLM/HKCU Run/RunOnce/RunServices/RunOnceEx/32bit", espelha em `SOFTWARE\VS Revo Group\Revo Uninstall\...`) |
| `sub_14005BE30` | Runner pós-desinstalação (SHELLEXECUTEINFOW, opções) |
| `sub_140178BA0`/`C70`/`D20`/`DD0` | Helpers de string (erro fatal `AppData Invalid.` → MessageBoxW + ExitProcess quando o buffer do AppData é inválido) |

## Flags de linha de comando (`/leftovers` flow, sub_140178EB0)

- `pNumArgs > 4` obrigatório (arg0=exe, arg1=/leftovers, arg2/3/4 = alvos)
- `KeepFiles` (case-sensitive, 10 chars) → preserva arquivos (não deleta)
- `/continue` → retoma scan anterior
- `/forcedfolder` → força varredura de pasta específica

## Configuração (chave `Uninstaller\` do registro)

Valores lidos/escritos via `sub_14017F960`/`sub_14017FB20`
(DWORD, checado com SendMessage BM_GETCHECK de checkboxes):

- `Create System Restore Pont` — cria ponto de restauração antes
- `FastLoadMode` — carregamento rápido
- `StopRunExe` — mata processos do app antes de desinstalar
- `DelToBin` — deleta leftovers para a **Recycle Bin** (default; senão permanente)
- `Select leftovers by default` — pré-marca os itens achados
- `Use Reg Install Date` — usa data de instalação do registro
- `Show System Components` — mostra componentes de sistema
- `Disable scan after uninstall` — pula o scan pós-desinstalação
- `Maximize uninstall wizard` — maximiza o wizard

Outras chaves de config achadas: `View\Small Icons in Details`,
`Junk Files\General\`, `Junk Files\Columns\`, `Junk Files\Exclude\`,
`Junk Files\Include\`, `Junk Files\General\Extensions`,
`Junk Files\General\LastDrives\`, `Uninstaller\RegExclude`
(CDlgAddTracedRegExclude = diálogo de exclusão de registro rastreado).

## Marcadores AppData (ADCU / ADAU)

Strings `\VS Revo Group\Revo Uninstaller\ADCU` e `...\ADAU` (AppData Current
User / All Users) aparecem em 16+ pontos do código — é o mecanismo do Revo
para saber QUAIS pastas de dados do app existem (gravadas na instalação ou na
primeira varredura). O scanner pós-uninstall usa esses marcadores para mirar
`%AppData%` e `%LocalAppData%` do app sem varrer o disco inteiro.
`AppData Invalid.` = erro fatal quando o path do AppData é inválido.

## Scan de arquivos

- BFS/FIND com `FindFirstFileW/FindNextFileW` (155 call sites no total)
- Alvos: instal path + ADCU/ADAU + (opcional) drives configurados
- Filtros: extensões (`extensions`), padrão de busca, `Ignore files accessed
  in the last 24 hours` (opção de UI: "Delete files to the Recycle Bin*Ignore
  files accessed in the last 24 hours")
- Deleção: `DeleteFileW/RemoveDirectoryW` para a Recycle Bin via
  `%s:\$Recycle.Bin\%s` quando `DelToBin` ativo; `SHFileOperationW` também importado

## Registry scan

`sub_14018D6C0` varre (em ordem aproximada):
`SOFTWARE\Classes\CLSID`, `...\Interface`, `...\Applications`, `...\TypeLib`,
`...\AppID`, `...\Mime`, `...\SystemFileAssociations`, `...\Record`,
`...\Media Type`, `...\Local Settings`, `...\ActivatableClasses`,
com variants `SOFTWARE\WOW6432Node\Classes\*` e `SOFTWARE\Classes\WOW6432Node\*`,
checando `InprocServer32`, `InprocServer`, `LocalServer`, `InprocHandler32`,
`LocalServer32`, `DefaultIcon`, `OpenWithProgIds`, `\ProgID`,
`\VersionIndependentProgID`, `\TypeLib`, `Shell\Open\Command`, `Content Type`.

O scan aponta referências para o CLSID do app desinstalado (o Revo procura
entries cujo servidor/path aponta para o diretório do app).

## Comparação Revo × PC Manager × KitLugia

| Aspecto | PC Manager (Microsoft) | Revo Uninstaller | KitLugia (atual) |
|---|---|---|---|
| Tipo | .NET (ilspycmd) | Nativo x64 MFC/SQLite (IDA) | .NET |
| Snapshot global | ❌ | ❌ | ❌ (captureBaseline:false) |
| Scan pós-uninstall | ✅ (ResidualRule allowlist JSON) | ✅ (CLI /leftovers) | ✅ (ScanUwpLeftovers) |
| Alvo de arquivos | InstalledPath (BFS depth 9) | Instal path + ADCU/ADAU + drives | — |
| AppData por app | ❌ (não varrre global) | ✅ marcadores ADCU/ADAU | ❌ |
| Registry | Só a chave Uninstall própria | Classes\* completo + chave própria | — |
| Persistência de resultados | Observers (progress) | SQLite embutido | — |
| Allowlist por app | ✅ ResidualRule (AppUninstallKey/InstallPathFolder/Depth/FileExtension/FileCount) | ❌ (heurística por path gravado) | ❌ |
| Deleção | fail types por item | Recycle Bin opcional (`DelToBin`) | — |
| Exclusões | — | RegExclude / Junk Files\Exclude | — |
| Filtro tempo | — | Ignore < 24h acesso | — |
| Revert/Restore Point | ❌ | Create System Restore Point opcional | — |

## Lições para o KitLugia

1. **Marcadores ADCU/ADAU são o mecanismo mais barato e preciso** de mirar
   AppData: gravar os paths de dados do app no momento da instalação (ou na
   primeira lista) e usá-los no pós-scan. Melhor que varrer AppData global.
2. **Config persistente em chave própria** (`Uninstaller\` no Revo) com
   toggles de comportamento (DelToBin, StopRunExe, Select leftovers by
   default, Disable scan) é um bom padrão de UI — espelha os toggles que o
   KitLugia já tem para GameBoost/Comunidade.
3. **SQLite como buffer de resultados do scan** dá progresso incremental e
   evita re-scan total — o KitLugia poderia usar uma tabela temporária.
4. **Registry Classes scan é caro mas abrangente** — só faz sentido como
   "scan profundo" opcional, pareado com confirmação do usuário.
5. Exclusões (`RegExclude`/`Junk Files\Exclude\`) e filtro de tempo de acesso
   são os dois refinamentos de segurança mais baratos de implementar.

## Artefatos

- `%TEMP%\opencode\revo\RevoUnin.exe` — cópia do binário
- `%TEMP%\opencode\revo\RevoUnin.exe.asm` — saída IDA (110.990.934 B)
- `%TEMP%\opencode\revo\RevoUnin.exe.i64` — banco IDA (187.604.756 B)
- `%TEMP%\opencode\revo\pe_scan.py` / `extract_funcs.py` / `scan_density.py` /
  `func_strings.py` / `dump_*.py` — ferramentas de parsing off-line
- `%TEMP%\opencode\revo\funcs_out.txt` — funções extraídas
- `C:\Program Files\VS Revo Group\Revo Uninstaller\` — instalação (só leitura)
