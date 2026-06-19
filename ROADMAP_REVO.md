# Roadmap: Replicar Revo Uninstaller 1:1

## 📌 Princípios do Revo

| Revo | KitLugia | Implementação |
|------|----------|--------------|
| **Bold (Created)** = deletado | `SafetyLevel.Safe` + `Moderate` | Só itens com certeza alta são deletáveis |
| **Not Bold** = só info, NUNCA deleta | `SafetyLevel.Uncertain` | Checkbox existe mas é ignorada no delete |
| **Red** = excluído, nunca listado | `IsExcluded` | Lista de exclusão de chaves/pastas conhecidas |
| **"Mark as Created"** | `BtnMarkAsCreated_Click` | Usuário pode promover Uncertain → Moderate |
| **Pre-scan analysis** | Snapshot do registro ANTES do uninstall | Salvar estado antes de rodar o desinstalador |
| **Restore point** | Já tem | ✅ |
| **Backup .reg + Lixeira** | Já tem parcialmente | BackupRegistryKey existe |
| **3 níveis de scan** | Safe / Moderate / Advanced | Implementar os 3 modos |
| **Exclude list** | `Microsoft`, `Windows`, vendors | Expandir lista |

---

## 🔷 FASE 1: Fundação — `SafetyLevel`

### F1.1 Criar enum `CleanupSafety`

Arquivo: `KitLugia.Core/DeepUninstaller.cs` ou novo arquivo.

```csharp
public enum CleanupSafety
{
    Safe,      // Negrito, pré-selecionado, deleta com certeza
    Moderate,  // Semi-negrito, pré-selecionado, deleta
    Uncertain  // Normal, NÃO pré-selecionado, NÃO deleta (só informação)
}
```

### F1.2 Adicionar `SafetyLevel` ao `AppCleanupItem`

Arquivo: `AppsPage.xaml.cs` — classe `AppCleanupItem` (~linha 1125)

- Propriedade `SafetyLevel` (default `Safe`)
- Propriedade `IsBold` → `get => SafetyLevel != Uncertain`
- Propriedade `CanDelete` → `get => SafetyLevel != Uncertain`
- Propriedade `SafetyIcon` → `get => SafetyLevel switch { Safe => "🟢", Moderate => "🟡", _ => "🔴" }`

### F1.3 Modificar `PerformCleanup`: só deleta se `CanDelete == true`

Arquivo: `DeepUninstaller.cs` (~linha 1152)

- `PerformCleanup` recebe `List<AppCleanupItem>` em vez de `List<string>`
- No loop: `if (!item.CanDelete) continue;`
- Ou manter `List<string>` mas passar duas listas separadas (uma só com items deletáveis)

### F1.4 Reverter `_isSelected = true`, mas só pré-seleciona Safe/Moderate

- `_isSelected = true` novamente
- `IsSelected` get: se `SafetyLevel == Uncertain` → false (nunca pré-selecionado)

### F1.5 Build + teste

- Confirmar que Uncertain NÃO é deletado mesmo se o checkbox estiver marcado

---

## 🔷 FASE 2: Classificar cada item escaneado

### F2.1 `GetInstallLocationFromRegistry(displayName)`

Arquivo: `DeepUninstaller.cs`

- Procurar em `HKLM\...\Uninstall` e `HKCU\...\Uninstall` pelo display name
- Extrair `InstallLocation` se existir
- Usar como âncora para classificação

### F2.2 ScanLeftoverFiles: classificar cada arquivo/pasta

| Condição | Safety |
|---|---|
| Dentro do `installLocation` conhecido | **Safe** |
| `AppData\Local\{AppName}` match exato | **Safe** |
| `AppData\Roaming\{AppName}` match exato | **Safe** |
| Nome do app aparece no path completo | **Moderate** |
| Match Sift4 ≥85 | **Moderate** |
| Match Sift4 ≥70 e <85 | **Uncertain** |
| Dentro de `ProgramData` (qualquer match) | **Uncertain** |
| Caminho também referenciado por outro programa | **Uncertain** |

### F2.3 ScanLeftoverRegistry: classificar cada chave

| Condição | Safety |
|---|---|
| GUID/CLSID que aponta para installLocation | **Safe** |
| `HKCU\Software\{AppName}` match exato | **Safe** |
| `HKLM\...\Uninstall\{AppName}` | **Safe** |
| Match por nome ≥85 | **Moderate** |
| Match por nome ≥70 e <85 | **Uncertain** |
| Match só por valor (sem nome) | **Uncertain** |
| Fora de HKCU\Software (ex: HKLM\SYSTEM, Classes) | **Uncertain** |

### F2.4 ScanLeftovers retornar `List<AppCleanupItem>`

- Em vez de `(List<string> files, List<string> registry)`, retornar `(List<AppCleanupItem> files, List<AppCleanupItem> registry)`

### F2.5 Build + teste

- RustDesk: todos os itens classificados corretamente

---

## 🔷 FASE 3: Scanner mais inteligente

### F3.1 Adicionar `ScannerMode` enum

```csharp
public enum ScannerMode { Safe, Moderate, Advanced }
```

