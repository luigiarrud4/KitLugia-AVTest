# WinPE Data Preservation — Instalação Completa sem Perder Dados

## Conceito

A ideia central é **MOVER** todos os dados do usuário (perfis, apps, configs) para uma pasta `C:\!`, instalar um Windows novo **sem formatar**, e depois **DEVOLVER** os dados ao lugar original. O WinPE KitLugia faz tudo de ponta a ponta — reboot direto no Windows novo.

```
Estado inicial:    C:\ [Windows + Users + ProgramFiles + dados]
                          │
                    WinPE move tudo para C:\!
                          │
                    DISM apply Windows novo em C:\
                          │
                    Devolve dados de C:\! para seus lugares
                          │
                    Reboot → usuário no Windows NOVO com dados intactos
```

## Por que MOVE em vez de COPY?

`robocopy /move` **move** os arquivos em vez de copiar:

- **Zero espaço extra necessário** — os dados só mudam de pasta
- Se o disco tem 500GB ocupados, precisa de 500GB livres em C: `C:\!` (na mesma partição) — SEM duplicação
- Risco: se falhar no meio, dados podem ficar inconsistentes. Mas em NVMe/SSD moderno é muito rápido

## O que mover para C:\!

| Item | Move? | Por quê |
|------|-------|---------|
| `C:\Users\*` | ✅ Move | Perfis, Desktop, Documents, Downloads, AppData, NTUSER.DAT |
| `C:\ProgramData\*` | ✅ Move | Dados compartilhados de apps |
| `C:\Program Files\*` | 🔲 Opcional | Apps instaladas (sem registro não rodam, mas portáteis sim) |
| `C:\Program Files (x86)\*` | 🔲 Opcional | Idem |
| `C:\Windows\Fonts` | ✅ Move | Fontes instaladas |
| `C:\Windows\System32\drivers\etc\hosts` | ✅ Move | Config de rede |
| Drivers de dispositivo | ✅ Move | `dism /online /export-driver` |
| Itens avulsos na raiz C:\ | ✅ Move | Pastas soltas que o usuário criou |

**Não move:** `C:\Windows` (inteiro), `C:\Windows.old`, `System Volume Information`, `$Recycle.Bin`.

## Preservação de Perfis de Usuário e Registry (navegadores inclusive)

### Registry do sistema (HKLM) — NÃO restaurar

As hives `C:\Windows\System32\config\SAM`, `SECURITY`, `SOFTWARE`, `SYSTEM` contêm:
- Mapeamento de drivers (`ENUM`) — hardware diferente → **BSOD**
- SIDs de contas — **permissões quebradas**
- Estado do Component Based Servicing (CBS) — **corrompe o Windows novo**
- Ativação — **pede reativação**

**Conclusão:** NUNCA restaurar HKLM. O Windows novo precisa do próprio registry.

### Registry do usuário (NTUSER.DAT) — SIM, com estratégia

Cada `C:\Users\[nome]\NTUSER.DAT` contém configurações pessoais (HKCU).

**Risco:** se o SID do usuário antigo não existir no Windows novo, o perfil não carrega.

**Estratégia segura: exportar keys seletivamente como .reg**

Em vez de mover o NTUSER.DAT inteiro, extrair keys específicas via WinPE:

```batch
:: Exporta configurações do usuário (HKCU)
reg export "HKCU\Control Panel\Desktop" C:\!\reg\desktop.reg
reg export "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer" C:\!\reg\explorer.reg
reg export "HKCU\Software\Microsoft\Windows\CurrentVersion\Themes" C:\!\reg\themes.reg
reg export "HKCU\Software\Google\Chrome" C:\!\reg\chrome.reg
reg export "HKCU\Software\Mozilla\Firefox" C:\!\reg\firefox.reg
reg export "HKCU\Software\Microsoft\Internet Explorer" C:\!\reg\ie.reg
reg export "HKCU\Software\Microsoft\Windows NT\CurrentVersion\Taskband" C:\!\reg\taskbar.reg
```

Depois da instalação do Windows, importar no usuário novo:

```batch
reg import C:\!\reg\desktop.reg
reg import C:\!\reg\explorer.reg
...
```

### O que funciona e o que não funciona

