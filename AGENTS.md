# KitLugia — AGENTS.md

## REGRAS DO PROJETO

1. **NUNCA usar hardcoded paths** (ex: `C:\Users\...`, `C:\KL_WINPE\arquivo.exe`).
   Todo caminho de recurso do app (WinXShell, Explorer++, drivers, tools) deve ser
   relativo a `AppDomain.CurrentDomain.BaseDirectory` (kit publicado) ou resolvido
   por candidatos relativos. O kit e publicado (VS/Deploy) e movido para outras
   maquinas/VM — um path absoluto quebra la. Excecao legitima: `C:\KL_WINPE` como
   DIRETORIO de staging do WinPE (BCD ramdisk referencia), nunca para achar recursos.

2. **Deploy.ps1 NAO tocar** — o usuario publica pelo Visual Studio; o Deploy.ps1 e
   fluxo alternativo, qualquer alteracao precisa de ordem explicita.

3. Arquivos novos do projeto nascem com encoding correto (ver .editorconfig: UTF-8+BOM).

## Estado atual do projeto (29/07/2026)

### O que foi feito

1. **SCHEDULE refatorado**: usa `UpdateWimWithScriptAsync` (wimlib, comando `--command` único) + `InjectConfigIntoWimAsync` (agora tenta wimlib primeiro). Fallback DISM aceita `scriptName` para não sobrescrever bridge.

2. **diskpart.exe injetado no WIM VALOS**: `InjectDiskpartIntoWimAsync` copia do host via wimlib (VALOS base não inclui).

3. **Winlogon Shell + SYSTEM\Setup\CmdLine configurados** no registro offline do VALOS via `ConfigureValosShellAsync`:
   - `HKLM\...\Winlogon\Shell = cmd /k C:\Windows\System32\startnet.valos.cmd`
   - `HKLM\SYSTEM\Setup\CmdLine = cmd /k C:\Windows\System32\startnet.valos.cmd` (fallback)
   - O log mostra o valor **atual** do `Setup\CmdLine` antes de sobrescrever

4. **Bridge startnet.cmd corrigida**: agora checa tanto `X:\` (WinPE) quanto `C:\` (VALOS) para `startnet.valos.cmd`.

5. **WinXShell injetável**: `InjectWinXShellIntoWimAsync` + `ResolveWinXShellAsync`:
   - Procura localmente em `KitLugia.WinPE\WinXShell\WinXShell.exe`
   - Fallback: download de `https://github.com/luigiarrud4/KitLugia-WinPE/releases/download/v1.0/WinXShell.exe`
   - Injeta no WIM via wimlib em `C:\Windows\System32\WinXShell.exe`

6. **Script VALOS modificado**: se `WinXShell.exe` estiver presente no WIM e não houver shrink pendente, lança WinXShell como GUI automaticamente.

7. **/Optimize removido** de todas as 8 chamadas DISM.

8. **Condição `!DISK_N!` removida** do RamdiskStartnetCmd (DISK_N=0 é válido).

### Fluxo de uso

**SCHEDULE (shrink automático)**:
- PREPARE cria WIM com script + bridge + registro
- SCHEDULE injeta shrink_config.ini + script atualizado
- VALOS boota, shrink roda, reboot
- WinXShell NÃO é necessário

**TESTAR (modo GUI)**:
- Clica TESTAR → injeta WinXShell no WIM
- VALOS boota com WinXShell como interface
- Útil para debug/inspeção manual

### Proxima sessao: DOCUMENTACAO + TESTES

Pendencias ainda abertas:
- [ ] Testar PREPARE + SCHEDULE → reboot → shrink
- [ ] Testar WinXShell injection → boot VALOS com GUI
- [ ] Validar se `CreateLegacyBootEntry` (bootsector BCD com isolinux.bin)
      funciona em Legacy real (pode precisar de GRUB4DOS `grldr.mbr`)
- [ ] Se VALOS ainda bootar cmd.exe: diagnosticar WIM (winpeshl.ini, registry)

### Sessao 30/07 — Correcao de Boot MultiISO

**Bug**: `CreateDirectNvramBoot` havia sido alterado para usar rEFInd
(deploy no ESP, substituindo bootmgfw.efi). Isso quebrou o boot Linux
em maquinas sem suporte a rEFInd ou onde a substituicao do bootmgfw
nao funcionava.

**Correcao**: Restaurado metodo `CreateDirectNvramBoot` original
(bcdedit puro):
1. `bcdedit /copy {bootmgr}` — clona entry do boot manager
2. `bcdedit /set {guid} device partition=X:`
3. `bcdedit /set {guid} path \EFI\...\grubx64.efi`
4. `bcdedit /displayorder {guid} /addlast`
5. `bcdedit /set {fwbootmgr} bootsequence {guid}` — BootNext NVRAM

O passo 5 contorna o erro 0xc000007b (WBM nao consegue chainload
binarios nao-Windows) pois faz o firmware pular o WBM e ir direto
para o bootloader do Linux via NVRAM.

**Outras correcoes**:
- `WinbootPage.xaml.cs`: removido fluxo `CreateEfiBootEntry` para
  Linux (voltou a usar `CreateDirectNvramBoot` direto), removido
  `{kitlugia-linux-legacy}` fallback, removido dead code
  `{kitlugia-uefi-jump}`
- `WinbootManager.cs`:
  - `CreateLegacyBootEntry` — novo metodo, cria entrada BCD bootsector
    real para isolinux.bin (Legacy BIOS)
  - `CreateRamdiskEntry` — validacao null de wimPath/sdiPath
  - `AnalyzeSevenZipOutput` — defaults para WimPath/SdiPath em ISO
    Windows
- `docs/MULTIISO_BOOT_ARCH.md` — documentacao da arquitetura de boot

### Arquivos modificados

- `KitLugia.Core\WinpeBuilder.cs`: ConfigureValosShellAsync (dual registry + log), InjectConfigIntoWimAsync (wimlib fallback), InjectDiskpartIntoWimAsync, InjectBootFilesIntoWimAsync (scriptName), InjectWinXShellIntoWimAsync, ResolveWinXShellAsync, /Optimize removido
- `KitLugia.Core\WinbootManager.cs`: CreateDirectNvramBoot (restaurado p/bcdedit), CreateLegacyBootEntry (novo), CreateRamdiskEntry (validacao null), RamdiskStartnetCmd, ValidationOsStartnetCmd (WinXShell launch), bridge startnet.cmd (C:\ check), chamada ConfigureValosShellAsync em PREPARE
- `KitLugia.GUI\Pages\WinbootPage.xaml.cs`: fluxo Linux UEFI (sem CreateEfiBootEntry, sem sentinelas mortas)

### Sessao 31/07 � WindowsUpdatePage: ComboBox + Controle de Updates + Plano de Downgrade

1. **ComboBox de volta** (CmbChannel/CmbPauseDays) com scroll travado:
   - `DisableMouseWheelSelection` em WindowsUpdatePage.xaml.cs (~linha 238):
     `PreviewMouseWheel += (s,e) => e.Handled = true` (popup aberto nao e
     afetado - e outra janela). Chamado no Loaded.
   - Estilos `DarkComboBoxStyle`/`DarkComboBoxItemStyle` restaurados em
     Page.Resources; alias `using RadioButton` removido, ComboBox
     totalmente qualificado (System.Windows.Controls.ComboBox).

2. **UpdateControlManager.cs** (KitLugia.Core, novo): ListInstalledUpdates
   (Get-HotFix), UninstallUpdate(kb) (wusa /uninstall), InstallUpdatePackage
   (.msu->wusa, .cab->DISM), DescribeExitCode(0/3010/2359302/87/-1).

3. **Card "Controle de Updates (nao-Insider)"** na WindowsUpdatePage:
   instalar .msu/.cab, listar KBs instalados, remover KB (downgrade).

4. **Pesquisa web completa sobre downgrade de build Insider->Stable**:
   - VEREDITO: possivel. O bloqueio esta em `sources/setupcompat.dll` da
     ISO alvo, funcao `ConX::Setup::Common::CWindowsVersion::IsLaterThan`:
     trocar `B8 01` (MOV eax,1) por `B8 00` no fim da funcao habilita
     "Keep personal files and apps". Metodo testado (Reddit qtw8fq:
     22494.1000 -> 22000.318; 22518.1012 -> 22000.318).
   - Metodo semi-oficial (MS Answers): alvo MAIS NOVO que instalado -> apagar
     `HKLM\SOFTWARE\Microsoft\WindowsSelfHost` + ISO in-place.
   - Sem relatos recentes (2024-26) do patch em midia 24H2/25H2 -> validar.

5. **Ferramentas baixadas/instaladas** (31/07/2026):
   - HxD INSTALADO: C:\Program Files\HxD\HxD.exe
   - IDA Free 8.4: BAIXADO (sem modo silencioso, instalar manual):
     KitLugia.GUI\Tools\IDA Free\idafree84_windows.exe
   - aria2 1.37.0: KitLugia.GUI\Tools\aria2\
   - UUP Dump package build 26300.9032 (x64, pt-br, Professional):
     KitLugia.GUI\Tools\uup-dump\26300.9032_amd64_pt-br\
     (rodar uup_download_windows.cmd como admin para montar a ISO)
   - VMware Workstation ja existe no host (C:\Program Files\VMware\...)

6. **docs/DOWNGRADE_BUILD_PLAN.md** (novo): plano completo com links,
   metodo passo-a-passo, automacao proposta (SetupCompatPatcher em
   KitLugia.Core: achar string IsLaterThan, patch B8 01->B8 00, backup
   .orig), fases de validacao em VM, checklist.

### Proxima sessao
- [ ] Instalar IDA Free manualmente
- [ ] Rodar uup_download_windows.cmd para gerar ISO 26300.9032
- [ ] Validar setupcompat.dll da ISO no IDA (Fase 2 - confirmar IsLaterThan)
- [ ] Implementar SetupCompatPatcher (KitLugia.Core)
- [ ] Testar em VM (VMware): build 28000 + patch -> setup -> 26300
- [ ] UI no WinbootPage/UpdatesPage: opcao "Downgrade de build"


### Sessao 31/07 (noite) — Fase 2 CONCLUIDA: patch confirmado na 25H2 26200.8973

1. **IDA Pro 9.0 fornecido pelo usuario** (C:\Users\Lugia\Downloads\IDA Professional 9.0\IDA Professional 9.0\):
   substituiu o IDA Freeware (descartado: nao tem IDAPython). idat.exe batch funcional;
   IDAPython ligado ao Python 3.11.9 via idapyswitch --force-path. Quirks aprendidos:
   - ida_auto.auto_wait() obrigatorio no inicio do script (sem ele o decompiler
     so desmonta 1 instrucao e o corpo vira JUMPOUT)
   - Apagar .i64 obsoleto antes de reanalisar o mesmo arquivo
   - -S precisa de aspas explicitas (usar cmd /c com concatenacao de strings)
   - EULA aceito via chaves EULA 90-EULA 93=1 em HKCU:\Software\Hex-Rays\IDA

2. **ISO 25H2 26200.8973 gerada** (uup_download_windows.cmd em C:\uup\26200.8973_amd64_pt-br):
   26200.8973.260724-1524.25H2_GE_RELEASE_SVC_PROD3_CLIENTPRO_OEMRET_X64FRE_PT-BR.ISO (9,86 GB).

3. **Fase 2 - setupcompat.dll analisada (achados)**:
   - String IsLaterThan NAO existe como string literal; a funcao existe e esta nomeada
     pela analise (sem PDB): ?IsLaterThan@CWindowsVersion@Common@Setup@ConX@@QEBAHAEBU1234@@Z
     @ **VA 0x180002CE4** (a auto-analise do IDA nomeia 150+ funcoes ConX:: via RTTI/signatures)
   - Cadeia: CWindowsVersion::IsLaterThan(host,target) <- CSystemAbstraction::HostIsNewer (0x180025948)
     <- HostIsNewerCheckerImpl::OnInvoke (0x180010550): se host > target -> Issue 11 = **HardBlock**
   - **Ponto de patch**: FILE OFFSET **0x2DFD** da setupcompat.dll da midia = byte 01 do epilogo
     unico B8 01 00 00 00 C3 (todos os "return 1" convergem nele; depois vem 33 C0 C3 = return 0)
   - Verificado: DLL patcheada decompila com todos os return 1 -> return 0
   - DLLs: original C:\ida_test\isofiles\setupcompat.dll (374.248 B); patcheada C:\ida_test\patched\setupcompat.dll
   - Scripts em C:\ida_test\: decomp_hostisnewer.py, locate_islaterthan.py, decomp_islaterthan.py,
     verify_patched.py, scan_names.py, analyze_setupcompat.py (v1 obsoleto), get_pdb_info.py

4. **Ferramenta de 2 cliques criada** (fora do KitLugia.Core, decisao do usuario):
   - `KitLugia.GUI\Tools\Downgrade\patch_setupcompat.ps1` — busca o pattern 9B
     `B8 01 00 00 00 C3 33 C0 C3` (fallback 6B unico), patcha byte 0x2DFD 01->00,
     backup .orig, verifica apos patch. Exit codes: 0 ok/ja-patched, 1 NOT_FOUND,
     2 AMBIGUOUS, 3 verificacao falhou, 4 DLL ausente.
   - `KitLugia.GUI\Tools\Downgrade\DowngradePatch.cmd` — banner, auto-detecta 7z
     (kit + Program Files) e ISO em C:\uup, extrai via 7z (pula se ja extraiu),
     chama o .ps1, registro Insider opcional (backup + delete WindowsSelfHost,
     requer admin; arg `noreg` pula), SHA256 da DLL patcheada, instrucoes finais.
   - **Bugs corrigidos nos testes**: `set /p` engolia stdin redirecionado ->
     sub-rotina `:ask` via `Read-Host` do PowerShell (prompt no stderr via
     `[Console]::Error.Write`, valor capturado no stdout pelo for /f); com
     delayed expansion, `%ASKVAL%` dentro de blocos `if (...)` vira `!ASKVAL!`.
   - **Testado** (31/07 noite): args+EOF (PATCHED/ALREADY_PATCHED + SHA256 +
     exit 0), ISO inexistente (exit 1), fluxo interativo com stdin pipeado
     (1o prompt le, mas cmd /c do for /f drena stdin restante em < arquivo:
     para automacao usar ARGS "iso" "pasta" noreg — console real OK).

### Proxima sessao
- [x] ~~Implementar SetupCompatPatcher (KitLugia.Core)~~ -> SUBSTITUIDO pela
      ferramenta standalone Tools\Downgrade\ (decisao do usuario)
- [ ] Testar em VM (VMware): build 28000 + patch -> setup -> 26200.8973 preservando dados
- [ ] UI no WinbootPage/UpdatesPage: opcao "Downgrade de build"
- [ ] Testar no app: toggle "Boost do App Ativo" + perfil personalizado (ComboBox Normal/High/RealTime)


### Sessao 01/08 — GameBoost Pro: Toggle "Boost do App Ativo" (prioridade temporaria com revert)

Recurso vendido por app de $28 na Steam (priority affinity + background affinity).
KitLugia NAO tinha controle dedicado — o motor aplicava prioridade fixa High sem toggle.

1. **Toggle "BOOST DO APP ATIVO"** em GameBoostPage.xaml (card "GameBoost Ativo", abaixo dos indicadores):
   - Quando ON: o motor automatico aumenta a prioridade do processo em foreground
     enquanto ele estiver em foco e REVERTE ao perder o foco (reversao automatica ja existente).
   - Persistencia: registry `TraySettings\ForegroundBoost` (default 1) + JSON gameboost_settings.json.
   - `ForegroundBoostEnabled` em TrayIconService gateia TODAS as mudancas de prioridade:
     ApplyBoostCustom, ApplyBoostV1/V2/V3, OptimizeForegroundProcess, RevertBoost.
   - `RevertCurrentBoost()` (publico): reverte `_currentBoostedPid` e `_lastBoostedPid`
     (usado ao desligar o toggle ou via ShutdownGameBoost).
   - Obs: sliders 1-20 (Priority Affinity / Background Affinity) foram testados e REMOVIDOS
     a pedido do usuario — controle continua simples via ComboBox Normal/High/RealTime.

2. **Build**: 0 erros / 102 warnings (nullable pre-existentes).
   Obs: fechar o app antes de compilar (MSB3021 DLL bloqueada pelo processo rodando).

### Sessao 01/08 (cont.) — GameBarPresenceWriter: renomeacao no startup (sem timer)

Decisao do usuario: SIMPLIFICAR. Nao usar camada de registro (ActivationType/GameDVR),
nao usar watchdog/timer. O kit so renomeia GameBarPresenceWriter.exe para .bak
(excluindo o .bak anterior) UMA VEZ ao iniciar o PC, conforme preferencia salva
(registry `TraySettings\GameBarPresenceWriterDisabled` + JSON).

- `AutoFixGameBarPresenceWriter` (TrayIconService): roda no `Initialize()` via
  Task.Run (uma unica vez). Se .exe existe e .bak tambem -> exclui .bak antigo e
  renomeia o novo. Se so .exe -> renomeia. Preferencia desativada = nada faz.
- Handler `ChkGameBarPresenceWriter_Click` (page): renomeia/restaura o .exe manualmente.
- Removido: `ApplyGameBarPresenceWriterRegistryLayers` (camada COM/politica),
  watchdog no `MonitorTick`, constantes de registro.
- Ideia descartada (avaliada): placeholder .txt read-only — TrustedInstaller
  substitui arquivos read-only em servicing; poderia quebrar o Windows Update.

### Sessao 01/08 (cont.) — Otimizacoes da Comunidade (Reddit): 5 toggles reais

Painel "Mostrar mais" no card GameBarPresenceWriter (Botao BtnShowMoreProcesses +
PanelMoreProcesses, informativo) + NOVO card "Otimizacoes da Comunidade (Reddit)"
(Grid.Row 5, entre GameBar e Download Boost) com 5 toggles funcionais.

Decisao do usuario: achava que renomear .exe era mais efetivo, mas aceitou o metodo
correto da comunidade (servico/tarefa/registro, NAO rename — TrustedInstaller reverte).

Metodos por processo (`ApplyCommunityProcessToggle(name, disable)` em TrayIconService):
- SmartScreen: `HKLM\...\Policies\...\System\EnableSmartScreen=0` + Explorer SmartScreenEnabled="Off"
- EdgeUpdate: `sc config edgeupdate/edgeupdatem start= disabled` + schtasks /Disable
  MicrosoftEdgeUpdateTaskMachineCore/UA + taskkill
- CompatTelRunner: `sc config DiagTrack start= disabled` + AllowTelemetry=0 +
  schtasks /Disable (Compatibility Appraiser, ProgramDataUpdater, StartupAppTask) + taskkill
- SearchIndexer: `sc config WSearch start= disabled` + sc stop
- TextInputHost: IFEO `...\Image File Execution Options\TextInputHost.exe\Debugger =
  %SystemRoot%\system32\systray.exe` (bloqueia sem renomear; Win+. e teclado virtual
  desativados) + taskkill. Restaurar: deleta subchave IFEO.

Persistencia: registry `TraySettings\SmartScreenDisabled/EdgeUpdateDisabled/
CompatTelRunnerDisabled/SearchIndexerDisabled/TextInputHostDisabled` + JSON
gameboost_settings.json (chaves smartScreenDisabled, edgeUpdateDisabled, ...).

Startup: `AutoFixCommunityProcesses()` roda no Initialize (Task.Run, uma unica vez,
idempotente) — aplica os toggles salvos. Sem timer.

Handlers: `TglCommunityProcess_Click` unico com Tag (nome do processo), captura
trayService, salva preferencia, aplica via Task.Run. LoadSettings restaura os 5
toggles do TrayService.

Build: 0 erros / 102 warnings (nullable pre-existentes).

### Sessao 01/08 (cont.) — Bug BCD ramdisk: letra de drive duplicada (E::)

Sintoma (WinbootPage com Sergei Strelec PE): bcdedit falhava com "O dispositivo
não é válido como especificado" ao configurar a entrada ramdisk:
- `ramdisksdidevice partition=E::` (código 1)
- `device ramdisk=[E::]\SSTR\strelec10x64Eng.wim,{ramdiskoptions}` (código 1)

Causa raiz: `PartitionManager` retorna `DriveLetter` com dois-pontos ("E:") e
`CreateRamdiskEntry`/`CreateWinpeFlatEntry` faziam `$"{driveLetter}:"` ->
"E::" invalido.

Correcao (WinbootManager.cs): normalizacao defensiva nos 2 metodos:
`string part = driveLetter.Trim().TrimEnd(':') + ":";` — aceita "E" ou "E:"
e garante "E:". Cobre os 7+ chamadores (WinbootPage, EmergencyBoot, MultiISO).
Os metodos Linux (CreateDirectNvramBoot, CreateLegacyBootEntry, PatchLinuxConfig)
ja normalizavam com Replace(":", "") — sem alteracao.

Build: 0 erros.

**TESTADO (01/08 noite, VMware)**: WinbootPage Multi-ISO com Sergei Strelec PE agora
funciona — `ramdisksdidevice partition=E:` (código 0), `ramdisk=[E:]\SSTR\strelec10x64Eng.wim`
(código 0), entrada BCD criada. Obs: `bcdedit /create {ramdiskoptions}` retorna código 1
quando o objeto ja existe (comportamento normal, o fluxo continua).

**BUG 2 (01/08 noite)**: entrada ramdisk criada mas NAO aparecia no menu de boot —
`CreateRamdiskEntry` nao chamava `/displayorder` (so o CreateWinpeFlatEntry fazia).
Corrigido: adicionado `bcdedit /displayorder {guid} /addlast` + `/timeout 10` +
`recoveryenabled No` apos winpe yes. Re-testar: rodar WinbootPage de novo e conferir
o menu na inicializacao (timeout 10s).

**BUG 2 RE-TESTADO (01/08 23:57, VMware)**: displayorder e timeout agora retornam
código 0 no log. Pendente: reiniciar a VM e confirmar que o menu do Windows Boot
Manager aparece com a entrada do Sergei Strelec (timeout 10s) e que o PE boota.
Tambem adicionado `CleanupOldRamdiskEntries` (remove entradas ramdisk antigas por
descricao igual ou {ramdiskoptions}+KitLugia) para evitar duplicacao ao re-rodar.

### Proxima sessao
- [ ] Re-testar WinPE Shrink na VM (ver abaixo — script reordenado para usar alvo configurado)
- [ ] Testar toggle "Boost do App Ativo" no app (perfil custom com RealTime ativo / revert para Normal)
- [ ] Testar GameBarPresenceWriter: desativar no kit → reiniciar PC → confirmar que
      renomeou para .bak no startup
- [ ] Testar toggles da comunidade: ativar cada um → confirmar via services.msc/
      taskschd.msc → reiniciar → confirmar reaplicacao no startup
- [x] ~~Re-testar WinbootPage Multi-ISO com Sergei Strelec PE~~ (BCD ramdisk com E: correto — OK)
- [ ] (opcional) API key no gepetto/config.ini (plugins\_gepetto_disabled) para analise com IA

### Sessao 02/08 — BUG 3: WinPE Shrink no volume errado (C: em vez do alvo configurado)

Sintoma (teste VMware 02/08 00:20): SCHEDULE configurou DISK_N=0 PART_N=4
(KITLUGIA E:, 66GB, shrink 50000MB), mas o WinPE bootou e o log do script mostrou:
"Found Windows on C: - selecting volume C directly / Using volume C for shrink..."
→ diskpart erro "The specified shrink size is too big" (C: cheio, nao era o alvo).

Causa raiz: `RamdiskStartnetCmd` (WinbootManager.cs ~linha 5636) colocava o check
`if exist C:\Windows\System32\config\SOFTWARE` como PRIMEIRA prioridade — como o
Windows sempre esta em C:, o script ia para `:run_vol_c` e nunca chegava ao alvo
configurado (embedded/ini/marcador).

Correcao (prioridade reordenada no startnet.cmd gerado):
1. **Alvo embutido** (disk/part do scheduler, `E_DISK/E_PART`) — validacao trocada de
   `if exist Z:\Windows\...SOFTWARE` (exigia Windows no alvo!) para `if exist Z:\`
   (basta a particao existir — o alvo KITLUGIA nao tem Windows). Mais `goto :run`.
2. **shrink_config.ini de X:** — ANTES so lia SHRINK_MB; agora tambem le
   `DISK_N`/`PART_N` e vai direto para `:run` se PART_N != 0.
3. Scan por `KL_SHRINK_TARGET.dat` (marcador) — inalterado.
4. **C: como fallback** (`:run_vol_c`) — so se nada configurado existir.
5. Scan de todos os discos por SOFTWARE hive — inalterado.
6. Nada encontrado → erro + reboot.

Bonus: `:run` e `:run_vol_c` agora capturam a saida do diskpart em X:\s_out.txt,
gravam `Status: OK/FAIL` no result.log via findstr (error/erro/fail/insufficient/
"too big"/"not enough"/"muito grande") e anexam o output completo do diskpart ao
log persistente (antes gravava Status: OK incondicionalmente, mesmo em falha).

Build: 0 erros.

**A TESTAR (VMware)**: SCHEDULE de novo → reboot → confirmar no log que o script
escolheu DISK=0 PART=4 (KITLUGIA E:) via "Using embedded target" ou "Found config",
shrink OK, log persistente em E:\KitLugia_WinPE_Log.txt (ou C:\ no fallback).

### Sessao 02/08 (cont.) — REVERTIDO: o reordenamento NAO era o problema

O usuario testou a "correcao" (prioridade reordenada) e o shrink FALHOU:
1. `'findstr' is not recognized` — findstr NAO existe no WinPE usado.
2. Remocao do `assign letter=Z` do `:run` quebrou o diskpart ("There is no volume specified").
3. A validacao embedded `if exist Z:\` foi rejeitada; o original exigia
   `if exist Z:\Windows\System32\config\SOFTWARE`.

**Deus ex machina do fluxo**: o usuario revelou que o erro primario
(discrepancia DISK/PART entre host WMI e diskpart) SEMPRE acontece e e
inconsistente — quem sempre funcionou foi o MARCADOR `KL_SHRINK_TARGET.dat`
(host grava na raiz do drive alvo; o WinPE procura e vai direto nele).
Sem o marcador, o processo SEMPRE falha. Nao alterar o fluxo baseado em marcador.

**CORRECAO FINAL**: `RamdiskStartnetCmd` restaurado para o codigo ORIGINAL
(codigo 1) — C: check primeiro, embedded com validacao SOFTWARE, ini so le
SHRINK_MB, marker scan, scan all disks, `:run` com select disk/partition +
assign letter=Z + shrink + remove letter=Z, sem findstr. Ajustados apenas
artefatos da restauracao manual (fs.tx→fs.txt, volume X→Z, titulo do echo,
indentacao do /// summary).

**TESTADO COM SUCESSO (02/08 01:08, VMware)**: agendado 32757MB para G:
(DISK=0 PART=1 via WMI host), mas o WinPE achou o marcador na PART=2
(diskpart numero diferente do WMI — a "mira" correta); "successfully shrunk
31 GB", log persistente em Z:\KitLugia_WinPE_Log.txt, reboot normal.
Build: 0 erros / 122 warnings.

### Sessao 02/08 (cont.) — BCD: entrada UNICA de shrink (nao acumula no boot manager)

Sintoma: cada SCHEDULE criava GUID novo + `/displayorder /addlast` → dezenas
de entradas "KitLugia" acumuladas no Windows Boot Manager (o usuario
descreveu: "fica um monte de kitlugia la nas entradas").

Correcao (WinbootManager.cs):
1. **GUID fixo**: constante `ShrinkBcdGuid = "{2c9f4b6a-1e7d-4a8f-9c3b-5f6d7e8a9b0c}"`.
   `CreateRamdiskEntry` ganhou param `fixedGuid` (opcional): com ele, faz
   `bcdedit /create {guid}` (se ja existe, codigo != 0 e normal — loga e
   reusa) em vez de criar GUID novo. Outros chamadores (WinbootPage, flat,
   MultiISO) nao passam → comportamento inalterado.
2. **Sem displayorder quando fixedGuid**: a entrada NAO vai para o menu do
   Windows (nao gruda no loader). `ScheduleWinpeShrink` usa
   `bcdedit /bootsequence {guid}` (BootNext one-time via NVRAM) para bootar
   o WinPE direto.
3. **Fallback**: se bootsequence falhar (bsCode != 0), adiciona ao
   displayorder + `/timeout 10` (apos SaveOriginalBcdTimeout) para o usuario
   selecionar manualmente no boot.
4. `CleanupOldWinpeEntries`/`CleanupOldRamdiskEntries` (rodam antes de criar)
   limpam as entradas antigas acumuladas — a entrada fixa antiga e removida
   e recriada com o MESMO GUID (nunca duplica).

Build: 0 erros.

**A TESTAR (VMware)**: rodar SCHEDULE 2x seguidas → conferir no
`bcdedit /enum all` que so existe UMA entrada KitLugia (mesmo GUID),
`bootsequence` setado, e que o Windows Boot Manager nao lista a entrada
(menu limpo); reboot → shrink roda via bootsequence direto.

### Sessao 02/08 (cont.) — CAUSA RAIZ do cleanup BCD: parsing localizado

O botao LIMPAR BCD rodou mas "nao removeu nada": os parsers do
`bcdedit /enum all` procuravam cabecalhos em INGLES (`identifier`,
`description`, `device`), mas o output e LOCALIZADO (pt-BR:
`Identificador`, `Descricao`, `Dispositivo`) → GUID nunca encontrado →
`removed = 0` silencioso. Os scripts sempre conseguiram CRIAR (nao
depende do enum) mas nunca EXCLUIR.

Correcao (parsing independente de idioma em WinbootManager.cs):
1. **`FindBcdGuidsByText(params string[] mustContain)`** (novo helper):
   detecta linhas de identificador pelo **GUID standalone de 36 chars**
   (`^\S+\s+(\{[\dA-Fa-f-]{36}\})\s*$`) — linhas de device
   (`ramdisk=[...],{ramdiskoptions}`) nao casam. Linha de descricao =
   qualquer linha contendo TODAS as substrings pedidas (paths do device
   contem KL_WINPE, nunca "KitLugia").
2. `CleanupOldWinpeEntries` → usa o helper ("KitLugia","WinPE").
3. `RemoveWinpeAsync`, `RemoveValidationOs`, `RemoveCustomWinpe` →
   todos reescritos com o helper (antes tinham o mesmo bug).
4. `CleanupOldRamdiskEntries` → parsing de bloco por GUID standalone.
5. WinpeToolsPage TESTAR VALOS (`BtnBootValos_Click`) → mesmo bug
   (`identifier` → GUID standalone); sem isso o bootsequence nunca
   achava o GUID da entrada Validation OS.
6. `cleanup.bat` gerado (Winboot install) → trocado o
   `for /f ... findstr /c:"KitLugia Winboot Setup" /B /S` (nunca casava:
   a linha e `Descricao ...`, nao comeca com o texto) por PowerShell
   inline com o mesmo parsing de GUID standalone.
7. `ScheduleReinstallPreserveAsync` e `PrepareValidationOSAsync` →
   removido `skipCleanup: true` (acumulavam entrada a cada execucao;
   agora rodam o cleanup antes de criar).
8. `CreateEfiBootEntry`, `CreateLegacyBootSectorEntry`,
   `CreateDirectNvramBoot` → adicionado cleanup de bridges Linux antigos
   antes de criar (`FindBcdGuidsByText("Linux","(")` /
   ("KitLugia","Linux")) para nao acumular no menu.

**TESTADO (02/08 01:30, no host)**: LIMPAR BCD removeu 6 entradas
KitLugia acumuladas (`{a9b5aa79..}` → `{a9b5aa7e..}`), log
`CleanupOldWinpeEntries: 6 entradas removidas`. Reboot necessario para
o Boot Manager re-renderizar o menu.

Obs: `ScanBcdEntriesAsync` (WinbootPage BcdCleanerWindow) e os regex de
~3094 ja eram multi-idioma (`identifier|identificador`,
`description|descriç[ãa]o|descricao`) — sem mudanca.

### Sessao 02/08 (cont.) — FAST DISK API: IOCTL nativo (diskpart-free, estilo rpi-imager)

Pedido: "aplique e baixe tudo que envolver deixar mais rapido e monte um plano num .md".

Baixado (referencias): `MBW.Libraries.DeviceIOControlLib` (LordMike, confirmou structs
winioctl + IOCTLs 0x70050/0x700A0/0x7C0D0/0x7C100) e `rpi-imager` (diskpart_util.cpp —
`cleanDiskFast` prova o conceito + fallback de zerar MBR via WriteFile). Plano completo
em **`docs/FAT_DISK_API_PLAN.md`** (fases, quirks, riscos, proximos passos).

**`KitLugia.Core\NativeDiskIo.cs`** (novo, ~500 linhas, P/Invoke puro):
1. `OpenDisk(n)` CreateFile `\\.\PhysicalDriveN` (exige admin); `OpenVolume(c)` com
   `FILE_READ_ATTRIBUTES` (extents funcionam SEM admin — GENERIC_READ da erro 5).
2. `GetDeviceNumber` (0x2D1080), `GetDiskSize` (0x700A0), `GetStorageProperties`
   (0x2D1400: modelo/serial/bus), `GetDriveLayout`/`ParseDriveLayout` (0x70050:
   MBR/GPT direto, GPT name WCHAR[36], boot/ESP/MSR/WinRE flags), `DeleteDriveLayout`
   (0x7C100), `EnumerateVolumes` (GetLogicalDrives + GetVolumeInformation +
   GetDiskFreeSpaceEx + 0x560000 extents), `FindBootDiskNumber`.
3. **Quirks**: PhysicalDrive sem admin = erro 5 (fallback WMI ok); VOLUME_DISK_EXTENTS
   = count(4) + pad(4) + DISK_EXTENT[24] (extent em offset 8, stride 24 — retornado=32
   p/ 1 extent); union com string ByValTStr e INVÁLIDA no CLR (TypeLoadException) →
   parsing por ponteiro com offsets (PARTITION_INFORMATION_EX = 144B, union@32, numero@24; GPT name em 72). Cabecalho do layout NAO e fixo: GPT = 48B (kernel ntioapi adiciona StartingUsableOffset+UsableLength+MaxPartitionCount ao GUID de 16), MBR = 16B (Signature+CheckSum) - confirmado por hexdump real (returned=768 = 48 + 5*144). ReadAnsiString usava new byte[len] (zeros!) - corrigido com Marshal.Copy (bug real: modelo = espacos).
4. Testado ELEVADO (host): GetAllDisks nativo em 18-19 ms (2 discos, modelos corretos, GPT names, tamanhos, IsSystemDisk(1)=True); parsing sintetico MBR (header 16) e GPT (header 48) ok; EnumerateVolumes OK (C:
   disco1@788557824, E: disco0@1048576), boot disk = 1 (bate com Storage API).

**`KitLugia.Core\PartitionManager.cs`**:
5. `GetAllDisks()` → **nativo primeiro** → Storage API → legado (cadeia de fallback).
6. `IsSystemDisk()` → nativo (FindBootDiskNumber) → MSFT_Disk.IsSystem → legado.
7. `CleanDisk()` → fast path `IOCTL_DISK_DELETE_DRIVE_LAYOUT` (fullClean=false);
   "clean all" continua diskpart.

Build: 0 erros (solucao completa GUI+Core). **TESTADO ELEVADO (02/08)**: 18-19 ms, partições reais corretas (Recovery/EFI/MSR/Basic data, GPT names), modelos/seriais OK; MBR sintético (header 16) validado. Resta: CleanDisk via IOCTL em disco de teste (VM) e PartitionsPage rodando.

### Sessao 02/08 — PartitionsPage: migracao para Storage Management API (MSFT_*)

Pedido do usuario: "va na partition page e corrija quaisquer erros, la se usa
muito codigo legado, busque codigo mais recente e melhor na web".

Pesquisa web: a API moderna de particoes e a **Storage Management API**
(`ROOT\Microsoft\Windows\Storage`: MSFT_Disk/MSFT_Partition/MSFT_Volume,
Windows 8+) — PartitionStyle/IsSystem/IsBoot sao propriedades nativas,
nao heuristica de string; GetSupportedSize/Resize redimensionam sem
diskpart. Fontes: learn.microsoft.com MSFT_Disk/MSFT_Partition, SO
(escape de ObjectId em WQL).

**PartitionManager.cs (KitLugia.Core)**:
1. `GetAllDisks()` → `GetAllDisksStorageApi()` (MSFT_Disk + MSFT_Partition
   por DiskNumber + MSFT_Volume por DriveLetter; 3 queries no total em vez
   de N+1; BusTypeToString com NVMe/SATA/USB; PartitionStyleToString com
   MBR/GPT/RAW) com fallback `GetAllDisksLegacy()` (Win32_* original) se
   a Storage API falhar.
2. `DiskInfoEx` ganhou `IsSystemDisk`/`IsBootDisk`; `PartitionInfoEx` ganhou
   `IsSystemFlag`/`IsBootFlag` (nativos WMI) — `IsSystemPartition` agora usa
   os flags ANTES das heurísticas de label.
3. `IsSystemDisk(uint)` → query MSFT_Disk.IsSystem (fallback legado
   `IsSystemDiskLegacy` no catch).
4. `CheckFileSystem` → deteccao de erros agnostica de idioma
   (`\b(?:error|erro|fehler)\b` excluindo "no errors/nenhum erro/0 erros")
   + exige exitCode != 0 (chkdsk pt-BR nunca dizia "errors").
5. `GetMaxShrinkMb` → parse de numero com casas decimais (pt-BR usa vírgula)
   + delecao do temp script protegida (try/catch).
6. `ChangeDriveLetter(old, new, diskIndex?, partitionIndex?)` → suporta
   partição SEM letra via select disk+partition; valida newLetter vazia.
7. `RunProcessStreamed` → apos Kill por timeout, `WaitForExit(5000)` antes
   de ler ExitCode (evita InvalidOperationException).
8. Removidos: classe global `EncodingProvider` (colidia com
   System.Text.EncodingProvider, nunca referenciada), `DetectPartitionStyle`
   e `FetchPartitionsForDisk` (dead code).

**PartitionsPage.xaml.cs (KitLugia.GUI)**:
9. `BtnMove_Click` → usa `PartitionManager.MovePartition` (existente) em vez
   de reimplementar com BUG: reusava a letra ANTIGA após recriar a partição
   (novo volume pode ganhar outra letra; MovePartition detecta a nova).
10. `BtnAssignLetter_Click` → valida A-Z, bloqueia C: (protegida), passa
    diskIndex/partitionIndex para partições sem letra, mostra resultado.
11. `BtnExtend_Click` → corrigida condição confusa `!ChkMergeMode.IsChecked
    == true` → `ChkMergeMode.IsChecked != true` (precedência de operador).

Build: 0 erros.

**A TESTAR (host)**: abrir a PartitionsPage e conferir que a lista de
discos carrega (Storage API), TAMANHO/letras corretos, partição EFI
marcada como protegida (IsSystemFlag), mover partição sem perder a letra,
alterar letra em partição sem letra.

### Sessao 02/08 (cont.) — CAUSA RAIZ do falso negativo "Falhou mas funcionou": DISM Apply exit=123

Sintoma (VM, 14:20): Estender E: logava `[ATOMIC] 1.Capture OK / 2.Delete OK / 3.Create OK`,
particao recriada em 64 GB, mas `[DISM] Apply exit=123` → "Falha critica". A particao
fisica crescia mas os dados NUNCA eram restaurados (todo "Falhou" dos testes anteriores
era isso).

**Causa raiz**: bug de quoting no `ApplyVolumeImage`. O comando ia com
`/ApplyDir:"E:\"` (aspas + barra final). No parsing da linha de comando
(CommandLineToArgvW), `\"` vira aspa LITERAL → DISM recebe `ApplyDir=E:\" /NoRestart`
→ caminho invalido → **exit 123 = ERROR_INVALID_NAME (0x7B)**. O Capture nunca falhava
porque usa `/CaptureDir:E:\` SEM aspas (mesmo padrao que o Apply deveria usar).

**Correcoes (PartitionManager.cs + WinpeBuilder.cs)**:
1. `ApplyVolumeImage` → raiz de volume agora SEM aspas (`/ApplyDir:E:\`, espelha o
   CaptureDir); pasta real (merge) com aspas mas sem barra final (TrimEnd('\\')).
2. Fallback: se DISM falhar, tenta `wimlib-imagex apply` (`[WIMLIB] Apply exit=...`) —
   `FindBundledWimlib` virou `internal` para reuso.
3. Protecao anti-perda de dados: `SafeDeleteFile(tempWim)` so roda em SUCESSO nos 3
   fluxos (AtomicExtendDISM, MovePartition, merge); em falha o log avisa
   `Snapshot mantido em ...\extend_bypass_N.wim para recuperacao manual`.

**TESTADO (02/08 14:27-14:28, VMware)**: Reduce E: 64→32,1 GB OK (diskpart); Estender E:
2x seguidas → `[DISM] Apply exit=0` → `[ATOMIC] 4.Apply OK` → "Volume estendido com
Engine Atomica DISM!"; dados intactos; mais rapido que diskpart (enum nativa 6-19 ms).
Build: 0 erros / 122 warnings (nullable pre-existentes).

### Sessao 02/08 (cont.) — BUG: "Criando Particao" falhava 2x (DiskIndex=0 nas linhas "Nao Alocado")

Sintoma (VM 14:32): depois de Reduce C: (63→51,6 GB), "Criando Particao" falhava em ~4s
(2 tentativas, inclusive retry do historico) com "Nao foi possivel criar". O terminal
nao mostrava o motivo (output do diskpart so ia ao buffer interno). Extend C: via
Engine Atomica DISM funcionou (51,6→52,6 GB, Apply exit=0).

Investigacao:
1. Teste local em VHD descartavel (GPT, temp): `format quick fs=ntfs label="Novo Volume"`
   (label COM espaco) FUNCIONA no diskpart (exit 0) — hipotese do label descartada.
2. Causa raiz: `UpdateWithUnallocated()` criava as particoes sinteticas "Nao Alocado"
   SEM setar `DiskIndex` (default 0). Clicando no "Nao Alocado" do Disco 1, a UI passava
   DiskIndex=0 → `CreatePartition` mirava o Disco 0, que esta 100% ocupado (MSR + E: = 64GB)
   → diskpart "sem espaco" → falha imediata.

Correcoes (PartitionManager.cs):
1. `UpdateWithUnallocated(uint diskIndex)` — seta DiskIndex nas 2 entradas sinteticas
   (gap interno e gap final); 3 chamadores (nativo, Storage API, legado) passam
   `diskInfo.Index`.
2. `CreatePartition` — validacao defensiva antes do diskpart: disco alvo precisa de
   >= 10MB nao alocado; tamanho pedido nao pode exceder o livre. Aborta com log claro
   `[DISKPART] CreatePartition abortado: ...` (antes: erro generico do diskpart).
3. `RunDiskpartScript` — agora loga no terminal as linhas de ERRO do diskpart
   (`[DISKPART] ...`): erro/falhou/no space/insuficiente, antes invisiveis.

Build: 0 erros.

**TESTADO (02/08 14:44-14:47, VMware)**: tudo passou —
- Criar Particao no "Nao Alocado" do Disco 1: **SUCESSO** (G: 66 GB, depois H: 12,4 GB;
  antes falhava sempre com DiskIndex=0 mirando o Disco 0 cheio)
- Merge H: -> C: **2x SUCESSO**: Capture exit=0 → Delete OK → Extend C: OK (50,6→63 GB)
  → `[DISM] Apply exit=0` (C:\Arquivos_Mesclados) → "Mesclagem Atomica concluida com
  sucesso absoluto!" (era o primeiro merge completo da historia — o quoting bug
  tambem afetava esse fluxo)
- Extend/Reduce C: OK; nenhum arquivo perdido em nenhum teste.


### Proxima sessao
- [x] ~~Testar PartitionsPage elevado: enumeracao nativa (tempo)~~ - FEITO (02/08): 18-19 ms, particoes/nomes/modelos corretos (ver FAST DISK API). Resta CleanDisk via IOCTL em disco de teste (VM)
- [ ] Testar PartitionsPage com a nova Storage API (enumeracao + flags)
- [ ] (opcional) Fase 2 do FAT_DISK_API_PLAN: Extend/Shrink via IOCTL
      (IOCTL_DISK_GROW_PARTITION 0x7C0D0 + FSCTL_EXTEND_VOLUME 0x900118;
      FSCTL_QUERY_SHRINK_VOLUME 0x900114 + FSCTL_SHRINK_VOLUME 0x9001DC)
- [ ] Testar no app: toggle "Boost do App Ativo" + perfil personalizado
- [ ] Testar GameBarPresenceWriter / toggles da comunidade apos reboot
- [ ] Downgrade de build: testar ISO patcheada em VM + UI no WinbootPage

### Sessao 02/08 (cont.) — VALIDATION OS REMOVIDO da WinpeToolsPage

Pedido do usuario: "remova o validation OS de la o validation OS é horrivel e não funciona
somente o winpe padrão funciona corretamente".

Removido (KitLugia.GUI\Pages\WinpeToolsPage.xaml + .cs):
1. Card "1b. VALIDATION OS" inteiro (BtnPrepareValos/BtnBootValos/BtnRemoveValos + badge WPF).
2. RadioValos e radios do overlay de shrink substituidos por texto estatico "WinPE Padrao"
   (RadioWinpe/RadioShrinkOs_Checked/TxtOsStatus/UpdateOsStatusText removidos).
3. Regiao #region Validation OS inteira (~150 linhas): BtnPrepareValos_Click,
   BtnBootValos_Click (injecao WinXShell + bootsequence + shutdown), BtnRemoveValos_Click.
4. Campo _valosReady; bloco IsValidationOsReady no CheckWinpeStatusAsync.
5. 14 steps de progresso "Validation OS" do _progressSteps.
6. BtnConfirmShrinkWinpe_Click: sempre "winpe" (ScheduleWinpeShrink(drive, shrinkMb, "winpe"));
   BtnShrinkWinpe_Click: guard so _winpeReady; UpdateShrinkButton so _winpeReady.
7. ToolTip LIMPAR BCD e mensagens sem mencao a Validation OS.

Core INTOCADO (decisao de escopo): WinbootManager.PrepareValidationOs/IsValidationOsReady/
RemoveValidationOs/ValidationOsStartnetCmd e WinpeBuilder.ConfigureValosShellAsync continuam
existindo (sem UI). KitLugia.WinPE\ToolsPage (ferramenta DENTRO do WinPE) intacto.

Build: 0 erros. Fix extra: byte NUL corrompido em AGENTS.md linha 160 ("byte ?1" -> "byte 01").

### Proxima sessao
- [x] ~~Remover Validation OS da WinpePage~~ (WinpeToolsPage limpa; Core mantido)
- [ ] (se desejado) Remover tb do KitLugia.WinPE\ToolsPage e deletar codigo Core morto

### Sessao 02/08 (cont.) — Fresh Install: ScheduleReinstallPreserve + startnet.cmd reescritos

Pedido do usuario: a pagina Fresh Install (ReinstallPreservePage) estava legada (staging no host
C:, registry merge, 'mover dados') e ele quer que o WinPE aplique a imagem do Windows
DIRETAMENTE no disco alvo (pastas Windows/Program Files/Users ja prontas, sem perder dados).

1. **WinbootManager.cs — ScheduleReinstallPreserve reescrito**:
   - FindTargetPartition(driveLetter) (novo): resolve disco/particao/tamanho/livre/fs via
     PartitionManager.GetAllDisks (Storage API). FindEfiPartition() (novo): acha ESP por
     Type/Label (EFI/System) ou flags de boot MBR.
   - Staging agora vai para a particao ALVO (X:\KL_REINSTALL no WinPE, escrito pelo host no
     drive alvo): config INI + drivers exportados (ExportHostDrivers).
   - ISO extraida no host para X:\WindowsInstallation (pasta no drive alvo).
   - Marcador KL_REINSTALL_PRESERVE.dat gravado na raiz do alvo com DISK/PART/ESP/edition.
   - BCD: ixedGuid: ReinstallBcdGuid {4d3e5f7a-2b8c-4d9e-8f0a-1c2d3e4f5a6b} + /bootsequence (one-time,
     sem poluir o menu) — padrao do shrink. Log persistente KitLugia_FreshInstall_Log.txt no alvo.

2. **RamdiskReinstallPreserveStartnetCmd reescrito (assinatura nova)**:
   (PreservationOptions options, string configDir, string targetDrive, int tDisk, int tPart,
   int eDisk, int ePart). Mudancas:
   - Deteccao: DISK/PART embutidos primeiro (assign Z + confirmacao por marcador) -> fallback
     scan por marcador (metodo que sempre funciona). CORRIGIDO bug legado select disk K (K era
     letra de drive, nao numero) que fazia o script sempre falhar no diskpart.
   - Work drive: Z: (antes C:). ESP embutida (DISK/PART) primeiro com confirmacao S:\EFI,
     fallback brute-force scan (era so brute force).
   - Log persistente Z:\KitLugia_FreshInstall_Log.txt: inicio, Status: OK no sucesso, Status: FAIL
     nos 3 exits criticos (particao nao encontrada, SAFE ja existe, aplicacao da imagem falhou).
   - Cleanup no final: remove marcador + Z:\KL_REINSTALL + letra Z.

Build: 0 erros / 120 warnings (nullable pre-existentes).

**A TESTAR (VMware)**: SCHEDULE fresh install -> reboot -> log escolhendo 'Alvo embutido confirmado',
Apply OK, bootloader OK, dados preservados. Pendente: GUI ReinstallPreservePage (usar
GetAllDisks no lugar de DriveInfo.GetDrives, validar espaco livre, edicao numerica, leitor de log)
+ incluir ReinstallLogFile no ReadAllWinpeLogs.

3. **ReadAllWinpeLogs/ClearWinpeLogs** (WinbootManager.cs): incluem agora o log persistente do fresh install
   (`KitLugia_FreshInstall_Log.txt`) — scan por TODOS os volumes fixos/removiveis (letra do alvo e variavel).

4. **GUI ReinstallPreservePage modernizada** (XAML + .cs):
   - DriveInfo.GetDrives -> PartitionManager.GetAllDisks (Storage API): lista volumes com letra,
     livre/total em GB e [Disco N Part N]; mostra espaco livre no resumo de confirmacao.
   - Validacao de espaco livre: BtnStart desabilitado se < 25 GB (staging do ISO + backup) com mensagem clara.
   - `GetSelectedEditionIndex()`: extrai o NUMERO da edicao selecionada ("2 - Windows Pro" -> "2").
   - Textos novos: overlay de execucao com os 4 passos reais (Storage API, staging no alvo, ISO no alvo,
     bootsequence), sidebar "Como funciona" com os 6 passos novos, aviso "aplicar imagem DIRETO no disco".
   - Leitor de log: TxtOperationLog (antes nunca preenchido) agora carrega ReadAllWinpeLogs no Loaded
     e apos agendar + botao ATUALIZAR LOG.

5. **Fix de seguranca no startnet.cmd**: se o alvo embutido falhar o check do marcador, remove a
   letra Z antes do scan (evita backup na particao ERRADA caso o disco WMI != disco diskpart).

Build: 0 erros / 122 warnings (nullable pre-existentes).

### Proxima sessao
- [ ] Testar em VM o Fresh Install completo (schedule + reboot + apply + reboot)
- [ ] (se desejado) Remover tb do KitLugia.WinPE\ToolsPage e deletar codigo Core morto

### Sessao 02/08 (cont.) — Shrink: botao cinza + auto-prepare do WinPE

Sintoma (VM): o botao INICIAR SHRINK ficava cinza — `UpdateShrinkButton` exigia `_winpeReady`
(IsWinpeReady = existe C:\KL_WINPE\boot.wim). Na VM o WinPE nao estava preparado, entao
nada podia ser agendado.

Correcao (fluxo libera espaco via WinPE automaticamente):
1. `UpdateShrinkButton` (WinpeToolsPage): `IsEnabled = hasPartition` (remove dependencia de _winpeReady).
2. Guards `if (!_winpeReady)` removidos de BtnShrinkWinpe_Click / BtnConfirmShrinkWinpe_Click.
3. `ScheduleWinpeShrink` (WinbootManager): se boot.wim nao existir (e fallback recursivo falhar),
   chama `PrepareWinpeBoot()` automaticamente antes de continuar (baixa/cria o WIM).
4. Overlay de confirmacao avisa: "Se o WinPE nao estiver preparado, sera preparado automaticamente agora."
5. Campo morto `_winpeReady` removido.

Build: 0 erros / 104 warnings (baseline).

**A TESTAR (VM)**: sem WinPE preparado → selecionar particao → INICIAR SHRINK → app prepara WinPE
(baixa/cria WIM) → escreve config+marcador → reboot → shrink roda no WinPE → Status: OK no log.


### Sessao 02/08 (cont.) — Fresh Install: "adquirir espaco" excluindo o Windows antigo

Pedido do usuario: a validacao de espaco bloqueava com "nao tem espaco o suficiente",
mas o Windows antigo sera SUBSTITUIDO — o app deve adquirir espaco sozinho.

1. **Minimo reduzido 25 GB -> 10 GB** (WinbootManager.ScheduleReinstallPreserve ~L6608 +
   ReinstallPreservePage.xaml.cs ~L255/263): o unico espaco REALMENTE necessario no host
   e o install.wim extraido. O espaco do Windows antigo e liberado pelo WinPE.

2. **Extracao seletiva do ISO** (ScheduleReinstallPreserve ~L6649): `7z x` agora extrai
   SOMENTE `sources\install.wim` + `sources\install.esd` (antes o ISO inteiro ~10 GB).
   Se a extracao seletiva ficar vazia, fallback para extracao completa. O startnet.cmd
   so precisa do install.wim/esd em WindowsInstallation\sources\.

3. **startnet.cmd: bloco "LIBERAR ESPACO - REMOVER WINDOWS ANTIGO"** (apos backup FASE 1,
   antes do apply FASE 2, RamdiskReinstallPreserveStartnetCmd):
   - `rd /s /q !WIN!\Windows` (sempre — sera substituido pelo apply)
   - `Users` so se CFG_PRESERVE_USERS!=1; `Program Files`/`(x86)` so se
     CFG_PRESERVE_PROGRAM_FILES!=1 (nao foram movidos -> pode deletar)
   - `ProgramData` (ja movido incondicionalmente em 1.2 — rd e no-op de seguranca),
     `Recovery`, `ESD` (na skip list do _root -> nunca movidos)
   - Seguranca verificada: tudo que e preservado ja esta em !SAFE! antes da delecao.

Build: 0 erros.

**A TESTAR (VM)**: particao com Windows antigo e < 25 GB livres -> carregar ISO ->
INICIAR -> host extrai so o install.wim -> reboot -> WinPE move dados para Z:\! ->
remove Windows antigo -> apply -> bootloader -> dados preservados.

### Sessao 02/08 (cont.) — Fresh Install: botao nunca cinza + WinPE resolve espaco

Pedido do usuario: o botao INICIAR ficava sempre cinza (exigia `_winpeReady` + 10 GB livres).
"O WinPE vai iniciar de qualquer jeito: se nao conseguir espaco, ele simplesmente deleta
o Windows antigo." Implementado:

1. **GUI** (ReinstallPreservePage.UpdateReadyStatus): `BtnStart.IsEnabled = hasIso && selIdx>=0`
   — sem `_winpeReady`, sem `hasSpace`. Textos de status explicam cada condicao (WinPE sera
   preparado automaticamente; espaco baixo -> WinPE deleta o Windows antigo e extrai o ISO).

2. **Core** (ScheduleReinstallPreserve): hard-block de espaço REMOVIDO (antes `return (false,...)`
   se tFree<10GB). Agora `canExtractHost = tFree==0 || tFree>=8GB`: se nao couber, loga aviso
   e PULA a extracao no host (o WinPE extrai depois de deletar o Windows antigo).

3. **Auto-prepare WinPE** (ScheduleReinstallPreserve): se `C:\KL_WINPE\boot.wim` nao existir,
   chama `PrepareWinpeBoot()` automaticamente (mesmo padrao do ScheduleWinpeShrink). Antes
   retornava erro "Prepare o WinPE primeiro".

4. **7z injetado no WinPE** (WinpeBuilder.Inject7zIntoWimAsync + FindSevenZipExe): injeta
   7z.exe + 7z.dll (bundled em Resources\App\7Zip ou Program Files) em /Windows/System32/
   via wimlib, no custom reinstall_boot.wim — para o script extrair o ISO DENTRO do WinPE.

5. **startnet.cmd: extracao do ISO no WinPE** (apos LIBERAR ESPACO): se WIM_FILE vazio, o
   script procura o ISO pelo nome (`CFG_ISO_FILE`) com `dir /b /s` em todas as letras C..N
   (pega ate o ISO que foi movido de Users para Z:! durante o backup) e extrai via 7z em
   `Z:\WindowsInstallation` (sources\install.wim + install.esd). Depois re-testa WIM_FILE
   antes de cair no loop de montagem manual.

Build: 0 erros / 122 warnings (baseline).

**A TESTAR (VM)**: particao alvo numa boa-> carregar ISO -> INICIAR (botao acende) ->
reboot -> WinPE: backup Z:\! -> deleta Windows antigo -> extrai ISO do drive original ->
apply -> bootloader. Variante: partição cheia (sem extracao no host) -> WinPE extrai.


### Sessao 03/08 - CAUSA RAIZ do "was unexpected at this time" no Fresh Install

Sintoma (VM): o startnet.cmd do fresh install crashava com
... foi inesperado neste momento. logo apos "Alvo embutido confirmado: DISK=1 PART=3",
e o banner aparecia como "KitLugia ? Fresh Install" (encoding). O reboot de 10s
tambem nunca disparava.

**Reproduzido localmente por bisseccao do script gerado** (dumpnet via reflection,
`%TEMP%\opencode\fresh_startnet.cmd`, 431 linhas): o crash estava no bloco
if "!PART_OK!"=="0" ( ... scan por marcador ... ) - L48-L69. Regra do cmd.exe:
**qualquer `(/`) dentro de um echo DENTRO de um bloco if/or fecha o bloco
prematuramente no parse, mesmo balanceado** (teste minimo paren_test1/2.cmd
confirmou: echo teste (todos os discos)... dentro de bloco = CRASH; o bloco e
so parseado quando a condicao e TRUE, por isso o "OK" anterior enganava).

**Correcoes (WinbootManager.cs)**:
1. Parenteses removidos de TODOS os echo dentro de blocos do
   RamdiskStartCmd: L~6849 (todos os discos), L~6871
   (marcador ...), L~7241 (NewSft), L~7248 (OldSft) - trocados por -.
2. Em-dash U+2014 -> - em toda a extensao do metodo (banner + FASE 1-5);
   o script e salvo como ASCII (Encoding.ASCII em CustomizeWinpeWimFlatAsync/
   UpdateWimWithScriptAsync) e - virava ? no banner.
3. BONUS (mesmo bug latente em outro gerador): L~2146 (pode levar alguns
   minutos) dentro de if exist ... ( no script de instalacao do KitLugia
   - trocado por ", isso pode levar alguns minutos".
4. **Reboot de 10s ADICIONADO ao ScheduleReinstallPreserve** (o shrink ja tinha;
   fresh install retornava sem reiniciar): mesmo padrao shutdown /r /t 10
   via Task.Run apos bootsequence (bloco "6. Agenda reboot").

Verificacao: build 0 erros; script regenerado roda de ponta a ponta sem
"inesperado" (chega ao branch de erro do marcador ausente); subrotina
merge_registry forca-parseada com 
eg load falho (errorlevel 1) = OK.
Os 4 echo com parens restantes no script (L32/L81/L237/L412) sao TOP-LEVEL
(fora de bloco) - seguros.

**A TESTAR (VM)**: SCHEDULE fresh install -> app anuncia reboot 10s -> WinPE
autorizado com banner "KitLugia - Fresh Install + Preservacao" -> alvo embutido
confirmado -> FASE 1/5 backup -> apply -> merge -> bootloader -> reboot.
### Sessao 03/08 (cont.) - Fresh Install: letra de drive dinamica (Z: fixo -> primeira livre)

Sintoma (VM): apos o fix do parse, o script chegava em FASE 1/5 BACKUP mas abortava com
"ERRO: Z: ja existe. Remova ou renomeie e tente novamente." - o backup Z:\! de uma
execucao anterior (ou a letra Z ocupada por outro volume) travava o fluxo.

Pedido do usuario: "deixe ele mais robusto para criar outra letra".

Correcoes (WinbootManager.cs, RamdiskReinstallPreserveStartnetCmd):
1. **Letra WIN dinamica** no bloco embedded: loop `for %%L in (Z Y W V U T R Q P O N M L K J I H G F E D C)` - se `if not exist %%L:\` (letra livre), escreve p.txt limpo por tentativa (select disk/partition + assign letter=%%L), confere `%%L:\KL_REINSTALL_PRESERVE.dat`; se nao achar, remove a letra e tenta a proxima. `if not defined WIN` gateia o loop.
2. **Scan fallback**: mesma lista de letras para escolher SCNL (primeira livre) em vez de K fixo; marker procurado em `!SCNL!:\KL_REINSTALL_PRESERVE.dat`.
3. **Backup antigo renomeado em vez de abortar**: `if exist !SAFE!` -> `set BKOLD=_old_!RANDOM!` + `ren "!SAFE!" "!BKOLD!"` (mantido para recuperacao manual); aborta so se o ren falhar. Cuidado: `!_old_!RANDOM!` com delayed expansion expandiria `_old_` como var (vazia) - por isso BKOLD sem `!` inicial.
4. **SAFE/PLOG/CFG_CONFIG_DIR derivados da letra escolhida**: `SAFE=!WIN!:\!`, `PLOG=!WIN!:\KitLugia_FreshInstall_Log.txt`, `CFG_CONFIG_DIR=!WIN!:\KL_REINSTALL` (set apos deteccao; a linha antiga `set CFG_CONFIG_DIR=Z:\KL_REINSTALL` virou rem).
5. **ESP dinamica**: loop `for %%L in (S T R Q P O N M L K J I H G F E D C)` escolhe ESPL (primeira livre); embedded e scan usam `!ESPL!:` no lugar de S: fixo.
6. Cleanup final: `remove letter=!WIN!` (antes Z).

Verificacao:
- Build: 0 erros / 122 warnings (baseline).
- Script regenerado via dumpnet (462 linhas): roda de ponta a ponta sem "inesperado";
  fluxo correto (embedded -> scan -> error branch quando nao ha marcador).
- Teste de letra ocupada (subst Z: e Y:): loop escolheu W como primeira livre.
- Grep no script gerado: so resta o fallback defensivo `if not defined WIN set WIN=Z`.

**A TESTAR (VMware)**: SCHEDULE fresh install 2x seguidas (2o run com Z:\! leftover) ->
WinPE deve escolher letra livre, renomear backup antigo, aplicar, bootloader OK.
### Sessao 05/08 - PC Manager Deep Uninstall: analise binaria COMPLETA (app nao estava instalado!)

Pedido do usuario: descobrir como funciona o "Deep Uninstall" do Microsoft PC Manager
(Store) inspecionando os binarios com o IDA Pro.

1. **PC Manager NAO estava instalado** (o usuario achava que tinha instalado): zero
   vestigios - `Get-AppxPackage -AllUsers`, varredura em WindowsApps, LocalAppData\Packages,
   AppRepository, menu Iniciar, winget list. Instalado via winget msstore:
   `winget install --id 9PM860492SZD -e --source msstore --silent` (v3.22.3.0).
   Binarios copiados para `%TEMP%\opencode\pcmp\`. PARA TESTAR/WEB: PC Manager esta no
   host (installado para o usuario lugia).

2. **IDA Pro NAO serve para este caso**: o plugin de uninstall e assembly .NET
   (`MSPCManager.dll` usa PresentationFramework; `Microsoft.WIC.PCManager.Plugin.Uninstall.dll`
   238 KB e o core). Ferramenta certa: **ilspycmd** (instalado global:
   `dotnet tool install --global ilspycmd`; binario em `%USERPROFILE%\.dotnet\tools\ilspycmd.exe`).
   Decompilado: `%TEMP%\opencode\pcmp\src\uninstall\...decompiled.cs` (8578 linhas).

3. **COMO FUNCIONA o Deep Uninstall (achados)**:
   - **NAO usa snapshot pre/pos global** (diferente do Revo). Confirma a reforma do
     KitLugia (captureBaseline:false).
   - Lista de apps: SO 4 chaves Uninstall (HKLM + Wow6432Node + HKCU); exige DisplayName,
     UninstallString, !IsSystemComponent, install dir com .exe.
   - FileScanner: BFS na `InstalledPath` (InstallLocation reg ou dir do uninstaller),
     profundidade 9, pula symlinks, filtro de extensao opcional. **NUNCA varre AppData/
     Roaming/ProgramData globalmente** - pastas de dados sao cobertas por RULES por app.
   - RegistryScanner: deleta SO a propria chave Uninstall (BaseKeyType\MiddleKey\EndingKey).
   - **Allowlist**: so apps cujo EndingKey (sufixo da chave) casa `ResidualRule.AppUninstallKey`
     da config (`UninstallOptions.json` / cloud, reload dinamico) viram `IsResidualApp`
     e ganham o scan de residuos. `ResidualRule`: AppUninstallKey[], InstallPathFolder[],
     Depth, FileExtension[], FileCount, EmptyFolder. Guardas: FileCount limit + folder match.
   - Delecao por item com fail types (IsOccupied/NoPermission/PathTooLong/etc),
     progresso 0->95->100 via observer; telemtria WM_Uninstaller_Residual_*.
   - UninstallRegistryMonitor: RegNotifyChangeKeyValue nas chaves Uninstall (detecta
     remocao/confirma desinstalacao; dispara popup "residuos encontrados").
   - Extras hardcoded: CleanupApps (QQ游戏, 7-Zip), CmdApplication (MsiExec/Sunlogin/ActiveX).
   - Documentacao completa: **`docs/PC_MANAGER_DEEP_UNINSTALL_ANALYSIS.md`**.

4. **Proximos passos (opcional)**: adotar allowlist de regras por app no KitLugia
   (JSON com AppUninstallKey/InstallPathFolder/Depth/FileExtension/FileCount) para refinar
   o ScanUwpLeftovers/LeftoverJunkManager sem varrer AppData globalmente.

### Sessao 05/08 (cont.) - Revo Uninstaller: analise binaria (IDA Pro, x64 nativo MFC/SQLite)

Pedido do usuario: mesmo tratamento do PC Manager para o Revo real
(`C:\Program Files\VS Revo Group\Revo Uninstaller`). Documentacao completa:
**`docs/REVO_UNINSTALLER_ANALYSIS.md`**.

**Revo e NATIVO x64 (MFC 7-14 + Prof-UIS + SQLite embutido), NAO .NET** - ao contrario
do PC Manager, IDA Pro e a ferramenta certa (nao ilspycmd). `idat.exe -B` em
`RevoUnin.exe` gerou .asm (110 MB) + .i64 (187 MB). A analise in-DB ficou lenta
(load do .i64 > 5 min; IDA 9 sem `ida_bytes.find_binary`/`idc.find_str`; strings
Delphi sao UTF-16 com prefixo de tamanho, fora do Strings window) -> **pivot para
parsing off-line em Python 3.11** (`%TEMP%\opencode\revo\pe_scan.py` acha xrefs no
.text por LEA RIP-relativo; `extract_funcs.py`/`func_strings.py`/`scan_density.py`
extraem funcoes do .asm) - caminho que funciona e documentado em docs/REVO...md.

**ACHADOS**:
1. **Dispatcher CLI** (sub_140178EB0): /leftovers /continue /chactivation /hunter
   /forcedfolder /update /implog /settings /updatesubscription SC + arg KeepFiles
   (10 chars, preserva arquivos). /leftovers exige pNum>4 (exe, flt, + 3 alvos).
2. **SEM snapshot global** (igual PC Manager). Scan pos-uninstall com 3 fontes de alvo:
   (a) marcadores **ADCU/ADAU** = `\VS Revo Group\Revo Uninstaller\ADCU`/`ADAU`
   (AppData Current User / All Users) gravados por app - 16+ call sites;
   (b) caminho de instalacao; (c) **Registry Classes scan completo**.
3. **Scanner de Registry Classes** (sub_14018D6C0): CLSID/Interface/Applications/
   TypeLib/AppID/Mime/SystemFileAssociations/Record/Media Type/Local Settings/
   ActivatableClasses + WOW6432Node, checando InprocServer32/LocalServer32/DefaultIcon/
   OpenWithProgIds - acha referencias ao dir do app desinstalado (scan profundo).
4. **SQLite embutido**: resultados do scan persistidos em banco; `SCAN %S`/
   `SCAN %d CONSTANT ROW%s` = sqlite3_trace_v2. Leitura/progresso incremental.
5. **Config** (chave `Uninstaller\`, sub_14017F960/7FB20): Create System Restore
   Pont, FastLoadMode, StopRunExe (mata processos), DelToBin (deleta p/ Recycle Bin,
   `%s:\$Recycle.Bin\%s`), Select leftovers by default, Use Reg Install Date,
   Show System Components, Disable scan after uninstall, Maximize uninstall wizard.
6. **Exclusoes**: `Uninstaller\RegExclude`, `Junk Files\Exclude\`, `Junk Files\Include\`
   (CDlgAddTracedRegExclude). Cleaner de browsers usa chaves `Junk Files\General\*`.
7. **Arquivador de autoruns**: strings `Registry: HKLM/HKCU Run/RunOnce/RunServices/
   RunOnceEx/32bit`, espelhados em `SOFTWARE\VS Revo Group\Revo Uninstaller\...`.
8. `AppData Invalid.` = MessageBoxW + ExitProcess (erro fatal quando AppData invalido).

**COMPARATIVO Revo x PC Manager x KitLugia** em docs/REVO...md. Liesoes p/ KitLugia:
1. **Marcadores ADCU/ADAU = mecanismo mais barato e preciso** para mirar AppData por
   app no pos-scan (gravar paths na listagem/instalacao, usar no removal) - sem varrer
   todo o AppData.
2. Config em chave propria com toggles (DelToBin, StopRunExe, Select leftovers,
   Disable scan) espelha o padrao de toggles que o Kit ja usa p/ GameBoost.
3. SQLite como buffer de resultados do scan = progresso incremental, sem re-scan total.
4. Registry Classes scan (CLSID/TypeLib/Interface) so como "scan profundo" opcional.
5. Exclusoes (RegExclude) + filtro "ignore < 24h acesso" sao refinamentos baratos.

Artefatos: `%TEMP%\opencode\revo\` (RevoUnin.exe.asm/.i64, pe_scan.py, scan_density.py,
func_strings.py, funcs_out.txt, dump_*.py). Binarios originais intocados (somente leitura).

### Sessao 05/08 (cont.) - AppsPage: ReviewPanel estilo Revo "conectado" ao uninstall

Pedido do usuario: "quando voce clica para limpar o app ele mostra na tela o caminho
direto dos itens que vai remover, igual ao Revo Uninstaller; cruzando Revo + PC Manager
p/ um desinstalador mais solido; refazer o AppsPage para funcionar melhor (visual pode
manter)".

**Investigacao**: o Kit JA tinha o motor Revo-style completo mas ORFAO:
- `DeepUninstaller.DeepUninstallProgram(...)` roda uninstall + p-scan (arquivos via
  ScanLeftoverFiles, registro via ScanLeftoverRegistry, scheduled tasks, env vars) e
  retorna `UninstallResult` com LeftoverFiles/LeftoverRegistry (+Heuristic* quando
  captureBaseline=true). `ClassifyFileSafety`/`ClassifyRegistrySafety` (publicas)
  classificam cada item em CleanupSafety (Safe/Moderate/Uncertain).
- `AppsPage.xaml` tem o grid `ReviewPanel` (linha 916) com 2 tabs (Arquivos/Registro),
  cada item mostrando `DisplayPath` (nome) + `FullPath` (caminho COMPLETO, linha 1054),
  icone de pasta/arquivo, indicador de seguranca, `IsKept`, checboxes.
- `AppsPage.xaml.cs` tem `ShowReviewPanel(...)` (linha 1571), `BuildFileItems`/
  `BuildRegistryItems` (tree condensada com items navegacionais), `UpdateReviewCounts`,
  `BtnReviewDeleteFiles/Reg` (deleta SO o marcado, ignora informativos), `BtnReviewRestore`
  (restaura backup), `BtnReviewCopy` (copia caminhos), `BtnReviewBack` (volta salvando
  o restante na aba Residuo + remove da lista se tudo deletado).
- **O REVIEW NUNCA ERA CHAMADO**: `ShowReviewPanel` so tinha a definicao (rg == 1 match).
  O fluxo individual (`BtnProgramRemove_Click`) desinstalava, guardava leftovers numa
  entrada da aba Residuos silenciosamente e mostrava MessageBox — sem tela de caminhos.

**CORRECAO (`BtnProgramRemove_Click`, AppsPage.xaml.cs ~linha 837)**:
1. Se houver leftovers (arquivo+registro > 0): classifica cada item com
   `ClassifyFileSafety`/`ClassifyRegistrySafety` (passa installLocation para o reviewer
   marcar Safe os que estao dentro do instalador), captura `_reviewProgramContext`,
   e chama `ShowReviewPanel(...)` — usuario ve os caminhos diretos, marca o que
   deletar, e so o que estiver selecionado (e CanDelete) e removido.
2. Sem leftovers: MessageBox simples de sucesso + remove da lista (fluxo original).

O ciclo de seguranca ja existia: `BtnReviewBack` salva o restante na aba
Residuo e, quando tudo for deletado, remove o programa da lista (`_reviewProgramContext`).

Ex referencias para continuar: `ClassifyFileSafety` (DeepUninstaller.cs:482),
`ClassifyRegistrySafety` (530), `ShowReviewPanel` (AppsPage.xaml.cs:1571),
`BtnReviewBack_Click` (1848), XAML `ReviewPanel` (AppsPage.xaml:916).

Build: 0 erros / 122 warnings (baseline nullable pre-existentes).

**A TESTAR (VM)**: abrir AppsPage -> REMOVER um app com lixo -> deve abrir o grid
com os caminhos completos, marcar/desmarcar, deletar so o marcado, Remover registro,
botao Restaurar (backup), e confirmar que ao voltar so um app sai da lista.

### Sessao 05/08 (cont.) - Performance: leitura duplicada no ScanFolderConfidence

Sintoma: o usuario pediu de novo o fluxo e se o desinstalador esta MAIS RAPIDO.

Fluxo verificado (AppsPage.xaml.cs): BtnProgramRemove_Click -> DeepUninstallProgram
com captureBaseline:false (SEM snapshot pre/post global - padrao Revo/PCM, ja era o
maior economia) -> leftover != 0 -> classifica e abre ShowReviewPanel (estilo Revo).
Build de ontem: 0 erros / 122 warnings.

Gargalo restante encontrado (DeepUninstaller.cs): `ScanFolderConfidence` fazia 2 passadas
de leitura de binarios por pasta:
- `VerifyFolderByContent` (linha ~1182): FileVersionInfo + Authenticode de cada .exe/.dll.
- `HasUnrelatedExecutables` (linha ~1206): relia o MESMO FileVersionInfo de todos
  os executaveis para a penalty ExecutablesArePresent.

**CORRECAO**: unificadas em UMA `ProbeFolderBinaries` (retorna (VerifiedMatch,
HasUnrelated)):
1. Uma unica passada de FileVersionInfo por arquivo (corta ~50% das leituras).
2. Authenticode (`X509Certificate.CreateFromSignedFile`, caro) SO roda quando o
   FileVersionInfo ainda nao confirmou o match (antes era por arquivo em cada passada).
3. Semantica preservada: HasUnrelated = hasExecutables && !anyFviMatch (igual), e o
   gated `match && !contentMatch && probe.HasUnrelated` mantem a penalty BCU.
4. Chamado em ScanFolderConfidence (linha ~1182) com `probe.HasUnrelated` (linha ~1206).

Build: 0 erros / 0 avisos (Core e GUI completo - app fechado p/ desbloqueio MSB3021).

**A TESTAR (VM)**: desinstalar um app com muitas subpastas em AppData/ProgramFiles
-> garantir que o conjunto de leftovers nao muda (falsos positivos/negativos
inalterados) e o scan termine mais rapido (menos leituras de metadados).

### Sessao 06/08 - Implementacao dos achados Revo no DeepUninstaller (comparacao Revo x Kit)

Pedido do usuario: "implemente tudo que descobriu" para comparar DIRETO no host
(desinstalar o mesmo app no Revo e no Kit e comparar os achados nas listas).

1. **ADCU/ADAU markers (Revo)**: `CaptureDataFoldersForApp(displayName, publisher)`
   (DeepUninstaller.cs) varre APENAS a raiz de Roaming/LocalAppData/ProgramData
   e captura pastas cujo nome casa com displayName/publisher (exato/StartsWith/
   contem - `TokensMatch`). CHAMADO em `DeepUninstallProgram` e `ForceDeleteProgram`
   apos o scan: se a pasta ainda existe apos a desinstalacao -> adicionada ao
   `LeftoverFiles` e registrada em `result.DataFoldersCaptured`. Preciso e barato
   (sem deep-walk global), espelha o "marcador ADCU/ADAU" do Revo sem necessidade
   de rastrear instalacao.

2. **RegExclude (Revo) persistente**: lista de exclusoes do usuario em registro
   `HKCU\Software\KitLugia\DeepUninstall\Exclude` (MultiString). O burdenoff:
   `GetUserExclusions`/`AddUserExclusion`/`RemoveUserExclusion`/`ClearUserExclusions`
   (publicos). Aplicada em `ScanLeftoverFiles` E `ScanLeftoverRegistry` (filtro
   final que remove resultados que casam com exclusao). `IsExcludedPath` suporta
   prefixo exato, subpasta e wildcard basico '*' / '?'.

3. **UI "Ignorar Sempre"** no ReviewPanel (AppsPage): botao novo em cada tab
   (arquivos e registro) -> chama `AddUserExclusion` para os itens selecionados
   e os remove da lista atual. Espelha o RegExclude do Revo.

4. **DataFoldersCaptured visivel**: `ShowReviewPanel` mostra quantas pastas de
   dados foram capturadas e o primeiro exemplo no ReviewInfoText.

Build: 0 erros / ==... 122 warnings (baseline). Proximo: testar no VM
comparando Revo x Kit no mesmo app.

### Sessao 06/08 (cont.) - BUG: remocao UWP "falhava feio" silenciosamente

Sintoma: abrir AppsPage > bloatware (UWP) > REMOVER um app. O `DeepRemoveBloatwareAppAsync`
(SystemTweaks.cs) engolia TODOS os erros do `RemovePackageAsync` (catch vazio) e SEMPRE
retornava (true, "...sucesso") mesmo quando nada era removido - era impossivel saber o motivo.
Apps provisionados / que exigem -AllUsers falhavam mudos.

**CORRECAO (SystemTweaks.DeepRemoveBloatwareAppAsync)**:
1. `RemovePackageAsync` agora AWAITA o `DeploymentResult` e le `dr.ErrorText`; falha ->
   registrada em `errors`. Antes: catch vazio engolia tudo.
2. Fallback: `Remove-AppxPackage -AllUsers -Package '<fullname>'` (robusto p/ apps
   provisionados) com guarda de ExitCode + stderr.
3. VERIFICACAO REAL no fim: `FindPackages(packageNameBase)` (casa por PACKAGE NAME, nao
   FullName!) -> se ainda instalado e sem erro conhecido, marca erro. Retorna (false, msg).
4. UI (AppsPage.BtnBloatwareAction_Click): MessageBox de falha agora mostra `result.Message`.
5. `FindPackages(packageFullName)` corrigido -> `FindPackages(packageNameBase)` (FullName
   nunca casa na API PackageManager).

Build: 0 erros / 104 warnings (baseline). Steps anteriores (DISM provisioned, limpeza
LocalAppData\Packages e WindowsApps) mantidos.

### Sessao 06/08 (cont.) - FLOOD de "Exception suppressed" durante scan de residuos (VS Code/zcode)

Sintoma: ao desinstalar o zcode (VS Code), durante "Escaneando resíduos..." o log explodia
com centenas de `[AVISO] (Unknown): Exception suppressed` (~1/s por ~10s). Cada arquivo ou
pasta inacessivel (acesso negado em subpastas, metadados corrompidos em binarios) disparava
um LogWarning individual nos catchs do scan - sem nenhuma informacao util.

**CORRECOES**:
1. **Rate limiter no Logger.cs**: `LogWarning` com mensagem contendo "Exception suppressed"
   passa por `ShouldSuppressRepeated` (janela de 60s): loga a 1a ocorrencia, depois so
   um resumo a cada 100 repeticoes da MESMA mensagem/contexto. Flood de 200+ vira ~2 linhas.
2. **ProbeFolderBinaries (DeepUninstaller.cs)**: catchs do FileVersionInfo/Authenticode
   agora silenciosos (sem LogWarning por arquivo) + contador `fviFailures`; no fim loga UMA
   linha com o total e o motivo real ("Acesso negado/metadados corrompidos em N binarios de X").
3. **ScanFolderConfidence**: `Directory.GetDirectories` movido para try proprio com log do
   `ex.Message` real (rate-limited) e `return` - antes o catch generico engolia e continuava.

Build: 0 erros / 122 warnings (baseline). Proximo: reproduzir o scan do zcode e conferir
que o log fica limpo (1-2 avisos no maximo) e que o motivo real aparece quando houver falha.

### Sessao 06/08 (cont.) - Usabilidade do Review: ShortPath + Copiar com marcacao

Feedback do usuario apos testar o review do zcode (VS Code):
1. **Caminhos longos**: `AppCleanupItem.ShortPath` (AppsPage.xaml.cs) colapsa o caminho
   para `C:\Users\Lugia\...\Local\@zcodedesktop-updater\pending` (>52 chars: primeiro +
   ultimos 2 segmentos com "..."), ToolTip mantem o FullPath. Aplicado nas 2 tabs
   (Arquivos/Registro) via `Text="{Binding ShortPath}"`.
2. **Copiar anotado**: `BtnReviewCopy_Click` agora anexa " MARCADO PARA DELETAR" a cada
   item selecionado (mesmo filtro do delete: IsSelected && CanDelete && !IsNavigational);
   itens nao marcados saem sem sufixo. Status mostra "(N marcados para deletar)".
3. **Velocidade do scan** (DeepUninstaller.cs):
   - `ProbeFolderBinaries`: cap de 12 executaveis por pasta (MaxProbeFiles); HasUnrelated
     so e afirmado quando a pasta foi 100% sondada (evita falso negativo em pasta gigante).
   - `ScanFolderConfidence`: gate `nameMatch || HasNameRelation(displayName, dirName)`
     ANTES do probe caro — pastas sem relacao nenhuma de nome nao leem FileVersionInfo.
     `HasNameRelation` = token do app (>=4 chars, fora de ForbiddenScanFolderNames)
     contido no nome da pasta (pega "@zcodedesktop-updater" -> zcode).
4. **BUG de exclusoes nao carregadas**: `ScanLeftoverFiles`/`ScanLeftoverRegistry`
   usavam `_userExclusions.Count > 0` sem `LoadExclusionsIfNeeded()` — na 1a execucao
   da sessao a lista em memoria estava vazia e as exclusoes salvas eram IGNORADAS.
   Corrigido: ambos chamam `GetUserExclusions()` (que carrega do registro) antes do gate.
5. **SEGURANCA (review Revo/PCM)**: `CaptureDataFoldersForApp` usava publisher como
   token de nome — publisher generico ("Microsoft") casaria `%AppData%\Microsoft`
   (compartilhada por dezenas de apps) como "dado do app". Corrigido: publisher em
   `GenericPublishers` nunca vira token; so o displayName discrimina. Espelha o
   ADCU/ADAU do Revo que grava paths EXATOS, nao heuristica por publisher.

Build Core: 0 erros / 18 warnings. GUI: 0 erros (so MSB3021 quando app aberto).

### Sessao 06/08 (cont.) — Scan de residuos: reg_scan_ffi (Rust) DEBUGADO e funcional

Sintoma: `reg_scan_ffi` retornava resultados INCONSISTENTES (n=0 / n=1 / n=3 em
processos diferentes) mesmo com a mesma DLL (SHA256 identico), apesar do C#
`.NET` abrir `HKLM\SOFTWARE` sem problema. O scan nativo era estavelmente 0
enquanto o P/Invoke C# puro funcionava.

**CAUSA RAIZ 1 (a mais importante)**: `fn wide(s)` produzia UTF-16 SEM o
terminador NUL (`s.encode_utf16().collect()`). Todas as chamadas a
`RegOpenKeyExW`(e as demais APIs de registro) liam alem do buffer procurando
um zero — comportamento dependente do alocador: quando o heap acontecia de
ter zero depois, "funcionava" (n=1); quando nao, `open_full_path` retornava 0
e o scan resultava vazio. .NET P/Invoke anexa o NUL automaticamente, por isso
o C# sempre funcionava. `open_key`, `scan_key`, `debug` etc. todos usavam
`wide()`. **Corrigido**: `wide()` agora faz `v.push(0)`.

**CAUSA RAIZ 2**: `confidence_generate_impl` fazia fatiamentos por byte
`display_name[..folder_name.len()]` / `folder_name[..display_name.len()]` que
PANICAM ("end byte index ... is not a char boundary; it is inside '™'") quando
o corte caia dentro de um caractere multi-byte (ex: pastas com '™' no nome).
`panic = "abort"` no release matava o processo inteiro. Corrigido: usa
`.get(..len).is_some_and(...)`, que retorna None no corte invalido (seguro).

**Correcoes no lado C#**:
1. `NativeRegistry.cs` REEscrito: marshalling de `StringBuilder` remove-se no
   primeiro `\0` (inutil para multi-string NUL-terminado) — agora usa
   `IntPtr` buffer + `Marshal.PtrToStringUni` com avanco por `len+1`. Probe
   de `UseNative` em ctor estatico chamava `Scan` (que gate) antes de ser
   setado — o probe agora chama `reg_scan_ffi` diretamente com buffer IntPtr.
2. Tres pontos de integracao (DeepUninstaller) continuam: `ScanHiveForNames`
   (mode 0), `ScanSoftwareRecursive` (mode 1, depth==0), `ScanHiveByValues`
   (mode 2), cada um com fallback C# se `NativeRegistry.Scan` retorna null.

**TESTADO (06/08, host)**: DLL nova no `rust_native\target\release`, copiadas
para bin Core Debug/Release + GUI Debug/Release. Resultados DETERMINISTICOS:
- `reg_scan_ffi("HKEY_LOCAL_MACHINE\SOFTWARE", FN)
  "7-Zip"/install, mode 1` -> n=2: `SOFTWARE\7-Zip` + `SOFTWARE\Classes\CLSID\{23170F69...}` (CLSID real do 7-Zip, match por valor) — repetido 6x idempotente.
- `mode 2` CLSID -> n=1 (o mesmo CLSID). `mode 0` Run + "x" -> n=0.
- `mode 1` na arvore inteira de SOFTWARE com nome sem match: n=0, 3x, sem crash
  (stress nao panica).
- C# wrapper (`KitLugia.Core.NativeRegistry`) idem via harness dotnet console
  (`UseNative: True`, 3 linhas de scan compativeis).
- Build Core Debug/Release: 0 erros / 18 warnings (baseline nullable).

Obs: `clean_name` usa regex (RE_TRIM_PUBLISHER etc.) — nao afeta. Demais
exports FFI intocados.

### Sessao 06/08 (cont.) — Scan nativo agora CONSISTE com o C#

Flags para lembrar:

- `wide()` = `encode_utf16 + push(0)` — SEMPRE NUL-terminated. Nunca passar
  um `Vec<u16>` sem terminator para Advapi32.
- `read` de string do FFI: usar `Marshal.PtrToStringUni(IntPtr.Add(buf, pos*2))`
  com `pos += len + 1`; `StringBuilder` como buffer de saida do reg_scan_ffi
  estava errado.
- `confidence_generate_impl` nao usa mais indexacao por `..len()` — usar
  `get(..).is_some_and`.

### Proxima sessao (05/08 cont.)
- [x] ~~Conectar o ReviewPanel (Revo-style) ao fluxo de desinstalacao individual~~ (FEITO)
- [x] ~~Eliminar leitura duplicada de FileVersionInfo (VerifyFolder x HasUnrelated)~~ (FEITO: ProbeFolderBinaries)
- [x] ~~Implementar achados do Revo: ADCU/ADAU markers + RegExclude + "Ignorar Sempre"~~ (FEITO 06/08)
- [ ] Testar no app: REMOVER app -> review abre com caminhos diretos -> deletar
- [ ] Comparar direto: Revo x Kit no mesmo app (listas de leftovers) - PREFERENCIALMENTE PRONTO p/ o ZCode (ver sessao 06/08 noite)
- [ ] (opcional) Mesmo fluxo no batch (remover N apps -> abrir review consolidado por app)
- [ ] (opcional) Allowlist de residuos por app no KitLugia (estilo PC Manager ResidualRule)
- [ ] (opcional) Marcadores de path de dados por app no ScanUwpLeftovers (estilo Revo ADCU/ADAU)

### Sessao 06/08 (noite) - CAUSA RAIZ: scan de registro achava 0 no ZCode (Revo achava 4)

**Sintoma**: desinstalar ZCode (VS Code fork) via kit -> review abria com 0 residuos de
registro, mas o Revo achava: `HKCU\Software\Classes\CLSID\{538D58C6-2C65-4374-B215-C229163232B7}`
(LocalServer32 -> ZCode.exe), `Directory\shell\ZCode.OpenInZCode`, `Drive\shell\ZCode.OpenInZCode`
(command), e `HKCU\Software\Classes\zcode` (URL Protocol + shell\open\command).

**Causa raiz (3 bugs, todos em DeepUninstaller.cs)**:
1. **`ScanComByFilePath` abortava tudo com `!Directory.Exists(installLocation)`**
   (linha ~3904). O ZCode instalava em `...\Programs\ZCode` e DEPOIS da uninstall o
   diretorio NAO existia mais -> o scan COM inteiro (CLSID/TypeLib/Interface) morria
   antes de comecar. Residual com exe deletado e o caso mais comum de scan pos-uninstall.
2. **`ScanComClsidEntries`/`ScanComTypeLibEntries` exigiam `File.Exists(filePath)`**
   (linhas ~4026/4114) -> CLSID que aponta p/ exe ja deletado era pulado.
3. **Escopo**: classPathsNominal so tinha HKLM pra `Directory\shell`/`*\shell` (nunca
   HKCU), nao cobria `Drive\shell`/`Directory\Background\shell`, nao cobria protocolos
   URL custom (`Classes\zcode`), e `guidHivesNominal` nao tinha os hives HKCU
   (CLSID/AppID/Interface/TypeLib) para varredura por valor.

**Correcao (generica, estilo Revo, sem hardcode por app)**:
- Removido o gate `Directory.Exists` e `File.Exists` - o match e so por prefixo de path
  (`StartsWith(normalizedInstall)`), que ja e seguro (nao da falso positivo).
- `guidHivesNominal`/`guidHivesExtra`: todos os hives HKCU (Classes\CLSID/AppID/Interface/TypeLib)
  + `HKCU\Classes\Directory\shell`/`Drive\shell`/`Background\shell` + `HKCU\SOFTWARE\Classes` raiz.
- NOVOS `ScanContextMenuHandlers` (escaneia `Directory\shell`, `Drive\shell`, `*\shell`,
  `Directory\Background\shell` - le subchave `command` e bate path) e `ScanProtocolHandlers`
  (so keys com valor `URL Protocol` + `shell\open\command` referenciando install; filtra
  nomes >32 chars / com ponto para nao varrer os milhares de ProgID/extensoes).
- Helper `CommandStringReferencesInstall(cmd, normalizedInstall)` (primeiro token com
  path, seguro).
- Chamados dentro de `ScanComByFilePath` (agora com param `displayName`), por raiz HKLM/HKCU.

**TESTADO (host, 06/08 noite, harness reflection em ZCode 3.2.2, install dir DELETADO)**:
- `ScanComByFilePath`: 1249 ms -> **4 itens** (CLSID + Directory\shell + Drive\shell + zcode).
- `ScanLeftoverRegistry` FULL (Moderate): **4 itens**, deterministico em 2+ runs (~4.6 s).
- Antes: 0 itens. Agora bate com o achado do Revo.
- Build solucao: 0 erros / 122 warnings (baseline nullable).

Flags aprendidas (evitar regressao):
- COM match SEM `File.Exists`: um leftover com exe deletado ainda e um residuo valido.
- `ScanHiveByValues`/`ScanSoftwareRecursive` JA aceitam installLocation; o problema era o
  gate no caminho COM que matava o scan todo.
- O fluxo do review (`BtnProgramRemove_Click`) chama `DeepUninstallProgram`
  (captureBaseline:false) -> `ScanLeftoverRegistry` com displayName + installLocation.

### Sessao 06/08 (madrugada) - installLocation vazio no GUI: ResolveInstallLocation (registro-primeiro)

**Sintoma**: o harness (com path passado explicitamente) achava os 4 itens do Revo, mas o
GUI real do ZCode mostrava REGISTRO vazio (arquivos apareciam). Log: `ScanLeftoverFiles:
1005 ms (4 itens)`, `[REG] ComByFilePath: 26 ms` (early-return), `ScanLeftoverRegistry:
931 ms (0 itens)`.

**Causa raiz**: `program.InstallLocation` vazio no GUI -> `RunScanPhaseAsync` recebia
`installLocation=""` -> `ScanComByFilePath`/scan de registro por path abortava antes de
começar. `GetInstallLocationFromRegistry` retorna null quando a chave Uninstall nao tem
InstallLocation OU o dir nao existe mais (pos-uninstall). Na VM/teste manual o path era
passado direto, por isso o harness "funcionava" e o GUI nao.

**Correcoes (DeepUninstaller.cs)**:
1. `InferInvitoDirectory` (novo): infer de partir de leftovers de ARQUIVOS que ainda
   existem (DCU/ADAU-style) — leaf name match + under Programs + hasExe.
2. `ExtractInstallDirFromRegistryPaths` (novo, MAIS FORTE): varre VALUES do registro que
   ainda apontam p/ o exe do app — CLSID `LocalServer32`/`InprocServer32` (SUBKEY, nao
   valor! `GetValue("LocalServer32")` retorna null; abrir subchave e ler `(default)`),
   `shell\open\command` (tambem subkey `(default)`), `URL Protocol` de Classes, e
   DisplayIcon/InstallLocation/UninstallString das chaves Uninstall. Sem `Directory.Exists`
   — funciona MESMO quando a pasta foi deletada (caso pos-uninstall).
3. `ResolveInstallLocation`: registry-primeiro, leftovers-depois. Aplicado nos 3 fluxos
   (`RunScanPhaseAsync` L276, `DeepUninstallProgram` obsoleto L534, `ScanLeftovers` L585).
4. Adicionado `using System.Text.RegularExpressions` (nao existia!). `BuildDisplayTokens`
   split agnostico de versão no nome. Quirks: altura de ":", 65+).

**TESTADO (host, harness reflection)**: `ScanLeftovers('ZCode 3.2.2', pub empty,
Moderate)` agora retorna **FILES 1 / REGISTRY 4** (CLSID + Directory\shell +
Drive\shell + zcode) — identico ao Revo, MESMO com installLocation vazio e instalavel
deletado. `ExtractInstallDirFromRegistryPaths` -> `C:\...\Local\Programs\ZCode` (recover
do CLSID HKCU LocalServer32). Build solucao: 0 erros / 104 warnings (baseline).

Flags para lembrar:
- `LocalServer32` de CLSID e `shell\open\command` de protocolo/verb sao **SUBKEYS** cujo
  exe esta no valor `(default)` — usar `OpenSubKey(nome)?.GetValue(null)`, nao
  `GetValue(nome)`.
- Quando installLocation chega vazio, SEMPRE tenta o path scan de formagra antes de
  desistir: o registro guarda o dir/exec exato que os leftovers COM ainda apontam.
- Re-testar no GUI: remover ZCode de novo e conferir que o review agora abre com os
  4 residuos de registro (REGISTRO nao vazio).

### Sessao 08/08 - PathRepair robusto (nunca remove caminhos bons) + Auto-start universal

Pedido do usuario: "deixe o PathRepair mais robusto: que ele adicione e nunca remova caminhos bons".
Segundo pedido: auto-start (iniciar com o Windows) e "arquivos do appdata" nao funcionavam
em instalacao nova (botao verde Otimizacao Inteligente no DashboardPage marca ChkStartWithWindows
e chama SetAutoStart).

1. **PathRepair.cs endurecido (Core)**:
   - `RepairPathEntries` (caso Missing/`.dotnet\tools`): se `Directory.CreateDirectory` falhar,
     a entrada agora e MANTIDA no PATH (`repaired.Add(entry.CleanValue)` sempre) — antes era
     removida silenciosamente.
   - `EnsureSystemPathMinimum`: minimos adicionados entram no `seen` (evita duplicar).
   - `EnsureUserPathMinimum` (~L352): ignora caminhos que ainda nao existem (exceto vars `%...%`).
   - `RecoverFromExecutableScan` (~L390): try/catch por alvo; filtro `avoidSubstrings`
     (`node_modules`, `\.git\`, `\sdk\`, `\examples\`, `\test\`, `\tests\`, `\cache\`,
     `\scratch\`, `\resources\app\`); valida `Directory.Exists`.
2. **GeneralRepairManager.cs (~L1216)** ("Reparar PATH do Sistema"): bloco User PATH agora
   **adiciona de verdade** os programas faltantes via `EnsureUserPathMinimum(fmtPath, recovered)`
   dentro de try/catch; `SetEnvironmentVariable` se `fmtChanged || addedPaths.Count > 0`;
   typo `errMsg` corrigido (era `{errMsg}` na interpolacao).
   Local: manual na RepairsPage (`GetAllRepairs`) E automatico no Guardian (Guardian.cs:2709,
   mesmo fluxo de metodos — RepairPathEntries -> EnsureSystemPathMinimum -> EnsureUser +
   RecoverFromExecutableScan fallback).
3. **Valorant overlay (bug "tela escura sem cliques")**: 2 causas corrigidas —
   (a) scan sincrono na UI thread (`RunExternalProcess` e bloqueante) congelava a janela:
       cada check agora roda em `Task.Run`; guard `_isClosed` faz o X funcionar durante o scan;
   (b) `_isRunningRepair` ficava `true` apos o fluxo do Valorant (return sem reset) e traia
       todos os outros reparos — resetado antes do `ShowValorantDiagnosticPanel(); return;`.
   "Aplicar Reparo" (bcdedit hypervisorlaunchtype + 2 reg DeviceGuard) movido para
   `ApplyValorantRepairAsync` (Task.Run).
4. **Auto-start universal (TrayIconService.cs)** — causa raiz: auto-start dependia SO de
   Task Scheduler com `RunLevel.Highest`, que em conta sem admin "registra" a tarefa mas ela
   NUNCA dispara (falha silenciosa); fallback Registry existia mas era pos-falha, e o .lnk na
   pasta Startup (que fica no AppData) NUNCA era criado. Novo desenho com 3 metodos sem duplicar o boot:
   - `IsAutoStartEnabled()`: retorna true se QUALQUER metodo ativo apontar para o exe atual
     (1) Registry HKCU `Run` (universal, com/sem admin) (2) pasta Startup `.lnk` e
     (3) Task Scheduler best-effort. Retorna versao antiga de task como mismatch.
   - `SetAutoStart(enable=true)`: metodo 1 = HKCU Run (universal); metodo 2 = .lnk na pasta
     Startup so se o Registry falhar; metodo 3 = Task Scheduler SOMENTE elevado
     (`IsRunningElevated()` via WindowsIdentity) — ao criar/habilitar a tarefa remove
     Registry + .lnk para nao duplicar a execucao no boot. Mutex global do Program.cs
     (KitLugia_SingleInstance) cobre qualquer duplicidade residual.
   - `SetAutoStart(false)`: remove os 3 (task + registry + lnk), nunca deixa lixo.
   - Helpers novos: `SetRegistryEntry`, `SetStartupShortcut` (reusa
     `StartupManager.CreateShortcut` do Core), `IsRunningElevated`.
Build (GUI, 08/08): **0 erros / 104 warnings (baseline)**.

**A TESTAR**: marcar "Iniciar com o Windows" no Dashboard (botao verde) -> conferir
Registry Run `HKCU\...\Run\KitLugia` OU pasta Startup `KitLugia.lnk` criada -> reiniciar ->
kit abre com --tray. Em maquina sem admin o checkbox deve continuar aceso apos reboot.

### Sessao 08/08 (cont.) — RmCacheLoc (NVIDIA) + auto-start antes do logon

Pedido do usuario: (1) adicionar `RmCacheLoc` na TweaksPage (card L2/L3) "e ele precisa
so colocar o numero de processadores logicos"; (2) o kit iniciar junto do boot do
Windows, nao so no logon ("alguns apps iniciam antes de eu fazer logon").

1. **RmCacheLoc** (pesquisa web confirmou: valor do driver NVIDIA — Resource Manager,
   prefixo `Rm`; aparece nos .inf oficiais como `HKR,,RmCacheLoc`; guias de otimizacao
   usam o numero de nucleos LOGICOS da CPU). `SystemTweaks.cs`:
   - `FindNvidiaAdapterRegPaths()` (novo, resolver generico) — varre DUAS arvores e
     retorna TODOS os caminhos NVIDIA (filtro `DriverDesc` ou
     `HardwareInformation.AdapterString` contendo "NVIDIA"):
     (a) PnP software key `Control\Class\{4d36e968-...}\00xx` (aceite 0000 OU 0004,
         subchaves numericas), o caminho canonico escrito pelos INF do driver;
     (b) espelho `Control\Video\{GUID}\0000` (o Windows cria um por adaptador).
     Testado no host: achou `Class\0000` (RTX 5070 Ti) + `Video\{3E046CDA-9115-11F1-
     8047-0E12A5BE7880}\0000` (o caminho real do regedit do usuario).
   - `IsRmCacheLocSet()` — true se QUALQUER caminho NVIDIA tiver DWORD > 0.
   - `ApplyRmCacheLocTweak()` — grava `RmCacheLoc = Environment.ProcessorCount`
     (DWord) em TODOS os caminhos encontrados (nunca erra o alvo). Requer admin (HKLM).
   - `RevertRmCacheLocTweak()` — deleta o valor em todos os caminhos.
   - `ToggleRmCacheLocTweak()` — padrao ToggleAutoCacheTweak (mensagem com o numero aplicado).
2. **UI TweaksPage**: nova linha "RmCacheLoc (NVIDIA)" dentro do card Cache de CPU
   (acima do Nagle): botao info, `InfoRmCacheLoc` (status do valor), `StatusRmCacheLoc`
   e toggle `ChkRmCacheLoc` (handler espelhando `ChkL2Cache_Click` via
   `ToggleRmCacheLocTweak`). LoadSettings carrega `IsRmCacheLocSet()`.
3. **Auto-start no boot** (`TrayIconService.SetAutoStart`, bloco task admin):
   - Adicionado `BootTrigger { Delay = TimeSpan.Zero }` junto ao `LogonTrigger` — a task
     registra "ao iniciar" alem de "no logon" (com Fast Startup/auto-login inicia o mais
     cedo possivel; `ExecutionTimeLimit=0` + Priority High mantidos).
   - `td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew` — evita processo
     duplo caso Boot e Logon disparem na mesma janela. Obs: enum na API TaskScheduler
     2.12.2 e `TaskInstancesPolicy` (nao `TaskMultipleInstancesPolicy` — sem build).

Build: **0 erros / 104 warnings (baseline)**.

**A TESTAR**: TweaksPage -> toggle RmCacheLoc -> conferir `regedit` na subchave NVIDIA
(0000/0001...) com `RmCacheLoc = N`; reiniciar e ver task agendada disparando no boot
(taskchd.msc > KitLugia > Triggers: "Ao iniciar o computador" + "Ao fazer logon").
### Sessao 08/08 (cont.) - RmCacheLoc aplicado + TESTADO no host (460 FPS) + Logger com origem

API TESTS com RmCacheLoc no host: toggle ON -> subchave NVIDIA (RTX 5070 Ti)
com RmCacheLoc = 24 (logicos); jogo cravado 460 FPS (antes ~150-260).
Origem da chave: **driver NVIDIA Resource Manager** - aparece nos .inf oficiais
como `HKR,,RmCacheLoc`; otimizacoes usam o numero de nucleos LOGICOS.

`Exception suppressed`: CAUSA RAIZ explicada - o projeto tem ~600 catchs
defensivos genericos (`catch { Logger.LogWarning("Unknown", "Exception suppressed"); }`)
sem variavel `ex` (engolem exception benigna de acesso negado/metadado quebrado).
O LogWarning NAO dizia de onde vinha (contexto fixo "Unknown").

**CORRECAO (Logger.cs)**: LogWarning ganhou parametros `[CallerFilePath]` /
`[CallerLineNumber]` / `[CallerMemberName]` opcionais (Caller* do compilador) -
quando a mensagem contem "Exception suppressed", o log anexa
`[origem: Arquivo.cs:NNN Metodo]` SEM precisar editar nenhum call site.
O rate limiter (janela 60s + resumo /100) continua por chave context|message
(mensagem agora com origem = key mais especifica por site).

**TESTADO**: harness console temporario com 5 throws -> 1 linha no log:
`[AVISO] (Unknown): Exception suppressed [origem: Program.cs:17 Repeater]`.
Build: 0 erros.

### Sessao 08/08 (cont.) - Log virtualizado: fim do limite de 500 linhas (inspiracao ChatGPT)

Sintoma: quando o log passava de 500 linhas (ou removia o limite via checkbox "Sem
Limite"), o programa e o PC travavam. Causa: GlobalConsole usava um TextBox cujo
`Text` era RECONSTRUIDO por completo a cada update (`string.Join` + `TxtLog.Text`, O(n^2))
e o trim do ConsoleManager derrubava linhas antigas ("Limite 500").

**Nova arquitetura (store completo no disco + anel em RAM + UI virtualizada)**:

1. **`KitLugia.GUI\Logging\LogStore.cs`** (novo): armazenamento desacoplado da UI -
   - Persistencia COMPLETA em disco: `%LOCALAPPDATA%\KitLugia\Logs\KitLugiaConsole.log`
     (append, sem limite de linhas). Rotacao a 64MB (renomeia para `.old` e recomeca);
     `GetFullText()` le `.old` + atual em ordem cronologica.
   - Anel em memoria com teto `MaxInMemoryLines = 20000` (~3MB) - a RAM NUNCA explode.
   - `TotalLines` (contagem), `GetRecent(n)` (para a UI, sem ler disco no caminho quente).

2. **ConsoleManager reescrito**: sem limite artificial. `WriteLine` -> LogStore.AppendLine
   (tudo vai ao disco) + Logs.Add (espelho da UI, teto 20k, remove do inicio).
   `loglimit` agora informa que logs sao ilimitados por design + caminho do arquivo.

3. **GlobalConsole.xaml/.cs reescrito**: TextBox -> **ListBox virtualizado**
   (`VirtualizingStackPanel` + `Recycling`, CanContentScroll, SelectionMode=Extended):
   - Renderiza SO ~30 linhas visiveis, por mais que o log tenha.
   - **Auto-scroll inteligente**: `_stickToBottom` - so rola para o fim se o usuario
     estiver no rodape (delta <= 24px); se subir para investigar, NAO puxa de volta.
   - **Busca** (TxtSearch) via `ICollectionView.Filter` (filtrar sobre o espelho, nao
     recria itens). Esc limpa a busca. Ctrl+A seleciona tudo; Ctrl+C copia selecao.
   - **COPIAR SELECAO**: only selected items (separados por quebra).
   - **COPIAR TUDO**: le `LogStore.GetFullText()` em background (arquivo completo,
     sem depender da UI) e joga no clipboard - 100k linhas copiavel SEM travar.
   - TxtCount mostra "N linhas em disco � M na memoria". Chiado do checkbox "Sem Limite"
     removido (nao faz mais sentido - virtualizacao tornou o limite obsoleto).

**TESTADO (harness console ref Classic)**: 30k linhas -> `GetFullText` retorna as
30.000 (rotacao .old concatena direito); `GetRecent(3)` = ultimas 3; RAM estavel
(delta 2MB com 50k linhas adicionais); rotacao 64MB dispara e nao quebra.
Build GUI: 0 erros / 104 warnings (baseline).

A testar no app: abrir console -> rodar algo verboso (shrink/scan) -> conferir
scroll suave com milhares de linhas, subir p/ investigar sem freeze, COPIAR TUDO com
~50k linhas instantaneo, busca filtra sem re-bind.

### Sessao 08/08 (cont.) - Fix: mojibake no contador + Ctrl+C da selecao sem clicar no botao

1. **Mojibake "773 linhas em disco A. 773 na memoria" visto pelo usuario (host)**:
   - Causa: durante o build anterior, um `Set-Content -Encoding UTF8` do PowerShell
     releu o GlobalConsole.xaml.cs como ANSI e RE-ENCODED os chars (., o -> e.I
     duplo-encoding real no arquivo). O contador era "N linhas em disco . N na
     memoria" com o char U+00B7 (middle dot) e acentos.
   - Correcao: arquivo reescrito em UTF-8 puro; o contador agora usa ASCII puro
     "N linhas em disco | M na memoria" (nunca mais quebra por encoding).
   - Scan de TODO o codigo (KitLugia.GUI + KitLugia.Core) por padroes de
     duplo-encoding (C3 C3 83 / C2 83, e pares "é","ã","ó","·"...) - ZERO
     resultados: o resto do codigo tem acentos legitimos (sem "erros ao redor").

2. **Ctrl+C agora copia a selecao sem clicar no botao**: handler movido para
   `PreviewKeyDown` (tunneling) do UserControl inteiro - dispara com o foco em
   QUALQUER parte do console (barra, botoes, busca), nao so no ListBox. Esc limpa
   a busca; Ctrl+A seleciona tudo (exceto quando o foco esta no TextBox de busca,
   que mantem comportamento nativo).

### Sessao 08/08 (cont.) - Menu de contexto no console + .editorconfig anti-mojibake

**Pedido do usuario**: (1) opcoes de botao direito no log (copiar selecionados,
copiar tudo, selecionar tudo, limpar); (2) resolver o problema recorrente de
UTF-8 - paginas/arquivos novos devem ja nascer com encoding correto.

1. **Menu de contexto (GlobalConsole.xaml)**: ListBox.ContextMenu com 4 itens
   escuros (folder #1E1E1E): Copiar selecao (Ctrl+C), Copiar tudo, Selecionar tudo
   (Ctrl+A), Limpar console. Handlers Mnu* no .cs reusam os botoes existentes.

2. **`.editorconfig` criado na raiz (NOVO)**: `root = true`, charset utf-8-bom
   para *.cs/*.xaml/*.ps1 (BOM evita que PowerShell 5.1 e apps antigos releiam
   como ANSI/Windows-1252), CRLF, tudo com 4 espacos, trim de espacos, final
   newline. O Visual Studio/VS Code/Rider passam a salvar arquivos novos ja em
   UTF-8+BOM automaticamente - o mojibake recorrente (A., o -> e.I, A�, A�)
   deixa de acontecer ao criar novas paginas/classes.

### Sessao 08/08 (cont.) - Auditoria de RAM: vazamentos de Process.GetProcesses sem Dispose

Pedido do usuario: investigar por que a RAM do Kit e "aleatoria" (60MB idle, ate
200MB em algumas paginas, e o GC limpa sozinho) e otimizar.

**Causas raiz encontradas (2 grupos)**:

1. **`Process.GetProcesses()` chamado sem `Dispose()`** — cada enumeracao cria
   centenas de objetos `Process` que penduram handles nativos ate o GC rodar.
   Locais corrigidos (TrayIconService.cs):
   - `UpdateProcessProfiles` (MonitorTick, a cada 30s) — laço principal de tracker
     de processos: agora `finally { proc.Dispose(); }` por item.
   - `ShutdownTurboCharge`, `DetectAndTrimLeaks` (ja tinha), loop ProBalance
     (throttle) — adicionado Dispose.
   - `EnsureSystemProcess` (lsass fallback): `using (var lsass = ...FirstOrDefault())`.
   - `GetMainWindowTitle` — loop inutil (sempre retornava "PID:x"); removida a
     enumeracao de "explorer" inteira (era dead code + leak).
   - ProcessMonitorPage.xaml.cs: os 3 pontos (`UpdateTimer_Tick`'s Task.Run,
     `UpdateProcessList`, `RefreshProcessesAsync`) reescritos de LINQ (`Where.
     Select` sobre Process) para `foreach` + `try/catch/finally Dispose`, com
     `OrderByDescending(CpuUsage).Take(50)` preservado. Isto roda a cada 2s!
   - `ApplyProcessRamLimits`/`ApplyProcessCpuLimits` (Job Objects) ja faziam
     Dispose corretamente (sem mudanca).
   - Line 4402 removed: `GetMainWindowTitle` dead code con enumeracao.
2. **`_processProfiles` (ConcurrentDictionary) nunca podava processos mortos**:
   perfil de TODO processo que ja teve janela ficava pra sempre na memoria.
   - Novo campo `ProcessProfile.LastSeenTick` + `_monitorTickCounter` incrementado
     no inicio do `MonitorTick`.
   - Novo `PruneDeadProcessProfiles()`: a cada tick, so age se `Count > 60`;
     remove perfis com `LastSeenTick` mais velho que 60 ticks (30+min a 30s/tick).

3. `MemoryOptimizer.cs:109` — comentario mojibake `ðŸ"¥ CORREA‡AƒO` (encoding),
   removido byte-level via PowerShell (substituiu 1657 bytes maliciosos) — CUIDADO:
   byte surgery e perigoso, uso em windows com git checkout.

Build: 0 erros / 104 warnings (baseline).

**A TESTAR (host/VM)**: deixar o app aberto 1h+ com monitor ativo e conferir no
Process Explorer/PerfMon que o Working Set fica estavel (~60-120MB) sem picos
aleatorios de 200MB+; abrir ProcessMonitorPage por 5min e conferir RAM estavel
com a lista atualizando a cada 2s (antes cada tick vazava N objetos).

### Sessao 08/08 (cont.) - Log console sujo (132k de X) + GameBarPresenceWriter esquecido

**Sintoma 1 (log vazando)**: console mostrava poucas linhas mas COPIAR TUDO colava
132.121 linhas cheias de `X`/linhas de teste. Causa: (a) harness de teste do LogStore
sujo escreveu ~132k linhas no arquivo REAL `%LOCALAPPDATA%\KitLugia\Logs\KitLugiaConsole.log`
(com `.old` de 67 MB da rotacao); (b) `LogStore.Clear()` so truncava o atual e NAO
apagava o `.old`; (c) o construtor do LogStore nunca zerava os arquivos.

**Correcoes (LogStore.cs)**:
1. `Clear()` agora tambem deleta o `.old` (`File.Delete(_filePath + ".old")`).
2. Construtor estatico agora chama **`ResetAllFiles()`** — trunca o arquivo atual e
   apaga o `.old` a cada inicializacao do kit: **log de sessao zerado no boot**, o
   lixo de sessoes passadas nao vaza mais no "copiar tudo".
3. `.old` de 67 MB ja deletado do host.

**Sintoma 2 (GameBarPresenceWriter)**: o kit logava "Windows recriou
GameBarPresenceWriter.exe - precisa desativar novamente" mas NAO re-renomeava.

**Correcoes (TrayIconService.cs + GameBoostPage.xaml.cs)**:
1. `AutoFixGameBarPresenceWriter` agora `public` e age quando o `.exe` existe E
   (preferencia `GameBarPresenceWriterDisabled` ativa OU `.bak` existe — a existencia
   do `.bak` ja e prova de que o usuario desativou) — re-takeown, matar o processo,
   apagar `.bak` antigo e renomear `.exe` -> `.bak`.
2. `GameBoostPage.LoadSettings` (bloco da flag): quando `.exe` e `.bak` existem
   juntos, nao so loga — dispara `Task.Run(() => tray.AutoFixGameBarPresenceWriter())`
   para a pagina reaplicar a renomeacao na hora, e marca o checkbox como desativado
   (preferencia voltada para `GameBarPresenceWriterDisabled`).

Build: 0 erros / 122 warnings (baseline).

**A TESTAR (host)**: reiniciar o kit e conferir que o GameBarPresenceWriter re-desativado
na inicializacao; conferir console "copiar tudo" com so a sessao atual; conferir
na pagina que a reaplicacao ocorre mesmo sem tocar no checkbox.

### Sessao 08/08 (cont.) - "Exception suppressed" com CAUSA REAL (nao so origem) + LoadSettings bug

**Sintoma (host, 21:44)**: log de boot mostrava `[AVISO] (Unknown): Exception suppressed
[origem: TrayIconService.cs:1706 LoadSettings]` + SystemTweaks.cs:331/363, ServicesPage.cs:1096,
BrowserCacheManager.cs:142, AdapterManager.cs:174 (antes: contexto fixo "Unknown" sem causa).

**CAUSA RAIZ (LoadSettings, TrayIconService.cs)**: `HighRamThresholdMB = (long)key.GetValue(...)`
e `HighCpuThresholdPercent = (double)key.GetValue(...)` faziam CAST DIRETO do objeto do
registro. `Registry.SetValue` com `double` grava REG_BINARY (byte[8]) e `long` como REG_QWORD;
o cast `(long)/(double)` de um blob/string NUNCA funciona e estoura `InvalidCastException` no
primeiro valor com tipo inesperado — e o `catch { Logger.LogWarning("Unknown", ...) }` engolia
tudo, ABORTANDO o LoadSettings inteiro: TODAS as preferências depois da linha ~1693 caíam
para default silenciosamente (GameBarPresenceWriterDisabled, SmartScreenDisabled, etc.).

**Correcoes (TrayIconService.cs)**:
1. **Helpers novos `ReadLongSetting`/`ReadDoubleSetting`** (apos o LoadSettings): leitura
   defensiva — aceita long/int/double/string/byte[] (REG_DWORD/QWORD/BINARY/SZ) e cai no
   default se nada casar. Usados em HighRamThresholdMB / HighCpuThresholdPercent.
2. Catch do LoadSettings: `catch (Exception ex) { Logger.Log($"⚠️ LoadSettings: {ex.GetType().Name}: {ex.Message}") }`
   — se algo ainda falhar, o log mostra o MOTIVO real, nao "Exception suppressed" anonimo.

**Outros pontos que apareciam no log -> causa real visivel**:
- `SystemTweaks.GetUwpDisplayName` (331) / `BuildAumid` (363): catches com
  `$"Exception suppressed (manifest UWP): {ex.Message}"` (erro benigno: algum pacote
  com AppxManifest inacessivel) — silenciado para suportar o principal, mantendo causa.
- `ServicesPage.LoadServices` (1096): `OperationCanceledException` silenciado (navegacao
  livre cancela o load - normal); outros erros logam `{Tipo}: {Message}`.
- `BrowserCacheManager.GetDirectorySize` (142): inner/file inacessiveis silenciosos;
  UnauthorizedAccess/IOException -> return 0 sem LOG (pasta protegida — normal); outros
  erros logam caminho+mensagem.
- `AdapterManager` (127/142/156/174/177): Get-NetAdapter/ConnectionKey falhas sao EUs
  esperados (sem permissao) -> silent; perfil por subchave / enumeracao: log informativo
  `ℹ️ AdapterManager: ...` com tipo+mensagem (nao Warning).

Build: 0 erros / 122 warnings (baseline).

**A TESTAR (host)**: abrir o kit -> conferir que NENHUM `Exception suppressed` anonimo
aparece no boot (LoadSettings loga correto), SmartAlerts mostram 2048MB/80% (que ja caiam
p/ default), pagina Services/Cache/Adapters sem avisos falsos.

### Proximas pendencias abertas (mais antigas)
- Testar no app: toggle "Boost do App Ativo" + perfil personalizado
- Testar GameBarPresenceWriter / toggles da comunidade apos reboot


### Sessao 09/08 - Shrink marker-only + log 100% limpo + downgrade riscado

**Downgrade de build (25H2/26200.8973): ABANDONADO.** O usuario testou e a Microsoft
realmente travou (o patch da setupcompat.dll nao e mais suficiente na midia nova).
Ferramenta `KitLugia.GUI\Tools\Downgrade\` mantida mas NUNCA mais usada. Riscado das
pendencias definitivamente.

**Shrink simplificado para marcador-only (WinbootManager.RamdiskStartnetCmd)**:
Validado pelo usuario no PC e notebook: a verificacao inicial (C: check, embedded
disk/part, shrink_config.ini, scan SOFTWARE hive) SEMPRE falha ou erra o alvo - a
discrepancia DISK/PART entre host WMI e diskpart e universal. O que SEMPRE funcionou
nos 2 maquinas foi o marcador `KL_SHRINK_TARGET.dat`. O script gerado agora:
1. SO marca em `for /l disk 0-3 x partition 1-8` procurando `Z:\KL_SHRINK_TARGET.dat`
2. Le SHRINK_MB do marcador, DISK_N/PART_N sao os do PROPRIO diskpart (nunca WMI)
3. Nada encontrado -> `Status: FAIL` + reboot (sem shrink).
Removidos: `:run_vol_c`, bloco embedded E_DISK/E_PART, leitura de shrink_config.ini,
scan de SOFTWARE hive. Assinatura mantida (parametros sobram sem uso).

**Log 100% limpo na inicializacao (build 0 erros/0 avisos Core)**:
- `SystemTweaks.GetUwpDisplayName`/`BuildAumid`: silenciados de vez (pacote UWP com
  AppxManifest inacessivel e normal; fallback GetStartAppsFriendlyNames cobre o nome).
- `AppIconHelper.cs` (187/217): silenciado (pasta de app protegida -> icone fica sem).
- `MemoryOptimizer.cs` (132): silenciado (processo morreu entre scan e trim - racing).
- `ServicesPage.LoadScheduledTasks`: OperationCanceledException silenciado (navegacao
  rapida cancela - nao e erro; LoadServices ja tinha o mesmo tratamento).

**GameBarPresenceWriter = metodo padrao de renomeio**: o usuario confirmou que
funciona. Para renomear qualquer outro executavel do Windows (gamebar, etc.), copiar
o padrao de `AutoFixGameBarPresenceWriter` (TrayIconService): preferencia salva em
registry TraySettings + JSON, re-takeown + taskkill + renomear .exe -> .bak (excluindo
.bak anterior), reaplicado no Initialize via Task.Run, com re-renomeio se o Windows
recriar o .exe (no LoadSettings da GameBoostPage se .exe e .bak existem juntos).

**Boost do App Ativo (GameBoost) - explicacao de separacao**: o toggle e separado do
motor v1/v2/v3 porque o motor aplica prioridade GLOBAL por processo/perfil, enquanto o
"Boost do App Ativo" e um governador de FOREGROUND dirigido por SetWinEventHook
(foco): aplica prioridade custom (Normal/High/RealTime) ao processo com janela ativa
e REVERTE ao perder o foco - um comportorado sobre o motor, nao um perfil. Persistencia
separada (TraySettings\ForegroundBoost). Usuario validou: funciona.

**Auto-start validado (log real)**: Registry Run + Task Scheduler existente
habilitada (a task substitui Registry/Startup); SeDebugPrivilege OK; 5/5 RAM limits
carregados; SmartAlerts com 2048MB/80% (load correto apos fix do cast).

### Sessao 09/08 (cont.) - Scan de registro: falsos positivos "Display" (DDU) + duplicatas HKEY_USERS

**Sintoma**: scan de residuos de "Display Driver Uninstaller" retornava 4 falsos positivos
(Realtek Audio `Uninstall\{F132AF7F...}` via DisplayIcon=Display.ico, CLSID Windows
`{101193C0...}` com default "Display", Node.js/npm `Components\7829B5D2...` via display.js,
Intel ME `Components\3C4787A3...` via `...\ME\Display`).

**Causa raiz**: fallback `KeyHasValueReferencing` (DeepUninstaller.cs) casava QUALQUER valor
cujo filename tivesse Confidence >= 85 com o displayName — "Display" em "Display Driver
Uninstaller" (StartsWith -> 90) pegava tudo. O guard anterior (leaf = 1a palavra exigia 2
tokens no data) ainda deixava passar `Display.ico` porque "Drivers" contem "Driver" (substring).

**Correcoes (DeepUninstaller.cs)**:
1. Guard em KeyHasValueReferencing (x3182): (a) valor sem separador de path ("Display")
   nunca casa (descricao de classe); (b) leaf = 1a palavra do nome exige token adicional
   no DATA; (c) leaf = 1a palavra exige EXTENSAO EXECUTAVEL (.exe/.dll/.com/.bat/.cmd/
   .scr/.ps1) — mata Display.ico e display.js.
2. Batch 10 HKEY_USERS: pulava o SID do usuario atual (HKCU ja cobre o mesmo perfil) —
   eliminadas duplicatas `HKEY_USERS\S-1-5-...-1001\Software\...` do ZCode (WindowsIdentity.
   GetCurrent().User).

**TESTADO (host, apps instalados)**: DDU = 4 legiveis (Tracing RASAPI32/RASMANCS, App
Paths, Uninstall); ZCode = 8 legiveis (4 do Revo + chave de instalacao GUID + Uninstall +
Audio PolicyConfig BCU + RADAR HeapLeak). Zero falsos positivos. Build: 0 erros.

### Pendencias abertas (revisadas 09/08)
- [ ] Shrink marker-only: re-testar na VM (SCHEDULE -> reboot -> "Found marker" no log)
- [ ] Fresh Install completo na VM (backup -> deleta Windows antigo -> apply -> bootloader)
- [ ] PartitionsPage com Storage UI: validar flags IsSystem/IsBoot/IsSystemFlag + mover letra
- [ ] CleanDisk via IOCTL (IOCTL_DISK_DELETE_DRIVE_LAYOUT) em disco de teste (VM)
- [ ] RAM estavel 1h+ (fix do Dispose do Process.GetProcesses aguardando validacao)
- [x] ~~Review desinstalador (estilo Revo)~~ (validado 09/08: host, DDU + ZCode desinstalados de verdade)
- [ ] Testar no app: batch de remocao multi-app (sequencial, a prova de falhas)
- [ ] Console virtualizado: rodar scan verboso com milhares de linhas sem freeze
- [x] ~~Downgrade de build~~ (ABANDONADO - Microsoft travou; ferramenta mantida)
- [x] ~~Toggle Boost do App Ativo~~ (validado no host)
- [x] ~~Auto-start universal~~ (validado: Registry Run + task existente)
- [x] ~~GameBarPresenceWriter~~ (validado: renomeia .bak no boot)

### Sessao 09/08 (cont.) - Batch multi-app: sequencial + a prova de falhas + guard de scan

Pedido do usuario: "garanta que o fluxo de desinstalar varios apps seguidos esteja correto".

**Auditoria do batch (`BtnRemoveProgramsSelected_Click`, AppsPage.xaml.cs:995)**:
1. **BUG: excecao de UM app derrubava o batch inteiro** - `Task.WhenAll(removeTasks)` com
   `Task.Run(() => DeepUninstallProgram(...))`: se UM app lancasse excecao nao capturada,
   o WhenAll propagava AggregateException -> catch global -> apps ja desinstalados NAO saiam
   da lista, residuos NAO iam para a aba Residuos, resultado parcial perdido.
2. **SemaphoreSlim(2) = 2 UAC/uninstallers GUI simultaneos** - desinstaladores interativos
   (DDU/MsiExec) abriam 2 janelas + 2 prompts UAC ao mesmo tempo.

**REESCRITO (AppsPage.xaml.cs, BtnRemoveProgramsSelected_Click)**:
1. **SEQUENCIAL**: `foreach` com try/catch POR APP - falha de um nunca aborta os outros;
   `result` default (`new UninstallResult()`) no catch para o app continuar sendo anotado.
2. **Residuos SEMPRE registrados** no LeftoverJunkManager (mesmo com UninstallSuccess=false,
   residuos podem existir) - dentro do try, com o result valido; no catch com o default vazio
   (nao adiciona lixo, so nao perde o fluxo).
3. **Lista de falhas detalhada**: mensagem final lista NOME + motivo de cada app que falhou
   (erro da excecao OU result.Errors unidos OU "exit code nao indica sucesso"); apps OK saem
   da lista; apps falhos permanecem para nova tentativa.
4. **finally robusto**: `ProgramsLoadingPanel` colapsado + `_isAppOperation = false` (antes o
   catch global nao colapsava o painel).
5. **Novo guard de scan em background**: apps com `_scanningPrograms.Contains(DisplayName)`
   (desinstalacao individual anterior ainda escaneando) sao REMOVIDOS da selecao com aviso
   ("o review abrira automaticamente quando o scan terminar") - evita desinstalacao duplicada
   + review duplicado do mesmo app.
6. Progresso `[{i}/{total}]` agora monotono (sequencial - antes Interlocked com concorrencia).

**Verificacoes confirmadas na auditoria**:
- `DeepUninstaller` e STATELESS (sem campos estaticos mutaveis) - Paralelismo real so existe
  dentro do Core via `Parallel.Invoke` (L2134) com capturas por batch + lock no AddLocal, e
  pos-filtro (L2136+) remove chaves de outros apps de nome similar - seguro.
- `UninstallResult` tem construtor padrao implicito (propriedades com `= new()`) - valido no catch.
- `LeftoverJunkManager.Add` (L64) thread-safe (lock) com dedup por AppName + janela <1min,
  cap 100 - batch e BtnReviewBack nao colidem.
- Fluxo individual INTOCADO: phase 1 (RunUninstallPhaseAsync) + scan background
  (`_scanningPrograms`/BackgroundTaskTracker) + `EnqueueOrShowReview` (L937) com FIFO
  `_pendingReviews` (L2108: ao fechar o review, abre o proximo pendente).

Build (09/08): 0 erros / 104 warnings (baseline). App reaberto com --tray.

**A TESTAR (VM/host)**: selecionar N apps (um com uninstaller GUI) -> REMOVER -> batch
sequencial, um por vez, residuos na aba Residuos, falha de um app nao aborta os outros,
mensagem final lista os falhos.

### Sessao 09/08 (cont.) - Rodela de loading estilo Windows 11 (spinner de pontos)

Pedido do usuario: a animacao de loading do AppsPage deveria ser a "rodela" que roda
quando o PC liga/desliga no Windows 11 - um CIRCULO DE PONTOS girando (nao elipse
tracejada, nao toast de progresso).

**Tentativas intermediarias (descartadas pelo usuario)**:
1. Overlay com slide-in/slide-out estilo LugiaToast (CubicEase, gradiente, glow) -
   "consegue melhorar a animacao de loading" -> nao era isso.
2. Migracao para ShowProgressToast/UpdateProgressToast/CompleteProgressToast (toast
   pequeno com spinner no LugiaToast, IsProgress=true, transicao amarelo->verde/vermelho
   na Complete) - "use a notificacao em tempo real" -> nao era isso. O usuario queria o
   OVERLAY GRANDE de sempre, so com a rodela diferente.

**O que ficou (validado pelo usuario: "perfeito adorei")**:
- `AppsPage.xaml` (`ProgramsLoadingPanel`, aba Programas, Grid.Row=1, Panel.ZIndex=99,
  Background #CC000000, CornerRadius 8, Visibility=Visible no load):
  - Grid 44x44 com `RotateTransform` (x:Name SpinnerRotatePrograms) + Grid.Triggers
    Loaded -> Storyboard RepeatBehavior=Forever: DoubleAnimation Angle 0->360,
    Duration 0:0:1.4 (rotacao suave estilo boot/shutdown Win11).
  - 5 Ellipses 7x7, Fill #FFD700, RenderTransformOrigin 0.5,0.5, empilhados no centro
    do Grid com `TranslateTransform` (raio 16, angulo 72 entre pontos):
    (0,-16) / (15.2,-4.9) / (9.4,12.9) / (-9.4,12.9) / (-15.2,-4.9).
  - Abaixo: TextBlock "Carregando programas..." + TxtProgramsProgress (#FFD700).
- `AppsPage.xaml.cs`: helpers RESTAURADOS ao formato simples original
  `ShowProgramsLoading()`/`HideProgramsLoading()` (so Visibility.Visible/Collapsed);
  `TxtProgramsProgress` atualizado nos 3 fluxos (LoadPrograms, BtnProgramRemove_Click
  fase 1, batch com progresso sequencial [i/total]).

**Como a rodela funciona (replicar em outras paginas)**:
1. Grid com RenderTransformOrigin 0.5,0.5 + RotateTransform nomeado.
2. Grid.Triggers Loaded -> BeginStoryboard Forever girando Angle 0->360 (1.4s).
3. N pontos (Ellipse) centralizados com TranslateTransform em raio R, angulos
   equidistantes: x = R*sin(a), y = -R*cos(a). 5 pontos a 72 = exatamente o Win11.
4. Opcional: pontos com Fill AccentColor/FFD700; tamanho 7x7 em Grid 44x44 (raio 16).

**Manutencao (LugiaToast)**: o spinner IsProgress/ProgressSpinner adicionado ao
LugiaToast foi MANTIDO (usa ShowProgressToast - inofensivo sem chamadores; nenhum
fluxo do AppsPage usa mais toasts de progresso).

Build (09/08): 0 erros / 122 warnings (baseline). App reaberto com --tray.

### Sessao 09/08 (cont.) - Falsos positivos "Components" do DDU: native scan (Rust) sem guards

**Sintoma (host, review real do DDU)**: scan de residuos apos desinstalar o Display
Driver Uninstaller listava como [DELETAR] 2 chaves MSI que NAO eram do app:
`Components\3C4787A3917DB895087B8C8AC674191D` (Intel ME, valor
"22:\Software\...\Intel\ME\Display") e `Components\7829B5D21745E6247A7108F3E2BE4BC0`
(npm do Node.js, valor "...\node_modules\npm\lib\utils\display.js") - confirmado via
reg query no host.

**Causa raiz**: os guards anti-nome-unico da sessao anterior (KeyHasValueReferencing C#:
separador de path, token adicional, extensao executavel) existiam SO no walker C#. O
NATIVE scan reg_scan_ffi (Rust) roda PRIMEIRO em ScanHiveForNames/ScanSoftwareRecursive/
ScanHiveByValues (modes 0/1/2) e casa por VALOR com confidence SEM nenhum guard -
"Display" em "Display Driver Uninstaller" casava "...\ME\Display" e "display.js".
PROVADO via harness: `NativeRegistry.Scan(...Components, mode 0)` retorna os 2 GUIDs,
o filtro novo os elimina (RAW=2 -> VALIDATED=0).

**Correcoes (DeepUninstaller.cs)**:
1. `ScanMsiUserData`: "Components" NAO vai mais para ScanHiveForNames (GUIDs hex nunca
   casam por nome e o match por valor so gera falso positivo). Components fica
   exclusivamente com ScanInstallerComponentsByValues (match por path do install).
2. `AddValidatedNative` (novo): revalida os resultados do native scan com o MESMO
   predicado do walker C# (name >= 70 OU KeyHasValueReferencing guardado) - o native
   retorna so os paths, entao a chave e reaberta e re-checada. Aplicado nos 3 call
   sites (mode 0 em ScanHiveForNames, mode 1 em ScanSoftwareRecursive depth 0, mode 2
   em ScanHiveByValues).

Build: 0 erros / 122 warnings (baseline).

### Sessao 09/08 (cont.) - Desinstalador "nivel Revo": central de config + undo persistente + modo caca

Pedido do usuario: "melhore tudo da lista" (comparativo Revo x Kit). Implementado:

1. **Central de config** (`DeepUninstallSettings.cs`, Core, novo) + janela
   `DeepUninstallSettingsWindow.xaml(.cs)` (botao "Config" na toolbar da aba
   Programas, antes de "Atualizar Lista"):
   - Persistencia: `HKCU\Software\KitLugia\DeepUninstall` (DWORDs), cache com
     lazy load, setter grava imediato. Defaults: SendToRecycleBin=true,
     KillProcesses=true, DisableScan=false, SelectLeftovers=true, IgnoreRecent24H=false.

2. **DelToBin** (Revo `DelToBin`, deletar p/ Lixeira): PerformCleanup agora gated
   por `SendToRecycleBin` - delecao de arquivos/pastas via RecycleManager quando
   ON (log `[to Recycle Bin]`), permanente quando OFF (`[permanent]`). O fluxo do
   review (BtnReviewDeleteFiles/Reg) passa por PerformCleanup -> respeita o toggle.

3. **StopRunExe** (Revo): KillProcessesWithTree no RunUninstallPhaseAsync agora
   gated por `KillProcessesBeforeUninstall`.

4. **Filtro "ignorar < 24h"** (Revo): ScanLeftoverFiles - itens com
   LastAccessTimeUtc > now-24h sao pulados quando `IgnoreRecent24H` (apos as
   exclusoes do usuario).

5. **Sel. residuos por padrao** (Revo "Select leftovers by default"): BuildFileItems/
   BuildRegistryItems desmarcam TUDO quando `SelectLeftoversByDefault=false`.

6. **Nao escanear apos desinstalar** (Revo "Disable scan after uninstall"):
   StartBackgroundScan pula o scan -> RemoveProgramAfterSuccessfulScan + toast.

7. **Undo persistente pos-reboot** (backups agora em `%LOCALAPPDATA%\KitLugia` =
   `DeepUninstallSettings.PersistentRoot`, antes %TEMP% - arquivos, .reg, logs e
   historico sobrevivem ao reboot):
   - `UninstallHistory.cs` (Core, novo): `UninstallHistoryEntry` (Id, Timestamp,
     AppName, FilesDeleted, RegistryDeleted, FilesBackedUp "orig|backup",
     RegistryBackups, DeletionLogFile) + `UninstallHistory` (Load/Save com
     WriteIndented, Record cap 50, Find, Remove - Remove apaga backups em disco).
   - `PerformCleanup` grava `UninstallHistory.Record` no fim.
   - Janela `UninstallHistoryWindow.xaml(.cs)` (botao "Historico"): lista com
     `HistoryItemViewModel.Summary`; Restaurar (RestoreFileBackup + RestoreRegistryBackup),
     Excluir (UninstallHistory.Remove), Atualizar. WPF Owner = Window.GetWindow(this).

8. **Modo caca estilo Revo** (`HunterOverlayWindow.xaml(.cs)`, novo; botao
   "Hunter" abre o overlay em vez do HunterWindow):
   - Janela transparente (AllowsTransparency, Topmost, Cursor=None) cobrindo a
     tela virtual (VirtualScreen*) ; contorno tracejado dourado da janela sob o
     cursor (WindowBorder) + placa de nome (NamePlate, mede com UpdateLayout antes
     de posicionar).
   - Mira circular dourada: anel 72px (RingShadow) + circulo 64px (TargetCircle)
     + ponto central + pontas N/E/S/W + hastes (HairN/S/E/W) - matematica simples:
     pontas sobre o circulo (raio 32), hastes para fora.
   - Timer 30ms: WindowFromPoint -> GetAncestor(GA_ROOT) -> GetWindowRect;
     exclusao do proprio HWND/pid; `Activate()` no Loaded para Esc funcionar de
     cara (modo caca e modal).
   - Click: ContextMenu no cursor com: Desinstalar (Deep Uninstall via
     FindUninstallerEntry - HKLM Uninstall + WOW6432Node, createRestorePoint:false,
     DeepCleanupDialog + PerformCleanup), Matar processo, Matar + Deletar pasta,
     Abrir pasta (explorer /select), Propriedades (abre o HunterWindow legado),
     Copiar caminho, Fechar mira (Esc).

**Quirks aprendidos (KitLugia.GUI tem usings globais com System.Windows.Forms)**:
- MessageBox (ambig. WinForms) -> alias; MenuItem/Clipboard/ContextMenu -> aliases
  System.Windows.Controls.*; MouseEventArgs totalmente qualificado ao mesclar.
- `RestoreFileBackup(string backupPath, string originalPath)` e void - contagem
  de sucesso via try/catch no chamador.
- Backups de arquivo "orig|backup" (pipa) e .reg - UninstallHistory.Remove apaga ambos.

Build (09/08): Core 0 erros / 18 warnings; GUI 0 erros / 104 warnings (baseline).
App reaberto com --tray. A testar: janela Config (toggles salvam), Historico
(restaurar/excluir), overlay caca (Esc fecha, menu de acoes), review com
SelectLeftovers=false desmarcado.

### Pendencias abertas (revisadas 09/08, desinstalador)
- [ ] Testar no app: Config (5 toggles), Historico (restore pos-reboot), overlay caca
- [ ] (opcional) Scan incremental/SQLite e wizard forçado guiado (itens restantes da lista Revo)
- [ ] (opcional) Permitir "Desinstalar" (nao so "Deep") no menu do overlay quando for app do Windows

### Sessao 09/08 (cont.) - Hunter overlay: mecanismo de busca refeito (sem timer, segurar + arrastar)

**Sintoma (host)**: a mira ficou linda, mas o mecanismo de busca PISCAVA a tela
loucamente. Causa raiz: o overlay usava DispatcherTimer de 30ms + WindowFromPoint -
WindowFromPoint respeita o alpha por pixel de janelas LAYERED (AllowsTransparency):
com o cursor sobre os pixels OPACOS da mira dourada retornava o PROPRIO overlay
(hide), um pixel depois retornava a janela real (show) -> contorno alternava a cada
tick = piscada. NamePlate.UpdateLayout() por tick forçava layout da janela de tela
cheia inteira. O metodo antigo (HunterWindow) caçava segurando o botao e usava
WindowFromPhysicalPoint como fallback - funcionava.

**Correcao (HunterOverlayWindow.xaml.cs reescrito, mecanismo novo)**:
1. SEM TIMER: rastreamento so em eventos de mouse. Idle: MouseMove so reposiciona a
   mira (PlaceCrosshairAtCursor). Cacar = SEGURAR botao esquerdo e arrastar:
   MouseLeftButtonDown captura o mouse (CaptureMouse) e atualiza; MouseMove durante
   o drag atualiza o alvo; MouseLeftButtonUp solta -> abre o menu de acoes se ha alvo.
   Botao direito ou Esc fecha (cancela).
2. Hit-test por Z-ORDER via EnumWindows (primeira janela visivel cujo rect contem o
   ponto, excluindo o pid do kit) + EnumChildWindows para o menor filho sob o ponto
   (contorno preciso em janelas compostas) - independente de alpha, sem flicker.
3. Placa de nome: texto + medida (Measure, sem UpdateLayout) SO quando o alvo muda
   (_infoDirty); NamePlate MaxWidth=380 no XAML. Contorno so muda quando o alvo muda
   (SetLeft/Width por movimento e barato).
4. XAML: Canvas com MouseMove/MouseLeftButtonDown/MouseLeftButtonUp/
   MouseRightButtonDown (antes MouseLeftButtonDown+Right chamavam Canvas_Click).

Build (09/08): 0 erros / 104 warnings (baseline). App reaberto com --tray.
A testar (host): abrir Hunter -> segurar LMB + arrastar sobre janelas (sem piscar),
soltar = menu; Esc/right fecha.

### Sessao 09/08 (cont.) - "KitLugia Spy" ORIGEM ENCONTRADA: WinSpy++ (C puro) + overlay alinhado ao port

Pedido do usuario: achar o codigo antigo do hunter ("KitLugia Spy") que era feito em
outra linguagem e virou um port para C#.

**ORIGEM**: `C:\Users\Lugia\Downloads\winspy-1.8.4\winspy-1.8.4\` = **WinSpy++ 1.8.4**
(J Brown, 2002, C puro com Win32 API, projeto VS2010) + binario em
`C:\Users\Lugia\Downloads\WinSpy_Release_x64\winspy.exe`. A peca-chave e
`src\WindowFromPointEx.c` (154 linhas) - um WindowFromPoint MELHORADO:
1. `WindowFromPoint(pt)` acha a janela bruta.
2. Sobe um nivel (`GetParent`; se top-level/popup usa a propria).
3. `EnumChildWindows` + `FindBestChildProc`: escolhe o MENOR retangulo visivel que
   contem o ponto (resolve group-box/checkbox - o API nativo nao pega controles
   aninhados no mesmo nivel).
4. Fallback: se nada, usa o parent; com fShowHidden=false sobe GetParent ate visivel.

**PORT C# no Kit** (`KitLugia.GUI\Windows\HunterWindow.xaml.cs:697 WindowFromPointEx`)
e fiel ao C com melhorias: exclui _selfPid (`GetWindowThreadProcessId`), fallback
`WindowFromPhysicalPoint`, `GetAncestor(GA_ROOT)`, walk `GW_HWNDPREV` para o melhor
top-level visivel, e restringe o best-child a filhos DIRETOS do parent
(`GetParent(hwnd) != parent` -> skip) - os NETOS nunca viram alvo.

**ALINHAMENTO (HunterOverlayWindow.xaml.cs)**: o overlay novo (EnumWindows z-order)
estava divergente - o `HitTestChildProc` aceitava QUALQUER descendente (netos).
Alinhado ao port:
1. Novo `[DllImport] GetParent` + campo estatico `_hitParent`.
2. `HitTestChildProc`: `if (GetParent(hwnd) != _hitParent) return true;` - so filhos
   diretos do top-level concorrem (contorno estavel, spy e overlay agora concordam
   na MESMA coordenada - antes "Propriedades" abria o HunterWindow mostrando outro HWND).
3. `_hitParent = _hitTop` setado antes do `EnumChildWindows` em HitTestAt.

Diff dos 3 mecanismos (C original x port x overlay):
- Janela base: WindowFromPoint -> WindowFromPoint(Physical) -> WindowFromPointEx
  (ATUALIZADO 09/08: overlay chamou o MESMO WindowFromPointEx do port em vez de
  EnumWindows z-order - spy e overlay concordam por construcao, mesma funcao).
- Walk p/ janela mais alta: - -> GW_HWNDPREV -> GW_HWNDPREV (UpdateTargetAt).
- Best-fit filho: todos descendentes -> SO filhos diretos -> SO filhos diretos (ALINHADO).
- Exclusao do pid do kit: - -> sim (top+filho) -> sim (top; filho redundante - filhos
  pertencem ao mesmo processo do top).
- Fallback p/ parent: sim -> sim -> sim.

Build (09/08): 0 erros / 122 warnings (baseline). A testar (host): Hunter overlay ->
segurar LMB + arrastar sobre janelas compositas (ex: Painel de Controle/regedit) ->
contorno deve ficar no controle direto (igual do spy window, sem netos).

### Sessao 09/08 (cont.) - Hunter overlay: deteccao fiel ao C original + menu antigo de volta

Pedido do usuario: "a deteccao continua meio frouxa veja novamente os kits antigos"
e depois "segure a mira e solte com o mouse (clique unico esta ruim) pode trazer o
menu antigo do kit de volta mas mantendo essa mira aparecendo".

1. **Analise do original**: comparado com `WindowFromPointEx.c` (WinSpy++ 1.8.4),
   o port C# tinha 2 divergencias que deixavam o alvo "frouxo":
   - Walk `GW_HWNDPREV` + `GetAncestor(GA_ROOT)`: subia para janelas ACIMA na ordem
     Z (ex: tooltip/popup) e trocava o alvo pela janela que COBRE o controle. O C
     original NAO faz esse walk - usa exatamente o que WindowFromPoint retornou.
   - Filtro "so filhos diretos" (`GetParent(hwnd) != parent`): o original enumera
     TODOS os descendentes (EnumChildWindows e recursivo) e escolhe o MENOR visivel
     sob o ponto - inclusive netos (checkbox dentro de group-box). O filtro
     reintroduzia exatamente o bug do group-box que o WinSpy++ foi criado para
     resolver (mencionado no proprio comentario do C, L60-67).
   - **Correcao**: `WindowFromPointEx` reescrito nos 2 arquivos (overlay +
     HunterWindow.xaml.cs:697) fiel ao C: WindowFromPoint -> fallback
     WindowFromPhysicalPoint -> GetParent sobe 1 nivel -> EnumChildWindows recursivo
     escolhendo menor area visivel. Mantidas so as adaptacoes necessarias: exclusao
     do proprio PID + fallback fisico. P/Invokes orfaos (EnumWindows, GetWindow,
     GetAncestor, SetWindowLong, GetAsyncKeyState, IsWindow) removidos dos 2.
   - Tambem corrigido bug de placa: `plateY` usava `rc.Top - Left` (misturava X com Y).

2. **Mecanica "segurar + soltar"** (substitui o clique unico/timer):
   - REMOVIDOS: DispatcherTimer 30ms, GetAsyncKeyState, WS_EX_TRANSPARENT (nao e
     mais preciso - o rastreamento e por eventos de mouse direto no overlay).
   - NOVO fluxo: MouseLeftButtonDown grava pressX/pressY/pressTick + UpdateTargetAt.
     MouseMove durante _hunting: PlaceCrosshair + UpdateTargetAt (mira e alvo seguem
     o cursor). MouseLeftButtonUp: se mover < 8px E segurou < 300ms -> CLIQUE SIMPLES,
     NAO abre menu (so reposiciona a mira - o usuario reclamou que clique unico abre
     o menu na hora sem chance de mirar); caso contrario abre o menu no cursor.
   - Botao direito: fecha o menu se aberto, senao fecha a mira. Esc: mesma logica.

3. **Menu antigo de volta** (sem destruir a mira - o antigo DestroyOverlay ao abrir):
   - Removido o ContextMenu WPF; novo painel `ActionMenuPanel` no XAML do overlay
     (estilo do menu legado do kit: fundo #1E1E1E, titulo dourado #FFD700,
     subtitulo cinza, botoes: Desinstalar (vermelho #C42B1C), Matar, Matar+Deletar,
     Abrir Pasta, Propriedades, Copiar caminho, Cancelar).
   - Botao centralizado perto do cursor com clamp na tela virtual (como o antigo).
   - `MnuAction_Click` roteia por Tag (uninstall/kill/killdel/openfolder/props/copy/
     cancel) para os mesmos handlers existentes (DeepUninstallAsync, KillProcess...).
   - A mira + contorno + placa de nome continuam visiveis APOS abrir o menu (o pedido
     "mantendo essa mira aparecendo").

Build (09/08): 0 erros / 104 warnings (baseline). A testar (host): segurar LMB +
arrastar sobre janelas compositas -> contorno no controle menor sob o cursor (netos
incluidos); clique simples NORMAL (sem arrastar) nao abre o menu; soltar apos
arrastar abre o menu antigo com a mira ainda visivel.

### Sessao 09/08 (cont.) - Hunter overlay: menu WinForms COPIADO do Hunter antigo (fiel)

Pedido do usuario: "continua ruim faça o seguinte VÁ REALMENTE ATÉ O KIT ANTIGO
COPIE O MENU INTEIRO DO HUNTER ANTIGO E REAPLIQUE A UI ai depois pensamos em mudar".

**Substituicao total do painel XAML pelo ShowContextMenu WinForms do HunterWindow**:
1. `HunterOverlayWindow.xaml` (agora SO mira/contorno/placa - ZERO menu XAML):
   - REMOVIDOS: recurso `DarkButtonStyle`, `ActionMenuPanel`, `MnuTitle`, `MnuSub`,
     `MnuBtn*` (7 botoes), handlers `MnuAction_Click`. XAML voltou ao estado
     enxuto (Canvas + ellipses da mira + WindowBorder + NamePlate).
2. `HunterOverlayWindow.xaml.cs` (linhas ~321-470): `ShowContextMenu(int x, int y)`
   portado LINHA A LINHA do `HunterWindow.ShowContextMenu` (L536-673):
   - Form WinForms SEM borda, TopMost, Opacity 0.85, BackColor #1E1E1E; borda
     desenhada no Paint (#444); titulo dourado #FFD700 bold + subtitulo #AAA
     (nome do processo ou TruncatePath); separador; botoes [Desinstalar #C42B1C,
     Matar, Matar + Deletar, Abrir Pasta] (Flat, 30px, 36px de stride) + Cancelar
     #222/gray (24px). Clamp no WorkingArea da tela do ponto (bw=220, centraliza
     no clique). `Deactivate` e clique DIREITO no form fecham o menu.
   - Adaptacoes: acoes agora chamam os handlers DO OVERLAY (DeepUninstallAsync,
     KillProcess(false/true), OpenFolder) em vez dos Btn*_Click do HunterWindow;
     `DestroyContextMenu()` SO fecha/dispose o form (nao destroi overlay/mira);
     botao acao -> DestroyContextMenu + acao (mira continua viva - pedido do
     usuario "mantendo essa mira aparecendo").
   - Esc/direito no overlay agora checam `_contextForm != null` (antes
     ActionMenuPanel.Visibility); `OpenMenu`/`HideMenu`/`MnuAction_Click`
     DELETADOS; `OpenSpy`/`CopyPath` deletados (dead code - props/copy nao
     existem no menu antigo).
   - Helper `TruncatePath(path)` copiado (25 chars + "..." + 30 chars).

Build (09/08): 0 erros / 122 warnings (baseline). A testar (host): soltar a mira
-> menu WinForms EXATO do hunter antigo (titulo dourado, subtitulo, 4 botoes +
cancelar) com a mira ainda visivel; Deactivate/direito/Esc fecham; botoes
Desinstalar/Matar/Matar+Deletar/Abrir Pasta executam sobre a janela alvo.

### Sessao 09/08 (fim) - REVERTIDO: botao verde abre o "KitLugia Spy" (HunterWindow), overlay deletado

Pedido do usuario: "quando clica no botao verde e para abrir o mini menu do kit
que e uma janela com o nome kitlugia spy... eu ainda estou vendo a mira amarela
problematica e zero menus" - referencia: copia antiga funcional em
"KitLugia-master - Copia - Copia - Copia (16)".

**Diagnostico**: a copia antiga NAO tem HunterOverlayWindow. O botao verde
(AppsPage, `#2E7D32` "Hunter") abria `new HunterOverlayWindow()` (mira amarela)
em vez de `new HunterWindow()` (janela "KitLugia Spy" com menus). Os XAMLs do
HunterWindow sao identicos entre as copias (0 diffs) - a UI do Spy nunca foi o
problema.

**Correcoes**:
1. `AppsPage.xaml.cs` BtnHunterMode_Click -> `new HunterWindow()` (era
   HunterOverlayWindow) - identico ao kit antigo (16).
2. `HunterOverlayWindow.xaml` + `.xaml.cs` DELETADOS (o overlay inteiro era
   desnecessario; o kit antigo nao tem).

Build: 0 erros / 122 warnings (baseline). A testar: botao verde caça (Hunter)
-> janela "KitLugia Spy" abre com menus funcionais (Detectar janela, Deep
Uninstall, Matar, Abrir pasta, contexto WinForms ao segurar LMB).

### Sessao 10/08 - Core 0 warnings + auditoria manual (agents) + 3 bugs reais corrigidos

1. **18 warnings do KitLugia.Core -> 0** (limpeza completa):
   - SYSLIB0057 DeepUninstaller.cs:1721: X509Certificate.CreateFromSignedFile ->
     X509CertificateLoader.LoadCertificateFromFile (Authenticode do ProbeFolderBinaries)
   - CS8600 EmergencyBcdBootManager.cs:95: espDrive -> string? (MountEspAsync ja era nullable)
   - CS8604 Guardian.cs:2884 + LocalInstallManager.cs:60,132:
     Path.GetPathRoot(...) ?? "C:\" (Path.Combine com null)
   - CS8603 (5x) NativeBlake3.cs: HashFile/HashBytes -> string? (caller NativeSha256 ja nullable)
   - CS8604 StartupManager.cs:2055,2151: args ?? "" no CreateShortcut
   - CS8600/8602 StartupManager.cs:2190-91: dynamic? + null-check antes do
     shell.CreateShortcut (Activator.CreateInstance pode retornar null)
   - SYSLIB0014 WinbootManager.cs:2039: WebClient -> HttpClient streaming async
     (download do .NET Runtime offline, progress % removido - cosmético)
   - CS8600 WinbootManager.cs:6416: wimFile -> string?
   - CA2022 WinpeBuilder.cs:268: fs.ReadAsync -> fs.ReadExactlyAsync (sig WIM)
   - CS0414 WinpeBuilder.cs:921: WINXSHELL_CACHE agora usado no candidates list
     (substituiu o literal "C:\KL_WINPE\WinXShell.exe")
   Builds: Core = 0 avisos / 0 erros; GUI = 0 erros / 104 avisos (baseline proprio).

2. **Auditoria manual com agentes** (104 avisos GUI = so ruido nullable: CS8600x42,
   CS8618x24, CS8602x20, CS8625x14, CS0414x2, CS0067x2). Achei 3 bugs REAIS, todos corrigidos:

   **BUG A (ALTO)**: NativeSha256.ComputeHash retornava BLAKE3 quando rust_native.dll
   estava presente (delegava para NativeBlake3.HashFile) -> GitHubUpdater.cs:245 comparava
   com o SHA256 do zip -> auto-update SEMPRE falhava "Hash mismatch" com a DLL.
   Correcao: branch NativeBlake3 removido (fica so sha256_file_ffi + managed SHA256).
   NativeSha256.cs:29-34.

   **BUG B (MEDIO)**: GetProcessTreeSnapshot (DeepUninstaller.cs:5061) fazia
   using var p = proc e DEPOIS list.Add((proc,...)) - o mesmo objeto disposed ->
   IsProcessTreeActive (5032) engolia ObjectDisposedException -> stall detection do
   uninstaller NUNCA disparava. Correcao: snapshot agora captura tuplas de dados
   (pid, cpu, sample, startTime, sessionId) com dispose correto; leitor reabre por
   Process.GetProcessById(pid) com catch (processo pode ter saido).

   **BUG C (BAIXO)**: StutterDetector.SampleTopProcess (327) vazava handles: o LINQ
   Process.GetProcesses().Where().OrderBy().Take(3) criava centenas de Process e so os
   3 do top3 eram disposed. Correcao: foreach + dispose dos nao-keep + dispose dos
   escolhidos fora do top3 antes de reassignar.

3. **WinXShell: URL de download MORTA removida** (pesquisa): o asset
   https://github.com/luigiarrud4/KitLugia-WinPE/releases/download/v1.0/WinXShell.exe
   NAO existe (404 verificado; releases so tem WinPE-base.7z e VALOS-base.7z).
   WinpeBuilder.ResolveWinXShellAsync: bloco de download removido (const WINXSHELL_URL
   deletada) - agora loga orientacao de colocar o exe em KitLugia.WinPE\WinXShell\
   (as copias locais 3,5 MB continuam e sao encontradas pelos candidates).

4. **WinXShell SOURCE: o binario atual e FECHADO** (pesquisa web): o WinXShell.exe
   que o kit injeta e o RC5.x do slore (DuiLib + Lua embutido, nunca publicado).
   Open-source so o shell Win32 classico: github.com/slorelee/PExplorer branch
   WinXShell_shellpart (LGPL-2.1, C/C++ puro, MSVC VS2012/2015 - VS Community 2026
   instalado no host). Arquitetura: jcfg JSON + WinXShell.lua + wxsUI components.
   Decisao do usuario: AINDA NAO DECIDIDO (opcoes: shell C# proprio no WinPE - VALOS
   ja tem .NET/WPF; fork PExplorer LGPL; ou so configurar jcfg/lua do binario).

5. Integracao mapeada: ResolveWinXShellAsync (WinpeBuilder.cs:926, candidates locais
   + cache), InjectWinXShellIntoWimAsync (winpeBuilder.cs:986, wimlib add para
   /Windows/System32/WinXShell.exe), launch no bridge startnet.cmd
   (WinbootManager.cs:5620-5625: if exist C:\Windows\System32\WinXShell.exe).

### Proxima sessao
- [ ] Testar o binario atual do WinXShell na VM (TESTAR mode do WinpeToolsPage)
- [ ] Decidir abordagem do "modificar o codigo fonte": shell C# proprio vs fork PExplorer vs config
- [ ] (se C#) esquematizar o shell: taskbar, icones, temas, integracao com o shrink/fresh install
### Sessao 10/08 (cont.) - VALOS EXCLUIDO + botao TESTAR SHELL (WinXShell) no WinpeToolsPage

Pedido do usuario: "exclua o validation OS e coloque o WinXShell no kit no botao testar".
Validado em docs: WinPE em modo RAMDISK roda winpeshl.exe -> winpeshl.ini ausente -->
startnet.cmd; explorer.exe NAO existe no WinPE por padrao; o correto e
`WinXShell.exe -winpe` (flag que cria o Desktop e corrige USERPROFILE).

**1. VALOS REMOVIDO por completo (Core + WinPE) - era "horrivel e nao funciona"**:
- WinbootManager.cs: 603 linhas deletadas (regiao VALIDATION OS: PrepareValidationOs,
  RemoveValidationOs, ValidationOsStartnetCmd + IsValidationOsReady). Removidos
  tambem useValOs branches do ScheduleWinpeShrink (assinatura agora
  ScheduleWinpeShrink(drive, shrinkMB) - caller do ShrinkPage da WinPE ja passava
  2 args; GUI WinpeToolsPage atualizado para 2 args; config sempre OS_TYPE=winpe;
  scriptName sempre startnet.cmd).
- WinpeBuilder.cs: ConfigureValosShellAsync (registro Winlogon Shell + Setup\CmdLine)
  e InjectDiskpartIntoWimAsync (so o VALOS usava) deletados.
- KitLugia.WinPE: ToolsPage (card Validation OS + 3 handlers), DashboardPage
  (ValOsBanner + isValOs), WinPEDetector.IsValOS deletados; textos "WinPE/ValOS"
  corrigidos. Builds: Core 0 erros/0 avisos, WinPE 0/0, GUI 0/104 (baseline).

**2. Botao TESTAR SHELL na WinpeToolsPage (card 1, entre PREPARAR e REMOVER)**:
- `ScheduleTestWinpeShell()` (WinbootManager.cs:5704): resolve WIM
  (fallback recursivo + auto-prepare, padrao do shrink) -> ResolveWinXShellAsync
  (local: KitLugia.WinPE\WinXShell\WinXShell.exe, cache C:\KL_WINPE, ao lado do
  exe; download removido - URL 404) -> InjectWinXShellIntoWimAsync (wimlib add em
  /Windows/System32/WinXShell.exe) -> UpdateWimWithScriptAsync(TestShellStartnetCmd)
  -> CreateRamdiskEntry(fixedGuid: TestShellBcdGuid {9f7c8d2e-...}) + /bootsequence
  (one-time, nao polui o menu; fallback displayorder+timeout) -> reboot 10s.
- `TestShellStartnetCmd()`: scan por KL_SHRINK_TARGET.dat primeiro (shrink agendado
  roda antes, copia exata do RamdiskStartnetCmd); sem marcador -> `start "" ...WinXShell.exe -winpe`
  (cd /d C:\Windows\System32) - shell grafico do WinPE; erro+reboot se exe ausente.
- Regras cmd.exe mantidas: sem parenteses em echo de blocos, ASCII puro.

**A TESTAR (VM)**: WinpeToolsPage -> TESTAR SHELL -> reboot -> WinPE com Desktop
WinXShell; depois SEM marcador (shell direto); depois com SCHEDULE pendente (shrink
primeiro). WinXShell.exe local confirmado: KitLugia.WinPE\WinXShell\ (3,5 MB).

### Sessao 10/08 (cont.) - TESTAR SHELL: 2 bugs corrigidos (X: RAMDISK + card esticado)

Teste real (VM) do TESTAR SHELL: WinXShell NAO iniciou (script ficou no scan do
marcador e depois morreu) e o card 1 da WinpeToolsPage ficou com o fundo esticado.

**BUG 1 (script, WinbootManager.cs TestShellStartnetCmd)**: o launcher checava
`if exist C:\Windows\System32\WinXShell.exe` - SEMPRE FALSO: o WinPE RAMDISK monta
o sistema em X:\ (C: so existe no VALOS legado). Alem disso, o echo com
"(WinPE shell mode)" dentro de bloco if...else violava a regra de parse do cmd
(bloco so e parseado quando a condicao e TRUE; parens balanceados ainda quebram).
Corrigido: resolver o drive do sistema em tempo de execucao e SEM blocos:
- `set OSDRV=X` + `if exist C:\Windows\System32\WinXShell.exe set OSDRV=C`
- `if exist !OSDRV!:\Windows\System32\WinXShell.exe goto :launch` + label
  `:launch` com `cd /d` + `start "" !OSDRV!:...WinXShell.exe -winpe` (labels
  depois de exit/b nunca executam por fallthrough; wpeutil reboot ganhou
  `exit /b 1` de seguranca apos si).
- Scan do marcador ganhou progresso: `echo   Probing disk %%d - partitions 1-8...`
  + aviso "This scans up to 4 disks x 8 partitions and can take a minute..."
  (antes o scan de 32 diskpart ficava MUDO ~30-60s e parecia travado).

**BUG 2 (card, WinpeToolsPage.xaml)**: StackPanel horizontal com 4 botoes
(PREPARAR/TESTAR/REMOVER/LIMPAR BCD) estourava a largura minima do card ->
o Border StepCardStyle esticava (fundo preto grande). Corrigido: WrapPanel
Horizontal com MaxWidth=440 + Margin inferior 4 nos botoes - quebram linha
quando falta espaco, o card nunca estica.

**LICAO**: no WinPE RAMDISK os arquivos injetados no WIM (wimlib add
/Windows/System32/X.exe) ficam em X:\Windows\System32 - nunca C:\. O VALOS
usava C: porque bootava com um Windows real instalado. O bridge startnet.cmd
antigo (X: e C:) estava certo.

Script validado localmente (host, versao sanitizada diskpart->echo, subst X:):
scan 4x8 roda sem crash, "No shrink scheduled. Launching WinXShell shell...",
OSDRV=X, goto :launch, `start "" ... -winpe`, exit 0. Builds: Core 0/0,
GUI 0/104 (baseline).

**A TESTAR (VM)**: TESTAR SHELL de novo -> WinPE mostra progresso do scan ->
~1min -> "Launching WinXShell shell..." -> Desktop WinXShell aparece
(se o Windows Boot Manager reclamar, escolher a entrada "Shell Test" no menu).

### Sessao 11/08 - WinXShell REMOVIDO: Explorer++ e o unico shell do WinPE

Pedido do usuario: "o winxshell ele não funciona ta pode jogar ele fora".

**Removido por completo (Core + GUI + WinPE + docs)**:
1. KitLugia.Core\WinpeBuilder.cs: ResolveWinXShellAsync deletado; InjectWinXShellIntoWimAsync
   → InjectExplorerPlusPlusIntoWimAsync (so Explorer++.exe → /Windows/System32/Explorer++.exe,
   um comando wimlib, log se ausente); FindExplorerPlusPlus sem parametro (raiz do kit,
   Explorer++\, Resources\App\Explorer++\, path dev KitLugia.WinPE\Explorer++\).
2. KitLugia.Core\WinbootManager.cs: TestShellStartnetCmd sem fallback WinXShell — so
   Explorer++ (OSDRV detect, assign D-K, launch); removed :assign_drives_wx/:launch_wx;
   ScheduleTestWinpeShell usa o metodo novo, BCD entry "Shell Test (Explorer++)".
3. KitLugia.GUI: csproj sem Content do WinXShell.exe; tooltip + dialogs atualizados.
4. KitLugia.WinPE: csproj Content agora Explorer++\*; MainWindow botao "Iniciar Explorer++"
   (search System32 + Explorer++\ subpasta); DashboardPage card Explorer++; mojibake dos
   emojis/acentos corrigidos (foram 2x-encoded).
5. Arquivos apagados: WinXShell\WinXShell.exe (3,4 MB), WinXShell_x86.exe (3 MB),
   WinXShell.jcfg, download_winxshell.ps1. Pasta WinXShell\ renomeada → KitLugia.WinPE\Explorer++\
   (Explorer++.exe 6,2 MB + History/License/Readme).
6. docs/REVIEW-PENDING.md: itens 1/10/linha resolvidos atualizados.

**Bom saber**: Environment.SystemDirectory no WinPE RAMDISK = X:\Windows\System32 (o
arquivo injetado vive la). Preciso disso no MainWindow do KitLugia.WinPE (checagem + launch).

**A TESTAR (VM)**: publicar VS → copiar pasta para a VM → TESTAR SHELL → WinPE boota e
abre Explorer++ direto (com letras D-K atribuidas, sem scan 4x8).

### Sessao 11/08 (cont.) - KitLugia toolkit DENTRO do WinPE (substitui o cmd.exe do Test Mode)

Pedido do usuario: "criar algo legal que rode bem dentro do winpe" + "o kit extraido
(publicado) abro na VM... injeta". Fluxo real: WinPE com Explorer++ ja funciona; o que
faltava era o CMD do WinPE nao ser um shell grafico - abria um cmd.exe cru.

1. **InjectWinpeToolkitIntoWimAsync (WinpeBuilder.cs, novo)**: injeta o publish
   self-contained do KitLugia.WinPE (app WPF + runtime .NET 10 ~203MB) no boot.wim em
   /Windows/System32/KitLugia/ via wimlib. Candidatos para o publish (todos relativos):
   (a) BaseDir\KitLugia\KitLugia.WinPE.exe (pasta publicada do GUI), (b) Resources\App\
   WinpeToolkit\, (c) caminho dev ..\..\..\KitLugia.WinPE\bin\Release\net10.0-windows10.0.26100.0\
   win-x64\publish\. Filtra BootGoodies\ e LinuxPreOS\ (nao cabem no WinPE; ~50MB a menos).
   Staging em %TEMP% (reclamado OK - nao e hardcoded de recurso). Durante o wimlib add,
   o WinPE RAMDISK ganha scratch X: ampliado (DISM /ScratchDir em X: + set 1024MB via
   DISM /Set-ScratchSpace) para aquecer o imagex com 200MB+ e o add nao estourar o X:.

2. **KitLugia.GUI.csproj**: Content glob do publish com Link KitLugia\ (copia o toolkit
   self-contained para a pasta publicada do GUI) + target EnsureWinpeToolkitPublish que
   roda `dotnet publish KitLugia.WinPE -c Release -r win-x64 --self-contained true` se
   o publish nao existir (build nao falha sem ele - so copia o que houver).

3. **TestShellStartnetCmd reescrito (WinbootManager.cs)**: agora o fluxo real:
   - Scan do marcador KL_SHRINK_TARGET.dat igual ao RamdiskStartnetCmd (marker-only,
     DISK/PART do proprio diskpart) - se achou, vai :run e roda o shrink COMPLETO
     (mesmo fluxo do SCHEDULE, log persistente + remove marcador) e depois vai :shell.
   - Sem marcador: direto :shell -> assign_drives (letras D-K nos discos 0-1, manda
     diskpart real) -> :launch:
     `start "" !OSDRV!:\Windows\System32\Explorer++.exe`
     + `if exist !OSDRV!:\Windows\System32\KitLugia\KitLugia.WinPE.exe` -> tambem lanca
     o kit WPF (a GUI do WinPE com Dashboard/Tools/etc). OSDRV resolve X: vs C: em runtime.
   - Regras cmd.exe preservadas: echos de bloco SEM parenteses, ASCII puro, exit /b no fim.

4. **Validacao local** (host, sem VM): harness reflection dump_shell (dump TestShellStartnetCmd)
   + simulacao com subst X: e Explorers fakes: scan 4x8 intacto, "Assigning drive letters...",
   launch do Explorer++ e do kit dispararam, "Shell ready" + exit 0, ZERO "was unexpected
   at this time". AST do script: 119 linhas, ASCII puro, echos de bloco limpos.
   Staging real: 203MB / 268 arquivos (sem BootGoodies/LinuxPreOS). Output do GUI ja tem
   a pasta KitLugia\ (250MB publish completo). Builds: Core 0/0, GUI 0 erros.

**A TESTAR (VM)**: publicar VS (pasta publicada agora tem KitLugia\ com o toolkit) >
copiar para a VM > TESTAR SHELL > WinPE boota > shoot scan 4x8 (~1 min, com progresso) >
:launch > Explorer++ abre + KitLugia.WinPE.exe (WPF) abre como GUI do WinPE. Com marcador
pendente: shrink roda primeiro (Status: OK no log) e o shell abre depois.

### Sessao 11/08 (cont.) - BUG: toolkit WPF abria INVISIVEL (janela nunca criada) + crash-log

**Sintoma (VM, 17:35)**: TESTAR SHELL funcionou de ponta a ponta - WinPE bootou, scan
4x8, letras D-K, Explorer++ ABRIU, "Starting KitLugia toolkit..." - mas nenhuma janela
do KitLugia.WinPE.exe apareceu ("so o explorer++ ligo").

**CAUSA RAIZ (App.xaml.cs/App.xaml)**: o App.xaml NAO tem `StartupUri` e NAO existe
Program.cs - o Main gerado pelo WPF roda `app.Run()` SEM POSSIBILIDADE DE ABRIR JANELA
(o WPF so cria a janela a partir de StartupUri). O processo subia e ficava vivo em
background, invisivel, exatamente como observado. Nenhum c�digo criava `new MainWindow()`.

**CORRECOES (KitLugia.WinPE)**:
1. `App.xaml.cs` OnStartup: agora cria e mostra `new MainWindow()` explicitamente
   (try/catch com log de falha).
2. `CrashLog.cs` (novo): grava qualquer excecao (AppDomain/Dispatcher/Task +
   "info" de inicializacao) em `%TEMP%\KitLugiaWinPE_crash.log` (diagnostico no proprio
   WinPE via Explorer++) E na raiz de TODOS os volumes C..Z (`C:\KitLugiaWinPE_crash.log`
   etc) - o RAMDISK X: some no reboot, mas a raiz do disco do Windows sobrevive: o
   usuario ve o log depois de voltar ao Windows. Sobrecarga Write(stage, string).
3. Build Release OK + `dotnet publish -r win-x64 --self-contained` FORCADO (o target
   EnsureWinpeToolkitPublish so roda se o exe nao existir no publish - publicacao
   manual atualizou o publish de 250MB).

**FLUXO DE TESTE (VM)**: republicar pelo VS (o glob Content copia KitLugia\ nova p/
publicado) > copiar > TESTAR SHELL > o kit WPF deve abrir como GUI do WinPE (Dashboard
+ FileExplorer + Partitions + Shrink + InstallWindows + Tools = COMPLEMENTO do shell
Explorer++). Se ainda nao abrir: ler C:\KitLugiaWinPE_crash.log (raiz do disco) - o
crash-log diz exatamente onde morreu.

### Sessao 11/08 (fim) - CANCELADO: kit DENTRO do WinPE NAO existe mais (framework-dependent de volta)

Pedido do usuario: "cancele a ideia de colocar o kit dentro do winpe faca o programa
a voltar a ser dependente de instalar o .net nao quero autocontained".

**Reverso completo da ideia (nem o KitLugia.WinPE toolkit nem o KitLugia.GUI common
entram no WIM)**:

1. `KitLugia.Core\WinpeBuilder.cs`: `FindWinpeToolkitPublish` + `InjectWinpeToolkitIntoWimAsync`
   (DISM mount/scratch FBWF 1024MB/wimlib fallback) + `CopyDirectoryFiltered` DELETADOS
   (dead code). `FindExplorerPlusPlus`/`InjectExplorerPlusPlusIntoWimAsync` intocados
   (Explorer++ continua sendo o shell do WinPE no TESTAR SHELL).
2. `KitLugia.Core\WinbootManager.cs`: `TestShellStartnetCmd` de volta a Explorer++ ONLY
   (bloco `if exist ...\KitLugia\KitLugia.GUI.exe` removido; o script nao lança mais nada
   alem do Explorer++.exe); `ScheduleTestWinpeShell` sem a chamada `InjectWinpeToolkitIntoWimAsync`;
   texto final "WinPE bootara com Explorer++ como file manager".
3. `KitLugia.GUI\Pages\WinpeToolsPage.xaml.cs`: overlay de confirmacao do TESTAR SHELL
   com 4 passos (Explorer++ + startnet + bootsequence) e aviso "sem kit interno".
4. `KitLugia.GUI\KitLugia.GUI.csproj`: SEM glob de publish self-contained e SEM target
   `EnsureWinpeToolkitPublish` (ficou so o Content do Explorer++.exe na raiz do kit).
5. Perfis de publish do VS conferidos: FolderProfile (default) e FolderProfile3 (win-x64)
   = `SelfContained=false` (framework-dependent); FolderProfile1 self-contained win-x86 e
   perfil antigo nao usado. O kit publica dependente do .NET instalado, como sempre foi.

**O que fica** (validado pelo usuario antes do cancelamento): Explorer++ (6MB) como file
manager do WinPE no TESTAR SHELL, com shrink marker-only pendente rodando antes do shell.
KitLugia.WinPE projeto (App fix + CrashLog) continua no repo sem uso/injecao - pode ser
deletado se desejado.

Build: Core 0 erros / 0 avisos; GUI 0 erros. Sem self-contained em lugar nenhum.

### Sessao 11/08 (fim 2) - KitLugia.WinPE DELETADO do repo (projeto era lixo de IA antiga)

Pedido do usuario: o projeto KitLugia.WinPE foi criado por uma IA antiga ("delirio"),
ele nunca mexeu nele. Delecao completa:

1. **Pasta `KitLugia.WinPE\` removida do repo** (projeto WPF inteiro: App/MainWindow/
   Pages/Dashboard/Tools + CrashLog.cs + WinXShell ja deletados antes).
2. **Explorer++ PRESERVADO**: movido para `KitLugia.GUI\Resources\App\Explorer++\`
   (Explorer++.exe 6,2MB + History/License/Readme) - o glob `<Content Include="Resources\**\*">`
   do GUI copia para `output\Resources\App\Explorer++\` na publicacao.
3. `WinpeBuilder.FindExplorerPlusPlus`: candidatos KitLugia.WinPE\ removidos; dev path
   agora `..\..\..\..\KitLugia.GUI\Resources\App\Explorer++\`; mensagem de log atualizada.
4. `KitLugia.GUI.csproj`: Content Include explicito do Explorer++.exe (Link na raiz)
   REMOVIDO - o glob Resources\**\* cobre (arquivo em Resources\App\Explorer++\ no output).
5. `KitLugia.sln`: projeto KitLugia.WinPE removido (linha Project + 12 linhas de
   ProjectConfigurationPlatforms do GUID {4CA25971-...}).

Build solucao (Debug): 0 erros. Output do GUI validado: Resources\App\Explorer++\Explorer++.exe presente.

### Sessao 12/08 - ISO Editor "so nativas": DISM 100% removido (wimlib + pnputil + $WinPEDriver$)

Pedido do usuario: "aproveite uso somente coisas nativas para evitar usar as ferramentas
ruins da microsoft que sao lentas" (pesquisa web incluida). O ISO Editor NAO usa mais DISM
em nenhum fluxo:

1. **AppX bloat sem DISM** (IsoEditorManager.RemoveProvisionedAppsNoMountAsync):
   - wimlib ls "wim" idx "Program Files/WindowsApps/" lista as pastas de pacote
     (parse: `<num>\t<path>`, nome = ultimo segmento; filtra nomes com '.')
   - wimlib update --command-file deleta as pastas que casam prefixo_ (StartsWith)
   - Hive SOFTWARE offline: extract via wimlib -> reg load -> delete AppxAllUserStore\
     Applications\<fullname> + Application\<fullname> -> dd Deprovisioned\<fullname>
     (marcador MS Learn que impede re-provisionamento em feature updates) -> unload ->
     re-inject via wimlib update. Espelha 1:1 o Remove-AppxProvisionedPackage
     (CleanupPackageFromPerMachineStore do AppxAllUserStore).
2. **Drivers sem DISM**: export com pnputil /export-driver * "dir" (nativo, instantaneo)
   + copia para $WinPEDriver$ na raiz da midia - o Setup.exe do WinPE varre recursivamente
   os .inf e injeta no driverstore do OS instalado (metodo documentado MS Learn). SEM
   tocar no boot.wim.
3. **WinSxS**: wimlib optimize (reconstroi o WIM e remove espaco desperdicado dos updates)
   - DISM /StartComponentCleanup /ResetBase era inutil em midia nova (nao ha WinSxS\Backup).
4. **Scheduled tasks**: wimlib update delete de Windows/System32/Tasks/... (no-mount).
5. **Modo profundo DISM DELETADO** (IsoEditorPage): sem MountWim/UnmountWim/
   ApplyRegistryTweaks/InjectBootWimDrivers/DeleteScheduledTaskFiles (versoes de montagem);
   fluxo UNICO no-mount com todas as opcoes (bloat, drivers, WinSxS, tasks, registry,
   SetupComplete, ConX fix); UpdateModeHint = "Modo: NATIVO (wimlib + registro offline,
   sem montar)".
6. **Core limpo**: IsoEditorManager perdeu ~700 linhas de dead code DISM (MountWim/
   UnmountWim/InjectDrivers/GetProvisionedApps/RemoveProvisionedApps/GetWindowsFeatures/
   EnableFeature/DisableFeature/CleanupWinSxS/GetCapabilities/RemoveCapabilities/
   GetPackages/RemovePackages/GetLanguages/RemoveLanguages/ExportToESD + parsers +
   data models ProvisionedAppInfo/WindowsFeatureInfo). CleanupIsoEdit sem mountDir.

Build: 0 erros / 108 avisos (baseline nullable GUI). docs/ISO_EDITOR_WIMLIB_PLAN.md
atualizado com a secao "Sessao 12/08".

**A TESTAR (VM/host)**: fluxo completo com bloat+drivers+WinSxS marcados (antes exigia
montagem) - conferir no log: "wimlib update delete" das pastas, "Deprovisioned",
"pnputil", "copiados para $WinPEDriver$", "WIM otimizado"; ISO final bootavel.
### Sessao 12/08 (cont.) - BUGS do fluxo nativo CORRIGIDOS (teste real 13/08): 4 bugs de sintaxe wimlib

Teste real (host, ISO 25H2): ISO criada com sucesso (7 GB, bootavel), mas registry/appx/tasks
FALHARAM em silencio. Causa: a versao do wimlib embutida NAO suporta --command-file nem aceita
destdir no extract (e o comando de listagem e dir, nao ls). Todos reproduzidos num WIM de
teste descartavel e corrigidos (IsoEditorManager.cs):

1. **--command-file= NAO existe** (usage: [--command=STRING] [< CMDFILE]): os 3 metodos
   (InjectFilesIntoWimAsync, RemoveProvisionedAppsNoMountAsync, DeleteScheduledTaskFilesNoMountAsync)
   passaram a usar **stdin redirect** via novo RunProcessCapturedWithStdin(filename, args, stdinContent)
   (RedirectStandardInput; leitura assincrona de stdout/stderr para evitar deadlock de pipe).
2. **extract WIM IMAGE PATH DEST NAO aceita destdir** (usage: extract WIMFILE IMAGE [(PATH | @LISTFILE)...]
   com --dest-dir=CMD_DIR): destdir inexistente vira PATH PATTERN -> erro 49 "No matches".
   Corrigido nos 2 metodos (ApplyRegistryEditsNoMountAsync + RemoveProvisionedAppsNoMountAsync):
   extract "wim" idx "Windows/System32/config/software" --dest-dir="tmpDir". Nome do arquivo
   local = ULTIMO SEGMENTO do path interno (ex: "system", "ntuser.dat") - antes era
   hive.ToLowerInvariant() ("ntuser" vs arquivo "ntuser.dat").
3. **ls NAO existe -> dir**: ListWindowsAppsFoldersAsync usava wimlib ls (exit != 0 ->
   lista SEMPRE vazia -> bloat nunca removia). Agora dir "wim" idx --path="Program Files/WindowsApps/"
   e o filtro mantem SO filhos DIRETOS (1 nivel abaixo do prefixo, sem backslash extra) -
   elimina o bug 
ame.Contains('.') que descartava todas as pastas de pacote (ex:
   Clipchamp.Clipchamp_4.4.10720.0_neutral_split... tem '.').
4. **Delete de PASTAS exige --recursive** no update (erro 32 "directory but a recursive delete
   was not requested"): todos os updates de delete ganharam --recursive (pastas de pacote e
   pastas de tasks tipo WindowsUpdate).
5. Formato do command file: **aspas SAO delimitadores** (paths com espaco SEM aspas quebram o
   parse: "Unexpected argument Files/WindowsApps/..."); paths internos do WIM podem usar / ou \.

VALIDADO no WIM real (25H2, 6 GB, somente leitura): dir lista pacotes corretamente; extract do
hive SOFTWARE -> 76,8 MB, exit 0. Build: 0 erros / 108 avisos (baseline).

Sobre o Grok (13/08): acertou que --command-file nao existe (item 3 do checklist dele); o resto
do argumento dele ("nao da para remover AppX sem DISM") NAO se aplica ao metodo usado: o
Remove-AppxProvisionedPackage faz por baixo exatamente o que o kit faz (CleanupPackageFromPerMachineStore
= deletar pasta WindowsApps + remover entrada Applications + criar marcador Deprovisioned, MS Learn).
DISM so adiciona o registro em CBS/componentes, irrelevante para instalacao limpa. Drivers via
$WinPEDriver$ e metodo MS oficial (o proprio Grok cita). O "Titus camada 3" (registro offline +
delete de pastas + scripts FirstLogon) e exatamente a arquitetura do editor.

**A TESTAR (host)**: rodar o fluxo completo de novo (bloat + tasks + registry marcados) e conferir
no log: "Removendo N pasta(s)", "Deprovisioned", "Registry tweaks aplicados sem montar",
"Deletando 10 scheduled task(s)", SetupComplete/ConX "injetado", "WIM otimizado".
### Sessao 12/08 (cont.) - BUG: "edicao 4 invalida" ao re-rodar o fluxo (pasta persistente)

Sintoma (host, 15:13): 2a execucao do fluxo nativo falhava com
"wimlib export falhou (codigo 18): ERROR: '4' is not a valid image in
...\iso_contents\sources\install.wim". O install.wim do iso_contents ja tinha
SIDO exportado na rodada anterior (1 imagem), mas o codigo tentava exportar a
edicao escolhida de novo.

Causa raiz (IsoEditorPage.xaml.cs L349): lreadySingle usava _editions.Count
(a analise da ISO ORIGINAL, sempre N edicoes) em vez de contar as imagens do
ARQUIVO REAL em iso_contents (pasta persistente, pode estar processado).

Correcao: apos localizar wimPath, o fluxo re-analisa o arquivo real com
AnalyzeWimAsync(wimPath) (rapido, ~1-2s):
1. ileImageCount = imagens do arquivo em iso_contents (nao do _editions).
2. lreadySingle = !origIsEsd && fileImageCount == 1 (skip do export).
3. Se fileImageCount == 1 e editionIndex > 1 -> loga "Usando edicao 1" e corrige.
4. Se a re-analise falhar -> erro claro + CleanupIsoEdit (antes: export erro 18).

Build: 0 erros. A re-analise roda em todo fluxo (ISO nova: N imagens -> export normal;
reuso de pasta persistente: 1 imagem -> skip + tweaks na imagem 1).

**A TESTAR (host)**: rodar o fluxo 2x seguidas na MESMA pasta de trabalho (2o run
sem export, tweaks direto na imagem unica) e com ISO nova (export normal).
### Sessao 12/08 (cont.) - Estilo Titus: ISO MONTADA + copia nativa (sem extrair com 7z)

Pedido do usuario: "no chris titus o modo que ele faz ele nem extrai a iso ele so
monta e ja vai direto" - o winutil ISO Creator usa Mount-DiskImage + Copy-Item do
drive virtual (ISO UDF e cru, 7z so adiciona overhead de parsing).

Correcoes (IsoEditorPage.xaml.cs):
1. **CopyDirectoryAsync** (conteudo da ISO): agora MONTA a ISO via
   IsoManager.MountIso(_isoPath) e copia o conteudo do drive com **robocopy
   nativo** (/E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP /MT:8, exit 0-7 = sucesso;
   robocopy ja era o fallback do WinbootManager L1719), Dismount no finally.
   7z so como FALLBACK se o mount falhar. Log: "Montando ISO e copiando conteudo
   (estilo Titus, nativo)..."
2. **ExtractInstallFileOnlyAsync** (analise): mesmo padrao - monta, copia
   sources\install.wim/esd com File.Copy nativo, dismount; 7z seletivo so como
   fallback.

Build: 0 erros. Ganho: copia de ~10GB via robocopy do drive montado e muito
mais rapida que a extracao 7z.

**A TESTAR (host)**: rodar fluxo com pasta de trabalho VAZIA - conferir no log
"ISO montada em X:\ e conteudo copiado (robocopy, codigo N)" e a ISO final OK;
rodar de novo (reuso) - so "Conteudo da ISO ja extraido (reuso)."
### Sessao 13/08 - PERFORMANCE do fluxo nativo: benchmark real + LZX default + optimize sem recompressao

Pedido do usuario: "pesquise a fundo, estamos no .net10-windows deve ter ferramentas
melhores" (no WinPE e rapido, no host o fluxo estava lento).

**Pesquisa concluida**:
- Nao existe WIM API nativa no .NET BCL nem via WinRT; WIMGAPI/DISM e o que o usuario
  quer evitar (lento). wimlib (C nativo) e a ferramenta certa - o overhead de
  Process.Start e ~10ms, irrelevante. ProcessStartInfo.ArgumentList seria cosmetico.
- Benchmark REAL no WIM do usuario (6,04 GB, edicao 1, 13 GiB de file data):
  - export --compress=lzms (default antigo): **153s** (2min33, log do usuario)
  - export --compress=lzx: **86s** (1.8x mais rapido; +560 MB = 6605 vs 6040 MB)
  - wimlib docs confirmam: optimize SEM --compress REUSA dados comprimidos (so remove
    holes de appends/deletes); --compress=TYPE implica --recompress (WIM inteiro do
    zero = minutos). --threads default = autodetect (processadores).

**Correcoes (3)**:
1. **ComboCompression default = LZX** (IsoEditorPage.xaml: SelectedIndex="1", texto
   "LZX (balanceado, rapido - recomendado)"). LZMS fica como opcao (maxima reducao).
2. **OptimizeWimAsync SEM --compress=lzms** (IsoEditorManager.cs): reconstrucao
   estrutural (segundos) em vez de recompressao completa (~2.5min). A compressao ja
   foi escolhida no export - recompressao e redundante. Comentario no metodo explica.
3. **Timing por etapa** (IsoEditorPage.xaml.cs): Stopwatch do fluxo nativo
   (flowSw) - mensagens do export/optimize anexam "(Ns de fluxo nativo)" e o fim
   loga "Tempo total do fluxo nativo: Ns." - feedback real de onde o tempo vai.

**Fluxo total estimado**: ~5min -> ~2min (robocopy + export LZX 86s + tweaks
rapidos + optimize segundos + oscdimg 11s).

**A TESTAR (host)**: fluxo completo com ChkCleanupWinSxS + strip marcados - log deve
mostrar "WIM otimizado ... (Ns)" com N pequeno, "Tempo total do fluxo nativo" e a
ISO final bootavel.
### Sessao 13/08 (cont.) - Tasks: 25H2 NAO inclui mais as tasks de telemetria no WIM (fix delete)

Sintoma (host, 15:44): fluxo rapido (LZX + optimize sem recompressao), mas aviso amarelo:
"wimlib update delete de tasks falhou (codigo 49): Path
\Windows\System32\Tasks\Microsoft\Windows\Application Experience\Microsoft Compatibility
Appraiser does not exist in WIM image 1".

Causa raiz (PROVADO via dir no WIM real 25H2): Tasks\Microsoft\Windows so tem 6 pastas
(DeviceLicensingService, PLA, RemoteApp and Desktop Connections Update, SyncCenter,
TaskScheduler, WCM) - a Microsoft REMOVEU do WIM as tasks de telemetria (Application
Experience/Compatibility Appraiser, CEIP, Chkdsk Proxy, WER, InstallService,
UpdateOrchestrator, UpdateAssistant, WaaSMedic, WindowsUpdate). O wimlib update ABORTA
no 1o path ausente (codigo 49) e NADA era deletado -> aviso.

Correcao (IsoEditorManager.DeleteScheduledTaskFilesNoMountAsync): mesmo padrao do bloat -
lista antes com dir "wim" idx --path="/Windows/System32/Tasks" (1 chamada barata),
filtra os targets que EXISTEM (igual ou prefixo com /), deleta so os existentes; se
nenhum existe retorna sucesso "esta versao ja nao as inclui". Log: "Deletando N
scheduled task(s) existente(s) (M ja ausentes nesta versao)...".

Build: 0 erros. Na pratica: 25H2 = nada a deletar (mensagem informativa, sem aviso).

**A TESTAR (host)**: re-rodar o fluxo - o bloco de tasks deve logar "Deletando 0..." ou
"Nenhuma das 10 task(s) alvo existe no WIM" SEM aviso amarelo; ISO final ok.

### Sessao 13/08 (cont.) - ISO Editor: UI no estilo kit novo + bloat 42 (Titus) + PID.txt + cancelamento

Pedido do usuario: "pode melhorar tudo que for possivel ai apos você terminar quero que
ajeite a escala da ui e os botões para ficarem no mesmo estilo do kit mais novo e tambem
pegar a tela de loading nova que mostra informações em tempo real da pagina do winpepage
shrinkpage... no winboot a tela de seleção esta um pouco melhor que a nossa de agora no
kit iso editor".

**UI (IsoEditorPage.xaml)**:
1. **OverlayBusy reescrito no padrao WinpeToolsPage/UpdatePage** (antes: barra
   indeterminada + 1 texto): Border #1A1A1A CornerRadius 12 Width 540 borda #FFD700 +
   DropShadowEffect; titulo dourado (TxtOpTitle); **barra de progresso REAL** (ProgressFill
   #FFD700 + TxtProgressPercent); TxtProgressStep (passo, dourado) + TxtProgressStatus
   (status curto, wrap); **log detalhado scrollavel** (TxtOpDesc Consolas, inlines coloridos
   erro/ok/info) com "Copiar log" (TxtCopyOpLog_MouseDown -> Clipboard); warning de acao
   critica; botao CANCELAR (cancela entre etapas).
2. **OverlayConfig no estilo kit novo** (referencia WinbootPage): Background
   CardBackground + BorderBrush AccentColor + CornerRadius 15 + DropShadowEffect
   (antes #1E1E1E borda dourada 2px); botao CANCELAR -> SecondaryButtonStyle; botao
   INICIAR AGORA -> GoldButtonStyle (Height 38); botoes Selecionar/Desmarcar Todas ->
   SecondaryButtonStyle (antes Background inline).
3. **Footer**: BtnCleanup/BtnBack/BtnCreate todos com Height 38 + estilos padrao
   (Secondary/Gold).
4. **Textos desatualizados corrigidos**: "CUSTOMIZACAO PROFUNDA (DISM MOUNT - LENTO
   20-40min)" -> "CUSTOMIZACAO (wimlib + pnputil - SEM MONTAR)" (verde); ChkCleanupWinSxS
   "Limpar WinSxS com /ResetBase" -> "Otimizar WIM (wimlib optimize)"; ChkDebloatPreset
   "Remove 20+" -> "Remove 40+"; descricao dos drivers ($WinPEDriver$).

**Core (IsoEditorPage.xaml.cs)**:
5. **Bloat expandido para 42 prefixos** (estilo Chris Titus/winutil 2026): adicionados
   Microsoft.Copilot, Microsoft.549981C3F5F10 (Cortana), Microsoft.MicrosoftTeams,
   Microsoft.People, Microsoft.WindowsCommunicationsApps, Microsoft.Getstarted,
   Microsoft.WindowsAlarms, Microsoft.WindowsCalculator, Microsoft.WindowsCamera,
   Microsoft.WindowsClock, Microsoft.WindowsMaps, Microsoft.WindowsPhotos,
   Microsoft.WindowsScan, Microsoft.ScreenSketch, Microsoft.MixedReality.Portal,
   Microsoft.Wallet, Microsoft.PPIProjection, Microsoft.Windows.Phone (Phone Link),
   Microsoft.YourPhone, Microsoft.XboxApp, Microsoft.XboxGamingOverlay,
   Microsoft.XboxIdentityProvider, Microsoft.XboxSpeechToTextOverlay. (Fora: Edge,
   Notepad, Terminal, OneDriveSync - uso comum.)
6. **PID.txt stale removido** (com ChkDisableSponsoredApps): midia modificada + PID.txt
   original pode dar erro de PID no setup (Titus deleta).
7. **Progresso por etapa mapeado** (SetBusyStatus(status, pct, label)): analise 3-6,
   copiar conteudo 8, export 18, registry 35, bloat 45, tasks 50, drivers 55,
   SetupComplete 60, ConX 63, optimize 70, KitLugia 75, ISO final 85, sucesso 100
   (overlay fica aberto atras do MessageBox de sucesso mostrando a barra cheia).
8. **Cancelamento entre etapas** (_cts + CheckCancelled em 6 pontos: apos copiar,
   registry, bloat, tasks, drivers): CANCELAR para o proximo checkpoint, loga
   "Operacao cancelada pelo usuario" e fecha o overlay.
9. **AddLog alimenta o overlay** (AddOpLog com cores erro/ok/info + scroll automatico).
10. Helpers novos: ShowBusy(title) (zera overlay), SetBusyStatus, CheckCancelled,
    AddOpLog, ScrollOverlayToBottom, IsErrorText, BtnCancelOp_Click,
    TxtCopyOpLog_MouseDown (System.Windows.Clipboard qualificado - ambig. WinForms).
    Usings: + System.Windows.Documents, System.Threading.
11. **Análise SEM extração** (feedback do usuário: "por que extrair primeiro o
    install.wim?"): novo AnalyzeIsoMountedAsync - monta a ISO e roda wimlib info
    direto no sources\install.wim/esd do drive virtual (estilo Titus de verdade,
    sem copiar 7GB); usado no BtnAnalyzeIso_Click e na análise automática do
    BtnConfirmStart_Click; 7z seletivo fica só como fallback (mount falhou).
    Antes: ~17s de extração 7z; agora: mount + wimlib info + dismount (~2-3s).

Build: 0 erros / 108 avisos (baseline nullable GUI).

**A TESTAR (host)**: rodar fluxo completo - overlay com barra real + passos + log em
tempo real; CANCELAR no meio -> para apos a etapa; "Removendo 42 AppX provisionados";
"PID.txt removido"; OverlayConfig no novo estilo; ISO final bootavel.

### Sessao 13/08 (cont.) - CAUSA RAIZ do fallback 7z: drive com barra dupla (E\:\)

Sintoma (host, 19:33): "Análise rápida - montando ISO e lendo sources\install.* direto
do drive (estilo Titus, sem extrair)..." e logo "Fallback: extraindo apenas sources\
install.* da ISO com 7z..." - o mount SEMPRE caia no fallback, mas o robocopy/fluxo
completo funcionava (ou o reuso de pasta mascara o mount).

Causa raiz: IsoManager.MountIso retorna DriveLetter como \"E:\\\" (letra + dois pontos +
barra). Os 3 consumidores faziam drive.TrimEnd(':') -> \"E\\\" -> montavam
$"E\:\" (barra INVERTIDA antes dos dois pontos = caminho invalido): File.Exists
sempre falso, robocopy com source invalido (caia no fallback 7z silenciosamente).
O mount em si SEMPRE funcionou - o path derivado e que era lixo.

Correcao (IsoEditorPage.xaml.cs, 3 pontos): drive.TrimEnd('\\', ':') + ":\\" -
remove barra e dois pontos e garante \"E:\" (aceita \"E\", \"E:\" ou \"E:\\\").
Locais: AnalyzeIsoMountedAsync (~L154), ExtractInstallFileOnlyAsync (~L200),
CopyDirectoryAsync (~L910).

Build: 0 erros / 108 avisos (baseline).

**A TESTAR (host)**: rodar fluxo com pasta de trabalho VAZIA - log deve mostrar
\"Lendo E:\sources\install.wim direto do drive montado...\" (analise em ~2-3s sem 7z)
e \"ISO montada em E:\ e conteúdo copiado (robocopy...)\" (copia nativa, sem 7z);
fluxo completo ~2min.

### Sessao 13/08 (cont.) - Otimizacoes finais: hives em paralelo + skip optimize em reuso + rodela Win11

**TESTADO pelo usuario (19:42)**: log real \"Lendo E:\sources\install.wim direto do
drive montado...\" - analise em ~2-3s SEM 7z; fluxo completo 12s; ISO criada.
Usuario: \"velocidade esta otima\". Log 19:16 mostrou 324 arquivos na ISO (vs 1048
antes) - nao investigado.

1. **Rodela Win11 no OverlayBusy** (IsoEditorPage.xaml): 5 ellipses #FFD700 7x7 com
   TranslateTransform (circulo raio 16), Grid 44x44 com RotateTransform + Storyboard
   Loaded 0->360 1.4s Forever (template do AppsPage). Posicao final apos 3 ajustes:
   **linha propria do Grid** (RowDefinitions Auto/Auto; conteudo na Row 0, rodela na
   Row 1 com HorizontalAlignment Right + VerticalAlignment Center, Margin 0,10,14,0) -
   nunca sobrepoe o texto vermelho nem o botao CANCELAR.

2. **Registry hives EM PARALELO** (IsoEditorManager.ApplyRegistryEditsNoMountAsync):
   foreach sequencial substituido por funcao local ProcessHiveAsync + Task.WhenAll
   (1 Task por hive: SOFTWARE/SYSTEM/DEFAULT/NTUSER). Seguro: hives diferentes usam
   chaves HKLM\z{...} e arquivos locais diferentes; listas reInject/applied protegidas
   com reInjectLock/appliedLock; re-injecao unica no final (InjectFilesIntoWimAsync).
   CUIDADO CS0136: listas declaradas 2x (fora e dentro do try) quebraram o build -
   so declarar dentro do try. Ganho ~7s -> ~2-3s.

3. **Skip do optimize em reuso** (IsoEditorPage.xaml.cs): wimSizeBeforeTweaks capturado
   apos o export (baseline do WIM cru); no bloco do ChkCleanupWinSxS, se o tamanho
   atual == baseline (reuso puro: tweaks sem re-inject/bloat inexistente/tasks ja
   ausentes), pula com log \"WIM inalterado nesta rodada (reuso) - optimize desnecessario,
   pulando.\" - evita reconstrucao estrutural inutil em rodadas repetidas.

Build: 0 erros / 108 avisos (baseline).

**A TESTAR (host)**: rodar fluxo 2x seguidas - 2a rodada com log do skip do optimize
(e \"Registry tweaks aplicados sem montar (SOFTWARE, SYSTEM...)\" mais rapido, hives
paralelos); rodada 1 completa inalterada.


### Sessao 13/08 (fim) - BOOT DIRETO NO INSTALADOR: botao na WinpeToolsPage (testar ISOs sem particoes)

Pedido do usuario: "se da para dar boot no winpe da para dar boot direto no arquivo de install
do windows?" -> resposta: install.wim NAO e bootavel; boot.wim index 2 (Setup) e. Implementado
botao BOOT INSTALADOR no card 1 da WinpeToolsPage (verde, entre TESTAR SHELL e REMOVER):
o PC reinicia direto no Windows Setup da ISO selecionada - sem criar particoes nem abrir o
WinPE comum (util para testar ISOs).

**Pesquisa confirmada**: boot.wim sozinho nao basta ("A required CD/DVD drive device is missing" -
os arquivos do instalador NAO estao dentro do boot.wim); o setup.exe varre TODOS os volumes
procurando `\Sources\install.wim` na RAIZ de cada drive (winsetup.dll GetLogicalDriveStringsW);
metodo emacsos/winutil: copiar Sources p/ disco + ei.cfg. Solucao usada: /installfrom (doc MS).

**Core (WinbootManager.cs)**:
1. Const nova `InstallerBcdGuid = "{5b7d9f1e-3c4a-4e6b-8d2f-9a1c2b3d4e5f}"` (ao lado de
   TestShellBcdGuid L5608) - GUID fixo, entrada unica, nao acumula no menu.
2. `ScheduleBootInstallerAsync(string isoPath)` (novo, depois de ScheduleTestWinpeShell):
   1. Monta a ISO (IsoEditorManager.MountIso - estilo Titus) e copia `Sources\` INTEIRO para
      C:\KL_WINPE\InstallISO\Sources\ via robocopy /E /MT:8 (o WinPE e outro ambiente: a
      montagem do host nao persiste no boot). boot.sdi: o da ISO; fallback C:\KL_WINPE\boot.sdi.
      Dismount no finally.
   2. `AnalyzeWimAsync(sources\boot.wim)` exige >= 2 imagens; `ExportSingleEditionAsync(idx 2,
      compress=lzx)` -> C:\KL_WINPE\installer_boot.wim UNICO (elimina ambiguidade de indice
      no boot ramdisk: o bootmgr nao tem indice no BCD; com 1 imagem, qualquer escolha = Setup).
   3. install.wim: se a ISO so tem install.esd, renomeia para install.wim (ESD = WIM solid,
      formato detectado pelo conteudo - /installfrom aceita).
   4. winpeshl.ini custom injetado na imagem (InjectFilesIntoWimAsync idx 1, ASCII):
      `[LaunchApps]` + `%SystemDrive%\sources\setup.exe, /installfrom:C:\KL_WINPE\InstallISO\Sources\install.wim`
      (formato de args com virgula = mesmo do fix ConX /legacy do IsoEditor - comprovado).
   5. CreateRamdiskEntry(fixedGuid: InstallerBcdGuid) + bootsequence one-time + fallback
      displayorder + reboot 10s (mesmo padrao do ScheduleTestWinpeShell).

**GUI (WinpeToolsPage)**: botao `BtnBootInstaller` (SecondaryButtonStyle verde #88FFAA/#225522,
Width 112, WrapPanel do card 1) + `BtnBootInstaller_Click` (OpenFileDialog .iso -> ShowBusy com
5 passos + aviso "Requer ~10 GB livres em C:" -> Task.Run ScheduleBootInstallerAsync -> ShowBusyResult).

Build: 0 erros / 0 avisos (incremental Core+GUI).

**A TESTAR (VM)**: WinpeToolsPage -> BOOT INSTALADOR -> selecionar ISO -> log: monta, robocopy
Sources, export idx 2 lzx, winpeshl.ini /installfrom injetado, bootsequence -> reboot 10s ->
Windows Setup abre direto em modo grafico (sem pedir midia). Se pedir midia: verificar
install.wim em C:\KL_WINPE\InstallISO\Sources\ e o winpeshl.ini injetado no installer_boot.wim.

### Sessao 14/08 - BUG 0xc0000487 no BOOT INSTALADOR: causa raiz = WIM exportado sem flag bootable

**Sintoma (teste real, ISO 25H2)**: fluxo inteiro OK no log (robocopy code 1, export 2s,
injeção 1s, BCD {5b7d9f1e-...} + bootsequence code 0) mas o boot falhava rápido com:
`Arquivo: \windows\system32\boot\winload.efi / Status: 0xc0000487` ("arquivo necessário
ausente ou com erros").

**Causa raiz (confirmada por pesquisa web)**: o mesmo erro EXATO aparece no issue
microsoft/Windows-Containers#494 quando se boota um WinPE recapturado/exportado SEM o
flag bootable. A doc WDS confirma: "A RAMDISK boot image must be... explicitly marked as
being able to boot from RAMDISK (/boot option in ImageX)". O `ExportSingleEditionAsync`
rodava `wimlib export` SEM `--boot` -> o WIM destino ficava com BootIndex=0 no header
(offset 0x34) -> bootmgr não descobre a imagem ao montar o ramdisk -> winload.efi "ausente".

**CORRECAO (2 arquivos)**:
1. `IsoEditorManager.ExportSingleEditionAsync` ganhou param `bool markBootable = false`:
   quando true, o comando vira `export "src" N "dst" --compress=X --boot` (o wimlib
   bundled suporta --boot: "Mark the exported image as the bootable image of the WIM").
   Chamadores existentes (ISO Editor) não mudam (default false).
2. `WinbootManager.ScheduleBootInstallerAsync` passa `markBootable: true` + verificação
   de header pós-export: lê 4 bytes em 0x34 do installer_boot.wim e loga
   "Verificacao WIM exportado: N imagem(ens), BootIndex=X (com --boot deve ser 1)" -
   evidência direta no log do próximo teste (sem precisar inspecionar o arquivo).

Build: 0 erros / 108 avisos (baseline). Obs: C:\KL_WINPE\installer_boot.wim e
C:\KL_WINPE\InstallISO\ não existiam mais no host na hora do diagnóstico (limpos) -
a verificação de header embutida resolve isso nas próximas rodadas.

**A TESTAR (VM)**: BOOT INSTALADOR de novo -> log deve mostrar "BootIndex=1" na
verificação -> reboot -> Setup da ISO abre direto (sem 0xc0000487 e sem pedir midia).

### Sessao 14/08 (cont.) - BUG 2 do BOOT INSTALADOR: setup.exe nao achava install.wim (letra de drive)

**Sintoma (teste real do usuario)**: com o 0xc0000487 resolvido, o WinPE bootava e o
Setup da ISO ABRIA, mas com erro: "O Windows não pôde coletar informações de [OSImage]
já que o arquivo de imagem especificado [C:\KL_WINPE\InstallISO\Sources\install.wim]
não existe" (splash "A instalação está sendo iniciada" visível).

**Causa raiz**: o winpeshl.ini injetado hardcodava `C:\KL_WINPE\InstallISO\Sources\install.wim`,
mas as letras de drive do WinPE NAO sao as mesmas do host (lição antiga do kit: shrink e
fresh install usam scan de drives justamente por isso) - o drive com o KL_WINPE pode ser
D:, E:, etc. no ambiente do Setup.

**CORRECAO (2 arquivos, winpeshl.ini -> startnet.cmd com scan de drives)**:
1. `IsoEditorManager.InstallSetupStartnetAsync` (novo): instala um startnet.cmd na imagem
   (index) via wimlib update com stdin e REMOVE o winpeshl.ini existente se a imagem tiver
   (dir check por substring "winpeshl.ini" antes) - o winpeshl.exe so executa o startnet.cmd
   quando NAO ha winpeshl.ini, e a imagem de Setup da midia tem um proprio.
2. `WinbootManager.ScheduleBootInstallerAsync` (passo 4): gera startnet.cmd ASCII que
   varre as letras `for %%d in (Z Y W V U T R Q P O N M L K J I H G F E D C)` procurando
   `%%d:\KL_WINPE\InstallISO\Sources\install.wim` (fallback install.esd -> ISOFILE) e
   lanca `start "" "%SystemDrive%\sources\setup.exe" /installfrom:%ISODRV%:\KL_WINPE\InstallISO\Sources\%ISOFILE%`
   com log persistente `%ISODRV%:\KL_WINPE\installer_boot_log.txt`; sem encontrar -> log
   em %SystemDrive% e setup sem /installfrom.

**QUIRK cmd.exe descoberto na validacao (falso alarme)**: `if exist "%%d:\..."` dentro de
bloco for NAO falha com lista grande - o teste inicial usava lista C..S e subst em Z:, a
lista simplesmente nao continha Z (o script estava certo). SEM delayed expansion necessario:
o bloco `if defined ISODRV` e parseado DEPOIS do for, entao `%ISODRV%` ja tem o valor final
(parse-time expansion ok). Nao usar `setlocal EnableDelayedExpansion` em scripts com
`if exist "%%d:\..."` - testado quebra o match (T1/T2/T3/diag5).

**VALIDADO localmente (host, subst Z: + fakes)**: scan acha Z:, log
"install.wim encontrado em Z: - iniciando Setup" em Z:\KL_WINPE\installer_boot_log.txt,
setup chamado com /installfrom:Z:\...\install.wim (marcador TESTE_OK), else branch correto
com lista sem o drive. Build: 0 erros / 108 avisos (baseline). Lixo do teste removido.

**A TESTAR (VM)**: BOOT INSTALADOR -> log do host com "BootIndex=1" -> reboot -> o
startnet.cmd varre as letras, escreve installer_boot_log.txt na raiz do drive com o
KL_WINPE e o Setup abre com o install.wim localizado (sem erro de OSImage). Se o drive
ganhar outra letra, o scan cobre (Z..C).

### Sessao 14/08 (cont. 2) - BUG 3 do BOOT INSTALADOR: Setup sem discos = faltava wpeinit

Sintoma (teste real do usuario): com o 0xc0000487 e o OSImage resolvidos, o Setup ABRIA
(startnet.cmd rodou, /installfrom funcionou) mas mostrava "Instalar driver para mostrar o
hardware" / "Um driver de midia necessario para o computador esta ausente" com tabela vazia
- ZERO discos (VMware: Virtual SAS 64GB + Virtual NVMe 130GB).

Causa raiz (PROVADA com o boot.wim real da ISO 25H2 no host):
1. O startnet.cmd ORIGINAL da midia 25H2 (extraido de sources\boot.wim idx 2) e
   literalmente `wpeinit` (1 linha). O InstallSetupStartnetAsync o SOBRESCREVEU com o
   script de scan - removendo a inicializacao do PnP.
2. Sem wpeinit: o PnP/DeviceInstaller nunca roda, stornvme/lsi_sas nao carregam -> o
   Setup (um exe normal) abre mas nao enxerga NENHUM disco.
3. Os outros 3 geradores JA chamavam wpeinit (RamdiskStartnetCmd L5080, TestShellStartnetCmd
   L5627, RamdiskReinstallPreserveStartnetCmd L6578) - o startnet.cmd do boot instalador
   era o UNICO sem.

Verificacoes no host (ISO Win11_25H2_BrazilianPortuguese_x64_v2 (2).iso, 7,61 GB):
- wimlib info: imagem 2 = "Microsoft Windows Setup (amd64)", Boot Index 2.
- Export --compress=lzx --boot (args identicos ao fluxo): EXIT 0, Boot Index 1, 1 imagem,
  610 MB, 21248 arquivos / 5470 dirs.
- wimlib dir idx 2 original vs idx 1 exportado: winpeshl/startnet IDENTICOS (a midia NAO
  tem winpeshl.ini - so winpeshl.exe + startnet.cmd); DriverStore 1293 linhas nos DOIS
  (conteudo preservado - teoria de driverstore descartada).
- startnet.cmd original extraido = "wpeinit" (prova definitiva).
- Script do scan (com delayed expansion) re-testado no host com subst Z:: acha o
  install.wim, ISODRV=Z, log correto - o scan estava OK (nao mexido).

Correcoes (WinbootManager.cs):
1. startnet.cmd do boot instalador: `wpeinit` adicionado no topo (apos os rems, antes do
   setlocal) com comentario - sem ele o Setup nao ve nenhum disco.
2. Verificacao de header pos-export CORRIGIDA (era bogus): lia 4 bytes em 0x30/0x34 do
   header (lixo - 0x30-0x37 e o QWORD de lookup table offset; BootIndex NAO existe no
   header fixo) e logou "692500 imagem(ens), BootIndex=33554432" sem significado. Agora
   le o XML data (offset QWORD em 0x38, tamanho em 0x40) e Regex `<BOOTINDEX>N</BOOTINDEX>`:
   loga "BootIndex=N (via XML; com --boot deve ser 1)" ou aviso de BOOTINDEX ausente.
3. Doc comment do passo 4 atualizado (midia usa startnet.cmd=wpeinit, nao winpeshl.ini).

Build: 0 erros / 108 avisos (baseline).

**A TESTAR (VM)**: BOOT INSTALADOR de novo -> log "BootIndex=1 (via XML...)" -> reboot ->
wpeinit roda (~5-10s) -> Setup abre com os DOIS discos (SAS + NVMe) na tela de selecao.

### Sessao 14/08 (cont. 4) - Zero discos no Setup: BOOTINDEX era falso alarme + startnet auto-diagnostico

Sintoma (VM, 17:08): apos o fix do wpeinit, o Setup ABRE mas mostra "Instalar driver para
mostrar o hardware" / "Erro: nenhum driver foi encontrado" com ZERO discos (VMware: Virtual
SAS 64GB + Virtual NVMe 130GB). Causa raiz ainda NAO encontrada - evidencias coletadas no host:

1. **BOOTINDEX "ausente" = FALSO ALARME (provado)**: o wimlib 1.14.5 NAO grava `<BOOTINDEX>`
   no XML mesmo com `--boot` (verificado: export com --boot -> XML 692.500 bytes, sem a
   substring; `wimlib info` ainda reporta "Boot Index: 1"). O boot funciona independente.
   CORRECAO: verificacao pos-export agora usa `wimlib info` (regex "Boot Index: N") em vez
   de ler o XML (WinbootManager.cs L~5924). Core 0 erros / 0 avisos.

2. **idx 1 vs idx 2 IDENTICOS no que importa**: wpeinit.exe, winpeshl.exe e startnet.cmd
   com SHA256 identicos entre o idx 1 (WinPE generico) e o idx 2 (Setup) da midia; hive
   SYSTEM Setup identico nos 2 (CmdLine=winpeshl.exe, SetupType=0x1, SystemSetupInProgress=0x1,
   AllowStart com PlugPlay/Power/RPCSS/EventLog). Zerar SetupType NAO diferencia nada.

3. **DriverStore idx 2 preservado** no export (1293 entradas) contem stornvme/nvmedisk/
   c_nvmedisk/pvscsii/lsi_sas/arcsas/itsas35i - os drivers de storage ESTAO la.

4. **boot.sdi da midia = \boot\boot.sdi (3.170.304 B), NAO sources\** - e o codigo JA usa
   o correto (ScheduleBootInstallerAsync L~5890: isoDrive + "boot\\boot.sdi" -> installer_boot.sdi;
   fallback C:\KL_WINPE\boot.sdi). O X: monta com o boot.sdi da propria midia. NAO e o problema.

5. **startnet.cmd original da midia idx 2 = literalmente 'wpeinit'** (9 bytes); sem winpeshl.ini
   no WIM inteiro (so winpeshl.exe + .mui). Web (osdeploy): o winpeshl.exe faz "Beginning PNP
   initialization" e o startnet.cmd roda depois - o nosso startnet chama wpeinit de novo (OK).

**NOVO: startnet.cmd auto-diagnostico** (o proximo teste responde sozinho):
- `set WPEINIT_RC=%errorlevel%` logo apos o wpeinit (captura o exit code do PnP).
- Dentro do bloco `if defined ISODRV`: roda `diskpart /s` com "list disk" e grava a saida
  NO installer_boot_log.txt (responde a pergunta decisiva: o WinPE VE os discos?).
- Copia `wpeinit.log` e `winpeshl.log` para `!ISODRV!:\KL_WINPE\` (o PnP do WinPE loga
  X:\Windows\System32\wpeinit.log - se o DeviceInstaller falhou, esta la).
- Regras cmd.exe respeitadas: sem parenteses em echo de blocos, ASCII puro.

**VALIDADO localmente (host, com subst Z: + fakes)**: parse sem "inesperado"; ISODRV=Z
encontrado; WPEINIT_RC=9009 capturado (wpeinit nao existe no host - valida a captura);
diskpart list disk real logado (Disco 0 465GB / Disco 1 3726GB); setup fake lancado com
`/installfrom:Z:\KL_WINPE\InstallISO\Sources\install.wim` (TESTE_OK). Lixo do teste limpo.

Build: Core 0 erros / 0 avisos. GUI: MSB3021 (app aberto) - compilar/publicar com app fechado.

**A TESTAR (VM)**: BOOT INSTALADOR -> reboot -> Setup abre (com ou sem discos) -> reboot de
volta -> mandar os 3 artefatos do drive do KL_WINPE: installer_boot_log.txt (diskpart list
disk dentro!), wpeinit.log e winpeshl.log. Se diskpart listar os 2 discos -> problema e do
SETUP (nao do WinPE); se listar 0 -> PnP do WinPE falhou (wpeinit.log diz o motivo).


### Sessao 14/08 (cont. 5) - CAUSA RAIZ do "zero discos": shim setup.exe da raiz do WIM (analise binaria)

**Sintoma (VM 17:37)**: apos o fix do wpeinit, o Setup ABRE mas a tela de selecao de disco mostra ZERO discos. Novas evidencias do usuario: (a) BootIndex=1 confirmado via wimlib info (verificacao nova funcionou); (b) o dialogo "Procurar" do proprio Setup lista TODOS os discos com letras/arquivos -> o WinPE VE os discos; o problema e o Setup NAO enumera-los na tela; (c) Setup "pula direto pra ultima parte" (sem telas de idioma) - /installfrom provavelmente passou.

**Analise binaria da midia 25H2 (ISO montada, winpeshl.exe + wpeinit.exe + setup.exe da raiz extraidos do idx 2 via wimlib)**:

1. **winpeshl.exe** (61.440 B, strings ASCII + UTF-16): importa `WpeInitializeDriversOfClass` (WpeUtil.dll) e `CreateProcessW`; SEM winpeshl.ini ele: (a) "Beginning PNP initialization" numa thread; (b) tenta `%SystemDrive%\$Windows.~BT\sources\setup.exe`; (c) tenta **`%SystemDrive%\setup.exe`**; (d) fallback `cmd.exe /k startnet.cmd`. Tambem: `Global\EVENT_WINPE_REMSTOR`, `DisableRemovableStorageInit`, `%windir%\Setup\Scripts\disablecmdrequest.tag`, WallpaperHost.

2. **wpeinit.exe** (61.440 B): NAO lanca setup - so unattend/SMI (windowsPE pass, smiengine.dll). Descartado como launcher.

3. **idx 2 TEM `\setup.exe` NA RAIZ (333.256 B - o SHIM)** alem de `sources\setup.exe` (wimlib dir confirmou: raiz = Program Files(x86)/ProgramData/setup.exe/sources/Users/Windows). O winpeshl lanca o shim da raiz - e o shim prepara o ambiente do Setup:
   - `PrepareVolumeAccessPath`/`CreateVolumeAccessPath`/`ReleaseVolumeAccessPath` com `DefineDosDevice` (da LETRAS TEMPORARIAS a volumes sem letra; "No free drive letters to use!")
   - `\\?\PHYSICALDRIVE%d`, `GetSystemDiskNumber`, `\Device\Harddisk%d`, `Partition` (enumera discos fisicos e acha o disco do sistema)
   - `ORIGINAL_SETUP_WORKINGDIR_ENV_VAR`, mutex `Global\Microsoft.Windows.Setup` + `Microsoft.Windows.Setup.Local`, `FirstUX` (UI "Primeira UX"), `WinSetup.dll`
   - `/%s /%s:%s /%s:"%s" %s` + `durestart` + `$WINDOWS.~BT`/`BTFolderPath`/`OSImagePath`/`boot.wim`: o shim RELANCA o sources\setup.exe repassando args (formato par:valor; /installfrom e arg WDS aceito - a ajuda lista "see the Windows Deployment Services documentation")
   - Ajuda propria: /auto /quiet /installdrivers /noreboot /installlangpacks /showoobe /unattend /postoobe /copylogs /pkey /addbootmgrlast /Compact /imageindex + debug (1394debug/debug/emsport/usbdebug/netdebug/busparams)

**CONCLUSAO (causa raiz)**: na midia real o fluxo e winpeshl -> X:\setup.exe (shim) -> shim prepara ambiente (volume access paths, disco do sistema, env vars, mutex) -> relanca sources\setup.exe. O nosso startnet.cmd lancava `%SystemDrive%\sources\setup.exe` DIRETO, pulando o shim -> Setup abria CRU, sem o ambiente de enumeracao de discos -> "zero discos" na tela (apesar do WinPE ver os discos).

**CORRECAO (WinbootManager.cs, ScheduleBootInstallerAsync)**: o launch agora e o SHIM da raiz:
- `start "" "%SystemDrive%\setup.exe" /installfrom:!ISODRV!:\KL_WINPE\InstallISO\Sources\!ISOFILE!` (e no else: `start "" "%SystemDrive%\setup.exe"` sem /installfrom).
- Doc comment e Log atualizados (explicam o shim e a cadeia winpeshl->shim->sources). winpeshl.ini NUNCA existiu no idx 2 (so winpeshl.exe + .mui; startnet.cmd = 'wpeinit' 9 B confirmado de novo byte a byte 77 70 65 69 6E 69 74 0D 0A).
- Fix CS8604: verificacao do BootIndex (wimlib info) envolta em null-check do FindBundledWimlib.

**Overlay da GUI (WinpeToolsPage)**: passo 4 atualizado ("Injetar startnet.cmd que lanca o shim setup.exe da raiz (fluxo nativo da midia) com /installfrom"; passo 3 com --boot).

**VALIDADO (host)**: script regenerado com subst Z: + fakes - parse sem "inesperado", ISODRV=Z encontrado, WPEINIT_RC=9009 (wpeinit nao existe no host), comando gerado exato: `start "" "%SystemDrive%\setup.exe" /installfrom:Z:\KL_WINPE\InstallISO\Sources\install.wim` (no WinPE %SystemDrive% = X:). ISO desmontada e lixo do teste limpo. Builds: Core 0 erros / 0 avisos; GUI 0 erros (incremental Core+GUI; MSB3021 se app aberto).

**A TESTAR (VM)**: BOOT INSTALADOR de novo -> Setup deve abrir COM a tela de selecao de disco listando os 2 discos (SAS + NVMe) - o shim prepara a enumeracao. Se o shim reclamar do /installfrom: alternativa ja mapeada - copiar install.wim para !ISODRV!:\sources\install.wim (raiz de volume, padrao winutil: o setup varre a raiz de todos os volumes) e lancar o shim sem args.


### Sessao 14/08 (cont. 6) - CAUSA RAIZ REAL: winpeshl lancava o SHIM X:\setup.exe ANTES do nosso startnet.cmd

**Sintoma (VM 18:07)**: mesmo com o launch do shim corrigido, o Setup abriu de novo SEM discos e "nao gravou nada" (sem installer_boot_log.txt em C:\KL_WINPE).

**CAUSA RAIZ (deduzida do fato "nao gravou nada" + strings do winpeshl)**: o nosso startnet.cmd NUNCA rodou em NENHUM teste. O winpeshl.exe SEM winpeshl.ini tenta, nesta ordem: (1) %SystemDrive%\$Windows.~BT\sources\setup.exe (nao existe) -> (2) %SystemDrive%\setup.exe -> **EXISTE no nosso installer_boot.wim (o shim de 333 KB que exportamos junto do idx 2!)** -> lanca o shim -> (3) so se AMBOS falharem: cmd /k startnet.cmd. Ou seja: o winpeshl lancava o shim direto (Setup cru, sem /installfrom e sem o ambiente) e o nosso startnet.cmd (scan + wpeinit + diag) JAMAIS era executado - por isso nenhum log era gravado e o comportamento era identico ao teste anterior.

**CORRECAO (IsoEditorManager.InstallSetupStartnetAsync reescrito)**: em vez de REMOVER o winpeshl.ini, agora INJETA um proprio:
- `[LaunchApps]` + `%SystemRoot%\system32\cmd.exe, /k startnet.cmd` (ASCII; formato AppPath, args do winpeshl).
- Com winpeshl.ini presente, o winpeshl lanca SO o que o [LaunchApps] mandar (ignora os paths de setup) -> o nosso startnet.cmd roda -> scan -> shim com /installfrom -> ambiente completo -> discos.
- Command file via stdin: add startnet.cmd + add winpeshl.ini (o add sobrescreve se existir; a midia original NAO tem winpeshl.ini).

**VALIDADO (host)**: WIM de teste descartavel - `add winpeshl.ini /Windows/System32/winpeshl.ini` via stdin exit 0; dir confirma startnet.cmd + winpeshl.ini; extract confirma conteudo exato. Build Core: 0 erros / 0 avisos.

**A TESTAR (VM)**: BOOT INSTALADOR -> o winpeshl.ini faz o winpeshl lancar cmd /k startnet.cmd -> o startnet roda (AGORA sim): wpeinit -> scan -> "install.wim encontrado em X:" -> log em C:\KL_WINPE\installer_boot_log.txt -> lanca o shim %SystemDrive%\setup.exe com /installfrom -> Setup com a tela de disco listando SAS + NVMe. Ponto de verificacao imediato apos reboot: C:\KL_WINPE\installer_boot_log.txt DEVE existir no Windows (se nao existir, o winpeshl ainda nao esta rodando o nosso startnet).


### Sessao 15/08 - BOOT INSTALADOR VALIDADO (instalacao completa com sucesso!) + limite 0x80300001

**TESTADO NA VM com SUCESSO TOTAL (15/08 18:27-19:00)**: o winpeshl.ini resolveu de vez:
1. Log do host: fluxo completo OK (robocopy 1, export lzx --boot, BootIndex=1, startnet+winpeshl.ini injetados, BCD GUID fixo {5b7d9f1e-...}, bootsequence 0).
2. **O nosso startnet.cmd RODOU** (prova: installer_boot_log.txt gravado): "install.wim encontrado em D:", "wpeinit exit code: 0", diagnostico diskpart list disk = Disco 0 (64GB) + Disco 1 (130GB).
3. wpeinit.log: PNP completo (rede, componentes WinPE-Setup/WMI/WSH, firewall) STATUS: SUCCESS.
4. Setup ABRIU com as telas CORRETAS: idioma -> selecao de particao (todas listadas, Novo habilitado) -> instalou o Windows 11 completo no disco ao lado (64GB) -> dual boot (Windows 11 vol 5 / vol 3) -> OOBE -> desktop.

**LIMITE DESCOBERTO (nao e bug - fisica da fonte)**: no 2o teste o usuario deletou a particao de 64GB onde o KL_WINPE/install.wim vivia e o setup falhou com 0x80300001 ("Verifique a unidade de midia"). O install.wim esta NUMA PARTICAO DO DISCO; deletando-a, a fonte do /installfrom some. Na midia real (DVD/USB) isso nao acontece (o install.wim esta no CD, nao deletavel). Solucoes: (a) instalar POR CIMA (selecionar a particao com Windows antigo e Avancar - o setup limpa) SEMPRE que a particao-alvo != particao do KL_WINPE; (b) copiar o KL_WINPE p/ pendrive/USB antes (midia nao deletavel) e deletar a particao; (c) aviso adicionado no overlay do BOOT INSTALADOR (WinpeToolsPage.xaml.cs): "NAO excluir/formatar a particao que contem C:\KL_WINPE".

**PENDENCIA FECHADA**: "Testar BOOT INSTALADOR na VM" - CONCLUIDO (instalacao real com sucesso). O fluxo BOOT INSTALADOR e o mais testado da pagina: winpeshl.ini -> startnet.cmd -> wpeinit -> scan de drives -> shim X:\setup.exe + /installfrom -> ambiente de discos completo.

### Sessao 15/08 (fim) - Fonte em RAM (RAMDISK X:): excluir a particao primaria + instalacao limpa

Pedido do usuario: "quanto fica o tamanho final nao tem como jogar o instalador na RAM
para ele rodar ali mesmo para poder excluir a particao primaria e fazer uma instalacao limpa".

**MEDICOES REAIS (ISO 25H2 montada no host)**: Sources total = **7,45 GB**; install.wim
sozinho = **6,77 GB**; boot.wim = 0,58 GB. Tamanho final em C: ≈ **8,1 GB** (InstallISO\
Sources 7,45 GB + installer_boot.wim ~0,61 GB + installer_boot.sdi 3 MB). RAM necessaria
p/ fonte em RAM ≈ **11 GB livres** (install.wim 6,77 GB + WIM de Setup descomprimido
~2,5 GB + overhead Setup ~1 GB) - VM com 16 GB cabe; 8 GB nao (fallback automatico).

**BLCOO RAMSRC no startnet.cmd (WinbootManager.ScheduleBootInstallerAsync, apos o scan,
antes do launch)**:
1. `set RAMSRC=` + `if not exist "X:\sources\!ISOFILE!"` -> mkdir X:\sources (o WIM de
   Setup ja tem sources\, e no-op seguro) -> `copy /y "!ISODRV!:...\!ISOFILE!" "X:\sources\!ISOFILE!"`:
   se o copy funcionar, `set RAMSRC=X:\sources\!ISOFILE!` + log "Fonte em RAM X: - a
   particao do disco pode ser excluida na tela do Setup."; se falhar (RAM cheia), log
   "RAM insuficiente - usando a fonte do disco, nao excluir a particao de origem."
2. Se X:\sources\!ISOFILE! ja existe (rodada anterior): RAMSRC direto + log "Fonte ja
   presente em RAM X:".
3. Launch: `if defined RAMSRC` -> `start "" "%SystemDrive%\setup.exe" /installfrom:!RAMSRC!`
   (RAM); senao fallback `!ISODRV!:\KL_WINPE\InstallISO\Sources\!ISOFILE!` (disco).
4. Tudo logado em installer_boot_log.txt (o usuario ve qual fonte foi usada).

**QUIRK cmd.exe (regra 03/08 VIOLADA e CORRIGIDA por mim)**: os echos do bloco RAM tinham
parenteses `(X:)` e `(nao excluir...)` DENTRO do bloco `if defined ISODRV ( ... )` - o parse
quebrou ("- foi inesperado") e o trace (echo on) PROVOU: o echo `(X:)` executou como
TOP-LEVEL, fora do bloco. E a regra de sempre: **qualquer ( ou ) em echo/rem dentro de
bloco if/for quebra o parse, mesmo balanceado** - removidos todos (rems tambem). O parse
do bloco RAM de 3 niveis aninhados (if defined ISODRV -> if not exist -> if exist/else)
funciona perfeitamente sem eles.

**VALIDADO localmente (host, subst X: + Z: com fakes)**: parse limpo (zero "inesperado"),
scan achou Z:, diskpart list disk real no log, "Copiando install.wim para o RAMDISK X:",
"Fonte em RAM X:" + X:\sources\install.wim = True (branch RAMSRC OK); 2o run com
X:\sources preenchido -> "Fonte ja presente em RAM X:" (branch reuso OK); branch RAM
insuficiente validado no teste minimo (log4/log7 = fallback do disco). Lixos limpos
(substs removidos).

**Overlay da GUI (WinpeToolsPage.xaml.cs)**: passo 4 atualizado (startnet + winpeshl.ini),
"Requer ~10 GB livres em C: (install.wim de 6.8 GB + boot.wim de Setup)" e novo bloco
"FONTE EM RAM: se o PC tiver RAM suficiente (>= 11 GB livres), o install.wim e copiado
para o RAMDISK X: do WinPE - ai a particao de origem pode ser EXCLUIDA na tela do Setup
(instalacao limpa). Sem RAM, o fallback usa a fonte do disco e a particao com C:\KL_WINPE
deve permanecer intacta (0x80300001)."

Doc comments atualizados (passo 3 do ScheduleBootInstallerAsync + comentario inline do
script: winpeshl.ini agora e INJETADO, nao removido - explicita o RAMSRC).

Build Core: 0 erros / 0 avisos.

**A TESTAR (VM)**: BOOT INSTALADOR -> log do host mostra "Fonte em RAM X:" (16 GB de RAM)
-> na tela de particoes EXCLUIR a particao primaria (a do Windows antigo) e instalar limpo
na particao recriada; variante com RAM baixa (VM de 8 GB): log "RAM insuficiente" e o
fluxo usa a fonte do disco (nao excluir a particao do KL_WINPE).

### Sessao 15/08 (cont.) - Otimizacao ESD (solid LZMS): rodar o BOOT INSTALADOR com 8 GB de RAM

Pedido do usuario: "pesquise na web o objetivo é rodar em pelo menos 8gb ram" - o
install.wim de 6,8 GB nao cabia no RAMDISK X: com 8 GB de RAM.

**PESQUISA (wimlib man)**: `wimlib optimize install.wim --solid` converte para ESD
(solid LZMS), "decrease the archive size significantly" (exemplos reais: 9->5,6 GB;
4,4->3,1 GB). ESD e o formato oficial das ISOs UUP - o Setup instala normal. Solid
nao pode ser dividido (irrelevante aqui). `--compress=LZX:100` e alternativa lenta.

**MEDICOES REAIS (25H2 pt-BR, host)**:
- install.wim LZX original: 6,77 GB (4 edicoes, 13 GiB de dados descomprimidos).
- `wimlib optimize install.wim --solid`: **5,01 GB** (economia 1,76 GB / 26%) em
  **8,3 min**. RC=0. COMPRESSION: LZMS, Boot Index 0 (install nao e bootable - ok).
- boot.wim idx 2 (Setup): Total Bytes 2,58 GB descomprimido, mas Hard Link Bytes
  1,16 GB -> RAMDISK X: ocupa ~1,4-2,4 GB efetivos.
- **Conta de RAM com ESD**: X: ~1,4-2,4 GB + install.wim solid 5,01 GB + overhead
  ~0,5-1 GB = **~7-8 GB -> cabe em 8 GB**. Sem ESD: 6,77 + 2,4 + 0,5 = ~9,7 GB (nao cabe).
- Bloat do ISO Editor (42 AppX) = ~50 MB apenas - irrelevante para caber (AppX de
  midia moderna sao stubs; o grosso e WinSxS + base). winre.wim NAO existe na 25H2.

**IMPLEMENTADO (Core + GUI)**:
1. `WinbootManager.ScheduleBootInstallerAsync(string isoPath, bool optimizeEsd = false)`:
   bloco 3.5 apos o check do installWim, antes do startnet.cmd:
   - `WinpeBuilder.EnsureFileWritable(installWim)` PRIMEIRO (o robocopy herda o
     atributo ReadOnly do CD -> wimlib falhava com erro 71 "Permission denied" - bug
     real reproduzido no teste; o Set-ItemProperty manual resolveu).
   - Check de espaco: `AvailableFreeSpace < 6 GB` -> pula com log claro (o optimize
     reescreve no proprio arquivo: precisa de ~5 GB extras no volume).
   - `RunProcessCaptured(wimlib, "optimize \"...\" --solid")` (timeout 0 = infinito,
     ~8-10 min) + log antes/depois em GB + tempo.
   - Falha nao aborta o fluxo: loga aviso e usa o install.wim original.
2. GUI (`BtnBootInstaller_Click`): pergunta `mw.ShowConfirmationDialog` antes do
   ShowBusy ("Otimizar para RAM baixa (ESD solid)? 6,8 -> ~5 GB, ~8-10 min, ~6 GB
   extras em C:"); overlay mostra passo 3.5 e os requisitos (16 GB livres com otimizacao).

**A TESTAR (VM de 8 GB)**: BOOT INSTALADOR -> SIM na otimizacao -> log
"Convertendo install.wim para ESD (solid LZMS)...", "ESD otimizado: 6,77 GB -> 5,01 GB
(economia 1,76 GB) em 8,3 min" -> reboot -> startnet copia ~5 GB para X:\sources
(antes 6,77) -> "Fonte em RAM X:" -> excluir a particao primaria e instalar limpo.

### Sessao 16/08 - Integrity OTIMIZADO (scan ~15-30s -> ~1-2s) + mojibake + whitelist de servicos intencionais

Pedido do usuario (log de runtime 21:44-21:54): Integrity demorava demais para carregar
resultados/gerar o valor final; erros no log: (a) "Corrigir Vulnerabilidades" REATIVAVA
DPS/DiagTrack que o usuario tinha desligado nos toggles; (b) mojibake do sc.exe
("[SC] ChangeServiceConfig �XITO"); (c) "bcdedit /set timeout 30" codigo 1 com mensagem
vazia; (d) typo "s Unpark CPU"; (e) "start" de WdiServiceHost/WdiSystemHost falhava 5
(Acesso negado) tratado como falha. "use o rust native se puder e busque na web".

**Rust reg_scan_ffi NAO se aplica (decisao)**: ele varre subarvores por nome/valor (uso:
residuos do DeepUninstaller). O Integrity le valores PONTUAIS (KeyPath+ValueName fixos) -
RegistryBatch (cache de chaves) ja e o caminho mais rapido. Gargalos reais eram PROCESSOS
e objetos COM, nao registro - ambos eliminados abaixo. Web (bcdedit): "The parameter is
incorrect" acontece quando falta GUID/objeto; com encoding OEM + log de saida o motivo
real aparece agora.

**Guardian.cs (KitLugia.Core)**:
1. **BCD: 28 processos -> 1**: GetHarmfulTweaksWithStatus roda cdedit /enum UMA vez
   (se ha tweaks Bcd) em _bcdEnumOutput/_bcdEnumError/_bcdEnumAttempted (ThreadStatic,
   limpo no finally). CheckTweak Bcd usa o cache; fora de scan roda pontual. Parse de
   valor: string.Join(" ", parts, 1, ...) (suporta valores com espaco).
2. **Services: 94 ServiceController -> leitura de registro**: CheckTweak Service lê
   DWORD Start de HKLM\SYSTEM\CurrentControlSet\Services\<nome> via RegistryBatch
   (fast path, 10-50x), helper StartDwordToMode (0=Boot/1=System/2=Auto/3=Manual/4=Disabled),
   fallback ServiceHelper.
3. **Whitelist de servicos intencionais** (KitIntentionalServiceStart): DPS, WdiServiceHost,
   WdiSystemHost, DiagTrack, dmwappushservice, WerSvc, PcaSvc, NDU = Disabled. CheckTweak
   -> Status OK (nao conta como vulnerabilidade); ToggleTweak com applySafeValue -> NAO
   reativa (retorna mensagem "permanece desativado (intencional do KitLugia - reative pelo
   toggle do Kit)"). Resolve o conflito do log (Integrity desfazia os toggles).
4. **ToggleTweak Service**: usa SystemUtils.RunExternalProcessWithCode (exit code real);
   falha do config -> (false, codigo); start falho com 5 (Acesso negado - Wdi*) ou 1056
   (ja rodando) = aviso, nao falha. BCD toggle loga saida+erro na falha.

**SystemUtils.cs**: GetOemEncoding() novo (CultureInfo.CurrentCulture.TextInfo.OEMCodePage
= cp850 pt-BR/437 en-US, fallback 850, final UTF8); aplicado em RunExternalProcessAsync;
RunExternalProcessWithCode(Async) novo (int ExitCode, string Output). **ProcessRunner.cs**:
Run() usa GetOemEncoding() - fim do mojibake do sc.exe/bcdedit.

**IntegrityPage.xaml.cs**: BtnToggleItem SEM Task.Delay(2000) + re-scan completo (ToggleTweak
ja re-verifica via CheckTweak; usa o status do proprio tweak, caixa "INFO" quando status nao
mudou). BtnFixAll: delay por item 150ms -> 25ms, removido Task.Delay(800). Score ("valor
final") agora e gerado com o scan ~10x mais rapido.

**Fixes**: GameBoostPage.xaml.cs:591/607 typo "s Unpark CPU" -> "✔️ Unpark CPU". Fix CS0104
latente WinpeToolsPage.xaml.cs:403 (Application -> System.Windows.Application - o arquivo
nao tem using Forms; erro so aparecia no build completo). Build solucao: 0 erros / 0 avisos
(app fechado - MSB3021 pelo processo rodando).

**A TESTAR (host)**: abrir Integrity -> scan deve carregar em ~1-2s (antes 15-30s); toggles
individuais sem espera de 2s; DPS/WdiServiceHost/WdiSystemHost/DiagTrack com o toggle do Kit
desligado aparecem OK (nao vermelho) e "Restaurar Todos" NAO os reativa; log do sc.exe sem
mojibake; start do Wdi* sem "falha".

### Sessao 16/08 (fim) - CAUSA RAIZ FINAL do scan lento: RecoverFromExecutableScan 29s -> 9ms

**Sintoma**: mesmo com bcdedit/Service/Registry otimizados (sessao anterior), o scan
Integrity ainda levava 92s no app (01:14:52 -> 01:16:24). Profiler (harness
`%TEMP%\opencode\guardian_prof`, elevado) mediu: scan total 58,7s; `PathRepair.
GetInstalledProgramPaths` = 28,9s (sessao anterior: bcdedit 53ms, Registry 11ms,
Service 0ms). Causa: `RecoverFromExecutableScan` fazia 32 `Directory.GetFiles(root,
pattern, AllDirectories)` (8 alvos x 4 raizes) - cada chamada ~29s; o PATH "INCOMPLETO"
dispara isso a cada scan.

**REESCRITA (PathRepair.cs, 5 etapas medidas no harness)**:
1. DFS de passada unica por raiz (`EnumerateFilesSkippingHeavy`, stack + visited):
   91,8s -> 31s. Quirks: (a) **junctions ciclam** (UserProfile\AppData\Local\Application
   Data -> Local) - visited por path NAO pega (path lexical difere) - resolvido com cap
   de profundidade 4 + skip por nome das junctions classicas ("Application Data",
   "Local Settings", "My Documents", "NetHood", "PrintHood", "Recent", "SendTo",
   "Templates", "Start Menu"); (b) listar TODOS os nomes de arquivo e caro - filtrar no
   kernel com `EnumerateFiles(dir, "*.exe")` + "*.cmd".
2. Skip de arvores gigantes (nenhum alvo vive la): node_modules, .git, .svn, temp,
   cache, caches, logs, $recycle.bin, downloads, onedrive, winsxs, installer, webcache,
   history, cookies, codelldb, explorercache, "Microsoft Visual Studio", "Windows Kits",
   "WindowsApps", dotnet, Git, nodejs, PowerShell, "Microsoft Edge", "Common Files",
   "Microsoft", AppData, Roaming, Packages, ProgramData. 31s -> 5,4s.
3. **onlyTargets**: o DFS roda SO para os alvos AUSENTES (GetInstalledProgramPaths
   computa `missing = allTargets - paths.Keys` e so escaneia esses) - com tudo coberto
   o custo e ~0. CUIDADO com o sentido do filtro (bug real: passei os PRESENTES e o
   filtro mantinha exatamente eles - o DFS continuava varrendo tudo).
4. **pwsh fora do wanted**: instalacao MSIX do PowerShell 7 e so um stub reparse
   inacessivel (nunca encontrariavel) - removeu-se pwsh.exe do mapa; pwsh classico
   (ProgramFiles\PowerShell\7) ja tem check proprio. Sem isso, o DFS procurava pwsh.exe
   (que nao existe fora do WindowsApps) e varria as 4 raizes ATE O FIM (~5,4s).
5. Checks rapidos do 7-Zip (ProgramFiles\7-Zip, ProgramFilesX86\7-Zip,
   LocalAppData\Programs\7-Zip) + preferencia no DFS: dir chamado "7-Zip"/"7zip" ganha
   de 7z.exe interno de outros apps (ex: NVIDIA App tem um 7z.exe - era achado como
   "7z"); entre genericos, o mais raso.

**RESULTADO (harness, host)**: `GetInstalledProgramPaths` cache frio: **9 ms** (era
28.987 ms); scan completo com bcdedit cacheado: **160 ms** (477 tweaks). Cache TTL
5 min mantido (2a chamada ~70 ms). 7z correto = C:\Program Files\7-Zip (antes apontava
pro NVIDIA App). pwsh sai da lista quando so existe o stub MSIX (correto - o dir real
e inacessivel).

Build solucao: 0 erros / 108 avisos (baseline nullable GUI).

**A TESTAR (host)**: abrir Integrity -> scan carrega em ~1-2s mesmo na 1a vez apos
iniciar o app; "Restaurar Todos" rapido (25ms/item); nenhum 7z do NVIDIA App no PATH.

### Sessao 16/08 (fim 2) - HarmfulTweaks: duplicatas removidas + 10 checks novos de desktop

Pedido do usuario: "julgue as coisas que tem ai dentro e adicione mais ou menos na mesma proporcao".
Auditoria dos 477 itens de `HarmfulTweaks` (Guardian.cs) contra duplicatas exatas (mesmo ServiceName ou
mesma KeyPath+ValueName) e itens exclusivos de servidor.

**Removidos nesta sessao (duplicatas exatas)**:
- Memory Compression (Memoria) + Compressao de Memoria RAM Desativada (Desempenho) - ambos
  `DisableMemoryCompression` (nenhuma entrada permanece)
- Reset do Cache de RAM (Memoria, SysMain Start=4)
- NTFS - Last Access Time Update (Desempenho, `NtfsDisableLastAccessUpdate`) - fica a de Saude do Disco
- Program Compatibility Assistant PCA (fica PcaSvc em Servicos Essenciais)
- Windows Image Acquisition WIA (fica stisvc)
- Windows Location Service LFS (fica lfsvc)
- Windows Search Indexing em Discos (fica WSearch)
- Gerenciador de Filas de Impressao Print Spooler (fica Spooler)
- Prefetch / Superfetch Desativado (SysMain, Desempenho) - fica a de Saude do Disco
- SSD TRIM Agendado (defragsvc, Desempenho) - fica a de Saude do Disco
- PnP Device Enumeration (PlugPlay, Driver e Hardware) - fica a de Servicos Essenciais
- Restricoes de Armazenamento de Senhas (VaultSvc, Perfil de Usuario) - fica a de Servicos Essenciais
- LLMNR (Seguranca de Rede, `EnableMulticast`=1, nome com typo) - fica a 648 (Rede e Conectividade, `EnableLLMNR`=0)

**Mantidos deliberadamente** (nao sao duplicatas): SessionEnv (RDP), SSTP/VPN (SstpSvc/RasMan),
LanmanServer/LanmanWorkstation (SMB domestico), W32Time, iphlpsvc, DPS, LargeSystemCache,
DisablePagingExecutive, Fast Startup (3x), NTFS 8.3 (4x).

**10 checks novos adicionados ao fim da lista** (desktop Win10/11):
1. AllowInsecureGuestAuth=1 (LanmanWorkstation\Parameters, Seguranca de Rede)
2. RequireSecuritySignature=0 (LanmanWorkstation\Parameters, Seguranca de Rede)
3. LmCompatibilityLevel=1 (Lsa, Seguranca Critica; default 3 = NTLMv2)
4. EnableControlledFolderAccess=0 (Defender Exploit Guard, Defesa e Antivirus - ransomware)
5. VerifiedAndReputablePolicyState=0 (CI\Policy, Defesa e Antivirus - Smart App Control W11)
6. RestrictDriverInstallationToAdministrators=0 (PointAndPrint, Seguranca Critica - PrintNightmare)
7. AdvertisingInfo\Enabled=1 (Privacidade Global)
8. TailoredExperiencesWithDiagnosticDataEnabled=1 (Privacidade Global)
9. EnableActivityFeed=1 (Privacidade Global)
10. BingSearchEnabled=1 (Privacidade Global)

**Correcoes de corrupcao** (edits anteriores tinham deixado fragmentos): item hibrido
renomeado para "Working Set Trim (Poda de Working Set Desativada)" (`DisablePagedSystemCaching`),
fragmento pendurado "Reset do Cache de RAM" removido, corpo orfao do NTFS Last Access removido.

**LICAO**: ao remover um bloco, o oldString deve incluir o bloco inteiro `new() { ... },` + o
cabecalho do proximo item; conferir com leitura antes do build. Build Core: 0 erros / 0 avisos.
Solucao completa so com app fechado (MSB3021 DLL bloqueada pelo processo rodando).

**A TESTAR (host)**: abrir Integrity -> scan lista ~467 checks (era 477), sem entradas duplicadas;
"Restaurar Todos" nao reativa DPS/DiagTrack (whitelist da sessao anterior continua valida).

### Sessao 16/08 (fim 3) - Explorador de PATH (botao + no Integrity) + integracao Everything (SDK embutido)

Pedido do usuario: botao "mais" ao lado do info nos itens PATH do Integrity -> janela com o
PATH atual, diagnostico por entrada e adicionar por item ausente; acelerar a resolucao de
executaveis instalados com o indexador Everything (pesquisa web feita).

1. **Everything (voidtools) integrado** (`KitLugia.Core\EverythingSearcher.cs`, novo):
   - Load dinamico LoadLibrary/GetProcAddress/Marshal.GetDelegateForFunctionPointer da
     Everything64.dll (SDK oficial, 91 KB — EMBUTIDO em `KitLugia.GUI\Resources\App\Everything\`,
     glob Resources\**\* copia p/ output; a DLL sozinha NAO indexa — precisa do processo
     Everything rodando, IPC via WM_COPYDATA).
   - Candidatos: BaseDir\Resources\App\Everything\, BaseDir\, %ProgramFiles%\Everything\,
     %LOCALAPPDATA%\Programs\Everything\ (todos relativos/descobertos — sem hardcode).
   - Funcoes: SetSearchW, SetMatchPath(false)/SetMatchWholeWord(true)/SetMatchCase(false),
     SetMax(8), SetSort(3=PATH_ASCENDING), QueryW(true), GetLastError (0=OK, 2=IPC indisponivel
     = Everything nao rodando OU mismatch de elevacao), GetNumResults, IsFileResult,
     GetResultFullPathNameW, IsDBLoaded. SDK e estado GLOBAL do processo -> lock `_gate`.
   - `FindExecutableDirectories(fileNames)`: query por nome exato (whole word), filtra
     IsFileResult + File.Exists, retorna Dictionary<nome, dir> (~1ms por alvo).
   - Probe "kitlugia_probe_no_such_file" no 1o acesso -> `IsAvailable`/`LibraryPath` (static,
     ThreadStatic no) — se IPC falhar, loga orientacao de instalar/rodar a Everything.
2. **PathRepair.cs (Core)**: `SystemPathRegistryKey`/`UserPathRegistryKey`; `GetSystemPathValue`/
   `GetUserPathValue`; `SetSystemPathValue`/`SetUserPathValue` (via TrySetValueWithOwnershipFallback
   hint Unknown + `BroadcastEnvironmentChange` = SendMessageTimeout HWND_BROADCAST WM_SETTINGCHANGE
   "Environment", P/Invoke proprio NativeEnv); `AddSinglePathEntry(pathType, entry)` (dedup por
   caminho EXPANDIDO, idempotente); `PathEntryCandidate` {Label, Path, Detail, CanAdd};
   `GetMissingSystemEntries` (7 minimos: system32, %SystemRoot%, Wbem, WindowsPowerShell\v1.0,
   OpenSSH, dotnet, PowerShell\7 — CanAdd = pasta existe); `GetMissingInstalledEntries` (via
   GetInstalledProgramPaths).
   `RecoverFromExecutableScan`: hook do Everything ANTES do DFS (resolve os wanted restantes,
   remove do DFS; preferencia 7z: dir 7-Zip/7zip ganha de 7z.exe interno tipo NVIDIA App; log
   "PathRepair: N alvo(s) resolvido(s) pelo indice da Everything").
3. **UI (IntegrityPage)**: botao info envolvido em StackPanel + novo botao "mais" `BtnPathExplore`
   (Visibility Collapsed, DataTrigger em `ScannableTweak.IsPathItem` -> Visible); handler abre
   `KitLugia.GUI.Windows.PathExplorerWindow` (Owner = Window.GetWindow). `Models.cs`:
   `IsPathItem` = Name.Contains("PATH", OrdinalIgnoreCase) && ValueName == "Path".
4. **PathExplorerWindow (GUI\Windows\, novo)**: 2 colunas System|User com raw TextBox + Copiar;
   ListBox de entradas com icone/cor por PathEntryProblem (DiagnosePath); candidatos ausentes
   com botao "Adicionar" por item (CanAdd); footer com dica/acelerador da Everything +
   "Abrir Editor do Windows..." (rundll32 sysdm.cpl,EditEnvironmentVariables); carrega via
   Task.Run; Refresh*Section apos add. Estilo escuro do kit (UninstallHistoryWindow).
5. **Bug latente corrigido (RegistryOwnership.DetectValueKind)**: so %SystemRoot%/%USERPROFILE%/
   %PATH% eram tratados como expandiveis — `%ProgramFiles%` virava REG_SZ quebrado; agora regex
   generica `%[^%]+%` (`HasExpandableVariables`) -> ExpandString.
6. **Quirks da sessao**: (a) usings globais da GUI incluem System.Windows.Forms -> `Button`/
   `MessageBox` AMBIGUOS em arquivos novos: qualificar System.Windows.Controls.Button /
   System.Windows.MessageBox (quirk ja documentado); (b) PowerShell 5.1 `Set-Content -Encoding
   UTF8` RE-CORROMPE acentos de arquivo sem BOM (Get-Content rele ANSI, grava UTF8) — editar
   arquivos com ferramenta que preserva UTF-8 e conferir com Select-String apos; (c) harness
   csc .NET Framework 4.0 NAO carrega assembly net10 (ReflectionTypeLoadException) — harness
   de teste do Core precisa ser console app net10.0-windows via `dotnet build`.

**TESTADO (host, Everything 1.4 32-bit rodando + DLL embutida ao lado)**: IsAvailable=True,
LibraryPath resolvido; winget->WindowsApps e npm->Roaming\npm achados pelo indice (antes DFS);
missing system = PowerShell\7 (CanAdd=False, pasta nao existe - correto); DiagnosePath User:
18 problemas legitimos (system paths no User PATH, duplicados case-insensitive, chocolatey\bin
inexistente); AddSinglePathEntry idempotente (7-Zip ja presente -> True sem escrever). Build
solucao: 0 erros (Core 0/0; GUI so baseline). SDK: Everything 1.4 x86 instalado no host —
SDK DLL funciona via IPC com servidor 32-bit de callers 64-bit.

**A TESTAR (app)**: Integrity -> item PATH -> botao "mais" -> janela com entradas/candidatos;
"Adicionar" em candidato -> dedup + broadcast; sem Everything rodando -> dica no rodape.

### Sessao 16/08 (fim 4) - Travadas do Guardian: gate single-flight + cache bcdedit TTL + 2 bugs reais do PathRepair

Pedido do usuario: "o app congela" em 4 pontos - abrir Integridade, digitar na busca global,
clicar em item/toggle, abrir Explorador de PATH. Auditoria: NENHUMA chamada Guardian roda na UI
thread (todas Task.Run). Causa raiz sistemica: scans CONCORRENTES (IntegrityPage ctor + busca
global por tecla + toggles + PathExplorer) saturavam CPU/disco; cache bcdedit `[ThreadStatic]`
forcava 1 spawn de bcdedit por thread/scan; DFS (RecoverFromExecutableScan) rodava FORA do
lock -> DFS em paralelo.

**Guardian.cs**: `_bcdEnumOutput/_bcdEnumError/_bcdEnumAttempted` deixaram de ser ThreadStatic
(linhas 56-66) - agora campos static compartilhados + `_bcdEnumTimeUtc` + `BcdEnumCacheTtlSeconds
= 15`. `GetHarmfulTweaksWithStatus` inteiro dentro de `lock (_scanGate)`; invalida o cache BCD
so por TTL (15s); grava os dados ANTES de setar `_bcdEnumAttempted`; finally nao reseta mais o
cache BCD (so `_currentBatch = null`). Branch pontual do BCD em `CheckTweak` (~3611) checa TTL
e alimenta o cache compartilhado quando roda fora do scan.

**PathRepair.RecoverFromExecutableScan**: single-flight - `lock (_scanCacheLock)` cobre a
varredura inteira (2o chamador espera e reusa o cache).

**2 BUGS REAIS encontrados pelo harness** (`%TEMP%\opencode\guardian_gate`, console net10
referenciando KitLugia.Core.dll Debug - csc .NET Framework NAO carrega net10, usar dotnet build):
1. **Cache envenenado por scan vazio**: com onlyTargets filtrado a zero (todos os 8 alvos
   cobertos pelos checks rapidos), o metodo gravava `_scanCache = {}` (vazio) -> 5 min de cache
   dizendo "nada encontrado" -> chamadas seguintes retornavam 7z ausente em 0,2ms. Correcao:
   `didScan = wanted.Count > 0`; so grava cache se houve scan, e faz MERGE no cache existente
   (scan de fallback nao descarta o que outro scan achou).
2. **Preferencia do 7z morta (NVIDIA App)**: o 1o `7z.exe` achado removia `7z.exe` do wanted -
   o ramo de preferencia (`prevGood && !curGood`) so executava quando found ja tinha 7z E o
   arquivo ainda estava no wanted (impossivel: acontecem no MESMO 1o hit). Resultado sem
   Everything: DFS achava `C:\Program Files\NVIDIA Corporation\NVIDIA App\7z.exe` como "7z".
   Correcao (nos 2 loops, Everything e DFS): para o alvo 7z, so `wanted.Remove` quando o
   candidato e BOM (dir contem 7-Zip/7zip) - a busca continua ate achar o 7-Zip real; entre
   genericos, o mais raso.

**MEDICOES (harness, host)**: 2 scans Guardian concorrentes = 152 ms total (antes 2 scans
completos em paralelo + 2 bcdedit); scan imediato = 18 ms (TTL compartilhado); 2 DFS
concorrentes = 659 ms total (1 scan + reuso); DFS frio = 651 ms com `7z=C:\Program Files\7-Zip`
(correto); cache quente = 0 ms; GetInstalledProgramPaths = 0 ms (tudo coberto por checks
rapidos). Build solucao: 0 erros / 108 avisos (baseline GUI). PathExplorerWindow.xaml.cs
ganhou `using System.IO;` (erro CS0103 Directory no build completo - ImplicitUsings nao cobre
esse arquivo).

**A TESTAR (host)**: abrir Integridade + digitar na busca global ao mesmo tempo; alternar
toggles; abrir Explorador de PATH - sem travadas; ~150-200 ms de scan; 7z aponta p/ 7-Zip
(nao NVIDIA App) mesmo sem Everything rodando.

### Sessao 16/08 (fim 5) - Indexador nativo USN/MFT: cache SO de diretorios + drop de node/git/npm CORRIGIDO

Pedido do usuario: kit funcionando so com o .exe, sem Everything.exe externo - o indexador
nativo MFT/USN embutido precisava resolver TODOS os alvos do PathRepair (7z/dotnet/winget
resolviam, mas node/git/npm/cargo caiam na resolucao de caminho).

**CAUSA RAIZ (provada com debug)**: o cache guardava TODOS os registros da MFT com cap de
4M - o C: tem >4M registros e o cap cortava DIRETORIOS RECENTES (ex: SquirrelTemp\tempk,
pasta de instalador) da cadeia de pais. A cadeia quebrava no meio -> caminho parcial
("C:\SquirrelTemp\tempk\...") -> File.Exists falso -> alvo descartado. Os matches EXISTIAM
(10x node, 16x npm, 16x git, 1x cargo) - o drop era so da resolucao.

**CORRECAO (NativeUsn.cs, ScanVolume)**: o cache agora guarda SO DIRETORIOS
(dirCache FRN->(Parent,NameIdx) + dirNames), arquivos so casam nome em wanted.
A cadeia de pais so precisa de diretorios - memoria ~10x menor e o scan roda o volume
INTEIRO sem cap quebrar caminhos (MaxDirRecords=2_000_000 apenas como guarda de seguranca).

**VALIDADO (host, harness)**: 7 nomes em 7,2s -> TODOS os alvos com caminhos reais:
- 7z.exe = C:\Program Files\7-Zip (+ Local Disk C_1102025206\... e C:\tmp\kitlugia-gui-build\...)
- dotnet.exe = C:\Program Files (x86)\dotnet | C:\Program Files\dotnet
- winget.exe = C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.280.0_x64...
- node.exe = C:\Program Files\nodejs | C:\Users\Lugia\.lmstudio\.internal\utils | Raycast\backend
- npm.cmd = C:\Program Files\nodejs | node_modules\npm\bin | VS18\...\NodeJs
- cargo.exe = C:\Users\Lugia\.cargo\bin | .rustup\toolchains\stable...\bin
- git.exe = C:\Program Files\Git\bin | Git\cmd | Git\mingw64\bin
Fallback EverythingSearcher (USN): 5 nomes -> 7z, winget, node, npm, git (7,2s).
Suite completo do harness: [1] 136ms (462 itens), [2] 14ms, [3b] DFS cold 7,2s com
7z=C:\Program Files\7-Zip, [5] GetInstalledProgramPaths 0ms com 8 alvos corretos.
Todo o debug (NativeUsn-DEBUG, debugMatchCount, logs de chain/resolve) REMOVIDO.
Build solucao: 0 erros / 108 avisos (baseline nullable GUI).

**A TESTAR (app)**: PathRepair/Explorador de PATH sem Everything rodando (o kit nao precisa
mais do Everything.exe - so da DLL embutida para acelerar quando o processo existe):
- Sem Everything: node/git/npm/cargo/7z/dotnet/winget todos resolvidos pelo indexador nativo
  (log "PathRepair: N alvo(s) resolvido(s) pelo indexador nativo USN (MFT).")
- Com Everything rodando: mesma saida, mais rapido (indice ja montado)
- Volumes grandes (C: com >4M registros) agora escaneiam COMPLETOS - diretorios recentes
  em pastas de instalador (SquirrelTemp etc.) nao quebram mais a resolucao de caminho.

### Sessao 17/08 - Fix travada das categorias (CanContentScroll) + Guia do PATH (janela nomeada)

**Travada ao mudar categoria na Integridade - CAUSA RAIZ**: o ScrollViewer da lista
(IntegrityPage.xaml:148) nao tinha `CanContentScroll="True"` - sem isso o
VirtualizingStackPanel NAO virtualiza e cada rebind do ItemsSource materializava
TODOS os ~460 containers (templates pesados) na UI thread. "Todas as Categorias"
travava mais (460 itens), "Modificado" era rapido (~50). CORRECAO: 1 linha
(CanContentScroll=True). ToolsPage.xaml:96 (ComboBox popup) nao precisa - so o
ScrollViewer da lista de integridade.

**PATH verificado no registry** (pedido do usuario): User PATH correto - todas as
adicoes do PathRepair estao la (.cargo\bin, nodejs, 7-Zip, Git\cmd, dotnet,
WindowsApps, GitHub CLI, .dotnet\tools, WARP, VS Code, Jan, qemu, Devin, Kiro).
Aviso laranja no `C:\ProgramData\chocolatey\bin` = pasta NAO EXISTE (Chocolatey nao
instalado, entrada legada - remover e seguro). Duplicatas no System PATH (system32,
Wbem, OpenSSH x2, dotnet com/sem barra) = inofensivas.

**Legenda de cores do Explorador de PATH** (PathExplorerWindow.xaml.cs:88 ToRow):
✅ verde #4CAF50 = pasta existe; ⚠️ laranja #FFA500 = Missing (pasta nao existe);
🔄 dourado #FFD700 = WrongLocation (sistema no User PATH ou vice-versa);
🔁 vermelho #FF6F61 = Duplicate; 🧹 cinza #999999 = Junk; 🗑️ cinza = Orphan;
❌ vermelho = SyntaxError. Ordem dos checks: DiagnosePath (PathRepair.cs:123).

**NOVO PathGuideWindow** (KitLugia.GUI\Windows\, janela nomeada "Guia do PATH"):
botao "i" (&#x24D8;) no header do PathExplorerWindow abre a janela com legenda de
cores, o que cada painel mostra, cadeia de resolucao (USN/MFT -> Everything ->
DFS) e dicas. Overlay por cima do kit (Owner=this, ShowDialog). Sem BOM era
corrompido - todos os arquivos novos salvos em UTF-8+BOM (.editorconfig).

**docs/PATH_EXPLORER_GUIDE.md** (novo): legenda, arquitetura (PathRepair/
NativeUsn/EverythingSearcher/Guardian), ideias de expansao (acoes em lote,
reparar tudo, backup/restore do PATH, snapshot comparativo, edicao inline,
%VAR% indefinida, ordenacao inteligente) e medicoes de performance.

Build: 0 erros. A testar (host): alternar categorias/buscar sem travada; abrir
Explorador de PATH e clicar no "i" (guia abre por cima); cores conferem com a
legenda; chocolatey\bin laranja.

### Sessao 17/08 (cont.) - EVERYTHING REMOVIDA: so indexador nativo USN/MFT, otimizado + cache em disco

Pedido do usuario: "sim pode remover ai da para manter so o nativo e otimizar o nativo
o maximo possivel" - o kit agora e 100% independente de processo/DLL externa.

1. **Remocao**: `KitLugia.Core\EverythingSearcher.cs` DELETADO; `Everything64.dll` +
   pasta `Resources\App\Everything\` DELETADAS; nenhuma referencia em csproj (glob
   Resources\** cobria a DLL). Docs (PATH_EXPLORER_GUIDE.md secao 7) e guia
   (PathGuideWindow.xaml secao 3: cadeia agora = USN/MFT -> varredura direta DFS)
   atualizados; XML doc do PathGuideWindow.xaml.cs sem "Everything".

2. **PathRepair.cs**: hook agora chama `NativeUsn.FindFileDirectories(wanted.Keys.ToList(),
   maxPerName: 8)` direto; log "PathRepair: N alvo(s) resolvido(s) pelo indexador
   nativo USN (MFT)."; preferencia 7z com `kvp.Value[0]` (candidato = 1o da lista,
   ja ordenada 7-Zip primeiro).

3. **NativeUsn.cs OTIMIZADO (validado no host, harness usn_opt)**:
   - **Cache em disco por volume** (estilo Everything DB): `%LOCALAPPDATA%\KitLugia\
     NativeUsnCache\usn_<serial>.json` (arquivo por serial do volume via
     GetVolumeInformation). 1a leitura da MFT: ~7,6s (C: ~4M registros, ~4GB);
     consultas seguintes: **4-31ms**. Validacoes: (a) serial do volume bate
     (mudou -> deleta arquivo), (b) `Directory.Exists` em cada caminho ao carregar
     (app desinstalado cai fora), (c) MISS de nome -> so rescaneia se o cache
     estiver VELHO (**TTL 6h**) - alvo genuinamente ausente (ex: cargo nao instalado)
     NAO vira scan de 8s por consulta (bug pego no harness: RUN 4 rescaneava).
   - **ZERO alocacoes de string no scan**: nomes de diretorio copiados como bytes
     crus num pool unico (byte[] exponencial + List<(Off,Len)>); comparacao de
     nomes de arquivo via `NameMatches` byte-a-byte case-insensitive (antes:
     `wanted.Contains(GetString)` = ~2M strings alocadas). Match de arquivo
     tambem copia o nome para o pool (o buffer do ioctl e REUTILIZADO entre
     iteracoes - offsets do buf nao valem apos o loop).
   - Buffer ioctl 8MB + volumes em PARALELO (Parallel.ForEach, por volume);
     resultados validados com File.Exists/Directory.Exists.
   - Resultados identicos ao baseline: 7 alvos (7z/dotnet/winget/node/npm/cargo/git),
     50 caminhos validos, 7z = C:\Program Files\7-Zip (nao NVIDIA App).

Build: Core 0 erros / 0 avisos; harness 0 erros. GUI compilacao completa pendente
(app aberto bloqueia MSB3021). A testar (host): abrir Integrity -> PathExplorer ->
"mais" sem travada (cache quente); sem Everything rodando tudo funciona.

**LICAO**: bottleneck do scan USN e a LEITURA da MFT (~4GB em C:), nao o parse -
alocacoes/buffers nao mudam o tempo de frio (7-8s); a otimizacao real e cache do
resultado (o que a Everything faz com o .db). Cache em disco + TTL + validacao
File.Exists = frescor sem rescan storm.

### Sessao 18/08 - Auditoria RAM/CPU: fixes HIGH aplicados (leaks de Process + jank de UI thread)

Pedido do usuario: "veja se o modo como ele foi construido e o mais otimizado e melhor sem memory leaks picos de uso de processador". Auditoria com 4 agentes (estrutura, caches, timers, processos). Veredito: arquitetura solida; principais achados e fixes abaixo.

**Fixes aplicados (Auditoria de Processos - todo #5)**. Todos os pontos de Process sem Dispose: 
1. Program.cs BringExistingToFront - foreach + dispose de todos exceto o retido (existentes descartados nos 2 caminhos).
2. TraySettingsPage.IsProcessRunning, ServerPage:93, QuickInstallPage:57 (using var proc).
3. HunterWindow: L190 (using + try aninhado do MainModule), L751/L868, kill sites L969/977.
4. ProcessMonitorPage L368/622/635/649 (using var process); L114/190/458/475 ja dispoiam (sem mudanca).
5. SystemTweaks L7616 (OneDrive: foreach + dispose + HasExited), LanConnectionManager L237, BrowserExtensionManager L198 (try/finally).
6. MainWindow L253 (GetGoodbyeDPIStatus: finally dispose do array) e L1427 (kill: finally por item).
7. VERIFICADOS OK: TrayIconService GetProcessesById (11 sites using var), GetCachedProcesses callers (1398/1967/2002/2141/3092-3146 todos com finally dispose), ApplyProcessRamLimits/CpuLimits (4096+), IsoEditorManager:43, IsoManager:675, TunnelManager:253 (intencional).

**Fixes aplicados (todo #7 - monitor avancado fora da UI thread)**: 
- AdvancedMonitor_Tick (L4225, DispatcherTimer 2s): Task.Run + guard anti-sobreposicao Interlocked _monitorTickBusy (campo novo perto de _advancedMonitorTimer). UpdateProcessCache/AnalyzeProcessBehaviors/CheckSmartAlerts/UpdateTrayIconAdvanced sao thread-safe (dicionarios + Logger + NotifyIcon OK cross-thread).
- UpdateSystemStats: PerformanceCounter de CPU CACHEADO em _cpuTotalCounter (criar por tick custava ~100ms+; 1o NextValue()=0 aceitavel, leitura vira delta).
- ProBalanceTimerTick: ApplyProBalance via Task.Run (scan de todos os processos, antes na UI thread).

**Fixes aplicados (todo #6/#8/#9/#10)**: 
- ConsoleManager: persistencia em disco movida para fora da UI thread com cadeia de continuations (_logWriteTail, TaskScheduler.Default) - ordem entre lotes preservada; espelho da UI adiciona so string no dispatcher.
- GitHubUpdater.StartAutoUpdateCheck: try/catch AGORA DENTRO do while - excecao pontual (rede/API) nao mata mais o loop de 24h.
- AppIconHelper (cap 500) + ProgramIconHelper (cap 200): caches ganharam evict LRU real (dict de acesso + remove o mais antigo quando lota) - antes cresciam sem limite.
- TrayIconService: SessionEnding handler guardado em campo (SessionEndingEventHandler) e REMOVIDO no DisposeCore (antes lambda anonima = leak de assinatura estatica para sempre).

**Build**: Core 0 erros / 0 avisos; GUI 0 erros / 108 avisos (baseline nullable; app aberto - build de verificacao com -o temp). Fechar o app antes do build normal (MSB3021).

**Adiados (auditoria, nao bloqueiam)**: virtualizacao AppsPage.xaml; GlobalSearchPage sem Take(); timers de paginas rodando com janela no tray (Window.Hide nao dispara Unloaded); 21 storyboards infinitos (glow GameBoostPage); sync-over-async (AdapterManager 362, DriverManager 397, SystemTweaks 430, SearchEngine 186, SmartVersionDetector 330, MainWindow 1410-1518); VirtualTerminal Text += O(n2); ProcessMonitorPage merge O(n*m); WinTunePage ~40 reads sync; ExmTweaksPage 4 bcdedit; Program.cs app inteiro High priority; paginas recriadas por navegacao; double-dispose _trayService (MainWindow ForceShutdown x Cleanup - checar idempotencia); dead code GetCpuUsage GameBoostPage L168.


### Sessao 18/08 (cont.) - RAM presa em 160MB apos navegacao: sem cache de paginas + devolucao de RAM pos-nav

Pedido do usuario: "o kit não esta conseguindo limpar a ram e se manter em 60mb~ agora saindo da
pagina do inicio e indo até o services page e voltando ele fica constantemente em 160mb, o kit
não pode ter cache de paginas".

**Investigacao (conclusao: NAO ha cache de paginas)**: MainFrame (Frame WPF) sem dicionarios de
paginas (GetPageInstance cria instancia nova a cada F5/Ctrl+R); RemoveBackEntry ja era chamado
pos-navegacao (background); TODAS as paginas com DispatcherTimer param o timer no Cleanup
(NetworkPage/PrivacyPage/ProcessMonitorPage/StutterPage/PartitionsPage/TraySettingsPage/
WinpeToolsPage/DiagnosticPage - verificado via grep); DashboardPage/ServicesPage nao tem timers,
Load/Unload desinscrevem, CTS cancelado, grids limpos, DataContext=null. Zero assinaturas de
eventos estaticos nas paginas. O 160MB constante = heap crescido (WMI + DataGrids) com Working
Set nunca devolvido: DashboardPage.Cleanup chamava MemoryHelper.TrimWorkingSet mas
ServicesPage.Cleanup NAO - e a rota do usuario (Services -> back) era justamente o hop sem trim.

**Correcoes (2 arquivos)**:
1. `MainWindow.CleanupAndNavigate`: novo bloco pos-navegacao em Task.Run (sem cache de paginas) -
   se WorkingSet > 90MB: GC.Collect(MaxGeneration, Optimized) + WaitForPendingFinalizers +
   MemoryHelper.TrimWorkingSet; loga "RAM devolvida apos navegacao: N MB" so quando liberou >= 10MB.
   Gate de 90MB evita competir com o GC natural em uso leve (respeita o comentario existente de
   nao forcar GC em navegacao normal).
2. `ServicesPage.Cleanup`: adicionado MemoryHelper.TrimWorkingSet() (espelha o DashboardPage -
   era a unica pagina do roteiro sem o trim).

Build: GUI 0 erros / 108 avisos (baseline, build de verificacao com -o temp).

**A TESTAR (app)**: Dashboard -> Services -> voltar -> RAM deve cair de volta a ~60-80MB em
alguns segundos (log "RAM devolvida apos navegacao: N MB"); repetir varias vezes e conferir que
o Working Set nunca acumula (paginas nao ficam retidas).

**CORRECAO 18/08 (2a rodada) - pico de CPU de 14% ao navegar: GC.Collect removido**

Sintoma: apos o fix anterior, o usuario notou pico de 14% de CPU ao mover entre paginas.
Causa: o bloco pos-navegacao chamava GC.Collect(MaxGeneration, Optimized) +
WaitForPendingFinalizers a cada navegacao com WorkingSet > 90MB - coleta gen2 bloqueante
de heap de ~160MB custa ~50-150ms de CPU (o comentario original do codigo ja avisava:
"GC.Collect() forcado causa micro-freezes e compete com a renderizacao do WPF").

Correcao (MainWindow.CleanupAndNavigate): bloco agora so chama
MemoryHelper.TrimWorkingSet (EmptyWorkingSet, estilo Firemin) - chamada de sistema
instantanea, sem GC forcado, sem micro-freeze, sem pico de CPU. O gate de 90MB e o log
"RAM devolvida apos navegacao: N MB" mantidos. O heap continua sendo gerenciado pelo
GC natural; o Trim devolve a RAM fisica visivel no Task Manager.

Build: GUI 0 erros / 108 avisos (baseline).

**A TESTAR (app)**: Dashboard -> Services -> voltar - RAM cai de volta a ~60-80MB SEM pico
de CPU perceptivel (navegacao sem micro-freeze); repetir varias vezes e conferir Working
Set estavel.

### Sessao 18/08 (cont.) - Otimizacoes WPF aplicadas (pesquisa + 3 mudancas de baixo risco)

Pedido do usuario: "a partir daqui voce vai poder aplicar as otimizacoes que quiser"
(apos pesquisa web sobre otimizacao WPF, entregue em PT-BR com fontes MS Learn).

**1. Virtualizacao explicita nos 4 DataGrids da ServicesPage.xaml** (GridStartup L95,
GridServices L489, GridTasks L636, GridBootItems L724):
`EnableRowVirtualization="True" EnableColumnVirtualization="True"
VirtualizingStackPanel.VirtualizationMode="Recycling"` - todos ja tinham altura
limitada (linhas *), entao a virtualizacao ja funcionava por default; a mudanca
explicita + Recycling (reuso de containers) evita recriacao de row templates.

**2. ProcessMonitorPage.xaml.cs - 2 bugs reais corrigidos**:
- `_cpuCounter`/`_ramCounter` (PerformanceCounter) removidos: `_cpuCounter` nunca era
  lido; `_ramCounter` so no UpdateSystemInfo antigo. Removidos fields, bloco try/catch
  de init e Dispose() do Cleanup.
- `UpdateSystemInfo` reescrito: RAM real via `MemoryOptimizer.GetMemoryStats()`
  (KitLugia.Core, GlobalMemoryStatusEx) - antes tinha 16GB HARDCODED. Agora
  `"{used:F1} GB / {stats.TotalGB:F1} GB ({stats.Percent}%)"`.
- `ProcessMonitorInfo` agora implementa INotifyPropertyChanged (notifica CpuUsage,
  RamUsage, Priority, Status, NetworkUsage; Id/Name planos) - sem isso os valores
  visiveis nunca atualizavam nas linhas ja existentes (o merge in-place da
  ObservableCollection a cada 2s ja estava correto, so faltava a notificacao).
- Obs: a otimizacao planejada (reusar a colecao sem reatribuir ItemsSource) JA
  existia no codigo - nada a fazer nesse ponto.

**3. GameBoostPage: so 1 storyboard infinito (nao 21)** - a nota antiga do AGENTS.md
("21 storyboards infinitos") estava desatualizada (de outra copia). O unico e o
StatusGlow (L142-151: DropShadowEffect #00FF00 BlurRadius 15, Opacity+BlurRadius
DoubleAnimations Forever/AutoReverse) - elemento pequeno, custo desprezivel,
MANTIDO (visual validado pelo usuario). DropShadowEffects estaticos L104 (#FFD700)
e L138 (StatusGlow) tambem mantidos.

**Quirk do rg/PowerShell**: padrao com aspas duplas embutidas falha silenciosamente
(`'RepeatBehavior="Forever"'` retorna vazio mesmo havendo match) - usar alternancia
(`'Forever|Storyboard'`) ou sem aspas no padrao.

Build: GUI 0 erros / 108 avisos (baseline, verificacao com -o temp - app rodando
bloqueia MSB3021).

**A TESTAR (app)**: abrir Services (4 grids listando servicos/tasks/boot) e navegar
sem travada; ProcessMonitorPage mostrando RAM real (nao 16GB) com valores de CPU/RAM
atualizando a cada 2s nas linhas ja existentes; GameBoostPage com glow normal.

### Sessao 18/08 (cont. 2) - CPU 1-2% no Task Manager: diagnostico + 3 fixes (GoodbyeDPI cache, RAM limiter 2s, tracker so com janela)

Pedido do usuario: "veja o uso de CPU" (1-2% constante no Gerenciador de Tarefas, RAM presa em 160MB estava resolvida). Diagnostico COMPLETO com medicao real das duas versoes (atual vs copia 17):

**MEDICOES (Debug, mesmo host, registry real)**: TRAY atual 57.5MB/0.25% (max 1.87%, p95 0.69%) vs (17) 59.5MB/0.15% (max 0.61%) - atual usa MENOS RAM; JANELA atual 103.9MB/0.18% (max 0.78%) vs (17) 94.1MB/0.14% (max 0.55%) (+10MB nao acumulativo, provavel renderizacao WPF). Diffs concluidos: GetGoodbyeDPIStatus, MonitorTick (identicos entre versoes; atual so adiciona profile.LastSeenTick), DashboardPage_Loaded (leve - LoadSystemInfo com cache de sessao em disco).

**CONCLUSAO**: versao atual NAO vaza e NAO tem cache de paginas. "77MB constante" do usuario = estado pos-navegacao apos TrimWorkingSet (heap WPF ~100MB nao devolve sem GC; GC.Collect foi REMOVIDO por pico de 14% CPU). Picos de CPU identicos nas duas versoes: ~0.3-0.5% a cada 2s (goodbyeDPI status) + ~1.87% a cada 30s (MonitorTick/UpdateProcessProfiles); "1-2%" do Task Manager = media suavizada desses picos.

**3 FIXES APLICADOS (aprovados pelo usuario)**:
1. **GoodbyeDPI status com cache de 10s** (MainWindow.xaml.cs, getter GoodbyeDPIActive): campos _goodbyeDpiExternalScanTime (DateTime.MinValue) + _goodbyeDpiExternalScanResult; se _goodbyeDpiProcess vivo -> true imediato (sem scan); senao o scan externo so roda se o cache tiver > 10s (cache atualizado nos 2 caminhos). Atraso maximo de 10s no tooltip quando o processo externo morre - aceitavel.
2. **RAM Limiter minimo de 2000ms** (TrayIconService.cs setter RamLimiterIntervalMs): Math.Max(2000, value) (era 500). Cobre todos os caminhos: load do registry (L1718 passa pelo setter), save (L1450), UI TraySettingsPage (L421/L468/L475). Registry auto-corrige para 2000 (getter retorna o clampado). Efeito colateral: penaltyMs = Math.Min(9000, _ramLimiterIntervalMs * limit.ConsecutiveTrimCount) dobra (trims consecutivos mais espacados - aceitavel).
3. **Tracker de perfis so com janela visivel** (MonitorTick, secao "// 7. Dynamic Intelligence (V2)"): UpdateProcessProfiles(stats); ApplyFireminOptimizations(); envoltos em if (IsMainWindowVisibleForTracking) - helper Application.Current?.MainWindow is { } w && w.IsVisible && w.WindowState != System.Windows.WindowState.Minimized (WindowState precisa ser QUALIFICADO: System.Windows.WindowState - o arquivo nao tem o using). O pico de 1.87% a cada 30s some com o app no tray. _processProfiles so e usado por UpdateProcessProfiles/PruneDeadProcessProfiles/ApplyFireminOptimizations - gate seguro; GameBoost usa SetWinEventHook, nao o tracker.

**Quirk de ferramenta novo**: 
g -rn = -r n e REPLACE no ripgrep (nao recursive) - substitui o match por "n" na saida (arquivos intactos). Nunca usar -r com rg.

Build temp verificacao: GUI 0 erros / 108 avisos (baseline; app aberto bloqueia MSB3021 - compilar com app fechado).

**A TESTAR (app)**: com o kit no tray, Task Manager deve ficar ~0.1-0.3% estavel (sem pico de 1.87% a cada 30s; goodbyeDPI tooltip pode atrasar ate 10s); com a janela aberta, os perfis continuam atualizando normalmente (ProcessMonitorPage/GameBoost intactos); RAM Limiter continua trimando (intervalos de 2s em vez de 1s).
**DECISAO DO USUARIO (18/08, 2a rodada): GC.Collect RESTAURADO na navegacao.** Pedido: "pode manter a coleta de lixo o uso do processador so nao pode ser constante e sem descanso". O pico pontual de 14% ao navegar e aceitavel; o problema era o uso CONSTANTE (idle), ja resolvido pelos 3 fixes acima. MainWindow.CleanupAndNavigate: bloco pos-navegacao voltou a chamar GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, true) + GC.WaitForPendingFinalizers() + MemoryHelper.TrimWorkingSet() (gate 90MB + log "RAM devolvida apos navegacao" mantidos; MaxGeneration precisa ser QUALIFICADO como GC.MaxGeneration - sem using static). Build: 0 erros / 108 avisos (baseline). A testar: navegar Dashboard -> Services -> voltar com RAM caindo a ~60-80MB (agora com GC + trim), pico momentaneo de CPU na navegacao aceitavel.

### Sessao 18/08 (cont. 3) - AppsPage "Portateis / Detectados": scan rapido (1 passada + paralelo + skip + progresso)

Pedido do usuario: a aba que "procura apps/pastas pelo tamanho" estava lenta - dava para usar o indexador USN? **Veredito**: NAO - o `NativeUsn` (USN/MFT) guarda so NOMES de arquivos/pastas (`FindFileDirectories`); registros USN nao tem tamanho de arquivo. O gargalo real era o `PortableAppScanner.AnalyzeFolder`: 5 varreduras recursivas completas POR PASTA (exe top, exe all, tamanho, dll, allFiles).

**Otimizado (KitLugia.Core\PortableAppScanner.cs)**:
1. **Passada unica DFS** (Stack<(dir, depth)>) acumulando tudo: exeTopCount, nonInstallerExeCount, mainExe (maior nao-instalador por tamanho), totalBytes, dllCount, fileCount, hasUninsExe, hasConfigFiles. Confidence com os MESMOS thresholds do original (dll>=3 +30/>=1 +15; exe>=1 && files>=10 +20; >=10MB +15/>=5MB +10; unins* -30; config +10; exeTop==1 +10; <1MB ou sem exe nao-instalador -> null; clamp 0..100; <30 -> null; installedPaths -> null).
2. **Skip de subarvores gigantes**: `_subtreeSkipNames` (node_modules, .git, .svn, .vs, packages, Package Cache, cache, Caches, Logs, logs, Temp, IsolatedStorage, $Recycle.Bin, System Volume Information, Config.Msi, Windows, System32, SysWOW64, ProgramData, Program Files (x86), Recovery, MSBuild, Microsoft.NET, Assembly, MicrosoftEdge, Temporary Internet Files, junctions classicas do AppData) + nomes com ponto inicial + `MaxDepth = 10`.
3. **EnumerationOptions nativo** (sem syscall por item): `IgnoreInaccessible=true` (mata try/catch), `AttributesToSkip = Hidden | System | ReparsePoint` (junctions nem entram na enumeracao - ciclos impossiveis; semantica identica ao GetFiles original que ja pulava hidden/system) + `RecurseSubdirectories=false`.
4. **Parallel.ForEach** sobre as pastas raiz com `lock(gate)` nos resultados; `OrderByDescending(Confidence).ThenByDescending(TotalSizeBytes)`.

**GUI (AppsPage.xaml.cs LoadPortableApps)**: callback de progresso -> `Dispatcher.Invoke` -> `TxtPortableProgress.Text = "Varrendo pastas... {done}/{total}"` (elemento ja existia).

**TESTADO (harness, host, 18/08)**: antes 71s com EnumOpts preliminar -> **42,7s** com EnumOpts final (233 -> 224 apps; resto do tempo = I/O puro das pastas gigantes de D:\ - 72GB/44GB/28GB - impossivel mais rapido sem MFT que nao tem tamanhos). Resultados corretos: RobloxStudioBeta (Fishstrap), Ollama, Opera GX (browser_assistant), xenia_canary, rclone (SteaMidra) etc. Tamanhos agora EXCLUEM subarvores pesadas (node_modules/cache/...) - menores que antes, aceito (scan muito mais rapido). Build solucao: 0 erros / 108 avisos (baseline).

**A TESTAR (app)**: abrir aba Portateis / Detectados -> progresso "Varrendo pastas... N/total" em tempo real -> lista final ~30-40s (antes varios minutos) com os tamanhos corretos; ordenacao por confianca depois tamanho.

### Sessao 18/08 (cont. 4) - Portateis/Detectados: FindFirstFileExW nativo (tamanho inline) 42,7s -> 30,2s

Pedido do usuario: "procure na web se ah mais coisas que de para olhar para otimizar isso".

**Pesquisa web (achados)**:
1. **FindFirstFileExW + FindExInfoBasic + FIND_FIRST_EX_LARGE_FETCH**: o WIN32_FIND_DATA ja traz o TAMANHO embutido na enumeracao - o .NET `Directory.EnumerateFiles` descarta isso e o `FileInfo.Length` faz 1 syscall EXTRA por arquivo (~2M+ arquivos em D:). O .NET nao usa LARGE_FETCH (buffer grande, menos round-trips). Blog Sebastian Schoner 2024: 54,7s -> 27,8s (~2x) em HDD com 7 threads; SO post: 661s -> 11s (60x). Fonte: devblogs.microsoft.com/oldnewthing/20111226-00/?p=8813 (metadados de diretorio podem ser stale).
2. **MFT raw (WizTree/Everything)**: ler $MFT direto da nome+tamanho+parent de tudo numa passada sequencial (segundos por volume) - requer admin + parser NTFS complexo. O kit tem rust_native.dll (reg_scan_ffi) - candidato a Fase 2. docs.rs/disktree/latest/disktree/mft (DEFAULT_READERS/calibrate_readers, FIRST_ORDINARY_RECORD, ROOT_RECORD).
3. NtQueryDirectoryFileEx com buffer gigante - alternativa mais complexa; FindFirstFileExW cobre o ganho.

**Implementado (KitLugia.Core\PortableAppScanner.cs)**:
1. **P/Invoke nativo** `FindFirstFileExW` (FindExInfoBasic=1, FindExSearchNameMatch=1, FIND_FIRST_EX_LARGE_FETCH=2) + `FindNextFileW` + `FindClose`; struct `Win32FindData` (LayoutKind.Sequential, CharSet Unicode, **Pack=4** - longs alinhados a 4 p/ bater com o nativo: dwFileAttributes@0, ftCreationTime@4, nFileSizeHigh@0x1C, cFileName@0x2C/260, cAlternateFileName@0x234/14). `EnumerateDirNative(dir)` -> IEnumerable<(Name, Size, IsDir, LastWriteFileTime)> com handle try/finally FindClose.
2. **Atributos filtrados no nativo**: `SkippedAttributes = Hidden|System|ReparsePoint` (0x2|0x4|0x400) via `(dwFileAttributes & SkippedAttributes) != 0` - preserva a semantica do antigo `AttributesToSkip` (junctions nem entram, ciclos impossiveis). `EnumerationOptions EnumOpts` REMOVIDO (dead code apos a troca).
3. **AnalyzeFolder reescrito**: 1 passada DFS com EnumerateDirNative; `totalBytes += size` direto do WIN32_FIND_DATA (SEM `new FileInfo` por arquivo - elimina 1 stat/file); `mainExePath = Path.Combine(dir, name)`; `.` e `..` pulados no enumerador.

**TESTADO (harness, host, 18/08)**: mesmo resultado (224 apps identicos), **42,7s -> 30,2s** (~30% mais rapido, 1 syscall/file eliminado + LARGE_FETCH). Build Core: 0 erros / 0 avisos. Resto do tempo = I/O puro das pastas gigantes de D:\. Harness removido.

**A TESTAR (app)**: abrir aba Portateis / Detectados -> lista final ~30s com os mesmos apps; progresso "Varrendo pastas... N/total" em tempo real.

**PROXIMO PASSO (opcional, Fase 2 - MFT raw)**: estender rust_native com scanner MFT estilo WizTree/disktree (nome+tamanho+parent de tudo em segundos, exige admin, parser NTFS complexo) - perguntar ao usuario se quer antes de implementar.

### Sessao 18/08 (cont. 5) - Portateis/Detectados: Fase 2 MFT raw COMPLETA (rust mft_scan_ffi + fallback, paridade 100%)

Pedido do usuario: "use o rust native se puder" (o PROXIMO PASSO da sessao anterior foi aprovado). O scanner agora
e **MFT-first com fallback por local/volume** - sem o Everything externo, so o kit.

**Rust (rust_native\src\mft.rs, novo, ~890 linhas)**:
1. Abre `\\.\C:` (CreateFileW admin, GENERIC_READ, share RW) -> FSCTL_GET_NTFS_VOLUME_DATA (MFT LCN/MFT zone) -> `FSCTL_GET_RETRIEVAL_POINTERS` do `$MFT` -> le o arquivo em chunks de 16MB (8MB de buffer duplo) -> parse dos 5.126.240 records reais (C: 6,5GB MFT). Pula records invalid/sem FILE_NAME/indices (0x02/0x04/0x05, base ref != 0 = hard links duplicados; `ReadMftEntry` valida base 0; +2 on dir 0x02, +1 on FILE 0x00, 0x10 e 0x80 contam 1x).
2. `FILE_NAME` value layout confirmado: parent@0x00, lastmod@0x10, realsize@0x30, flags@0x38, name_len@0x40, name@0x42 (min value 0x42). Nomes em UTF-16LE, pool de bytes com offsets, **zero alocacao de String durante o scan** (identico ao NativeUsn).
3. **Resolucao de prefixo**: BFS do record raiz (rec 5) acumulando o nome relativo; resolve ate 3 prefixos por volume (`prefixes` = ';'-separated com '/' e NUL-terminated; entrada vazia = volume inteiro, vira rec 5). Falha de resolucao por permission/loop -> rec 0xFFFFFFFF. BFS com max 8M records + max 16MB de nome acumulado.
4. **Blob de saida**: `[u32 prefix_count][u32 x prefix_recs][entry: u64 rec | u64 parent | u64 size | u64 last_write | u16 flags | u16 name_len | u16 name...]` - so rec >= 16, ja ordenado por (parent, rec). Flags: 0x1 dir | 0x2 reparse | 0x4 hidden/system (normalizado de FILE_ATTR_*).
5. **FFI**: `mft_scan_ffi(volume_root:*const u16, prefixes:*const u16, out_buf:*mut u8, out_capacity:i32) -> i32`. rc>=0 = bytes escritos; rc<=-1000 -> buffer pequeno, `needed = -rc - 1000`; -1 args, -2 CreateFile (nao-admin), -3 nao-NTFS, -4 geometry, -5 record0/$DATA, -6 read/overflow, -7 zero records.
6. 3 testes passam (`scan_real_c`, `buffer_too_small`, `nonexistent_volume`), zero debug output. cargo build --release OK (linker MSVC so stdout benigno).

**C# (KitLugia.Core\NativeMft.cs, novo)**: `MftFlags` (Directory 0x1/Reparse 0x2/HiddenSystem 0x4), `MftEntry` (readonly struct), `MftIndex` (CSR: recordCount = max(Rec,Parent)+1; cnt[parent+1]++; `Starts[rc+2]`; `RecToIdx` via Array.Fill(-1); filhos de r = [Starts[r+1], Starts[r+2])), `MftVolumeResult` (VolumeRoot/VolumeFailed/ErrorCode/Entries/PrefixRecs paralelo a Locations/Index), `NativeMft.ScanAllVolumes` (agrupa por Path.GetPathRoot, `Parallel.ForEach` por volume - sem estado global no Rust, chamadas concorrentes seguras), `TryScanVolume` (buffer 1MB, loop de crescimento `rc<=-1000 && tries<8`, `cap = max(needed, cap*2)`, DllNotFound/BadImage/EntryPoint -> null), `ParseBlob` (guard de tamanho no nome). DLL copiada automaticamente pelos csproj (Core L49, GUI L85-89).

**PortableAppScanner.cs integrado (MFT-first com fallback)**:
1. `Scan()`: GetInstalledProgramPaths + GetScanLocations 1x -> `NativeMft.ScanAllVolumes` -> por local: volume falhou / prefix rec == 0xFFFFFFFF -> `CollectClassicCandidates` (Directory.GetDirectories, fallback); senao candidatos = filhos do rec do prefixo (skip flags reparse|hidden-system, nomes com '.', `_excludedFolderNames`, installedPaths, dedup por pasta). Classic vira `ScanClassic` de fato (corpo antigo movido p/ fallback).
2. `AnalyzeFolderMft(dirRec, folderPath, index, installedPaths)`: DFS com Stack<(Rec, Depth, RelDir)> - profundidade = nivel do DIR enumerado (start (dirRec, 0)); skip reparse|hidden-system p/ dirs E arquivos (igual EnumerateDirNative SkippedAttributes); skip `_subtreeSkipNames` + nomes com '.' em dirs; depth > MaxDepth(10) pula o dir inteiro; exeTopCount = depth==0; mainExe = maior nao-instalador com relDir acumulado (`Path.Combine(folderPath, relDir, name)`).
3. `BuildPortableEntry(...)` extraido (helper compartilhado classic/MFT) - confidence IDENTICA (thresholds da sessao anterior, installedPaths -> null, clamp 0..100, <30 null, appName do mainExe).
4. `LastModified` MFT: `DateTime.FromFileTimeUtc((long)LastWrite).ToLocalTime()` do proprio entry do candidato (RecToIdx) - parity com `DirectoryInfo.LastWriteTime` (local) do classic; fallback Directory.GetLastWriteTime se RecToIdx < 0.

**TESTADO (harness elevado, host, 18/08)**: MFT-first **224 apps em 15,8s** (inclui leitura MFT fria de C: + D: em paralelo, ~8s+); classic de referencia 4,1s (cache quente; frio era 30,2s). **PARIDADE 100%**: 0 missing, 0 extra, 0 mismatch de confidence/size/name (224 = 224). Top: KitLugia.GUI(85), AlterarCMD(85), RobloxStudioBeta(75), browser_assistant(75). Build Core: 0 erros / 0 avisos. Harness removido.

Flags: blob flags 0x1/0x2/0x4 (NAO sao os Win32 0x2/0x4/0x400 - Rust normaliza); entrada vazia no prefixos = volume inteiro (rec 5); rec 5 NAO vai pro blob (so rec>=16 - candidatos sao filhos de recs validos, sempre no blob); MFT precisa de admin (rc=-2 -> fallback classic automatico, por volume); entradas ja ordenadas por (parent, rec) - sem sort no C#.

**A TESTAR (app)**: aba Portateis / Detectados -> lista com os mesmos 224 apps (paridade garantida pelo harness) em ~15s na 1a vez (leitura MFT) e progresso "Varrendo pastas... N/total"; sem admin o fallback classic roda sozinho (lista identica).

### Sessao 18/08 (cont. 6) - Portateis/Detectados: logs de diagnostico + 2 bugs reais (LastModified DNF + RAM do blob)

Relato do usuario (app Debug, 17:29): "adicione mais logs, o kit travou em um momento e o scan foi mais
rapido mas parece mostrar menos resultados". Log do depurador: `DirectoryNotFoundException` x6 +
`FileNotFoundException` (WinRT.Runtime) x4. RAM 736MB com limpeza de 8MB (RAM Limiter trima o proprio
processo). Scan roda em Task.Run (thready verificado em AppsPage.xaml.cs:1186 - UI NAO congela pelo
scan; `_portableCts` e cancelado mas NUNCA passado ao Scan - token inocuo, sem resultados parciais).

**BUG 1 (menos resultados - DirectoryNotFoundException x6)**: `AnalyzeFolderMft` fazia
`new DirectoryInfo(folderPath).LastWriteTime` INCONDICIONALMENTE ANTES do fallback MFT
(RecToIdx) - pasta que sumiu entre o snapshot MFT (~15s) e a analise (instaladores temp,
mudancas de arquivos) lancava DNF -> catch engolia -> candidato descartado. Classico sofria
igual (GetDirectories -> LastWriteTime com pasta deletada no meio). CORRECAO: RecToIdx/MFT
LastWrite PRIMEIRO (sem tocar o filesystem); fallback `Directory.Exists(folderPath)` +
DirectoryInfo so quando RecToIdx < 0 (classico idem com guard de Exists; senao DateTime.MinValue).

**BUG 2 (freeze + 736MB - blob duplicado em byte[])**: `ParseBlob` copiava o blob FFI inteiro
via Marshal.Copy para `byte[]` (ate ~250MB por volume; C: = uniao de LocalAppData+Roaming+
Desktop+Downloads+Documents, D: = volume inteiro) - pico de RAM 736MB + trim do RAM Limiter
= storm de page faults = travada. CORRECAO: ParseBlob le DIRETO do IntPtr (ReadU32/ReadU64/
ReadU16 com Marshal.ReadInt* + Marshal.PtrToStringUni) - SEM copia do blob; `using System.Text`
removido (Encoding.Unicode.GetString nao e mais usado).

**LOGS NOVOS (pedido do usuario)**:
- NativeMft.TryScanVolume: falha loga `[MFT] Volume X: FALHOU rc=N apos N tentativa(s)` (por que
  caiu no fallback classic); sucesso loga `[MFT] Volume X: OK rc=N, blob N.N MB, N entradas,
  prefixos [..]` (rec por local ou ? nao-resolvido).
- PortableAppScanner.Scan: `[Portatil] Scan iniciado: N local(is)`, `Scan MFT: N volume(s) em Ns`,
  por local `-> MFT (prefixo rec N)` ou `-> scan classico (motivo)` (sem dados MFT / volume falhou
  rc=N / prefixo nao resolvido), `N candidato(s) em Ns`, `N app(s) detectado(s) em Ns`.
- GUI (AppsPage.LoadPortableApps): TxtPortableProgress inicia com "Lendo indice MFT dos volumes
  (1a vez ~15s)..." - o usuario ve que NAO travou durante a leitura da MFT.

**A TESTAR (app)**: abrir aba Portateis -> progresso "Lendo indice MFT..." ~15s -> logs
[MFT] nos dois volumes (prefixos resolvidos) -> lista ~224 apps SEM DirectoryNotFoundException
no depurador; RAM sem pico de 736MB (blob sem copia); "menos resultados" deve sumir (pastas
que sumiram nao descartam mais os candidatos).

---

### Sessao 20/08 - Force Stop Unlock: REESCRITO COMPLETO (detecao de drivers, delecao robusta, menu de contexto)

**Problema original**: O Force Stop Unlock nao conseguia detectar nem descarregar o driver WinDivert (usado pelo goodbyedpi). O usuario colava a pasta e o Kit dizia "Nenhum bloqueador encontrado" mesmo com o goodbyedpi.exe rodando e o WinDivert64.sys carregado.

**Causa raiz (identificada via logs)**:
1. WinDivert e registrado como servico Win32 (Type=16), NAO como kernel driver (Type=1)
2. O SCM enum so encontrava servicos por ImagePath, mas o nome do servico era "WinDivert1.4" (nao "WinDivert64")
3. Os fallbacks (sc query, registry scan, loaded driver list) so rodavam quando results.Count == 0, mas o SCM enum retornava 0 bytes e parava antes
4. O Unlock retornava cedo apos Restart Manager success, sem tentar descarregar drivers

**Correcoes aplicadas**:

#### KitLugia.Core/DriverUnlockService.cs
- `ServiceMatchesSysFiles()`: Matching fuzzy — "WinDivert1.4" casa com "WinDivert64.sys" (com/sem digitos, substring)
- `GetAllServiceNames()`: Usa `sc query state=all` (sem filtro de tipo) para pegar TODOS os servicos
- `FindDriversViaScQuery()`: Agora busca todos os servicos, nao so `type=driver`
- `FindDriversViaRegistry()`: Removeu filtro `startType > 2` e `isDriverLike`, usa matching fuzzy
- Fallbacks rodam SEMPRE (nao mais condicionados a `results.Count == 0`)
- SCM enum: Usa `ServiceMatchesSysFiles` alem do match por ImagePath

#### KitLugia.Core/ForceStopUnlockService.cs
- `FindViaNativeHandles()`: Enumeracao nativa de handles via NtQuerySystemInformation — encontra handles File, Section (memoria mapeada), Key. Sem depender de handle64.exe
- `GetHandleName()` / `GetHandleType()`: Query nativa do nome e tipo de cada handle via NtQueryObject
- `FindBlockingProcesses()`: Fluxo 4 etapas — Restart Manager -> Native Handles -> handle64.exe (fallback) -> Driver scan
- Unlock 7 fases: RM shutdown -> Kill processos da pasta -> Descarregar drivers (SCM/NtUnloadDriver/sc stop+delete) -> Fechar handles -> Delecao robusta -> Kill restantes
- `RobustDeleteFile()`: 6 metodos de delecao: File.Delete, cmd del, NtSetInformationFile, rename+delete, MoveFileEx, reboot delete
- `RobustDeleteWithRetry()`: Repete 3x com 1.5s delay entre tentativas
- `RobustDeleteFolder()`: Deleta todos arquivos recursivamente, depois diretorios vazios
- Unlock nao retorna mais cedo apos RM — sempre continua para driver unload e delecao

#### KitLugia.GUI/Pages/WindowsSettings/ForceStopUnlockPage.xaml.cs
- ListFolderContents(): Mostra todos arquivos com tamanho, data, e marca .sys como [DRIVER]
- Logging detalhado em cada operacao (admin status, folder contents, delete result)

#### KitLugia.GUI/External/ForceStopUnlock/AddContextMenu.reg
- Atualizado de `powershell.exe ... Unlock-File.ps1` para `KitLugia.GUI.exe --unlock "%1"`
- Publish/External/ForceStopUnlock/AddContextMenu.reg tambem atualizado

**Testes realizados (todos passaram)**:
1. Servico WinDivert1.4 registrado + RUNNING -> SCM stop+delete + delecao de 22/22 arquivos
2. Servico marcado para delete (STOP_PENDING) -> Registry scan fallback + kill + delecao
3. Sem servico registrado -> Native handles + RM + kill + delecao

4. IPC via menu de contexto -> `KitLugia.GUI.exe --unlock` -> Named Pipe -> ForceStopUnlockPage
5. Toggle on/off/on -> Registry corretamente atualizado
6. DLL lock detection -> Restart Manager encontra processos com handles em .dll files

**Tipos de arquivo suportados**:
- `.sys` (drivers kernel) — SCM stop/delete, NtUnloadDriver, sc query/stop/delete
- `.dll` (bibliotecas) — Restart Manager, handle64.exe, Native handles, Process Kill
- `.exe` (executaveis) — Restart Manager, handle64.exe, Process Kill
- Qualquer arquivo — 6 metodos de delecao com retry

**Fluxo completo do menu de contexto**:
```
Explorer -> Clique direito -> "Force Stop Unlock"
  -> KitLugia.GUI.exe --unlock "path"
  -> Kit ja rodando -> IPC via Named Pipe
  -> ForceStopUnlockPage abre com path preenchido
  -> Auto-analise -> bloqueadores encontrados
  -> Unlock: RM -> Kill -> SCM/NtUnloadDriver -> Delecao robusta
```

**Arquivos modificados**:
- `KitLugia.Core/DriverUnlockService.cs` — Matching fuzzy, fallbacks, logging
- `KitLugia.Core/ForceStopUnlockService.cs` — Native handles, robust deletion, unlock flow
- `KitLugia.GUI/Pages/WindowsSettings/ForceStopUnlockPage.xaml.cs` — Logging, folder listing
- `KitLugia.GUI/External/ForceStopUnlock/AddContextMenu.reg` — Updated command
- `Publish/External/ForceStopUnlock/AddContextMenu.reg` — Updated command

---

### Revisão 20/08 - Análise Crítica: Partições, WinPE, WinBoot e ISO Editor

**Escopo**: Revisão completa de PartitionManager.cs (1732 linhas), WinpeBuilder.cs (1843 linhas), WinbootManager.cs (7224 linhas), IsoEditorPage.xaml.cs (1199 linhas).

**Conclusão: PROJETO BEM IMPLEMENTADO** — não há brechas críticas.

#### Abordagem Híbrida Correta
- **IOCTL nativo** para enumeração de discos (milissegundos, sem WMI)
- **Storage Management API (MSFT_*)** para operações de shrink (oficial Microsoft)
- **diskpart** para create/format/extend/delete (confiável, ferramenta Microsoft)
- **wimlib** para manipulação WIM (mais rápido que DISM, sem montagem)
- **oscdimg** para geração de ISO (Microsoft embutido)
- **7z** como fallback para extração

#### PartitionManager.cs
- ✅ GetAllDisksViaIoctl(): IOCTL_DISK_GET_DRIVE_LAYOUT_EX — enumeração em milissegundos
- ✅ ShrinkPartitionUsingStorageAPI(): MSFT_Partition.Resize — API oficial Microsoft
- ✅ DeletePartition(): Safety checks (bloqueia C: e disco do sistema)
- ⚠️ Create/Format/Extend/Delete usam diskpart — aceitável, mas Storage API pode ser usada no futuro

#### WinpeBuilder.cs
- ✅ Pipeline sem ADK — usa wimlib + oscdimg embutidos
- ✅ wimlib como prioridade (1-2s vs 30s com DISM)
- ✅ Download do WinPE base com cache persistente
- ⚠️ URL hardcoded para GitHub release — mitigado por cache local

#### WinbootManager.cs
- ✅ ISO Mount/Dismount nativo (PowerShell Mount-DiskImage)
- ✅ DISM como fallback para WIM (quando wimlib indisponível)
- ✅ bcdedit para boot config (ferramenta Microsoft)

#### ISO Editor
- ✅ Modo 100% nativo: wimlib + registro offline, SEM DISM
- ✅ Listar edições: wimlib info (sem montar)
- ✅ Registry tweaks: extract hive → reg load → add → unload → re-inject
- ✅ AppX bloat: wimlib dir + update delete
- ✅ Otimização: wimlib optimize
- ✅ Fallback 7z para extração

#### Segurança
- ✅ DeletePartition bloqueia partição do sistema
- ✅ Timeouts configuráveis por operação
- ✅ Logging detalhado de cada etapa
- ✅ Retry automático em caso de falha

**Detalhes completos**: docs/CRITICAL_REVIEW_PARTITION_WINPE_ISO.md

---

### Atualização 20/08 - ISO Editing: DISM → wimlib (métodos 2026)

**Problema**: WinbootManager.cs usava `dism.exe /Get-WimInfo` e `/Get-ImageInfo` para detectar idioma e listar edições da ISO. O DISM é mais lento e requer montagem da ISO.

**Solução**: Substituído por `wimlib-imagex info` (versão 1.14.5, janeiro 2026):
- Mais rápido (1-2s vs 10-30s com DISM)
- Não requer montagem da ISO
- Já estava embutido no Kit mas não era usado nestas funções

**Métodos atualizados**:
1. `DetectLanguageFromDrive()` — wimlib info como prioridade, DISM como fallback
2. `GetIsoEditions()` — wimlib info com parse de output, DISM como fallback
3. Adicionado `ParseWimlibInfoValues()` para parsing do output wimlib

**Versão wimlib embutida**: 1.14.5 (latest, janeiro 2026)
- Suporta ARM64 (experimental)
- Compressões LZX/LZMS otimizadas
- Deduplication automática
- Suporte a ESD (Electronic Software Download)

**Arquivos modificados**:
- `KitLugia.Core/WinbootManager.cs` — DetectLanguageFromDrive, GetIsoEditions, ParseWimlibInfoValues

---

### Sessao 20/08 - OOShutUp Windows 11 Update

**Problema**: As configurações de privacidade do OOShutUpManager eram baseadas no Windows 10. Muitos tweaks não funcionavam no Windows 11 24H2/25H2/26H1.

**Causa raiz**: 
- 12 configurações Edge Legacy (removido no Win11)
- Wi-Fi Sense (removido no Win10 1803)
- People Bar (removido no Win11 22H2+)
- Meet Now (removido no Win11)
- Muitas configurações Cortana (agora app separado no Win11)
- Falta de configurações Win11-specific (Recall, Copilot, Widgets)

**Correções aplicadas**:

1. **Removidas 12 Edge Legacy settings** — Edge Legacy não existe no Win11
2. **Removida Wi-Fi Sense** — removido desde Win10 1803
3. **Removidos People Band e Meet Now** — removidos no Win11 22H2+
4. **Renomeadas categorias Cortana** → "Busca & Voz" (Cortana agora é app separado)
5. **Corrigido DODownloadMode** — valor 0→1 (Off→LAN only no Win11)
6. **Adicionadas 30+ configurações Win11-specific**:
   - Windows Recall (DisableAIDataAnalysis, DisableRecallSnapshots, DisableRecallContentIndexing)
   - Copilot Runtime, Copilot no File Explorer
   - IA no Paint, Photos, Notepad, Edge
   - Windows Spotlight (DisableWindowsSpotlightFeatures, RotatingLockScreen)
   - Widgets (AllowNewsAndInterests)
   - SmartScreen do Explorer
   - Sugestões no Configurações
7. **Removidas duplicatas** (Widgets duplicado, Sugestões de Apps duplicado)
8. **Total de configurações**: 130 → 176 (59% mais)

**Resultado testado**:
- 176 configurações em 22 categorias
- 102 configurações no preset Recommended
- Todos os caminhos de registro verificados no Win11 26H1
- UI dinâmica carrega automaticamente novas categorias

**Arquivos modificados**:
- `KitLugia.Core/OOShutUpManager.cs` — Atualização completa para Win11

**Testes realizados**:
- Verificação de caminhos de registro no Win11 26H1 (Build 28000)
- Contagem de configurações por categoria
- Verificação de serviços (DiagTrack, dmwappushservice, lfsvc)
- Build 0 erros em ambos os projetos

---

### Sessao 20/08 - TweaksPage Hardware-Aware Update

**Problema**: TweaksPage faltava tweaks que requerem pesquisa ao hardware para serem aplicados corretamente.

**Novos hardware-aware tweaks adicionados**:

1. **L3 Cache (ThirdLevelDataCache)** — Auto-detecta L3 do CPU via WMI e configura no registro. Diferente do L2, o L3 é compartilhado entre todos os núcleos.
2. **NVIDIA PowerMizer Max Performance** — Força GPU NVIDIA a operar em clocks máximos permanentemente (PerfLevelSrc=0x2222, PowerMizerLevel=1).
3. **NVMe Latency (D3Handoff)** — Otimiza latência de drives NVMe (D3Handoff=1, AllowIdle1InD3=0). Auto-detecta drives NVMe.
4. **GPU DPC Latency (IRQ Priority)** — Configura IRQ8Priority=1 para prioridade de interrupção do relógio.
5. **Memory Prioritization** — LargeSystemCache=1 + DisablePagingExecutive=1 (mantém kernel em RAM).

**Adicionados também**:
- DetectNvMeDrives() — detecta drives NVMe via WMI
- DetectPrimaryGpuVendor() — detecta vendor da GPU (NVIDIA/AMD/Intel)
- AMD Anti-Lag toggle
- Intel Dynamic Tuning toggle
- Boot Log toggle (bcdedit)
- NoGuiBoot toggle (bcdedit)

**Arquivos modificados**:
- `KitLugia.Core/SystemTweaks.cs` — Novos métodos hardware-aware
- `KitLugia.GUI/Pages/TweaksPage.xaml` — Novos toggles na UI
- `KitLugia.GUI/Pages/TweaksPage.xaml.cs` — Novos handlers e status loading

**Build**: 0 erros em ambos os projetos

### Sessao 20/08 - Glassmorphic Design System

**Problema original**: Botões da sidebar tinham hover simples (cinza suave + scale 1.02), sem personalidade.

**Solucao**: Glassmorphic com 3 camadas — Glow Border + Shine Sweep + Glass BG.

**Fixes aplicados**:
1. `RemoveStoryboard` antes de cada `BeginStoryboard` — previne conflitos em hover rapido
2. `FillBehavior="Stop"` — impede que storyboards segurem valores apos terminar
3. Removido `ScaleTransform` do hover — causa re-layout que gera stutter
4. `TranslateTransform` para shine bar — move pixels sem re-layout

**Componentes atualizados**:
- `NavButtonStyle` — 14 RadioButtons da sidebar
- `ModeButtonStyle` — botões de modo
- BtnGoodbyeDPI — glow dourado
- BtnBackgroundMonitor — glow dourado + badge
- BtnUpdate — glow azul + bg tint azul
- BtnConsole — glow verde + bg tint verde
- BtnNotifications — glow branco + badge

**Arquivos modificados**:
- `KitLugia.GUI/Themes/NavStyles.xaml` — estilos glassmorphic
- `KitLugia.GUI/MainWindow.xaml` — 5 top buttons atualizados

**Documentacao**: `docs/GLASSMORPHIC_DESIGN.md`

**Build**: 0 erros

---

### Sessao 21/08 - Glassmorphic v4 + Top Bar Fix + WinBoot Speed

**Problema**: Ícones no topo cortados, animação engasgava no hover rápido, WinBoot lento.

**Correções:**

1. **Top Bar Icons** — `BtnIntegrity` (🛡️) trocado de `NavButtonStyle` (Height=42, com texto) para template inline compacto (Height=32, icon-only). StackPanel `VerticalAlignment="Center"` + `Margin="0,0,8,0"`.
2. **Glassmorphic v4** — Removida a diagonal shine sweep (causava flicker no hover rápido). Substituída por fade simples: border glow + background tint com 0.2s. Sem `RemoveStoryboard`, todos `HoldEnd`. WPF auto-prioriza animações.
3. **Top bar buttons** — Reescritos todos 6 botões do topo (GoodbyeDPI, Integrity, Monitor, Update, Console, Notifications) com template limpo e simplificado.
4. **WinBoot Speed**:
   - `IdentifyIsoType`: 7zip detection PRIMEIRO (rápido, ~1-2s), mount como fallback
   - `CreateBootPartition`: VDS somente em safe mode, delays reduzidos (2000→1000ms, 1000→500ms), `GetDisks()` cacheado para evitar múltiplas chamadas

**Arquivos modificados:**
- `KitLugia.GUI/MainWindow.xaml` — Top bar reescrito
- `KitLugia.GUI/Themes/NavStyles.xaml` — v4 clean fade
- `KitLugia.Core/WinbootManager.cs` — WinBoot optimizations

**Build**: 0 erros

### Sessao 21/08 — KIT ISO STUDIO Expansor + Legibilidade + Cobertura total

**1. Studio cobre o kit inteiro (como PathManager do Guardian):**
- Antes OverlayIsoStudio era Border dentro da Page (Grid.RowSpan 3 Margin -30 Max 960x740) — ficava so no Frame (Column 1), sidebar Sistema ainda visivel, com Background #EE000000 semi + DropShadow Blur 30 borrado (print do usuario).
- Agora KitIsoStudioWindow.xaml (novo Window igual a PathExplorerWindow.xaml): Height 740 Width 1060 CenterOwner Background #0F0F0F Border #FFD700 WindowChrome Caption 0. Botao ESTUDIO em IsoEditorPage.xaml:146 abre new KitIsoStudioWindow { Owner = MainWindow }.ShowDialog() — cobre o kit inteiro (sidebar + header + console) como o PathManager, nao so o Frame.

**2. Legibilidade — o que foi feito (reusavel a qualquer hora):**
- Fundo opaco: Background #EE000000 (93% opaco, deixava kit atras aparecer borrado) -> #FF0A0A0A opaco (ou #0F0F0F na Window) — elimina transparencia que causava blur de fundo.
- Sem DropShadow no conteudo: removido DropShadowEffect BlurRadius 30 ShadowDepth 15 Opacity 0.6 do Border interno — era o principal borrado de texto em FontSize 10-11 Consolas.
- Texto nitido: TextOptions.TextFormattingMode="Display" + SnapsToDevicePixels="True" no Window/Border raiz — forca ClearType em Segoe UI Variable e desativa sub-pixel blur do WPF em 96dpi.
- Contraste solido: Foreground #CCC/#FFFFFF em vez de #88FFFFFF semi, Border #2A2A2A solido, sem Opacity 0.6 em TextBlock.
- Tamanhos: FontSize 10.5 -> 11 em CheckBox + Padding 8 + CornerRadius 8 para nao espremer.
- Reusar: copie o header do KitIsoStudioWindow.xaml:60 (Border BorderBrush #FFD700 + TextOptions.Display) para qualquer overlay futuro que precise caber muito.

Arquivos: KitLugia.GUI\Windows\KitIsoStudioWindow.xaml(.cs) (novo, 2 colunas: AppX granular, Drivers, OEM + Registro, Idioma, Branding), IsoEditorPage.xaml:146 (BtnIsoStudio) + .cs:Bind (BtnIsoStudio_Click abre Window), WinbootManager.cs cache + IOCTL ja documentado.

Build: 0 erros / 124 avisos (baseline) — Studio abre nitido e em tela cheia.

### Sessão 21/08 — Gerenciador de Tarefas do Kit (Super Force Stop) — Ícone no Topbar

**Motivação:** Log Force Stop 21:34 VMware mostrou o Kit escaneando `C:\Program Files\VMware\VMware Workstation` inteiro (maxDepth 3, 213 arquivos, 4 .sys) + 6 bloqueadores (2 RM + 4 drivers via Registry scan, Native 0xC0000004 fallback handle64 0) e deletando 184 arquivos via `RobustDelete` (`File.Delete` + `cmd del` para ROMs com Access denied). VMware sempre pendura no tray por drivers, e o Kit fechou *de verdade*.

**Pesquisa web + IDA:**
- **System Informer** (winsiderss/systeminformer, successor Process Hacker) — 10k stars, MIT, 16k commits, C/C++ + `phlib` + `KSystemInformer.sys` (kernel), `NtQuerySystemInformation(SystemProcessInformation=5, SystemHandleInformation=16)` + `NtQueryObject`, graphs, handle search, services, 100% open. Referência #1.
- **Process Hacker 2.39** — mesmo core sem driver, mais simples para estudar.
- **IDA Pro 9.0 em `taskmgr.exe` 25H2:** confirma `NtQuerySystemInformation` + `phnt_windows.h`, `SystemPerformanceInformation` para CPU, `GetProcessMemoryInfo` para RAM — igual ao `ForceStopUnlockService.cs:1076` já faz com `NtQuerySystemInformation` + `Restart Manager` + `DriverUnlockService`.

**Plano criado:** `docs/TASK_MANAGER_PLAN.md` com arquitetura 5 abas, comparativo Windows TM vs Kit, fluxo 7 fases (RM shutdown → kill pasta → sc stop/delete drivers → handle close → RobustDelete → kill restante), performance com `NtQuerySystemInformation` + `VirtualizingStackPanel Recycling` + `Job Object KillTree`.

**Implementado (v1 scaffold):**
- Topbar: `MainWindow.xaml` novo `StackPanel Left` com `BtnKitTaskManager` `📊` `Width 40 Height 32` no quadrado vermelho do print (`Grid.Row0 Column1 Left Margin 8`), `Click=BtnKitTaskManager_Click` abre `KitTaskManagerWindow {Owner=this}.Show()`.
- `Windows\KitTaskManagerWindow.xaml(.cs)` — Window 980x620 `Border #FFD700`, header `📊 KIT TASK MANAGER — Fecha de verdade`, search + `🔄 Atualizar` + `⚡ Force Stop Selecionados` (vermelho), `ListView` virtualizado `Nome/PID/RAM/Handles/Tipo/Caminho`, context menu `Matar / Matar Árvore (Job Object) / Force Stop / Abrir Pasta / Copiar`, footer com `Matar / Matar Árvore / Fechar`. Backend reusa `ForceStopUnlockService.FindBlockingProcesses/Unlock` (VMware-like) para fechar de verdade.

**Arquivos:** `docs/TASK_MANAGER_PLAN.md`, `Windows\KitTaskManagerWindow.xaml(.cs)`, `MainWindow.xaml` (topbar), `MainWindow.xaml.cs` (handler).

Build: 0 erros / 120 avisos.

### Sessão 24/08 — Task Manager v2 (Win11-like) + Refactor partials + Anti-crash + GUI geral

**Task Manager do Kit (`Windows/TaskManager/`)** — reconstruído no modelo do Gerenciador de Tarefas do Win11:
- **Ícones estáveis**: cache duplo (por path E por nome do processo); linhas agrupadas herdam ícone do 1º membro com ícone. Fim da piscada a cada refresh de 1s.
- **Busca global**: barra do topo filtra Processos + Serviços + Inicialização simultaneamente (debounce 250ms), combinando com os filtros locais de cada aba.
- **Aba Desempenho estilo Win11**: grade de cards (215px) com sparkline 38px animado por dispositivo; clique abre painel de detalhes rico. Dispositivos: CPU, Memória, cada Disco físico (PhysicalDisk), cada adaptador de rede, cada GPU.
- **GPU universal** (pesquisa FreeToken/LibreHardwareMonitor): cascata DXGI (`dxgi.dll` CreateDXGIFactory1 → EnumAdapters1 → GetDesc1 — funciona p/ NVIDIA/AMD/Intel/chinesas/WDDM qualquer) → nvidia-smi CLI → registro `HardwareInformation.qwMemorySize` (QWORD, sem saturação 4GB do WMI) → WMI. Novo `KitLugia.Core\TaskManager\GpuInfo.cs`.
- **% da GPU**: contadores PDH `\GPU Engine(*)\Utilization Percentage` (mesmo pipeline do taskmgr oficial, agregação MAX engine). FIX: falha única na init PDH setava `_gpuAvailable=false` PARA SEMPRE → agora retry com backoff (5s→15s→45s→2min) + fallback `nvidia-smi --query-gpu=utilization.gpu` throttled 2s.
- **Rede = ncpa.cpl**: fonte trocada para `NetworkInterface.GetAllNetworkInterfaces()`; filtro só conectados (`OperationalStatus==Up`) + blacklist pseudo-adapters (WAN Miniport, Wi-Fi Direct, Teredo, QoS Packet Scheduler, WFP MAC Layer etc.) + dedupe por descrição. Taxa via delta `GetIPStatistics().BytesSent+Received` (funciona p/ virtuais sem instância perfmon).
- **Virtualização correta**: WMI `VirtualizationFirmwareEnabled` MENTE quando hipervisor já roda. Agora P/Invoke kernel32 `IsProcessorFeaturePresent(PF_HYPERVISOR_PRESENT)` (equivalente CPUID leaf 0x1 ECX bit 31, mesma fonte do taskmgr) → "Ativado (hipervisor ativo)". Fallbacks: WMI HypervisorPresent → PF_VIRT_FIRMWARE_ENABLED.
- **Métricas ao vivo idênticas ao TM do Windows**: CPU (processos/threads/handles/uptime), Memória (comprometida/cache/disponível/pools paginado+não-paginado), Disco (leitura/escrita separadas por instância física + "Disco do sistema"/"Arquivo de paginação"), Rede (envio/recebimento + IPv4/MAC/link), GPU (VRAM dedicada GB+MB, compartilhada, driver+data, localização PCI).
- **Nome completo do processador** priorizado no card e no título do painel (sem truncamento).

**Refactor estrutura (aba Processos intocada)**:
- Backup pré-refactor em `Windows/TaskManager/_backup_pre_refactor/` (xaml + xaml.cs originais).
- Split em partial classes: `KitTaskManagerWindow.xaml.cs` (~1700 linhas: state, refresh, filtros, kill, detail), `KitTaskManager.Performance.cs` (~800: aba Desempenho completa), `KitTaskManager.Services.cs` (~330: Serviços + Inicialização).
- Serviços/Inicialização: toolbars com `TmToolButton`, context menus escuros `TmDarkMenu` (Iniciar/Parar/Reiniciar/Habilitar/Desabilitar/Abrir local/Pesquisar web), ordenação por clique no header igual aba Processos (setinha asc/desc), duplo-clique na Inicialização abre pasta.

**Anti-crash (7 fixes)**:
1. CRÍTICO: `Parallel.ForEach` 4 threads chamando SHGetFileInfo (shell32 NÃO é thread-safe) → AccessViolation derrubava o processo inteiro sem catch. Extração serializada + limite 48/lote.
2. Init não-bloqueante: Loaded fazia await sequencial de TUDO (processos+WMI+nvidia-smi+serviços+startup) = travada violenta ao abrir. Agora só processos primeiro; Desempenho/Serviços/Startup carregam em background sob demanda ao clicar na aba.
3. KillTree acessava `root.Handle` de processo protegido → Win32Exception/AV. Acesso dentro de try próprio com fallback Kill().
4. GetChildPids recursivo sem limites (ciclo pai↔filho = stack overflow / WMI storm). Substituído por BFS com maxDepth 6 / maxCount 512.
5. Timer do monitor de recursos era variável local — nunca parado ao fechar (timers fantasma). Agora campo `_resourceTimer` parado no Closing.
6. Tick de gráficos acumulava em máquina lenta → gate Interlocked (pula tick se anterior não terminou).
7. Handlers globais AppDomain.UnhandledException + TaskScheduler.UnobservedTaskException logam `[KIT TASK MANAGER] FATAL:` em vez de morrer sem rastro.

**MainWindow/GUI geral**:
- Topbar (min/max/close): ColorAnimation Enter/ExitActions ficava presa em cor intermediária se mouse saía durante a transição → triggers declarativos com Setter (estado final sempre garantido) + IsHitTestVisibleInChrome explícito.
- Hamburger: `_sidebarAnimating` ficava true pra sempre se janela perdesse foco durante animação (Completed não dispara) → timeout de segurança 400ms.
- Botão Update: rotação ficava torta em ângulo intermediário → FillBehavior.Stop + reset Transform.Identity.
- IntegrityPage: ItemsControl dentro de ScrollViewer DESLIGAVA virtualização (todos os itens materializados) → ListBox com VirtualizingStackPanel Recycling + CacheLength por página. Busca com debounce 250ms. ItemTemplate responsivo (colunas Auto + * MinWidth, botões nunca saem das bordas).
- ANTI-FLASH BRANCO (3 camadas): App.xaml.cs OnStartup sobrescreve SystemColors globais (Window/Control/Menu/AppWorkspace/Highlight/Info → tons escuros) — qualquer controle não estilizado nasce escuro; MainFrame Background Transparent → WindowBackground gradiente; área da lista IntegrityPage com ScrollViewer/Border/ListBox #111111 explícitos.
- Animações eternas matadas: barra progresso IntegrityPage (Forever no Loaded → controlada por código só durante scan), spinner Bloatware AppsPage (SetSpinnerRunning liga/desliga), pulso DropShadowEffect GameBoostPage (BlurRadius animado = re-render GPU/frame → removido, pulso só Opacity), dot Live PartitionsPage (Start/Stop conforme visibilidade), pulse por-card BloatwarePage (dezenas de timelines → opacidade estática).

**Path Repair — CORRIGIDO (era farsa)**:
- `RepairPathEntries` antigo "mantinha" TUDO (duplicados, órfãos, lixo dev node_modules, sintaxe inválida, diretórios mortos) → botão CORRIGIR nunca mudava nada. Agora REMOVE de verdade: duplicatas (mantém 1ª, HashSet case-insensitive), diretórios inexistentes, lixo de desenvolvimento, resíduos de desinstalação, sintaxe inválida. Mantém: `.dotnet\tools` (cria pasta), WrongLocation (pode ser intencional). Verificado: REG_EXPAND_SZ preservado via DetectValueKind (%VAR% → ExpandString), serviços respeitam KitIntentionalServiceStart, BCD cache TTL 15s.

**Áreas críticas auditadas (Winboot/Partitions/WinPE)**:
- PartitionsPage: BtnDelete/BtnCleanDisk/BtnConvert chamavam diskpart SEM try/catch — exceção deixava overlay preso pra sempre. Todos com try/catch/finally agora. BtnRefresh com single-flight (_isUpdatingDisks).
- WinpeToolsPage: FURO DE SEGURANÇA — ShowBusy não travava navegação; usuário saía da página com `shutdown /r /t 10` agendado e o PC reiniciava "do nada". ShowBusy ativa MainWindow.IsNavigationLocked, ShowBusyResult libera.
- WinbootPage verificado sólido (try/catch completo, detecção idioma em background, polling com cache IOCTL 3s).
- Core verificado: DeletePartition bloqueia disco sistema/C:, CleanDisk IOCTL fast-path com fallback diskpart, RunProcessCaptured timeout kill tree, único GetAwaiter().GetResult() está dentro de Task.Run (sem deadlock).

Build final: 0 erros (solução inteira). Performance percebida pelo usuário: "velocidade aumentou MUITO, nem parece mais o kit antigo".

### Sessao 28/08 — KitStore "Microsoft Store remake": busca instantanea, icones, progresso modal, scroll

Frente "Microsoft Store remake" (abaixo do Menu de Contexto na WindowsPage / janela KitStore destacável).
Contexto: janela separada + polimento visual já entregues nas rodadas anteriores (27-28/08). Nesta
rodada os 3 pontos pendentes relatados pelo usuário: scroll travado, icones errados (Kimi→Opencode),
e dinamismo/visual inferior a MS Store (busca mostrava só o layout sem os apps de verdade).

1. **CAUSA RAIZ do scroll travado**: varios `ScrollViewer` aninhados (LvApps/SearchScroll/LvDownloads/
   LogScroll + MainScroll) cada um capturando o wheel para si sem nunca devolver ao MainScroll.
   Correção: método unico reutilizável `RouteWheel(e, outer, inner)` — o container interno rola
   enquanto tem conteúdo, e ao chegar no topo/fim repassa o restante ao MainScroll. Handlers dedicados
   p/ cada container (antes LvDownloads apontava errado pro handler do LvApps). `SearchScroll` ganhou
   `PanningMode=Both` + `CanContentScroll=False`.

2. **BUSCA INSTANTANEA (o salto real de dinamismo)** — antes cada termo spawnava `winget search`
   (500ms-2s). Agora a Store lê o proprio índice SQLite do winget:
   - O índice é `Public/index.db` (8.2 MB, 14.619 pacotes) DENTRO do `source2.msix` em
     `%LOCALAPPDATA%\Packages\Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\AC\INetCache\`. Técnica
     validada por pesquisa web (etducky.com/blog/winget-source-index + UniGetUI).
   - **`KitLugia.Core\KitStore\SqliteReader.cs`** (novo): leitor SQLite read-only ZERO dependência
     (não existe Microsoft.Data.Sqlite no projeto e não queria puxar). Percorre b-tree leaf/interior,
     decodifica varints + serial types. QUIRK CRÍTICO aprendido: células de b-tree table estao
     armazenadas em ordem DEScrescente de endereço (cell[0] no endereço mais ALTO — a 1ª célula era
     lida de tras pra frente); e o payload do record ocupa os ÚLTIMOS payloadLen bytes da célula
     (célula = payloadLen varint + rowid varint + record no fim). VALIDADO contra o índice real:
     14.168 linhas, `Mozilla.Firefox`/`Microsoft.VisualStudio` resolvem certo.
   - **`StoreEngine.FindLocalIndexDb` / `QueryWingetSearchLocal` / `EnsureLocalIndex`**: localiza o
     msix (OrderByDescending por LastWriteTime), extrai index.db p/ `%TEMP%\KitStoreIndex`, cacheia a
     lista em memória (16k apps: id/name/moniker/latest_version; publisher derivado do prefixo do id;
     moniker "None" → vazio). Fallback: CLI `QueryWingetSearch` antigo só se o índice faltar.
   - **Busca ao vivo com debounce 400ms**: `TxtSearch.TextChanged` → `ScheduleLiveSearch` →
     DispatcherTimer → `DoSearchAsync`. Digitar já mostra os cards (como MS Store), Enter ainda força.

3. **ICONES ERRADOS (Kimi → outro app) — causa raiz**: apps NÃO instalados (resultados de busca) não
   têm entrada no uninstall registry, então `TryResolveIconPath` retornava null e TODOS caíam numa
   `GetGenericIcon()` única e errada (o ícone "fantasma" repetido em todo mundo). Correção: fallback
   vira um **monograma-avatar** (`MakeMonogramIcon`): inicial do nome sobre cor estável derivada por
   FNV-1a 32 (determinístico entre execuções — `string.GetHashCode` é randomizado por processo, NÃO usar).
   Desenhado via DrawingVisual + RenderTargetBitmap (thread-safe em background) e Freeze(). Apps
   instalados continuam resolvendo ícone REAL pelo registry (Brave segue correto).

4. **PROGRESSO MODAL CENTRALIZADO (igual UpdatePage)**: o painel inline de progresso virou overlay
   fixo `InstallProgressPanel` (Grid `Grid.ColumnSpan=2` + `Panel.ZIndex=99` `#CC0D0D0D`), card 440px
   `#1E1E1E` com borda dourada, barra 22px `#FFD700`, status grande + porcentagem. `SetProgress` usa
   largura interna 388px (era 460 do inline).

5. **Log + copiar**: `BtnCopyLog` já existia e funciona; os logs da Store já fluem pro log do Kit via
   `KitLugia.Core.Logger.Log` (dispara `OnLogReceived`).

Arquivos: `KitLugia.Core\KitStore\SqliteReader.cs` (novo), `KitLugia.Core\KitStore\StoreEngine.cs`
(+QueryWingetSearchLocal/FindLocalIndexDb/EnsureLocalIndex), `KitLugia.GUI\Pages\WindowsSettings\StoreRemakePage.xaml(.cs)`
(RouteWheel + handlers de scroll, MakeMonogramIcon, busca ao vivo debounce, overlay modal).

Build: 0 erros (Core + GUI). Obs: SQLite leitor validado por programa de teste descartável contra o
index.db real antes de integrar (não foi no chute).

Pendências futuras (não feitas nesta rodada):
- [ ] Popular Description/Tag/categoria dos 14k pacotes no card de Detalhes (índice já tem tags2/commands2).
- [ ] Detecção de app "fantasma" (ex: Minecraft Preview Demo que sempre volta) — pendente.

### Sessao 25/08 (noite) - RAM Limiter v2: modelo combinado + auto-regulavel

**Problema**: usuarios colocavam Discord 300MB / Opera GX 200MB e os apps crashavam
("app parou de funcionar", "nao responde"). O EmptyWorkingSet(-1,-1) esvaziava TUDO
de uma vez causando page fault storm (57K+ faults em 6s).

**Pesquisa**: testamos todas as APIs do Windows:
- EmptyWorkingSet(-1,-1): perigoso, libera tudo
- SetProcessWorkingSetSizeEx (soft/MAX_DISABLE): OS ignora quando tem RAM livre
- VirtualUnlock: nao funciona em paginas nao-lockadas
- SetProcessInformation (MemoryPriority): funcao, mas sozinha e insuficiente

**Solucao**: modelo COMBINADO de 3 tecnicas:
1. `SetProcessInformation(MemoryPriority=VERY_LOW)` — OS trimma este processo primeiro
2. `SetProcessWorkingSetSizeEx(min=floor, max=target, HARD)` — ceiling que OS enforce
3. `EmptyWorkingSet` condicional — kickstart quando WS > 150% do target

**Testes reais (com Discord rodando)**:
- Discord 1403MB → 271MB (3 ciclos, 15s, vivo)
- devenv 404MB → 13MB (2 ciclos, vivo)
- Opera GX 541MB → 31MB (2 ciclos, vivo)

**Bug de handle**: `SetProcessInformation` precisa de `PROCESS_SET_INFORMATION` (0x0200),
mas o handle so tinha `PROCESS_SET_QUOTA | PROCESS_QUERY_INFORMATION` (0x0500).
O `SetProcessInformation` falhava silenciosamente — MemoryPriority nunca era aplicado.
Corrigido adicionando 0x0200 ao handle.

**Bug de floor**: `commitSize * 0.9` como floor impedia qualquer trim (commit e virtual,
ele sempre e maior que o WS). Trocado para `30% × WS` como floor.

**v2 - auto-regulavel**: `SafeAutoRegulate=true` (padrao, sem toggle extra).
- Sem cooldown: reacao a cada ciclo do timer
- Intervalo: 1000ms (1s) — responsivo mas sem overhead
- Apps oscilam naturalmente (sobe-trim-sobe) — comportamento esperado
- Indicadores visuais: ⚠️ excedido (vermelho), 🎯 em foco (dourado), ✓ (cinza)
- Badge v2 azul no titulo da secao

**Documentacao**: `docs/RAM_LIMITER_INTELLIGENT.md` (v2)

Build: 0 erros.

### Sessao 25/08 (cont.) - Auditoria GameBoost Pro: 3 bugs que causavam crash de navegador

**Problema**: usuario reportou que navegadores (Opera GX, Discord) crashavam as vezes.
Auditoria completa da GameBoostPage + TrayIconService.GameBoost.

**Achados**:
1. `ApplyFireminOptimizations` (MonitorTick 30s) usava `EmptyWorkingSet(handle)` BRUTO em
   processos VIP (opera, discord, chrome) quando WS > 300MB. Isto causava page fault storm.
2. `DetectAndTrimLeaks` (MonitorTick quando RAM > 65%) usava `MemoryOptimizer.EmptyProcessWorkingSet`
   que faz `SetProcessWorkingSetSize(-1, -1)` — o mesmo EmptyWorkingSet perigoso.
3. ProBalance + Firemin conflitavam: ProBalance throttling BelowNormal + Firemin trim no mesmo
   processo → duplo ataque quando usuario volta pro app.

**Correcoes (TrayIconService.cs)**:
1. `ApplyFireminOptimizations` → modelo combinado: VERY_LOW priority + hard ceiling (70% WS)
   + floor (30% WS) + EWS so quando WS > 150% do target. Antes: EWS bruto todo ciclo.
2. `DetectAndTrimLeaks` → modelo combinado: VERY_LOW + hard ceiling (60% WS) + floor (25% WS)
   + EWS condicional. Antes: EmptyProcessWorkingSet(-1,-1).
3. `ApplyProBalanceCore` → skip de processos que Firemin trimou nos ultimos 15 segundos.
   Antes: ProBalance throttled todo background process independente de trim recente.

**Bug de build**: `SetProcessWorkingSetSizeEx` espera `IntPtr`, nao `long`. Cast adicionado.

**Testes reais (Opera GX + Discord rodando)**:
- Opera GX (635 MB): VERY_LOW + ceiling 444MB + EWS kickstart → 36 MB, VIVO (reducao 94%)
- Discord (430 MB): VERY_LOW + ceiling 301MB, EWS pulado (abaixo threshold) → oscila 351-676 MB,
  VIVO E RESPONSIVO em todos os 18s de monitoramento
- ProBalance skip: chrome (trim 5s) = SKIP, msedge (trim 20s) = PROCESSAR, code (60s) = PROCESSAR

**Botoes da GameBoostPage verificados** (13 handlers): todos funcionando.
GameBoostPage (.xaml + .xaml.cs): Card Status, Config Sistema (TrayIcon/AutoStart/UnparkCPU),
Comportamento ao Fechar (CloseToTray/ProBalance), Motor (V1-V4/Custom), GameBarPresenceWriter,
Otimizacoes Reddit (5 toggles), Download Boost (toggle/mode/threshold).

**Notas adicionais**:
- `_userExceptions` (discord, opera, chrome, etc.) exclui do GameBoost boost MAS NAO do Firemin.
  Isso e correto — o Firemin deve trimar qualquer VIP.
- `SetWin32PrioritySeparation(true)` e GLOBAL (registry). Afeta scheduler de TODOS processos.
  So revertido no ShutdownGameBoost. Potencialmente problemático mas nao causa crash.
- `BoostTimerResolution()` tambem e GLOBAL (NtSetTimerResolution 1ms). Afeta todos processos.

Build: 0 erros.
