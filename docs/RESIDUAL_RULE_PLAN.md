# Plano: ResidualRule (PC Manager) + Limpeza de Falsos Positivos

Baseado em `docs/PC_MANAGER_DEEP_UNINSTALL_ANALYSIS.md` (allowlist `ResidualRule`) e nos falsos positivos confirmados em `docs/REVO_UNINSTALLER_ANALYSIS.md` (Components de DDU, display.js, etc.).

---

## Objetivo

Implementar filtro de allowlist por app (`ResidualRule` JSON) no `DeepUninstaller`, para que o scan pós-desinstalação só ache resíduos de apps configurados — eliminando os falsos positivos de `Components\*` sem alterar o scanner global.

---

## CHECKLIST: Implementação

- [ ] `docs/residual_rules.json` — arquivo de regras (exemplo abaixo)
- [ ] `DeepUninstallSettings.cs` — adicionar `LoadResidualRules()` / `GetRulesForApp()`
- [ ] `DeepUninstaller.cs` — filtro `ApplyResidualRule()` antes de adicionar resultados ao `UninstallResult`
- [ ] `ScanLeftoverFiles` — gate `IsResidualApp()` usando `AppUninstallKey` + `InstallPathFolder`
- [ ] `ScanLeftoverRegistry` — mesmo gate para resultados de registro
- [ ] `UninstallHistory` — registrar `RuleApplied` (nome do arquivo/regra usada)
- [ ] `ReviewPanel` — mostrar no info se algum item foi filtrado por regra (`X itens filtrados por ResidualRule`)
- [ ] `DeepUninstallSettingsWindow` — botão "Editar Regras" (abre `residual_rules.json` no editor padrão)

---

## Exemplo `residual_rules.json`

```json
[
  {
    "AppName": "ZCode",
    "AppUninstallKey": ["zcode", "ZCode", "3.2.2"],
    "InstallPathFolder": ["Programs\\ZCode", "Local\\Programs\\ZCode"],
    "Depth": 3,
    "FileExtension": [".log", ".ini", ".db"],
    "FileCount": 50
  },
  {
    "AppName": "Display Driver Uninstaller",
    "AppUninstallKey": ["DDU", "Display Driver Uninstaller"],
    "InstallPathFolder": ["DDU", "DisplayDriverUninstaller"],
    "Depth": 2,
    "FileExtension": [],
    "FileCount": 100
  }
]
```

---

## CHECKLIST: Limpeza dos Falsos Positivos (Confirmados)

Os seguintes resultados falsos positivos foram confirmados nos testes (host + VM):

### 1. Registry — `Components` (DDU, Intel ME, Node.js/npm)
- [ ] Aplicar `AppUninstallKey` como filtro primário antes de aceitar resultados de `ScanHiveForNames` (mode 0) e `ScanHiveByValues` (mode 2)
- [ ] Confirmar que `Components` NÃO casa com `AppUninstallKey` de nenhum app real → removido automaticamente
- [ ] Manter `ScanInstallerComponentsByValues` separado (match por path, não por nome)

### 2. Registry — `Display` (Realtek Audio, Intel ME, display.js)
- [ ] Aplicar `FileExtension` vazio (`[]`) + `Depth` baixo como limite — `display.js` não tem `.exe` no nome
- [ ] Confirmar que `Display.ico` (sem `.exe`) é rejeitado pelo filtro de extensão (se `FileExtension` não incluir `.ico`)

### 3. Registry — `CLSID` Windows (`{101193C0...}`) com default `Display`
- [ ] Confirmar que `AppUninstallKey` do Windows (`Windows`, `Microsoft`) não está na allowlist → rejeitado
- [ ] Se algum CLSID real do app ainda aparecer, confirmar que `InstallPathFolder` casa com o `LocalServer32`/`InprocServer32`

### 4. Arquivos — `AppData` compartilhado (`%AppData%\Microsoft`)
- [ ] Confirmar que `Depth` limitado (ex: 3) evita varredura profunda
- [ ] Confirmar que `FileExtension` vazio + `Depth` baixo rejeita a maioria dos resíduos genéricos
- [ ] Adicionar regra específica para `Microsoft` (se necessário) com `AppUninstallKey` vazio → rejeita

---

## Validação (Testar no Host)

- [ ] Rodar `DeepUninstall` no `DDU` com `residual_rules.json` vazio → deve retornar 0 (ou só itens Safe confirmados)
- [ ] Rodar com `residual_rules.json` contendo `DDU` → deve achar os 4 itens legítimos (Tracing) sem os 4 falsos positivos (`Components`)
- [ ] Confirmar que `ReviewPanel` não mostra itens de `Components`
- [ ] Confirmar que `Batch multi-app` funciona com as regras aplicadas

---

## Ordem de Implementação Sugerida

1. Criar `residual_rules.json` vazio (estrutura válida)
2. Adicionar `LoadResidualRules()` no `DeepUninstallSettings`
3. Aplicar filtro no `ScanLeftoverFiles` (arquivo) — mais simples
4. Aplicar filtro no `ScanLeftoverRegistry` (registro) — confirma limpeza dos `Components`
5. Adicionar UI (botão "Editar Regras", info no ReviewPanel)
6. Testar em VM com `DDU` e `ZCode`