| Componente | Funciona? | Por quê |
|------------|-----------|---------|
| Desktop, Documents, Downloads | ✅ | São arquivos, movidos como tal |
| Bookmarks do Chrome/Firefox/Edge | ✅ | Estão em AppData, movidos com Users |
| Extensões, histórico, cookies | ✅ | Arquivos de dados, não dependem de SID |
| Personalização (tema, wallpaper, cores) | ✅ | Reg export/import de HKCU\Themes |
| Config do Explorer (layout, atalhos) | ✅ | Reg export/import |
| Taskbar presa (pins) | ✅ | Reg export HKCU\Taskband |
| **Senhas/logins do browser (DPAPI)** | **❌** | Criptografadas com hash do SID + máquina |
| Apps da Microsoft Store | ❌ | Licenciamento + registry |
| Programas instalados via .exe/.msi | ❌ | Registry HKLM perdido |
| Programas portáteis | ✅ | Só extrair arquivos |
| Salvamentos de jogo em AppData | ⚠️ | Funciona se o jogo não depender de registry |
| Windows Hello / PIN | ❌ | Atado ao TPM + SID |
| Certificados digitais (EFS) | ❌ | Atados ao SID + chave da máquina |

### Exportação de senhas (mitigação)

O usuário pode exportar senhas ANTES de iniciar o processo:

| Browser | Como exportar |
|---------|--------------|
| Chrome | Settings → Passwords → Export |
| Firefox | Settings → Passwords → Export |
| Edge | Settings → Passwords → Export |

O arquivo CSV exportado vai para `C:\!` junto com o resto.

## Fluxo Completo Automatizado

### Fase 1 — No WinPE KitLugia

```batch
@echo off
setlocal enabledelayedexpansion

echo =============================================
echo  KitLugia — Fresh Install sem perder dados
echo =============================================

:: 1. Detecta partição Windows
call :detect_windows
echo Partição Windows: !WIN_DRIVE!:\

:: 2. Cria pasta segura
set SAFE_DIR=!WIN_DRIVE!:\!
if exist !SAFE_DIR! (
  echo ERRO: C:\! já existe. Remova ou renomeie e tente novamente.
  pause
  exit /b 1
)
md !SAFE_DIR!

:: 3. Move perfis de usuário
echo Movendo perfis de usuário...
robocopy !WIN_DRIVE!:\Users !SAFE_DIR!\Users /copyall /b /xj /e /move /r:0 /np
if errorlevel 8 goto :erro

:: 4. Move ProgramData
echo Movendo ProgramData...
robocopy !WIN_DRIVE!:\ProgramData !SAFE_DIR!\ProgramData /copyall /b /xj /e /move /r:0 /np

:: 5. Move Program Files (opcional, perguntar)
choice /m "Manter Program Files (apps instaladas)?"
if errorlevel 2 goto :pula_programfiles
robocopy "!WIN_DRIVE!:\Program Files" "!SAFE_DIR!\Program Files" /copyall /b /xj /e /move /r:0 /np
robocopy "!WIN_DRIVE!:\Program Files (x86)" "!SAFE_DIR!\Program Files (x86)" /copyall /b /xj /e /move /r:0 /np
:pula_programfiles

:: 6. Move Fontes
echo Movendo Fontes...
robocopy !WIN_DRIVE!:\Windows\Fonts !SAFE_DIR!\Fonts /e /move /r:0 /np

:: 7. Exporta drivers
echo Exportando drivers...
md !SAFE_DIR!\Drivers
dism /online /export-driver /destination:!SAFE_DIR!\Drivers

:: 8. Hosts
copy !WIN_DRIVE!:\Windows\System32\drivers\etc\hosts !SAFE_DIR!\hosts.txt

:: 9. Move itens avulsos da raiz
echo Coletando itens avulsos da raiz...
md !SAFE_DIR!\_root
for /d %%i in (!WIN_DRIVE!:\*) do (
  set "name=%%~nxi"
  if /i not "!name!"=="Windows" if /i not "!name!"=="Users" if /i not "!name!"=="Program Files" if /i not "!name!"=="Program Files (x86)" if /i not "!name!"=="ProgramData" if /i not "!name!"=="!SAFE_DIR:~0,1!" if /i not "!name!"=="System Volume Information" if /i not "!name!"=="$Recycle.Bin" if /i not "!name!"=="Recovery" if /i not "!name!"=="ESD" (
    robocopy "%%i" "!SAFE_DIR!\_root\%%~nxi" /copyall /b /xj /e /move /r:0 /np
  )
)

:: 10. Salva inventário
echo Inventário salvo em !SAFE_DIR!\Manifest.txt
dir !SAFE_DIR! /s /b > !SAFE_DIR!\Manifest.txt

echo =============================================
echo  Backup concluído! Dados seguros em !SAFE_DIR!
echo  Pressione qualquer tecla para instalar Windows...
echo =============================================
pause >nul
goto :fase2

:erro
echo ERRO durante o backup. Verifique os logs.
pause
exit /b 1
```

### Fase 2 — Aplicar Windows (automático, mesmo script)

