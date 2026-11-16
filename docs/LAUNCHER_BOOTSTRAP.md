# Launcher / Bootstrapper do .NET — o que foi feito (e como reativar)

> **Situação atual (16/11/2026): REVERTIDO por decisão do usuário.**
> O KitLugia voltou ao esquema antigo: **o usuário instala o .NET manualmente**
> (é o diálogo nativo do .NET que aparece, com o botão de download).
> O código do launcher **continua no repositório** (`KitLugia.Bootstrap/`) e está
> documentado aqui — abaixo está exatamente o que fazer para reativá-lo.

---

## 1. O problema original

O app principal é `net10.0-windows` **framework-dependent**
(`KitLugia.GUI/KitLugia.GUI.csproj` — sem `SelfContained`; o `Deploy.ps1` publica com
`-p:SelfContained=false`). Numa máquina **sem o .NET Desktop Runtime 10**:

- o app **não abre nenhuma tela** — o *apphost* do .NET mostra
  "Você precisa instalar o .NET Desktop Runtime" e manda o usuário para a página de
  download no navegador;
- por isso **uma tela dentro do Kit não resolveria** — o WPF nem carrega;
- a única forma de resolver isso antes do app iniciar é um **binário que rode sem o .NET**.

Referência do modelo usado pelo LuaTools (ver §5).

## 2. O que foi construído (código mantido no repo)

### 2.1 Projeto `KitLugia.Bootstrap/` (net48 WPF, ~60 KB)

| Arquivo | Função |
|---|---|
| `KitLugia.Bootstrap.csproj` | `net48` — o .NET Framework 4.8 já vem no Windows 10/11, então o launcher roda **mesmo sem o .NET 10** |
| `app.manifest` | `asInvoker` (o UAC fica para o instalador oficial da Microsoft e para o app, que exige admin) + DPI `PerMonitorV2` |
| `Program.cs` | Fluxo: detecta → se falta, mostra a tela → instala → abre o app. Modos de diagnóstico: `--selftest`, `--shot <pasta>`, `--simulate-missing`, `--download`, `--install-dryrun` |
| `ComponentDetector.cs` | Detecta o que falta: **.NET Desktop Runtime x64 (major 10)** com 3 fontes (registro do .NET → `dotnet --list-runtimes` → varredura de `%ProgramFiles%\dotnet\shared`) + **Visual C++ 2015-2022 x64** (registro `HKLM\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64`) |
| `ComponentInstaller.cs` | Baixa de URL **oficial** (`aka.ms/dotnet/10.0/...`, fallback `builds.dotnet.microsoft.com/...`; `aka.ms/vs/17/release/vc_redist.x64.exe`) com progresso real, valida o arquivo (tamanho + cabeçalho `MZ`) e instala com `/install /quiet /norestart` |
| `DependencyDialogView.xaml(.cs)` | A tela **"Componentes Adicionais Necessários"** no visual do Kit (Surface0 `#0F0F0F`, dourado `#FFD700`, gradiente `GoldGradient`, triângulo de aviso idêntico ao `LugiaMsgBox`), com a lista do que falta, barra de progresso + percentual + status e os botões **Cancelar / Instalar** |
| `DependencyDialogWindow.xaml(.cs)` | Janela sem chrome, com 28 px de folga para o brilho dourado não ser cortado; conduz o download → instalação → verificação → abre o Kit |
| `BootstrapLog.cs` | Log em `%LOCALAPPDATA%\KitLugia\bootstrap.log` (único vestígio em máquina sem runtime) |

**Estados de instalação** (espelhando os do LuaTools / Velopack `Runtimes` API):
`0` = sucesso · `3010` = instalado mas **o Windows precisa reiniciar** · `1223` = **cancelado pelo usuário** (UAC recusado) · demais = falha.

### 2.2 Núcleo (`KitLugia.Core/AppPaths.cs`)

Fonte única de verdade sobre os executáveis: `LauncherExeFileName`, `AppExeFileName`,
`LegacyAppExeFileName`, `ResolveAppExePath()`, `GetAppExePathForRegistration()`.
Substituiu nomes chumbados em: menu de contexto do Explorer (`SystemTweaks`, 3 pontos),
auto-fix do menu (`TrayIconService`), updater (`GitHubUpdater` + `UpdatePage`), ISO
(`IsoEditorPage`) e WinPE (`WinbootManager` — hoje o nome do exe é **injetado** no script
em vez de assumido).

> Essas mudanças são **neutras** em relação ao esquema antigo: com o app publicado como
> `KitLugia.GUI.exe`, o helper resolve exatamente o mesmo caminho de antes.

### 2.3 Release (`Deploy.ps1`)

