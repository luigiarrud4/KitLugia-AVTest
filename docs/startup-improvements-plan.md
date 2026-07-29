# Startup Module — Análise e Plano de Melhorias

## 1. Bugs Corrigidos

### 1a. App some ao desabilitar (Action C / BruteForce) ✅
- `SetStartupItemState()` deletava `Run`/`RunOnce` values do registro ao desabilitar
- Agora só marca `StartupApproved` (como o Windows Task Manager faz)
- `BruteForceDisableStartup` também limpo

## 2. Problemas Estruturais

### 2a. DelegateToKitLugia deleta StartupApproved ⚠️
**Arquivo**: `StartupManager.cs`, `DelegateToKitLugia()`
**Problema**: Deleta `StartupApproved\Run` para o app ao mover para Turbo Boot, impedindo reativação futura.
**Fix**: Remover `DeleteValue` em `StartupApproved`.

### 2b. ConvertToAdmin remove antes de criar ⚠️
**Arquivo**: `ServicesPage.xaml.cs`, `MenuConvertToAdmin_Click` e `MenuConvertToAdminDelayed_Click`
**Problema**: Chama `RemoveStartupItem` antes de `CreateElevatedStartupTask`. Se criar falhar, app some.
**Fix**: Criar task primeiro, remover depois.

### 2c. Race condition no cache da UI ⚠️
**Arquivo**: `ServicesPage.xaml.cs`
**Problema**: `_allStartupApps` lido na UI thread e escrito via `Task.Run` sem lock.
**Fix**: Adicionar `lock` ou `Interlocked.Exchange`.

### 2d. RemoveStartupItem não limpa StartupApproved ⚠️
**Arquivo**: `StartupManager.cs`, `RemoveStartupItem()`
**Problema**: Apaga o Run value ou .lnk, mas deixa `StartupApproved` órfão.
**Fix**: Limpar `StartupApproved\Run` e `StartupApproved\StartupFolder` após remoção.

### 2e. Add Normal usa PowerShell COM — risco de injeção ⚠️
**Arquivo**: `ServicesPage.xaml.cs`, `BtnAddSave_Click`
**Problema**: Concatena `appName` e `exePath` em script PowerShell sem escapar.
**Fix**: Usar `IShellLink` (Win32 nativo) ou `IWshRuntimeLibrary` via .NET.

### 2f. Turbo Boot toggle: desabilitar não funciona ⚠️
**Arquivo**: `StartupManager.cs`, `SetStartupItemState()`
**Problema**: App em Turbo Boot (KitLugia registry) não está no Run/folder. Desabilitar escreve `StartupApproved` mas não impede o Turbo Boot de iniciar.
**Fix**: Detectar Turbo Boot e remover da chave KitLugia (ou mover para `AutorunsDisabled`).

### 2g. Duplicate name detection ⚠️
**Arquivo**: `ServicesPage.xaml.cs`, `BtnAddSave_Click`
**Problema**: Ao adicionar app, se já existe atalho com mesmo nome, sobrescreve sem aviso.
**Fix**: Verificar existência e perguntar ao usuário.

## 3. Edge Cases Não Tratados

### 3a. App duplicado em Run + Task Scheduler ⚠️ NÃO CORRIGIDO
App pode estar `HKCU\...\Run` E ter tarefa no Task Scheduler (ex: OneDrive, Google Update).
- Converter para Admin remove do Run mas esquece da task externa
- App inicia duas vezes após boot
**Necessário**: verificar existência de task externa antes de converter

### 3b. App em Startup Folder + Run registry
BuildAppList deduplica, mas ao reabilitar, StartupApproved\Run volta como enabled.
**Estado atual**: Funciona, mas frágil.

### 3c. App Turbo Boot Non-Admin convertido para Elevated ✅ MITIGADO
NonAdmin task deletada, Elevated criada.
**Problema original**: Se falha no meio, app some.
**Mitigação**: ConvertToAdmin agora cria task PRIMEIRO, remove só depois.

## 4. Melhorias de UX

### 4a. Backup antes de remover permanentemente
`RemoveStartupItem` deveria salvar backup em `HKCU\...\KitLugia\Backup\{appName}` antes de deletar.

### 4b. Botão "Restaurar itens removidos"
Interface para restaurar apps do backup ou de `AutorunsDisabled`.

### 4c. Indicador de confiança para startup apps
Assim como serviços têm `ServiceSafetyLevel`, apps de startup poderiam ser classificados.

## 5. Estado Atual

- [x] 1a. App some ao desabilitar (Action C removida, BruteForce limpo)
- [x] 2a. DelegateToKitLugia — parar de deletar StartupApproved (agora marca como disabled)
- [x] 2b. ConvertToAdmin — criar task antes, remover depois
- [x] 2c. Lock no cache _allStartupApps (lock + snapshot)
- [x] 2d. RemoveStartupItem limpar StartupApproved (CleanStartupApproved + helper)
- [x] 2e. PowerShell injection — migrado para COM seguro via dynamic/WScript.Shell
- [x] 2f. Turbo Boot toggle desabilitar (novo Case 1-B em SetStartupItemState)
- [x] 2g. Duplicate name detection (confirmação ao adicionar)
- [x] 4a. Backup antes de remover (BackupStartupItem, HKCU\...\KitLugia\RemovedApps)
- [x] 4b. Restaurar itens removidos (RestoreRemovedItem + GetRemovedApps)