```batch
:fase2
:: 1. Pede ISO ou usa detectada
if not exist "D:\sources\install.wim" if not exist "E:\sources\install.wim" (
  echo Insira o pendrive/mídia com Windows ou informe o caminho da ISO.
  set /p ISO_PATH="Caminho da ISO: "
  if exist "!ISO_PATH!" (
    echo Extraindo ISO com 7z...
    "!KITLUGIA_PATH!\Resources\App\7Zip\7z.exe" x "!ISO_PATH!" -o"!WIN_DRIVE!:\WindowsInstallation" -y
  ) else (
    echo ISO não encontrada.
    pause
    exit /b 1
  )
)

:: 2. Detecta install.wim ou install.esd
set WIM_FILE=!WIN_DRIVE!:\WindowsInstallation\sources\install.wim
if not exist !WIM_FILE! set WIM_FILE=!WIN_DRIVE!:\WindowsInstallation\sources\install.esd

:: 3. Escolhe edição (índice)
echo Escolha a edição do Windows:
dism /Get-WimInfo /WimFile:!WIM_FILE!
set /p WIM_INDEX="Índice (ex: 1): "

:: 4. Aplica imagem
echo Aplicando Windows... (isso leva alguns minutos)
dism /Apply-Image /ImageFile:!WIM_FILE! /Index:!WIM_INDEX! /ApplyDir:!WIN_DRIVE!:\
if errorlevel 1 (
  echo ERRO ao aplicar imagem.
  pause
  exit /b 1
)
echo Windows aplicado com sucesso.

:: 5. (Opcional) Devolve dados ANTES do primeiro boot
choice /m "Restaurar dados agora (antes do primeiro boot)?"
if not errorlevel 2 call :restore

:: 6. Configura bootloader
bcdboot !WIN_DRIVE!:\Windows /s S:
if errorlevel 1 (
  echo ERRO ao configurar bootloader.
  pause
  exit /b 1
)

echo =============================================
echo  Pronto! Remova o pendrive e reinicie.
echo  O Windows novo vai iniciar com seus dados.
echo =============================================
pause
wpeutil reboot
```

### Fase 3 — Restauração de dados

```batch
:restore
echo Restaurando dados do backup...

:: Users
robocopy !SAFE_DIR!\Users !WIN_DRIVE!:\Users /copyall /b /xj /e /move /r:0 /np

:: ProgramData
robocopy !SAFE_DIR!\ProgramData !WIN_DRIVE!:\ProgramData /copyall /b /xj /e /move /r:0 /np

:: Program Files (se foi movido)
if exist "!SAFE_DIR!\Program Files" (
  robocopy "!SAFE_DIR!\Program Files" "!WIN_DRIVE!:\Program Files" /copyall /b /xj /e /move /r:0 /np
  robocopy "!SAFE_DIR!\Program Files (x86)" "!WIN_DRIVE!:\Program Files (x86)" /copyall /b /xj /e /move /r:0 /np
)

:: Drivers
md !WIN_DRIVE!:\Windows\System32\DriverStore 2>nul
dism /image:!WIN_DRIVE!:\ /Add-Driver /Driver:!SAFE_DIR!\Drivers /Recurse

:: Fontes
robocopy !SAFE_DIR!\Fonts !WIN_DRIVE!:\Windows\Fonts /e /move /r:0 /np

:: Itens avulsos
robocopy !SAFE_DIR!\_root !WIN_DRIVE!:\ /copyall /b /xj /e /move /r:0 /np

:: Hosts
copy !SAFE_DIR!\hosts.txt !WIN_DRIVE!:\Windows\System32\drivers\etc\hosts

:: Limpa pasta de instalação
rd /s /q !WIN_DRIVE!:\WindowsInstallation 2>nul

:: Mantém C:\! por segurança (usuário apaga depois)
echo Dados restaurados com sucesso!
goto :eof
```

## Estratégia de pastas-fantasma

Antes de aplicar o Windows, podemos criar esqueletos vazios dos diretórios que o Windows espera:

```batch
:: Pastas-fantasma: Windows espera encontrar estas pastas
md !WIN_DRIVE!:\Windows\System32\config
md !WIN_DRIVE!:\Users\Default
md !WIN_DRIVE!:\ProgramData
md "!WIN_DRIVE!:\Program Files"
md "!WIN_DRIVE!:\Program Files (x86)"
```

Isso evita que o DISM apply reclame de estrutura faltando e acelera o processo.

## Riscos e Mitigação

| Risco | Mitigação |
|-------|-----------|
| MOVE falha no meio do caminho | Usar `robocopy /move` com `/r:0` (zero retries) — se falhar, o arquivo fica no original |
| DISM apply sobrescreve C:\! | `C:\!` começa com `!` — Windows Setup/DISM não toca pastas não reconhecidas |
| SID diferente → perfil novo | Após restore, Windows pode criar `C:\Users\[nome].OLD`. Solução: renomear via WinPE antes do boot |
| Program Files sem registro | Apps não abrem. Solução: oferecer como opcional, avisar |
| Senhas de navegador perdidas | Avisar antes: "Exporte suas senhas no Chrome/Firefox antes de continuar" |
| Espaço insuficiente para MOVE | MOVE não precisa de espaço extra (mesma partição) — só o inode muda |

