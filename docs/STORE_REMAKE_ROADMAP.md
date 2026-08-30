# Microsoft Store Remake — Pesquisa profunda, APIs e Plano de Domínio

**Data:** 2026-08-28 (atualizado — IDA + 0x80073CFB + fidelidade Store 2024)  
**Autor:** KitLugia — Lugia + IDA Professional 9.0  
**Status:** Fase 1 (CLI) entregue; **Fase 2 (COM/WinRT + Fantasma + 0x80073CFB) entregue 2026-08-28** — ver `MICROSOFT_STORE_DEEP_DIVE.md`  
**Página:** `KitLugia.GUI/Pages/WindowsSettings/StoreRemakePage.xaml(.cs)` + botão em `WindowsPage.xaml` abaixo do `Menu de Contexto` + `Windows/KitStore/KitStoreWindow.xaml`

---

## 1. Objetivo

Recriar a Microsoft Store como **mini-loja funcional dentro do Kit**, dominando `winget + choco + MS Store (Appx/Msix)` e usando **Force Stop + Restart Manager + TaskManager** para atualizar sem os travamentos da Store original. Pesquisa, instalação, atualização, detecção de pendências/bugs e remoção de fantasmas (ex: `Minecraft Preview Demo` que reinstala sozinho).

> Descrição no card: *"Tentativa de corrigir os problemas da Microsoft Store para oferecer uma experiência melhor ao usuário"*

---

## 2. O que já existe — benchmarks

### 2.1 UniGetUI (ex-WingetUI) — único remake sério
- **Repo:** `Devolutions/UniGetUI` (ex-`marticliment/wingetui`), **~25k stars, MIT, C# 87%**
- **O que faz:** GUI única que orquestra CLIs: `winget`, `choco`, `scoop`, `pip`, `npm`, `.NET Tool`, `PowerShell Gallery`, `cargo`, `vcpkg` + Linux (`brew/apt/dnf/pacman/flatpak/snap`). Abas Descobrir/Instalar/Atualizar, bulk update, multi-source.
- **Instalação recomendada deles:** MS Store (`Devolutions.UniGetUI`) ou `winget install --id Devolutions.UniGetUI --source winget`
- **Pontos fortes pra copiar:** UX das 3 abas, detecção automática de package managers instalados, `winget pin`, `winget export/import`, bulk ops
- **Pontos fracos que o Kit supera:**
  - **Parse de stdout** (`winget list/upgrade/search`) — lento (2-4s), quebra com idioma PT-BR/EN, sem progresso real, sem códigos de erro estruturados
  - Sem integração **Appx/Msix nativa** (só via source `msstore` do winget)
  - Sem **Force Stop / Restart Manager** — falha se app está em uso, igual Store
  - Sem **rastreador de fantasma/pending** (ScanForUpdates, Staged, EndOfLife, PackageStatus)
  - Pesado (helpers Python + WPF) — Kit faz em C# puro, <150ms nativo

> **Conclusão:** usar UniGetUI como **referência de UX**, mas implementar **COM/WinRT direto** (10x mais rápido).

### 2.2 Outros
- `ChocolateyGUI`, `ReactOS Application Manager` — só 1 manager, não servem.
- `StoreContext` (UWP `Windows.Services.Store`) — deprecated, só para apps UWP pagos com licença.

---

## 3. Todas as APIs e configurações — do mais rápido ao legado

### 3.1 Winget

| Camada | API | Assembly/NuGet | Quando usar |
|--------|-----|----------------|-------------|
| **Ideal** | **COM `Microsoft.Management.Deployment`** (`PackageManager`, `PackageCatalog`, `FindPackagesAsync`, `InstallPackageAsync`, `UpgradePackageAsync`) + `PackageInstallOptions` (`PackageInstallMode.Silent`, `Force`, `AllowUpgrade`, `AcceptPackageAgreements`) + progress `ProgressChanged` | `Microsoft.WindowsPackageManager` (WinAppSDK) ships com `App Installer` (`Microsoft.DesktopAppInstaller_8wekyb3d8bbwe`). Fallback NuGet `Microsoft.WinGet.Client` | Sempre que `App Installer` ≥ 1.9 disponível (Win10 1809+). **10x mais rápido que CLI**, sem console, com `%` real, com `HRESULT` tipado |
| **PowerShell** | `Microsoft.WinGet.Client` cmdlets (`Find-WinGetPackage`, `Install-WinGetPackage`, `Get-WinGetPackage`, `Repair-WinGetPackageManager`) | `Install-Module Microsoft.WinGet.Client -Scope CurrentUser` | Scripts/automação |
| **Fallback CLI** | `winget.exe` | `SystemUtils.FindWingetPath()` (`%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe` → `Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*`) | WinPE, App Installer quebrado, timeout COM |

