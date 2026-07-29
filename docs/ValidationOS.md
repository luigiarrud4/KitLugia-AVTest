# Validation OS (WinVOS) — Documentacao Completa

## Visao Geral

**Validation OS** (tambem chamado de **WinVOS**) e um sistema operacional leve, rapido e customizavel da Microsoft, baseado no Windows 11, projetado para uso em linhas de fabrica para diagnosticar, mitigar e reparar defeitos de hardware durante a fabricacao de dispositivos Windows.

- Lancamento inicial: 2022
- Base: Windows 11 (build 26100 a partir da versao 2504)
- Arquiteturas: x64 (AMD64) e ARM64
- Tamanho base: ~193 MB (WIM comprimido)
- Boot: modo texto (cmd.exe) por padrao
- Licenca: gratuita para OEMs e desenvolvedores (nao e para uso do consumidor final)

## Historia das Versoes

| Versao | Build | Data | Novidades |
|--------|-------|------|-----------|
| 22H2 | — | 2022 | Lancamento inicial |
| 2504 | 26100.3916 | Abr/2025 | Suporte WPF + .NET, drivers Surface Dock, CJK fonts separados, config RAM-DISK via DISM |
| 2507 | 26100.4768 | Jul/2025 | RDP, Task Manager, Serial Console (SAC), Deployment independente de Camera |
| 2509 | 26100.6725 | Set/2025 | Modern Standby, Smart Card/NFC, WinUI3, Developer Tools, Benchmarking Tools, Crash Dump USB |
| 2604 | 26100.8328 | Abr/2026 | — |

## Diferencas entre Validation OS e WinPE

| Caracteristica | Validation OS | WinPE |
|---|---|---|
| Proposito | Diagnostico/validacao de hardware | Instalacao/deploy/recovery do Windows |
| Tamanho do WIM | ~193 MB | ~500 MB |
| Tempo de boot | 8-10 segundos | 15-20 segundos |
| Shell padrao | cmd.exe (CLI) | cmd.exe (CLI) |
| Suporte WPF/.NET | Sim (desde 2504, via pacote opcional) | Nao nativo |
| Suporte HLK | Nativo | Parcial (add-ons) |
| Drivers inbox | Nao (precisa injetar) | Sim (basico) |
| Armazenamento | RAM-only por padrao | Configuravel (disco/USB) |
| Atualizacoes de seguranca | Nenhuma (imagem estatica) | Mensal |
| Ciclo de vida | 72h (reinicio automatico) | 240h (10 dias) |
| Uso permitido | Validacao de hardware em fabrica | Deployment e recovery |
| App GUI via WPF | Sim (com pacote extra) | Nao |

## Arquitetura

O Validation OS e uma versao extremamente reduzida do Windows 11:
- Remove: Microsoft Store, Edge, explorer.exe (desktop), telemetria, servicos desnecessarios
- Mantem: kernel NT, Win32 API, .NET runtime (via pacote), suporte a drivers WDDM
- Boot: direto para cmd.exe (sem Winlogon/desktop)

### Indices do WIM

O ISO do Validation OS contem multiplos indices no arquivo `ValidationOS.wim`:

- **Index 1**: para boot em disco interno (aplica-se no HD/SSD)
- **Index 2**: para boot via USB (boot por RAM)

## Pacotes Opcionais (Cab)

