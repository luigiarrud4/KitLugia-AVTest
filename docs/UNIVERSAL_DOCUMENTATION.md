# Documentação Universal do KitLugia

> Gerada automaticamente em 2026-11-02 pela skill `documentation-and-adrs`.

## Visão geral

O KitLugia é uma ferramenta desktop Windows de manutenção/otimização, com foco em desinstalação profunda (deep uninstall) segura, modo Hunter (seleção visual de janelas), limpeza de resíduos de registro e arquivos, e interface WPF.

### Stack

- C# 13 + WPF em .NET 10 (SDK 10.0.301)
- Rust `cdylib` em `rust_native/` via FFI
- UI em `KitLugia.GUI/Pages/AppsPage.xaml.cs`

## Arquivos principais

### DeepUninstaller.cs

- **Linhas**: 5654
- **Função**: componente nuclear
- **Responsabilidades**:
  - Executa desinstalação via uninstaller do app ou quiet uninstall.
  - Captura baseline prévia de diretórios e registro.
  - Varre resíduos (arquivos e registro) pós-desinstalação.
  - Classifica itens em `Safe`, `Moderate`, `Uncertain`.
  - Delega remoção para `RecycleBinOp` com `forceToRecycleBin`.
  - Suporta restore point, kill de processos e captura de pastas de dados.

### RecycleBinOp.cs

- **Linhas**: 163
- **Função**: componente auxiliar
- **Responsabilidades**:
  - Encapsula `SHFileOperationW` para lixeira/permanente.
  - Decide destino por `FOF_ALLOWUNDO` (Lixeira) vs ausência (permanente).
  - Trata `fAnyOperationsAborted` (cancelamento pelo usuário).
  - Alocação nativa manual para `SHFILEOPSTRUCT` (evita marshaling errado).

### RegistryOwnership.cs

- **Linhas**: 315
- **Função**: componente auxiliar
- **Responsabilidades**:
  - Implementa take-ownership de chaves protegidas do registro.
  - Habilita `SeBackupPrivilege`/`SeRestorePrivilege`.
  - Usado como fallback em deleções que falham com `UnauthorizedAccessException`.

### AppsPage.xaml.cs

- **Linhas**: 3004
- **Função**: componente interface principal
- **Responsabilidades**:
  - Página principal da UI (WPF).
  - Lista apps instalados, executa deep uninstall, hunter e limpeza manual.
  - Consome `DeepUninstaller`, `RecycleBinOp`, `RegistryOwnership`.

## Decisões arquiteturais relevantes

- Delegação de remoção de arquivos para `RecycleBinOp` com flags idênticas ao Revo (`FOF_ALLOWUNDO`, `FOF_NOERRORUI`).
- Baseline snapshot prévia para diferenciar resíduos confirmados de heurísticos (padrão Revo ADCU/ADAU).
- Zero falso-positivo como regra: limpeza automática só para itens com score 100 em `AutoCleanup`.
- Fail-closed: em caso de dúvida, não remove.

## ADRs existentes relacionadas

- `docs/KITLUGIA_VS_REVO_PARITY.md` — paridade com Revo Uninstaller, correção de flags da lixeira e plano priorizado.
- `docs/HUNTER_AUDIT.md` — auditoria do HunterWindow, correção de hit-test e integração com Force Stop.