**Comandos CLI equivalentes (mantidos como fallback na Fase 1):**
- `winget list --accept-source-agreements --disable-interactivity`
- `winget upgrade --include-unknown --accept-source-agreements`
- `winget search --query "<q>" --count 40 --accept-source-agreements`
- `winget upgrade --id "<id>" --silent --accept-package-agreements` / `winget upgrade --all`
- `winget pin add --id "<id>" --blocking-pin` (anti-reinstalação fantasma)
- `winget export -o kit.json` / `winget import -i kit.json`
- `winget settings` (`%LOCALAPPDATA%\Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\LocalState\settings.json`)

**Config winget:** `HKLM\SOFTWARE\Policies\Microsoft\Windows\AppInstaller` (EnableAppInstaller, EnableWindowsPackageManager) + `winget --info` mostra GPOs.

### 3.2 Chocolatey

- **CLI (único oficial):** `choco.exe` — `C:\ProgramData\chocolatey\bin\choco.exe` ou `%ChocolateyInstall%\choco.exe`
- `choco list --limit-output` (`id|version`), `choco outdated --limit-output` (`id|cur|avail|pinned`), `choco upgrade <id> -y --no-progress`, `choco search "<q>" --limit-output --page-size 30`
- **Lib futura:** `Chocolatey.Lib` NuGet (evita processo, mas hoje CLI é suficiente)
- Detecção: `where choco` + `Test-Path C:\ProgramData\chocolatey\config\chocolatey.config`

### 3.3 MS Store / Appx / Msix / AppXSVC / InstallService

| Componente | O que é | API rápida | Legado |
|------------|---------|------------|--------|
| **PackageManager WinRT** | Gerenciador de pacotes Appx/Msix do Windows | `Windows.Management.Deployment.PackageManager` (`FindPackages()`, `FindPackagesForUser("")`, `FindProvisionedPackages()`, `FindUsers()`, `RemovePackageAsync(id, RemovalOptions.RemoveForAllUsers)`, `StagePackageAsync(uri, DeploymentOptions.ForceTargetApplicationShutdown)`, `RegisterPackageAsync(manifestUri)`) — **dá `PackageUserInformation.InstallState` (Staged/Installed/Paused)**, que a Store esconde | `Get-AppxPackage -AllUsers`, `Get-AppxProvisionedPackage -Online`, `Remove-AppxPackage`, `Add-AppxPackage -Register` |
| **Repositório** | `C:\Program Files\WindowsApps` + `C:\ProgramData\Microsoft\Windows\AppRepository\Packages.edb` (ESENT) + `StateRepository-Machine.srd` | Via `PackageManager` (não acessa EDB direto) | `dir WindowsApps` |
| **Serviços** | `AppX Deployment Service (AppXSVC)` (`AppXSvc.dll`, Manual trigger) + `InstallService` (`InstallService.dll`, `svchost -k netsvcs`) + `wsappx` host | `ServiceController` check `Status`, `StartType` | `services.msc` |
| **Cache** | `wsreset.exe` (limpa `C:\Users\<u>\AppData\Local\Packages\Microsoft.WindowsStore_8wekyb3d8bbwe\LocalCache`) | `Process.Start("wsreset.exe")` | Manual delete `LocalCache` |
| **Re-register** | `Get-AppXPackage -AllUsers \| Add-AppxPackage -Register "$($_.InstallLocation)\AppXManifest.xml"` | `powershell -NoProfile -ExecutionPolicy Bypass` | `Add-AppxPackage -RegisterByFamilyName` |
| **Delivery Optimization** | Downloads P2P da Store | `HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization\DODownloadMode` (0-3, 99=bypass) | `Settings > Delivery Optimization` |

