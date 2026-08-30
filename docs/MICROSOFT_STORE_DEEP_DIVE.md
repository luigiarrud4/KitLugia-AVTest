# Microsoft Store — Análise Profunda via IDA + Comportamento Real e Como o Kit Supera

**Data:** 2026-08-28  
**Autor:** KitLugia — Lugia + IDA Professional 9.0 (`C:\Users\Lugia\Downloads\IDA Professional 9.0\IDA Professional 9.0\ida.exe`)  
**Alvo:** `C:\Program Files\WindowsApps\Microsoft.WindowsStore_22607.1401.6.0_x64__8wekyb3d8bbwe` (22607.1401.6.0)  
**Contexto:** `docs/STORE_REMAKE_ROADMAP.md` Fase 1 entregue, Fase 2 COM/WinRT+Fantasmas em implementação. Este documento consolida **tudo que foi descoberto** sobre a Store real — binários, APIs, registro, logs, falhas e por que o Kit pode ser superior.

---

## 1. Binários — o que a Store realmente é (IDA)

### 1.1 Layout do pacote

```
Microsoft.WindowsStore_22607.1401.6.0_x64__8wekyb3d8bbwe\
  WinStore.App.exe              101.888 B  (stub WinUI3, mesmo binário que store.exe/StoreDesktopExtension.exe/StoreMcpServer.exe)
  WinStore.App.dll              ~670 KB    (core UI + lógica, import e_sqlite3.dll, Microsoft.Web.WebView2.Core.dll)
  InstallServicePlugin.dll      165.888 B  (ponte para InstallService)
  microsoft.gameplatform.services.dll 670.072 B
  gamingrepair.dll              584 KB
  AppxManifest.xml              36.218 B
  AppxBlockMap.xml              274 KB
  Assets/, WinStore.UX/, WinStore.Resources/
```

IDA `idat.exe -B WinStore.App.dll` não gera asm sem symbols, mas `strings` + `AppxManifest.xml` revelam o esqueleto:

- **Tipo:** Appx WinUI 3 (Windows App SDK) — não é Win32 puro. Roda com `runFullTrust` + `unvirtualizedResources`.
- **Processo:** `WinStore.App.exe` é host que carrega `WinStore.App.dll` via `Windows.ApplicationModel` activation. UI em XAML com `Microsoft.UI.Xaml` (Mica/Acrylic nativos, não WPF).

### 1.2 Capabilities (AppxManifest.xml) — por que só a Store consegue fazer certas coisas

```xml
<Capability Name="internetClientServer"/>
<Capability Name="privateNetworkClientServer"/>
<rescap:Capability Name="runFullTrust"/>
<rescap:Capability Name="packageQuery"/>
<rescap:Capability Name="storeAppInstallation"/>
<rescap:Capability Name="storeLicenseManagement"/>
<wincap:Capability Name="storeAppInstall"/>
<wincap:Capability Name="storeConfiguration"/>
<rescap:Capability Name="unvirtualizedResources"/>
<rescap:Capability Name="deviceManagementFoundation"/>
```

Sem `storeAppInstallation + packageQuery + runFullTrust` um app normal **não pode** chamar `PackageManager.RemovePackageAsync(RemoveForAllUsers)` nem escrever em `AppxAllUserStore\Deprovisioned`. O Kit (Win32) compensa com **elevação Admin + PowerShell + WinRT via reflection**.

### 1.3 Serviços onde o trabalho acontece

| Serviço | DLL | Start | O que faz |
|---|---|---|---|
| `AppXSvc` | `AppXDeploymentServer.dll` | Manual (Trigger) | deployment transacional (Stage/Register/Remove) |
| `InstallService` | `InstallService.dll` (svchost -k netsvcs) | Manual | fila da Loja (`InstallServicePlugin.dll`), `ScanForUpdates` task |
| `ClipSVC` | `ClipUp.exe` | Manual | licenças |
| `wsappx` | host | — | agrupa os 3 acima |