## Limitações Conhecidas

- **Apps instaladas via .exe/.msi** não funcionam depois da restauração (registry perdido)
- **Senhas de navegador** criptografadas (DPAPI) se perdem com SID novo
- **Windows Hello/PIN/Biometria** precisa ser reconfigurado
- **Licenciamento do Windows** pode precisar reativação

## O que NÃO funciona (e ok)

| Item | Status | Motivo |
|------|--------|--------|
| Arquivos do usuário (Docs, Fotos, Música) | ✅ OK | Movidos e restaurados |
| Bookmarks, extensões, histórico | ✅ OK | Estão em AppData |
| Programas portáteis | ✅ OK | Só extrair de volta |
| Fontes, hosts, drivers | ✅ OK | Movidos e reinseridos |
| Logins de sites salvos | ❌ Perde | Criptografia DPAPI por SID |
| Apps da Microsoft Store | ❌ Perde | Registry + licenciamento |
| Programas instalados (.exe/.msi) | ❌ Perde | Registry perdido |
| Windows Hello | ❌ Perde | Dados biométricos por SID |

## Testes em VM (próximos passos)

O usuário vai testar em VM:
1. VM com Windows + dados falsos (Users, Program Files, etc.)
2. Boot WinPE KitLugia
3. Mover tudo para C:\!
4. DISM apply Windows
5. Restaurar C:\! → lugares originais
6. Reboot
7. Verificar: usuário logado com dados intactos? Apps funcionam? Navegador ok?

## Integração com KitLugia (após testes)

- Novo modo: `KitLugia --reinstall-preserve`
- Detecta ISO automaticamente (ou baixa do UUP dump)
- Usa ADK/wimlib para aplicar imagem (mais rápido que DISM)
- Restaura drivers automaticamente
- Gera unattend.xml para automação completa

---

## Análise Completa de Possibilidades — Pesquisa Web

### Ferramentas Existentes no Mercado

| Ferramenta | Preço | Migra Programas? | Migra Registry? | Como funciona |
|-----------|-------|-----------------|-----------------|--------------|
| **USMT (Microsoft)** | Grátis (parte do ADK) | ❌ Só settings | ✅ HKCU + seletivo HKLM | CLI, ScanState/LoadState, XML de configuração |
| **ForensIT ProfWiz** | Grátis/Enterprise | ❌ Só perfil | ✅ HKCU (NTUSER.DAT) | Reatribui perfil a novo domínio/usuário |
| **Laplink PCmover** | US$40-60 | ✅ SIM | ✅ Completo (HKLM+HKCU) | Captura programas+registry+arquivos, reinstala no destino |
| **Zinstall WinWin** | US$79-189 | ✅ SIM | ✅ Completo (HKLM+HKCU) | Transferência direta ou via container |
| **Fab's AutoBackup 7** | €60 | ❌ Só lista + settings | ✅ HKCU seletivo | Backup de dados + settings + lista de programas |
| **KitLugia (nosso)** | Grátis/open | ⚠️ Parcial (portáteis) | ✅ HKCU seletivo (reg export) | WinPE → Move → DISM Apply → Restore |

### Como Laplink e Zinstall Consegue Migrar Programas?

Ambos fazem a mesma coisa:
1. **Capturam o HKLM inteiro** (SAM, SECURITY, SOFTWARE, SYSTEM) e HKCU
2. Aplicam no Windows novo
3. **Corrigem keys de hardware** (ENUM, driver references) — têm uma base de dados interna do que NÃO copiar
4. Ajustam caminhos de arquivos (se o Windows foi para outra partição)

**Por que funciona:** porque eles capturam TUDO e depois filtram. O que quebra são keys específicas de hardware — o resto (installed programs, COM classes, App Paths, etc.) funciona entre instalações.

**Risco:** mesmo eles fazendo isso, alguns programas quebram — especialmente:
- Antivírus (profundamente integrado ao kernel)
- Drivers de dispositivo
- Software com proteção contra cópia (licensing tied to hardware ID)
- Apps da Microsoft Store

### Abordagens Alternativas que Pesquisamos

#### Abordagem 1: Registry Completo (estilo Laplink/Zinstall)

```
Pré:
  - Exportar hives: reg save HKLM\SOFTWARE C:\!\reg\SOFTWARE
  - Exportar HKCU: reg save HKCU C:\!\reg\NTUSER.DAT
  
Pós-instalação (antes do primeiro boot):
  - Substituir hives: copy C:\!\reg\SOFTWARE C:\Windows\System32\config\SOFTWARE
  - Substituir NTUSER.DAT de cada usuário
```

