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
merge_registry forca-parseada com eg load falho (errorlevel 1) = OK.
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