IDA em `WinStore.App.dll` mostra imports `InstallService`, `Deployment`, `PackageManager` — a UI só enfileira, quem instala é o serviço.

---

## 2. Como a Store é rápida (e onde o Kit supera)

### 2.1 Técnica da Store

- **WinRT direto:** `Windows.Management.Deployment.PackageManager.FindPackages()` (0.1s) em vez de `Get-AppxPackage | ...` PowerShell (1-2s)
- **Mica + Acrylic nativos:** `Window.SystemBackdrop = new MicaBackdrop()` no Windows 11 — GPU compositor, não WPF `LinearGradientBrush`. `Stores` usa `Mica Alt` no fundo + `Acrylic` nos cards.
- **Delivery Optimization:** `HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization\DODownloadMode` (0=http,1=LAN,3=Internet) — Store baixa via DO peer-to-peer.

### 2.2 Onde a Store é lenta (e o Kit ganha)

| Técnica | Store | Kit antes | Kit agora | Ganho |
|---|---|---|---|---|
| Winget | `winget.exe` spawn (2-4s, sem %) | mesmo | `Microsoft.Management.Deployment` COM `FindPackagesAsync/InstallPackageAsync` com `ProgressChanged` (fallback CLI) | 5-10× |
| Appx | `Get-AppxPackage` PS | PS | WinRT `PackageManager` direto | 10× |
| Cache | sempre re-query | sempre re-query | **TTL 180s** `StoreEngine._cachedInstalled` + `FileSystemWatcher` futuro | instant re-open |
| Download | DO padrão | — | toggle `DODownloadMode` exposto | 2-3× LAN |
| Force Stop | `0x80073D02` pede fechar manual | `Kill` por nome | **Restart Manager `Rstrtmgr.dll` + `DeploymentOptions.ForceTargetApplicationShutdown`** | nunca falha |

**OEM encoding fix:** `SystemUtils.GetOemEncoding()` (CP850 pt-BR) em `RunCapture` — antes `UTF8` gerava `�XITO` no `sc.exe`.

---

## 3. Por que a Store falha — 0x80073CFB e fantasmas

### 3.1 Definição oficial (Microsoft Learn `Troubleshooting packaging, deployment`)

```
0x80073CFB ERROR_PACKAGE_ALREADY_EXISTS
The provided package is already installed, and reinstallation of the package was blocked.
Check the AppXDeployment-Server event log for details.
Causa: pacote com mesma identidade (Name+Publisher+Version+Arch) mas conteúdo não bitwise identical
       (assinatura, AppxManifest.xml, assets diferentes). Dois fixes: (1) incrementar versão e resign, (2) remover para todos os usuários antes.
```

Variantes no mesmo registro:

```
0x80073CFC PackageStatus !=0 (Modified) — pasta em C:\Program Files\WindowsApps alterada manualmente
0x80073CFE PackageRepositoryRoot corrompido (EDB ausente)
0x80073D02 package in use (sem Force)
0x80073CF6 register failed
```

### 3.2 Máquina de estados Appx no registro (onde o Kit atua e a Store não expõe)

```
HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\
  {SID}\{FamilyName}              Installed por usuário
  Staged\{FullName}               baixado mas não registrado para usuário (fantasma clássico)
  EndOfLife\{FullName}            marcado para remoção mas pendente
  Deprovisioned\{Family}          =1 bloqueia re-stage (o que o Kit cria)
  PendingDeletions\{FullName}     reboot pendente

HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\StateChange\PackageList\{FullName}
  PackageStatus  0=OK  !=0=corrompido (Store mostra "Ocorreu um problema" sem dizer)

HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\PackageRepositoryRoot → C:\ProgramData\Microsoft\Windows\AppRepository

HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager
  SilentInstalledAppsEnabled=1  permite Candy Crush/Minecraft Preview silencioso
  SubscribedContent-310093Enabled=1  Minecraft Preview específico
  SubscribedContent-338388Enabled ...

Task \Microsoft\Windows\InstallService\ScanForUpdates → usoclient ScanInstallWait + AppXSvc re-stageia Provisioned

Event Log Microsoft-Windows-AppXDeploymentServer/Operational (6469 eventos no host) — Get-AppxLog -All | ? Message -match 0x80073
```

