# KitLugia × Revo Uninstaller — auditoria de paridade

> Data: 31/10/2026. Referência: Revo Uninstaller (analisado por IDA Pro 9.0,
> `RevoUnin.exe`, SHA-256 `27e29f57…19492b`). Alvo: `DeepUninstaller.cs`,
> `AppsPage`, `RecycleBinOp`, `RegistryOwnership`, `UninstallHistory`.
> Rótulos: IMPLEMENTADO / PARCIAL / AUSENTE / DIVERGENTE (deliberado).

## 0. Aviso de leitura

Duas conclusões deste documento **corrigem** o que está em
[REVO_UNINSTALLER_ANALYSIS.md](REVO_UNINSTALLER_ANALYSIS.md) e no
[REVO_UNINSTALLER_PLANO.md](REVO_UNINSTALLER_PLANO.md). As correções estão nas
§1 e §2 e são o resultado mais útil da auditoria.

---

## 1. 🔴 CORREÇÃO: as flags 1044/1108 do Revo estavam lidas ao contrário

`REVO_UNINSTALLER_ANALYSIS.md` §4 tabela `1044 = Lixeira` e `1108 = permanente`.
O binário faz `if (DelToBin == 1) fFlags = 1108`. Aquilo parecia uma divergência
doc-vs-binário. **Não era: o doc estava invertido.**

Decodificado contra `shellapi.h`:

| Valor | Hex | Flags reais | Efeito |
|---|---|---|---|
| 1044 | `0x414` | `FOF_SILENT`(0x04) + `FOF_NOCONFIRMATION`(0x10) + `FOF_NOERRORUI`(0x400) | permanente |
| 1108 | `0x454` | os mesmos **+ `FOF_ALLOWUNDO`(0x40)** | **Lixeira** |

A diferença é exatamente `0x40 = FOF_ALLOWUNDO` — *a* flag que manda para a
Lixeira. Logo o binário está **certo** (`DelToBin=1` = "delete to bin" =
Lixeira). A aritmética do doc também não fechava: `0x414 + FOF_RENAMEONCOLLISION
(0x08) = 0x41C`, não `0x454`. Dois erros se sustentavam mutuamente: `0x0020`
foi batizada de `FOF_NODIRFLAGS` (na verdade é `FOF_WANTMAPPINGHANDLE`) e
trocou-se qual valor tem `ALLOWUNDO`.

**Confirmado em runtime** (§3), não só por aritmética.

---

## 2. 🟢 O que o plano pedia "fazer" já estava feito

O plano de 05/10 listava como prioridade #1 "apagar para a Lixeira via
`SHFileOperationW` `FOF_ALLOWUNDO`". O código já tem isso em
`KitLugia.Core/RecycleBinOp.cs` — mesmas flags, mais o tratamento de
`fAnyOperationsAborted` que o Revo também faz (`sub_14003BA80`), e ainda
corrigindo um bug real do caminho antigo (o `Microsoft.VisualBasic.FileSystem`
caía em exclusão **definitiva silenciosa** quando a Lixeira falhava, e logava
`[permanent]` como se fosse escolha do usuário).

O plano também supunha que o scan de registro "só segue valores, nunca chaves
filhas". O código atual tem walker recursivo (`ScanSoftwareRecursive`, :2428) e
varredura por valor em hives GUID (`ScanHiveByValues`, :3548).

---

## 3. ⚠️ BUG PROVADO: o modo "permanente" manda para a Lixeira

`RecycleBinOp.cs:93-94`:

```csharp
ushort flags = (ushort)(FOF_ALLOWUNDO | FOF_NOERRORUI | FOF_NODIRFLAGS);   // 0x460
if (permanent) flags |= FOF_RENAMEONCOLLISION;                            // 0x468
```