**Problemas:**
- Mapeamento de drivers (`HKLM\SYSTEM\CurrentControlSet\Services`) — se o hardware mudou = BSOD
- SIDs de usuários diferentes = permissões quebradas
- Activation = pode invalidar
- **Requer uma base de "keys para preservar" e "keys para ignorar"** — como Laplink tem

#### Abordagem 2: Selective HKLM (só keys de programas)

```
Pré:
  - Enumerar programas: wmic product get name,version
  - Para cada programa instalado:
    - Exportar HKLM\Software\Microsoft\Windows\CurrentVersion\App Paths\[programa]
    - Exportar HKLM\Software\Classes\[extensão]
    - Exportar HKLM\Software\[vendor]\[programa]
    - Exportar HKLM\Software\WOW6432Node\[vendor]\[programa] (se 64-bit)
  
Pós:
  - Importar só essas keys
```

**Resultado:** alguns programas funcionam, outros não:
- ✅ Programas "portable-friendly" (que só precisam de `App Paths` e `Classes`)
- ❌ Programas que registraram COM/DLL services, drivers, ou dependem de `InstalledLocation`
- ⚠️ **Muito trabalho para resultados imprevisíveis**

#### Abordagem 3: Reinstall Assistido (nosso candidato principal)

```
Pré:
  - Mover Users (AppData incluído)
  - Mover Program Files (opcional)
  - Exportar registry HKCU seletivo (personalização)
  - Exportar lista de programas instalados (para reinstall fácil)
  
Pós:
  - Restaurar Users
  - Restaurar Program Files (para portáteis)
  - Importar reg HKCU
  - Gerar script de reinstall automático
```

**Vantagem:** é o que temos hoje, mas com bônus de portáteis + reinstall automático.

#### Abordagem 4: Symbolic Links (híbrida)

```
Pré:
  - Renomear C:\Program Files → C:\Program Files.old
  - Mover Users para C:\!
  
Pós:
  - Instalar Windows novo (cria C:\Program Files vazio)
  - mklink /J "C:\Program Files (legado)" "C:\Program Files.old"
  - Para cada programa portátil: mklink /D "C:\Program Files\[app]" "C:\Program Files.old\[app]"
```

**Útil para:** acesso rápido a programas antigos sem mover dados de volta. Não faz programas funcionarem magicamente.

### Matriz de Compatibilidade Entre Versões

| De → Para | Users + AppData | Programas Portáteis | Programas Instalados (mesma versão) | Programas Instalados (versão diferente) |
|-----------|----------------|-------------------|-------------------------------------|---------------------------------------|
| Win 10 → Win 10 | ✅ | ✅ | ⚠️ (se registry for preservado) | ⚠️ |
| Win 10 → Win 11 | ✅ | ✅ | ⚠️ (incompatibilidades de versão) | ⚠️ |
| Win 11 → Win 11 | ✅ | ✅ | ⚠️ (se registry for preservado) | ⚠️ |
| Win 7 → Win 10 | ✅ (dados) | ✅ | ❌ | ❌ |
| Win 8.1 → Win 10 | ✅ (dados) | ✅ | ❌ | ❌ |
| Mesma build (22H2→22H2) | ✅ | ✅ | ⚠️ | ✅ (maior chance) |

**Nota importante:** A Microsoft mudou o formato de `C:\Users` entre versões? **Não.** Users é sempre Users desde Vista. AppData é AppData. A estrutura não muda. Então:
- ✅ Copiar/Mover `C:\Users\[nome]\AppData` entre Win10/11 funciona (os apps leem de lá)
- ❌ O que muda é o registry — `HKCU` e `HKLM`

### Mapa da Mina — Tudo que Pode Ser Preservado

Aqui está o mapa completo, dividido por "camada de certeza":

#### Camada 1 — ✅ 100% Seguro (implementar SEMPRE)

| Item | Técnica | Por que funciona |
|------|---------|-----------------|
| `C:\Users\[nome]\Desktop` | robocopy /move | São arquivos, independentes de registro |
| `C:\Users\[nome]\Documents` | robocopy /move | Idem |
| `C:\Users\[nome]\Downloads` | robocopy /move | Idem |
| `C:\Users\[nome]\Pictures` | robocopy /move | Idem |
| `C:\Users\[nome]\Music` | robocopy /move | Idem |
| `C:\Users\[nome]\Videos` | robocopy /move | Idem |
| `C:\Users\[nome]\Favorites` | robocopy /move | Favoritos do IE/Edge — são arquivos .url |
| `C:\Users\[nome]\Links` | robocopy /move | Atalhos da barra de navegação |
| `C:\Users\[nome]\Saved Games` | robocopy /move | Salvamentos de jogos da Microsoft Store |
| `C:\Users\[nome]\Searches` | robocopy /move | Pesquisas salvas |
| `C:\Users\[nome]\Contacts` | robocopy /move | Contatos do Windows Mail |
| Pastas soltas na raiz C:\ | robocopy /move | Dados do usuário |
| `C:\Windows\Fonts` | copy | Fontes TrueType/OpenType — só copiar de volta |
| `hosts` | copy | Config de rede simples |
| Drivers (`dism /export-driver`) | dism | DISM reinjeta drivers de boa |