Publicava o launcher junto com o app e renomeava o app para `KitLugia.App.exe`
(para sobrar **um** exe para o usuário clicar), além de gravar um
`COMO ABRIR O KITLUGIA.txt`. **Esse bloco foi revertido** — o release voltou a publicar
só o `KitLugia.GUI.exe`.

## 3. Como a tela ficou

Prints reais em `shots/bootstrap/`:

- `bootstrap-dialog.png` — estado inicial (lista do que falta, Cancelar / Instalar);
- `bootstrap-progress.png` — durante o download (barra dourada 43%, status, percentual);
- `bootstrap-real-window.png` — **a janela real** capturada via
  `PrintWindow(hwnd, PW_RENDERFULLCONTENT)`: 596×494, brilho dourado sem corte.

## 4. Como reativar (10 minutos)

1. **`Deploy.ps1`** — dentro do bloco `if (-not $SkipBuild)`, depois do publish do GUI:
   - publicar o launcher:
     ```powershell
     & dotnet publish "$RepoRoot\KitLugia.Bootstrap\KitLugia.Bootstrap.csproj" -c $Configuration -o $OutputPath --nologo -p:DebugType=none -p:DebugSymbols=false
     ```
   - (opcional) renomear o app publicado para `KitLugia.App.exe`:
     ```powershell
     Move-Item (Join-Path $OutputPath "KitLugia.GUI.exe") (Join-Path $OutputPath "KitLugia.App.exe") -Force
     ```
   - (opcional) gravar o `COMO ABRIR O KITLUGIA.txt` apontando o usuário para `KitLugia.exe`.
2. **`KitLugia.GUI/App.xaml.cs`** — o bloco que dispara `PromptAndInstallDirectAsync` pode
   ser removido: com o launcher no release ele vira redundante (e o app nem roda sem o runtime).
3. **`KitLugia.GUI/Services/TrayIconService.cs`** — `GetAutoStartTargetPath()` já prefere o
   launcher quando ele está ao lado do app; nada a fazer.
4. Rodar `Deploy.ps1` e testar o fluxo em uma máquina **sem** o .NET 10.

## 5. Como o LuaTools faz (referência)

Estudo do código em `C:\Users\Lugia\Downloads\LuaTools-main`:

- `src/LuaToolsGui/LuaToolsGui.csproj:5` → `net8.0-windows` **framework-dependent** (igual ao Kit).
- `src/LuaToolsGui/Program.cs:18` → `VelopackApp.Build().Run()` na primeira linha do `Main`:
  install/update são do **Velopack**.
- `README.md` (Requirements) → *"the released **installer** bundles a check for the .NET 8
  Desktop Runtime and installs it if missing"* — o check do runtime fica no **instalador
  nativo**, não no app.
- Assets da release: **`LuaTools-win-Setup.exe`** (instalador nativo = o "1 exe" que o usuário
  baixa) **+ `LuaTools-win-Portable.zip`** (portátil). O LuaTools também tem ZIP + instalador.
- `Services/SteamAutoCrackService.cs:120-155` → **Runtimes API do Velopack** para instalar
  runtime on-demand: `GetRuntimeByName("net10-x64-desktop")` → `CheckIsInstalled()` →
  `CheckIsSupported()` → `DownloadToFile(progress)` → `InvokeInstaller(...)` → 3 estados
  (`InstallSuccess` / `UserCancelled` / `RestartRequired`).
- A tela "Instalação do LuaTools / Componentes Adicionais Necessários / .NET 8.0.0 X64"
  **não está no repo do GUI** (não há um único `.lua` lá) — vem do plugin LuaTools do
  **Garry's Mod** (lado Lua), que roda dentro do GMod e precisa do .NET 8 para as ferramentas GUI.

**Conclusão**: o truque do LuaTools não é um flag `--netframework` (impossível — o apphost do
.NET falha antes de qualquer código managed). É o **instalador nativo** rodando antes do app.
O "auto-install" do .NET que o usuário esperava (o diálogo do apphost com o botão *Download*)
só abre o navegador: a instalação em si é sempre um passo explícito (instalador oficial,
Microsoft Store ou `winget install Microsoft.DotNet.Runtime.10`).

## 6. Decisão do usuário (16/11/2026, 17:45)

> "deixe como era antes, testei achei legal mas não é bem o que eu esperava. pode salvar antes
> num .md o que fez e, se quiser manter o código usado, só volte ao esquema antigo que o
> usuário instala manualmente o .net"

→ **Sem instalador e sem launcher no release.** O .NET volta a ser responsabilidade do
usuário (diálogo nativo do .NET → download). O código fica no repo, documentado aqui.