A ISO inclui pacotes `.cab` em `<ISO_ROOT>:\cabs\` para adicionar funcionalidades. Cada pacote tem versao neutra (language-neutral) e versao especifica de idioma (en-us). Ambos precisam ser adicionados.

### Lista Completa de Pacotes

| Pacote | Descricao |
|--------|-----------|
| Microsoft-WinVOS-Apps-Package | Aplicativos basicos (Notepad), VC++ runtimes, .NET Framework 4.5, COM suporte |
| Microsoft-WinVOS-Audio-Package | Audio playback e gravacao |
| Microsoft-WinVOS-Bluetooth-Package | Suporte Bluetooth |
| Microsoft-WinVOS-Camera-Package | Suporte a cameras |
| Microsoft-WinVOS-Debugging-Package | Debug kernel |
| Microsoft-WinVOS-DeviceProvisioning-Package | DISM, BCDboot, BCDedit, PowerShell, networking |
| Microsoft-WinVOS-Graphics-Package | DirectX basico, OpenGL |
| Microsoft-WinVOS-Graphics-UXTheme-Package | Theming UI moderna |
| Microsoft-WinVOS-HyperV-Package | Drivers Hyper-V |
| Microsoft-WinVOS-InboxDrivers-Package | Drivers inbox (ASIX ethernet, etc.) |
| Microsoft-WinVOS-Multimedia-Package | Playback multimedia (MP4) |
| Microsoft-WinVOS-NetFx45-Package | .NET Framework 4.5 |
| Microsoft-WinVOS-OOBE-Package | Out of Box Experience (muda hostname, admin user) |
| Microsoft-WinVOS-OptionalFileSystems-Package | UDFS, chkdsk |
| Microsoft-WinVOS-Peripherals-Package | Drivers perifericos, PnP, adaptadores de rede |
| Microsoft-WinVOS-PnP-Package | Plug and Play (pnputil.exe, devcon.exe) |
| Microsoft-WinVOS-Power-Package | Gerenciamento de energia, hibernacao, Modern Standby |
| Microsoft-WinVOS-PowerShell-Package | PowerShell 5.1 + .NET Framework |
| Microsoft-WinVOS-Privacy-Package | Capability Access Manager |
| Microsoft-WinVOS-RDP-Package | Remote Desktop Protocol |
| Microsoft-WinVOS-SecureStartup-Package | Secure Boot + WMI providers |
| Microsoft-WinVOS-Sensors-Package | Sensores basicos |
| Microsoft-WinVOS-SmartCard-Package | NFC e Smart Card |
| Microsoft-WinVOS-SerialConsole-Package | Serial console (SAC) |
| Microsoft-WinVOS-SMB-Package | Cliente SMB, Lanman, MUP |
| Microsoft-WinVOS-USB-Package | Suporte USB e HID |
| Microsoft-WinVOS-Virtualization-Package | Virtualizacao basica |
| Microsoft-WinVOS-WLAN-Package | Wi-Fi (netsh wlan) |
| Microsoft-WinVOS-WMIC-Package | WMIC (deprecado) |
| **Microsoft-WinVOS-WPF-Support-Package** | **Suporte WPF + .NET + WinUI3** (Extra) |
| Microsoft-WinVOS-WWAN-Package | WWAN/AT commands (Experimental) |
| Microsoft-WinVOS-PnP-Settings-Package | Configuracoes WWAN (Experimental) |
| Microsoft-WinVOS-BenchMarkingToolsSupport | Cinebench, FurMark, Geekbench 6, BurnInTest (Extra) |
| Microsoft-WinVOS-DeveloperTools | Regedit UI, Event Viewer, Computer Management, Disk Management, Device Manager, Services (Extra) |

### Pacotes na pasta Extra

A partir da versao 2504, alguns pacotes foram movidos para `<ISO_ROOT>:\Extras\CAB\`:
- `Microsoft-WinVOS-WPF-Support-Package` — suporte WPF/.NET/WinUI3
- `Microsoft-WinVOS-BenchMarkingToolsSupport-Package` — ferramentas de benchmark
- `Microsoft-WinVOS-DeveloperTools-Package` — ferramentas de desenvolvimento (MMC, regedit)

## Feature Packages (.pkg)

A ISO inclui definicoes de features em `<ISO_ROOT>:\GenImage\configs\`. Sao arquivos `.pkg` que agrupam varios CABs em uma feature unica.

### Features Disponiveis

| Feature | Descricao |
|---------|-----------|
| `apps.pkg` | Applications and Application Support |
| `audio.pkg` | Audio |
| `bluetooth.pkg` | Bluetooth |
| `camera.pkg` | Camera |
| `debug.pkg` | Debugging |
| `provisioning.pkg` | Device provisioning and administration |
| `graphics.pkg` | Graphics/DirectX support |
| `hyperv.pkg` | Hyper-V Support |
| `inboxdrivers.pkg` | Inbox Drivers |
| `multimedia.pkg` | Multimedia |
| `oobe.pkg` | OOBE |
| `filesystem.pkg` | Optional File Systems |
| `peripherals.pkg` | Peripherals and Network Adapters |
| `power.pkg` | Power management |
| `powershell.pkg` | PowerShell |
| `rdp.pkg` | Remote Desktop Protocol |
| `securesupport.pkg` | Secure Startup Support |
| `sensors.pkg` | Sensors |
| `smartcard.pkg` | Smart Card |
| `serialconsole.pkg` | Serial Console (SAC) |
| `smb.pkg` | SMB |
| `usb.pkg` | USB support |
| `virtualization.pkg` | Virtualization Support |
| `wlan.pkg` | Wi-Fi |

## Suporte WPF (Microsoft-WinVOS-WPF-Support)

### Disponibilidade
- Introduzido na versao **2504** (Abril 2025)
- Atualizado na versao **2509** para incluir WinUI3 (Windows App SDK)
- Localizacao no ISO: `<ISO_ROOT>:\Extras\CAB\`

### O que funciona:
- WPF applications basicas (.NET Framework e .NET Core)
- .NET runtime (antigo ".NET Core")
- WPF on .NET (antigo "WPF on .NET Core")
- WinUI3 (a partir da 2509)

### Limitacoes:
- Suporte "basico" — nem todos os recursos WPF podem funcionar
- Requer que o CAB seja adicionado via DISM ao WIM offline
- Dependencias: o pacote pode precisar de outros CABs (Graphics, GDI+, etc.)

## Ferramentas de Customizacao

### 1. Validation OS Image Builder (GUI)

Ferramenta grafica incluida no ISO em `<ISO_ROOT>:\ImageBuilder\ValidationOSImageBuilder.exe`.

Permite:
- Selecionar features (pacotes)
- Adicionar drivers (.inf)
- Adicionar software (.exe)
- Importar registros (.reg)
- Configurar comando de startup
- Gerar SDK para o WIM customizado
- Salvar template para uso futuro

### 2. Validation OS Image Builder CLI

Linha de comando em `<ISO_ROOT>:\IBCLI\ValidationOSImageBuilderCLI.exe`.

Opcoes principais:
```
-l, --list                    Lista todas as features
-i, --info <feature>          Descricao de uma feature
-f, --features <features>     Features a incluir
-d, --drivers <path>          Pasta de drivers
-s, --software <path>         Pasta de software
-r, --registry <path>         Arquivo .reg
-sc, --startup-command <path> Comando de startup
-o, --output <path>           Pasta de saida
-sdk, --generate-sdk          Gerar SDK
-g, --generate-image          Gerar imagem
-bt, --boottype <USB|InternalDisk>  Tipo de boot
```

### 3. GenImage (Avançado)

Script em lote em `<ISO_ROOT>:\GenImage\GenImage.cmd`. Metodo mais flexivel e customizavel.

Parametros principais:
```
GenImage [-ImageFile:] [-ImagePath:] [-PackagePath:] [-OutPath:]
         [-Packages:|-PackagesList:|-DriversOnly|-NoPackages]
         [-Drivers:|-HW] [-WinVOS_Root:] [-MountDir:]
         [-wim|-vhd|-vhdx] [-usb|-x] [-inc]
         [-TimeZone:] [-AddedSW: [-AddedSWTargetDir:]]
         [-RegistryImport:] [-StartupCommand:]
         [-NoWait] [-v]