#### Camada 2 — ✅ Muito Seguro (implementar como padrão)

| Item | Técnica | Notas |
|------|---------|-------|
| `C:\Users\[nome]\AppData\Local\Google\Chrome\User Data` | move com Users | Bookmarks, extensões, histórico, cookies funcionam |
| `C:\Users\[nome]\AppData\Roaming\Mozilla\Firefox\Profiles` | move com Users | Idem |
| `C:\Users\[nome]\AppData\Local\Microsoft\Edge\User Data` | move com Users | Idem |
| `C:\Users\[nome]\AppData\Roaming\Microsoft\Outlook` | move com Users | PST/OST files — Outlook precisa configurar conta de novo |
| `C:\Users\[nome]\AppData\Roaming\Microsoft\Windows\Network Shortcuts` | move com Users | Atalhos de rede |
| `C:\Users\[nome]\AppData\Roaming\Microsoft\Windows\Printer Shortcuts` | move com Users | Atalhos de impressora |
| `C:\Users\[nome]\AppData\Local\Microsoft\Windows\Caches` | move com Users | Cache de miniaturas etc. |
| `C:\ProgramData\Microsoft\Windows\Start Menu` | move | Atalhos no Menu Iniciar (só programas já reinstalados) |
| `C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup` | move | Programas de inicialização |
| Bookmarks do browser | move com AppData | ✅ Sempre funciona |
| Salvamentos de jogos (Steam, etc.) em AppData | move com AppData | ✅ Se for só save file |
| Config de clientes de email (Thunderbird, etc.) | move com AppData | ✅ Contas, filtros, regras |

#### Camada 3 — ⚠️ Possível Mas Imperfeito (opcional, checkbox)

| Item | Técnica | Funciona? | Notas |
|------|---------|-----------|-------|
| `C:\Program Files` (binários) | robocopy /move | ⚠️ Programas não abrem (sem registry) | Mas portáteis sim. Útil como backup "raw" |
| `C:\Program Files (x86)` | robocopy /move | ⚠️ Idem | Idem |
| `C:\ProgramData\[apps]` | move | ⚠️ Depende do app | Alguns apps leem daqui + registry |
| Personalização (tema, wallpaper) | reg export HKCU\...\Themes | ✅ | Import pós-instalação |
| Config do Explorer | reg export HKCU\...\Explorer | ✅ | Layout, atalhos |
| Taskbar | reg export HKCU\...\Taskband | ✅ | Pins da taskbar |
| Config de rede (WiFi profiles) | `netsh wlan export profile` | ✅ | Import pós-instalação |
| Certificados do Windows | `certutil -exportPFX` | ✅ | Import de novo (com senha) |
| Variáveis de ambiente do usuário | reg export HKCU\Environment | ✅ | PATH customizado etc. |
| Impressoras | `printui /Ss` (export) | ⚠️ | Driver precisa ser reinstalado |
| Fontes baixadas (Google Fonts, etc.) | copy de Fonts | ✅ | Só colocar de volta |
| `C:\Windows\System32\drivers\etc\hosts` | copy | ✅ | |
| `C:\Users\[nome]\NTUSER.DAT` (registry inteiro do usuário) | reg save / robocopy | ⚠️ | Se SID bater, funciona. Se não, Windows cria perfil .OLD |

#### Camada 4 — ❌ Não Funciona Ou Muito Arriscado

| Item | Técnica | Por que não funciona |
|------|---------|---------------------|
| **Programas instalados (.exe/.msi)** | qualquer uma | Registry HKLM perdido — sem as entries de instalação, Windows não sabe que o programa existe |
| **HKLM completo** (SAM, SECURITY, SOFTWARE, SYSTEM) | reg save + restore | **BSOD** — drivers, SIDs, hardware IDs diferentes |
| **Microsoft Store apps** | qualquer uma | Licenciamento + AppX manifest + registry específicos |
| **Windows Hello / PIN** | qualquer uma | Atado ao TPM + SID da máquina |
| **Senhas de navegador (DPAPI)** | reg export / copy AppData | Criptografadas com hash do SID + machine key |
| **Certificados EFS** | copy | Encrypting File System — chave do usuário + SID |
| **OneDrive config** | copy | Precisa reautenticar (token vinculado ao SID + conta MS) |
| **BitLocker** | N/A | Precisa da chave de recuperação |
| **Ativação do Windows (digital license)** | N/A | Vinculada ao hardware + conta MS. Geralmente reativa sozinha |
| **Drivers de dispositivos** | copy de System32\drivers | DISM /Add-Driver funciona **se** os drivers forem compatíveis |

