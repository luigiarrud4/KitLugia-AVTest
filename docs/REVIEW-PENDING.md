# Revisão de Código — Pendências e Em Revisão

## ❌ Pendente (precisa resolver)

### 1. WPF não funciona em WinPE padrão
- `PresentationCore.dll` depende de `milcore`/DWM — ausente no WinPE
- **Solução proposta:** Launcher nativo Win32 (~50KB) que detecta ambiente e desvia para WinXShell + diskpart scripts

### 2. Tamanho excessivo (250MB)
- Scratch space WinPE = 512MB default — app ocupa metade
- **Solução:** Condicionar publish: só incluir runtime .NET quando for ValOS/Windows

### 3. System.Management (WMI) ausente no WinPE
- `KitLugia.WinPE.csproj` referencia `System.Management 10.0.8`
- WMI não existe em WinPE — assembly carrega mas falha em runtime
- **Solução:** Remover referência ou condicionar carregamento

### 4. OpenFileDialog (COM) falha no WinPE
- `InstallWindowsPage.xaml.cs:34`, `ToolsPage.xaml.cs:46,68`
- Requer `ExplorerFrame.dll` (COM CommonItemDialog) — ausente no WinPE
- **Solução:** Substituir por path digitável + try-catch

### 5. Clipboard inexistente no WinPE
- `PartitionsPage.xaml.cs:162` — `Clipboard.SetText()` lança exceção
- **Solução:** try-catch + feedback visual alternativo

### 6. ShellExecute sem Explorer
- `FileExplorerPage.xaml.cs:235` — `UseShellExecute=true` falha sem shell
- **Solução:** Win32 `ShellExecuteW` direta ou fallback para cmd

## 🔄 Em Revisão (analisar viabilidade)

### 7. WinPEDetector frágil
- Checa `winpe.jpg`/`startnet.cmd` — método não oficial
- **Oficial:** `HKLM\SYSTEM\CurrentControlSet\Control\SystemStartOptions` contendo `MININT`
- **Decisão:** Substituir ou manter ambos?

### 8. Logger escreve em `%LOCALAPPDATA%`
- Em WinPE, `LocalApplicationData` = `X:\Users\default\AppData\Local`
- Pode não existir — `Directory.CreateDirectory` atenua mas é frágil
- **Decisão:** Redirecionar log para `X:\KitLugiaPE\` no WinPE?

### 9. `KitLugiaInstallPath` usa `ProgramFiles`
- Em WinPE: `X:\Program Files` — pode não existir
- **Decisão:** Usar `C:\KL_WINPE` como raiz universal?

### 10. WinXShell fallback vs shell único
- App tenta ser shell (startnet.cmd) E lançar WinXShell
- Duas GUIs competindo — confuso
- **Decisão:** Modo shell único: `startnet.cmd` → check → WinXShell OU app

## ✅ Já Resolvido nesta Sprint
- Styles.xaml `Color` → `Background`
- FileExplorerPage.xaml Grid sem Padding
- `UseWindowsForms` removido + referências ambíguas corrigidas
- `InputDialog` substitui `Microsoft.VisualBasic.Interaction.InputBox`
- Build 0 erros, 0 warnings
- WinXShell baixado + injetado
- ISO WinPE criada (478MB, BIOS+UEFI)
- QEMU lançado com ISO