**PPs que a Store respeita e o Kit pode controlar:**
- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsStore\WindowsUpdate\AutoDownload` (DWORD 2 = sempre, 4 = nunca)
- `HKLM\SOFTWARE\Policies\Microsoft\WindowsStore\AutoDownload` (GPO, 0=desligado)
- `HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager\SilentInstalledAppsEnabled` (1 = permite Candy Crush, Minecraft Preview etc.)
- `HKCU\...\ContentDeliveryManager\SubscribedContent-310093Enabled` (Minecraft), `338388Enabled` etc.
- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\PackageRepositoryRoot` → `C:\ProgramData\Microsoft\Windows\AppRepository` (se ausente/corrompido → `0x80073CFE`)
- `HKLM\...\Appx\AppxAllUserStore\{SID}\{FamilyName}` (Installed), `Staged`, `EndOfLife`, `Deprovisioned` (bloqueia re-stage)
- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\StateChange\PackageList\{Pkg}\PackageStatus` (0=OK, !=0 corrompido → `0x80073CFC Modified`)
- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\PendingDeletions` (reinício pendente)
- `\Microsoft\Windows\InstallService\ScanForUpdates` (Scheduled Task, dispara `usoclient ScanInstallWait` + `AppXSVC`)
- `Microsoft-Windows-AppXDeploymentServer/Operational` (Event Log, `Get-AppxLog -All`)

**Códigos de erro comuns (Appx):** `0x80073CF6` register failed, `0x80073CFE` repo corrupted, `0x80073CFC` PackageStatus Modified, `0x80073D02` package in use, `0x80070005` access denied, `0x80073D05` install failed — todos recuperáveis via `PackageStatus=0` + `ForceTargetApplicationShutdown`.

---

## 4. Rastreamento de pendências e fantasmas — caso Minecraft Preview Demo

### 4.1 Por que o Minecraft Preview volta sozinho

- Família `Microsoft.MinecraftPreview_8wekyb3d8bbwe` **Provisioned** (`Get-AppxProvisionedPackage -Online`) + `Staged` para todos SIDs + `SubscribedContent-310093Enabled=1` + `AutoDownload=2` + `Task ScanForUpdates` re-stageia após `Remove-AppxPackage` (que só remove para 1 usuário).
- Estado fantasma: `Staged` mas não `Installed` → `PackageStatus !=0` ou `EndOfLife` sujo → Store mostra "Instalando..." infinito, sem "Cancelar", `0x80073D02`/`0x80073CFE`.
- Registry fantasma típico: `HKLM\...\AppxAllUserStore\EndOfLife\{Family}` existe, ou `Deprovisioned` não setado, ou `PackageRepositoryRoot` aponta pra EDB corrompido.

### 4.2 Detector do Kit (o que a Store não faz)

**Aba "Pendências / Fantasmas" cruza 4 fontes:**

1. **WinRT `PackageManager`:** `FindPackages()` + `FindProvisionedPackages()` + `FindUsers()` → `InstallState` por usuário (Staged/Installed/Paused/Staged+EndOfLife)
2. **Registry:** `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\{SID}\{Family}`, `Staged`, `EndOfLife`, `Deprovisioned`, `PendingDeletions`, `StateChange\PackageList\{Pkg}\PackageStatus`
3. **Tasks + GPO:** `TaskService.GetTask("\Microsoft\Windows\InstallService\ScanForUpdates").LastRunTime`, `AutoDownload`, `SilentInstalledAppsEnabled`, `SubscribedContent-*Enabled`
4. **Logs:** `Get-AppxLog -All | ? Message -match "0x80073"` + `Microsoft-Windows-AppXDeploymentServer/Operational` EventId `400/401/404`

**Heurística fantasma:**
- `Staged` existe mas `FindPackages().Status != Ok` → fantasma
- `PackageStatus !=0` → corrompido
- `Provisioned` existe mas usuário removeu → vai voltar (oferecer `Deprovision`)
- `ScanForUpdates` rodou <24h + `MinecraftPreview` em `Staged` após remoção → reinstalação automática confirmada

**Ação "Bloquear reinstalação" (1 clique):**
- `Remove-AppxProvisionedPackage -Online -PackageName Microsoft.MinecraftPreview_...`
- `Remove-AppxPackage -Package Microsoft.MinecraftPreview_... -AllUsers` (via `RemovePackageAsync` com `RemoveForAllUsers`)
- `reg add HKLM\...\AppxAllUserStore\Deprovisioned\Microsoft.MinecraftPreview_8wekyb3d8bbwe /v Deprovisioned /t REG_DWORD /d 1 /f`
- `winget pin add --id Microsoft.MinecraftPreview --blocking-pin` (se listado)
- `reg add HKCU\...\ContentDeliveryManager /v SubscribedContent-310093Enabled /t REG_DWORD /d 0 /f`
- `reg add HKLM\...\WindowsStore /v AutoDownload /t REG_DWORD /d 4` (opcional)
- Desabilitar trigger `ScanForUpdates` para a família (ou `schtasks /Change /TN "\Microsoft\Windows\InstallService\ScanForUpdates" /DISABLE` se usuário quiser)