`"apagar permanentemente"` **adiciona** `FOF_RENAMEONCOLLISION` mas **mantém
`FOF_ALLOWUNDO`** — e é `ALLOWUNDO` que decide o destino. Medido nesta máquina
(arquivo de nome único; presença conferida nos metadados `$I*` de
`C:\$Recycle.Bin\<SID>\`; `NukeOnDelete=0` em todos os volumes):

| caso | flags | hr | saiu do disco? | na Lixeira? |
|---|---|---|---|---|
| KitLugia modo Lixeira | `0x460` | 0 | sim | **sim** ✅ |
| KitLugia modo Permanente | `0x468` | 0 | sim | **sim** ⚠️ |
| fix (remover `ALLOWUNDO`) | `0x420` | 0 | sim | não ✅ |
| Revo 1044 | `0x414` | 0 | sim | não (permanente) |
| Revo 1108 | `0x454` | 0 | sim | sim (Lixeira) |

**Impacto:** o usuário que escolhe "apagar permanentemente" (e a UI diz "não
pode ser desfeita", `AppsPage.xaml.cs:2038`) recebe um apagamento **recuperável**
pela Lixeira. Não há perda de dado — é a promessa que não se cumpre, e o oposto
do que o próprio `RecycleBinOp.cs:19-22` diz ter corrigido.

**Correção** (~3 linhas): no modo permanente, **remover** `FOF_ALLOWUNDO` em vez
de adicionar `RENAMEONCOLLISION`; e corrigir o comentário `:91-92`, que afirma
`0x414/0x454` — valores que não correspondem ao que o código calcula.

**O harness não pegava isso.** `tests/RecycleBinAudit/Program.cs:63` valida o
resultado *reportado* (`DeletedPermanently`) e admite nas linhas 15-16 que não
prova o destino. Teste que lê o retorno do próprio código não falha por bug de
destino.

---

## 4. Tabela de paridade (14 capacidades do Revo)

| # | Capacidade | Referência | KitLugia | Rótulo |
|---|---|---|---|---|
| 1 | Lixeira vs. permanente por config | ✅ `sub_14003BA80`, flags 1044/1108 | `RecycleBinOp.cs:82-140`; `DeepUninstallSettings.cs:68`; consumo `DeepUninstaller.cs:3249-3286` | **DIVERGENTE (bug §3)** |
| 2 | Marcadores ADCU/ADAU por app | 🟡 strings, 0 xref de código | `CaptureDataFoldersForApp` `DeepUninstaller.cs:1197-1246` (reenumera por nome, no scan, não persiste) | DIVERGENTE (deliberado) |
| 3 | Snapshot pré/pós (baseline) | ✅ (via marcadores + scan pós) | captura `:174-187` / `SnapshotKeyFileLocations` `:370-398`; diff `:330-347` | IMPLEMENTADO (ressalva §5) |
| 4 | Scan de registro com chaves filhas + `Classes\*` | 🟡 `sub_14018D6C0` (~9 KB, não descompilado) | `:2096-2142`, `ScanComHives` `:4612`, `ScanComByFilePath` | PARCIAL (faltam `Mime`, `ActivatableClasses`) |
| 5 | Take-ownership de chaves protegidas | ✅ `sub_140161700` / `sub_140162F70` (ACL + retry + owner + DACL) | `RegistryOwnership.cs:57-102` existe, usado só em `Guardian.cs:1554` e `PathRepair.cs:830` | **AUSENTE no caminho de desinstalação** |
| 6 | Log estruturado `arquivo:linha` | ✅ `sub_140026440` | `Logger.cs:145-168` só anexa origem quando a msg contém "Exception suppressed" | PARCIAL |
| 7 | Exclusões/allowlist por app | 🟡 `Uninstaller\RegExclude` (0 xrefs) | `Exclude` global `:1268-1325`; sem allowlist por app; não aplicada na deleção | PARCIAL |
| 8 | Blocklist de caminhos críticos | ✅ 8 caminhos exatos | `ProhibitedLocations` `:733-831` (~50), `IsDirectlyInsideCriticalRoot` `:943`, `IsKitLugiaSelfPath` `:971` | IMPLEMENTADO (**melhor que o Revo**) |
| 9 | CLI headless para o scan | ✅ `/leftovers` + `KeepFiles` (`sub_140178EB0`) | — (`App.xaml.cs:115-173` só `--tray/--update/--store/--unlock`) | AUSENTE |
| 10 | Histórico persistente | 🟡 SQLite embutido | `UninstallHistory.cs` + `UninstallHistoryWindow` (JSON, 50) | IMPLEMENTADO (paridade funcional) |
| 11 | Safe/Moderate/Uncertain + confirmado vs heurístico | ✅ (separação visível) | `CleanupSafety` `:19-24`, `Classify*Safety` `:615-700`, diff `:330-347` | PARCIAL (desligado na UI — §5) |
| 12 | Backup `.reg` antes de deletar | ✅ (implícito no log estruturado) | `BackupRegistryKey` `:3949-3982`, antes de `DeleteSubKeyTree` `:4297-4301`; restore `:3394-3413` | IMPLEMENTADO |
| 13 | Filtrar acessados nas últimas 24h | ✅ string de UI | `IgnoreRecent24H` `DeepUninstallSettings.cs:110`, `DeepUninstaller.cs:1170-1185` | IMPLEMENTADO (desligado; só arquivos) |
| 14 | Kill de processos que bloqueiam | ✅ driver `RevoProcessDetector.sys` | `KillProcessesWithTree` `:4389-4495` (por nome + WMI, cap 2 s) | PARCIAL (sem re-kill; sem driver) |
| — | TxF / rollback atômico | ❌ **REFUTADO** (0 imports) | não usa | não copiar |
| — | Ponto de restauração | ⚠️ não encontrado no binário | `TryCreateRestorePoint` `:3919` | IMPLEMENTADO (melhor) |

---

## 5. Bugs e código morto encontrados (não são paridade — são defeito)

| Achado | Evidência | Efeito |
|---|---|---|
| `ShowReviewPanel` **sem nenhum callsite** | def. `AppsPage.xaml.cs:1951`; 0 chamadas no repo | todo o painel de review (XAML `958-1259`) é inalcançável: "Ignorar Sempre", "Restaurar Backup", "Copiar Tudo", "Só Deletáveis" são código morto |
| `captureBaseline: false` no fluxo da página | `AppsPage.xaml.cs:937`, `:1218` | o diff pré/pós nunca roda ali ⇒ `HeuristicFiles/Registry` sempre vazio ⇒ a separação confirmado-vs-heurístico **não funciona na UI** (só no Hunter, `HunterWindow.xaml.cs:941`) |
| `IsExcludedPath` não consultada na deleção | comentário `:1264-1265` afirma o contrário; `PerformCleanup` `:3174-3351` não chama | exclusões do usuário podem ser respeitadas no scan e ignoradas na remoção |
| Histórico grava `AppName` vazio | `PerformCleanup` chamado sem `displayName` (`AppsPage.xaml.cs:1690, 1787, 2051, 2206`) | histórico mostra "(sem nome)" |
| `AutoCleanConfirmed` sem checkbox na UI | `DeepUninstallSettings.cs:104` vs `DeepUninstallSettingsWindow.xaml.cs:14-28` | só editável pelo registro |
| `ScannerMode` (Safe/Moderate/Advanced) não exposto | 0 hits no GUI | modo do scanner inacessível ao usuário |
| `DeepCleanupDialog` descarta heurísticos | `DeepCleanupDialog.xaml.cs:65-88` lê só `LeftoverFiles/Registry` | resíduos heurísticos do Hunter somem sem aviso |
| `EnableTakeOwnershipPrivilege` retorna sucesso com `ERROR_NOT_ALL_ASSIGNED` | `RegistryOwnership.cs:283-287` | privilégio pode não estar habilitado e o chamador acha que sim |
| Mensagem cita botão inexistente | "Use 'Marcar como Criado'" `AppsPage.xaml.cs:2033, 2188` (o controle é "manter") | instrução errada ao usuário |
| Aba "Extensões" oculta | `AppsPage.xaml:789` `Visibility="Collapsed"` | feature pronta e não exposta |
| Comentários que descrevem o que o código não faz | `RecycleBinOp.cs:91-92` (flags), `DeepUninstaller.cs:75-76` ("byte-level"), `:1264-1265` (exclusões) | enganam a próxima auditoria |

---

## 6. O que NÃO copiar do Revo (e por quê)

| Mecanismo | Por que recusar |
|---|---|
| **TxF / transação de filesystem** | ❌ REFUTADO no binário (0 imports de `CreateTransaction*`/`ktmutil`; o caminho transacional é código morto) e descontinuado no Win11 24H2. Portar seria implementar o que nem a referência executa. |
| **ACE `Everyone` na ACL de registro** | O Revo concede `GENERIC_READ` a Everyone. O `RegistryOwnership` atual concede `FullControl` **só ao Administrador** — mais restrito e suficiente. Copiar amplia superfície sem ganho. |
| **Driver `.sys` de detecção de processos** | Exige assinatura, modo kernel e política de AV. O Restart Manager já existe no projeto (`ForceStopUnlockService`); o caminho certo é integrá-lo, não escrever um driver. |
| **SQLite embutido** | 🟡 na referência (só a string `sqlite3_trace_v2`). JSON atende 50 entradas de histórico + 100 de resíduos. Custo real, ganho hipotético. |
| **Filtro de 24 h como diferencial** | Existe, mas NTFS não atualiza *last access* desde o Win10 1803 — é pouco confiável por natureza. Manter desligado por padrão é o certo; não investir. |

---

## 7. Plano priorizado

### Fazer agora (barato, corrige defeito)

1. **`RecycleBinOp` modo permanente** — remover `FOF_ALLOWUNDO` (`0x468` → `0x420`),
   corrigir o comentário das flags, e ajustar o texto da UI que promete
   irreversibilidade. ~5 linhas + 1 teste que confere o **destino** (§3).
2. **Ligar `RegistryOwnership.ForceTakeOwnership`** no fallback de
   `SafeDeleteRegistryEntry` quando `DeleteSubKeyTree` lança
   `UnauthorizedAccessException` (hoje: só loga `FAILED`). É o gap de maior
   impacto funcional e é **uma chamada** — a infraestrutura já existe. ~20 linhas.
3. **`EnableTakeOwnershipPrivilege`** — não retornar sucesso com
   `ERROR_NOT_ALL_ASSIGNED (1300)`. ~5 linhas.
4. **Decidir sobre o `ReviewPanel`**: religar `ShowReviewPanel` ou apagar
   `AppsPage.xaml:958-1259` + `:1951-2470`. Código morto de 300+ linhas confunde
   toda auditoria futura.
5. **`captureBaseline: true`** no fluxo principal da página, para o diferencial
   confirmado-vs-heurístico existir de fato.

### Fazer depois

6. `Mime` + `ActivatableClasses` no scanner de `Classes\*` (única lacuna real
   frente ao Revo nesse scanner).
7. Consultar `IsExcludedPath` também na deleção (ou corrigir o comentário).
8. `displayName` no histórico dos cleanups vindos do review/junk.
9. Expor `ScannerMode` e `AutoCleanConfirmed` na janela de configurações.
10. Log de limpeza com severidade/origem + um visualizador na GUI (o
    `cleanup_*.log` é gravado e nunca aberto).
11. Abrir a aba "Extensões" ou removê-la.

### Não fazer

- TxF, ACE `Everyone`, driver `.sys`, SQLite, "investir" no filtro de 24 h (§6).

---

## 8. O que ficou inconclusivo

- **Scanner `Classes\*` do Revo (`sub_14018D6C0`, ~9 KB) nunca foi descompilado**
  — a lista de subchaves (`Mime`, `ActivatableClasses`) vem de strings, não de
  código (🟡). Antes de tratar `Mime`/`ActivatableClasses` como "paridade real",
  decompor essa função. Se ela não os usa, o item 6 do plano perde a base.
- **Marcadores `ADCU`/`ADAU`**: 0 xrefs de código no Revo (🟡). O formato e quem
  os escreve continua desconhecido; o KitLugia chega ao mesmo objetivo por outro
  caminho (baseline + heurística). Não há evidência de que valha portar.
- **Ponto de restauração no Revo**: não encontrei API de System Restore em
  nenhum binário. Não afirmar que o Revo cria ponto de restauração.
- **`RevoProcessDetector.sys`**: o helper executa `sc stop revoprocessdetector`,
  mas o que o driver vigia não foi analisado (32 KB, análise rápida se for útil).

---

## 9. Implementado (31/10/2026)

Todos os itens abaixo estão no código, com build limpo e verificação real. Nada
foi commitado.

| # | Mudança | Arquivo | Verificação |
|---|---|---|---|
| 1 | **Modo permanente agora apaga de vez.** As flags deixaram de ser "adicionar `RENAMEONCOLLISION`" e passaram a ser construídas por destino: `FOF_NOERRORUI` sempre, `+FOF_ALLOWUNDO` só na Lixeira. Também saiu `0x0020`, que estava batizada de `FOF_NODIRFLAGS` mas é `FOF_WANTMAPPINGHANDLE` (pedia um `hNameMappings` que ninguém liberava). | `RecycleBinOp.cs` | Probe de destino: antes `0x468` → **na Lixeira**; depois `0x408` → **não** (apagado de vez); `0x440` continua indo para a Lixeira. Revo 1044/1108 conferem com a regra. |
| 2 | **Take-ownership ligado no desinstalador.** `DeleteSubKeyTree` numa chave protegida agora tenta tomar posse e repetir, em vez de deixar o item órfão com um `FAILED` no log. Log marca `[key, apos take-ownership]`. | `DeepUninstaller.cs` (`SafeDeleteRegistryEntry`) | Build. Roda **depois** do guard `IsRegPathTooBroad`, então a escalada não vira atalho para raiz de sistema; o backup `.reg` já foi gravado antes. |
| 3 | **`EnableTakeOwnershipPrivilege` não mente mais.** `AdjustTokenPrivileges` devolve TRUE com `ERROR_NOT_ALL_ASSIGNED (1300)`; a função retornava sucesso nesse caso. Agora retorna `false` e loga o erro. | `RegistryOwnership.cs` | Build. Efeito: falha de posse aparece como falha, em vez de virar erro de acesso negado mais adiante. |
| 4 | **Exclusão do usuário passou a valer na deleção** (arquivos e registro). Antes só o scan consultava `IsExcludedPath` — o comentário afirmava o contrário. | `DeepUninstaller.cs` (`PerformCleanup`, `SafeDeleteRegistryEntry`) | Build + harness 18/18. |
| 5 | **Diff pré/pós ligado no fluxo principal.** `captureBaseline: true` nas duas chamadas da página, para a separação confirmado-vs-heurístico existir de fato. | `AppsPage.xaml.cs:937, 1218` | Build. |
| 6 | **Histórico com nome**, inclusive no modo automático: `displayName`/`installLocation` passados no auto-cleanup (usa `program.DisplayName`), no card por app (`item.AppName`) e na limpeza em lote (rótulo resumido: 1 app → nome; N apps → "N aplicativos (limpeza em lote)"). | `AppsPage.xaml.cs:¶1061, ¶1710, ¶1787` | Build. |
| 7 | **Checkbox do modo automático** na janela de configurações. `AutoCleanConfirmed` existia no Core e só era acessível pelo registro. O tooltip documenta o gate de 100 e o fail-closed. | `DeepUninstallSettingsWindow.xaml(.cs)` | Build (XAML compila). |

| 8 | **Hives de `Classes` que faltavam, com evidência de código.** O plano antigo apontava `sub_14018D6C0`, mas esse endereço cai no MEIO da função — a função real é `sub_14018D510`, e o scanner continua em `sub_14018DA70`. Decompilando `sub_14018DA70` e seguindo os xrefs das strings wide: ele referencia `L"ActivatableClasses"`, `L"SOFTWARE\\Classes\\MIME\\Database\\Content Type\\"`, `L"Record"`, `L"Applications"`, `L"Interface"`, `L"Mime"`, MuiCache e `Shell Extensions` (via `CStringArray::SetAtGrow`). Ou seja: **o Revo percorre Mime e ActivatableClasses de fato** — antes era 🟡 (só string), agora é ✅ (xref + pseudocódigo). Adicionadas ao Kit nos dois scans: por-valor (`guidHives*`, filhos são GUIDs → casa por `installLocation` no dado) e por-nome (`classPathsNominal`, `Applications` → filhos são nomes de exe). Hives: `ActivatableClasses`, `Applications`, `Record`, `MIME\Database\Content Type`, MuiCache (HKCU), + espelhos WOW6432Node no 64-bit. | `DeepUninstaller.cs` (`classPathsNominal`, `guidHivesNominal`, `guidHivesExtra`) | Core build 0 erros. Os dois scans são de 1 nível e exigem nome ≥70 OU valor referenciando o install path, então adicionar hives não aumenta falso positivo. |
| 9 | **Limpeza manual agora SEMPRE vai para a Lixeira.** `PerformCleanup` ganhou `forceToRecycleBin`; os botões manuais (limpar resíduos em lote, limpar por app, painel de review de arquivos) passam `forceToRecycleBin: true`, então `toBin=true` e o arquivo usa `FOF_ALLOWUNDO` (recuperável) **mesmo que o usuário tenha desligado "Enviar para a Lixeira"** nas configs. O modo permanente fica restrito ao auto-cleanup de 100% lixo. Backup continua obrigatório no modo lixeira (fail-closed). Mensagens de confirmação ajustadas ("Enviar para a Lixeira… / podrán ser restaurados"; bulk mostra destino por tipo). | `DeepUninstaller.cs` (`PerformCleanup`), `AppsPage.xaml.cs` (callsites manuais + textos) | Build GUI 0 erros + probe end-to-end chamando o `RecycleBinOp.Recycle` real: `permanent=false` → `Recycled` e **NA_LIXEIRA=True** (lido nos `$I*` de `C:\$Recycle.Bin\<SID>`); `permanent=true` → `DeletedPermanently` e NA_LIXEIRA=False. |

### Não implementado (com o motivo)

| Item | Por quê |
|---|---|
| `Shell Extensions` completo por valor | O Kit já escaneia `Shell Extensions\Approved` por nome (Batch 6). Os handlers reais moram em `CLSID`, que já é coberto por valor. Adicionar a raiz por valor não casaria caminho. |
| Religar `ShowReviewPanel` | Decisão de UX (volta a um fluxo de revisão antigo em vez da aba Resíduos). Não é correção de bug — precisa de escolha sua. |
| CLI headless (`/leftovers`) | Feature nova, não correção. O `KitLugia.Core` é biblioteca: dá para expor depois sem tocar no motor. |
| `ScannerMode` na UI | Precisa de combo (enum), não checkbox — junto com o religamento do review. |

## 10. Reprodução

```bash
# Flags contra shellapi.h (prova a leitura de §1 e o bug de §3)
python -c "..."   # ver skill reverse-engineering, case-study-revo §5

# Destino real de cada flag (exige NukeOnDelete=0)
#  - cria arquivo de nome unico, chama SHFileOperationW, procura o nome em
#    C:\$Recycle.Bin\<SID>\$I*  (o $I guarda o caminho original em UTF-16LE
#    a partir do offset 24)
#  - matriz: 0x460 / 0x468 / 0x420 / 0x414 / 0x454

# Inventario do codigo por simbolo
rg -n "ShowReviewPanel|captureBaseline|ForceTakeOwnership|ActivatableClasses" .
```