```

Exemplo:
```cmd
GenImage -PackagesList:apps.pkg,graphics.pkg,powershell.pkg,usb.pkg
         -Drivers:D:\MyDrivers
         -RegistryImport:D:\settings.reg
         -StartupCommand:D:\mydiag.exe
         -OutPath:D:\MyValOS
```

## Metodos de Boot

### USB Boot (RAM)

1. Crie um drive WinPE bootavel
2. Substitua `boot.wim` pelo `ValidationOS.wim` (renomeie)
3. Desabilite o servico XtaCache no registro (via arquivo .reg offline)
4. Boot pelo USB

### Instalacao em Disco (Aplicacao)

1. Boot pelo WinPE
2. Formate o disco com DiskPart
3. Aplique a imagem: `dism /Apply-Image /ImageFile:ValidationOS.wim /Index:1 /ApplyDir:W:\`
4. Crie entrada de boot: `bcdboot w:\windows /s S: /f ALL`
5. Reinicie

### Boot via BCD Ramdisk (Metodo KitLugia)

O KitLugia implementa boot via BCD RAMDISK, similar ao WinPE:
1. Extrai WinVOS.wim do ISO
2. Customiza com wimlib + DISM (WPF support opcional)
3. Configura entrada BCD ramdisk com boot.sdi
4. Configura bootsequence one-time
5. Shell custom: `startnet.valos.cmd` (tenta lancar KitLugia.exe, fallback cmd)

## Script de Startup (startnet.valos.cmd)

O Validation OS permite definir um comando de startup que executa automaticamente ao boot.
Metodo oficial Microsoft: configurar a chave `Shell` no registro:
```
reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
    /v Shell /t REG_SZ /F /D "cmd /k c:\windows\System32\startnet.valos.cmd"