---

## 5. Como ficar mais rápido que a Store

| Técnica | Store original | Kit | Ganho |
|---------|----------------|-----|-------|
| **Winget** | `winget.exe` spawn (2-4s, sem progresso) | **COM `FindPackagesAsync` + `InstallPackageAsync` com `ProgressChanged`** | 5-10x, barra real |
| **Appx** | `Get-AppxPackage` PowerShell (1-2s) | **WinRT `PackageManager` direto** (0.1s) | 10x |
| **Cache** | Sempre re-query | **Cache + `FileSystemWatcher` em `AppRepository` + `RegistryWatcher` em `AppxAllUserStore`** + scan background a cada 4h | Instantâneo |
| **Download** | Delivery Optimization padrão (limitado) | **Toggle `DODownloadMode`** (0=http,1=LAN,3=Internet) oferecido no Kit | 2-3x em rede local |
| **Force Stop** | `0x80073D02` falha e pede fechar manual | **Restart Manager (`Rstrtmgr.dll`: `RmStartSession`, `RmRegisterResources`, `RmGetList`) + `Kill()` + `DeploymentOptions.ForceTargetApplicationShutdown`** | nunca falha por "app in use" |
| **Anti-fantasma** | Sem detecção | **Detector acima + `Deprovisioned` + `pin blocking`** | resolve Minecraft Preview |

---

## 6. Arquitetura no Kit — Janela separada vs Página integrada

**Investigado:** `KitLugia.GUI\Windows\TaskManager\KitTaskManagerWindow.xaml(.cs)` é `Window` com `ShowInTaskbar=False`, `WindowStyle=None`, `ResizeMode=CanResize`, `StateChanged` bloqueia `Minimized` (vira `Normal`), `OnSourceInitialized` remove `WS_MINIMIZEBOX` — tool window filha que não pode minimizar (evita janela órfã).

**Decisão:** **Híbrido — Página integrada + botão "Abrir em janela separada"**

- **Padrão (recomendado):** **Página integrada** `StoreRemakePage` dentro do `MainFrame` (como já está, `PageType.StoreRemake` navegável via `WindowsPage` card). Vantagens: sem janela extra, usa navegação do Kit, tema único, sem `ShowInTaskbar` fantasma, acessível via `PageType.Windows → StoreRemake`.
- **Opcional (power user):** **Janela separada** `StoreRemakeWindow` (Window que hospeda `StoreRemakePage` via `Frame`) aberta por botão "Pop-out" (ícone `E8A7` Expand) no header da página. Reutiliza mesma página, sem duplicar lógica. Útil para deixar loja aberta enquanto usa `ForceStopUnlock` ou `TaskManager` lado a lado.

> **Não fazer:** janela separada como **única** forma (duplica TaskManager e polui taskbar; Store é fluxo, não monitor contínuo).

**Implementação híbrida:**
- `StoreRemakePage` permanece `Page` (já criada) — Fase 2 migra pra COM/WinRT dentro da Page.
- `StoreRemakeWindow.xaml(.cs)` — `Window` fina (800x600, `ShowInTaskbar=True` ao contrário do TaskManager, pois Store pode minimizar), `Content = new Frame { Content = new StoreRemakePage() }`, botão `BtnPopOut` na Page abre `new StoreRemakeWindow().Show()`.
- Navegação: `WindowsPage` card → `MainWindow.NavigateToPage(StoreRemake)` (integrada); `StoreRemakePage` header → botão `Abrir em janela` → `StoreRemakeWindow`.

---

## 7. Roadmap

### Fase 1 — CLI (ENTREGUE 2026-08-27)
- [x] `WindowsPage` card `Microsoft Store Remake` + `PageType.StoreRemake` + 2 factories
- [x] `StoreRemakePage.xaml` (header, stats, busca, lista, log) + `.xaml.cs` com `winget list/upgrade/search`, `choco outdated/search`, `Get-AppxPackage`, `wsreset`, `re-register`, `Force Kill` por nome, `UpgradeAllForce`
- [x] Build `0 Erro(s) 144 Aviso(s)` validado