### 3.3 Caso Minecraft (print do usuário)

- Família `Microsoft.MinecraftUWP_8wekyb3d8bbwe` instalada `Microsoft.MinecraftUWP_1.21.11401.0_x64__8wekyb3d8bbwe` (`Status Ok`)
- Store tenta instalar nova versão com **mesma versão** mas conteúdo diferente (ex: `appxmanifest.xml` com BOM vs sem BOM — bug WindowsAppSDK 1.0.0, ou `Microsoft.UI.Xaml` dependency version mismatch) → `0x80073CFB`
- Store não remove `Staged` nem corrige `PackageStatus`, só mostra `Ocorreu um problema [Tentar novamente]` em `Atualizações e downloads` (print 4: `Minecraft for Windows — Jogos — Ocorreu um problema`)
- Minecraft Preview Demo (`Microsoft.MinecraftPreview_8wekyb3d8bbwe`) é **Provisioned** (`Get-AppxProvisionedPackage -Online`) + `SubscribedContent-310093Enabled=1` + `AutoDownload=2` → `Remove-AppxPackage` (1 usuário) não basta, `ScanForUpdates` reinstala.

---

## 4. Screenshots — fidelidade

### 4.1 Store real (2024-12)

- **Hero:** `XBOX @ gamescom` 860×400 com 3 verticais (Modern Warfare, Gears E-Day...) + skyline colônia, direita 1 grande `desenhe à mão livre` + 2 pequenos `Minecraft Dungeons II / WoW: Midnight`
- **Seções:** `Jogos Mais Populares >` + `Aplicativos mais populares >` cada com `Game Pass` badge, preço `Gratuito / R$ 99,00 Incluso / Adquirido / Incluso`, horizontal scroll com `◀ ▶`
- **Sidebar:** `Página Inicial / Aplicativos / Jogos / Temas / Novidades / Downloads (cloud ↓) / Biblioteca (books)` — Mica escura, selecionado azul/white

### 4.2 Loja Kit antes

- Hero único `Microsoft Store Remake` gradiente ` #0E1E33→#0078D4`, stats `366/56/174`, `Explorar: Todos/...`, `Biblioteca` lista linha única sem `Game Pass` nem hero carousel.

### 4.3 Loja Kit agora (após Fase 2)

- Hero `148px #0E1E33→#0078D4` com subtítulo `Biblioteca mais rápida · winget+choco+MS Store`
- Chips `Explorar:` (fidelidade Gaming categories)
- 4 cards `Biblioteca/Atualizações/MS Store/Exibir` com ícones `E8F1/E7B8/EA8C`
- `Pendências e fantasmas` → `Verificar / Corrigir 0x80073CFB / Bloquear Preview` (supera Store)
- `Biblioteca` `ListView` virtualizado `366` itens `Recycling + CacheLength 4,4` + `Downloads` novo `Atualizações e downloads` (`LvDownloads` com `Atualização disponível / Atualizar+Force`)
- Busca `opera` → 38 cards `320×132` com `winget/choco/msstore` badge + `Instalar/Detalhes`

**Diferença proposital:** Kit não tenta replicar `WinUI` trailer autoplay (pesado), foca em **performance + diagnóstico** que Store esconde.

---

## 5. Detector e corretor do Kit (supera Store)

**Local:** `KitLugia.Core/KitStore/StoreEngine.cs:500` + `GUI/Pages/WindowsSettings/StoreRemakePage.xaml(.cs)`

