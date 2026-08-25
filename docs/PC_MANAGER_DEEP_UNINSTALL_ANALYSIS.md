# Análise do Deep Uninstall do Microsoft PC Manager (Store)

Data: 05/08/2026
Método: decompilação do plugin `Microsoft.WIC.PCManager.Plugin.Uninstall.dll` (v3.22.3.0)
via ilspycmd (`.NET`). O binário foi copiado de
`C:\Program Files\WindowsApps\Microsoft.MicrosoftPCManager_3.22.3.0_x64__8wekyb3d8bbwe`
para `%TEMP%\opencode\pcmp`. Fonte: `%TEMP%\opencode\pcmp\src\uninstall\...decompiled.cs` (8578 linhas).

## Conclusão principal

**O PC Manager NÃO usa snapshot pré/pós global** (diferente do Revo Uninstaller).
O "Deep Uninstall" (resíduos) é um scan **pós-only, focado na pasta de instalação do
próprio app** + **chave Uninstall do registro**, e só roda para apps marcados por uma
**allowlist configurável** (cloud config). Isso valida a reforma feita no KitLugia
(remoção do snapshot global pré-desinstalação → `captureBaseline: false`).

## Arquitetura (classes reais do plugin)

### 1. Enumeração de apps (PeekAppListService / PeekAppInfoFromRegistry)
- Lista de apps vem SOMENTE das 4 chaves Uninstall do registro:
  - `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`
  - `HKLM\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall`
  - `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`
  - `HKLM\...` (MSI)
- `GetAppInfoFromSubkey` exige: DisplayName não-vazia, UninstallString presente,
  `IsSystemComponent==false` (SystemComponent=1/SystemComponent=0 omitido é skippado),
  e `InstallLocation` (ou diretório do uninstaller) contendo pelo menos 1 `.exe`.
- `InstalledPath` deriva de: `InstallLocation` reg value → senão `Path.GetDirectoryName(UninstallString)`.
- UWP: `GetAppInfoFromPackage` usa `package.InstalledLocation.Path` (não varre AppData).

### 2. Scanner de arquivos (FileScanner.GetUninstallFileTrash)
- **Escopo**: apenas `appInfo.InstalledPath` (BFS, profundidade máxima **9**, regra
  `Depth` do ResidualRule; padrão 9 se Depth==0).
- **NÃO varre** AppData\Local, AppData\Roaming, ProgramData, Start Menu etc de forma
  global. Para achar pastas de dados o PC Manager depende de regras por app
  (`ResidualRule.InstallPathFolder`), não de varredura ampla.
- Pula diretórios simbólicos (ReparsePoint).
- Filtro opcional por extensão de arquivo (`ResidualRule.FileExtension`), via
  `StringMatchUtil.IsMatch` (wildcard `*`, case-insensitive, DP).

### 3. Scanner de registro (RegistryScanner.GetUninstallRegistryTrash)
- Só deleta **a própria chave Uninstall do app** (`BaseKeyType\MiddleKey\EndingKey`),
  ex.: `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{GUID}`.
- Implementação do delete com fail types por item:
  `RegistryKeyNotExist`, `NoPermission`, `UnKnown`.

### 4. Regras por app (UninstallOptionsProvider / ResidualRule)
- **Allowlist**: só apps cujo sufixo da chave Uninstall (`EndingKey`) bate com um
  `ResidualRule.AppUninstallKey` da config são marcados `IsResidualApp=true`
  (`MarkResidualApp`).
- `ResidualRule`: `AppUninstallKey[]`, `InstallPathFolder[]`, `Depth`, `FileExtension[]`,
  `FileCount`, `EmptyFolder`.
- Config vem de `UninstallOptions.json` em common config path, ou serviço de config
  remoto (`_configManager.GetLocalConfigAsync<UninstallOptions>()`), com reload dinâmico.
- Guardas de exibição (`ShowResidual`):
  - só roda se `IsResidualApp`
  - `InstalledPath` precisa casar `InstallPathFolder`
  - `scanResult.Count() <= FileCount` (proteção contra scans absurdos)
- Sample rate: `UninstallResidualPopupEnableRate` (feature popup é A/B testada).

### 5. Deleção (ResidualDeleter / FileItem / RegistryItem)
- `FileItem.Delete()` → `FileOperator.SafeDelete` com fail types:
  `IsOccupied`, `PathTooLong`, `FolderNotExist`, `NoPermission`, `UnKnown`.
- `RegistryItem.Delete()` → abre `MiddleKey` com write e `DeleteSubKey(EndingKey)`.
- Progresso reportado via observer (0→95%, Complete=100%) + telemtria
  (`WM_Uninstaller_Residual_Scan` / `WM_Uninstaller_Residual_Delete`).

### 6. Monitoração (UninstallRegistryMonitor)
- Monitora `RegNotifyChangeKeyValue` nas 2 chaves HKLM Uninstall + 1 HKCU para
  detectar a remoção da chave (confirma desinstalação) e disparar o popup
  "resíduos encontrados".
- `DesktopAppUninstaller`: valida `PathUninstaller` existe → executa (com
  `QuietUninstallString` se `IsSilence`) → verifica chave sumiu; se não sumiu,
  agenda recheck async.

### 7. Extra (CleanupApps / CmdApplication)
- Lista hardcoded de apps com cleanup especial: QQ游戏, 7-Zip (Uninstall.exe). LOL.
- `CmdApplication`: MsiExec.exe, SunloginClient.exe, ActiveX.exe (exec via cmd).

## Implicações para o KitLugia

1. **Nossa reforma está alinhada**: remover o snapshot global pré-desinstalação e usar
   scan pós-only focado é exatamente o que a MS faz.
2. **Opcional (futuro)**: adotar allowlist de regras por app (arquivo JSON com
   `AppUninstallKey`/`InstallPathFolder`/`Depth`/`FileExtension`/`FileCount`) para o
   `ScanUwpLeftovers`/`LeftoverJunkManager` — permite "focar" em pastas de dados reais
   (AppData\Roaming\<app>, etc.) sem varredura global.
3. O KitLugia já tem o conceito de "focused post-scan" — pode crescer para um modelo
   híbrido: UWP via Packages + regras opcionais por app.

## Artefatos
- Plugin decompilado: `%TEMP%\opencode\pcmp\src\uninstall\Microsoft.WIC.PCManager.Plugin.Uninstall.decompiled.cs`
- Binários: `%TEMP%\opencode\pcmp\` (copy do InstallLocation MSIX)
- ilspycmd instalado globalmente (`.NET tool`, `ilspycmd`).

Obs: IDA Pro não é a ferramenta certa para assemblies .NET gerenciados — usar ilspy/dnSpy.