### F3.2 `ScanFolderConfidence` por modo

- **Safe**: só match exato + installLocation
- **Moderate**: + Sift4 ≥85
- **Advanced**: + Sift4 ≥70 (atual, com risco de falso positivo)

### F3.3 Pular `otherInstallLocations`

- `ScanLeftoverFiles` recebe `List<string> otherInstallLocations` de `GetAllInstallLocations(excludeName: displayName)`
- Se caminho começa com `otherInstallLocations[i]` → pular ou marcar Uncertain

### F3.4 Expandir `SystemFolderNames`

Adicionar vendors conhecidos:
- `Google`, `Mozilla`, `Adobe`, `Oracle`, `Apple`, `Package Cache`, `USOShared`, `USOPrivate`, `Temp`, `WinSxS`, `Assembly`, `Installer`, `MSBuild`, `Resources`, `servicing`

### F3.5 Build + teste

- Modo Safe não encontra falsos positivos

---

## 🔷 FASE 4: UI do Review Panel

### F4.1 3 visuais (🟢🟡🔴) + negrito

Arquivo: `AppsPage.xaml`

- Safe: fundo verde claro, negrito, pré-selecionado
- Moderate: fundo amarelo claro, negrito, pré-selecionado
- Uncertain: fundo normal, sem negrito, NÃO pré-selecionado, tooltip "Item informativo — não será deletado"

### F4.2 "Marcar como Criado"

- Botão de contexto (right-click) ou botão na barra
- Promove Uncertain → Moderate
- Aviso: "Tem certeza? Só marque se tiver absoluta certeza"

### F4.3 "Selecionar Tudo" só marca Safe/Moderate

- Revo behavior: Select All não marca Uncertain

### F4.4 Contador separado

- "5 itens serão deletados | 3 itens informativos"

### F4.5 Build + teste

- Review panel visualmente igual ao Revo

---

## 🔷 FASE 5: Pré-scan (Analyze step do Revo)

### F5.1 `SnapshotRegistryBeforeUninstall(displayName)`

- Antes de rodar o uninstaller nativo:
  - Salvar lista de subchaves de `HKCU\Software\{displayName}`
  - Salvar lista de subchaves de `HKLM\...\Uninstall\{displayName}`
  - Salvar caminhos de pasta conhecidos (installLocation, AppData match)

### F5.2 Comparação pré/pós

- Depois do uninstall, comparar com o estado pós
- O que SUMIU era do programa → certeza 100%
- O que SOBROU mas tem match → pode ser leftover (menos certeza)

### F5.3 Itens que sumiram = Safe automático

### F5.4 Build + teste

---

## 🔷 FASE 6: Segurança extra

### F6.1 Verificar backup .reg antes de deletar

- `PerformCleanup` já chama `BackupRegistryKey`
- Confirmar que backup foi criado com sucesso

### F6.2 Log de deleções

- `%LOCALAPPDATA%\KitLugia\deletion_log_{timestamp}.txt`
- Data, app, caminho deletado, safety level

### F6.3 "Restore Backup"

- Mostrar backups recentes e permitir restaurar

### F6.4 Build + teste completo

- Testar com RustDesk e apps reais

---

## 📋 Checklist Executável

```
[ ] F1.1  Criar enum CleanupSafety
[ ] F1.2  Adicionar SafetyLevel ao AppCleanupItem + IsBold + CanDelete
[ ] F1.3  Modificar PerformCleanup: só deleta se CanDelete == true
[ ] F1.4  Reverter _isSelected = true, mas só pré-seleciona Safe/Moderate
[ ] F1.5  Build + teste: Uncertain NÃO deletado mesmo se marcado
---
[ ] F2.1  Implementar GetInstallLocationFromRegistry(displayName)
[ ] F2.2  ScanLeftoverFiles classificar com SafetyLevel
[ ] F2.3  ScanLeftoverRegistry classificar com SafetyLevel
[ ] F2.4  ScanLeftovers retornar List<AppCleanupItem>
[ ] F2.5  Build + teste: RustDesk classificado corretamente
---
[ ] F3.1  Adicionar ScannerMode enum
[ ] F3.2  FolderConfidence por modo (Safe/Moderate/Advanced)
[ ] F3.3  Pular otherInstallLocations
[ ] F3.4  Expandir SystemFolderNames
[ ] F3.5  Build + teste: Safe mode sem falsos positivos
---
[ ] F4.1  UI: 3 visuais + negrito no ItemsControl
[ ] F4.2  UI: "Marcar como Criado" context menu
[ ] F4.3  UI: "Selecionar Tudo" só marca Safe/Moderate
[ ] F4.4  UI: contador separado
[ ] F4.5  Build + teste: review panel igual ao Revo
---
[ ] F5.1  SnapshotRegistryBeforeUninstall
[ ] F5.2  Comparação pré/pós
[ ] F5.3  Itens que sumiram = Safe automático
[ ] F5.4  Build + teste
---
[ ] F6.1  Verificar backup .reg antes de deletar
[ ] F6.2  Log de deleções
[ ] F6.3  UI: "Restore Backup"
[ ] F6.4  Build + teste completo
```