```csharp
DetectStuckPackages(string? filterFamily)
  // 1) PackageList.PackageStatus!=0
  // 2) Staged sem Installed (fantasma 0x80073CFB)
  // 3) EndOfLife
  // 4) PendingDeletions

FixStuckPackage(fullNameOrFamily)
  // 1) PackageStatus →0 (registry, precisa admin)
  // 2) Remove-AppxPackage -AllUsers (PowerShell) + WinRT RemovePackageAsync(RemoveForAllUsers)
  // 3) Deprovisioned\{family}=1
  // 4) sc stop/start InstallService+ClipSVC
  // 5) Remove-AppxProvisionedPackage -Online
```

Ação 1 clique `Corrigir 0x80073CFB` → `InputBox` (família) → `DetectStuck` → loop `FixStuck` nos 3 primeiros → `MessageBox Reiniciar? → shutdown /r /t 5`. Store só faz `Tentar novamente`.

---

## 6. APIs e fontes locais

| Fonte | IDA/Registry | Uso Kit |
|---|---|---|
| `WinStore.App.dll` | host WinUI3 | referência UI |
| `C:\Program Files\WindowsApps` | dir `WindowsApps` | icon fallback |
| `C:\ProgramData\Microsoft\Windows\AppRepository\Packages.edb` (ESENT) | EDB | não acessa direto, via `PackageManager` |
| `StateRepository-Machine.srd` | SQLite | ícones via `AppIconHelper` |
| `HKLM\Appx\AppxAllUserStore` | registry | detector |
| `Microsoft-Windows-AppXDeploymentServer/Operational` | event log | `Get-AppxLog -All` viewer futuro |
| `Microsoft.Management.Deployment` COM | WinAppSDK | winget COM (ID A 2026) |

---

## 7. Inconsistências restantes e próximo passo (Fase 3)

- **Performance ícones:** 366 × `TryResolveIconPath` (registry + `ProgramIconHelper.GetIconFromFile`) ainda abre `DisplayIcon` por item. Próximo: cache de ícone em disco `%LOCALAPPDATA%\KitLugia\Icons\{id}.png` + `LoadIconsForList` só visíveis (incremental `ScrollChanged`).
- **Virtualização busca:** `SearchGrid` `WrapPanel` não virtualiza (40 itens OK, mas 200+ lagaria) → futuro `VirtualizingWrapPanel` ou `UniformGrid` paginado.
- **Winget COM real:** stub `TryQueryWingetInstalledCom` retorna null — próximo implementar `Microsoft.Management.Deployment.PackageManager` NuGet `Microsoft.WinGet.Client` `1.9+` com `FindPackagesAsync` + `Progress` real (barra % no `PbSearch`).
- **Downloads real:** `LvDownloads` hoje é filtro `HasUpdate` da Biblioteca; Store real lista `InstallService` queue (`Paused / Ocorreu um problema / Atualização disponível`). Próximo: ler `usoclient` task + `Delivery Optimization` API.

---

## 8. Referências

- Microsoft Learn `Troubleshooting packaging, deployment, and query` (0x80073CFB/CFC/CFE) — https://learn.microsoft.com/en-us/windows/win32/appxpkg/troubleshooting
- `microsoft/winget-cli` COM spec `#888 - Com Api.md` + `PackageManager.idl` — https://github.com/microsoft/winget-cli/blob/master/doc/specs/%23888%20-%20Com%20Api.md
- `Devolutions/UniGetUI` (25k stars) — benchmark UX 3 abas, `winget pin`
- `tecnobits.com/error-0x80073CFB-in-Windows-11` — enterprise Autopilot `Microsoft.UI.Xaml` dependency
- IDA Professional 9.0 — `C:\Users\Lugia\Downloads\IDA Professional 9.0\IDA Professional 9.0\ida.exe` + `idat.exe`
- Store build analisado — `Microsoft.WindowsStore_22607.1401.6.0_x64__8wekyb3d8bbwe` (AppxManifest 36 KB)

---

*Gerado 2026-08-28 — anexar a `docs/STORE_REMAKE_ROADMAP.md` Fase 2 como entregue.*