```

O KitLugia usa este mecanismo para lancar o `KitLugia.exe` automaticamente, com fallback para cmd.exe.

## Bootsequence via BCD (One-Time)

Para criar uma entrada de boot que aparece apenas uma vez (sem modificar o boot padrao):

```cmd
bcdedit /copy {current} /d "KitLugia Validation OS"
bcdedit /set {guid} device ramdisk=[C:]\KL_WINPE\validation_boot.wim,{ramdiskoptions}
bcdedit /set {guid} osdevice ramdisk=[C:]\KL_WINPE\validation_boot.wim,{ramdiskoptions}
bcdedit /set {guid} path \windows\system32\boot\winload.efi
bcdedit /set {guid} detecthal yes
bcdedit /set {guid} winpe yes
bcdedit /timeout 10
bcdedit /bootsequence {guid}
```

## Vantagens do Validation OS

1. **Extremamente leve** (~193 MB WIM, ~400 MB em RAM)
2. **Boot ultra-rapido** (8-10 segundos)
3. **Suporte oficial a WPF/.NET** (desde 2504) — permite rodar aplicacoes GUI no lugar do WinPE
4. **Suporte HLK nativo** — executa testes de certificacao Windows sem adaptacoes
5. **Seguranca** — superficie de ataque minima, sem browser, sem loja, sem servicos desnecessarios
6. **RAM-only por padrao** — protege o armazenamento do dispositivo contra corrupcao
7. **Arquitetura modular** — adiciona apenas os componentes necessarios via CABs
8. **Suporte a Win32** — aplicacoes diagnosticas rodam nativamente
9. **Multi-arquitetura** — x64 e ARM64
10. **SDK customizado** — o Image Builder gera SDK especifico para o WIM gerado
11. **WinUI3** — suporte ao Windows App SDK (desde 2509)
12. **Ferramentas de desenvolvedor** — Regedit, Device Manager, Event Viewer, Disk Management (desde 2509)

## Desvantagens e Limitacoes

1. **Sem drivers inbox** — voce precisa injetar drivers manualmente
2. **Sem desktop/explorer** — apenas cmd.exe (a menos que voce crie uma GUI customizada)
3. **Sem suporte ao consumidor final** — licenca restrita a OEMs e desenvolvedores
4. **Imagem estatica** — sem Windows Update; precisa reconstruir o WIM para atualizar
5. **Reinicio automatico** — a sessao expira em 72 horas (USB boot)
6. **Ecosystema limitado** — sem suporte a ferramentas de terceiros (Acronis, Macrium, etc.)
7. **Dependencia de DISM** — para adicionar CABs (wimlib nao suporta Add-Package)
8. **Complexidade** — requer conhecimento tecnico para customizar
9. **Problemas em ARM64** — instabilidade com SerialConsole + Bluetooth (conhecido)
10. **WMIC deprecado** — sendo removido em versoes futuras
11. **Nao e para uso geral** — nao substitui Windows 11 Pro/Home para uso diario

## Casos de Uso

### 1. Diagnostico de Hardware em Fabrica (Caso Principal)
Testes de CPU, RAM, armazenamento, perifericos durante a fabricacao de dispositivos Windows.

### 2. Testes de Certificacao HLK
Execucao de testes do Windows Hardware Lab Kit para certificacao de drivers e dispositivos.

### 3. Ambiente de Diagnostico Avancado (IT Pro)
Boot rapido para diagnosticar hardware defeituoso em frotas de dispositivos gerenciados.

### 4. Base para Ferramentas de Rescue/Recovery Customizadas
Similar ao WinPE, mas com suporte a WPF/.NET para criar ferramentas GUI de recovery.

### 5. Teste de Drivers
Desenvolvedores de drivers podem bootar rapidamente em um ambiente Windows minimo para testar drivers.

### 6. Ambiente de Benchmarking
Com o pacote BenchmarkingToolsSupport, executa Cinebench, FurMark, Geekbench 6, BurnInTest.

## Como o KitLugia usa o Validation OS

O KitLugia implementa suporte ao Validation OS como alternativa ao WinPE:

1. **Download**: tenta GitHub release (VALOS-base.7z) -> Microsoft CDN (ISO oficial) -> cache local
2. **Extracao**: extrai WinVOS.wim do ISO
3. **Customizacao**: injeta `startnet.valos.cmd` via wimlib (rapido, sem montar); opcionalmente adiciona WPF support via DISM
4. **Boot**: configura entrada BCD ramdisk + bootsequence one-time
5. **Shell**: script que tenta lancar KitLugia.exe automaticamente; fallback para cmd.exe

### Fluxo de Preparacao

```
[1/5] Download ISO (GitHub -> MS CDN -> cache)
[2/5] Extracao WinVOS.wim
[3/5] Injecao startnet.valos.cmd via wimlib
[3b/5] WPF Support (opcional, via DISM se CAB encontrado)
[4/5] Resolucao boot.sdi
[5/5] Configuracao BCD ramdisk + bootsequence
```

## Metodo para Gerar VALOS-base.7z

O script `Build-ValidationOS.ps1` (disponivel no release v1.1 do GitHub) automatiza:

1. Download do ISO oficial da Microsoft (requer aceitacao de licenca)
2. Extracao do WinVOS.wim do ISO
3. Otimizacao do WIM (export para unico indice)
4. Empacotamento em VALOS-base.7z (compressed com 7-Zip)

Este artefato e usado como fallback de download no KitLugia, evitando que o usuario precise baixar o ISO completo (~3GB).

## API Validation (APIValidator.exe)

O Image Builder gera `apisurface.xml` contendo todas as funcoes API suportadas pelo WIM customizado. A ferramenta `APIValidator.exe` (do WDK) analisa se um `.exe` e compativel:

```cmd
APIValidator.exe MeuApp.exe apisurface.xml
```

## Debug

O Validation OS suporta debug via WinDbg:
- **User-mode**: usar `dbgsrv.exe` (Process Server) no alvo, conectar remotamente
- **Kernel-mode**: habilitar via `bcdedit /set {default} debug on`
- **Network KD**: `bcdedit /dbgsettings NET HOSTIP:x.x.x.x PORT:500xx`
- **USB KD**: `bcdedit /dbgsettings USB`

## Geracao de SDK

O Image Builder pode gerar um SDK do Visual Studio especifico para o WIM gerado. Isso garante que o SDK contenha apenas as APIs disponiveis na imagem customizada.

## Links Uteis

- Documentacao oficial: https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/validation-os-overview
- Download ISO (x64): https://aka.ms/DownloadValidationOS
- Download ISO (ARM64): https://aka.ms/DownloadValidationOS_arm64
- Licenca: https://learn.microsoft.com/en-us/legal/windows/hardware/validation-os-license
- SDK Samples (GitHub): https://github.com/microsoft/Validation-OS
- Release notes 2504: https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/validation-os-release-notes-2504
- Release notes 2507: https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/validation-os-release-notes-2507
- Release notes 2509: https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/validation-os-release-notes/2509
- KitLugia release v1.1: https://github.com/luigiarrud4/KitLugia-WinPE/releases/tag/v1.1

## Tabela Comparativa: Validation OS vs WinPE vs Windows 11 Full

| Aspecto | Validation OS | WinPE | Windows 11 |
|---------|--------------|-------|------------|
| Tamanho (WIM) | ~193 MB | ~500 MB | 5-20 GB |
| Boot ate prompt | 8-10s | 15-20s | 20-45s |
| RAM usage | ~400 MB | ~512 MB | 2-4 GB |
| Shell | cmd.exe | cmd.exe | explorer.exe |
| WPF/.NET apps | Sim (opcional) | Nao nativo | Sim |
| Win32 apps | Sim | Sim | Sim |
| Drivers inbox | Nao | Sim | Sim |
| Windows Update | Nao | Mensal | Regular |
| Licenca uso | OEM/Dev apenas | Deployment/Recovery | Consumidor |
| Custom tools GUI | Via WPF | Via Win32 | Nativo |
| Suporte HLK | Nativo | Parcial | Sim |
