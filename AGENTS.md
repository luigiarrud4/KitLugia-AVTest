# KitLugia — AGENTS.md

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
