# Explorador de PATH - Guia e Plano de Expansao

> KitLugia - pagina Integridade > item PATH > botao "mais" (Explorador de PATH).
> Janela de ajuda: botao "i" no header do Explorador (PathGuideWindow).

## 1. O que a janela mostra

| Secao | Conteudo |
|---|---|
| PATH do SISTEMA (HKLM) | Valor cru editavel + entradas diagnosticadas + essenciais de sistema ausentes |
| PATH do USUARIO (HKCU) | Idem para a conta atual + programas instalados ausentes |
| Ausentes - recomendados | Candidatos com botao "Adicionar" (dedup automatico, pergunta se a pasta nao existe) |
| Abrir Editor do Windows... | `rundll32 sysdm.cpl,EditEnvironmentVariables` (janela nativa) |

## 2. Legenda de cores (PathEntryProblem -> cor)

Fonte: `PathExplorerWindow.ToRow` + `PathRepair.DiagnosePath`.

| Icone | Cor | Problema | Acao recomendada |
|---|---|---|---|
| ✅ | #4CAF50 verde | None - pasta existe | Manter |
| ⚠️ | #FFA500 laranja | Missing - pasta nao existe | Remover ou verificar instalacao (ex: `chocolatey\bin` sem Chocolatey) |
| 🔄 | #FFD700 dourado | WrongLocation - caminho de sistema no User PATH (ou vice-versa) | Mover para o PATH correto |
| 🔁 | #FF6F61 vermelho | Duplicate (case-insensitive) | Remover duplicata |
| 🧹 | #999999 cinza | Junk - vazia ou SDK interno .NET | Remover |
| 🗑️ | #999999 cinza | Orphan - residuo de desinstalacao | Remover |
| ❌ | #FF6F61 vermelho | SyntaxError - malformada | Remover ou corrigir |

Ordem dos checks em `DiagnosePath` (PathRepair.cs:123): vazia -> sintaxe ->
duplicata -> SDK .NET junk -> sistema-no-user / user-no-system -> `Exists` ->
Missing/Orphan -> OK.

## 3. Como o kit acha programas ausentes (cadeia de resolucao)

1. **Indexador nativo USN/MFT** (`NativeUsn.cs`, embutido, requer admin):
   - Lê a MFT do volume via `FSCTL_ENUM_USN_DATA`; cache so de DIRETORIOS
     (`dirCache` FRN->(Parent, NameIdx)); arquivos so casam nome contra `wanted`.
   - Sem cap por registro (volumes grandes >4M registros varrem completos -
     o cap antigo quebrava a cadeia de pais e dropava node/git/npm/cargo).
   - `ResolvePath` reconstroi o caminho pela cadeia de pais; so diretorios.
2. **DFS com skip de arvores** (`PathRepair.RecoverFromExecutableScan`): fallback
   sem admin - so para alvos ausentes (`onlyTargets`), skips de node_modules/
   .git/WinSxS/etc., single-flight com lock, preferencia 7-Zip sobre 7z generico.

Cache de resultados: `_scanCache` (TTL 5min), merge entre scans, nunca grava
cache vazio quando o scan nao rodou.

## 4. Arquivos envolvidos

- `KitLugia.Core\PathRepair.cs` - DiagnosePath, PathEntry/PathEntryProblem,
  AddSinglePathEntry, GetMissingSystemEntries, GetMissingInstalledEntries,
  RepairPathEntries, EnsureSystemPathMinimum/EnsureUserPathMinimum, indexador hook.
- `KitLugia.Core\NativeUsn.cs` - indexador nativo USN/MFT (dirCache, ResolvePath).
- `KitLugia.Core\Guardian.cs` - GetHarmfulTweaksWithStatus (scan 160ms com TTL 15s do bcdedit).
- `KitLugia.GUI\Windows\PathExplorerWindow.xaml(.cs)` - janela principal.
- `KitLugia.GUI\Windows\PathGuideWindow.xaml(.cs)` - janela de ajuda (botao "i").
- `KitLugia.GUI\Pages\IntegrityPage.xaml(.cs)` - pagina; item PATH mostra o botao "mais"
  via DataTrigger `IsPathItem`.

## 5. Ideias de expansao (proximo passo sugerido)

- [ ] **Acoes em lote**: selecionar N entradas e aplicar "remover duplicatas",
      "remover junk/orphan", "mover para o outro PATH" de uma vez.
- [ ] **Botao "Reparar tudo"** no Explorador: executa RepairPathEntries com
      confirmacao por categoria (nao so por item).
- [ ] **Backup/restore do PATH**: exportar System/User PATH para .reg/txt antes
      de qualquer mudanca; historico de adicoes (estilo UninstallHistory).
- [ ] **Snapshot comparativo**: guardar PATH bom e avisar quando instaladores
      corromperem (adições de lixo) - integrado ao Guardian.
- [ ] **Edicao inline**: TextBox editavel por entrada (nao so o valor cru).
- [ ] **Detectar `%VAR%` nao resolvida**: marcar entradas com variavel de
      ambiente indefinida (expandida vazia) como aviso novo.
- [ ] **Ordenacao inteligente**: sugerir reordenacao (mais usados primeiro) com
      aplicacao em um clique.

## 6. Performance (medicoes 16/08)

- Scan Guardian completo: ~160ms (462 tweaks, bcdedit cacheado TTL 15s).
- Indexador USN frio: 7 alvos em 7,2s; cache quente ~0ms.
- DFS frio: ~650ms-7s (depende de alvos ausentes); single-flight 2 scans = 1 scan.
- GetInstalledProgramPaths com tudo coberto: 0ms.

## 7. Sem Everything (17/08)

O `EverythingSearcher.cs` (SDK externo da voidtools) foi REMOVIDO - o kit e 100%
independente: so o indexador nativo USN/MFT (embutido) + DFS. A DLL
`Everything64.dll` foi removida do Resources. Menos codigo, menos IPC, zero
dependencia de processo externo.