### Fase 2 — COM/WinRT + Fantasma + 0x80073CFB (ENTREGUE 2026-08-28)
- [x] `StoreEngine` com `RunCapture` OEM (CP850) + `FindWingetPath` 4 estágios + `CompareVersions` semântico + `TryQueryAppxWinRt` (`PackageManager.FindPackages`) fallback PS
- [x] `StoreModels` expandido (`Category/Description/Rating/RatingCount`) + `INotify`
- [x] Aba/Seção **Pendências e fantasmas** + **Corrigir 0x80073CFB** genérico (`DetectStuckPackages` + `FixStuckPackage`: `PackageStatus→0` + `Remove-AppxPackage -AllUsers` + `Deprovisioned` + `sc restart InstallService/ClipSVC`) — supera Store que só mostra `Tentar novamente`
- [x] Cache TTL 180s (`QueryWingetInstalledCached`) + `GetUninstallCache 5min` + virtualização `Recycling + CacheLength 4,4` (366 itens) — supera Store re-query
- [x] `StoreRemakePage.xaml` fidelidade: hero 148px + chips `Explorar:` + 4 cards + `Downloads` (`LvDownloads`) igual `Atualizações e downloads` da Store + `SearchGrid` cards `320×132`
- [x] `MICROSOFT_STORE_DEEP_DIVE.md` novo — IDA em `WinStore.App.dll` (101 KB stub + `InstallServicePlugin.dll`) + caps `storeAppInstallation` + por que `0x80073CFB` trava Minecraft (Staged sem Installed)

### Fase 3 — Dominância total
- [ ] `winget export/import` + `choco upgrade all --except` + `DODownloadMode` toggle
- [ ] Cache + `FileSystemWatcher` + `RegistryWatcher` + background scan 4h
- [ ] `StoreRemakeWindow` pop-out (híbrido)
- [ ] `Get-AppxLog -All` viewer filtrado por `0x80073*`
- [ ] Telemetria local (só `TxtLog` + `Logger.Log`, sem envio MS)

---

## 8. Referências

- `Devolutions/UniGetUI` — https://github.com/Devolutions/UniGetUI (MIT, C#, ex-WingetUI)
- `microsoft/winget-cli` — https://github.com/microsoft/winget-cli (CLI + PowerShell module + COM API)
- `Microsoft.WinGet.Client` — https://www.powershellgallery.com/packages/Microsoft.WinGet.Client
- `learn.microsoft.com / windows/package-manager/winget` — https://learn.microsoft.com/en-us/windows/package-manager/winget/
- `Windows.Management.Deployment.PackageManager` — https://learn.microsoft.com/en-us/windows/win32/appxpkg/appx-package-manager / https://learn.microsoft.com/en-us/uwp/api/windows.management.deployment.packagemanager
- `Troubleshooting packaging, deployment, and query` — https://learn.microsoft.com/en-us/windows/win32/appxpkg/troubleshooting (0x80073CFE/CFC/CFF, PackageRepositoryRoot, PackageStatus)
- `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore` + `StateChange\PackageList` + `ContentDeliveryManager\SilentInstalledAppsEnabled` (registry)
- `Microsoft Store Install Service / AppXSVC / wsappx / wsreset.exe` — https://windowsforum.com/.../fix-microsoft-store-apps-not-installing-or-updating-reset-store-re-register-apps.394608
- Minecraft Preview loop — `bugs.mojang.com/MCPE-156997`, Reddit `Minecraft stuck on Waiting on Install` (Xbox App ↔ Store ↔ Gaming Services handshake)

---

## 9. Código de referência rápida

**WinGet COM (C#):**
```csharp
var pm = new PackageManager();
var catalog = pm.GetPackageCatalogByName("winget");
var opts = new FindPackagesOptions();
opts.Filters.Add(new PackageMatchFilter{ Field=PackageMatchField.Name, Value=query });
var res = await catalog.FindPackagesAsync(opts);
```

**Appx WinRT (C#):**
```csharp
var pm = new Windows.Management.Deployment.PackageManager();
var pkgs = pm.FindPackagesForUser(string.Empty);
var prov = pm.FindProvisionedPackages();
var info = pkg.GetPackageUserInformation(sid);
if(info.InstallState == PackageInstallState.Staged) // fantasma
```

**Restart Manager:**
```csharp
[DllImport("rstrtmgr.dll")] static extern int RmStartSession(out uint h, int f, string key);
[DllImport("rstrtmgr.dll")] static extern int RmRegisterResources(uint h, uint nFiles, string[] files, ...);
```

---

*Gerado em 2026-08-27 — pronto para Fase 2.*
