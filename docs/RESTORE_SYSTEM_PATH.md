# Restaurar PATH do Sistema — KitLugia

## O que foi feito nesta sessão

1. **Backup** do PATH atual salvo em `%TEMP%\path_backup_*.txt`.
2. **Verificação**: 30 diretórios no PATH; `winget.exe` instalado (`WindowsApps`) mas **ausente** do PATH ativo; 836 executáveis no `System32`; `7-Zip`, `chocolatey` e outros faltantes.
3. **Correção**: adicionados ao PATH (registro `User` + ambiente atual):
   - `C:\Program Files\WindowsApps\...\DesktopAppInstaller` (winget)
   - `C:\Program Files\7-Zip`
   - `C:\ProgramData\chocolatey\bin`
   - `C:\Program Files\Git\cmd`
   - `C:\Program Files\nodejs\`
   - `C:\Program Files\GitHub CLI\`
4. Resultado: PATH de **38 diretórios**; `winget` acessível.

## Por que cada PC é diferente

- **PATH do Usuário** (`HKCU\Environment\PATH`) vs **PATH do Sistema** (`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment\PATH`).
- Programas como `winget` ficam em `C:\Program Files\WindowsApps` (pacote UWP) e não são adicionados automaticamente ao PATH do usuário.
- Instalações de `node`, `dotnet`, `Docker`, `Git`, `7-Zip`, `chocolatey` variam conforme o usuário; alguns usam `Program Files`, outros `AppData\Local`.
- O `System32` (684 .exe) é padronizado pelo Windows, mas o restante é configurado por cada instalação.

## Como trazer de volta em qualquer PC

1. **Fazer backup**:
   ```powershell
   $env:PATH | Out-File "$env:TEMP\path_backup_$(Get-Date -Format 'yyyyMMddHHmmss').txt"
   ```
2. **Listar diretórios faltantes** comparando com uma lista base (`Windows`, `System32`, `Wbem`, `PowerShell`, `OpenSSH`, `WindowsApps` para winget, etc.).
3. **Adicionar apenas o que falta** (evita duplicação):
   ```powershell
   $atual = [Environment]::GetEnvironmentVariable('PATH','User') -split ';'
   $novo = 'C:\Program Files\WindowsApps\...\DesktopAppInstaller'
   if ($atual -notcontains $novo) { $env:PATH += ";$novo" }
   ```
4. **Persistir** no registro (`[Environment]::SetEnvironmentVariable`) para sobreviver ao reboot.
5. **Verificar** com `where.exe` ou `Get-Command`.

## Referência de binários críticos verificados

- `C:\WINDOWS\system32`: `cmd.exe`, `diskpart.exe`, `bcdedit.exe`, `dism.exe`, `reg.exe`, `schtasks.exe`, `sc`, `net.exe`, `findstr.exe`, `xcopy.exe`, `robocopy.exe`.
- `winget.exe`: `C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.280.0_x64__8wekyb3d8bbwe`.
- `7-Zip`: `C:\Program Files\7-Zip`.

## Nota técnica

Cada usuário deve executar o script de restauração com privilégios adequados (admin para `PATH` do sistema, usuário para `PATH` do usuário). Nunca sobrescrever o `PATH` sem backup, pois isso pode quebrar o acesso a ferramentas de desenvolvimento (`node`, `dotnet`, `cargo`).