### Resumo das Estratégias Recomendadas (em ordem de ambição)

#### Estratégia A — "Segura" (implementação inicial)

```
Move:
  ✅ C:\Users\*           → C:\!\Users
  ✅ C:\ProgramData\*     → C:\!\ProgramData
  ✅ C:\Windows\Fonts     → C:\!\Fonts
  ✅ C:\ (pastas avulsas) → C:\!\_root
  ✅ hosts                → C:\!\hosts.txt
  ✅ Exporta drivers       → C:\!\Drivers

Não move:
  ❌ Program Files
  ❌ Program Files (x86)

Pós-instalação:
  ✅ Restaura Users, ProgramData, Fontes, hosts, drivers
  ✅ Cria script de reinstalação (lista de programas)
```

**Perde:** programas instalados, personalização, senhas.

#### Estratégia B — "Com Registry HKCU" (adição à A)

```
Move tudo da A, MAIS:

Pré:
  ✅ Exporta HKCU\Software\... (temas, explorer, chrome, firefox, taskbar)
  ✅ netsh wlan export profile → C:\!\WiFi
  ✅ certutil -exportPFX → C:\!\Certificates

Pós:
  ✅ Importa HKCU .reg files
  ✅ netsh wlan add profile
  ✅ Importa certificados
```

**Perde:** programas instalados, senhas de navegador.

**Ganha:** tema, wallpaper, taskbar pins, bookmarks, WiFi, histórico.

#### Estratégia C — "Tudo Incluindo Programas" (tipo Laplink, mas feito por nós)

```
Move tudo da A + B, MAIS:

Pré:
  ✅ Move C:\Program Files       → C:\!\Program Files
  ✅ Move C:\Program Files (x86) → C:\!\Program Files (x86)
  ✅ reg save HKLM\SOFTWARE      → C:\!\reg\HKLM_SOFTWARE
  ✅ reg save HKLM\SYSTEM        → C:\!\reg\HKLM_SYSTEM (parcial — só CurrentControlSet\Services filtrado)
  ✅ reg save HKLM\SAM           → C:\!\reg\HKLM_SAM (NÃO restaurar)

Pós (antes do primeiro boot):
  ✅ Restaura Program Files (binários)
  ⚠️ Restaura HKLM\SOFTWARE com FILTRO (remove keys de hardware/drivers)
  ❌ NÃO restaura HKLM\SYSTEM (drivers)
  ❌ NÃO restaura HKLM\SAM (contas)
```

**Riscos:**
- Mesmo filtrando keys de hardware, alguns drivers podem conflitar
- Programas com licenciamento tied to HWID podem pedir reativação
- Requer uma lista de exclusão (keys que não podem ser restauradas)
- **Se não filtrar corretamente = BSOD**

**Dá para fazer:** sim, mas requer uma base de dados de "bad keys" que cresce com o tempo.

### Plano de Testes em VM

O usuário vai testar:

1. **VM1:** Windows 10 → Windows 10 (mesma build) — ver o que funciona
2. **VM2:** Windows 10 → Windows 11 — ver diferenças
3. **VM3:** Windows 11 → Windows 11 (build diferente) — compatibilidade
4. **VM4:** Com Program Files movidos → testar se restaurar HKLM\SOFTWARE filtrado funciona

Métricas:
- Login do usuário funciona? (perfil novo vs. antigo)
- Desktop, Documents, Downloads intactos?
- Chrome/Firefox abre com histórico e bookmarks?
- Taskbar pins preservados?
- Programas portáteis funcionam?
- Programas instalados funcionam (se Strategy C)?
- Senhas do navegador perdidas (esperado)?
- WiFi reconecta automaticamente?

### Estratégia C Detalhada — Merge de Registry (a "Ideia do Usuário")

O insight: temos **dois registries completos** — o velho (com programas) e o novo (com drivers corretos). Em vez de escolher um, **mesclamos**.

#### Fluxo

```
Fase 1 (WinPE):
  1. Move Users, ProgramData, etc. → C:\!
  2. reg save HKLM\SOFTWARE C:\!\reg\OLD_SOFTWARE
  3. reg save HKLM\SYSTEM   C:\!\reg\OLD_SYSTEM
  4. reg save HKLM\SAM      C:\!\reg\OLD_SAM      (guardar, não usar)
  5. Move C:\Program Files      → C:\!\Program Files
  6. Move C:\Program Files(x86) → C:\!\Program Files (x86)
  7. DISM apply Windows novo em C:\

Fase 2 (WinPE, ainda offline):
  8. Carrega os dois registries:
     reg load HKLM\NewSys  C:\Windows\System32\config\SYSTEM
     reg load HKLM\NewSft  C:\Windows\System32\config\SOFTWARE
     reg load HKLM\OldSft  C:\!\reg\OLD_SOFTWARE

  9. MERGE: varre OldSft, para cada key:
     - Se é key de programa (Adobe, Google, etc.) → copia para NewSft
     - Se é key Microsoft → NÃO copia (usa a do Windows novo)
     - Se é key de hardware/driver → NÃO copia
     - Classes (associações de arquivo) → copia se for de programa, não se for Microsoft

  10. Descarrega:
      reg unload HKLM\NewSys
      reg unload HKLM\NewSft
      reg unload HKLM\OldSft

  11. Restaura Program Files de C:\! para C:\
  12. Restaura Users, ProgramData, Fontes, etc.
  13. bcdboot + reboot

Resultado: Windows novo com drivers corretos + programas do Windows velho.
```

#### Por que isso funciona (e é melhor que Laplink)

| Abordagem | Risco de BSOD | Programas funcionam | Drivers corretos |
|-----------|--------------|--------------------|------------------|
| Restaurar HKLM inteiro do velho | **ALTO** (drivers errados) | ✅ | ❌ |
| Manter HKLM do novo + nada | ✅ (zero) | ❌ Nenhum | ✅ |
| **Merge (nossa ideia)** | **✅ Mínimo** (só keys de programa) | ✅ | ✅ |

**Vantagem:** o `SYSTEM` (que contém driver mappings, services, etc.) é 100% do Windows novo. Nós só tocamos no `SOFTWARE` — e só as partes de programas, não de sistema.

#### Implementação do Merge

O merge precisa de um script que separe keys de programa de keys de sistema:

```batch
:: Estratégia de merge: lista de allow/deny
::
:: DENY (NUNCA copiar do velho):
::   Microsoft\*
::   Classes\Interface\*
::   Classes\CLSID\{...} se for Microsoft
::   Classes\TypeLib\{...} se for Microsoft
::   Wow6432Node\Microsoft\*
::   ODBC\*
::   Program Groups (vêm do novo)
::
:: ALLOW (SEMPRE copiar do velho se existir):
::   [VendorName]\[Product] (Adobe, Google, Mozilla, Valve, ...)
::   Microsoft\Windows\CurrentVersion\App Paths\[programa]
::   Microsoft\Windows\CurrentVersion\Uninstall\[programa]
::   Classes\[.ext] (se for de programa conhecido)
::   Classes\[ProgID] (se for de programa conhecido)
::   Wow6432Node\[VendorName]\[Product]
::   Microsoft\Windows\CurrentVersion\Explorer\MyComputer (pastas especiais)
```

**Mas tem um jeito mais simples:** exportar do velho APENAS as keys de programas conhecidos + classes associadas + App Paths + Uninstall. É uma lista fixa que cresce com o tempo.

Ou, ainda mais simples:

```batch
:: Abordagem "diff":
:: 1. Exporta OldSoftware COMPLETO
:: 2. Exporta NewSoftware COMPLETO  
:: 3. Converte ambos para .reg (formato texto)
:: 4. Remove do OldSoftware.reg toda linha que contém "Microsoft\"
:: 5. Aplica OldSoftware_filtrado.reg sobre NewSoftware
:: 6. Pronto — só keys de programas sobrevivem
```

**Ressalva:** keys `Microsoft\Windows\CurrentVersion\App Paths` e `Uninstall` são exceções — queremos elas mesmo sendo Microsoft. Então o filtro é mais sutil:

1. Remover do old: `Microsoft\*` EXCETO `Microsoft\Windows\CurrentVersion\App Paths\*`, `Microsoft\Windows\CurrentVersion\Uninstall\*`
2. Remover tudo que é COM/CLSID (muito volátil entre versões)
3. Aplicar o resto sobre o new

#### Teste em VM — Plano

1. VM com Windows e Chrome, Firefox, 7-Zip, Notepad++, VLC instalados
2. Rodar o merge script
3. Verificar: quais apps abrem? Quais quebram?
4. Ajustar lista de allow/deny
5. Repetir com apps mais complexas (Office, Adobe Reader, Steam)

---

### Próximos Passos Imediatos

1. ✅ Documentar plano completo (este documento)
2. ❏ Implementar Estratégia A no KitLugia (fase 1: mover + apply + restaurar dados básicos)
3. ❏ Implementar merge de registry (Estratégia C — scripts de export/filtro/import)
4. ❏ Testar em VM com cenários reais (10→10, 10→11, 11→11)
5. ❏ Criar unattend.xml para OOBE automático sem perguntas
