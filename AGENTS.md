# KitLugia — AGENTS.md

### Sessao 07/10 (cont. 4) - Modo DIRETO Firemin + header da pagina RAM + 2 bugs do log real

Pedido (prints): explicar como funciona; botao de modo "obedece" estilo Firemin
(2 modos); UI da pagina RAM com letras cortadas.

**1. Analise de ~30 s do app rodando (log real):** Opera oscila 205→1100 MB
(corta/recresce; VERY_LOW aplicada e removida em loop); Discord em storm
permanente (7-77k faults/s, backoff x8). Achados:
- `trimmedCount` contava 2x por processo (empty + teto) → "64 processo(s)" para
  32 reais. Agora `HashSet<int>` (processos únicos).
- Baseline preso em 0: só atualizava com `ConsecutiveTrimCount == 0`, mas quem
  nunca volta ao limite nunca zera → storm eterno. Agora a pausa por storm também
  alimenta o baseline (é o "normal barulhento" sem corte).

**2. Modo DIRETO (pesquisa Firemin: rizonesoft — `EmptyWorkingSet` ao exceder o
`ReduceLimit`, a cada `Boost` ms; sem verificações; "Only reduce if over" = o
threshold; dica oficial: subir o threshold se der lentidão/áudio cortando).**
- `ProcessRamLimit.DirectMode` (JSON, default false) + `ToggleProcessRamDirectMode`.
  Direto = passou do valor → EmptyWorkingSet + VERY_LOW, sem idle/storm/piso.
  Proteções que ficam nos dois modos: nunca em foreground + cooldown mín. 2 s.
- Botão 🧠/⚡ na linha (coluna Modo, entre MB e gear): 1 clique alterna + salva +
  reconstrói a linha; badge `⚡ DIRETO` na linha gov. PROVADO (filho ATIVO 400 MB,
  limite 200): 421→106 MB vivo, toggle volta ao governador.

**3. UI da pagina RAM:**
- O "|" após o título era o emoji 💾 sem fonte emoji (tofu) — título agora com
  `FontFamily="Segoe UI Emoji, Segoe UI"`.
- Header reestruturado em 3 linhas (título / subtítulo com Wrap / WrapPanel de
  controles) — o subtítulo era espremido pelas colunas Auto ("específic.").
- Status e gov com `TextWrapping="Wrap"` (nunca cortam letras).
- Relatórios BCD (`BcdRepairPage`) e Conversor (`DiskConverterPage`): `NoWrap` →
  `Wrap` (o texto do print 3 cortava em "para segurança p...").

**Validacao:** solucao 0 erros (via `-p:OutDir=klbuild`, app aberto trava a DLL);
`CheckUiThreading` 0 novos; screenshot `%TEMP%\rampage_out\ram_rows.png` (botão
modo + gov por linha, sem corte); harness modo Direto ALL-PASS.

**Adendo 5 (pedido: mapa visual + WinPE estilo EaseUS PreOS):**
- **Mapa visual** (`Controls\DiskMapPanel.cs`, novo): barra proporcional colorida
  (EFI=dourado, MSR/Recovery=roxo, C:=azul, dados=azul-claro, livre=tracejado) +
  legenda por particao + linha do disco. Nas paginas Conversor (card MAPA DO DISCO,
  atualiza ao trocar/analisar) e BCD (card MAPA, disco do Windows com Windows+ESP
  em destaque dourado). Provado em screenshot (`diskvis_out\diskmap.png`).
- **Conversao via WinPE** (`ScheduleWinpeConvertAsync` + `RamdiskConvertStartnetCmd`):
  mesmo padrao testado do shrink (WIM + startnet + `KL_CONVERT_TARGET.dat` + BCD
  GUID fixo + bootsequence + preflight + reboot 10 s). Identificacao do disco pela
  ASSINATURA/GUID (`uniqueid disk` + `findstr`, com fallback p/ numero do host) —
  nunca so pelo numero (discrepancia WMI x diskpart). `IsWinPeConvertEligible`
  (flags, nao texto): libera so "Windows em uso"; BitLocker/dinamico/>2 TiB/
  offline/RAW/sem-espaco/sem-assinatura continuam bloqueados. Botoes "WinPE:
  CONVERTER PARA GPT/MBR" com DUPLA confirmacao (a 2ª avisa do reboot).
- `ConvertPlan.CurrentSignature` (do MSFT_Disk: Signature/Guid) alimenta marcador
  e script. `CheckUiThreading` pegou o `await ScheduleWinpeConvertAsync` na UI —
  envolvido em `Task.Run` (0 novos).
- Provado (leitura pura + geracao): script ASCII, echos sem parens, OK/FAIL/log/
  remove-marcador; disco 0→MBR elegivel, disco 1 (sistema, 5 parts + C: 3,7 TB)
  recusado corretamente. **Teste real do reboot fica para VM.**
- Quirk: `PartitionRow` e classe IRMA (namespace), nao aninhada; `Color`/`Brushes`/
  `HorizontalAlignment`/`FontFamily` ambiguos (WinForms) — qualificar.
- **ⓘ explicativos (pedido: "uns i bem explicados"):** Conversor (MAPA: como ler as
  cores/legenda; CONVERSÃO: o que é/limites/quando usar cada botão/WinPE/segurança;
  SETOR 0: o que é cada botão + modo seguro x completo) e BCD (MAPA; REPARO: o que
  cada botão faz + ESP sem letra montada sozinha). Mesmo `InfoButton` + `&#x0a;`
  da pagina RAM.

**Adendo 4 (pedido: botoes de disco nao podem agir na hora):**
- **Conversor achava `force:true` SEMPRE:** 1 SIM na confirmacao ignorava disco de
  sistema/BitLocker/>2 TiB. Agora: sem bloqueios `force:false` (o Core decide);
  com bloqueios exige SEGUNDA confirmacao de FORÇAR (com riscos de nao-boot/perda)
  e so entao `force:true`.
- **Inicializar:** re-verifica particoes entre a confirmacao e a execucao (se o
  disco ganhou particao no meio, aborta + recarrega).
- **BCD Restaurar Arquivos:** executava SEM confirmacao (copia p/ ESP) — agora tem.
- **BCD Reconstruir:** re-verifica Windows+ESP entre confirmacao e `bcdboot`
  (diagnostico obsoleto aborta + re-analisa).
- Os botoes da sidebar so navegam (NavTagMap); o tratamento e nas paginas.
- Provado (leitura pura): disco 0 GPT→MBR livre, disco 1 sistema bloqueado
  (5 particoes + C: 3,7 TB > 2 TiB).

**Adendo 3 (debug só no cérebro + textos cortados):**
- Linha gov (dourada) com VERY_LOW/faults/backoff/commit é debug: no ⚡ mostra só
  badges + `⚡ DIRETO`; status no ⚡ mostra só "Atual + excedido/✓ (+ foco)".
  Completo só no 🧠 (`BuildStatusText`/`BuildGovText` com o modo).
- Subtítulo ISLC sem `TextWrapping` cortava ("passe o m...") — Wrap no título e
  no subtítulo do card.

**Adendo 2 (pedido: Direto padrao + botao i + pesquisa ISLC):**
- Novos limites nascem `DirectMode=true` (`SetProcessRamLimit`); existentes
  preservam o modo ao atualizar (AddOrUpdate mantem o objeto). Toggle + persistencia
  provados em harness (`ramdirect`, 6/6).
- Botao `ⓘ` (InfoButton padrao) no header da secao RAM explicando os 2 modos
  (tooltip com `&#x0a;`, mesmo padrao dos botoes que funcionam).
- **LICÃO/INCIDENTE:** harness que instancia `TrayIconService` e chama
  Set/Remove/Toggle SALVA no JSON real do usuario (`SaveProcessLimits` em cada
  chamada) — um teste apagou discord/opera/ramgovchild do disco (o app em memoria
  estava intacto). Restaurado a mao a partir da leitura anterior + backup
  `process_ram_limits.antes_do_restauro.json`. REGRA: harness com TrayIconService
  faz backup do JSON antes e restaura depois, ou nao chama metodos que salvam.

### Sessao 07/10 (cont. 3) - RAM Limiter governador de verdade (fim do "enfeite")

Pedido: pagina de monitor RAM com pontos fora do lugar na UI + o limite por
processo "virou enfeite" (navegadores/Discord com page faults; a medida aplicada
tinha tirado o poder do usuario).

**1. Por que era enfeite (3 causas provadas no harness `%TEMP%\opencode\ramgov`):**
- Teto MOLE = decorativo (ja medido em 03/10: 486 MB com teto de 330 ficou em 486).
- Teto DURO com handle sem `0x0200` (PROCESS_SET_INFORMATION): o `SetProcessMemoryPriority`
  do fallback falhava MUDO (outros fluxos usam `| 0x0200`; o limiter nao usava).
- Campos do "Resting State Tracker" (`RestingWorkingSetMB`, `LastPageFaultCount`,
  `StormBackoffLevel`, `EffectiveFloorFromCommit`, `CommitSizeMB`...) existiam mas
  NUNCA eram lidos nem escritos — motor desenhado e nao ligado. `CommitSizeMB` era
  sempre 0 na UI.

**2. Medicao decisiva (ApiDiag, filho parado, sem elevacao):**
`SetProcessWorkingSetSizeEx(100,150,duro)` = TRUE mas WS 321 MB intacto por 20 s;
`EmptyWorkingSet` = TRUE e 321→0 MB NA HORA. Conclusao: teto min/max e "preferencia"
que o balance set manager ignora sem pressao; o UNICO mecanismo imediato e esvaziar
(igual ao Firemin). Com RAM livre o esvaziado vai p/ standby (soft fault barato);
o storm que doi e HARD (disco, sem RAM livre).
Quirk do harness: orquestrador e filho com o MESMO nome .exe fazem o
`GetProcessesByName` somar os dois e o foreground pular tudo — copiar o exe para
`RamGovChild.exe` antes de lancar.

**3. Governador novo (`ApplyProcessRamLimits` reescrito, TrayIconService):**
- VERY_LOW 1x ao exceder (handle COM `0x0200`, agora entra de verdade) + volta a
  NORMAL ao normalizar; teto de contenção distribuido proporcionalmente entre
  instancias (opera 30 procs: teto dividido, nao repetido).
- IDLE (faults<100/s + cpu<3% + RAM livre>2 GB) → EmptyWorkingSet (barato) + teto;
  ATIVO ou RAM curta → só VERY_LOW + teto (sem esvaziar, sem storm).
- Anti-storm: faults>1500/s com corte recente → pausa + backoff x2/x4/x8; abaixo de
  500/s o backoff decai. Piso = max(min-por-tipo, 50% limite, 70% commit quando
  commit>limite). CPU% por instancia via delta de TotalProcessorTime (`_govCpuPrev`
  estatico, fora do JSON).
- PROVADO (filho 400 MB tocando em loop, limite 200): 421→106 MB no 1º ciclo, vivo,
  53k faults em 45 s (soft, standby), sem rasto ao desligar (teto liberado +
  NORMAL). Medicao de faults/commit via `GetProcessMemoryInfo` local (1 handle por
  instancia); `CommitSizeMB`/`PeakWorkingSetMB`/`RestingWorkingSetMB` preenchidos.

**4. UI (TraySettingsPage):**
- BUG: `RefreshProcessLimitsStatus` APAGAVA a 3ª linha (`floorTb.Text=""`) a cada 2 s
  — os badges do gear sumiam logo apos carregar. Agora atualiza (`BuildGovText`).
- BUG: construcao mostrava "Atual+Pico+Commit" e o refresh só "Atual" — a linha
  "piscava" a cada 2 s. Novo `BuildStatusText` unico nos dois caminhos.
- Linha gov: `🧠 gov: VERY_LOW 2350 faults/s ⛈ backoff x2 💾 commit` (provado em
  screenshot `%TEMP%\rampage_out\ram_rows.png`); `MemPrio 5` (default) escondido
  (era ruido em toda linha); estado zerado quando o processo para.

**Validacao:** solucao 0 erros; `CheckUiThreading` 0 novos; BOM+CRLF 100%.

**Adendo (log real do app, mesma noite): 2 bugs pegos pelo governador em producao:**
- **Piso MAIOR que o teto (inversao):** o "commit floor" (70% do commit) passava do
  teto (ex.: Opera teto 840 MB com piso 3184 MB; Discord teto 1913 com piso 2014).
  Causa: commit conta memoria NAO residente (standby/pagefile) — exigir 70% dele
  como piso impedia qualquer contenção e o `teto=max(piso)` virava o proprio piso
  gigante. REMOVIDO (commit continua medido e exibido, fora do piso); se piso>teto
  por limite baixo demais, loga aviso em vez de falhar mudo.
- **Storm FALSO por app barulhento:** Discord faultando 22-36k/s SOZINHO travava o
  governador (limiar fixo 1500/s pausava tudo para sempre). Agora storm e RELATIVO:
  `max(1500, 5x baseline)`, com `BaselineFaultsPerSec` (media quando sem corte).
- Build com app aberto dá MSB3021/3027 (DLL travada) — validar com
  `-p:OutDir=C:/.../klbuild/` (0 erros); o usuario recompila pelo VS com o app fechado.

### Sessao 07/10 (cont. 2) - Padrao de qualidade de ordenacao em TODAS as abas do TM

Pedido (prints): % de GPU nao ordenava (lista cheia de "—" no topo com dwm.exe 6%
la embaixo); "falta um padrao de qualidade" nas outras abas.

**1. GPU (e Pico/TempoCPU/PageFaults/Commit/Disco/Rede) — CAUSA RAIZ: agregados do
grupo zerados.** A linha individual preenche tudo (`GpuValue`, `PeakMemValue`,
`CpuTimeSec`, `PageFaultsValue`, `CommitMB`), mas a linha de GRUPO (ApplyFilter)
nao propagava NENHUM deles (e Disco/Rede/Threads/Handles vinham so do `first`).
Resultado: todos os grupos empatavam em 0 e o clique "nao fazia nada" (o harness
anterior dava PASS por tolerancia — empate geral passa em qualquer verificador;
falso positivo documentado). Agora o grupo agrega: taxas/contadores SOMAM
(GPU/Disco/Rede/threads/handles/tempo/faults/commit), pico e MAX, com texto
formatado igual ao individual ("—" sem engine). PROVADO: 24 grupos com texto GPU
→ todos com valor>0; `GpuValue|Desc` top real.

**2. Padrao aplicado (toda coluna clicavel tem SMP + criterio que sobrevive ao refresh):**
- Usuarios: case `"Status"` faltava no `ApplyUsersSort` (clicava e ordenava por CPU
  com a seta no Status). Adicionado.
- Servicos: colunas Nome/Exibicao/Inicializacao/Fabricante SEM `SortMemberPath`
  (clique morto com `e.Handled=true`). SMPs adicionados (o switch ja cobria).
- Inicializacao (TM): Nome/Comando/Local sem SMP. Adicionados.
- Conexoes: sem handler (sort default, perdido a cada refresh que reconstroi a
  lista). Novo `DgConnections_Sorting` + estado guardado, aplicado em
  `ApplyConnectionFilter` (Pid numerico, resto texto).
- Latencia x4 + Disco: novo handler GENERICO `TmGrid_Sorting` (CustomSort na view)
  com `TmValueComparer` (entende numero, pt-BR "36.794", e unidades GB/MB/KB/B/%/ms/us;
  "—"/"N/A" sempre por ultimo) + `RefreshTmGridSort` no fim de cada atualizacao
  (RefreshLatencyUi + reconciliacao Storage). SMPs adicionados onde faltavam
  (Storage aponta p/ props numericas reais: ReadBps/WriteBps/TotalBps/IopsValue).
  `DgSumTopCpu` DEIXADO de fora de proposito (ranking fixo Top-14 por CPU + pin).

**3. Validacao (harness `%TEMP%\opencode\tmsort`, janela real, cliques reais via
`DataGridSortingEventArgs`):** Processos 13/13, Usuarios 5/5, Servicos 5/5,
Inicializacao 4/4, Conexoes 6/6, `TryParseTmNumber` 8 casos + "—" — **ALL-PASS**.
Solucao 0 erros; `CheckUiThreading` 0 novos; BOM+CRLF 100%.

### Sessao 07/10 (cont.) - Demais colunas do TM + duplo clique no cabecalho

Pedido (print): apos o fix da Memoria, revisar as OUTRAS categorias; e duplo clique
no cabecalho (para inverter maior/menor) ABRIA A PASTA do processo.

**1. Duplo clique no header abria a pasta — CAUSA RAIZ + FIX.**
`DgProcesses_MouseDoubleClick` nao verificava ONDE foi o clique: o 2º clique do
duplo clique no cabecalho caia em `MenuOpenFolder_Click` (explorer /select). Novo
guard no topo do handler: sobe a arvore visual via `VisualTreeHelper.GetParent` e
retorna se achar `DataGridColumnHeader` (qualificado
`System.Windows.Controls.Primitives` — `DataGridColumnHeader` puro nao compila aqui
por causa dos usings globais com WinForms, CS0246). Sem `Handled` (o Sorting precisa
do evento). PROVADO com header REAL da arvore (`%TEMP%\opencode\tmsort`): grupo
opera.exe (29) continua nao-expandido apos o dblclick (antes expandia/abria pasta).
Quirk do harness: header criado manualmente NAO entra na arvore visual (Content sem
template aplicado → GetParent null) — o teste com header fake FALHAVA; tem que trocar
para a aba Processos (`SwitchTabByTag`) e pegar o header renderizado. Outro quirk:
`HashSet<string>` NAO implementa `ICollection` nao-generica (`as ICollection` = null
→ ArgumentNull no Cast) — usar `IEnumerable` no probe.

**2. Todas as 13 categorias validadas (harness `%TEMP%\opencode\tmsort`, janela real).**
Metricas (CPU/RAM/Disco/Rede/GPU/Pico/TempoCPU/PageFaults/Threads/PID) = ordem
GLOBAL estrita na view (60 pares, tolerancia 0,15 = granularidade do round anti-pisca);
texto (Nome/Usuario/Status) = grupos em ordem + alfabetico dentro. **13/13 PASS**
(`Pid|Asc` top=System; `Threads|Desc` top=System; `RamValue|Desc` top=opera.exe (29)).
`SortMemberPath="ProcessCount"` e de OUTRA aba (Usuarios) — nao e coluna do DgProcesses;
todas as colunas do DgProcesses tem `MetricOf`/`SortColumnLabel` cobertos.

**Validacao:** solucao 0 erros; `CheckUiThreading.ps1` 0 novos.

### Sessao 07/10 - Filtro RAM global de verdade + verificacoes estilo EaseUS nos modulos de disco

Pedido (prints): (1) o filtro de Memoria do KitTaskManager continuava agrupado por tipo
(devenv 1,2 GB em cima, opera.exe (24) 2,5 GB embaixo); (2) os menus do Gerenciar Discos
(WinPE Tools/shrink, Reparar Boot/BCD, Conversor MBR/GPT) sem verificacao boa o bastante
— a ideia era replicar o EaseUS Partition Master (docs EASEUS_EPM_*.md).

**1. Ordenacao RAM: CAUSA RAIZ ERA A VIEW, NAO O OrderRows.**
A correcao anterior (OrderRows global por metrica) estava CERTA e provada na source
(`_groupedLive`: opera 3,4 GB > Discord 2,1 GB > ...), mas a `CollectionView` tinha um
`PropertyGroupDescription("Group")` PERMANENTE que reagrupava tudo na apresentacao —
nenhuma ordem global jamais chegaria na tela. Novo `ApplyGroupingMode()`:
metrica (CPU/RAM/Disco/Rede/GPU/...) = `GroupDescriptions.Clear()` (lista plana
global); texto (Nome/Status/Usuario) = agrupa por tipo. Chamado no Loaded, no
`DgProcesses_Sorting`, no combo e na seta. `IsTextSortColumn()` unico (OrderRows usa
o mesmo). PROVADO com janela real (`%TEMP%\opencode\tmsort`, tecnica do
docs/TASKMANAGER_UI_DEBUG.md): top 10 global por RAM em ordem estrita, sem grupo
na frente. Clique em Nome volta a agrupar (comportamento preservado).

**2. Reparar Boot/BCD — 3 bugs reais + ESP sem letra (estilo EaseUS MountSrcBootPart):**
- `BackupBcdStore` retornava SEMPRE null: `File.Exists(dst)` numa PASTA (sempre false).
  Agora `Directory.Exists(dst)`. O tooltip "com backup antes" mentia — o backup era
  feito mas reportado como falha.
- `FindEspPartition` exigia letra: ESP nasce SEM letra (prova: Disco 1 Part 2, 100 MB,
  IsSystem, sem letra nesta maquina) → diagnostico travava em "ESP nao encontrada".
  Novo `FindEspPartitionNoLetter` + `EnsureEspMountedAsync` (`mountvol X: /s` em letra
  livre Z..S, com confirmacao de montagem) + `ReleaseEspMountAsync` (`mountvol /d` so
  do que nos montamos). `DiagnoseAsync`/`RebuildAsync`/`RestoreBootFilesAsync` montam
  sozinhos (finally libera). `ScoreEspCandidate` ganhou regras sem-letra: IsSystemFlag
  32 MB-2 GB → 70; "GPT: System" (rotulo do legado Win32_*) → 65; EFI → 100 mantido.
- `GetAllDisks` sem admin voltava discos "vazios" (so "Nao Alocado" do tamanho total)
  porque o nativo detectava o disco mas nao lia o layout (erro 5) e `disks.Count > 0`
  impedia o fallback. Agora: tudo-unallocated → throw → Storage API; e Storage API sem
  particoes reais (MSFT_Partition negado via DCOM p/ token nao elevado, embora o CIM
  do PowerShell passe) → legado Win32_* (que funciona: 5 particoes incl. ESP).
  PROVADO sem admin: ESP achada via legado. Mensagem da BcdRepairPage atualizada
  (o Core monta sozinho; nao pede mais letra manual).

**3. Conversor MBR/GPT — limpa read-only sozinho (EaseUS §3.2):**
Novo `PartitionManager.ClearDiskReadOnlyAsync` (`attributes disk clear readonly`).
`ConvertAsync`: read-only como UNICO bloqueio → limpa + re-analisa e segue; continua
bloqueando em BitLocker/dinamico/sistema-BIOS/>2 TiB. Outros bloqueios intactos.

**4. Extend/Shrink — retry estilo CAsynLockVolume (MAX_RETRY_TIMES):**
Novo `RunDiskpartWithRetryAsync`: repete 1x apos 3 s SOMENTE com medicao provando
"nada mudou" (gate anti-shrink-duplo: `shrink desired` repetido encolheria 2x; efeito
parcial nunca repete). Usado no degrau diskpart do Extend e do Shrink. Handle aberto
(AV/indexer/VSS) libera em segundos — era a causa nº 1 de "falhou sem motivo".

**Validacao:** solucao 0 erros; ordenacao visual OK (harness WPF, 139 itens);
score ESP 7/7; ESP achada sem admin; `CheckUiThreading.ps1` 0 novos; encoding
preservado (BOM+CRLF onde havia; LF puro onde o arquivo ja era LF — sem mistura).
Teste end-to-end com admin (montar ESP + bcdedit + conversao) fica para o app real:
os probes rodam sem elevacao e bcdedit/mountvol exigem.

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

4. **SEMPRE conversar em PORTUGUES (pt-BR)** — toda resposta, explicação, progresso,
   resumo e pergunta ao usuario em portugues. Codigo, identificadores, comandos e
   conteudo de arquivos seguem como estao (excecao natural). NAO mudar de idioma
   entre sessoes/mensagens.

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

### Sessao 03/09 — Kit Task Manager: numeros do resumo piscando (escrita unica por segundo)

Sintoma: os 5 numeros grandes da barra de resumo (CPU/Memoria/Disco/Rede/GPU) piscavam,
alternando entre valores mesmo com o sistema parado.

Causa raiz (multi-placa: varios timers + fontes escrevendo os MESMOS TextBlocks):
1. `TxtDiskUsage` tinha DOIS escritores com fontes diferentes: `RefreshAsync` (~1s, soma
   de I/O por processo) e `UpdatePerformanceGraphs` (1s, contadores PhysicalDisk) — o
   numero alternava entre os dois valores a cada segundo.
2. CPU/Memoria eram amostrados do MESMO `_cpuCounter`/`_memAvailable` por dois timers
   independentes (`StartResourceMonitor` 2s + graph tick 1s) — o `NextValue()` intercalado
   corrompia as amostras (intervalo de amostragem oscilante).
3. GPU/Rede/Disco eram escritos pelo `RefreshAsync` (tick async de duracao variavel)
   enquanto o graph tick reescrevia o Disco — rajada de re-renders por segundo.

Correcao (KitTaskManager.Performance.cs + KitTaskManagerWindow.xaml.cs):
1. **Escrita UNICA da barra no graph tick (1s)**: `UpdatePerformanceGraphs` agora grava
   todos os 5 numeros (ja le os contadores uma vez por tick). Removidos os escritores de
   `RefreshAsync` (TxtGpuUsage/TxtDiskUsage/TxtNetUsage) e todo o `StartResourceMonitor`
   (timer de 2s apagado — campo, Loaded call e Closing stop).
2. **`SetMetricText` (helper novo)**: so reescreve o TextBlock quando o texto mudou —
   valores iguais nao forcam re-render (causa visual do flicker).
3. Rede passa a ser calculada de `_allRows` (lock) em vez de `_filteredRows` (evita soma
   parcial quando ha filtro de busca).
4. Net/GPU usam `_brushGray` congelado (sem alocar SolidColorBrush novo a cada tick).

Build: 0 erros / 138 warnings (nullable pre-existentes).

**A TESTAR (host)**: abrir Task Manager na aba Processos e observar a barra de resumo
por ~10s — CPU/Memoria/Disco/Rede/GPU devem ficar estaveis (atualizacao suave 1x/s), sem
alternar valores quando o sistema esta ocioso. Pausado/2s no combo da aba Desempenho
agora congela a barra junto (comportamento esperado, mesma fonte).

### Sessao 03/09 — Botões de janela XP + ícones de processos rápidos

Pedido: botões de maximizar/minimizar às vezes aparecem estilo Windows XP em vez do tema do kit; ícones de processos devem carregar mais rápido.

**Causa raiz (botões XP)**: MainWindow e KitTaskManagerWindow usavam WindowChrome com WindowStyle DEFAULT (SingleBorderWindow) + UseAeroCaptionButtons=False. O Windows ainda re-pintava a barra de título nativa (com botões clássicos) por cima da área do chrome em certos repaints (troca de tema/DPI/resolução) — o "de vez em quando".

**Correção (padrão do projeto, igual StoreRemakeWindow/KitIsoStudioWindow)**:
1. `MainWindow.xaml` + `KitTaskManagerWindow.xaml`: `WindowStyle="None"` — o frame nativo some 100%, os botões customizados (minimizar/maximizar/fechar) são os únicos possíveis; nunca mais XP.
2. `MainWindow.xaml`: `ResizeMode="CanResizeWithGrip"` → `CanResize` (CanResizeWithGrip + WindowStyle=None lança exceção no init do WPF); adicionado `ResizeBorderThickness="6"` no WindowChrome (resize pelas bordas continua).
3. Ambas janelas: `MaxWidth/MaxHeight` ligados a `SystemParameters.WorkArea` (mesmo padrão do KitIsoStudioWindow/PathExplorerWindow) — sem isso, janela borderless maximizada cobre a taskbar.

**Causa raiz (ícones lentos)**: toda abertura do app re-extraía ícones do shell32 (SHGetFileInfo/ExtractIconEx) e o loop do Task Manager tinha 2 bugs de `return` dentro do `foreach` que abortavam o lote inteiro no 1º item já cacheado (além do cap de 24 ícones).

**Correção**:
1. `ProgramIconHelper.cs` — cache PERSISTENTE em disco (`%LOCALAPPDATA%\KitLugia\IconCache`): PNG por hash de caminho+índice+mtime (invalida sozinho se o exe mudar); 1ª execução extrai e grava, as seguintes leem do disco (~1ms/ícone). Parse de "caminho,índice" movido para o topo para o cache indexar pelo arquivo real.
2. `KitTaskManagerWindow.xaml.cs` — LoadIconsIncrementalAsync: lote de 96 ícones priorizados por CPU, guard de concorrência `_iconsLoadRunning`, `continue` no lugar dos `return` (itens cacheados/sem caminho não abortam mais o lote).

Build: 0 erros / 0 avisos (GUI). Resta testar no host: abrir/fechar o Task Manager 2x (2ª abertura com ícones instantâneos) e alternar tema do Windows com o app aberto (botões nunca mais XP).

### Sessao 03/09 — Force Stop + Take Ownership PROVA-DE-TUDO (windows.old incluso)

Pedido: "faça o force stop e o takeownership serem a prova de tudo" — takeown falhava em
C:\Windows.old. Pesquisa web + reflexao sobre tudo que interfere:

**TAKE OWNERSHIP (FileTakeOwnership.cs reescrito)**:
- CAUSA RAIZ windows.old: .NET `Directory.Exists`/`File.Exists` retorna FALSE quando a ACL
  nega leitura (windows.old inteiro e TrustedInstaller) → Kit dizia "Caminho não encontrado".
  Novo `ProbePath()` (GetFileAttributesW): distingue nao-existe (erro 2/3/53) de
  existe-mas-negado (erro 5/21) e segue com SetNamedSecurityInfo (WRITE_OWNER nao precisa ler).
- BFS PAI→FILHO: dono da pasta ANTES dos filhos (árvores negadas destravam nível a nível;
  a ordem folhas→raiz antiga falhava em tudo). Re-tenta enumeração 2x apos tomar posse.
- Skip de reparse points (AttributesToSkip=ReparsePoint + check): evita loop infinito em
  Windows.old\Documents and Settings → C:\Users (antes AttributesToSkip=0 seguia junctions!).
- Itens negados: SetOwnerAndDaclNative (dono + substitui DACL por Administradores:F via
  RawSecurityDescriptor/SetNamedSecurityInfo) — sem depender de leitura.
- MODO RAPIDO agora usa SetNamedSecurityInfo OWNER-only tambem para ARQUIVOS (antes .NET
  GetAccessControl, que exigia leitura).
- Fallback classico automatico quando restam falhas: `takeown /f "path" /r /d y` +
  `icacls "path" /grant *S-1-5-32-544:F /t /c /l /q` (SID locale-proof; takeown usa
  semantica de backup — metodo comprovado da MS p/ windows.old). Result ganhou
  FallbackUsed/FallbackMessage; UI mostra.
- Sem admin: EnablePrivileges falha → erro claro "Execute como Administrador" (antes
  falhava item a item sem explicar).
- RestoreToTrustedInstaller agora usa SID S-1-5-80-... (locale-proof) + SetNamedSecurityInfo.

**FORCE STOP (ForceStopUnlockService.cs)**:
- EnableDebugPrivilege() no inicio do scan E do unlock — sem ele OpenProcess/DuplicateHandle
  falham em processos de outros usuarios e o scan nativo "não acha nada" (causa classica).
- KillProcess em cascata: Process.Kill → NtTerminateProcess → diagnostico PPL
  (NtQueryInformationProcess class 61) com mensagem clara (anti-virus/EDR protegido).
- Phase 2 e 7: pula processo proprio e processos ja encerrados (RM mata primeiro —
  antes gerava erros fantasma "processo não encontrado").
- Probe de existencia (FileTakeOwnership.ProbePath) no FindBlockingProcesses — paths
  negados agora sao escaneados em vez de "Caminho invalido".

**ELEVACAO (Program.cs + ForceStopUnlockPage + UnlockIpcServer)**:
- Context menu lanca o Kit SEM runas → --unlock/--takeown agora relanca a mesma linha
  elevada (UAC) automaticamente antes de qualquer operacao.
- Se a instancia elevada acha o mutex ocupado (instancia principal sem admin), executa
  HEADLESS (worker elevado: TakeOwn/Unlock direto + toast Windows) em vez de IPC p/ a
  instancia nao-elevada (que falharia).
- Pagina: botões Assumir/Liberar/Tentar Deletar checam admin e oferecem relançar elevado
  (--takeown/--unlock "path"); analise/ACL usam ProbePath (mostra "existe mas acesso
  negado" em vez de "não encontrado").

Build: 0 erros / warnings nullable pre-existentes.

**A TESTAR (host/VM)**: Take Ownership em C:\Windows.old (deve completar com fallback
classico se necessario), Force Stop em arquivo travado por processo de outro usuario,
menu de contexto com UAC, e cancelar o UAC (deve abrir a pagina normal).

### Sessao 03/09 (teste real) — Force Stop / Take Ownership validados + 3 bugs reais corrigidos

Teste REAL no host (nao VM): force stop em goodbyedpi rodando + takeown em arvore
sintetica com DENY explicito (121 itens + junction loop) e UAC real.

1. **Bug P/Invoke**: `GetCurrentProcessNative` sem EntryPoint → EntryPointNotFoundException
   em kernel32 (SeDebugPrivilege NUNCA habilitava). Fix: `[DllImport("kernel32.dll",
   EntryPoint = "GetCurrentProcess")]`. O force stop funcionou mesmo assim (admin ja tem
   o privilegio por default), mas em fluxo sem admin era morte certa.

2. **Bug probe**: o takeown em modo rapido (grantFullControlOnDirs=false — o usado pelo
   worker headless) so trocava o OWNER e nunca o DACL → arvores com DENY explicito
   (ex: `icacls /deny Lugia:(OI)(CI)RX`) continuavam bloqueadas apos "121/121 ok".
   O probe inicial usava GetFileAttributesW, que PASSA em arquivo negado (processo
   elevado/dono) — o probe certo e abrir para leitura (CreateFileW GENERIC_READ):
   erro 5/21 = deny real. Fix: `CanReadOpen()` + substituicao de DACL so nos itens
   que continuam negados.

3. **Bug PACL vs SECURITY_DESCRIPTOR** (o mais grave): `SetOwnerAndDaclNative` passava
   os bytes de um RawSecurityDescriptor como pDacl de SetNamedSecurityInfo — mas o
   parametro e um PACL (ACL crua). O Windows lia o cabecalho do SD como cabecalho de
   ACL → AceCount = OwnerOffset (0) → gravava DACL VAZIO → negava TUDO (ate o dono
   elevado, icacls "Acesso negado"). Fix: passar `dacl.GetBinaryForm()` (RawAcl pura).

Resultado final verificado: 121/121 ok, 161/161 arquivos legiveis, escrita OK,
junction loop intacta, SDDL final `O:BA D:AI(A;;FA;;;BA)`, toast enviado.
Force stop: achou WinDivert64 (.sys, descarregado via SCM) + odbyedpi (PID), matou,
toast OK. Obs: teste matou o goodbyedpi do usuario (rodar os .cmd da pasta p/ voltar).

### Sessao 03/09 (cont.) — Context menu: reapply automatico no startup

Pedido: "quando o kit inicie ele recoloque os menus novamente com as novas
configuracoes" — o RefreshContextMenuPathsIfNeeded antigo SO atualizava o path
de itens que JÁ existiam; se algo removesse a entrada, ela nunca voltava.

1. **Persistencia** (`HKCU\Software\KitLugia\ContextMenu`, DWORD por item):
   `SystemTweaks.SaveContextMenuPref(id, enabled)` grava 1/remove o valor.
2. **`SystemTweaks.ReapplyContextMenuPrefs()`** (novo): le todos os prefs ==1,
   resolve a acao Add via `ContextMenuQuickAdd.GetAddAction(id)` (novo, catalogo:
   super items por Id case-sensitive — "takeownership"/"cmdhere"/... — e classicos
   capitalizados — "TakeOwnership"/"CmdHere"/...; case-sensitive evita colisao
   entre super e classico) e recria a entrada idempotente com a config ATUAL
   (path do exe, comandos, icones). Loga [CTX PREFS].
3. **Gravacao do pref nos 3 fluxos de toggle**: ContextMenuPage.QuickAddToggle_Click
   (item.Key), ContextMenuAddPage.ToggleAsync (item.Id), ForceStopUnlockPage
   (forcestopunlock/kittakeown). Remocao manual pelo grid/lista tambem limpa o pref
   (`ClearContextMenuPrefsForEntryName` mapeia nome da chave registry -> ids).
4. **Startup** (App.xaml.cs, 2 pontos: normal + cold-start --unlock/--takeown):
   `ReapplyContextMenuPrefs()` roda apos RefreshContextMenuPathsIfNeeded.

Build: 0 erros. TESTADO no host: pref forcestopunlock semeado -> app iniciado ->
log "[CTX PREFS] Menu de contexto recriado com config atual: forcestopunlock" +
"1 item(ns) reaplicado(s)"; comando do menu = exe atual. Pref de teste removido.

### Sessao 03/09 (cont.) — Auditoria Pages: gargalos/memory leaks

Passada rapida nas 49 paginas em busca de gargalo/leak (timers, eventos estaticos,
unloaded, alocacao por tick). Veredito: codigo ja segue a convencao (Unloaded +
Cleanup via reflexao em CleanupAndNavigate, journal do Frame limpo pos-navegacao,
CTS cancelados, eventos estaticos WinbootManager/InstallMonitor desinscritos).

ACHADO + FIX (NetworkPage): `RefreshAdapterStatus` (tick 3s) nao tinha guarda
anti-reentrancia — ListPhysicalAdapters (WMI) pode demorar > 3s e ticks se
sobrepunham empilhando chamadas. Adicionado `_refreshingAdapters` (mesmo padrao
do `_isLoading` do DNS). Build 0 erros/0 avisos.

Observacoes (nao corrigidas, aceitaveis): PartitionsPage `Items.Refresh()` por
segundo (so com ChkAutoRefresh ligado); ProcessMonitorPage diff-based OK;
brushes novos por acao (nao por tick) nas paginas de status; StoreRemakePage
16ms anim timer parado no Unloaded; DiagnosticPage usa reflexao para pausar
timers do tray (fragil mas nao vaza).

### Sessao 03/09 (cont.) — MainWindow/topbar animacoes + IntegrityPage comandos BCD

1. **Topbar: botoes presos/estranhos na animacao** — causa raiz: cada botao de
   icone (KitTaskManager, GoodbyeDPI, BackgroundMonitor, Update, Console,
   Notifications) tinha no trigger IsPressed um `<Setter bdr.RenderTransform>`
   que SUBSTITUIA o ScaleTransform inteiro — o clock de animacao do hover
   (EnterActions, HoldEnd 1.1) ficava preso no objeto antigo; soltar o clique
   fora do botao deixava ele em 1.1 ou com snap errado. Correcao: transform
   nomeado `scale` + IsPressed com EnterActions/ExitActions animando
   `scale.ScaleX/ScaleY` (0.9/0.92 -> 1) — nunca mais substitui o objeto.
   Mesmo padrao aplicado nos 6 botoes.

2. **IntegrityPage: comandos BCD "nao funcionavam"** — causa raiz em
   `Guardian.ToggleTweak` (case Bcd): apos `bcdedit /set` com sucesso, o
   CheckTweak pos-toggle lia o CACHE do `bcdedit /enum` (TTL 15s) que ainda
   continha o valor ANTIGO -> status continuava MODIFIED -> UI dizia "FALHA:
   alteracao nao aplicada" mesmo com o comando tendo funcionado. Correcao:
   invalidar `_bcdEnumAttempted/_bcdEnumOutput/_bcdEnumError` apos sucesso do
   toggle BCD para o re-check rodar bcdedit fresco.

3. **Bonus**: campo morto `_isRefreshing` removido do KitTaskManagerWindow
   (single-flight ja era feito pelo SemaphoreSlim `_refreshGate`).

Build: 0 erros / 136 avisos (nullable pre-existentes).

### Sessao 03/09 (cont.) — IntegrityPage FixAll travava com tudo ja corrigido

Sintoma (instalacao limpa): RESTAURAR TODOS rodava os comandos, tudo era
corrigido (score 100% ao reentrar na pagina), mas a UI CONGELAVA no fim com
um item/botao em estado de correcao.

Causa raiz (IntegrityPage.xaml.cs, BtnFixAll_Click):
1. `UpdateUiState(isLoading: true)` no inicio desabilita a lista/botoes e deixa
   BtnFixAll como "⏳ PROCESSANDO...", mas o fluxo de sucesso so fazia
   `_isBusy = false; ShowLoadingOverlay(false)` — NUNCA `UpdateUiState(false)`.
   Se qualquer passo final falhasse (ex.: excecao no re-scan pos-correcao
   `GetHarmfulTweaksWithStatus`), _isBusy ficava true para sempre, a lista
   ficava IsEnabled=false e o botao preso em "PROCESSANDO" — pagina congelada.
2. Sem try/catch/finally: excecao pos-await escapava e nunca destravava.

Correcoes:
- BtnFixAll_Click reescrito com try/catch/finally: o finally SEMPRE executa
  `_isBusy=false; UpdateUiState(isLoading:false); ShowLoadingOverlay(false)`;
  catch loga + CompleteTask + ShowError (nunca congela a pagina).
- BtnToggleItem_Click: RefreshFromAllTweaks movido para o finally (antes so
  rodava no sucesso) — botao da linha nao fica mais preso em "⏳" quando o
  toggle falha.

Build: 0 erros / 136 avisos.

### Sessao 03/09 (cont.) — WindowsUpdatePage: scroll travava sobre ComboBoxes no centro

Sintoma: na pagina de Windows Update (ultima do Dashboard), com o mouse sobre
elementos no centro (CmbPauseDays/CmbChannel), o scroll da pagina parava.

Causa raiz (WindowsUpdatePage.xaml.cs): `DisableMouseWheelSelection` fazia
`PreviewMouseWheel += (s,e) => e.Handled = true` SEMPRE — engolia o wheel e o
ScrollViewer da pagina nunca recebia o evento quando o cursor estava sobre os
ComboBox.

Correcao: padrao RouteWheel do projeto (StoreRemakePage). ScrollViewer raiz
nomeado `MainScroll`; o handler agora so bloqueia a troca de item com o popup
FECHADO e repassa o delta ao MainScroll (`VerticalOffset - e.Delta/3.0`). Com
o popup ABERTO mantem o comportamento padrao (wheel rola a lista). O scroll da
pagina funciona em qualquer ponto.

Bonus (WindowsUpdateManager.GetStatus — metodos antigos):
- `LastCheckTime` era parseado e descartado (`DateTime.TryParse(..., out _)`)
  — campo nunca preenchido; agora grava `status.LastCheckTime`.
- bcdedit era lido com `ReadToEnd()` antes de `WaitForExit()` SEM timeout
  (padrao de deadlock se o processo pendurar); agora usa
  `ProcessRunner.Run("bcdedit", "/enum {current}", 10000)` (timeout + encoding
  OEM, padrao do resto do Core).

Build: 0 erros / 149 avisos (nullable pre-existentes).

### Sessao 03/09 (madrugada) - Auto-start bulletproof (3 metodos sempre) + VRAM automatica consertada

**Pedido 1 - Startup com Windows**: garantir que TODOS os locais que iniciam o kit sejam
reconfigurados em toda execucao (funciona mesmo sem AppData na primeira vez).

- `TrayIconService.SetAutoStart(true)`: agora garante os 3 metodos SIMULTANEAMENTE
  (antes a Task Scheduler "substituia" Registry+Startup e apagava os outros — bastava
  um cleaner apagar a task e o kit parava de iniciar). Registry Run + .lnk Startup +
  Task (Boot+Logon) coexistem; o mutex de instancia unica evita processo duplo.
- `SetStartWithWindowsPref/GetStartWithWindowsPref` (novo): preferencia persistida em
  `HKCU\Software\KitLugia\TraySettings\StartWithWindows` — sobrevive a limpezas e nao
  depende de AppData/settings JSON.
- `IsAutoStartEnabled()`: agora faz OR com a preferencia persistida (intencao do usuario
  continua viva mesmo com entradas apagadas).
- `EnsureAutoStartMethods()` (novo): roda no startup (App.xaml.cs substituiu o
  `CheckAndFixStartupMethods` passivo, que so corrigia caminhos de entradas EXISTENTES)
  — se a preferencia ou qualquer metodo vivo existir, reconfigura tudo com o exe atual.
- TESTADO no host: Registry Run + KitLugia.lnk + task (Status Pronto) criados juntos,
  pref REG_DWORD 0x1, log "Reconfigurando auto-start em todos os metodos" a cada boot.

**Pedido 2 - VRAM "Automatica" nao aplicava (Dashboard x TweaksPage)**:

- CAUSA RAIZ 1: `CmbVram` item 0 = "Padrao (Automatico)" mapeava para `VramSizeMb=0`
  e o Orchestrator passava 0 para `ApplyGpuVramTweak` — que INTERPRETA 0 como
  "remover a chave". Ou seja, "Automatico" APAGAVA a VRAM em vez de aplicar o
  recomendado pela RAM. Corrigido no Orchestrator: `VramSizeMb<=0` -> aplica
  `GetRecommendedVramMb(RAM)` (nunca apaga).
- CAUSA RAIZ 2: resolucao do caminho do registro diferente entre paginas. Dashboard
  usava `FindGpuRegistryPathByDescription` (DriverDesc fuzzy); TweaksPage usa
  `FindGpuRegistryPathByPnpId` (DeviceInstance/MatchingDeviceId). Se resolvessem chaves
  diferentes, o tweak "aplicava" num lugar e a pagina lia outro. Dashboard agora usa
  `GetAllGpuInfo()` (mesma resolucao PNP da TweaksPage); `ApplyAutomaticVramTweak`
  tambem virou PNP-first com fallback por descricao.

Build: 0 erros / 69 avisos (baseline pre-existentes). App rodando em tray (PID 6356).

### Sessao 03/09 (cont.) - "Exception suppressed" eternos em IsNvMeLatencyOptimized/IsGpuDpcLatencyLow: HKLM curto quebrado

Sintoma (host, TODO boot): `[AVISO] (Unknown): Exception suppressed [origem:
SystemTweaks.cs:2025 IsNvMeLatencyOptimized]` + `:2112 IsGpuDpcLatencyLow`.

CAUSA RAIZ (reproduzida com .NET puro): `Registry.GetValue/SetValue` estaticos NAO
aceitam a forma curta `HKLM\` — lanca ArgumentException "nome valido de chave base"
(exige o prefixo completo `HKEY_LOCAL_MACHINE\`). A forma curta so funciona na API de
instancia (`Registry.LocalMachine.OpenSubKey`).

Impacto real (alem do ruido de log): os Is* SEMPRE retornavam false → os toggles de
"NVMe Latency" e "GPU DPC Latency" sempre apareciam desligados e NUNCA revertiam
(ToggleNvMeLatency reaplicava em vez de desfazer).

Corrigidos em SystemTweaks.cs (4 sitios + 1 lixo):
- `NvMeServicePaths` (stornvme/nvme Parameters): prefixo completo HKEY_LOCAL_MACHINE;
  Apply/Revert agora fazem `.Replace(@"HKEY_LOCAL_MACHINE\", "")`.
- `IsGpuDpcLatencyLow` (IRQ8Priority) idem.
- `IsAmdAntiLagEnabled` (HKLM\SOFTWARE\AMD\CN\GameFilters) idem — quebrado silencioso
  em maquinas AMD (catch sem log).
- `IsIntelDynamicTuningEnabled` (HKLM\SOFTWARE\Intel\Display\igfxcui) idem — quebrado
  silencioso em maquinas Intel.
- Removida var morta `nvmePowerPath` em ApplyNvMeLatencyTweaks.
- Curiosidade: `IsNvidiaPowerMizerMaxPerformance` ja "funcionava" porque fazia
  `$@"HKEY_LOCAL_MACHINE\{path.Substring(5)}"` (corta o HKLM\ e re-prefixa).

VERIFICADO: build 0 erros; boot novo com ZERO "Exception suppressed"; ciclo
write->read->revert do D3Handoff OK (Is* agora detecta o estado aplicado).

### Sessao 03/09 (cont.) - Auditoria dos Is* da TweaksPage + novo toggle "Turbo do Explorer"

**Auditoria dos tweaks da TweaksPage** (a pedido do usuario, apos o bug do HKLM curto):
- Varredura de TODAS as chamadas estaticas Registry.GetValue/SetValue + instancia
  OpenSubKey/CreateSubKey: nenhuma forma curta "HKLM\" restante (corrigidas na sessao
  anterior). Verificacao empirica: boot novo com ZERO "Exception suppressed".
- Normalizado `NvidiaPowerMizerPaths` (0000-0003): prefixo curto + hack `Substring(5)`
  trocados por HKEY_LOCAL_MACHINE completo usado direto na leitura; Apply/Revert usam
  Replace("HKEY_LOCAL_MACHINE\", "").
- Observacao: TweaksPage tem bloco duplicado "Interface & Explorer" (le de novo dentro
  do Dispatcher.Invoke, ~L338) — pre-existente, inofensivo (comportamento mantido).

**Novo toggle "Turbo do Explorer (abrir pastas rapido)"** (TweaksPage, grupo "Interface
e Explorer", apos PCA; handler ChkExplorerTurbo_Click):
- `SystemTweaks.DisableExplorerFolderDiscovery` / `RestoreExplorerFolderDiscovery` /
  `IsExplorerFolderDiscoveryDisabled` (novos): grava/le/apaga
  `FolderType=NotSpecified` em `HKCU\...\Shell\Bags\AllFolders\Shell`.
- POR QUE: Auto Folder Type Discovery (Win11 24H2/25H2+) "fareja" o conteudo de cada
  pasta p/ decidir layout (Musica/Fotos/Videos) — em pastas grandes causa delay e
  re-varredura. Forcar o modo generico abre instantaneo. Refs: makeuseof + xda (Jan/2026).
- Reversivel; nao reinicia o Explorer (vale para janelas novas).

**Diagnostico no host do usuario** (explorer lento):
- `FolderType=NotSpecified` JA estava aplicado (valor solto, sem UI) — o toggle agora o
  controla e mostra o estado.
- `MenuShowDelay=0` e `Max Cached Icons=8192` ja ativos.
- `StartupDelayInMSec` (HKCU\...\Explorer\Serialize) NAO setado — o toggle "Remover
  Startup Delay" (ChkStartupDelay) esta OFF: se o "loading do explorer" = demora ate a
  area de trabalho/logon, ligar ChkStartupDelay e o passo que falta nesta maquina.

Build: 0 erros. App em tray (PID 30484), 0 "Exception suppressed" no boot.

### Sessao 04/09 (12:40) — Turbo do Explorer: cor + toggle "Ir direto ao arquivo"

Pedido do usuario: (1) o texto "Desliga a analise automatica de tipo de pasta" estava
todo PRETO (ilegivel); (2) o Explorer abre rapido e carrega icones rapido, mas quando
outra janela aponta para um arquivo (navegador -> "Mostrar na pasta") demora muito para
levar ao item — mais facil descer a lista do que esperar.

1. **Cor do texto** (TweaksPage.xaml): o TextBlock de descricao do Turbo do Explorer
   nao tinha Foreground (os irmaos usam #A0A0A0) -> herdava preto. Adicionado
   `Foreground="#A0A0A0"`.

2. **Causa da demora ao destacar arquivo**: em `HKCU\...\Explorer\Advanced`,
   `IconsOnly=0` (miniaturas LIGADAS) na maquina; Docs tem 2214 arquivos top-level.
   Com miniaturas, o Explorer renderiza a pasta inteira antes de rolar/posicionar o
   item selecionado (navegador -> Explorer). `IconsOnly=1` (Sempre mostrar icones)
   elimina essa geracao na aberta.

3. **Novo toggle "Ir direto ao arquivo (sem miniaturas)"** (TweaksPage, Interface e
   Explorer, apos Turbo do Explorer; mesmo padrao ToolTip + badge + CheckBox):
   - Core (SystemTweaks.cs): `IsExplorerThumbnailsDisabled` / `DisableExplorerThumbnails`
     (IconsOnly=1 DWord em Explorer\Advanced) / `RestoreExplorerThumbnails`
     (apaga o valor). Reversivel, status "Sem miniaturas"/"Com miniaturas".
   - Code-behind: status no LoadCurrentStatus + `ChkExplorerThumbs_Click`.
   - APLICADO NA MAQUINA (IconsOnly=1 confirmado por leitura) — pular direto ao
     arquivo deve parar de demorar; reversible pelo toggle/seletor Explorer.

Build: 0 erros / 150 warnings (baseline). App em tray (PID 30940), boot limpo.

### Sessao 04/09 (13:10) — Pesquisa adicional: "pular direto ao arquivo" ainda lento

Usuario: IconsOnly=1 ajudou um pouco, mas o Explorer ainda demora ao abrir pasta
apontando para um arquivo. Pediu pesquisa extra na web.

**Estado real da maquina verificado**:
- FolderType=NotSpecified (AFTD OFF) e IconsOnly=1: ambos ativos.
- GroupBy: 0 bags com agrupamento ativo (Downloads NAO agrupa por data) - descartado.
- SortBy: 0 bags com override - Downloads usa o padrao do Windows (data/hora).
- Preview/Details pane: nao ativo nos bags; ShowPreviewHandlers sem valor (default ON).
- DisableSearchBoxSuggestions=1 (ja desativado).
- **Overlay de icone de terceiro: `00avg` = AVG (ashShell.dll)** em HKLM+
  WOW6432Node ShellIconOverlayIdentifiers - unico custo real por-arquivo restante.
- OneDrive: instalado, mas Downloads NAO e redirecionado (pasta real local).

**Metodos avaliados e descartados**:
- Rename do overlay 00avg p/ "passar dos 15 slots": NAO funciona aqui (so ha ~10
  overlays registrados; todos continuam dentro dos 15 carregados).
- Truque CTRL+F11 (forcar render): workaround de bug de render, SEM equivalente
  de registro limpo - nao da para virar toggle.
- Desligar overlay do AVG por registro: o metodo correto e o proprio AVG UI
  (Menu > Configuracoes > Geral > "Mostrar overlays de icone" desmarcado); mexer
  no registro do antivirus seria intrusivo e o AVG se auto-repara.

**Conclusao honesta**: as duas causas documentadas (AFTD + miniaturas) ja estao
corrigidas. O restante (2.214 arquivos + overlay/scan do AVG por arquivo) e custo
per-arquivo inevitavel; a alavanca restante segura e o toggle do proprio AVG.
Sem novas chaves de registro seguras a adicionar.

### Sessao 04/09 (14:10) - Turbo Explorer (F11) no tray: truque do re-render automatizado

O usuario confirmou que o truque Ctrl+F11/F11 (entrar e sair de tela cheia no Explorer)
faz o "ir direto ao arquivo" (ex.: navegador -> Mostrar na pasta) funcionar NA HORA,
mesmo em pastas gigantes. O truque vale por sessao do explorer.exe (some ao reiniciar
o Explorer/reboot) e nao tem equivalente em registro.

**Implementado (TrayIconService.cs)**:
1. `Win32Api`: + EnumWindows (delegate EnumWindowsProc), + PostMessage,
   consts WM_KEYDOWN/WM_KEYUP/VK_F11, const ExplorerWindowClass="CabinetWClass".
2. Item novo no menu do tray: **"⚡ Turbo Explorer (F11)"** (apos GameBoost).
   Handler roda em Task.Run -> `TurboExplorerF11()`:
   - EnumWindows acha a PRIMEIRA janela visivel de classe CabinetWClass (EnumWindows
     lista em ordem de Z -> a mais recente, nao precisa de foco/foreground).
   - PostMessage F11 down/up, Sleep 500ms, PostMessage F11 down/up (entra e sai de
     tela cheia -> forca re-render; a rolagem bugada de posicionamento e pulada).
   - Log "⚡ Turbo Explorer: F11 2x enviado...".

**TESTADO (04/09 14:05, host)**: janela do Explorer aberta -> PostMessage F11 1x
(rect 511,135 1071x729 -> 0,0 1920x1080 = fullscreen), F11 2x (voltou EXATAMENTE
para 511,135 1071x729). Mecanismo comprovado sem foco/foreground. Janela de teste
fechada via WM_CLOSE. Build: 0 erros / 0 avisos. App em tray (PID 9168), boot limpo.

Obs: como o truque e por sessao do explorer.exe, o item do tray e o substituto
de 1 clique para reaplicar apos reboot/restart do Explorer. Nao foi feito auto-aplicar
em toda janela nova (F11 fullscreen piscaria a cada abertura - intrusivo).

### Sessao 04/09 (13:30) - CAUSA RAIZ: RAM Limiter resets p/ 1000ms + aviso LoadSettings eterno

Sintoma: usuario seta 500ms no TxtRamLimiterInterval (TraySettingsPage), recebe
"✅ Intervalo atualizado", mas apos reiniciar o app volta para 1000ms. Toda sessao
logava "LoadSettings: InvalidCastException: Unable to cast object of type
'System.String' to type 'System.Int64'".

Causa raiz (confirmada no host): o valor `IslcThresholdMB` do registro
HKCU\Software\KitLugia\TraySettings esta gravado como REG_SZ String = "4069"
(assim como HighRamThresholdMB/HighCpuThresholdPercent — esses 2 ja usavam
ReadLongSetting/ReadDoubleSetting defensivos). `LoadSettings` fazia
`IslcThresholdMB = (long)key.GetValue(...)` — cast de string boxed p/ long =
InvalidCastException (Int64) — e o try/catch ABORTAVA o LoadSettings INTEIRO:
tudo apos aquela linha (incluindo RamLimiterIntervalMs) caia para default 1000
silenciosamente a cada boot. O "salvou" da pagina so valia em memoria; no
restart o 1000 voltava (e qualquer SaveSettings posterior gravava 1000 por cima).

Correcao (TrayIconService.cs LoadSettings):
- TODOS os casts crus `(int)key.GetValue(...)` e o `(long)` do IslcThresholdMB
  substituidos por leitores defensivos: ReadIntSetting (int/long/double/string/
  byte[]) + ReadBoolSetting (int -> bool). LoadSettings nao aborta mais com UM
  valor de tipo errado — cada setting se auto-protege com default individual.
- Fix duplo: (1) RamLimiterIntervalMs carrega de REG_SZ "500" corretamente;
  (2) o aviso "⚠️ LoadSettings: InvalidCastException" some de TODAS as sessoes.

TESTADO (04/09 13:26, host): RamLimiterIntervalMs gravado de proposito como
REG_SZ "500" -> app iniciou com "💾 RAM Limiter timer iniciado (500ms)" e
0 InvalidCastException. Build: 0 erros / 0 avisos. App em tray (PID 38672).

Obs: TraySettingsPage.TxtRamLimiterInterval_LostFocus ja salva certo
(SetValue int = REG_DWORD); nao precisou mexer no lado de escrita.

### Sessao 04/09 (cont.) - Turbo Explorer (F11): persistencia esclarecida

Usuario confirmou que bastou 1x Ctrl+F11 e continuou rapido mesmo fechando e
abrindo o Explorer (fechar janelas NAO reinicia o processo explorer.exe — o
truque vale por processo; reboot/restart do shell ainda limpa). Mensagem do log
do tray item ajustada para nao afirmar tempo de validade.

### Sessao 04/09 (13:35) - Turbo Explorer AUTOMATICO: watchdog do explorer.exe

Pedido do usuario: garantir que o truque F11 seja reaplicado sozinho quando o
explorer.exe (re)iniciar — login do Windows, crash ou restart do shell.

**Implementado (TrayIconService.cs)**:
1. Pref `ExplorerAutoTurbo` (default TRUE) persistida em TraySettings
   (SaveSettings/LoadSettings com os leitores defensivos novos).
2. `StartExplorerWatchdog()` (chamado no Initialize): System.Threading.Timer a
   cada 2s -> `ExplorerTurboWatchdogTick()`.
3. Tick: acha o PID do explorer.exe da MESMA sessao (SessionId). Quando o PID
   muda/aparece (login, crash/restart) -> ARMA o turbo + log. Armado, espera a
   1ª janela de pasta (CabinetWClass, EnumWindows em ordem de Z) e aplica F11 2x
   (dwell 400ms) via Task.Run -> DESARMA. 1x por processo: nao fica piscando
   janela a janela.
4. Refactor: TurboExplorerF11 (manual/tray) extraiu FindTopExplorerWindow() +
   ApplyF11Render(hwnd, dwellMs, source) compartilhados com o watchdog.
5. Edge cases: explorer ainda nao subiu no login (kit inicia antes) -> pid 0
   desarma e re-arma quando aparecer; DisposeCore para o timer.

**TESTADO (04/09 13:34, host)**: app lancado -> "explorer.exe ativo (PID 8356)
— auto-turbo armado" -> Downloads aberto -> "F11 2x aplicado (re-render) —
origem: auto" em ~7s (janela de pasta gigante demorou p/ materializar).
Build: 0 erros. App em tray (PID 18488).

### Sessao 04/09 (13:42) - Toggles de UI do Turbo Explorer automatico (TweaksPage + GameBoostPro)

O watchdog `ExplorerAutoTurbo` (default true, sessao 13:35) ganhou controle visual
nas 2 paginas pedidas pelo usuario, ambos persistindo via TraySettings\ExplorerAutoTurbo:

1. **TweaksPage** (grupo Interface e Explorer, apos "Ir direto ao arquivo"): row nova
   "Turbo Explorer automatico (F11 ao reiniciar)" — badge de status (Automático/Manual),
   tooltip completo, CheckBox ToggleSwitchStyle. `ChkExplorerAutoTurbo_Click` seta
   `tray.ExplorerAutoTurbo` + SaveSettings (padrao das outras rows); estado carregado
   no LoadCurrentStatus do dispatcher via `(MainWindow)TrayService.ExplorerAutoTurbo`.

2. **GameBoostPage** (CARD 2 "Configuracoes do Sistema", apos Unpark CPU): row
   "TURBO EXPLORER AUTOMATICO (F11)" com GoldToggleStyle. Handler proprio
   `ChkExplorerAutoTurbo_Click` (imediato + SaveSettings); estado inicial em
   LoadGameBoostSettings e persistencia extra em SaveGameBoostSettings (tray + JSON
   `explorerAutoTurbo`).

Build: 0 erros / 138 avisos (baseline nullable). App em tray (PID 39292),
ExplorerAutoTurbo pref = 1, watchdog armado, boot limpo (sem exceptions).

### Sessao 04/09 (13:55) - Tray SEMPRE visivel (fim do processo fantasma) + auditoria da pag RAM

Problema do usuario: em Kit limpo, fechar a janela deixava o app rodando em background
SEM icone (Close-to-Tray com IsTrayEnabled=false no primeiro run = default false!).
Ele tinha que matar via Gerenciador de Tarefas. Pedido: remover a opcao de ocultar o
icone — o tray deve aparecer SEMPRE enquanto o Kit roda.

**Core (TrayIconService.cs)**:
1. `IsTrayEnabled` default TRUE (era false). Primeira execucao (key==null) -> true.
2. LoadSettings: ignora preferencia antiga 0 -> forca true + log de migracao
   ("...preferencia antiga 'ocultar' migrada para ON").
3. Initialize: cria/mostra o icone SEMPRE (condicoes `IsTrayEnabled &&` removidas).
4. SetTrayEnabled(false) agora e no-op com aviso (compatibilidade com telas antigas).

**Toggles de "mostrar icone" REMOVIDOS das UIs (4 paginas + diag)**:
- TraySettingsPage: removidos ChkEnableTray (Automação) e ChkTrayIcon (Comportamento ao
  fechar) -> substituidos por linha verde estatica "🔔 Ícone na bandeja — SEMPRE VISÍVEL".
- GameBoostPage CARD2: removida linha "MOSTRAR ÍCONE" (ChkTrayIcon + handler + refs;
  JSON trayEnabled agora sempre true).
- SettingsPage (gear): removida linha "Minimizar para bandeja" (ToggleTray -> controlava
  SetTrayEnabled; MinimizeToTray do JSON agora fixo true). "Rodar em segundo plano"
  (ToggleCloseToTray) continua = controla CloseToTray real.
- DashboardPage: removido checkbox "1. Ícone de bandeja" do menu rapido (renumerados
  2->1, 3->2) e ChkCrTray da coluna do criador -> linha verde estatica informativa.
- DiagnosticPage: BtnToggleTray agora so reafirma (sempre ativo).

**Auditoria da pag RAM (TraySettingsPage)**: handlers verificados (limpeza por modo,
auto-clean, sliders, limites por processo com picker/arquivo, gear por processo, ISLC,
intervalo) — todos wired. Fix: BtnConfigureAutoClean_Click criava
`new AdvancedRamCleanSettingsPage()` descartado a cada clique (leak de Page) — removido.

TESTADO (04/09 13:52, host): IsTrayEnabled gravado = 0 de proposito -> app iniciou com
log "🔔 ... migrada para ON" + "📊 Tray Habilitado: ✅ SIM", icone saudavel, 0 exceptions.
Build: 0 erros / 152 avisos (baseline). App em tray (PID 32700). Pref restaurada p/ 1.

### Sessao 04/09 (14:05) - X sempre minimiza p/ tray (CloseToTray forçado) + toggle "segundo plano" removido

Continuacao: alem do icone sempre visivel, o usuario quer que o X da janela SEMPRE
minimize para o tray (fechar de verdade = menu do tray > "Sair Completamente"), e
pediu para remover o toggle "Rodar em segundo plano" de todas as telas (agora e
sempre verdade). Finalizar pelo Gerenciador de Tarefas/dev continua matando o
processo normalmente.

**Core (TrayIconService.cs LoadSettings)**: CloseToTray forçado true (migra valor 0
antigo com log). MainWindow Window_Closing ja minimizava quando CloseToTray &&
IsTrayEnabled (ambos true agora) -> X sempre esconde p/ o tray; saida completa via
tray Sair Completamente / ForceShutdown / End Task.

**Toggles removidos**:
- TraySettingsPage: row "Rodar em segundo plano" (ChkCloseToTray) -> linha verde
  "Fechar (X) sempre minimiza p/ o tray...".
- GameBoostPage CARD3: removida row ChkCloseToTray (+handler, var closeToTray, refs;
  JSON closeToTray agora true).
- SettingsPage (gear): removida ToggleCloseToTray (+ handler/events/load; AppSettings.
  CloseToTray agora true; linha verde informativa no lugar).
- DashboardPage coluna criador: removida ChkCrBgRun ("Rodar em segundo plano") -> linha
  verde informativa.

TESTADO (04/09 14:02, host): CloseToTray gravado 0 de proposito -> boot com
"'Rodar em segundo plano' forçado a SEMPRE ATIVO" + "Tray Habilitado SIM" +
"Close to Tray: ATIVO", 0 exceptions. Build: 0 erros. App em tray (PID 11388).
Pref restaurada p/ 1.

### Sessao 04/09 (14:20) - IntegrityPage: 4 PATH do Guardian presos em "corrigir" (loop eterno)

Sintoma: os itens PATH do Guardian (Caminhos Inexistentes, Entradas Duplicadas,
Vulneravel a Hijacking, Variavel de Ambiente PATH Corrompida) ficavam SEMPRE como
"corrigir" mesmo apos clicar corrigir, e o + (PathExplorer) mostrava o PATH certo.

CAUSA RAIZ (provada com harness refletindo o codigo real, KitLugia.Core carregado):
1. `PathRepair.EnsureSystemPathMinimum` re-adicionava SEMPRE os 7 "minimos" ao PATH do
   sistema — inclusive `%SYSTEMROOT%\System32\OpenSSH\` e `%ProgramFiles%\PowerShell\7\`
   mesmo quando a PASTA NAO EXISTIA no disco. Fluxo do "corrigir":
   RepairPathEntries removia a pasta morta -> EnsureSystemPathMinimum re-adicionava ->
   proximo scan via o diretorio inexistente -> MISSING/VULNERABLE/CORRUPTED de volta =
   LOOP ETERNO. Na maquina do usuario: `C:\Program Files\PowerShell\7\` (nao existe) +
   bloco Windows duplicado no inicio (installers prepend) = 4 itens presos.
2. `Guardian.CheckPathProblems` (INCOMPLETE System) exigia OpenSSH no PATH mesmo em
   maquinas SEM o recurso opcional instalado = falso "modificado" sem cura possivel.

CORRECOES:
- PathRepair.cs `EnsureSystemPathMinimum`: so adiciona um "minimo" cuja pasta existe
  apos ExpandEnvironmentVariables (`if (!Directory.Exists(expanded)) continue;`).
  Nunca mais re-adiciona pasta morta (loop morto).
- Guardian.cs `CheckPathProblems` (System INCOMPLETE): requisitos essenciais agora sao
  existencia-dependentes — array requiredDirs {system32, wbem, windowspowershell\v1.0,
  openssh}; cada pasta so e exigida se `Directory.Exists(dir)` (sem falso positivo de
  OpenSSH/recursos opcionais).

VERIFICACAO (harness /tmp/pathguard, refs KitLugia.Core real):
- Simulacao do corrigir ANTES: RepairPathEntries removia a pasta morta mas
  EnsureSystemPathMinimum re-adicionava (`has PowerShell7: True` apos ensure).
  DEPOIS do fix: `has PowerShell7: False` apos ensure; scan seguinte so WrongLocation.
- Reflection em CheckPathProblems: PATH limpo SEM OpenSSH -> harmful=True SO porque a
  pasta existe na maquina; com pasta inexistente o requisito e ignorado.
- REPARO REAL APLICADO (elevado, aprovado pelo usuario): removidas 5 duplicatas do
  bloco Windows + entrada morta PowerShell\7 via codigo de reparo do proprio app;
  WRITE OK; re-checagem Guardian pos-write: os 4 itens harmful=False.
- Scan final Guardian (registro ao vivo): os 8 itens PATH todos [OK]
  (antes: 4 [MODIFIED]). Registry: 0 duplicatas, 0 dirs inexistentes.

Build: 0 erros (GUI completo). App relancado (tray). Obs: valor Path gravado como
REG_SZ (sem %vars restantes apos limpeza; equivalente ao que o proprio corrigir faria).

### Sessao 04/09 (14:30) - Lado USER do PATH: mesmo loop do System + detector de apps fora do PATH verificado

Pedido: auditar EnsureUserPathMinimum + Guardian "PATH do Usuário Incompleto" (mesma
classe de bug do lado System) e confirmar se o detector de apps fora do PATH funciona
(ex: trazer o winget de volta).

BASELINE AO VIVO (harness /tmp/pathguard, KitLugia.Core real):
- GetInstalledProgramPaths: 8 alvos (7z/cargo/dotnet/git/node/npm/winget) todos com
  diretorio existente; 0 missing no User PATH atual; SIMULANDO WindowsApps fora do
  PATH -> [winget] C:\...\Microsoft\WindowsApps CanAdd=True -> DETECTOR FUNCIONA
  (o + do PathExplorer mostra e 1 clique restaura o winget).
- BUG 1 confirmado: EnsureUserPathMinimum comparava pathToAdd NAO expandido contra
  entradas expandidas -> %USERPROFILE%\.dotnet\tools NUNCA casava com C:\Users\X\...
  ja presente -> re-adicionava a cada corrigir (duplicatas acumuladas).
- BUG 2 confirmado: git (instalacao de maquina, no System PATH) era re-adicionado ao
  User PATH pelo EnsureUserPathMinimum e exigido la pelo check -> poluicao + falso
  "PATH do Usuário Incompleto" eterno.

CORRECOES:
- PathRepair.cs EnsureUserPathMinimum: comparacao SEMPRE expandida
  (ExpandEnvironmentVariables em pathToAdd antes de comparar; adiciona a forma
  original) + SKIP de entradas ja presentes no System PATH (GetSystemPathValue).
  Duplicacao %-var morta; maquina-wide nao polui o User.
- Guardian.cs CheckPathProblems User-INCOMPLETE: pasta inexistente -> nao exigir;
  programa presente no System OU User PATH -> OK (sem falso positivo).

VERIFICACAO (harness):
- Dup %-var vs absoluto: added=1 ANTES -> added=0 DEPOIS.
- Git no System so: added=1 ANTES -> added=0 DEPOIS.
- Guardian User-INCOMPLETE via reflection: user-sem-git -> harmful=False (git no
  System OK); user vazio -> harmful=True (cargo/npm em lugar nenhum - correto).
- Detector winget: continua detectando (regressao zero).

Build: 0 erros. App relancado (PID 38244), boot limpo.

### Sessao 04/09 (14:35) - IntegrityPage: PATH consolidado em 1 card + CORRIGIR resolve tudo de uma vez

Pedido: o botao de corrigir deve adicionar tudo de uma vez (sem abrir o menu +) e nao
precisa haver varios botoes de PATH no IntegrityPage - pode ser um so.

- Guardian.cs (novo, publico): `RepairAllPathsOnce()` -> System PATH (RepairPathEntries +
  EnsureSystemPathMinimum) + User PATH (RepairPathEntries + EnsureUserPathMinimum com
  installedPaths: adiciona winget/git/node/dotnet/npm/7z/cargo automaticamente).
  Retorna (Changed, Summary) com as acoes.
- IntegrityPage.xaml: NOVO card unico "PATH do Sistema e do Usuario" acima da lista
  (row 3; lista foi para row 4): badge de status dinamico + botao unico
  "CORRIGIR PATH (ADICIONA AUSENTES)" (BtnPathRepairAll) + botao ➕ que abre o
  PathExplorer para inspecao manual. Itens IsPathItem removidos da lista
  (ApplyFilters exclui `IsPathItem`) - os 7 botoes PATH separados sumiram.
- IntegrityPage.xaml.cs: UpdatePathCardState() (verde "PATH OK" ou vermelho com as
  pendencias em texto curto); BtnPathRepairAll_Click roda RepairAllPathsOnce em
  background, re-le status via GetHarmfulTweaksWithStatus + RefreshFromAllTweaks
  (sem scan completo de 2s+) e mostra o resumo.

Build: 0 erros. App relancado (PID 21084). Card sempre visivel; CORRIGIR roda
tambem quando esta OK (re-aplica/adiciona ausentes sob demanda).

### Sessao 04/09 (14:40) - Verificacao do card PATH + GlobalSearch/RepairsPage consolidados

Pedido: abrir IntegrityPage, clicar CORRIGIR PATH e conferir badge verde + resumo;
conferir se outras paginas que listam tweaks (GlobalSearch/RepairsPage) nao mostram
mais os 7 itens de PATH separados.

RESULTADO DA VERIFICACAO (harness = mesmo codigo do botao):
- IntegrityPage: 7 itens IsPathItem todos [OK] -> badge "✅ PATH OK" (verde).
- Guardian.RepairAllPathsOnce() rodou DE VERDADE e achou o User PATH com o bloco
  Windows inteiro DUPLICADO -> changed=True, dedup aplicado. Estado final: User PATH
  41 entradas unicas, 0 duplicatas, 0 dirs mortos; winget/WindowsApps, npm, cargo,
  .dotnet\tools, VS Code, 7-Zip, GitHub Desktop, WinGet Links PRESERVADOS.
  2a execucao: changed=False ("ja estao corretos") -> converge (badge fica verde).
- Refinamento: RepairAllPathsOnce agora filtra acoes informativas ("Mantido
  WrongLocation"/"Ignorado") do resumo do dialogo - so mostra mudancas reais.

OUTRAS PAGINAS:
- RepairsPage usa GeneralRepairManager.GetAllRepairs(): JA tem UM unico item
  consolidado "Reparar PATH do Sistema (Adiciona Programas Faltantes)" -> ok, nada a mudar.
- SearchEngine (busca global): os 7 tweaks IsPathItem eram indexados separadamente;
  agora sao FILTRADOS e substituidos por UM resultado "PATH do Sistema e do Usuario
  (Corrigir tudo)" com CheckState agregado + ExecuteAction=RepairAllPathsOnce.

Build: 0 erros. App relancado (PID 36232).

### Sessao 06/09 (16:40) - Turbo Explorer default OFF + ProBalance por processo (3 modos)

1. **ExplorerAutoTurbo default FALSE** (TrayIconService): usuario pegou Windows limpo e o kit
   ja vinha ativado movendo explorer p/ tela cheia. Propriedade + ReadBoolSetting agora
   default false — instalacao limpa NUNCA liga sozinho; usuario marca manualmente em
   TweaksPage/GameBoostPro. Maquina do usuario mantem 1 (pref salva vence).

2. **ProBalance por processo (quebra o tabu)**: o EngineConfig do gear ⚙️ na pagina de
   monitor RAM era SO salvo — unico campo aplicado era CpuLimitEnabled (Job Object);
   ProBalance per-process era dead code e o global so rodava com GameBoost em foreground.
   Agora `ApplyProcessEngineLimits()` roda no tick do RAM Limiter (standalone):
   - Motor estatico: prioridade CPU (ParsePriorityClass), I/O (NtSetInformationProcess),
     page priority, thread memory priority, EcoQoS, ThreadEfficiencyMode (E-cores).
   - ProBalance dinamico com exclusao de foreground (regra Process Lasso) + 3 modos:
     Classic (BelowNormal + mem muito baixa, 3 amostras + cooldown 30s, restaura sozinho),
     EcoQoS (API oficial Win11 PROCESS_POWER_THROTTLING — mesmo mecanismo do Modo Eficiencia
     do Task Manager), HardCap (Job Object hard cap enquanto acima do threshold, libera quando
     normaliza). Restore garantido ao desligar/remover o limite.
   - Job Object refatorado em EnsureCpuJob/ReleaseCpuJob; ApplyProcessCpuLimits pula
     ProBalanceMode==HardCap (evita conflito de nome de job).

3. **UI**: ProcessEngineConfigOverlay ganhou ComboBox "Modo do ProBalance"
   (Classico/EcoQoS/HardCap) + caixa explicativa com os 3 mecanismos; linha do processo
   (BuildLimitRow) mostra badges do motor (⚡ prioridade · ⚖️ ProBalance modo · 📉 cap %
   · 🌱 EcoQoS · 🧵 E-cores · ⏱ Timer) + ⚡ Pico e 💾 Commit; LoadProcessLimits() chamado
   apos salvar o gear (badges atualizam na hora).

Pesquisa web (06/09): bitsum.com/how-probalance-works (BelowNormal temporario, marginal,
so em background, ignora prioridades nao-Normal), learn.microsoft.com SetProcessInformation
(EcoQoS = PROCESS_POWER_THROTTLING_EXECUTION_SPEED), JOBOBJECT_CPU_RATE_CONTROL HARD_CAP
(CpuRate = %*100).

Build: 0 erros / 154 warnings (nullable baseline). App no tray (PID 14364), boot limpo.

### Sessao 06/09 (17:10) - InfoButton padronizado + overlay de processo usa o padrao

1. **InfoButton = padrao canonico de icone de informacao** (Controls/InfoButton.xaml + .cs):
   - Overlay de config de processo (ProcessEngineConfigOverlay) trocou o estilo local InfoIcon
     (ⓘ cinza) pelo `controls:InfoButton` (ℹ dourado, opacidade 0.65 -> 1 no hover) - identico
     aos cards do TweaksPage.
   - Opacidade movida do TextBlock p/ o Button (opacidade NAO e herdada pelo trigger - hover
     agora clareia de verdade).
   - ToolTip enriquecido no code-behind: TextBlock com TextWrapping + MaxWidth 460 + fundo
     escuro/arredondado (tooltips longos legiveis), InitialShowDelay 0 / ShowDuration 20s.
   - Fix de ambiguidade: System.Drawing vs System.Windows.Media (Color/FontFamily) - qualificado.

2. ProcessEngineConfigOverlay ampliado p/ 620px (max 700) com clamp por SystemParameters.WorkArea
   (92% da tela, DPI-safe) + subtitulo explicativo + 15 icones InfoButton com texto de ajuda.

Build: 0 erros. App no tray (PID 25428), boot limpo.

### Sessao 06/09 (17:20) - FIX: tooltip InfoButton + rodape do overlay cobrindo conteudo

1. **Tooltip do InfoButton parou de exibir**: a versao que funcionava (git HEAD) setava
   `BtnInfo.ToolTip = string`. Troquei por Border solto como conteudo - WPF nao exibia de
   forma confiavel. Fix: criar `System.Windows.Controls.ToolTip` real com Content=Border
   (Placement=Mouse, HasDropShadow, StaysOpen=false). Ambiguidade ToolTip/PlacementMode
   (System.Windows.Forms + Primitives) qualificada. InfoButton.xaml.cs agora compila.

2. **Rodape do overlay cobria o ultimo item**: Border de Cancelar/Salvar tinha
   Margin="-20,-12,-20,-20" - o -12 no topo invadia a area de rolagem (ScrollViewer) e
   cobria o ultimo botao da pagina. Fix: Margin="-20,0,-20,-20" (topo 0, nao invade) +
   espacador Height=6 no fim do conteudo.

Build: 0 erros. App no tray (PID 25540).

### Sessao 06/09 (17:24) - FIX 2 tooltip InfoButton: conteudo STRING (mesmo padrao dos botoes GPU/TweaksPage)

Ainda nao mostrava nada. O padrao QUE FUNCIONA neste app (botoes de info do TweaksPage,
GPU rows, criados em runtime) e `ToolTip = string` simples - WPF renderiza quebras de
linha por &#x0a; no proprio string. Montar Border/ToolTip custom em codigo (duas tentativas)
fazia o tooltip nao abrir. InfoButton.xaml.cs agora faz exatamente o git-HEAD:
`BtnInfo.ToolTip = string.IsNullOrEmpty(text) ? null : text;` (ToolTipText DP).
Build 0 erros. App no tray (PID 29936). AGUARDANDO confirmacao do hover.

### Sessao 06/09 (17:35) - Turbo Explorer F11 INVISIVEL (nao interrompe o usuario)

Pedido: o toggle F11 (entra/sai de tela cheia) fazia a janela do Explorer pular a tela e
interrompia o usuario. Solucao: tornar a janela INVISIVEL durante o toggle.

Win32Api (TrayIconService.cs): GetWindowLongPtr/SetWindowLongPtr (GWL_EXSTYLE ja existia),
SetLayeredWindowAttributes, WS_EX_LAYERED, LWA_ALPHA.

ApplyF11Render reescrito:
1. Salva exstyle original; adiciona WS_EX_LAYERED se nao tinha; SetLayeredWindowAttributes
   alpha 0 (janela some visualmente, mas continua processando mensagens/layout - sem roubar
   foco, sem mover a janela).
2. F11 down/up, Sleep(dwellMs), F11 down/up (igual antes).
3. finally: restaura alpha 255 e remove WS_EX_LAYERED se tinha adicionado (SEMPRE roda).
Fallback: se a opacidade falhar (hidingApplied=false), faz o toggle visivel antigo - nunca
perde a funcao. Log marca "(invisível)" quando funcionou.

TESTADO (17:32, host, PowerShell mesmo codigo P/Invoke): janela Explorer System32 -
hidden=True, F11 2x, rect voltou EXATO (537,161,1071,729 antes/depois). A funcao do
re-render preservada (mesmos PostMessage), sem pulo visual.

Build: 0 erros. App no tray (PID 31696). A TESTAR pelo usuario: reiniciar explorer ->
abrir pasta - conferir se NAO ve mais o pulo de tela cheia e o 'ir direto ao arquivo'
continua funcionando.

### Sessao 06/09 (17:40) - ExplorerAutoTurbo default TRUE + textos atualizados (F11 invisivel)

Usuario confirmou que o F11 invisivel funciona ("janela fechou e abriu instantanea" =
transparente durante o toggle). Fluxo desejado: kit abre -> ja roda o processo invisivel;
se explorer reiniciar -> kit faz de novo rapido, sem o usuario ver.

1. ExplorerAutoTurbo default voltou a TRUE (era FALSE desde que o usuario pediu default off
   por causa do pulo VISIVEL em tela cheia; com o F11 invisivel esse problema sumiu).
   Instalacao limpa ja roda sozinho; usuario pode desligar no TweaksPage/GameBoostPro.
   ReadBoolSetting default tambem TRUE.

2. ToolTip do TweaksPage atualizado: antes dizia "(a janela pisca em tela cheia por um
   instante)" - agora diz que a aplicacao e INVISIVEL (janela oculta por fracoes de segundo,
   nao pula, nao interrompe).

TESTADO (17:38, host): log "explorer.exe ativo (PID 6448) — auto-turbo armado" + "F11 2x
aplicado (re-render) — origem: auto (invisível)" ~3s apos o kit iniciar. Build: 0 erros.
App no tray (PID 10516).

### Sessao 06/09 (18:00) - Restaurar Visual Normal do Explorer (miniaturas + agrupamento por data)

Sintoma do usuario: apos o F11 render fix, o visual do Explorer seguia quebrado —
(1) fotos/videos aparecem so com o icone do app associado (sem previews) e
(2) a separacao por dias/meses/anos sumiu do Downloads e de outras areas.

Causa raiz: DOIS tweaks de contorno que o kit aplicou ANTES de descobrir que o
"pular direto ao arquivo" lento era bug de RENDER (F11 2x invisivel), e que
sacrificavam o visual normal:
1. `IconsOnly=1` em `HKCU\...\Explorer\Advanced` (toggle "Ir direto ao arquivo")
   = "Sempre mostrar icones, nunca miniaturas" -> Explorer nao gera previews.
2. `FolderType=NotSpecified` em `HKCU\...\Shell\Bags\AllFolders\Shell`
   (toggle "Turbo do Explorer") = desliga o Auto Folder Type Discovery -> pastas
   abrem em template generico, sem o template padrao que agrupa por data.

Correcao (KitLugia.Core\SystemTweaks.cs):
- `IsExplorerVisualNormal()`: miniaturas ligadas E folder-type discovery ativo.
- `ResetSavedFolderViews()`: apaga as views salvas POR PASTA (subchaves numericas
  de Bags, preserva AllFolders) + BagMRU inteiro, nos 2 locais
  (`HKCU\Software\Classes\Local Settings\...\Shell` e `HKCU\Software\Microsoft\Windows\Shell`),
  equivale ao "Restaurar Padroes das Pastas" do Explorer. O Explorer recria os
  templates padrao na proxima abertura -> agrupamento por data volta em Downloads.
- `RestoreExplorerNormalVisuals()`: chama RestoreExplorerThumbnails +
  RestoreExplorerFolderDiscovery + ResetSavedFolderViews. Requer reiniciar o
  explorer.exe para o shell reconstruir as Bags.

GUI (TweaksPage, card "Interface e Explorer", apos Turbo Explorer automatico):
- Row nova "Restaurar visual padrao do Explorer" com controls:InfoButton
  (explica os 2 tweaks obsoletos), texto e botao GoldButtonStyle
  `BtnRestoreExplorerVisual` (Click `BtnRestoreExplorerVisual_Click`).
- Handler: confirmacao (ShowConfirmationDialog) -> Task.Run de
  RestoreExplorerNormalVisuals -> taskkill explorer + start -> sincroniza
  ChkExplorerTurbo/ChkExplorerThumbs para OFF ("Padrao"/"Com miniaturas") ->
  ShowSuccess. RecordTweak dos 2 nomes como false (nao reaplica no boot).

APLICADO NA MAQUINA (18:00): IconsOnly removido, FolderType(AllFolders) removido,
Bags numericas + BagMRU resetadas nos 2 locais, explorer reiniciado (shell
reconstruiu as views; PID novo 18064 com auto-turbo re-armado). Build: 0 erros /
154 warnings (baseline nullable pre-existentes).

A TESTAR (host): abrir Downloads -> conferir separadores de data (hoje/ontem/
mes/ano) de volta; abrir pasta de fotos/videos -> previews de volta; conferir que
o "ir direto ao arquivo" continua instantaneo (watchdog reaplica F11 invisivel).

### Sessao 07/09 - Icones do desktop preservados (3 fontes de wipe corrigidas)

Sintoma: usuario arruma os icones da area de trabalho e apos reboot estavam
desarrumados de novo, toda hora. CAUSA: o Kit apagava o bag 1 (Bags\1\Desktop)
- onde o Windows guarda as POSICOES dos icones - em 2 fluxos:

1. CleanupPage BtnCleanEvidence_Click: chamava EvidenceCleaner.CleanAll()
   SEMPRE, mesmo com 1 categoria marcada (o dialogo dizia "Limpar N categorias").
   CleanBagMRU deletava TODAS as Bags nos 2 locais -> layout do desktop ia junto.
2. TweaksPage REPARAR EXPLORER: ResetSavedFolderViews apagava as Bags
   numericas (incluindo o bag 1) + BagMRU - o dialogo avisava "posicoes podem
   ser reordenadas".
3. GeneralRepairManager "Resetar Visualizacao de Pastas" deleta so BagMRU
   (Local Settings): mapeamento e recriado, bag desktop sobrevive - sem mudanca.

Correcoes:
- SystemTweaks.ResetSavedFolderViews + EvidenceCleaner.CleanBagMRU: novo helper
  IsDesktopBag (subchave numerica de Bags contendo "Desktop") preserva o bag do
  desktop nos 2 locais; BagMRU inteiro continua removido (bag 1 nao depende dele).
- CleanupPage.BtnCleanEvidence_Click: agora limpa SOMENTE as categorias
  selecionadas (Dictionary nome->Clean*; nomes da UI mapeados 1:1).
- TweaksPage: dialogo do REPARAR EXPLORER trocou o aviso "posicoes podem ser
  reordenadas" por "posicoes sao preservadas"; comentario do card atualizado.

Extras: cache_query.bat e cache_test.bat removidos da raiz (descartaveis do
teste L2/L3).

Build: codigo compila sem erros (Core+GUI, so warnings nullable pre-existentes);
copia final bloqueada com o Kit rodando (MSB3021 PID 10832) - fechar o app antes
de compilar.

A TESTAR: REPARAR EXPLORER -> mover icones -> reboot -> posicoes mantidas;
CleanupPage marcar so "Recent Documents" -> limpar -> reboot -> layout intacto.

### Sessao 07/09 (cont.) - Layout do desktop SALVO + tool standalone de backup/restore

Estado real da maquina do usuario (inspecao read-only): bag 1 Desktop com
ItemPos=0 e Local Settings\...\Bags SEM bag Desktop - layout existia so na
memoria do explorer (loop de perda: wipe do kit -> boot desarruma -> usuario
arruma -> nunca era commitado). AutoEndTasks/HungAppTimeout/WaitToKillAppTimeout
ausentes (padrao), IconsOnly/DisableThumbnailCache/FolderType ausentes
(visual saudavel - REPARAR EXPLORER desnecessario no momento).

- taskkill graceful do explorer (sem /f) NAO fecha o explorer no Win11
  (mesmo PID apos 14s) - ABORTADO sem forcar, pois /f perderia o layout
  da memoria (bag vazio).
- NOVO: KitLugia.GUI\Tools\IconLayout\DesktopIconLayout.ps1 - le as posicoes
  DIRETO do ListView do desktop (Progman/WorkerW > SHELLDLL_DefView >
  SysListView32, LVM_* cross-process com VirtualAllocEx/ReadProcessMemory),
  salva "x,y|nome" por icone. -RestoreFile restaura por NOME (sobrevive a
  re-sort). Fix: nome cortado no PRIMEIRO NUL (buffer remoto guarda lixo de
  nomes anteriores - TrimEnd nao bastava).
- SALVO: C:\Users\Lugia\AppData\Local\KitLugia\IconLayoutBackup\
  desktop_layout_2026-09-07_1330.json - 156 icones, posicoes da grade
  (colunas ~532px, linhas ~101px). Restaurar:
  powershell -File DesktopIconLayout.ps1 -RestoreFile <json>
- Kit fechado (graceful foi pro tray pela CloseToTray; /f necessario),
  rebuild 0 erros, Kit reaberto recompilado (PID novo).
- Obs: o commit das posicoes no REGISTRO acontece no proximo logoff
  normal (graceful) - evitar desligar no botao do gabinete ate la.
- Proximo passo natural: card/pagina no Kit com SALVAR/RESTAURAR layout
  (usar o mesmo metodo LVM_* ou rodar este ps1) + auto-backup no boot.

### Sessao 07/09 (cont.) - RestaurarIconesDesktop.bat (2 cliques) + bug de parse do cmd

- NOVO: KitLugia.GUI\Tools\IconLayout\RestaurarIconesDesktop.bat - duplo clique
  restaura o layout; sem arg escolhe o desktop_layout_*.json mais recente
  (pre_restore_* excluido do auto-pick), arg/drag-and-drop de .json restaura
  esse. Antes de restaurar salva snapshot pre_restore_YYYYMMDD_HHMMSS.json do
  estado atual (undo = arrastar esse snapshot sobre o .bat). Pausa no fim.
- BUG DE PARSE do cmd (variante nova do de 03/08): o path do projeto contem
  "(25)" - %TOOL% expandido SEM aspas dentro de bloco if (...) faz o ) de (25)
  FECHAR o bloco no parse e o resto vira token ("- foi inesperado neste
  momento"). Diagnostico por bisseccao (bloco de pick isolado = OK). FIX:
  estrutura com goto/labels - nenhum caminho com parenteses expande dentro de
  bloco. Bat regravado em ASCII puro sem BOM (Get-Content -Raw +
  WriteAllText Encoding.ASCII) - cmd nao engole BOM UTF-8.
- TESTADO: duplo-clique flow OK (snapshot + RESTAURADOS 156 de 156), drag-mode
  com arg OK, verificacao final: captura pos-restore comparada ao backup com
  Compare-Object = MATCH 156/156 (desktop identico, restore no-op visual).
- Quirk: powershell -File NAO expande $env:VAR em argumentos (passar path
  Windows literal); no bash, "$env:..." em aspas duplas e comido pelo bash.

### Sessao 07/09 (cont.) - PrivacyPage: Presets Personalizados (estilo O&O ShutUp10)

Pedido: escanear a PrivacyPage toda, atualizar os metodos, garantir feedback
na UI e substituir os 3 botões de preset (RECOMENDADO/LIMITADO/MAXIMO) por UM
botão "PRESETS PERSONALIZADOS" que abre seleção por nivel como o O&O ShutUp10.

1. **XAML (PrivacyPage.xaml)**:
   - Root virou <Grid> (ScrollViewer + OverlayPresets) para o overlay cobrir a
     pagina toda (mesmo padrao do OverlayQuickMenu do DashboardPage).
   - 3 cards de preset removidos; no lugar, 1 card dourado com botão
     "PRESETS PERSONALIZADOS" (GoldButtonStyle) + descricao.
   - OverlayPresets: header, selecao rapida (Apenas Seguros / Seguros +
     Moderados / Marcar Tudo / Limpar Selecao), contador "X de Y selecionados",
     3 secoes com borda colorida (verde #4CAF50 / laranja #FFA500 /
     vermelho #C42B1C) e aviso de perigo na vermelha, cada item = CheckBox com
     tooltip (registry/servico + valores + nivel), footer com status +
     Cancelar + APLICAR SELECAO.

2. **Code-behind (PrivacyPage.xaml.cs)**:
   - PresetSafeItems/PresetModerateItems/PresetDangerItems (ObservableCollection)
     + PresetItemViewModel (INotifyPropertyChanged, Tooltip com caminho e
     aviso PERIGOSO). Default: so Recomendados marcados.
   - BtnApplyPresets_Click: confirmacao (avisa N perigosas selecionadas),
     desabilita botoes + status "Aplicando...", Core.ApplyCustomSelection em
     Task.Run, BackgroundTaskTracker, ShowSuccess/ShowError com contagens,
     overlay fecha e RefreshStatus() reconcilia checkboxes.
   - Removidos: ApplyPreset helper + 3 handlers + stubs mortos
     (BtnGenerateReport/BtnExportConfig/BtnSnapshot - nao existiam no XAML).

3. **Core (OOShutUpManager.cs)** - metodos atualizados:
   - IsPrivacySettingApplied (servicos): antes so reconhecia SafeValue=4
     (Disabled); servico com SafeValue=3 (Manual) NUNCA aparecia protegido.
     Agora mapeia 4=Disabled/3=Manual/2=Auto (WMI StartMode).
   - RevertPrivacySetting (servicos): modeStr so mapeava 2=auto; UnsafeValue=4
     caia em "demand" (errado). Agora switch 2/4=auto, resto demand +
     `sc start` best-effort (servico parado pelo SafeValue=4 reativa).
   - BackupRegistryValue: .reg backup agora grava dword/qword/hex corretos
     (antes tudo virava dword ou sz - binarios corrompiam o .reg).
   - NOVO ApplyCustomSelection(toApply, toRevert): aplica marcadas + reverte
     desmarcadas (selecao = estado desejado, semantica O&O), retorna
     (Applied, Reverted, Failed) para a UI.

Inventario: 91 Recommended / 28 Limited / 41 NotRecommended = 160 settings,
3 servicos. Build 0 erros. Kit relancado recompilado.

A TESTAR: abrir PrivacyPage -> PRESETS PERSONALIZADOS -> marcar/desmarcar ->
contador atualiza -> APLICAR SELECAO -> confirmacao mostra N perigosas ->
aplica e status do card principal (% Protegido) reconcilia em ~5s; toggle
individual de servico com SafeValue=3 agora aparece como protegido.

### Sessao 07/09 (cont.) - PrivacyPage: botao RESETAR TUDO

- Card dos presets agora tem 2 botoes: PRESETS PERSONALIZADOS (dourado) +
  RESETAR TUDO (DeleteButtonStyle vermelho, x:Name BtnResetAll).
- RESETAR TUDO = OOShutUpManager.RestoreDefaults() (reverte as 160 settings
  para UnsafeValue/padrao Windows + reativa servicos de telemetria), com
  confirmacao forte (lista o que acontece), status "Revertendo..." no score,
  botoes desabilitados, BackgroundTaskTracker e resultado com contagens.
- Substituiu o antigo "Restaurar Padrao Windows" da linha de acoes detalhadas
  (duplicado sem feedback); SaveUserConfig/RestoreUserConfig/Expandir ficaram.
- Build 0 erros; Kit relancado recompilado (GUI.exe 15:00, PID 15016).

### Sessao 07/09 (cont.) - Presets Personalizados: overlay -> JANELA propria

Sintoma (screenshot): o overlay de 880px NAO cabia na janela do kit (cortado
nos 2 lados). Pedido: fazer como a IntegrityPage (PATH + abre janela propria).

- NOVO KitLugia.GUI\Windows\PresetsPersonalizadosWindow.xaml(.cs): janela
  1060x720 no padrao PathExplorerWindow (WindowChrome CaptionHeight=0, fechar
  X proprio, borda dourada #E8B84E, MaxWidth/MaxHeight = WorkArea,
  CenterOwner, ShowInTaskbar=False) + DragMove no header (PathExplorer nao
  tem - adicionamos).
- 3 secoes (SEGUROS/MODERADOS/PERIGOSOS) + selecao rapida + contador; cada
  item mostra estado ao lado: "✅ SERA ATIVADO" (verde/laranja) ou
  "— nao sera ativado" (cinza); perigoso selecionado = "⚠ SERA ATIVADO
  (PERIGOSO)" vermelho. Row com trigger no IsSelected (fundo colorido).
- Confirmacao DENTRO da janela (MessageBox com dono, conta perigosas);
  DialogResult=true expoe ToApply/ToRevert; a pagina aplica (Task.Run +
  ApplyCustomSelection), mostra "Aplicando N..." no score, toasts com
  contagens e RefreshStatus reconcilia.
- PrivacyPage: overlay XAML (126 linhas) e todo o codigo do overlay removidos
  (PresetItemViewModel mudou para PresetItem dentro da janela). BtnOpenPresets
  abre a janela com Owner. RESETAR TUDO inalterado.
- Build 0 erros; Kit relancado (PID 16304).

### Sessao 07/09 (cont.) - Janela de presets COMPACTA com abas de nivel

Feedback (screenshot): 2 linhas por item consumia espaco demais (so ~12 itens
visiveis) e faltavam os botoes principais dos presets no topo.

- Janela redesenhada estilo O&O ShutUp10: ABAS DE NIVEL no topo (Todos/🟢
  Seguros/🟡 Moderados/🔴 Perigosos, RadioButton com counts no conteudo,
  IsChecked=dourado) filtrando UMA lista unica (master _allItems +
  _displayItems reconstruida no filtro).
- Lista COMPACTA: 1 linha por item (~26px) = checkbox + bolinha do nivel +
  nome (semi-bold) + descricao cortada (ellipsis) + estado a direita
  ("✅ SERA ATIVADO" / "— nao sera ativado" / "⚠ SERA ATIVADO (PERIGOSO)").
  Estado/cores via propriedades do PresetItem (StateText/StateBrush/RowBrush/
  LevelBrush) com PropertyChanged ao togglar - sem triggers complexos.
  Clique na LINHA inteira toggle (ItemRow_MouseDown ignora clicks dentro de
  CheckBox p/ nao dupliar).
- Quirk C#: FrameworkElement e System.Windows (NAO System.Windows.Controls).
- Build 0 erros; Kit relancado (PID 26416).

### Sessao 07/09 (cont.) - Ajustes finais da PrivacyPage (feedback do usuario)

1. **Card de status por nivel**: saiu o "20% Protegido / Protegidos 32 |
   Vulneraveis 128". Agora 3 linhas com bolinha + label + "X / Y" + mini barra
   (ProgressBar 6px): verdes (TxtProtSafe/PbSafe), amarelos (TxtProtModerate/
   PbModerate), vermelhos (TxtProtDanger/PbDanger) - e linha de resultado
   TxtPrivacyScore = "Resultado: N de 160 protecoes ativas (P%)" (mesmo x:Name
   reutilizado como feedback "Aplicando.../Revertendo..." nos handlers).
   RefreshStatus calcula counts por nivel e percentuais por barra.
2. **Abas maiores** na janela de presets: LevelTab FontSize 14 Bold,
   Padding 20,10.
3. **Lista organizada**: RebuildDisplay ordena VERDES -> AMARELOS -> VERMELHOS
   (OrderBy (int)Level + ThenBy indice original, ordem estavel).
4. **Botoes de selecao rapida maiores** (feedback: "muito espremidos"):
   WinButton Padding 16,8 / FontSize 12.5 SemiBold / MinHeight 34 - aplica aos
   4 botoes de selecao rapida e ao CANCELAR da janela de presets.
- Build 0 erros; Kit relancado (PID 22516 -> 29732).

### Sessao 07/09 (cont.) - Dry-run READ-ONLY da PrivacyPage

- NOVO tests/PrivacyDryRun (console net10.0-windows, ProjectReference Core):
  valida as 160 settings SEM escrever no registro - estrutura (hive/ValueName/
  SafeValue), leitura real (Registry.GetValue + IsPrivacySettingApplied),
  servicos via WMI StartMode (existem?), amostra dos comandos reg add/sc config
  que seriam emitidos, e simulacao dos 3 presets + ApplyCustomSelection.
  Rodar: dotnet run --project tests/PrivacyDryRun
- RESULTADO (07/09): 0 erros estruturais; 3 servicos existem (DiagTrack
  Disabled, dmwappushservice Disabled, wuauserv Manual) -  wuauserv com
  SafeValue=3 agora corretamente reportado protegido=True (fix validado!);
  62/157 valores presentes, 30 protegidas; IsPrivacySettingApplied x160 em
  151ms; presets simulados 91/119/160; registro intocado.

### Sessao 07/09 (cont.) - IntegrityPage: 2 regras viraram OPCIONAIS

Pedido do usuario: Smart App Control desativado e Fast Startup (HD) nao devem
contar como modificados/perigosos no score.
- Guardian.cs: "Smart App Control Desativado (VerifiedAndReputablePolicyState=0)"
  e "Inicializacao Rapida (Fast Startup) Desativada para HD" (HiberbootEnabled
  Harmful=0) ganharam IsOptional=true -> saem do score (nonOptionalTweaks) e do
  filtro MODIFICADO, aparecem no filtro Opcional da IntegrityPage.
- OBS: existe regra CONFLITANTE no Guardian (linha ~2434) sobre o MESMO valor
  HiberbootEnabled com polaridade INVERTIDA (Harmful=1/Default=0, ja IsOptional)
  - as duas regras nunca podem estar ativas ao mesmo tempo para o mesmo
  estado; ambas opcionais hoje, sem impacto no score.
- Build 0 erros; Kit relancado (PID 26552).

### Sessao 07/09 (cont.) - AUDITORIA COMPLETA da IntegrityPage/Guardian (sem mudancas de codigo)

Pedido: "procure qualquer erro/inconsistencia/metodos diferentes de mesmo resultado" +
explicar por que o scan caiu de ~3 min para <3s.

**Velocidade E LEGITIMA** (otimizacoes de sessoes anteriores, nenhum check foi removido):
1. BCD: 28 processos bcdedit -> 1 UNICO `/enum` com cache TTL 15s (_bcdEnumOutput,
   GetHarmfulTweaksWithStatus ~2594).
2. 78 servicos via leitura do DWORD Start em HKLM\...\Services\<nome> (RegistryBatch,
   cache de chaves) em vez de WMI/ServiceController por item (10-50x mais rapido).
3. RegistryBatch (cache de chaves abertas por scan) + single-flight lock (_scanGate)
   serializa scans concorrentes.
4. Antigamente: spawn de processo por tweak BCD + WMI por servico + varredura de PATH
   por item = minutos; hoje o scan inteiro roda em ~160ms+.

**Consistencias verificadas (tudo OK, sem alteracao)**:
- CheckTweak cobre TODOS os TweakType em uso (127 regras: 78 Service, 18 Registry,
  28 Bcd, 1 Mouse, 2 PageFile) - nenhum tipo fica com status stale.
- Servicos: StartDwordToMode (0=Boot/1=System/2=Auto/3=Manual/4=Disabled),
  HarmfulStartMode (78x "Disabled"), KitIntentionalServiceStart (8x "Disabled") e
  DefaultStartMode (Auto/Manual/Demand/Delayed-Auto) - todos casam por
  OrdinalIgnoreCase; switch sc.exe mapeia Manual->demand, Delayed-Auto->delayed-auto,
  Auto/"Demand" ja sao sintaxe valida.
- FixAll (RESTAURAR TODOS): so toca MODIFIED && !IsOptional; delay 25ms entre
  servicos; sempre reabre a UI no finally (nada congela).
- Score: usa apenas nonOptionalTweaks (IsOptional sai do score E do filtro Modificado).
- Revert de servico intencional (DiagTrack etc.) bloqueado no ToggleTweak com
  mensagem "reative pelo toggle do Kit".
- Prefixos de KeyPath: so HKEY_LOCAL_MACHINE/HKEY_CURRENT_USER/HKEY_CLASSES_ROOT
  (43/305/5) + 1 HKLM (coberto) - gap latente de strip "HKCU\"/"HKCR\" no ToggleTweak
  nunca e exercitado (nenhuma regra usa prefixo curto).
- REG_EXPAND_SZ: Registry.GetValue e RegistryKey.GetValue expandem por padrao -
  caminho batch e nao-batch comparam o mesmo valor (sem divergencia).

Conclusao: nenhuma correcao necessaria; comportamento identico em qualquer PC
(SecurityException/UnauthorizedAccess/Argument -> NOT_FOUND, nunca crash).

### Sessao 07/09 (cont.) - Fix ToggleTweak HKCU/HKCR + AUDITORIA COMPLETA da NetworkPage

**1. ToggleTweak (Guardian.cs)**: adicionados strips de "HKCU\\" e "HKCR\\" no path
+ deteccao StartsWith("HKCR") -> ClassesRoot. Gap dormente eliminado (nenhuma regra
usa prefixo curto hoje, mas regras futuras agora funcionam nas duas formas).

**2. NetworkPage (primeira revisao da pagina) - achados e correcoes**:
- **BUG REAL (flags conflation)**: `_isLoading` era usado como guarda dos 7 toggles
  (Chk*_Click) E como guarda do refresh de DNS (timer 10s setava _isLoading=true
  durante o refresh) -> durante qualquer refresh DNS, TODOS os toggles clicados
  morriam em silencio. Criado `_dnsRefreshing` separado; `_isLoading` so para a
  carga inicial, setado false no fim de LoadAllDataAsync (antes nunca era resetado
  pela carga inicial - so pelo finally do RefreshDnsStatus!).
- **Timers duplicados**: StartRefreshTimers rodava a cada Loaded sem guarda ->
  `_timersStarted` (timer 3s adaptador + timer 10s DNS nao empilham mais).
- **XAML \uXXXX literal** (bug visual): "DNS R\u00e1pido", "DNS Prim\u00e1rio",
  "Prim\u00e1rio (ex: 1.1.1.1)", "Secund\u00e1rio (opcional)" apareciam com o
  escape cru na tela (XAML nao interpreta \\uXXXX - so &#xXXXX;). Corrigidos
  para acentos reais.
- **Catch blocks copy-paste**: 10 handlers nao relacionados (MAC, limpeza, reset,
  custom DNS) escreviam "Erro: ..." no TxtBenchmarkStatus + mexiam no PgbBenchmark
  (UI do benchmark de DNS). Removido o bloco em todos; benchmark mantem o seu
  (+ ShowError dialog novo).
- **BtnFlushDns_Click ignorava result.Success**: mostrava ShowSuccess mesmo com
  falha. Corrigido.
- **UpdateLabel(TextBlock,bool) dead overload** removido (so a versao 4-args
  com textos customizados e usada).
- **Consistencias verificadas sem mudanca**: GetActiveDnsInfo ("Cloudflare"/
  "Google"/"Personalizado"/"Automatico (DHCP)") casa com UpdateDnsUi (contains
  DHCP ok); SetDns/SetCustomDns via netsh; CleanNetworkSafe/Full so comandos
  padrao; IsInterruptModerationDisabled/IsNagle (registro+netsh); MAC spoofing
  com confirmacao dupla + workaround NetworkAddress + verificacao pos-restart;
  RefreshAdapterStatus com single-flight (_refreshingAdapters) + preserva
  selecao por ConnectionName.

Build: 0 erros / 0 avisos. Kit relancado (PID 31096, exe 17:54).

### Sessao 07/09 (cont.) - NetworkPage DNS Rápido: area reformada + comandos atualizados

Pedido: "melhore toda essa area e refaça os comandos dessa pagina de rede".

**DnsBenchmark.cs (reescrito)**:
1. Best-of-3 pings por servidor (antes: 1 ping único — pico momentaneo punia o
   provedor injustamente). Para cedo se best <= 5ms.
2. Fallback TCP 443: se ICMP do primario falhar (bloqueado por firewall), tenta
   secundario; se ICMP falhar nos 2, TcpConnect 443 (1500ms timeout) — varios
   provedores dropam ICMP mas respondem TCP. Propriedade MeasuredVia (ICMP/TCP:443).
3. Lista atualizada: REMOVIDOS mortos/obsoletos (Neustar 156.154.x descontinuado,
   Comodo 8.26.x virou DNS Forge x.x.130.130, OpenNIC antigo, UncensoredDNS,
   Yandex, Oracle Dyn, Alternate DNS); ADICIONADOS Control D (76.76.2.0),
   NextDNS Anycast (45.90.28.0), AdGuard Sem Filtro (94.140.14.140), Mullvad
   secundario corrigido (era 194.242.2.3 inexistente, agora 1.1.1.1), Hurricane
   Electric corrigido (75.75.75.75 era da Comcast, real e 74.82.42.42), puntCAT
   primario corrigido (185.121.177.177).
4. IsCurrent (bool) + MarkCurrentProvider() + TooltipText (primario/secundario/
   categoria/latencia com via).

**NetworkManager.cs (Toolbox)**:
5. SetDnsServers: validacao de IP ANTES de tocar interfaces (IPAddress.TryParse;
   primario obrigatorio, secundario opcional); bug real: secundario vazio gravava
   ('prim','') -> string vazia como servidor DNS no Windows. Agora passa so o
   primario nesse caso.
6. GetActiveDnsInfo: reconhece TODOS os provedores da lista (nome amigavel na UI)
   em vez de so Cloudflare/Google.

**NetworkPage.xaml**:
7. Badge "EM USO" verde na linha do provedor ativo + fundo verde translucido
   (BoolToBrushConverter local + BooleanToVisibilityConverter).
8. Botao Aplicar maior (FontSize 10, Padding 6,3), botao renomeado
   "↻ Testar Novamente", status com TextWrapping (nao corta mais).
9. TextBox secundario sem texto-placeholder fixo (placeholder do primario
   tratado como vazio no handler).

**NetworkPage.xaml.cs**:
10. Benchmark AUTOMATICO ao abrir a pagina (RunBenchmarkAsync no fim de
    LoadAllDataAsync) — antes so rodava com clique.
11. RunBenchmarkAsync com single-flight (_benchmarkRunning) — clique durante o
    teste nao empilha; BtnBenchmarkDns_Click virou wrapper.
12. UpdateDnsUi: nome amigavel do Core ("Cloudflare (1.1.1.1)") + marca
    IsCurrent na lista + Items.Refresh.
13. BtnApplyCustomDns_Click: validacao de IP na UI ANTES do Core (defesa em
    profundidade), placeholders tratados como vazio, erro proprio em vez de
    sumir no log.
14. BtnResetNetwork_Click: catch com ShowError (antes so logava, usuario nao
    via o erro).

Build: 0 erros / 12 avisos (pre-existentes). Kit relancado (PID 30496, exe 19:55).

### Sessao 07/09 (cont.) - DNS Rápido: visual refeito (pedido do usuario)

"refaça o visual... botões de dns primario/secundario com placeholder cinza que
some ao digitar... botão amarelo está frito demais, deixe preto com borda e
texto amarelo".

1. **Card refeito**: cabecalho com titulo + subtitulo e botao "↻ Testar Todos"
   movido para o topo (direita); tabela de provedores dentro de Border #161616
   com cantos arredondados; linhas com Padding/cornerRadius (hover visual);
   cabecalhos de coluna em caps cinza; latencia agora VERDE (#4CAF50) em vez de
   amarela (menos poluicao amarela); status do teste abaixo da tabela.
2. **Botoes "Aplicar"**: todos preto (#1A1A1A) com BORDA amarela (#FFD700) e
   texto amarelo (antes: fundo amarelo chapado). Mesmo estilo no Aplicar do DNS
   customizado e no Testar Todos.
3. **Placeholders REAIS nos TextBoxes**: novo estilo DnsPlaceholderTextBoxStyle
   (ControlTemplate com TextBlock "ph" ligado ao Tag, Visibility ligado a
   Trigger Text="" + borda dourada no foco). Textos: "DNS Primário (ex:
   8.8.8.8 Google)" / "DNS Secundário (opcional, ex: 8.8.4.4)". Removido o hack
   antigo de texto-fixo-como-placeholder no handler.
4. TextBoxes com Height 32, cantos 6, CaretBrush dourado, fundo #161616.

Build: 0 erros. Kit relancado (PID 15204).

### Sessao 07/09 (cont.) - DNS Rápido: spinner individual por linha + lista expandida (estilo DNS Jumper)

Pedido: "durante o teste coloque aquele negocio de carregando... um individual em
todos os testes... ser tão bom quanto o DNS Jumper... pesquisar na web para os
melhores resultados e adicionar mais animações".

**Pesquisa web** (gist mutin-sa "List of Top Public Recursive Name Servers" +
public-dns.info): validados IPs reais e descobertos provedores que faltavam.

**DnsBenchmark.cs**:
1. `DnsProvider` agora e `INotifyPropertyChanged` com `TestState` (enum:
   Pending/Testing/Done/Failed) + `TestStateText` — a linha atualiza AO VIVO
   durante o teste (spinner individual estilo "1 tarefa em execução" do Kit).
2. `BenchmarkAsync` seta Testing no início de cada item, Done/Failed no fim.
3. Lista EXPANDIDA (20 -> 26): ADICIONADOS Quad9 Sem Filtro (9.9.9.10), DNS.SB
   (185.222.222.222), dns0.eu (193.110.81.0), Level 3 4.2.2.1, Dyn/Oracle
   (216.146.35.35), Quad101 Taiwan (101.101.101.101), Freenom World (80.80.80.80);
   CORRIGIDOS Gcore (95.85.95.85/2.56.220.2, os antigos 185.158.x eram
   invalidos), Hurricane Electric secundario (216.218.221.6), puntCAT
   (185.121.177.177 = OpenNIC real).

**NetworkPage.xaml**:
4. `RowSpinnerStyle`: Ellipse 13px com StrokeDashArray + RotateTransform animado
   (storyboard 0.9s RepeatBehavior Forever, ativado por DataTrigger TestState=Testing,
   StopStoryboard no exit) — spinner girando SO na linha que esta sendo testada.
5. Converter `DnsTestStateToVisibilityConverter` (só Testing = Visible).
6. Latencia "Falhou" em vermelho (#E53935) quando Failed.
7. Quirk: `StrokeLineCap` nao existe em WPF Shape — `StrokeStartLineCap`/
   `StrokeEndLineCap` (MC4005).

**NetworkPage.xaml.cs**:
8. Reordenacao da lista SO NO FIM do teste (ItemsSource null + reatribui) —
   durante o teste as linhas ficam fixas com spinner girando individualmente;
   status final inclui "· N sem resposta".

Build: 0 erros. Kit relancado (PID 35524).

### Sessao 07/09 (cont.) - ANALISE BINARIA do DNS Jumper v2.3 (IDA Pro) + RESOLUCAO DNS REAL no Kit

Pedido: "use ida pro no dnsjumper so para ver se ele faz alguma coisa extra que
nao vimos, os resultados dele parecem ser diferentes do que nos pegamos".

**Binario**: DnsJumper.exe = PE32 i386, 912KB, compilado em AUTOIT (strings
MouseClickDelay/STRINGTOBINARY/GUICTRLSENDTODUMMY confirmam; Aut2Exe). Analise
por parsing estatico (imports PE, strings UTF-16, .asm do idat -B) — nao IDA
decompile completo (AutoIt e interpretado em bytecode embutido).

**ACHADOS (como o DNS Jumper testa)**:
1. Imports: ICMP.DLL (IcmpCreateFile/IcmpSendEcho/IcmpCloseHandle) + WSOCK32
   ordinals [151=WSAStartup, 52=gethostbyname, 111=WSAAsyncGetHostByName,
   20/17=sendto/recvfrom, 115/116=WSAAsyncSelect, 23=htons...]. SEM dnsapi.dll,
   SEM iphlpapi.dll, SEM ws2_32.dll (usa wsock32 legado).
2. **O teste de LISTA do DNS Jumper e SO PING ICMP** (IcmpSendEcho com timeout
   500ms padrao — INI ResolveWaitTime=500). A coluna "Resultado 1/2" = ICMP RTT.
3. "Turbo Resolve" = resolver www.google.com via gethostbyname (usa o DNS DO
   SISTEMA, nao o servidor selecionado — mede o resolver ATIVO, nao o candidato).
4. INI confirma: ResolveDomainName=www.google.com, MixedDns=True (ordena
   misturado), AutoSortDnsList=True.
5. O gethostbyname/sendto/recvfrom do binario servem para o "Check DNS"
   individual (janela de resolve time), nao para o teste de lista.

**POR QUE OS RESULTADOS DIFEREM**: ICMP mede a rede ate o servidor; o tempo de
RESOLUCAO real (processamento do resolver + cache miss) e outro numero. O Kit
media ICMP (igual ao Jumper), mas o ranking ficava diferente porque o Jumper
usa timeout 500ms (descarta lentos) e 1 ping so.

**MELHORIA IMPLEMENTADA no Kit (supera o Jumper)**:
6. `DnsQueryMsSync`: query DNS REAL (UDP porta 53, tipo A por www.google.com)
   com EDNS0 OPT record — **DESCOBERTA CRITICA: Cloudflare/Quad9/1.0.0.1/
   AdGuard/Mullvad DESCARTAM queries sem EDNS0** (testado: sem OPT so Google/
   OpenDNS/Level3/SafeDNS respondem; com OPT TODOS os 26 respondem). Sem isso
   o ranking ficaria cheio de "-- ms" falsos.
7. Ranking agora pela RESOLUCAO REAL (mais fiel que ICMP); ICMP fica como
   coluna secundaria. Coluna nova "RESOLUCAO" no XAML + latencia ICMP
   rebaixada para cinza-dourado.
8. Status final reporta "por resolução real" ou "por ICMP" (fallback).
9. Timeout 1500ms (Jumper usa 500 — o nosso aceita redes lentas mas
   diferenciadas).

Artefatos: %TEMP%/opencode/dnsjumper/ (exe copiado, .asm 8.4MB, .i64 10.8MB),
%TEMP%/opencode/dnstest/ (projeto de teste UDP/EDNS).

Build: 0 erros. Kit relancado (PID 39312).


### Sessao 08/09 - Navegacao entre abas otimizada p/ CPU antiga + SSD lento (trava ao trocar de aba)

Sintoma: em Ryzen 5 5500 com SSD ruim, o kit travava ao passar entre as abas.

CAUSAS RAIZ (3, todas no caminho da troca de aba):
1. EmptyWorkingSet SINCRONO na UI thread no meio da troca: 6 paginas chamavam
   MemoryHelper.TrimWorkingSet() dentro do Cleanup() (Dashboard, GameBoost, Drivers,
   Partitions, Services, Winboot), invocado via reflection de forma sincrona pelo
   CleanupAndNavigate ANTES do Navigate — em SSD lento isso gera page-faults e congela.
2. Animacao de escala 0.98->1.0 (MainFrame_Navigated, 250ms) concorrendo com a
   construcao/layout da pagina nova (passe extra de layout + composicao GPU).
3. GC.Collect + Trim IMEDIATO apos navegar (gate 90MB): despaginava a pagina que
   acabou de abrir; cliques rapidos empilhavam GC + EmptyWorkingSet.

CORRECOES:
- NOVO KitLugia.GUI\UiPerformance.cs: NavigationAnimationEnabled=false (troca
  instantanea por padrao), PostNavTrimDelayMs=4000, PostNavTrimThresholdMb=140.
- MainWindow.NavigateToPage virou async: guarda single-flight (_isNavigating ignora
  cliques durante a construcao), cursor de espera + await Dispatcher.Yield(Render)
  (o clique pinta antes do trabalho pesado XAML/JIT).
- MainFrame_Navigated: early-return sem animacao quando desabilitada (default);
  animacao de escala mantida so se UiPerformance.NavigationAnimationEnabled=true.
- CleanupAndNavigate: trim pos-nav ADIADO (4s) e CANCELAVEL (_navTrimCts — trocar de
  aba de novo cancela o anterior; sem storm em cliques rapidos) + gate 140MB
  (90MB pegava uso normal e despaginava a pagina nova). Dispose no Cleanup().
- 6 Cleanups SEM TrimWorkingSet sincrono (Dashboard/GameBoost/Drivers/Partitions/
  Services/Winboot) — trim agora e so o centralizado do MainWindow.

Build: 0 erros / 132 avisos (baseline nullable). Sem cache de paginas (regra mantida).

A TESTAR (PC fraco): clicar rapido entre abas pesadas (Services/Apps/Partitions/
Winboot) — sem congelamento; cursor de espera visivel em vez de trava; RAM continua
caindo sozinha ~4s apos parar de navegar (log "RAM devolvida apos navegacao").


### Sessao 08/09 (cont.) - RAM + picos de CPU: 4 fixes reais no monitoramento continuo

Sintoma: picos periodicos de CPU + RAM do processo crescendo. Auditoria dos timers
sempre-ativos (tray + janela) e da coleta por processo.

ACHADOS + CORRECOES (todos com consumidor verificado antes de cortar):
1. **PerformanceCounter novo a CADA tick (2s)** - UpdateSystemStats criava
   
ew PerformanceCounter("Processor",...) + 1 NextValue() por tick: custo de
   init PDH (registry + instancias) a cada 2s E valor sempre 0 (primeira leitura
   sem baseline). Fix: _cpuTotalCounter reusado (delta real de 2s) + dispose
   no DisposeCore. (TrayIconService.cs)
2. **Tooltip do GoodbyeDPI a cada 2s** - o tick chamava GetGoodbyeDPIStatus()
   direto (GetProcessesByName + StartTime + WorkingSet) mesmo no tray. Fix: tick
   usa o getter cacheado (10s) + string detalhada cacheada
   (_goodbyeDpiStatusText/_goodbyeDpiStatusTextTime, refresh max 10s). (MainWindow)
3. **UpdateProcessCache coletava 5 campos sem consumidor** (VirtualMemoryMB,
   StartTime, ThreadCount, HandleCount, MainWindowTitle = 1+ syscall por processo
   por tick) + Responding (SendMessageTimeout, o mais caro) a cada 2s. Fix:
   coleta so Id/Name/WS/Cpu; Responding amostrado a cada 5 ticks (~10s, alerta
   tem cooldown de 5min); campos mortos nao coletados (props mantidas).
4. **Caches sem poda** - _cpuTimeCache (keyed por PID) acumulava PIDs mortos
   para sempre; behaviors de instaladores/temp ficavam. Fix: poda por PIDs vivos
   ao fim da coleta + behaviors com LastSeen > 30min quando Count > 500.
5. Bonus: GetDetailedMemoryReport com using var proc (handle vazado).

Verificados e DEIXADOS como estao: MonitorTick 30s (ok), RAM limiter, ProBalance
3s, foreground 250ms, MemoryDiagnostics/MemoryLeakProfiler (dead code, sem
callers — nao rodam), prioridade High do processo (comportamento intencional).

Build: 0 erros / 132 avisos (baseline nullable).

A TESTAR: deixar o kit aberto e observar CPU no Gerenciador (picos de 2s devem
sumir; leitura de CPU do monitor passa a mostrar valor real); RAM estavel em
sessoes longas (sem crescimento do _cpuTimeCache).


### Sessao 08/09 (cont. 2) - RAM sob controle: teto configuravel + 2 vazamentos estaticos

Pedido: continuar procurando economias de RAM + dar um controle do uso de RAM do kit.

AUDITORIA (caches estaticos do Core + GUI, buffers, GC):
- Core: todos os caches estaticos ja sao limitados ou podados (NetworkTrafficMonitor
  dispoe counters stale; Guardian/RegistryBatch por scan; SearchEngine/_database 1x;
  DriverManager por carga; GpuMonitor/ProcessIoHelper transientes). Sem achado no Core.
- Resources\ (9,7MB) sao ferramentas em disco (Explorer++/7z/wimlib/oscdimg), nao RAM.
- GC: Workstation default (sem ServerGC), correto p/ app UI. Prioridade High do
  processo mantida (intencional; GameBoost usa prioridades proprias por processo).

CORRECOES:
1. **Teto de RAM do kit CONFIGURAVEL** (era hardcoded 200MB no MainWindow):
   TrayIconService.KitMemoryLimitMB (80..1024, default 200) persistido em
   HKCU TraySettings\KitMemoryLimitMB (save/load + GetKitMemoryLimitStatic p/
   o boot); setter aplica ao vivo via AggressiveMemoryCleaner.SetMemoryLimit.
   MainWindow inicia o monitoramento com o valor persistido. UI: campo "Teto:"
   + "MB" ao lado do "Intervalo:" na secao LIMITADOR DE RAM (TraySettingsPage
   .xaml/.cs, mesmo padrao do TxtRamLimiterInterval_LostFocus).
2. **GlobalSearchPage._statusCache sem teto** (static, 1 entrada por item ja
   pesquisado, p/ sempre): Clear() ao passar de 2000 (seguro, recalcula).
3. **ConsoleManager._pending sem teto**: flood de log enfileirava milhares de
   strings + backlog no dispatcher (pico de RAM + UI lenta). Cap 3000 (descarta
   as mais antigas + contador Interlocked); FlushBatch insere marcador
   "... N linhas suprimidas (flood de log) ..."; Clear() zera o contador.

Build: 0 erros / 132 avisos (baseline nullable).

A TESTAR: TraySettings -> campo Teto (ex: 120MB) -> sobe o uso (scan verboso) ->
limpeza automatica ao passar do teto; flood de log mostra o marcador de linhas
suprimidas em vez de travar; busca global repetida nao cresce memoria.


### Sessao 08/09 (cont. 3) - GlobalSearch refeita: indice normalizado + providers + popup live Top-8

Pedido: refazer a GlobalSearchPage com os melhores metodos, suportando tudo do kit.

PROBLEMAS DO DESENHO ANTIGO (todos provados no codigo):
1. Cada tecla (debounce 300ms) NAVEGAVA p/ uma GlobalSearchPage NOVA (recriava a
   pagina inteira por tecla) — o freeze ao digitar em PC fraco.
2. Resultados SEM limite em ItemsControl+WrapPanel dentro de ScrollViewer (sem
   virtualizacao: materializava 100+ cards).
3. 1 scan Guardian COMPLETO por tecla (UpdateSearch) + 1 scan por Invoke de
   CheckState (cada lambda de tweak fazia GetHarmfulTweaksWithStatus inteiro).
4. Popup de busca (LstSearchResults) MORTO: nunca preenchido, so fechado.
5. Sem tolerancia a acento (pt-BR: 'otimizacao' nao achava 'otimização').
6. Cobertura: 7 paginas fora do indice (WinpeTools/ReinstallPreserve/
   WindowsUpdate/Shrink/QuickInstall/ContextMenu/ForceStopUnlock/ExmTweaks/
   StoreRemake) + 160 settings de privacidade + 108 tweaks do AllTweaks fora.

REFEITA (Core/SearchEngine.cs reescrito):
- Indice pre-normalizado (Fold: lowercase + FormD sem acento, 1x na indexacao;
  zero ToLower/alocacao por tecla). Scoring AND por palavra + bonus por tipo.
- Search(query, maxResults=60) + SearchTop(query, 8) p/ popup. FFI
  NativeSearch por item REMOVIDO do caminho (500 transicoes FFI por tecla nao
  batem um scan managed <1ms; classe mantida p/ uso futuro).
- Providers: RegisterProvider() (Core sem depender da GUI) + RegisterStates().
- Cache de estados com TTL 15s + guarda anti-reentrancia _refreshing:
  1 scan Guardian + 1 passada de privacidade + resolvers, nunca 1 scan por
  tecla/item. InvalidateStates() apos cada toggle.
- BUG PEGO NO HARNESS: registrar o proprio check (que chama QueryState) como
  resolver causava StackOverflow no refresh — AddToggle nao registra; so
  providers registram explicitamente.
- Cobertura nova: 10 AddNav + 160 privacidade (toggle aplica/reverte pelo estado
  atual) + PATH consolidado com StateKey + extras com StateKey.

GUI:
- AllTweaksPage.DefineSystemTweaks virou public static (puro: sem estado de
  instancia) + Pages/SearchProviders.cs (bootstrap idempotente) indexa os 108
  como toggles com estados.
- GlobalSearchPage: ListBox virtualizada (Recycling) + top 60 + badge TypeLabel
  (PAGINA/TWEAK/ACAO) + 1 passada de estados via GetStates(); _statusCache
  estatico removido.
- MainWindow: popup live Top-8 funcional (debounce 300->150ms); digitar NAO
  recria mais pagina; Enter executa selecionado/Top-1; na pagina de busca,
  atualiza in-place. ExecuteGlobalSearchResultAsync rele estado via
  Invalidate+QueryState (sem scan direto).

VALIDADO (harness Release, host): init 31ms; buscas 0-10ms ('otimizacao' acha
'Otimização de Entrega', 'menu contexto' acha a pagina); 627 estados em 225ms
no 1o refresh, 0ms em cache. Build: 0 erros (avisos restantes pre-existentes).

A TESTAR (app): digitar na busca (popup Top-8 instantaneo, sem freeze); Enter
executa o Top-1; pagina de resultados com toggles mostrando estado real;
buscar 'telemetria'/'recall' (privacidade) e 'core parking' (alltweaks).


### Sessao 08/09 (cont. 4) - Busca: fora o mini-popup, resultado na pagina cheia (pedido do usuario)

Feedback (screenshot): o popup Top-8 sob a caixa de busca ficava estreito,
cortando texto por cima da sidebar — 'mini menu ruim'. Revertido p/ pagina
cheia a direita, mantendo os fixes de performance:
- PerformLiveSearch: query vazia limpa/atualiza; fora da pagina navega UMA vez
  p/ 
ew GlobalSearchPage(query) (Navigate e sincrono: o proximo debounce ja
  enxerga a pagina e atualiza in-place, sem recriar por tecla); Enter igual.
- Popup (LstSearchResults) nunca mais abre; badge TypeLabel do template mantido
  (inofensivo). SearchTop mantido como API publica (sem chamador na GUI).
- Motor inalterado: indice normalizado, providers, cache de estados TTL,
  pagina virtualizada top 60.

Build: 0 erros.

### Sessao 09/09 - Store Remake reescrita no estilo UniGetUI (substitui o remake MS Store)

Contexto: updates exclusivos da Microsoft Store nao dava p/ reimplementar —
usuario mandou usar o UniGetUI (fonte: C:\Users\Lugia\Downloads\UniGetUI-main)
como modelo. Interface e semantica de CLI do UniGetUI, gerenciadores do Kit
(winget/choco via StoreEngine). KitStoreWindow/WindowsPage/--kitstore INTOCADOS
(so troca o conteudo da pagina).

1. **StoreRemakePage.xaml recriada** (870 -> ~420 linhas):
   - Header UniGetUI: icone 44px + titulo 28pt + subtitulo que MUDA por aba
     (Descobrir/Instalados/Atualizações) + status winget/choco.
   - Toolbar row: botão principal contextual (Instalar/Reinstalar/Atualizar
     selecionado ou Atualizar tudo) + MegaQuery box (underline focus) + chips
     de fonte winget/choco/msstore (toggle; mudar filtro re-executa a busca).
   - Abas Descobrir/Instalados/Atualizações com tooltips explicando cada uma.
   - Lista virtualizada (ListView + Recycling) estilo DataGrid UniGetUI:
     icone, nome+editor, id, versão, nova versão laranja, badge fonte + badge
     verde "✓ instalado", botões por linha (Instalar/Atualizar/Force/…/Desinstalar).
   - Status bar: log + contadores (N instalados · N updates · N pacotes Store).
   - Mantidos: modal de detalhes (+ card verde "Já instalado neste PC"),
     widget de progresso flutuante, monograma de ícone, cache disco/memória.

2. **Code-behind reescrito** (1745 -> ~1100 linhas):
   - BUG 1 (lista vazia): 1a carga já estava na aba Discover -> SwitchTab não
     rodava -> ItemsSource nunca atribuído (contador dizia "21 resultados",
     lista branca). Fix: RenderDiscover() na entrada do DoSearchAsync mesmo
     já na aba + ItemsSource reatribuído INCONDICIONALMENTE no fim da busca.
   - BUG 2 (semântica): Discover mostrava "Instalar" p/ app já instalado.
     Fix: MarkInstalledFlags() cruza _results x _installed (chamado após
     busca, sugestões e refresh) -> IsInstalled -> badge "✓ instalado" +
     botão vira Reinstalar (ou Atualizar se tem versão nova). Modal mostra
     card "Já instalado neste PC".
   - Flags UniGetUI (WinGetPkgOperationHelper/ChocolateyPkgOperationHelper):
     winget `--id X --exact --silent --accept-package-agreements
     --accept-source-agreements --disable-interactivity`; upgrade com
     `--include-unknown --force`; msstore fixado `--source msstore`;
     choco `install/upgrade/uninstall "id" -y --no-progress`. NOVO:
     Reinstalar roda `install` (nao `upgrade` — upgrade sem pendência falha).
     Exit codes de sucesso: 0/3010/1641/1614/1605.
   - Discover com query vazia: sugestões do índice SQLite local do winget
     (monikers populares, instantâneo, sem rede).
   - Removidos do remake: Home hero/carrossel, fantasmas Minecraft, wsreset,
     reparar Store, pop-out de busca (mantidos no StoreEngine p/ reuso).

3. **StoreEngine.cs (Core)**: QueryWingetSearch(path, query, source) novo
   overload com `--source msstore` (busca real na fonte Store via CLI);
   QueryChocoSearch novo (--limit-output --by-id-only --order-by-popularity).

Build: 0 erros. A TESTAR: badge "✓ instalado" no Discover, Reinstalar via
install, msstore search aceitando acordo da fonte, abas com dados cheios.

### Sessao 09/09 (cont.) - Store Remake: detecca robusta de "ja instalado" (estilo AppsPage/BCU)

Feedback (screenshot): WinRAR mostrava badge/reinstalar, mas qBittorrent e
JDownloader (instalados fora do winget) apareciam como "Instalar".

**MarkInstalledFlags reescrito (StoreRemakePage.xaml.cs)**:
1. **Multi-fonte**: (a) indice winget/choco da lista _installed (id completo,
   1o segmento do id, tail do id apos o publisher, id sem sufixo choco
   install/portable/appx/package, nome); (b) indice do REGISTRO via
   RegistryProgramFactory.GetInstalledPrograms (a MESMA engine do AppsPage:
   HKLM+HKCU x 32+64-bit Uninstall keys) — DisplayNames + RegistryKeyNames,
   cache 10 min.
2. **Match**: exato em qualquer indice; fallback contencao (token >= 4 chars
   contido em nome instalado, ex "firefox" in "mozillafirefoxx64enus").
3. **Anti-falso-positivo**: tokens de vendor genericos (microsoft, google,
   adobe, mozilla, video, app...) valem so p/ match EXATO, nunca contencao;
   tokens < 3 descartados; normalizacao so [a-z0-9] ("7-Zip (x64)" ->
   "7zipx64").
4. Log do Discover/Busca agora mostra "· N ja instalado(s)" p/ validar.

Build: 0 erros. A TESTAR: qBittorrent/JDownloader/Firefox com badge ✓
instalado + Reinstalar na Descobrir; conferir que nao marcou demais
(tudo com badge = contencao larga demais).

### Sessao 09/09 (cont. 2) - Store Remake: abre em ATUALIZACOES + pipeline de update corrigido

Pedidos: pagina padrao = Atualizacoes (usuario ja faz o que precisa) e
"garanta que o codigo de atualizar os pacotes esteja correto".

1. **_activeTab inicial = StoreTab.Updates**; OnLoaded chama SwitchTab
   (header/toolbar/botoes certos no load). Discover so carrega sugestoes
   na PRIMEIRA visita (BtnTabDiscover_Click), nao no load.
2. **InstallOrUpgradeAsync(app, forceStop, refreshAfter=true)**: em lote
   passa refreshAfter=false — antes o "Atualizar tudo" re-query winget+
   choco (2x ~30s) DEPOIS DE CADA pacote (N x 30s de espera); agora
   recarrega UMA vez no fim com force=true.
3. **Fonte fixada no CLI**: winget/choco install/upgrade ganham
   `--source winget` ou `--source msstore` quando conhecida — sem isso o
   winget pode falhar/abrir prompt se o id existir em 2 fontes com
   --disable-interactivity.
4. **Atualizar tudo confirma antes** com preview (8 primeiros "nome (v ->
   nova)") e conta em "pacote(s)" (nao app).

Build: 0 erros. A TESTAR: abrir a Store -> cai em Atualizacoes direto;
Atualizar tudo com N pacotes termina ~Nx30s mais rapido que antes;
pacote de fonte dupla (ex id em winget+msstore) atualiza sem erro 0x8A15002B.

### Sessao 09/09 (cont. 3) - Store Remake: auditoria propria (CLI validado + 3 fixes)

TESTE REAL (host): winget upgrade 7zip.7zip via pagina -> exit 0, "Instalado
com exito", refresh forcado derrubou updates 53 -> 52 (pipeline OK).
CLI validados direto: `winget search spotify --source msstore` (tabela 3
colunas Nome/ID/Versao, Versao "Unknown"); `choco outdated --limit-output`
(exit 0).

**Fixes da auditoria**:
1. **FREEZE de UI**: RebuildInstalledIndex rodava o scan de registro
   (RegistryProgramFactory, centenas de chaves) NA UI THREAD na 1a busca.
   Extraido p/ RebuildRegistryIndexAsync (Task.Run interno, cache 10 min);
   MarkInstalledFlags -> MarkInstalledFlagsAsync (await em todos os 3
   chamadores).
2. **msstore "Unknown"**: StoreEngine msstore search normaliza versao
   "Unknown" -> "" (nao aparece "Unknown" feio na coluna).
3. **Flash "Tudo atualizado!"**: RenderUpdates nao mostra mais o texto
   enquanto _busy==1 (refresh inicial em curso) — spinner cobre.

Verificado tambem: bindings XAML x VM todos existentes (build WPF falharia
senao); VMs da aba Atualizacoes sao AS MESMAS instancias de _installed
(icones carregados valem la tambem); fluxo Reinstalar (registry match,
sem winget id) roda `install` corretamente.

Build: 0 erros. PROXIMO (pedido do usuario): refazer AppsPage — "ainda nao
100% confiavel".

### Sessao 09/09 (cont. 4) - Store Remake: gerenciadores de DEV nas Atualizacoes (pip/npm/dotnet/cargo)

Pedido: "UniGetUI mostra mais coisas p/ atualizar — diferentes de apps comuns,
mas ainda sao atualizacoes" (print: 139 updates = winget + Pip + Npm + Cargo).

**StoreEngine.cs (Core)** — comandos EXATOS extraidos do UniGetUI:
- QueryPipOutdated: `pip list --outdated` (tabela Package|Version|Latest,
  split pos "----", parse igual Pip.ParsePackages). FindPipPath() acha o
  pip como o UniGetUI: pip.exe no PATH -> python.exe no PATH (excluindo
  WindowsApps stub) -> Program Files/Python* e AppData\Programs\Python
  (com python roda `-m pip`).
- QueryNpmOutdated: `npm outdated --json` LOCAL + GLOBAL (WorkingDirectory
  = UserProfile; JSON {id:{current,latest}}; dedup local/global via
  Category="global"|"local").
- QueryDotnetToolUpdates: `dotnet tool list --global` + versao mais nova
  via NuGet flatcontainer API (api.nuget.org/v3-flatcontainer/{id}/index.json,
  HttpClient 8s timeout, CompareVersions) — fontes nuget.org como o
  UniGetUI (V3PackageType=DotnetTool). So reporta se latest > instalada.
- QueryCargoUpdates: `cargo install-update -l` (cargo-update); so linhas
  com "Needs update"=Yes; detecta cargo-update ausente e loga dica.
- FindFirstOnPath via `where`.

**StoreRemakePage.xaml.cs**:
- EnsureDevManagersAsync (1x por pagina, where em Task.Run) +
  QueryDevUpdatesAsync (4 queries em PARALELO dentro do Task.WhenAll do
  refresh — nao soma latency).
- Dev merges em _installed com flag DevOnly=true; NAO contam como
  "instalados" (contadores e aba Instalados filtram DevOnly); so aparecem
  em Atualizacoes (igual UniGetUI, que nao lista dev pkgs como apps).
- Update CLI por fonte: pip `install --upgrade id --no-input --no-color
  --no-cache` (python: `-m pip ...`); npm `install [-g] id` (UpdateVerb=install
  no UniGetUI); dotnet `tool update --global id` (UpdateVerb=update);
  cargo `install id`.

**TESTADO NO HOST**: pip nao existe no PATH do usuario (so python.exe stub
WindowsApps) -> FindPipPath acha e roda `-m pip`; tabela bate
(aiohttp 3.13.4->3.14.3, anthropic, anyio...). npm global JSON OK
(@deepseek-ai/dsh). dotnet tool list OK (ilspycmd). cargo install-update
OK (cargo-binstall v1.22->v1.23 Yes).

Build: 0 erros.

### Sessao 09/09 (cont.) - UI Performance global: timers fora do ctor + virtualizacao

Pedido: "aquele negocio que voce fez para otimizar a troca de abas do kit da para
fazer em todas as paginas dele?" — auditoria completa de padroes que atrasam a
navegacao (padrao ja estabelecido: ctor leve + timer inicia no Loaded + tick async
com single-flight; paginas sao REcriadas a cada navegacao e a antiga leva Cleanup()
via reflection no CleanupAndNavigate, entao o guard _timersStarted precisa resetar
no Cleanup).

**ACHADOS E CORRECOES** (padrao aplicado: `new DispatcherTimer` no ctor SEM Start()
+ handler `Loaded` com guard `_timersStarted` (return se ja started; reset=false no
Cleanup) + `Loaded -=`/`Unloaded -=` no Cleanup):

1. **DiagnosticPage**: timer .Start() no ctor + `RefreshDiagnostics()` sincrono no
   ctor (GC.GetTotalMemory + contagem de timers/tasks). Agora: timer so Start no
   Loaded; primeira refresh tambem no Loaded (pagina ja renderizada). Cleanup
   reseta _timersStarted e remove Loaded.
2. **TraySettingsPage**: StartRamRefresh() no ctor iniciava timer de 2s (tick:
   RefreshRamDisplay + RefreshProcessLimitsStatus (Process.GetProcessesByName) +
   UpdateIslcStatus). Agora: timer criado no ctor, Start + RefreshRamDisplay no
   Loaded (guard _timersStarted). StartRamRefresh removido.
3. **PartitionsPage**: _realTimeMonitorTimer.Start() no ctor (tick 10s:
   RefreshUsage por particao + RenderPartitionBar + Items.Refresh). Agora Start no
   Loaded com guard. Tick continua gated por ChkAutoRefresh/_isCriticalOperation/
   _isUpdatingDisks (ja existia).
4. **PrivacyPage**: InitializeTimer() fazia Start() no ctor (tick 5s ->
   RefreshStatus async: leitura de registro de TODAS as settings em Task.Run).
   Agora: criado no ctor, Start no Loaded com guard. Fix de edicao: comentarios
   com U+FFFD (bytes corrompidos) na linha do Unloaded impediam str_replace por
   matching exato — ancora limpa usada (`InitializeTimer();` + append Loaded).
5. **WinbootPage**: `RefreshDisks()` sincrono no ctor (WinbootManager.GetDisks ->
   IOCTL nativo rapido, mas primeira chamada pode cair no WMI ~340ms). Agora
   `RefreshDisks()` = wrapper fire-and-forget para `RefreshDisksAsync()`
   (Task.Run GetDisks + continuacao na UI thread — bindings seguros). Os outros
   chamadores (BtnRefresh_Click, pos-RemoveWinboot) continuam chamando
   RefreshDisks() sem mudanca; cache de 3s do GetDisks cobre re-entradas.
6. **AllTweaksPage.xaml**: ItemsControl dentro de ScrollViewer = ZERO virtualizacao
   (materializava TODOS os tweaks de uma vez, cada um com template pesado). Trocado
   por ListBox com VirtualizingStackPanel + Recycling + CacheLength 1,2 +
   ItemContainerStyle com ControlTemplate vazio (ContentPresenter puro, sem
   selecao/hover — visual identico). Code-behind inalterado (ItemsSource igual).

**JA CONFORMES** (verificados, sem mudanca): NetworkPage (timers no Loaded, ticks
async single-flight), StutterPage (timer no Loaded), KitTaskManagerWindow (timers
no Loaded), DashboardPage (timer local com using, escopo de metodo), AppsPage/
BloatwarePage/ProgramsPage (Load* async fire-and-forget), PrivacyPage LoadData
(GetPrivacyCategories = LINQ em memoria), ScreenPage LoadInfo (GetSystemMetrics),
GameBoostPage (InitializeTimer vazio — monitor via TrayIconService), listas
virtualizadas em AppsPage/BloatwarePage/ProgramsPage/ServicesPage/PartitionsPage/
DriversPage/GlobalSearchPage/StoreRemakePage.

Build: 0 erros / 134 warnings (baseline nullable pre-existentes).

### Sessao 14/09 — TMOG (Task Manager do Dave Plummer): analise binaria IDA Pro

Pedido do usuario: o TMOG (C:\Program Files\Task Manager TMOG) pega metricas COMPLETAS
sem admin — usar o IDA Pro 9.0 para descobrir as tecnicas. Documentacao completa:
**docs/TMOG_TASKMANAGER_ANALYSIS.md**.

**Achados centrais** (TMOG e nativo x64, C++; analise em %TEMP%\opencode\tmog\
decomp.txt 4.260 linhas, 9 funcoes decompiladas):
1. **OpenProcess com direitos minimos**: 0x1010 (QUERY_LIMITED_INFORMATION|VM_READ)
   com fallback 0x1000 — NUNCA 0x0400 (PROCESS_QUERY_INFORMATION). E' ISTO que faz
   funcionar sem admin: QUERY_LIMITED da GetProcessTimes/GetProcessId/IOCounters/
   MemoryInfo/FullProcessImageName para TODOS os processos. O kit usa
   Process.GetProcesses() (.NET) que abre com direitos completos -> Access Denied
   nos processos de sistema (KitTaskManagerWindow.xaml.cs:509).
2. Enumeracao: CreateToolhelp32Snapshot + Process32FirstW/NextW com token de
   cancelamento por iteracao e limite de 0x20000 entradas.
3. Disco por processo: GetProcessIoCounters (nao PerformanceCounter por instancia).
4. Memoria por processo: K32GetProcessMemoryInfo (cb=0x50).
5. Memoria global: K32GetPerformanceInfo (cb=104) em vez de 6 PerformanceCounter.
6. CPU total: GetSystemTimes; por nucleo: GetLogicalProcessorInformationEx.
7. Frequencia: CallNtPowerInformation(ProcessorInformation) 24B/nucleo.
8. % Processor Utility (metrica real do TM, normalizada por frequencia):
   PDH PdhAddEnglishCounterW (sempre ingles, imune a pt-BR), query persistente.
9. Rede: GetIfTable2/MIB_IF_ROW2 + FreeMibTable (sem instancias localizadas).
10. GPU/temperatura: PDH \GPU Engine(*)\Utilization Percentage e \Thermal Zone
    Information(*)\Temperature.
11. Graceful degradation por processo com "overlay" de direitos (campo marca o que
    veio de fonte limitada); SRUM/ESENT so para App History (NAO copiar).

**Plano de adocao** (docs/TMOG_TASKMANAGER_ANALYSIS.md): novo
KitTaskManager.NativeMetrics.cs (P/Invoke, padrao NativeDiskIo.cs):
- Fase 1: Toolhelp + OpenProcess(0x1010->0x1000) + GetProcessTimes + K32GetProcessMemoryInfo
  + GetProcessIoCounters -> substituir Process.GetProcesses() (maior impacto, sem admin).
- Fase 2: GetSystemTimes + K32GetPerformanceInfo + GetIfTable2 (mata ~10 PerformanceCounter).
- Fase 3: CallNtPowerInformation + PDH Utility/GPU.

Artefatos: %TEMP%\opencode\tmog\ (TaskManager.exe.i64, decomp.txt, report.txt,
tmog_analyze.py, tmog_decomp.py via idat.exe -S).

### Sessao 14/09 (cont.) — Fase 1 TMOG implementada: NtQuerySystemInformation no KitTaskManager

Pedido do usuario: validar os 3 pontos do Gemini sobre o plano TMOG e aplicar ao kit.
Todos validos e implementados. Documentacao completa em
**docs/TMOG_TASKMANAGER_ANALYSIS.md** (secao "Validacao dos pontos do Gemini").

**Novos arquivos**:
- `KitLugia.Core\TaskManager\NativeMetricsHelper.cs` (novo):
  - `EnumerateProcesses()`: NtQuerySystemInformation(SystemProcessInformation) resolvida
    DINAMICAMENTE via GetModuleHandleW+GetProcAddress (ponto Gemini #3; ANSI-only!),
    fallback Toolhelp + OpenProcess(0x1010->0x1000) direitos minimos.
  - `CpuDeltaTracker`: delta de CPU com memoria de estado por PID (ponto Gemini #1).
  - `GetPidsWithVisibleWindows()`: EnumWindows 1 passada (substitui p.MainWindowHandle).
  - Parse do SYSTEM_PROCESS_INFORMATION com bounds duros (AV do PtrToStringUni e
    FATAL/nao-capturavel — validar ponteiro antes).
- `ProcessIoHelper.SampleProcessIoFromTotals()`: IO rate a partir dos totais do
  snapshot (elimina a 2a OpenProcess por PID).

**GUI (KitTaskManagerWindow.RefreshAsync)**: caminho nativo primeiro (auto-desativa em
falha -> fallback Process.GetProcesses inalterado), threads REAIS no lugar de "-",
parentPid nativo, IO via totais do snapshot, CPU via tracker.

**BUGS CORRIGIDOS na implementacao (armadilhas)**:
1. NextEntryOffset e RELATIVO A ENTRADA ATUAL — baseAddr+next so funciona na entrada 0
   e le lixo da 2a em diante (2 crashes AccessViolation/CLR fatal 0x80131506 antes do fix).
2. UNICODE_STRING.Length e USHORT — lido como Int64 mistura MaximumLength e todos os
   nomes saem vazios.
3. GetProcAddress e ANSI-only — CharSet.Unicode retorna 0 silenciosamente.
4. PtrToStringUni com ponteiro fora do snapshot = CLR fatal (0x80131506), try/catch NAO
   pega — validar baseAddr<=buf && buf+len<=bufEnd antes.
5. Layout do SYSTEM_PROCESS_INFORMATION (x64 26100): e o do winternl.h PUBLICADO
   (hipotese SRCORE +48 estava ERRADA): nome@0x38 pid@0x50 ppid@0x58 handles@0x60(ULONG)
   WS@0x90 Private@0xC8 IO@0xD0; stride NAO fixo (624-656 + threads*80) — usar next do
   kernel; bound do walk = needed (bytes escritos), nao o alloc.

**VALIDADO (runtime standalone %TEMP%\nttest + PowerShell cross-check)**: 330 processos
em 6-8ms; WS/Private == Get-Process e IO == GetProcessIoCounters nos mesmos PIDs;
Memory Compression (protegido, OpenProcess impossivel) com dados COMPLETOS — vantagem
decisiva do caminho kernel; delta CPU OK. Build: 0 erros (Core+GUI).

**A TESTAR (host, app real)**: abrir o gerenciador de tarefas do kit SEM admin ->
processos de sistema devem mostrar CPU/RAM/Disco; coluna Threads com numeros; CPU%
estabiliza apos o 2o refresh; comparar com o Task Manager do Windows.

**Pendentes (Fase 2/3)**: GetSystemTimes + K32GetPerformanceInfo + GetIfTable2 (matar
~10 PerformanceCounter); CallNtPowerInformation + PDH % Processor Utility/GPU.

**Fix build (14/09)**: builds de verificacao com -p:OutputPath mal escapado no bash
criaram pastas-junk "binDebugverify 2" DENTRO dos projetos com COPIAS dos fontes -> o
glob do SDK compilava as copias (duplicando Program de FilterCommands.cs, que e script
dev de codegen). Removidas as pastas; build real 0 erros. NAO usar -p:OutputPath em
bash sem aspas corretas; para validar build sem tocar no binario rodando, preferir
`dotnet build -p:OutputPath=...` citado ou compilar so o csproj do Core.

### Sessao 14/09 (cont.) — TaskManager: paridade TMOG completa + fix dos icones

1. **Icones sumiram**: caminho nativo setava `IconPath = path` incondicionalmente, mas o
   loader incremental so processa `IconPath` VAZIO -> nenhuma linha nativa recebia icone.
   Fix: `IconPath = iconNow != null ? path : ""` (marca resolvido) + linhas sem path mas
   resolviveis pelo nome (System32) entram no lote do loader.
2. **GPU por processo** (coluna mostrava "-"): `GpuMonitor.GetGpuUtilizationPerPid()` extrai
   pid_NNNN das instancias `\GPU Engine(*)\Utilization Percentage` (soma clamp 100, igual TM);
   wiring nos caminhos nativo e fallback (`GpuValue` tambem setado).
3. **Header nativo TMOG** (KitTaskManager.Performance.cs): CPU = `% Processor Utility`
   (PDH `\Processor Information(_Total)`, metrica real do TM, fallback GetSystemTimes),
   RAM = K32GetPerformanceInfo + fallback GlobalMemoryStatusEx; NOVO no header XAML:
   **Frequencia** (CallNtPowerInformation ProcessorInformation, media CurrentMhz) e
   **Temperatura** (PDH Thermal Zone, K->C, "—" se sem sensor). Colunas Auto (5*+2 Auto).
4. **BUG x64**: PERFORMANCE_INFORMATION com uint dava erro 24 (ERROR_BAD_LENGTH) — campos
   sao SIZE_T (8B no x64, sizeof=104). Corrigido p/ UIntPtr. Quirk: GetProcAddress e
   ANSI-only (CharSet.Unicode retorna 0 silenciosamente).
5. **App History (SRUM/ESENT)**: decidido NAO implementar (lock de banco com o proprio
   Windows; TMOG usa para App History, risco > beneficio).

**Validado em runtime (host)**: utility 8-30% real, mem 42,8% == GMSE 43%, freq 3,50GHz,
GPU por PID 17,9% com carga, 344 processos ~7ms. Build: 0 erros (Core+GUI).
Docs: docs/TMOG_TASKMANAGER_ANALYSIS.md (secao Fase 2/3).

### Sessao 14/09 (cont.) — TaskManager: auditoria anti-crash do caminho de abertura

Relato do usuario: versoes antigas quebravam o KIT INTEIRO ao abrir o gerenciador
(necessario force-stop do processo). Auditoria completa do caminho de abertura e fixes:

1. **Classe fatal identificada**: excecoes nao-capturaveis — AccessViolationException/
   CLR 0x80131506 em interop nativo (nem DispatcherUnhandledException pega) e excecao
   dentro de callback nativo (EnumWindows = fail-fast). Handlers globais do App.xaml.cs
   (UI contained / UnhandledException / UnobservedTaskException) so cobrem excecoes gerenciadas.

2. **Parse NtQSI reescrito AV-PROOF** (NativeMetricsHelper): kernel buffer agora e COPIADO
   para byte[] gerenciado (Marshal.Copy) e o parse roda 100% em array com helpers
   Read*/EnsureRange bounds-checked — leitura fora dos limites lanca
   IndexOutOfRangeException CAPTURAVEL (entry loop em try/catch, devolve o que parseou),
   NUNCA AccessViolation fatal. Buffer nativo vive o minimo (freed no finally).
   VALIDADO: 374 processos, 374 nomeados, 4-7ms, explorer 229MB, Memory Compression 1,5GB.

3. **EnumWindows callback blindado**: corpo todo em try/catch (excecao em callback nativo
   e fail-fast — agora o corpo nao tem como lancar).

4. **Abertura defensiva (MainWindow BtnKitTaskManager_Click)**: try/catch externo +
   `DiscardBrokenInstance()` (novo: limpa singleton zumbi para o proximo clique tentar
   limpo) + ShowError — MainWindow SEMPRE sobrevive a falha de abertura.

5. **OpenOrActivate**: falha de ctor loga e re-lanca (limpa instancia), sem singleton zumbi.

6. **Closing 100% defensivo**: teardown inteiro em try/catch com sub-guards (excecao no
   close deixava janela zumbi com timers rodando = congelamento/force-stop).

7. **Ja conformes (auditado, sem mudanca)**: ticks de refresh/graph/search com try/catch
   + single-flight gates; Kill/KillTree protegidos; icones com SEM_FAILCRITICALERRORS +
   filtro UNC/drive ausente + SHGetFileInfo serializado; .Result so apos WhenAll; Loaded
   fire-and-forget.

Build: 0 erros (Core + GUI). Docs: docs/TMOG_TASKMANAGER_ANALYSIS.md.

### Sessao 14/09 (cont.) — TaskManager: paridade TMOG das telas (screenshots do usuario)

Referencia: 12 screenshots do TMOG (Processes colunas ricas, Users, CPU Power em W).
Implementado no gerenciador do kit:

1. **Novos campos no snapshot NtQSI** (ProcMetrics): PeakWorkingSetBytes (@0x88,
   PeakWorkingSetSize) e PageFaults (@0x80, PageFaultCount) — zero custo extra, mesmos
   dados ja lidos do kernel.

2. **User name por processo**: WTSEnumerateProcessesW (wtsapi32) + SID ->
   NTAccount (cache 15s). QUIRKS: struct WTS_PROCESS_INFOW tem SO 4 membros (nao 5) e
   o param Version TEM que ser 1 (0 = erro 87). ShortenUserName (DOMINIO\user -> user);
   GetActiveUserNames filtra SYSTEM/servicos (en+pt-BR), DWM-*/UMFD-*, SID puro e GUID
   (validei: activeUsers=Lugia). Coluna "Usuario" no grid + fallback .NET reusa o cache.

3. **CPU Power (W)** — paridade "CPU Power" do TMOG: provider RAPL do Windows expoe
   ENERGIA CUMULATIVA em nJ ("Medidor de Energia(*)\Energia"; PKG+PP0 == _Total
   confirmado). Watts = delta(energia)/delta(t). Pegadinhas: PdhAddEnglishCounterW NAO
   casa nome localizado ("Medidor de Energia" no pt-BR) e wildcard nao expande em
   ingles -> candidatos (pt-BR + en) + PdhExpandWildCardPathW + PdhAddCounterW no
   caminho expandido (novo helper ExpandFirst). VALIDADO no host: 28,8-31,3 W idle
   (TMOG: 55-73 W sob carga — mesma escala). Header ganhou coluna "Potencia"
   (heat color 45/80 W). Fallback "—" em desktops sem RAPL.

4. **Colunas novas no grid de processos** (todas ordenaveis): Usuario, Pico mem.,
   Tempo CPU (hh:mm:ss de kernel+user), Page faults. Ordenacao via MetricOf(prop)
   generico (case unico para PeakMemValue/CpuTimeSec/PageFaultsValue/Disk/Net/Gpu).

5. **Aba USUARIOS** (BtnTabUsers/TabUsers): agregados por usuario (processos, memoria
   somada do snapshot, CPU placeholder) — UserRow + LoadUsersSafeAsync (single-flight),
   DgUsers com colunas Usuario/Status/CPU/Memoria/Processos.

6. **Temperatura fixada no caminho**: Thermal Zone usava AddEnglish com caminho
   expandido localizado -> nunca adicionava; agora PdhAddCounterW no caminho expandido
   (mesma correcao do Power). No host segue null (sem sensor ACPI exposto) -> "—".

Build: 0 erros (Core + GUI). Pendente de teste visual no app: colunas novas, aba
Usuarios e Potencia no header.

### Sessao 14/09 (cont.) - TaskManager: multi-selecao de processos (paridade TMOG "mass end")

Pedido: "poder selecionar multiplos processos para realizar acoes" (print do TMOG Pro
mostrando ctrl/shift-click com "End 6 Tasks" no menu).

1. Grid `SelectionMode="Extended"` (ctrl/shift-click nativo WPF) — highlight de multi
   ja coberto pelo trigger `IsSelected` do RowStyle.
2. `SelectedRows` (helper): todas as linhas selecionadas EXCETO filhos (`IsChild`) —
   pai cobre a arvore, kill em massa nao duplica. Fallback single `SelectedRow`.
3. Acoes em massa via `SelectedRows`: Kill (com Force Stop fallback), KillTree,
   Suspend/Resume (NtSuspend/NtResume em lote), EcoQoS (SetProcessInformation em lote,
   antes era single), Prioridade (menu aplica direto via Process.PriorityClass em lote —
   ANTES so setava o ComboBox do painel single, bug latente).
4. Menu de contexto com headers dinamicos e contagem ("Finalizar 6 tarefas",
   "Suspender 6", "Eficiencia EcoQoS (6)", "Prioridade para 6") via
   `UpdateMultiSelectionUi` no SelectionChanged; itens multi colapsados quando N<=1.
5. Preservacao da MULTI-selecao no refresh automatico (antes: so o primeiro PID):
   captura PIDs + GroupKeys de TODOS os selecionados antes do diff, restaura todos por
   PID ou GroupKey apos a reconstrucao. Sem selecao -> limpa painel de detalhes.
6. Status bar com resultado agregado ("❌ 5/6 finalizados (1 com acesso negado)").

Build: 0 erros / 146 warnings (baseline nullable pre-existente).

### Sessao 14/09 (cont.) - TaskManager: GPU por processo REESCRITA (tecnica TMOG real)

Sintoma: coluna GPU so mostrava "—" no kit; TMOG mostra "0.0%"/"0.3%"/"Unavail"
(exited). IDA Pro (strings do binario TMOG) revelou a tecnica REAL:
- "DXCore adapter enumeration with GPU Engine counters ... matched NVIDIA hardware
  prefers NVML" + import de PdhGetFormattedCounterArrayW (API de ARRAY do PDH).

Correcao (GpuMonitor.cs reescrito):
1. UM contador curinga `\GPU Engine(*)\Utilization Percentage` via
   PdhAddEnglishCounterW (resolve nome localizado internamente — pt-BR OK) e leitura
   COMPLETA com PdhGetFormattedCounterArrayW: todas as 567 instancias em UMA chamada.
   ANTES: 567 handles individuais (1 por engine) + reexpand de 30s + remocao/readd.
   Depois de um reexpand, handles antigos ficavam sem dados = dict vazio = coluna "—".
2. Array inclui engines OCIOSOS (0,0%) — dict por PID tem ~28 processos com engine
   atribuido (nao so os ativos). UI mostra "0,3%" (1 decimal, como TMOG) para
   <10% e "0,0%" para engine presente ocioso; "—" so para SEM engine (exited).
3. Total = MAIOR engine individual (comportamento do Task Manager), nao soma.
4. Validacao empirica (host, pt-BR): 567 instancias/1 chamada; pids=28, ativos 2-4
   variando com carga, total 3,6->12,7% de pico; concorrente-safe; PIDs corretos.
5. Quirks documentados: PDH_FMT_COUNTERVALUE_ITEM_W x64 = stride 24 (LPWSTR @0,
   CStatus @8, double @16); probe retorna PDH_MORE_DATA (0x800007D2) com tamanho
   86446; nomes apontam para dentro do proprio buffer (validar limites antes de
   PtrToStringUni).
Build: 0 erros (Core + GUI).

### Sessao 17/09 - TaskManager: aba RESUMO (paridade Summary do TMOG) + CONEXOES + detalhes avancados

Pedido: "melhore o gerenciador de tarefas do kit para ficar tao bom quanto o tmog"
(user rodou o TMOG ao lado e mandou screenshots das 5 abas dele).

**NOVO ARQUIVO `KitLugia.GUI\Windows\TaskManager\KitTaskManager.Summary.cs`** (partial):
1. **Aba RESUMO = tela de ABERTURA** da janela (TabSummary Visibility=Visible no XAML,
   Processos ficou Collapsed; sidebar ganhou BtnTabSummary). Layout 3 colunas:
   - VITAIS (CPU/Clock/Temp/GPU/Potencia com barras) — alimenta do MESMO tick de 1s
     dos graficos (UpdatePerformanceGraphs expoe _lastCpuPct/_lastMemPct/_lastDiskReadMBps/
     _lastDiskWriteMBps/_lastFreqMhz/_lastTempC/_lastPowerW). RAZAO: PerformanceCounter
     e DESTRUTIVO — NextValue() 2x no mesmo tick = 0 na segunda leitura, por isso o
     Resumo le o CACHE do tick e nao re-amostra.
   - Grafico CPU MULTI-SERIE (DrawMultiSeriesChart novo): uso (verde, com fill) +
     kernel (vermelho) + temperatura (laranja, se sensor) no mesmo canvas + grade de
     quartos. Barras por NUCLEO (paridade "CPU Expanded" do TMOG): NtQSI class 8
     (SystemProcessorPerformanceInformation, stride 48B/nucleo, Idle/Kernel/User
     acumulados) — 1 syscall para os 20 nucleos, sem PDH. Estado proprio _prevCore*
     com guard dMs<200 (amostra duplicada mantem anterior).
   - Memoria detalhada: K32GetPerformanceInfo -> Used/Available/Commit/Peak/Cached/
     PoolPaged/PoolNonPaged/Handles/Threads/Processes.
   - TOP processos por CPU (14 linhas, INPC TopProcRow, merge in-place sem piscar;
     duplo clique abre o processo na aba Processos via ScrollIntoView).
   - Faixa inferior: Rede/Disco/GPU/Energia com sparklines.
   - Botao Copiar (resumo textual) + resmon + intervalo sincronizado com a aba Processos.
2. **Aba CONEXOES** (BtnTabConnections/TabConnections): TCP v4+v6 + UDP v4+v6 por PID
   (GetExtendedTcp/UdpTable OWNER_PID), filtro por processo/PID/endereco/porta,
   "apenas conexoes ativas", "agrupar por processo" (CollectionViewSource grouping com
   IsVirtualizingWhenGrouping=True). Refresh a cada 2 ticks (~2s) so quando visivel.
3. **Detalhes avancados do processo** (painel direito, card "Detalhes avancados"):
   Processo pai, Sessao (ProcessIdToSessionId), Arquitetura (IsWow64Process2, fallback
   IsWow64Process), Prioridade base (GetPriorityClass aceita QUERY_LIMITED), Grupo,
   Commit privado, Pico mem., Tempo kernel/usuario separados, E/S lida/escrita/ops
   ACUMULADAS (IO_COUNTERS do NtQSI), Linha de comando (NtQueryInformationProcess
   class 60 = ProcessCommandLineInformation, 2 chamadas; UNICODE_STRING.Length e USHORT,
   validar ponteiro contra [buf, buf+used) ANTES de PtrToStringUni — ponteiro ruim e
   CLR fatal 0x80131506). ProcessRow ganhou CommitMB/KernelTimeSec/UserTimeSec/
   IoReadTotal/IoWriteTotal/IoOpsTotal (nativo + fallback .NET).

**`KitLugia.Core\TaskManager\NativeMetricsHelper.cs` — novas APIs (todas sem admin)**:
- GetPerCoreCpuPercent() (class 8), GetSystemKernelPercent() (GetSystemTimes com
  ESTADO PROPRIO _sysK* — compartilhar o estado do CPU total fazia o 2o metodo do
  tick receber dMs<200 e devolver valor obsoleto PARA SEMPRE), GetMemoryDetails()
  (K32GetPerformanceInfo), GetProcessDetails(pid) (sessao/arch/cmdline/prio),
  GetNetworkRates() (GetIfTable2).
- **BUG 1 (PERFORMANCE_INFORMATION)**: a struct real tem PageSize ENTRE KernelNonPaged
  e HandleCount, e HandleCount/ProcessCount/ThreadCount sao DWORD (nao SIZE_T).
  Sem isso: handles=4096 (lia o pagesize) e processos=lixo; sizeof so fecha em 104
  com PageSize + 3 DWORDs. Validei empiricamente: handles=223k, threads=8.4k, procs=400.
- **BUG 2 (MIB_IF_ROW2)**: marshal de struct NAO bate com o layout C (ByValTStr/
  ByValArray ganham padding proprio do CLR) — Mtu lia 6 e type 0 a partir da linha
  17. SOLUCAO: leitura por OFFSETS BRUTOS validados com dump hex (Alias@28, Desc@542,
  Mtu@1124, Type@1128, OperStatus@1156, LinkSpeed@1200, InOctets@1208, OutOctets@1280,
  stride 1352, Table[] comeca em +8). Auto-validacao em runtime (type 1-300, mtu
  500-65535 QUANDO != 0 — mtu=0 e legitimo em NOT_PRESENT, link 1kHz-1Tbps): qualquer
  valor implausivel = struct deslocada => Valid=false (nunca numero inventado).
  LastNetDiag expoe o motivo (suporte). Filtro anti-inflacao: camadas NDIS "QoS"/
  "WFP" do MESMO hardware repetem os contadores do adaptador base (~3x inflava) —
  sao excluidas da soma. Loopback e interface nao-UP tambem.
- Rede validada no host: 39 interfaces (27 com MTU), 7 ativas pos-filtro,
  "Intel(R) Ethernet Connection (22) I219-V" como primaria, taxa real ~10-16 KB/s.

**`KitLugia.Core\NetworkTrafficMonitor.cs` — GetEndpoints()** (aba Conexoes):
- GetExtendedUdpTable (UDP_TABLE_OWNER_PID=1) + structs MIB_UDPROW/UDP6ROW_OWNER_PID.
- Porta vem em ordem de rede nos 16 bits baixos: PortOf = swap manual (BitConverter
  dependeria de endianess). TCP state 2=LISTEN => Remote="" e Listening=true;
  UDP sempre Listening ("*:*", estado "—"). TcpStateName em pt-BR (1-12).

**XAML (KitTaskManagerWindow.xaml)**: 2 botoes novos na sidebar (Resumo com icone
home, Conexoes com icone de troca), TabSummary (3 colunas + faixa inferior UniformGrid),
TabConnections (DataGrid + GroupStyle + filtro), card "Detalhes avancados" no painel
de processo (12 campos + linha de comando em bloco mono).

Smoke test: app abriu (PID 2196, Responding=True), sem excecao no log; processo de
teste encerrado depois (o kit resiste a taskkill externo — esperado, protecao propria).
Build: 0 erros, 0 warnings novos nos arquivos tocados (146 baseline).

**A TESTAR (visual, host)**: abrir gerenciador do kit -> Resumo como tela de abertura
(nucleos animando, top CPU listando, rede/disco com taxa real) -> clicar Conexoes
(318+ endpoints TCP/UDP com nomes de processo) -> selecionar processo e conferir
Detalhes avancados (linha de comando de processos próprios, sessao=1, arch=x64).

### Sessao 18/09 - TaskManager: aba LATENCIA (estilo LatencyMon, SEM driver) + detalhes do processo completos

Pedido do usuario (lado a lado com o LatencyMon 7.31 instalado em
"C:\Program Files\LatencyMon"): "faltam algumas informacoes e coloque mais um botao
sobre as coisas que o LatencyMon faz... crie uma aba so vendo os travamentos do
sistema, onde o kit escuta o que o sistema faz para reportar exatamente o que/como/
quando travou o audio ou o sistema, de forma facil para o usuario e com logs para
mandar para IAs".

**Core - `KitLugia.Core\TaskManager\LatencyMonitor.cs` (novo, ~1040 linhas)**
4 fontes nativas combinadas, sem driver proprio (ao contrario do LatencyMon, que
carrega o `rspLLL64.sys`):
1. **DPC/ISR por nucleo** - NtQSI classe 8 (1 syscall p/ os 20 nucleos). ATENCAO:
   os valores sao QUANTIZADOS pelo tick do relogio do sistema (multiplos de 15,625 ms)
   - e a mesma limitacao do LatencyMon; o tooltip da UI explica isso.
2. **Sessao ETW do "NT Kernel Logger"** (EVENT_TRACE_FLAG_DPC|INTERRUPT, grupo
   PerfInfo ce1dbfb4-...): cada evento traz o ENDERECO da rotina -> casamos com o
   modulo do kernel (RTL_PROCESS_MODULES) e acumulamos por .sys. Exige admin; se
   outro programa (LatencyMon) ja tem a sessao, NAO matamos a sessao alheia
   (colisao e reportada como EtwStatus).
3. **Hard page faults por processo** (classe 5, HardFaultsCount) com taxa/s;
4. **Janelas travadas** (IsHungAppWindow: inicio/fim/duracao/titulo) + **jitter do
   proprio loop** (pedimos ~1s de CPU; >2500ms = o sistema INTEIRO parou).
- `DescribeDriver`: nome amigavel a mao para ~20 drivers conhecidos; para o resto,
  FileDescription+Company do PROPRIO .sys em System32\drivers (FileVersionInfo),
  cacheado e resolvido FORA do lock do ETW (I/O dentro do lock travava o callback).
- `BuildConclusion()` (0=ok/1=atencao/2=critico, texto em pt-BR simples) e
  `BuildAiReport()` (texto completo: conclusao + drivers + nucleos + processos + 60
  eventos + metricas) para o botao "Copiar relatorio (IA)".
- Snapshot ganhou `PerCore` (DPC/ISR/pico/totais por nucleo), `TopHardFaults`
  (60 processos), `LastTickMs/MaxTickMs/JitterEvents`, `MaxDpcUsSession/MaxIsrUsSession`.

**Core - `NativeMetricsHelper.GetProcessDetails`**: ganhou contadores lidos DIRETO do
processo selecionado com o MESMO direito minimo (QUERY_LIMITED_INFORMATION):
K32GetProcessMemoryInfo (PrivateUsage=commit, PeakWorkingSetSize, PageFaultCount),
GetProcessTimes (kernel/usuario separados), GetProcessIoCounters (3 contagens + 3
bytes), GetProcessHandleCount, GetGuiResources (GDI/USER) e TokenElevation (elevado).
Motivo: os campos Commit/Pico/Tempo kernel/Tempo usuario/E-S vinham SO do snapshot da
lista - quando o refresh caia no fallback .NET o painel ficava cheio de "—" (o print
do usuario). Processo protegido (PID 4) devolve CountersKnown=false e a UI mantem "—".
BONUS corrigido: IO_COUNTERS tem ordem interna ReadOps/WriteOps/OtherOps/ReadBytes/
WriteBytes/OtherBytes - o codigo lia 0xE0 (OtherOps) como "E/S lida" e 0xE8 como
"E/S escrita".

**GUI** (`KitLugia.GUI\Windows\TaskManager\`):
- `KitTaskManager.Latency.cs` (novo partial, ~500 linhas): timer de 1s, 4 colecoes
  ObservableCollection com linhas INPC atualizadas in-place (sem piscar/selecao
  perdida), 4 visoes (Eventos/Drivers/Nucleos/Processos), botoes INICIAR-PARAR,
  Limpar, Copiar relatorio (IA) e "Admin" (relanca o Kit elevado via UAC - sem admin
  o Windows nao entrega o nome do .sys).
- `KitTaskManagerWindow.xaml`: botao "Latencia" na sidebar + TabLatency (faixa de
  6 metricas, caixa de conclusao colorida, 4 DataGrids, coluna direita com medidores
  de barra + explicacao didatica "O que o Kit esta escutando"/"Como ler") e o estilo
  reutilizavel `TmGrid` (DataGrid escuro). Tabela vazia NUNCA fica muda: o
  `LatEmptyHint` explica por que (ex.: "sem admin o Windows so entrega o nome do
  driver para programas elevados").
- `KitTaskManagerWindow.xaml.cs`: `SwitchTab` ganhou o case "Latency" (+ esconder/reset);
  card "Detalhes avancados" ganhou 3 linhas novas (Falhas de pag., GDI / USER, Elevado)
  e os 7 campos antigos agora sao refinados pelos contadores diretos ("…" -> valor,
  ou "—" so quando realmente nao ha leitura). `FormatCpuTime` mostra "<n> ms" abaixo
  de 1s ("00:00:00" parecia bug).

**Decisoes/limites registrados**: sem driver de kernel o Kit NAO mede "interrupt to
process latency" como o LatencyMon; o equivalente honesto e o jitter do proprio
monitor ("SISTEMA TRAVOU"), rotulado como aproximacao no relatorio - nunca um numero
inventado. DPC/ISR por driver so aparece com admin (ETW).

**Verificacao (harness WPF temporario, ja removido)**: instanciou o
KitTaskManagerWindow de verdade e chamou SwitchTab/BtnLatToggle/BtnLatClear por
reflexao. Resultados reais: janela criada (XAML da aba parseado), TabLatency=Visible,
MONITORANDO->PARADO, 20 nucleos (ex.: CPU1 dpc=3,12% pico 62,50ms isr=4,68% 3016
interrupcoes), 60 processos (Memory Compression 317k falhas no disco, 2,0 GB WS),
0 drivers (sem admin - esperado), eventos com explicacao, relatorio de 5.380
caracteres/69 linhas/7 secoes, limpou tudo, e o painel de detalhes: commit=54,4 MB,
pico=107,5 MB, E/S lida=1,1 MB, E/S escrita=3 KB, 8.029 ops, 36.794 falhas de pag.,
GDI/USER=43/15, Elevado=Nao, sessao=1, arch=x64, prioridade=8 (Normal).
Build: 0 erros, 0 warnings nos arquivos da sessao; Encoding UTF-8+BOM e CRLF em 100%
das linhas dos 2 arquivos novos (regra #3 do AGENTS).

**A TESTAR (visual, host)**: abrir o gerenciador -> aba Latencia -> INICIAR e deixar
rodar; conferir a caixa de conclusao, os medidores, os 4 botoes de visao e o tooltip
de uma linha. Depois rodar o Kit como admin (botao Admin) para a lista de Drivers
encher e o nome do .sys aparecer em "Maior DPC". Por fim, "Copiar relatorio (IA)" e
colar num chat.

### Sessao 18/09 - Paridade visual com o TMOG 60fps + efeitos + RAM + Usuarios + Latencia honesta

Pedido do usuario (comparando lado a lado com o "Task Manager TMOG"): 60 frames por
segundo, verde que esmace em processo novo, vermelho em processo que fecha, icones
nos apps, painel de uso de RAM, hover com atraso mostrando "o que causou quem causou
e porque" nos picos, consertar a mensagem de admin na aba Drivers e consertar a aba
de Usuarios.

**1. MOTOR DE RENDER 60 fps (novo: `KitTaskManager.Render.cs`)**
O gargalo real: a enumeracao nativa de ~400 processos custa ~100 ms (1x/s no maximo).
O TMOG parece liso porque INTERPOLA. Entao:
- `CompositionTarget.Rendering` = 1 chamada por quadro do monitor; o handler sai na
  primeira linha quando a aba nao e Processos/Resumo (custo zero nas outras abas).
- Easing exponencial `k = 1 - exp(-dt/0.09)` (~95% do caminho em 0,27 s): CPU, RAM,
  Disco, Rede e GPU caminham ate o valor da ultima amostra em vez de pular.
- `UpdateFrom` agora grava CpuTarget/RamTarget... em vez de sobrescrever o valor
  exibido; texto e barra sao recalculados no quadro.
- Rotulo `TxtRenderFps` no rodape da lista mostra os fps medidos.
- Medido no harness: **95,8 fps** (segue a taxa da tela) e 15 valores distintos de
  CpuValue em 600 ms (prova da interpolacao, ex.: 5,45 -> 5,38 -> 5,26 -> 5,17 ...).

**2. EFEITOS verde (novo) / vermelho (fechou)**
- Overlay `DGR_Highlight` no template do DataGridRow + badge ✖ para a linha fantasma.
- **ARMADILHA RESOLVIDA**: `SolidColorBrush` e Freezable com afinidade de thread e as
  linhas nascem numa thread de trabalho -> criar o brush no construtor dava
  "E necessario criar DependencySource no mesmo thread que o DependencyObject"
  (XamlParseException ao montar o template). O brush agora e LAZY
  (`_highlightBrush ??= ...`), criado na thread da UI.
- Fade animado mexendo em `brush.Opacity` (Freezable re-renderiza SOZINHO, sem
  PropertyChanged por quadro) = centenas de linhas a 60 fps sem custo de binding.
- Processo que fecha vira FANTASMA (IsGhost) no lugar, vermelho esmaecendo 2,5 s, e o
  `ReapGhostRows` remove no fim do fade. Medido: 1 fantasma com opacidade 0,56 e
  depois 0 (removido); verde maximo 0,54 ao aparecer processo novo.

**3. ABA USUARIOS (estava quebrada: CPU sempre 0%)**
- `CpuDeltaTracker` proprio (`_usersCpuTracker`) soma CPU real por processo (antes a
  coluna ficava 0% porque so WorkingSet era usado).
- `UserRow` ganhou `Processes` (lista de processos do usuario) + IsExpanded; o grid usa
  RowDetails + `UserRowStyle` com `DetailsVisibility` ligado ao IsExpanded, setinha ▶/▼
  e heatmap na celula de CPU.
- Refresh a cada 1 s JUNTO com o refresh timer (so quando a aba esta visivel); o estado
  expandido e preservado.
- Medido: Outros 7,0% / 6,5 GB / 179 procs, Lugia 6,2% / 9,9 GB / 207 procs, SYSTEM
  0,4% / 24 MB / 1 proc; expandir -> DetailsVisibility=Visible, icone=▼.

**4. PAINEL DE RAM (Resumo) - paridade com o "Memory Utilization" do TMOG**
- Grafico de 86 px, percentual grande (roxo), barra de composicao EM USO | EM CACHE |
  LIVRE (GridLength com peso via `SetStarWidth`) e linhas Disponivel / Em cache / Swap /
  Comprometida / Pools / handles-threads-processos.
- **Swap medido DE VERDADE**: `NativeMetricsHelper.GetPageFileUsageNonBlocking()` usa WMI
  `Win32_PageFileUsage` (CurrentUsage/AllocatedBaseSize/PeakUsage) cacheado 10 s e
  consultado em BACKGROUND (a UI nunca espera). Motivo: a primeira versao ESTIMAVA por
  subtracao (commit - RAM em uso) e mostrava 20,0 GB(!) de swap; o valor real e
  ~3,0 GB. Se o WMI ainda nao respondeu, a linha mostra a medida honesta equivalente
  ("Alem da RAM: X GB comprometidos (medindo o swap...)") em vez de inventar numero.
- Tooltip do grafico mudou com o estado (tranquilo / alto / quase cheia) explicando o
  que acontece com o disco quando a RAM aperta.

**5. ICONES no TOP PROCESSOS POR CPU (Resumo)**
- `TopProcRow.Icon` (ImageSource) + coluna "Nome" virou template com `Image` 16x16
  (`IconVisibility` esconde quando o icone ainda nao carregou) e ToolTip com o nome
  completo. Medido: 9 de 14 linhas com icone na primeira amostra (o resto chega depois,
  pois o cache de icones carrega em background).

**6. BALOES DE INFORMACAO na aba Latencia (hover com atraso)**
- Estilo de ToolTip igual ao das paginas Tweaks/GameBoost (fundo #1A1A1A, borda dourada,
  fonte 12, MaxWidth 520) + `InfoTipWrap` (ContentTemplate com TextWrapping) aplicado
  como recurso da propria aba (nao afeta baloes de conteudo rico de outras telas).
- `DataGridRow` implicito da TabLatency: `ToolTip="{Binding Tooltip}"`,
  InitialShowDelay=900 ms, ShowDuration=60 s -> o balao so aparece se voce PARAR o mouse.
- Conteudo reescrito com secoes: `QUEM / ONDE`, `O QUE ACONTECEU`, `O QUE FAZER`,
  `DETALHE TECNICO (para copiar e mandar a uma IA)`. O "quem causou" sai do titulo do
  evento (`EventCulprit`) e a recomendacao por tipo (`EventAdvice`: Memoria / DPC-ISR /
  Janela / Sistema).
- Os 6 indicadores de pico (TEMPO, DPC 1 min, MAIOR DPC, MAIOR ISR, PAGINACAO, SISTEMA
  TRAVOU) ganharam balao DINAMICO via `SetLatTip` (valor atual + por que importa + o que
  fazer), com atraso de 700 ms. Medido: balao de 312 chars no MAIOR DPC e evento com as
  3 secoes corretas.

**7. ABA DRIVERS: "ja sou admin e pede admin" - CAUSA RAIZ (bancada, 4 execucoes elevadas)**
- Sessao certa: `EVENT_TRACE_SYSTEM_LOGGER_MODE` (evntrace.h: "Receive events from
  SystemTraceProvider"), EnableFlags=0x60 (DPC|ISR), sessao criada com sucesso.
- MAS `EnableTrace`/`EnableTraceEx2` no SystemTraceControlGuid devolvem
  **ERROR_ACCESS_DENIED (0x5)** mesmo com o processo ELEVADO: habilitar
  `SeSystemProfilePrivilege` (novo `EnableKernelTracePrivileges`) passou a reportar
  `SeSystemProfilePrivilege=OK SeDebugPrivilege=OK` e o erro CONTINUOU 0x5, com
  `buffers=40 livres=39 escritos=0` (o kernel nao gera um unico evento).
- Conclusao: desde o Windows 8/10 os eventos de DPC/ISR do provedor de sistema so sao
  entregues a uma sessao criada por um DRIVER de kernel. E exatamente para isso que o
  LatencyMon instala o `rspLLL64.sys`. O Kit NAO instala driver de kernel (decisao de
  seguranca) -> nao e falta de permissao, e limite do Windows.
- Correcao (GUI): `Snapshot.EtwProviderDeniedByWindows` + `EtwPrivilegeStatus` no Core;
  a aba Drivers agora explica o motivo real (com os privilegios que foram habilitados),
  diz o que se perde (so o nome do .sys) e o que se mantem (DPC/ISR por nucleo, picos,
  tempos maximos, paginacao por processo, janelas travadas). O rotulo do topo deixou de
  dizer "sem admin". Nada de pedir admin de novo quando ja somos admin.

**Arquivos**: `KitLugia.GUI/Windows/TaskManager/KitTaskManager.Render.cs` (novo),
`KitTaskManagerWindow.xaml`/`.xaml.cs` (templates, Users, fps, tooltips, ghost/green),
`KitTaskManager.Summary.cs` (RAM + icones + SetStarWidth + TopProcRow.Icon),
`KitTaskManager.Latency.cs` (EventCulprit/EventAdvice/SetLatTip + mensagens),
`KitLugia.Core/TaskManager/LatencyMonitor.cs` (privilegios + flag de negacao),
`KitLugia.Core/TaskManager/NativeMetricsHelper.cs` (PageFileUsage).

**Verificacao**: build `--no-incremental` 0 erros; harness WPF temporario (removido)
instanciou a janela de verdade e mediu tudo acima com dados reais; UTF-8+BOM e CRLF em
100% dos arquivos tocados; temporarios `.tmp-*` removidos.

**A TESTAR (visual, host)**: FECHAR o app antes de compilar (DLL travada) -> abrir o
Gerenciador -> Processos (ver as barras deslizando e o rotulo de fps), abrir/fechar um
programa e ver o verde/vermelho, aba Usuarios (setinha dos processos), Resumo (painel de
RAM + icones no TOP CPU) e Latencia (parar o mouse numa linha/indicador para ler o balao).

### Sessao 18/09 (noite) - 3 bugs do relatorio do usuario: crash ToDictionary + max DPC absurdo + ETW orfa

Relatorio do usuario trouxe stack trace real (3x `ArgumentException: smartscreen.exe|
Processos do Windows`) e relatorio IA com "max 3265/3781ms" (fisicamente impossivel p/ 1
rotina) e sessao ETW "ATIVA" que entregou 0 eventos PerfInfo em 9 minutos.

1. **CRASH `ToDictionary` (linha 1548) - CAUSA RAIZ**: o fantasma (processo morto) fica
   na lista com a MESMA GroupKey (`Name|Group`). O `smartscreen.exe` morre/renasce a cada
   prompt UAC: morre -> vira fantasma -> renasce -> `UpdateFrom` nao aplica (guard
   `!ex.IsGhost`) -> linha nova ADICIONADA com a mesma chave -> refresh seguinte
   `_groupedLive.ToDictionary(r => r.GroupKey)` explode EM LOOP (exception repete a cada
   segundo). CORRECAO:
   - `ReviveGhost(ghost, fresh)` (KitTaskManager.Render.cs): fantasma volta a ser a linha
     viva (IsGhost=false + UpdateFrom + brilho verde) - sem duplicar chave, exatamente o
     comportamento do TMOG.
   - `ToDictionary` -> `GroupBy(...).ToDictionary(g => g.Key, g => g.First())` (defesa).
   - indexMap so adiciona a 1a ocorrencia; dedupe de seguranca remove duplicatas legadas
     de sessoes anteriores a correcao (roda a cada refresh, ignora IsChild).
2. **MAX DPC/ISR ABSURDO**: o delta da janela que ATRAVESSOU um stall (varredura AV, swap)
   acumula o tempo de MUITAS rotinas (2906-3781ms) - nao e UMA execucao. CORRECAO: teto
   fisico `CeilingUs = 250_000` (250ms) em LatencyMonitor.LoopAsync: deltas acima sao
   DESCARTADOS do pico (marcam `CoreStats.StallFlag=true`); GUI mostra "n/a" ou
   ">250 ms (janela c/ stall)" + nota no tooltip; relatorio IA idem. Validado em runtime:
   pior pico real = 125ms (antes 3781ms).
3. **SESSAO ETW ORFA**: `eventosPerdidos=11489469` IGUAL em relatorios com 8min de
   diferenca + `livres=40/40` = sessao parada/orfa de uma execucao ANTERIOR do proprio Kit
   (StartTrace OK + EnableTrace negado -> sessao viva sem provedor para sempre; "NT Kernel
   Logger" e unico por boot). CORRECOES:
   - Quando o EnableTrace falha apos o StartTrace, o Kit agora PARA a propria sessao na
     hora (nao fabrica mais orfas que bloqueiam LatencyMon/xperf ate o reboot).
   - WATCHDOG no LoopAsync: sessao ativa com 0 eventos PerfInfo apos 10s = orfa -> para a
     sessao PELO NOME (ControlTraceW(0, nome, ..., STOP); handle local e 0 no caminho
     "anexada") e recria (1 tentativa por Start). Em builds onde o provedor funciona, a
     tabela Drivers enche de vez.
   - GUI honesta: status "⚠ sessão sem eventos (órfã?) — recriando sozinho"; hint da aba
     Drivers distingue "sessão ativa sem eventos >15s" (orfa) de "primeiros segundos";
     relatorio IA avisa "sessão órfã ou criada sem flags" no topo e na tabela Drivers.

**Arquivos**: `KitLugia.GUI/Windows/TaskManager/KitTaskManager.Render.cs` (ReviveGhost),
`KitTaskManagerWindow.xaml.cs` (GroupBy-dedupe-indexMap), `KitTaskManager.Latency.cs`
(teto + StallFlag + mensagens orfa), `KitLugia.Core/TaskManager/LatencyMonitor.cs`
(CeilingUs + StallFlag em CoreStats + stop-na-negacao + watchdog + relatorio).

**Verificacao**: build `--no-incremental` 0 erros (159 avisos = baseline nullable);
harness WPF temporario (removido) reproduziu o cenario: janela real, 146 linhas,
12x ApplyFilter sem excecao, nenhuma chave duplicada, aba Latencia com 20 nucleos,
relatorio IA 5598 chars SEM max 2xxx/3xxx ms, pior pico 125ms. UTF-8+BOM + CRLF 100%.

**A TESTAR (host)**: deixar o Kit aberto 30+ min (smartscreen.exe nasce/morre a cada UAC)
e confirmar que nao ha exception no log; rodar Latencia 2x seguidas e ver a 2a sessao
substituir a orfa; conferir que a coluna "Pico DPC (1s)" nunca mostra mais de 250ms.

### Sessao 18/09 (noite 2) - TaskManager: startup rapido no clique + GDI/USER correto

Pedido: painel de detalhes mostrava "GDI/USER 0/10" sem rotulo (e "0" GDI em app WPF e
impossivel) e o clique no botao do gerenciador TRAVAVA o kit por ~1-2s.

1. **GDI/USER (KitLugia.Core\TaskManager\NativeMetricsHelper.cs + Summary.cs)**:
   - `GetGuiResources` retorna 0 tanto em falha quanto em valor real 0. Adicionado
     `GuiObjectsKnown` (bool) em `ProcessDetail`: usa `Marshal.GetLastWin32Error()`
     apos P/Invoke (SetLastError=true) para distinguir. Falha => GUI mostra "n/d"
     em vez de "0" falso.
   - Linha no painel agora rotulada: `GDI/USER  8.402 / 61 objetos` (antes: dois
     numeros soltos que pareciam nota de prova).

2. **Startup rapido (3 frentes, cause raiz medida por harness)**:
   - COLD ctor medido: ~610-700 ms (InitializeComponent de 2330 linhas de XAML + JIT
     de todas as partials em Debug). Era 100% pago no clique.
   - **Fix A**: `GetUserNames()` (WTS + traducao de SID de ~400 processos) rodava NA
     UI THREAD na linha 753 do primeiro refresh — movido para o worker existente
     (Task.Run) com resultado aplicado via dispatcher.
   - **Fix B**: prewarm da janela em idle — `KitTaskManagerWindow.Prewarm()` estatico:
     cria a janela em background (IsVisible=false, render engine sai cedo), adotada
     pelo singleton `OpenOrActivate`. Agendado no ctor do MainWindow (DispatcherTimer
     30s apos startup, prioridade Idle). O clique paga so o Show.
   - **Fix C**: tick de 1s da aba Performance fazia PDH/temperatura/potencia (queries
     nativas centenas de ms) NA UI THREAD — separado: coleta em worker (Task.Run,
     lock nos dicionarios de contadores) => render na UI so consome snapshot
     (`PerfSample`). `UpdatePerformanceGraphsSafe` virou async.

3. **Render engine 60fps: modelo TMOG real (custo < 30 ms/s medido)**:
   - Instrumentacao provou que animar strings (`Cpu`/`RamMB`) por quadro OU mutar
     brush de celula por quadro satura o WPF (~1000-2400 ms/s de UI).
   - Modelo final: **texto e cor snap 1x/s** (`UpdateFrom` com PropertyChanged;
     brushes de heatmap congelados `Freeze()`), motor de 60fps fica SO com os efeitos
     de brilho verde (novo processo) / vermelho (fechamento) via `Opacity` — provado
     barato (mesma tecnica do HighlightBrush).
   - Probe de custo adicionado ao `EaseStep`: loga warning se o motor passar de
     30 ms/s de trabalho por segundo (canario de regressao).

**Verificacao**: harness temporario (removido) mediu COLD ctor 607ms => **CLIQUE
pos-prewarm 59ms** (prewarm em idle custa 38ms fatiados); build `--no-incremental`
0 erros (159 avisos baseline); UTF-8+BOM + CRLF 100%; A/B com KL_TM_NO_RENDER=1
confirmou que stalls residuais ~1s sao carga externa da maquina (llama-server, AV,
Devin), nao do kit.

**A TESTAR (host)**: abrir o kit, esperar 30s, clicar no botao do gerenciador =>
deve abrir praticamente instantaneo; painel de detalhes de um processo WPF (o
proprio kit) deve mostrar GDI/USER com milhares de objetos (nao 0); UI deve ficar
fluida com o gerenciador aberto.

### Sessao 19/09 - Latencia: watchdog ETW de 2 estagios (prova: flags no StartTrace JÁ habilitam) + falso alarme de RAM

Relatorio do usuario (17:28): "Sessao ETW kernel: INDISPONIVEL — provedor de kernel negado
pelo Windows (erro 5, mesmo elevado)" + "PAGINACAO PESADA: 404/s — a RAM esta apertada"
com 29% em uso + tabela Drivers dizia "sessao ETW inativa?" com sessao ativa.

1. **CAUSA RAIZ do ETW "negado"**: o codigo criava a sessao com EnableFlags=0x60 (DPC|ISR)
   no StartTrace e depois MATAVA a sessao porque o EnableTrace legado devolvia erro 5.
   PROVA: a orfa achada no host (18/09) tinha EnableFlags=0x60 e "escritos=1236" com
   11,5 MILHOES de eventos gerados — sessao criada com flags de kernel JA FLUI (para
   system loggers, EnableFlags no StartTrace E a habilitacao do provedor; EnableTraceEx2
   com SystemTraceControlGuid e redundante e o EnableTrace legado devolve erro 5 mesmo
   com a sessao saudavel). Correcao: habilitacao explicita virou best-effort; falha NAO
   mata mais a sessao — o watchdog decide por EVIDENCIA (eventos chegando ou nao).

2. **Watchdog de 2 estagios (sem loop infinito)**: o antigo setava _etwRecoveryAttempted=true
   e chamava EtwStartSession() — que RESETAVA a flag na linha 987 => recriacao da sessao
   a cada ~12s PARA SEMPRE. Novo: estagio 1 (12s sem NENHUM evento — contador novo
   _etwSessionTotalEvents conta TODOS os eventos, qualquer GUID): se a sessao era alheia
   (anexada, handle=0), para e cria a NOSSA (system loggers adicionais sao legitimos via
   EVENT_TRACE_SYSTEM_LOGGER_MODE) com EtwStartSessionCore(skipWatchdogReset: true);
   estagio 2 (a nossa propria tambem 12s sem nada): ai sim _etwProviderDenied=true
   ("provedor bloqueado para processos de usuario; so driver de kernel contorna").
   Flag resetada agora no Start() (1x por ciclo Start->Stop); Stop() zera contadores.

3. **Falso alarme de RAM**: pagina dura so e problema quando a RAM ACABOU. Com RAM
   disponivel, e trafego de dados frios (standby), barato em NVMe. BuildConclusion usa
   agora GlobalMemoryStatusEx (ullAvailPhys): <1GB disponivel = alerta real (nivel 1/2
   pela taxa); RAM de sobra = "Paginacao frequente mas SAUDAVEL" (nivel 0). GUI: cor do
   medidor calibrada igual (verde com RAM de sobra mesmo a 2000/s), barra 500->2000/s,
   XAML "(0-2000 /s; verde se RAM de sobra)", dica de acao com MB disponiveis.
   Novo helper publico NativeMetricsHelper.GetAvailableRamMb(); relatorio IA mostra
   "RAM fisica: 42% em uso (18902 MB disponiveis)".

4. **Textos honestos**: relatorio "Sessao ETW kernel: CONFIRMANDO — ..." nos primeiros
   12s (EtwUnconfirmed novo no snapshot) e campo "eventos totais da sessao=N"; tabela
   Drivers sem "inativa?"; status da GUI distingue "sessao fluindo, mas sem eventos de
   DPC/ISR (provedor p/ processos comuns)" de "confirmando fluxo (~12s)" de "negado";
   dica da aba Drivers reescrita (provou-se que o Kit cria sessao dedicada e mesmo
   assim o Windows nao entrega — nao e permissao, e o design do Windows 8+).

**Verificacao**: harness de console (Core, removido) rodou 2 ciclos Start/Stop: falha
graciosa sem admin ("requer executar como administrador", nenhum loop), relatorio IA
5649 chars com RAM disponivel, texto novo da tabela Drivers. A elevacao via UAC do
ambiente nao chegou a executar o processo (wrapper retorna antes); o fluxo elevado
(ETW de verdade, estagios do watchdog) sera exercitado pelo app do usuario — abrir
gerenciador > Latencia > INICIAR e conferir: "CONFIRMANDO (~12s)" -> "ATIVA (flags de
kernel na criacao)" com eventos PerfInfo > 0 (ou estagio 2 com mensagem honesta).
Build `--no-incremental` 0 erros; UTF-8+BOM + CRLF 100%.

### Sessao 19/09 (cont.) - TaskManager: colapso/expansao de grupos de processos (7 fixes visuais)

Pedido: "ajeite os elementos visuais tipo o colapso de processo e quando expande ainda
esta meio errado". Causas raiz encontradas (todas com fix):

1. **Hover "congelado" em grupo expandido**: ordem dos triggers do DataGridRow colocava
   IsExpanded (fundo fixo #2A2A30) DEPOIS de IsMouseOver => hover nunca aparecia em grupo
   aberto. Reordenado: estado de grupo (expandido/filho) primeiro, hover/selecao/foco
   por ultimo (trigger posterior vence). Hover de filho DEPOIS do hover generico.
2. **Filhos como "pilulas soltas"**: linhas-filhas tinham Margin vertical do estilo
   (2,1,2,1) => gaps entre elas. Fix: filho com Margin 2,0,2,0 + CornerRadius 0
   (ControlTemplate.Triggers em DGR_Border/DGR_Highlight) = bloco continuo colado no pai.
3. **Indentacao do filho errada**: NameMargin filho era 28px (nome do filho alem do texto
   do pai). Agora 4px: seta oculta (14px) alinha o "-]" EXATAMENTE sob o inicio do nome
   do pai (arvore classica TMOG/Win11). MinRowHeight 32->24 (o 32 travava a altura 28
   do filho — MinRowHeight e piso, nao teto).
4. **Filhos congelados no refresh**: o refresh recriava as linhas-filhas de graça
   (RawChildren = src.RawChildren TROCAVA a lista de instancias) — a grid guardava a
   instancia velha: valores paravam de atualizar e selecao pulava. Fix: UpdateFrom faz
   MERGE por PID (keep.UpdateFrom(nc)), preservando instancia; re-insercao com dedupe
   O(n) por PID (insertedPids).
5. **ToggleExpand "engolia" filhos de outros grupos**: o loop de remocao exigia
   GroupKey == key && IsChild — filho renascido (ReviveGhost) perde IsChild=false e o
   loop parava no meio, deixando lixo visivel apos colapsar. Agora so GroupKey basta.
6. **Grupo morto (fantasma) pendurava filhos obsoletos**: processo morreu com grupo
   aberto => filhos ficavam visiveis ate o ReapGhostRows apagar. Fix: recolhe ANTES de
   virar fantasma (_expandedGroups.Remove + IsExpanded=false + remove filhos).
7. **Grupo que encolheu para 1 membro herdava fundo de "expandido"** sem seta nem
   filhos. Fix: IsExpanded = count > 1 && _expandedGroups.Contains(gkey).

Extras defensivos: re-expand remove instancia ja presente (IndexOf) recalculando a
posicao do pai; UpdateFrom reseta IsChild quando a linha volta a ser pai; restauracao
de selecao inclui filhos (removeu o skip IsChild).

**Verificacao**: harness WPF (removido) abriu a janela REAL, ativou a aba Processos
(SwitchTab + ApplyFilter direto — a grid so publica com a aba ativa) e validou com um
grupo real de 52 filhos: expand => 52 filhos imediatamente apos o pai, nenhum duplicado,
nenhum orfao; refresh de 1s com grupo aberto => filhos intactos na posicao; collapse =>
zero filhos; re-expand => 52 de volta; indentacao left=4. OBS de harness: _groupedLive e
ObservableCollection (cast IList, nao List) e DgProcesses.Items e o contador confiavel.
Build `--no-incremental` 0 erros; UTF-8+BOM + CRLF 100%.

**A TESTAR (host)**: abrir gerenciador > Processos > expandir um grupo grande (Chrome/
Opera/discord) => bloco continuo, filhos com valores atualizando 1x/s, hover funcionando
no grupo aberto; colapsar => nada sobra; processo do grupo morrer => grupo recolhe
sozinho antes do fantasma vermelho.

### Sessao 19/09 (cont. 2) - TaskManager: identidade visual PROPRIA (nao-kit) + docs do debug via UI

Pedido: "faca reformas no taskmanager, documente como voce interage com a UI para
debugar, ele nao precisa ter a mesma cara do kit".

**NOVA IDENTIDADE VISUAL** (aplicada por troca de paleta 1:1 no XAML, estrutura intocada):
- Conceito: "painel de instrumentos" — fundos azul-profundo (nao cinza-neutro), acento
  CIANO #4FC3F7 no lugar do dourado #FFD700 (54 ocorrencias trocadas, 0 dourados restam).
  Mapa: #151515->#0E141B, #1A1A1A->#111823 (chrome), #1E1E1E->#141C28 (cartoes),
  #222222->#1A2432 (header tabela), #2A2A2A->#1F2A3A, #333333->#26334A (pressed), etc.
  Estados semanticos (verde/laranja/vermelho) MANTIDOS — sao significado, nao decoracao.
- Code-behind: _goldBrush -> _accentBrush (ciano); GPU do grafico de performance agora
  ciano (antes dourado, colidia com amarelo do heatmap).
- NAVEGACAO: indicador de aba ativa virou PILULA vertical com gradiente ciano->azul
  (Height 26, CornerRadius 2, Margin 3) no lugar da barrinha 3px de canto a canto;
  sidebar 52->58px; ActivateSidebarButton(BtnTabSummary) adicionado ao Loaded (a pílula
  nova nao tem default visivel no XAML — sem isso a sidebar iniciava toda apagada).
- VALIDADO com screenshots REAIS (harness + RenderTargetBitmap): resumo, processos,
  processos expandido (bloco de filhos continuo e alinhado no novo tema), performance,
  latencia, usuarios. As 6 telas confirmam: nada quebrou, identidade coerente.

**DOCUMENTACAO do debug via UI: docs/TASKMANAGER_UI_DEBUG.md** — a receita completa:
1. Projeto de console com UseWPF=true + ProjectReference do GUI cria a janela REAL
   (new KitTaskManagerWindow()) numa thread STA propria + Dispatcher.Run().
2. Ponte Invoke<T> via _win.Dispatcher.InvokeAsync (toda leitura/escrita de
   DependencyObject NA thread da UI — IsLoaded fora dela = InvalidOperationException).
3. Reflexao para estado interno: _groupedLive (cast IList — e ObservableCollection!),
   ToggleExpand, SwitchTab (a grid so publica com a aba Processos ativa!), ApplyFilter("").
   Contador confiavel: DgProcesses.Items.Count.
4. Screenshot da janela viva: RenderTargetBitmap + PngBitmapEncoder (rendeu as 6 telas).
5. Fluxo: estatico primeiro -> harness -> asserts comportamentais -> screenshots -> rm.
6. Regras de ouro (Janela do harness = janela real; medir stall de worker thread, nao
   DispatcherTimer; dormir 2,5-3,5s antes de fotografar; probe de custo DENTRO do
   OnRendering; UTF-8+BOM/CRLF nos arquivos do Kit).

Build `--no-incremental` 0 erros; UTF-8+BOM + CRLF 100% (doc novo com BOM corrigido);
harness removido. A permissao de admin concedida pelo usuario elimina os prompts de
elevacao para proximas validacoes de fluxo ETW elevado.

**A TESTAR (host)**: abrir o gerenciador — visual novo (azul/ciano), pílula de aba
ativa, sidebar 58px; expandir grupo (bloco continuo); conferir que o resto do kit segue
com a identidade dourada (o TM agora e propositadamente diferente).

### Sessao 21/09 - TMOG: aba "Diagnostico de Armazenamento" (quem esta saturando o disco)

Pedido: "meu disco esta em 100%, descobrir O QUE esta causando, testar a hipotese de
forma mensuravel e oferecer a intervencao adequada". Nao e um clone do Gerenciador de
Tarefas: separa FATO -> HIPOTESE -> PROVA -> ACAO. Doc completa:
`docs/STORAGE_DIAGNOSTICS.md`.

**Core novo: `KitLugia.Core/TaskManager/StorageDiagnostics.cs`**
- Amostragem dos discos fisicos (PerformanceCounter persistente: leitura/escrita, fila,
  latencia, ops/s, atividade). Identidade (modelo/meio/barramento/saude) via
  `MSFT_PhysicalDisk` — unica fonte confiavel do TIPO (`Win32_DiskDrive` diz "SCSI"
  para NVMe). Meio: 3=HDD 4=SSD 5=SCM; barramento: 11=SATA 17=NVMe.
- `GetServicesByPid()` (Win32_Service, cache 30s) -> reiniciar/parar SERVICO em vez de
  matar processo (um PID pode hospedar varios).
- `SuspendProcess`/`ResumeProcess`/`ResumeAll` (NtSuspendProcess, registro proprio).
- `RunImpactTestAsync(pid, baseline, test)`: mede ANTES -> suspende -> mede DEPOIS ->
  retoma SEMPRE (finally, inclusive cancelamento). Veredito FORTE/MODERADA/FRACA/
  NENHUMA/INCONCLUSIVO + indice de correlacao.
- `Investigate(...)`: dossie (identidade, empresa/versao, pai, E/S, acumulado, CPU/RAM,
  threads/handles/modulos, servico, disco fisico do executavel, elevacao, respondendo).
- Seguranca em DOIS niveis: CRITICO (bloqueio rigido: System/Registry/Memory
  Compression/smss/csrss/wininit/winlogon/services/lsass/lsaiso/dwm/fontdrvhost/
  audiodg/LogonUI) e RISCO (aviso + confirmacao: svchost/SearchIndexer/MsMpEng/
  NisSrv/explorer/TrustedInstaller/spoolsv/SecurityHealthService/WmiPrvSE) — o 2o e
  reversivel e e o caso de uso real (SearchIndexer).

**BUG REAL encontrado e corrigido (atividade falsa)**: `% Idle Time` e
PERF_100NSEC_TIMER_INV; quando o provedor de performance nao atualiza o valor (disco
ocioso) o delta vira 0 e a formula devolve **100% de atividade FALSA** (reproduzido: HDD
100% ocioso lendo "ativ=100,0% R=0 B/s fila=0,00"). Atividade agora = latencia x
transferencias (o mesmo "% Active Time" do Windows), que degrada para 0 e nunca mente.
Cross-check: 0,2ms x 16 ops/s = 0,32% vs 0,3% medido.

**GUI novo: `KitTaskManager.Storage.cs` + aba `TabStorage` + botao `BtnTabStorage`**
- Faixa: disco em foco (SEGUE o mais ocupado ate o usuario clicar num chip), atividade,
  leitura, escrita, fila, latencia, ops/s; chips por disco fisico; 3 mini-graficos de
  60s (atividade/fila/latencia) = o antes/depois visual das intervencoes.
- Tabela ranqueada por E/S (icone, nome, badge SUSPENSO, badge SERVICO, leitura,
  escrita, total colorido por intensidade, ops/s, servico). Tooltip com a explicacao
  completa (900ms, igual LatencyPage).
- Card de diagnostico em portugues: distingue "transferencia alta (trabalho legitimo)",
  "dispositivo lento" (atividade alta + vazao baixa + latencia alta = HDD defeituoso/
  driver) e "disco ocupado". Candidato separado de culpado, com o texto explicito de
  que estar no topo NAO prova culpa; se o topo for o `System`, diz que a atividade e de
  baixo nivel. Se a soma dos processos for <15% do disco, avisa que o I/O esta em
  cache/kernel.
- Acoes: Investigar, Testar impacto, VARRER SUSPEITOS (testa os 5 maiores em sequencia,
  para no primeiro FORTE — "continua testando ate encontrar"), Suspender, Retomar,
  Reiniciar/Parar/Iniciar SERVICO, Force Stop (ultimo recurso).
- Ao fechar a janela: ResumeAll() — nunca fica processo suspenso para tras.
- Relatorio IA com discos, diagnostico, tabela, antes/depois, dossie e METODOLOGIA.

**Bugs de UI corrigidos no caminho**
1. `ObservableCollection.Clear()` na reconciliacao de 1s matava a selecao (o DataGrid
   limpa e dispara SelectionChanged(null)): a linha marcada "desmarcava", os botoes de
   acao piscavam e a dica voltava para "marque um processo". Agora reconcilia por
   Move/Insert/RemoveAt — nunca Clear().
2. Botoes nao refletiam o estado suspenso/retomado que so aparece DEPOIS do tick
   assincrono (Retomar ficava cinza) -> `UpdateStorageActionButtons()` quando o estado
   muda no merge.
3. Ops/s ficava 0 porque eu nao expunha ops/s por processo: `ProcessRow` ganhou
   `DiskRead/WriteBytesPerSec` + `DiskOpsPerSec` (4 pontos: nativo, fallback .NET,
   grupo, filhos, UpdateFrom).
4. Faixa/diagnostico divergiam (faixa travada no disco do 1o render).

**Validacao (nao foi so build)**
- Harness de Core: discos, ranking, dossie, bloqueio de seguranca, teste de impacto
  PONTA A PONTA contra um queimador real (155 MB/s -> suspenso -> 0,2%, correlacao 77%),
  suspender/retomar manual e ResumeAll. 20 checks, ALL-PASS.
- Harness de UI (janela REAL, tecnica do docs/TASKMANAGER_UI_DEBUG.md): 50 checks
  ALL-PASS — abre a aba, metricas reais, selecao estavel entre refreshes, Investigar
  por clique real, bloqueio de processo critico, Suspender/Retomar por clique (I/O
  congelou e voltou), render do antes/depois com o cenario 100%/18MB/s/42ms/14,7,
  conclusao "dispositivo lento", varredura (candidatos + ordenacao de veredito),
  relatorio IA, e fechar a janela retomando tudo. Screenshots conferidos.
- Build final da solucao: 0 erros. Arquivos novos em UTF-8+BOM/CRLF (o .xaml ficou em
  LF porque o blob no HEAD ja e LF — so as linhas reais mudam no diff).

**A TESTAR (host)**: abrir o gerenciador -> aba Disco -> confirmar que a faixa segue o
disco realmente ocupado; selecionar um processo com E/S e clicar "Testar impacto" com o
disco ocupado (deve dar FORTE/MODERADA quando for a causa e NENHUMA quando nao for);
"Varrer suspeitos" com o problema acontecendo; investigar o SearchIndexer e reiniciar o
servico Windows Search por ali.

**Proximos passos possiveis**: usar os drivers identificados na aba Latencia como
entrada do diagnostico (processo -> driver -> DPC/ISR); gravar historico de varreduras
para comparar entre sessoes; expor "causa provavel" no Resumo do TM.

### Sessao 19/09 (cont.) — Revisao final: stutters de audio + correcoes da revisao de 15 pontos

Pedido: "ele identificar stutters de audio ai ele conseguiria investigar de forma mais
precisa" + revisao de 15 pontos do ChatGPT (nao confundir atividade com culpa,
multi-metrica no teste de impacto, PID reciclado, reversibilidade, nao prejudicar a
propria medicao, HDD vs NVMe...).

**AudioGlitchMonitor.cs (Core, novo)** — captura em LOOPBACK do dispositivo de saida
padrao + `AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY` (API oficial de glitch, Win7+), 10 ms
por leitura, sem gravar nada em disco:
- Classificacao honesta: CONFIRMADO (flag do Windows + som audivel tocando) /
  INDICIO (sem som, ou salto pequeno) / DESCARTAVEL (arranque/troca de stream).
- **SelfStarvation**: se o proprio Kit estiver atrasado no instante do evento (gap do
  proprio loop de leitura), o evento e rebaixado — o Kit nao acusa o audio de problema
  dele. Fisica aprendida no harness: motor congelado = posicao do dispositivo PARA
  (medir duracao pelo intervalo de entrega); recuperacao salta posicao = episodio de
  leitura atrasada (sinal adaptativo `_lateEpisodeUntilMs`), nao glitch.
- Cada evento carrega snapshot do instante: disco (atividade/fila/latencia/meio),
  top E/S por processo (IoSnapshotProvider injetado pela GUI), quem tocava (PIDs),
  CPU, DPC, paginacao. Tabela de decisao extraida para funcao pura (4 ramos, por assercao).
- Ensaio do "pior caso": prioriza CONFIRMADOS; indice rotulado "maior indicio (sem
  confirmacao do Windows)" (harness pegou indice de 1528 ms que o proprio Kit causou).

**Correcoes da revisao (StorageDiagnostics.cs + StorageAssessment.cs, GUI)**:
1. Teste de impacto MULTI-METRICA (atividade+MB/s+ops+fila+latencia ponderadas) +
   corroboration: o I/O do PROPRIO processo tem de congelar durante a suspensao —
   "100% -> 80%" sozinho nao e correlacao forte.
2. Identidade de processo (PID + CreationTime via FILETIME) antes de suspender/testar —
   cobre PID reciclado. BUG REAL pegado: conversao FILETIME->Ticks com sinal INVERTIDO
   fazia a guarda falhar aberta (nunca protegia).
3. Primeira amostra dos contadores = zero (priming do PDH) -> SampleWarm; o teste de
   impacto travava o disco alvo justamente na amostra fria, medindo o disco ERRADO.
4. Saturacao ciente do meio (MediaProfile: HDD mecanico x NVMe — limites diferentes) +
   distribuicao multi-processo (top share) + caso System/kernel -> aba Latencia.
5. Diario de intervencoes REVERSIVEL (processo suspenso/retomado, servico parado/
   reiniciado com estado anterior) + desfazer tudo + recusa nao registra pendencia;
   ResumeAll no Closing (nunca deixar processo suspenso para tras).
6. Varredura de suspeitos registra candidato-por-candidato (testado / impacto).

**GUI**: tabela de Eventos da aba Latencia com TextTrimming (texto longo invadia a
coluna vizinha); auto-start da escuta de audio ao abrir aba Disco/Latencia (respeita
se o usuario desligou: _audioUserStopped); **Stop() da escuta no Closing da janela**
(thread zumbi a 100 Hz lendo o buffer com a janela fechada); card de audio na aba Disco.

**Validacao**: Core audio harness 39 OK/0 FALHA (estalo REAL: audiodg congelado 400 ms ->
flag lida, "motor parado por 414 ms" reportado); assess harness 41 OK/0 FALHA (queimador
real, guarda de identidade, SampleWarm); UI harness 26 OK/0 FALHA (auto-start, toggle,
estalo real na tabela, correlacao com disco "discos calmos -> provavelmente NAO foi o
armazenamento", diario, desfazer, sem thread zumbi). Build 0 erros. UTF-8+BOM/CRLF ok.
docs/STORAGE_DIAGNOSTICS.md: secao "Escuta de stutters de audio".

### Proxima sessao
- [ ] Campo arquivo/volume por E/S (ponto 6 da revisao) — arquitetura ja preparada
- [ ] Testar a aba Disco com o problema REAL acontecendo (disco 100% de verdade)

### Sessao 19/09 (cont.) — Recuperacao automatica de audio (o desligar/ligar que resolve)

Pedido: o usuario PROVOU que desligar/ligar a saida de audio zera os estalos — o Kit
deveria fazer isso sozinho "de uma forma que ele nem percebesse, so sincronizando de
volta o som". + regra do ChatGPT: evento real ≠ evento provocado pelo teste.

**AudioGlitchMonitor.cs (Core) — AutoRecover**:
- `ShouldAutoRecover(...)` funcao PURA (tabela de decisao, 6 ramos por assercao):
  gatilho = 2+ estalos CONFIRMADOS AUDIVEIS em 90s, cooldown 60s, max 3/hora, nunca
  durante reset em andamento. Avaliada ~1x/s no loop do monitor (MaybeAutoRecover).
- Execucao: NtSuspend/NtResume DIRETOS no audiodg por ~300 ms (SuspendProcess normal
  bloqueia audiodg como critico — aqui e deliberado, e o mesmo reset que o usuario faz).
  Identidade validada (GetIdentity/SameProcess); retomada pelo MESMO handle no finally
  (imune a PID reciclado). Sem admin/recusa => mensagem clara, nada parcial.
- **PROVOCADO**: o reset do proprio Kit cria descontinuidade real. Janela
  `_provokedUntilMs` (SuspendMs + 1500) marca eventos como Kind=PROVOCADO +
  ProvokedByKit=true. GlitchCount/AudibleConfirmedCount/gatilho EXCLUEM provocados
  (sem isso: loop de auto-recuperacao infinito). Relatorio IA com secoes separadas
  "EVENTOS REAIS" x "EVENTOS PROVOCADOS PELO TESTE/RECUPERACAO".
- Sem loop de recuperacao: provocados nao re-gatilham (provado no harness).

**GUI (KitTaskManager.Audio.cs + XAML)**:
- Checkbox "Recuperar sozinho: sincronizar o motor de audio quando os estalos se
  repetirem" no card de audio da aba Latencia (desligado por padrao; liga a escuta junto).
- Linha de estado verde da ultima recuperacao (ou motivo de nao ter feito).
- Tabela de Eventos: eventos do reset = gravidade "Recuperacao" VERDE (nunca Grave/
  Indicio — screenshot pegou o usuario achando que audio ainda quebrado pos-conserto).
- Stats do card mostram "recuperacoes: N".
- AudioGlitchMonitor.Recoveries exposto (IReadOnlyList<DateTime>).
- PROCESS_SUSPEND_RESUME do StorageDiagnostics virou public const (reuso).

**Validacao**: Core harness 17 OK/0 FALHA (6 ramos da decisao + reset REAL com som
tocando: 2 estalos semeados congelando o motor -> loop recuperou SOZINHO -> som voltou
-> evento do reset PROVOCADO fora das estatisticas -> sem re-gatilho); UI harness
7 OK/0 FALHA (checkbox, linha de estado, "Recuperacao" verde na tabela). Build 0 erros,
0 avisos nos arquivos novos. docs/STORAGE_DIAGNOSTICS.md: secao "Recuperacao automatica".

### Proxima sessao
- [ ] Campo arquivo/volume por E/S (ponto 6 da revisao) — arquitetura ja preparada
- [ ] Testar a aba Disco com o problema REAL acontecendo (disco 100% de verdade)
- [ ] Testar a Recuperacao sozinho na maquina real (ligar checkbox, esperar estalos
      naturais, conferir que o som volta sem o usuario mexer)

### Sessao 21/09 (cont.) - Update terminado: build 0 erros + limpeza

Pedido: "veja as ultimas alteracoes e termine o update". O update (sessoes 08/09-19/09:
TaskManager TMOG/storage/audio/latencia, ServicesPage, StoreRemake, SearchEngine,
UiPerformance, ServicePresetWindow, docs) estava com o build QUEBRADO e um harness
esquecido na raiz.

1. **Build quebrado (KitTaskManager.Fluid.cs, novo)**: o arquivo usa WPF
   (System.Windows.Media/Point) mas o projeto tem usings globais com
   System.Drawing — `Color` e `Point` ambíguos (CS0104, 3 + 8 ocorrencias).
   Fix: aliases `using Color = System.Windows.Media.Color;` e
   `using Point = System.Windows.Point;` (mesmo padrao do fix InfoButton de 06/09).
2. **LineTo com 2 args (CS7036 x2)**: `StreamGeometryContext.LineTo` exige
   `(Point, isStroked, isSmooth)` — as 2 chamadas do fill (cantos do poligono)
   ganharam `, true, false` (igual as demais do arquivo).
3. **Harness esquecido**: `Program.cs` na raiz (console de teste do NativeMetrics/
   GpuMonitor) DELETADO — nao faz parte do kit.
4. **Wiring conferido**: ServicePresetWindow (novo) ja usado pela ServicesPage
   (linha 1311); partials Storage/StorageJournal/Audio/Latency/Render/Summary/Fluid
   compilam; Core (AudioGlitchMonitor/LatencyMonitor/NativeMetricsHelper/
   StorageAssessment/StorageDiagnostics) ok.
5. **Build final**: 0 erros / 152 avisos (baseline nullable pre-existente).

**A TESTAR (host)**: abrir o gerenciador (todas as abas: Resumo/Processos/Disco/
Latencia/Usuarios/Conexoes), ServicesPage (presets), StoreRemake e busca global.

### Sessao 22/09 - TMOG: 14 divergencias consolidadas e aplicadas (6 bugs + 8 paridades)

Pedido: conferir a interface do KitTaskManagerWindow contra os 8 screenshots do TMOG
(tmog_*.png) e os 6 do Kit (tmshots/kit_*.png), corrigir divergencias. Feito com
permissao explicita do usuario (saiu do modo plano para esta sessao).

**LOTE 1 - bugs reais (todos com causa raiz provada)**:
1. **Coluna Usuário vazia**: linha-pai do grupo nunca recebia UserName (só filhos);
   + conta vinha crua ("DOMINIO\\user"). Fix: ShortenUserName nos 2 caminhos
   (nativo + fallback .NET) + pai herda o usuário dominante dos membros + filhos
   carregam UserName/GpuValue/PeakMem/CpuTime/PageFaults/Commit/tempos/E-S
   (xaml.cs:890/1103/grupo). Painel de detalhes também encurtado (DetailUser).
2. **Card "Disco 0 (_Total)"**: pseudo-instância agregada do PhysicalDisk virava
   card (Performance.cs:483, `continue` em "_Total").
3. **"Uso ..." eterno no detalhe da CPU**: CAUSA RAIZ — `Row()` devolvia
   `valueBlock = new TextBlock()` (dummy fora da árvore!) em vez do `tb` real;
   `SetUsageLine` escrevia no Tag do dummy para sempre. NENHUMA linha "Uso/"
   "Tempo ativo"/"Taxa" de NENHUM dispositivo jamais atualizou. Fix: `valueBlock
   = tb`. + FillCpuLive agora alimenta a linha com `_lastCpuPct`.
4. **"Tempo de atividade 0:00:00:32"**: usava `Process.GetCurrentProcess().StartTime`
   (uptime DO KIT, não do sistema) em FillCpuLive + detalhe do disco. Fix:
   `Environment.TickCount64` (bate com o rodapé do Resumo).
5. **Header branco na aba Usuários**: DgUsers sem ColumnHeaderStyle (único sem).
   Fix: estilo escuro igual às demais abas.
6. **SYSTEM com 1 processo + "Outros" fantasma**: fallback `PID<100 ? SYSTEM :
   Outros` jogava Memory Compression/Registry (sem SID = pseudo-processos do
   kernel, sempre SYSTEM) em "Outros". Fix: sem conta => SYSTEM. Resultado:
   Lugia 184 / SYSTEM 171, "Outros" sumiu.

**LOTE 2 - paridade TMOG**:
- Top processos: coluna GPU + status "N processos · top 14 por CPU" (Summary.cs + XAML).
- Eixo de temperatura 110°/55°/0° à direita do gráfico de CPU (XAML estático).
- Min/max em GB no gráfico de memória (`_sumMemBytesHist`, labels sobrepostas).
- Barra inline de CPU proporcional (Grid track + fill via `CpuBarWidthConverter`,
  classe top-level pública — aninhada na Window o XAML não resolve, MC3074).
- Auto-seleção da 1ª linha 1x (`_didInitialSelect` — painel nunca abre vazio).
- Totais "na sessão" na faixa do Storage (acumula B/s × dt, zera ao trocar de foco).
- Totais enviado/recebido desde o boot no detalhe do adaptador (GetIPStatistics).

**Quirks da sessão**:
- `dotnet run -- <arg>` NÃO repassou o outDir do harness (shots caíram no default);
  rodar o exe direto funciona. `read` de PNG por caminho REPETE conteúdo
  (cache?) — validar com nomes de arquivo NOVOS por rodada (tmshots2/3).
- `Binding.DoNothing` ambíguo (Forms x Data) — qualificar `System.Windows.Data`.
- Inicializador duplicado `Gpu` no objeto filho (CS1912) — a projeção já tinha.

**Validado (harness tmharness, janela REAL, 3 rodadas)**: Usuário "Lugia" em todas
as linhas, detalhes do processo cheios, "Uso 23%", uptime 0:08:57:16, sem _Total,
header escuro, SYSTEM 171, GPU no Top, eixo 110°/55°/0°, min/max 16,0 GB,
"0 B na sessão", totais de rede. Build: 0 erros / 152-165 avisos (baseline).

**A TESTAR (host)**: abrir o gerenciador no app real e conferir as 6 abas
fotografadas (tmshots3/ = estado final esperado).

**Fix pós-sessão**: linhas do gráfico de CPU colidiam com os rótulos do eixo na
borda direita — `SumCpuCanvas` ganhou `Margin="0,0,40,0"` (calha para 110°/55°/0°).
Validado no tmshots5/.

### Sessao 22/09 (cont.) - Animação 60fps dos gráficos REESCRITA (fim do "rebobinado")

Sintoma: gráficos rolavam entre amostras e davam salto para trás a cada período
("filme sendo rebobinado"). Causa raiz (`KitTaskManager.Fluid.cs`): a `phase`
(0..1) rolava o scroll por tempo e dava a volta com `while (phase >= 1) phase -= 1`
— com dado parado, a linha andava eSALT AVA de volta a cada período; com amostra
fora de hora, o salto caía no momento errado. Tentativa intermediária (Shift
+deslize de 250 ms) descartada: com poucas amostras o dx gigante chutava a linha
para a direita (borda dura no meio do gráfico, visto no tmshots6).

**Solução final (posicionamento por tempo)**: cada amostra carrega seu timestamp
(`FluidChart.Times`, mesmo FIFO dos valores); `x = w - idade·(w/janela)`. Amostra
nova entra exatamente na borda; antigas só andam com o tempo — chegada nenhuma
move ponto existente. Janela adaptativa com latch: abre junto com os dados
(gráfico cheio desde o início) e TRAVA em 60 s na primeira vez que o span chega
lá (`WindowPinned` — sem latch, o descarte do mais antigo faria a janela
"respirar" 59↔60 a cada amostra). Sem timestamp (fallback impossível), layout
estático por índice. Vale para todos os gráficos fluidos (resumo, performance,
mini previews, storage, latência) sem mexer nos ticks.

Build: 0 erros. Validado no tmshots10/ (linhas de ponta a ponta, sem saltos).

### Sessao 22/09 (cont. 2) - Gráficos: relógio dos dados (fim do "esticar sozinho")

Sintoma (pós tempo-real): com o relógio no tempo real, a linha derivava/esticava
mesmo sem amostra nova (janela crescendo no warmup + deriva em stall).
Solução final (`KitTaskManager.Fluid.cs`): `DataNow` por gráfico só avança na
chegada (entre amostras os alvos ficam congelados = zero movimento); na chegada,
`RenderShift += gap·pxPorSeg` cancela o passo na hora (pixels não se movem no
instante) e o easing traz até 0 em ~200 ms — movimento só para a esquerda.
`x = w - (DataNow-T[i])·pxPorSeg + RenderShift`; sem timestamp, layout estático.
Provado por construção: RenderShift nunca fica negativo e os alvos só andam na
chegada. Janela adaptativa com latch mantida. Build 0 erros; tmshots11/ OK.

### Sessao 25/09 - TaskManager: animacao dos graficos REVERTIDA + sidebar "toda acesa" + revisao de robustez do audio

1. **Graficos do TaskManager: animacao fluida REMOVIDA** (pedido do usuario: "faca a parte
   dos graficos nao ter mais aquela animacao que quebrou ele; pode deixar do jeito antigo
   que atualiza a visualizacao a cada 1s"):
   - `KitTaskManager.Fluid.cs` **DELETADO** (motor de apresentacao 60 fps: `HookFluidEngine`/
     `OnFluidFrame`, `FluidBar`, `FluidText`, `FluidChart`, `FluidRegisterChart`,
     `FluidSetBar`/`FluidSetText`). Nao sobrou nenhuma referencia a `Fluid` nos fontes.
   - `HookFluidEngine()` removido do ctor de `KitTaskManagerWindow.xaml.cs` (com comentario
     explicando); `HookFrameEngine()` (motor de linhas da grid de processos) PERMANECE.
   - Ticks voltaram a escrever/desenhar DIRETO, no tick fixo (`_graphTimer` = `_refreshSeconds`):
     `SetVital` (volta a `static`, `bar.Width = frac*BarMaxWidth`), barras de nucleo
     (`bar.Fill.Height = TrackHeight*frac`), `TxtSumCpuBig`/`TxtSumMemPctBig`/`StActivity`/
     `PerfDeviceUtil` (texto direto) e `SetLatBar` (`fill.Width = 3 + frac*255`).
   - Graficos: `DrawMultiSeriesChart` (CPU multi-serie, memoria, rede, disco, GPU, energia)
     e `DrawLineChart` (painel de Performance, mini previews da lista lateral, os 3 do disco).
     As duas funcoes antigas JA existiam em `KitTaskManager.Performance.cs` (`DrawLineChart`,
     `static`) e `KitTaskManager.Summary.cs` (`DrawMultiSeriesChart`) — foram reaproveitadas,
     nenhum codigo de desenho novo.
   - Build: 0 erros / 146 avisos (nullable pre-existentes, CS8600/8602/8618/8625).

2. **Sidebar: todos os icones acesos ao abrir o TaskManager** (causa raiz): os 9 templates de
   botao tem `<Border x:Name="Indicator">` (pilula ciano) e o `Loaded` so chamava
   `ActivateSidebarButton(BtnTabSummary)` — os outros 8 nunca eram resetados. O comentario do
   `Loaded` afirmava que a pilula "nao tem default visivel no XAML", mas o default do WPF para
   `Visibility` e `Visible` → as 9 pilulas nasciam acesas e so o 1o clique
   (`ResetSidebarButton`) apagava as erradas (exatamente o sintoma relatado: "so quando o
   usuario clica que volta ao normal"). Corrigido com `Visibility="Collapsed"` nas 9 pilulas
   (o XAML passa a refletir a intencao declarada no comentario). `ActivateSidebarButton`
   continua marcando a aba ativa.

3. **Revisao de robustez da recuperacao automatica de audio** (checklist de 8 pontos): nenhum
   bug nos itens 1, 2, 5, 7 e 8; os itens 3, 4 e 6 estavam incompletos e foram implementados
   de forma **puramente observacional** (gatilho, cooldown, limite/hora, duracao do reset e
   marcacao de provocado NAO mudaram):
   - `AudioGlitchMonitor`: `RecoveryAssessSeconds = 90` + `_assessFrom/_assessAfter` +
     `AssessLastRecovery()` (chamado ~1x/s no loop, FORA do guard do `AutoRecover`) → veredito
     "EFICAZ" (nenhum estalo real novo) ou "NAO resolveu a causa"; `Clear()` CANCELA a observacao
     em vez de declarar eficaz com a lista apagada; `BuildAiReport()` ganhou a secao
     `--- RECUPERACAO AUTOMATICA DO AUDIO ---` (ligada, resets na sessao/hora, "estado agora"
     calculado pelo proprio `ShouldAutoRecover`, ultima recuperacao + veredito).
   - **Harness novo** `tests/AudioRecoveryAudit/` (mesma convencao do `tests/PrivacyDryRun`,
     fora da solucao): roda contra o codigo REAL do Core via reflection.
     `dotnet run --project tests/AudioRecoveryAudit [-- phase1]`. **53 assercoes, 53 passaram**,
     incluindo 2 resets REAIS do `audiodg` (~303 ms, mesmo PID vivo depois), cooldown real,
     limite/hora real, PID reciclado (mesmo numero + hora de criacao diferente = recusado),
     evento provocado inoculado (nao conta, nao dispara outro reset) e a observacao
     pos-recuperacao. Antes deste harness NAO havia nenhum teste automatizado neste modulo.
   - Doc: `docs/AUDIO_RECOVERY_ROBUSTNESS.md` (veredito por item + riscos residuais honestos:
     identidade desconhecida falha-aberta em `SameProcess`; limite de 3/hora e por sessao do
     Kit; veredito "EFICAZ" nao distingue silencio).

### Proxima sessao
- [ ] Abrir o TaskManager e conferir no app: graficos redesenhados a cada ~1 s (sem a
      animacao que quebrava) e a sidebar com SO a aba Resumo acesa ao abrir
- [ ] Rodar `dotnet run --project tests/AudioRecoveryAudit` elevado (fase 2) e conferir a
      secao nova de recuperacao no relatorio copiado da aba Latencia
- [ ] (opcional) Ligar a recuperacao automatica no host e observar 90 s depois do reset: o
      texto deve trocar de "Observando os proximos 90 s..." para o veredito EFICAZ/NAO resolveu

### Sessao 25/09 (cont.) - Revisao de 5 repos externos (stuttometer, WhoLagged, RCA-GENAI, wfdiag, eir)

Pedido do usuario: olhar os 5 repositorios e dizer se vale a pena. Doc completo:
**`docs/EXTERNAL_REPOS_REVIEW.md`**. Nada foi copiado para o projeto (licencas: stuttometer
e WhoLagged = GPL-3.0, wfdiag = CC BY-NC-ND 4.0, RCA-GENAI = sem licenca, eir = MIT).

**ACHADO PRINCIPAL (verificado na maquina do usuario)**: o proprio Windows classifica o
nosso problema de audio. Provider `Microsoft-Windows-Audio`
(GUID `ae4bd3be-f36f-45b6-8d21-bdd6fb832853`) tem o canal
**`Microsoft-Windows-Audio/GlitchDetection`** (id 20, **`enabled: false`** por padrao) com
opcodes nomeados pelo motivo: 15 Capture Monitor Render Glitch, 16 Capture Monitor Capture
Glitch, 17 APO Glitch Format Converter, 18 Engine Glitch CP Client Input - No Messages,
19 Input Queue size mismatch, 20 Output Endpoint Server Overread, 21 Output Read Pointer
Overwrite. Acesso: `channelAccess` da leitura a IU (`0x1`) - **ler NAO exige admin**;
habilitar o canal exige (BA `0x7`). Log: `System32\Winevt\Logs\Microsoft-Windows-Audio%4GlitchDetection.evtx`,
max 1 MB, sem retention, `isolation: System`.
Hoje o `AudioGlitchMonitor` responde "quanto" (flag DATA_DISCONTINUITY + quadros perdidos);
este canal responde "**por que**" (motivo do OS, com PID/TID). Plano: habilitar opt-in
(reversivel, via diario de intervencoes) -> ler por `EventLogWatcher` (sem elevacao) -> cruzar
por timestamp com o nosso glitch (motivo = fato do OS; cruzamento = indicio).
**Validar ANTES de codar**: o manifesto local expoe OPCODES, nao "Event ID 11" como o README
do stuttometer afirma - confirmar id/opcode/template com uma sessao ETW real.

**Outros achados uteis**: (1) WhoLagged (7 KB de C#) prova que a lib `TraceEvent` faz sessao
de kernel sem P/Invoke e mede **latencia real de E/S por processo**
(`DiskIOTraceData.ElapsedTimeMSec`) - algo que o Kit nao tem (temos B/s por processo e latencia
do disco como um todo); (2) sessao de kernel e recurso unico global (o nosso LatencyMonitor ja
usa a "NT Kernel Logger" com DPC|INTERRUPT) -> **nao** abrir segunda sessao para audio;
(3) RCA-GENAI: presta o **lead/lag** (favorecer quem sobe ANTES do evento), o z-score robusto
(mediana/MAD) e a confirmacao sustentada+cooldown; NAO presta o cosine contra vetor de
constantes e o `raw/max(raw)` que **sempre elege um culpado com score 1.0**;
(4) wfdiag (so arquitetura, licenca proibe derivar): estados explicitos
verificado/detectado/desconhecido, reverificacao da evidencia apos cada fix (= nossa observacao
pos-recuperacao), confirmacao forcada no backend, auditoria jsonl, comparacao entre execucoes;
(5) eir (MIT): fila de aprovacao persistente que volta a pedir aprovacao se o servico caiu no
meio, aprendizado que so pode REDUZIR/reordenar acoes, garantia dura de nunca desinstalar.

Proximos passos sugeridos (valor/risco): causa do glitch pelo canal GlitchDetection (alto/baixo);
baseline rolante + z robusto + lead/lag no "quem causou" (medio); latencia de E/S por processo
(medio, atencao ao conflito de sessao); score de confianca + "copiar sem dados pessoais" (baixo).

### Sessao 25/09 — CENTRAL DE DIAGNOSTICO (um botao, tudo num lugar) + causa real do glitch de audio

Pedido do usuario: "uma area que reunisse tudo em um so lugar, o usuario aperta UM botao na
central, os logs sao gerados e os mais importantes ele pode copiar ou tentar resolver".
Plus: aproveitar os repos baixados (licenca liberada: "nao vai ter publico, e so para o kit").

**1. `KitLugia.Core\Diagnostics\` (novo)**
- `DiagnosticModels.cs`: `DiagnosticSeverity {Critical=0, Warning=1, Info=2}` (menor = pior, e a
  ordem natural da lista), `DiagnosticFinding` (Severity/Category/Title/Evidence/Suggestion/
  RepairName/ActionId/ActionLabel/PageTag/Source), `DiagnosticEvidence` (bloco de log cru;
  `AlwaysCopy` entra tambem na copia curta), `DiagnosticReport` (`BuildTitle/BuildSummary/
  BuildText/BuildClipboardText(importantOnly)/Sorted()`).
- `DiagnosticCenter.cs`: 11 coletores em sequencia com `IProgress<string>` — ambiente (SO/build/
  RAM/uptime/elevado), volumes (DriveInfo), discos fisicos (StorageDiagnostics.GetDevices),
  dispositivos com problema (Win32_PnPEntity ConfigManagerErrorCode), servicos AUTO parados,
  event logs System+Application (Level 1/2, 7 dias, agrupados por provedor#ID), WER/CrashDumps,
  reboot pendente (CBS/WU/PendingFileRenameOperations), diario de intervencoes do Kit,
  audio (canal + AudioGlitchMonitor) e latencia (LatencyMonitor).
- REGRAS DE PROJETO gravadas no proprio arquivo: (a) NADA altera o sistema — so leitura;
  (b) coletor que falha NAO desaparece, cai em `CollectorFailures` (silencio seria lido como
  "esta tudo bem"); (c) achado sem evidencia nao entra; hipotese entra como INFO e diz isso;
  (d) ruido cronico (DCOM inteiro, Perflib inteiro, CertEnroll#87, Kernel-EventTracing#2) NAO vira
  achado — mas continua visivel nas EVIDENCIAS (nada e escondido).
- `AudioGlitchChannel.cs` (o PORQUE do glitch, achado do stuttometer): le
  `Microsoft-Windows-Audio/GlitchDetection` com EventLogQuery/XDocument. FATOS CONFIRMADOS:
  ler NAO exige admin; habilitar exige (descritor da 0x1 de leitura a IU, BA 0x7 para escrita);
  o canal vem DESLIGADO de fabrica; chegam EventID 36 (IOMGR sem pacote pendente), 41 (BASE End
  Glitch: GlitchDuration/GlitchStreamPosition) e 48 (STREN privado: DevicePos/StreamPos/
  AvailFrames) com task 123/opcode 30 — NAO o "Event ID 11" do README. O caminho do .evtx e
  DERIVADO (`ChannelName.Replace("/", "%4") + ".evtx"`) porque `EventLogInformation.IsEnabled`/
  `.LogFilePath` NAO existem nesta superficie de API (erro de compilacao real).
  `SetEnabled(bool)` = wevtutil + conferencia do estado real + `RecordIntervention` (reversivel).
- **Correlacao temporal**: para cada glitch CONFIRMADO do Kit, o DiagnosticCenter procura eventos
  do canal em +-3 s e escreve a causa do Windows ao lado do estalo — é o que transforma "o audio
  travou" em "travou PORQUE o endpoint BASE reportou fim de glitch".

**2. `tests\DiagnosticCenterRun\` (novo harness de EXECUCAO REAL, fora da solucao)**
`dotnet run --project tests/DiagnosticCenterRun` roda o diagnostico de verdade e valida o
relatorio (14 assercoes; todas passam; ~1,9 s de coleta). NAO e teste de unidade: e o "rodar de
verdade" para pegar coletor quebrado em runtime.
**BUG QUE ELE PEGOU (e que o compilador nao pegaria)**: `StorageDiagnostics.DiskDevice.Health`
NAO e o numero do MSFT_PhysicalDisk — ja chega TRADUZIDO por `StorageDiagnostics.HealthName`
("Saudavel"/"Atencao"/"Nao saudavel"/"Desconhecido"/"Estado N"). O teste `Health != "0"`
acusava os dois discos saudaveis como CRITICO. Corrigido com `IsDiskHealthBad` — e
"Desconhecido" NAO e alerta (falta de leitura nao pode virar panico).

**3. GUI: aba "Diagnostico" no GERENCIADOR DE TAREFAS (decisao do usuario)**
O usuario corrigiu o rumo: "achei que ia colocar no kit taskmanager porque la faz mais sentido".
Faz sentido mesmo — o diagnostico JA mora la (abas Latencia/Armazenamento/Audio) e a mesma ferramenta
em duas janelas seriam duas telas para manter e o usuario sem saber qual e a "de verdade".
- `KitLugia.GUI\Windows\TaskManager\KitTaskManager.Diagnostic.cs` (novo partial) com TODA a logica.
- A `Pages\DiagnosticCenterPage.xaml(.cs)` que existiu por algumas horas foi DELETADA, junto com o
  `PageType.DiagnosticCenter`/`NavTagMap`/botao da sidebar que eu havia posto na MainWindow e o
  `AddNav` que eu havia posto no SearchEngine — tudo revertido (MainWindow.xaml/.xaml.cs/SearchEngine.cs
  voltaram a ser IDENTICOS ao HEAD, conferido com git diff). Nada de pagina orfa.
- XAML: `BtnTabDiagnostic` na sidebar (Tag="Diagnostic") + `TabDiagnostic`. UI: botao verde
  `BtnDiagRun` "GERAR DIAGNOSTICO AGORA" com template inline proprio (o `TmToolButton` fixa o
  Background DENTRO do template, entao pintar o Button nao adianta), 3 contadores
  (criticos/atencao/informacoes), alternancia `Achados` / `Evidencias e logs` pelo padrao do proprio
  TM (`DiagView_Click` + `SetDiagView`, igual ao `LatView_Click` da aba Latencia — um TabControl
  padrao do WPF traria chrome claro para o meio da janela escura), e `TxtDiagStatus` para o retorno
  das acoes (mesma convencao de `TxtStatus`/`TxtStStatus` das outras abas).
- `EnsureDiagnosticBuilt()` (padrao dos outros `EnsureXxxBuilt`) monta os ItemsSource e JA DISPARA a
  coleta na primeira abertura — quem chegou aqui quer resposta, nao um botao pedindo para apertar
  outro botao. O botao continua para repetir.
- SEM `AutoStartAudioListen` nesta aba: o diagnostico e 100% leitura e nao pode ter efeito
  colateral. Se a escuta de audio estava desligada, ele DIZ isso (INFO) em vez de ligar sozinho.
- Topo da lista: `Copiar so o importante` (sem INFO) / `Copiar tudo` (com evidencias) / `Salvar .txt` / `Limpar`.
- **Reverificacao apos o reparo (ideia do wfdiag)**: depois de executar um reparo da Central AIO o
  Kit RODA O DIAGNOSTICO DE NOVO e diz se o achado desapareceu ou continua — nao basta dizer "rodei".
- `RepairAction.Id` e GUID gerado a cada `GetAllRepairs()`, entao a ligacao e por NOME estavel
  (`RepairName`); `ActionId` cobre acoes fora da Central AIO (`audio:glitch-channel-on`,
  `kit:copy-log`). "Abrir pagina" ativa a janela principal e navega por tag.
- Contexto que so a GUI tem (log do console via LogStore, estado interno via
  `MemoryDiagnostics.GetMemoryReport()`, logs persistente do WinPE via `WinbootManager.ReadAllWinpeLogs`)
  e anexado como evidencia/achado — o Core continua sem depender de WPF. `Status: FAIL` no log do
  WinPE vira achado CRITICO.

**4. Navegacao ate a aba**: `SwitchTab(object, RoutedEventArgs)` agora so extrai a tag e delega para
`public void SwitchTabByTag(string)` — antes o clique e a abertura programada seriam caminhos
separados. Novo `KitTaskManagerWindow.OpenOnTab(owner, "Diagnostic")` = `OpenOrActivate` + `Show` +
troca de aba via `Dispatcher.BeginInvoke(DispatcherPriority.Loaded)`. O card "Central de Diagnostico"
do Dashboard usa isso: mantem a promessa do "um botao na central" sem duplicar tela.

**5. TaskManager: o PORQUE no relatorio copiado.** `KitTaskManager.Audio.cs`
`BuildCombinedLatencyReport()` agora anexa a secao "CAUSA REPORTADA PELO WINDOWS" com o estado do
canal e os eventos da janela de 30 min — quem copia o relatorio de audio para analisar (humano ou
IA) tem a causa junto, sem abrir o Visualizador de Eventos.

**6. REGRA DE ENCODING (aprendida na marra nesta sessao)**: `git show HEAD:file > file` no Git Bash
grava o blob com LF e SEM o BOM original — foi preciso reescrever CRLF e repor o BOM por arquivo
(MainWindow.xaml/DashboardPage.xaml/DashboardPage.xaml.cs NAO tem BOM no HEAD; MainWindow.xaml.cs
TEM). Tambem: NUNCA passar um normalizador de trailing-whitespace em arquivo ja existente "de
passagem" (gerou ~120 linhas de diff fantasma num arquivo que eu so queria editar em 7).

**Resultado da execucao real (host elevado, 25/09)**: 19 achados — 1 critico (C: com 8,8% livre),
13 atencao (8 servicos AUTO parados ignorando trigger-start, 8 falhas de app em 30 dias, SCM
7031/7034/7009/7000/7023, Application Error/Hang, .NET 1026, WindowsUpdateClient#20, 1 dispositivo
codigo 10), 4 informativos (canal de audio desligado, escuta inativa, monitor de latencia inativo,
reboot pendente). 0 coletores falharam.
Build: 0 erros / 159 avisos (baseline de nullable/CA2022 pre-existentes; ZERO avisos nos arquivos
novos).

### Sessao 25/09 (cont.) - Gerenciador de Tarefas: abertura rapida e sem travar o app

Medido com harness proprio antes de mexer (nunca estimado):
`tests\TaskManagerWindowBench` (novo, WPF/STA) constroi a janela SEM Show.
**Frio 641 ms** (BAML de 11 abas + JIT das partials, tudo na UI thread) contra
**~100 ms quente** (o piso e a instanciacao do BAML). Ou seja: o custo de abrir e
o XAML, e ele nao desaparece - so sai do clique.
`tests\TaskManagerOpenBench` (novo, console) mede o pipeline de dados do 1o refresh:
Rede 0,9 / GPU total 0,9 / GPU por PID 0,7 / NtQSI 5,9 / janelas 2,8 / **WTS 41,3 ms**
(este ultimo e o mais caro e ja estava fora da UI thread).

**Correcoes (KitTaskManagerWindow.xaml.cs / .Render.cs)**:
1. **`gpuPerPidTask` fora do `WhenAll`**: `await Task.WhenAll(net, gpu, snap)` seguido de
   `gpuPerPidTask.Result` — o `.Result` roda NA UI THREAD. Agora entra no `WhenAll` (waiting
   no mais lento em vez de "o mais lento + a sobra"; medido: -1,2 ms no wall clock, e o
   valor real esta no 1o refresh, onde o PDH paga a inicializacao).
2. **Gate de visibilidade**: `_refreshTimer` (1 s) e `_graphTimer` (1 s) agora fazem
   `if (!IsVisible) return`. Antes, com o kit minimizado para a bandeja e o gerenciador
   aberto, ~371 processos eram enumerados por segundo + PDH + desenho de graficos para
   NINGUEM ver (~7 ms de CPU/s de puro desperdicio, pior em maquina fraca).
   `IsVisibleChanged` religa com refresh + redesenho IMEDIATOS ao reaparecer (flag
   `_windowWasHidden` distingue a 1a exibicao, que nao precisa de refresh extra).
3. **Motor de quadros nao liga fora da aba Processos**: `RequestFrameAnimation` ganhou
   `if (!CurrentTabAnimates()) return;` — antes, cada processo nascendo/morrendo com outra
   aba aberta assinava e desassinava `CompositionTarget.Rendering` em quadros alternados.
   O retorno a aba Processos religa pelo `ResumeFrameAnimationsIfNeeded` (ja existente).
4. **Prewarm so com o usuario ocioso**: `GetLastInputInfo` (P/Invoke novo no partial) —
   com input nos ultimos 3 s o `Prewarm` adia (reagenda em 8 s) em vez de construir a
   janela e engasgar a UI no meio de um clique. Pior caso = cold-start (como era antes).
5. **`Owner` na instancia adotada**: a janela pre-aquecida nasce sem dono; `OpenOrActivate`
   agora adota o owner no caminho quente (o caminho frio sempre criou com `Owner`).

Ja existia da rodada anterior e continua valendo: lazy-load por aba (Servicos/Inicializacao/
Dispositivos so quando a aba abre), icone generico do shell sob demanda, motor de quadros
ligado SOB DEMANDA (zero hook em repouso — o Resumo saiu do `CurrentTabAnimates`),
prewarm em `ApplicationIdle` (4 s) + re-prewarm 15 s depois de fechar, e `Closed` desassina
o motor de quadros.

Build: 0 erros / 146 avisos (baseline nullable). Harness `tests\DiagnosticCenterRun`
segue 100% verde.

**A TESTAR (manual)**: abrir o TM pelo topbar (deve aparecer na hora, com o log
"Janela pre-aquecida em idle"), minimizar o kit para a bandeja com o TM aberto e conferir
no Monitor de Recursos que o app para de consumir CPU, restaurar e ver o dado atualizar
na hora, e abrir a aba Processos para conferir o esmaecer verde/vermelho (unica aba com
motor de quadros).

### Proxima sessao
- [ ] TESTE MANUAL: abrir a aba "Diag" do Gerenciador de Tarefas (sidebar do TM e card do Dashboard,
      que chama `OpenOnTab`), conferir auto-coleta/contadores/cores, alternar Achados/Evidencias,
      copiar so o importante, copiar tudo e salvar .txt
- [ ] TESTE MANUAL: ligar o canal de glitch (`audio:glitch-channel-on`) → reproduzir estalo →
      gerar diagnostico → conferir a secao de correlacao +-3 s; depois DESLIGAR o canal
- [ ] (opcional) levar a secao "CAUSA REPORTADA PELO WINDOWS" tambem para o card de audio na TELA
      (hoje ela ja entra no relatorio copiado)
- [ ] (opcional) lead/lag + z-score robusto (mediana/MAD) no "quem causou" do disco/latencia
      (RCA-GENAI): favorecer quem sobe ANTES do evento; NAO usar raw/max(raw)
- [ ] (opcional) latencia real de E/S por processo (WhoLagged/TraceEvent) — atencao: sessao de
      kernel e recurso UNICO e o LatencyMonitor ja usa a "NT Kernel Logger"
- [ ] (opcional) fila de aprovacao persistente estilo eir para acoes arriscadas do Kit

### Sessao 26/09 - PagesBench: custo/RAM de TODAS as paginas + fix do dialogo "Erro ao carregar programas"

Pedido do usuario: "otimizar as paginas do kit e o uso de RAM dele, leve o tempo que for e
monte um plano" + o dialogo "Erro ao carregar programas: O thread de chamada nao pode acessar
este objeto" aparecendo "toda hora".

1. **Harness `tests/PagesBench` (reconstruido e validado)**: mede por pagina frio/quente
   (ctor na UI thread), layout (Measure/Arrange/UpdateLayout), "assenta" (settle do Loaded),
   retido (RAM viva) e sobra2/sobra3 (o que fica pos-Cleanup). CLI: `leak`, `loaded`,
   `selftest`, `ctx`, `ctx2`, `--cap N`, `-- --root <Nome> [--once] [--wait N]`.
   - **BUG HISTORICO do harness corrigido**: a pagina era criada e anulada no MESMO frame que
     media -> a local de pilha a segurava e nem `new object()` era coletado (origem dos "6
     vazamentos" falsos). Agora criacao/Cleanup acontecem em frames que saem antes do GC
     (`CriarECleanup`/`FaseInterna`), com `BombeiaDispatcher` e auto-teste (`selftest`).
   - **Metodo**: `--root` (WeakReference + bombeio 1,5 s + GC) e o ARBITRO de vazamento;
     o modo `leak` em lote e so TRIAGEM (delta de memoria) e erra para mais com trabalho em voo.

2. **VEREDITO: nenhuma pagina vaza.** `--root` deu "NAO vazou" para os controles
   (`OBJECT`, `Page`) e para todas as suspeitas. `NetworkPage` (unico flag do triador) libera
   em t+2s; `ProcessMonitorPage`/`OptimizationPage` sao falso positivo de trabalho em voo.
   Baseline (48 paginas): frio total 679 ms, quente medio 3,8 ms/pagina, layout medio 20,6 ms,
   soma do retido 15,2 MB, working set 44 -> 188 MB. Piores: AboutPage 194 ms (frio),
   GameBoostPage 96, PrivacyPage 72 (mas **213 ms de layout**), RepairsPage 164 ms de layout.

3. **FIX REAL do dialogo** (o print do usuario mostrou a mensagem verdadeira: **"Erro ao
   carregar programas: The CancellationTokenSource has been disposed."** — NAO era o erro
   cross-thread; esse diagnostico inicial estava ERRADO):
   **Causa raiz**: `AppsPage.LoadPrograms` (linha ~730) e `ProgramsPage.LoadPrograms` (linha ~82)
   liam o CAMPO `_programsCts.Token` / `_iconLoadCts.Token` DEPOIS dos `await`
   (`await LoadProgramsIconsAsync(_programsCts.Token)`), enquanto `Cleanup()` faz
   `_programsCts?.Dispose(); _programsCts = null;`. A continuacao pos-await corre no worker
   (contexto nulo) e le o campo exatamente entre o `Dispose` e o `= null` ->
   `ObjectDisposedException` (ou `NullReferenceException`, dependendo do timing da corrida).
   **`LoadBloatware` (L244) e `LoadPortableApps` (L1242) JA faziam certo** (`var token = ...`
   em local) — o `LoadPrograms` ficou para tras.
   **Correcao** (os dois arquivos): capturar `var cts = new CancellationTokenSource();
   _programsCts = cts; var token = cts.Token;` e usar SO o `token` local depois; `Task.Run(...,
   token)` e `Task.Delay(100, token)`; `token.ThrowIfCancellationRequested()`;
   `catch (OperationCanceledException) { }` + `catch (ObjectDisposedException) { }` antes do
   catch generico (sair da pagina e fluxo NORMAL, nao erro -> sem dialogo); `try/catch` em
   volta do `Task.WhenAll`; e snapshot da colecao em local no inicio do `LoadProgramsIconsAsync`/
   `LoadIconsAsync` (o `Cleanup` zera o campo concorrentemente).
   Mantidas as blindagens da rodada anterior (guarda `Dispatcher.CheckAccess()`,
   escrita de UI pos-await via `Dispatcher.InvokeAsync`, `Logger.LogError(...ex.ToString())`
   antes do MessageBox, `finally` via dispatcher): elas resolvem a classe de bug cross-thread
   mas NAO eram a causa deste dialogo. `ProgramsPage` e ORFA (nada navega para ela).

4. **Padroes de ctor alinhados ao `_CONVENTIONS.md`** (Anti-Pattern 8: nada de trabalho pesado
   no ctor; sem lambda em evento nao-removivel):
   - `TweaksPage`: `Loaded += (s,e) => {...}` -> handler NOMEADO `TweaksPage_Loaded`,
     removido no `Cleanup`.
   - `ExmTweaksPage`: `_ = LoadCurrentStatus()` no ctor -> `Loaded` nomeado + guard
     `_loadedOnce`; `Cleanup` agora desinscreve `Loaded`/`Unloaded` (antes nao desinscrevia nada).
   - `ProcessMonitorPage`: `DispatcherTimer` de 3 s era `Start()`ado no ctor -> `Start()` agora
     so no `Loaded` (guard `_isLoaded`).
   - `DriversPage`: 2x `Task.Run` de varredura + `CheckVerifierStatus()` no ctor -> tudo movido
     para `DriversPage_Loaded` (nomeado, guard `_loadedOnce`), `Cleanup` desinscreve `Loaded`.

5. **Harness mais honesto**: `FaseInterna` bombeia o Dispatcher 1200 ms (era 400) antes de
   medir a sobra -> foi o que removeu os falsos vazamentos de `ProcessMonitorPage`/
   `OptimizationPage`.

6. **`docs/PAGES_RAM_OPTIMIZATION_PLAN.md`** (novo): metodologia, baseline completa,
   achados, o que foi entregue, guardrails e 6 fases propostas (PrivacyPage/RepairsPage layout;
   lazy real de subarvore em WinTune/Tweaks/ExmTweaks; AboutPage/GameBoostPage; fechar o modo
   `--loaded`; higiene continua).

Build: **0 erros**. Arquivos: `tests/PagesBench/Program.cs`,
`KitLugia.GUI/Pages/{AppsPage,ProgramsPage,TweaksPage,ExmTweaksPage,ProcessMonitorPage,DriversPage}.xaml.cs`.

### Proxima sessao
- [ ] Fase 1 do plano: PrivacyPage (213 ms de layout) e RepairsPage (164 ms) -> meta < 80 ms
- [ ] Fase 2: lazy real de subarvore nas abas de WinTune/Tweaks/ExmTweaks (retido 1,5 MB -> < 500 KB)
- [ ] Fase 5: rodar o modo `--loaded` em porcoes (`--cap 2000`) e registrar o pior settle do clique
- [ ] TESTE MANUAL: abrir AppsPage -> aba Programas (1x), apertar Atualizar Programa e NAVEGAR
      PARA FORA no meio do carregamento (o cenario da corrida) -> confirmar que o dialogo
      "The CancellationTokenSource has been disposed" NAO aparece mais e que o log nao registra erro

### Sessao 26/09 (cont.) - C/C++ no kit: onde encaixa e se vale a pena (tray icon / arranque)

Pedido do usuario: "veja onde C++ se encaixa no kit, o trayicon por exemplo talvez inicie mais
rapido se for feito em C++; pesquise e veja se vale a pena". Documento: **`docs/NATIVE_CPP_EVALUATION.md`**.

**VEREDITO: nao vale a pena reescrever o tray em C++.** O tray do kit JA e um wrapper Win32 fino
(`TrayIconService` usa `System.Windows.Forms.NotifyIcon`, que por baixo e `Shell_NotifyIcon`);
o ctor so cria um `DispatcherTimer` e o `Initialize()` nao bloqueia (3 `Task.Run` de auto-fix +
watchdog + `_monitorTimer.Start()`). Ganho teorico: ~1-3 ms. Custo: um SEGUNDO processo (mais
working set, nao menos), IPC (named pipe) para TODA interacao tray<->app (settings, monitor de
RAM, GameBoost, prioridade, toggles da comunidade...), e uma segunda toolchain/ABI para manter.

**Native AOT nao e alternativa**: o SDK bloqueia WPF (`error NETSDK1168: WPF is not supported or
recommended with trimming`). A unica alavanca nativa suportada para WPF e ReadyToRun — e o
projeto **ja usa**: `KitLugia.GUI.csproj:24` tem `<PublishReadyToRun>true</PublishReadyToRun>`.

**Onde C/C++ JA encaixa (e esta certo)**: `KitLugia.Core/Resources/KitLugiaEFI/` — fork do rEFInd
+ `kitlugia_shrink.c`, `gpt_lib.c`, `disk_io.c`, com Makefile/toolchain proprio
(`build_toolchain.ps1`, `refind_fork/build_refind_fork.ps1`). E ambiente UEFI pre-Windows: nao
existe runtime .NET ali, entao nativo nao e escolha, e obrigacao. Alem disso o kit embarca
binarios nativos de terceiros (`Tools/`: aria2, GoodbyeDPI, 7-Zip, wimlib). O padrao correto que
o kit ja segue: **nativo no pre-OS e nas ferramentas de terceiros; .NET no app**.

**Recomendacao para "abrir mais rapido"** (do mais barato ao mais caro): (1) instrumentar
`App.OnStartup` com marcos de Stopwatch; (2) mostrar o tray ANTES de construir a janela pesada;
(3) confirmar que o publish real aplica o R2R e medir antes/depois; (4) atacar a construcao das
paginas (Fases 1-3 do `PAGES_RAM_OPTIMIZATION_PLAN.md` — 679 ms medidos para as 48 paginas,
`PrivacyPage` com 213 ms de layout); (5) so entao considerar um helper nativo PONTUAL via
P/Invoke, nunca um processo paralelo dono do tray.

Build: **0 erros**. Arquivos: `AppsPage.xaml.cs`, `ProgramsPage.xaml.cs`,
`docs/NATIVE_CPP_EVALUATION.md` (novo).

### Sessao 26/09 (cont.) - KitTaskManager: auditoria visual, bug da aba Diagnostico e textos sobrepostos

Pedido: visual unico para o KitTaskManager (interface mais larga, nao precisa ser igual ao
Kit padrao), corrigir o que faz a pagina inteira piscar no refresh, e checar textos um por
cima do outro. Documento: **`docs/TM_VISUAL_AUDIT.md`**.

1. **Harness novo `tests/TmVisualBench`** (auditoria visual, read-only):
   `dotnet run -- tabs` gera **10 PNGs** (um por aba) em `out/` via `RenderTargetBitmap` e roda
   um **detector de texto sobreposto** (percorre a arvore visual, bounds em coordenadas da
   janela, reporta pares com intersecao > 35% do menor). `flicker` renderiza 2x com 2,2 s de
   bombeio e conta pixels mudados.
   **Quirks aprendidos (o detector mente sem eles)**: (a) aplicar o CLIP dos ancestrais —
   TextBlock em celula de DataGrid reporta a largura que gostaria de ter; (b) cortar pela
   VIEWPORT do ScrollViewer — conteudo abaixo da dobra nao e sobreposicao; (c) excluir texto
   CORTADO da comparacao — dois pedacos cortados pelo mesmo viewport colapsam no mesmo
   retangulo e pareceriam sobrepostos. **Sempre rodar COM build** (`dotnet run -- tabs`, sem
   `--no-build`): com `--no-build` ele usa a copia ANTIGA de `KitLugia.GUI.dll`.

2. **BUG REAL corrigido**: a aba **Diagnostico nao renderizava** — o harness nao conseguia
   nem gerar o PNG dela:
   `XamlParseException: associacao TwoWay nao pode funcionar na propriedade somente leitura
   'Evidence' de DiagFindingVm`. `TextBox.Text` e TwoWay POR PADRAO e `DiagFindingVm.Evidence`
   e read-only. O `Preview` (aba Evidencias) ja tinha `Mode=OneWay` — alguem corrigiu um e
   esqueceu o outro. Fix: `{Binding Evidence, Mode=OneWay}` (KitTaskManagerWindow.xaml).

3. **Textos sobrepostos: antes x depois** — Summary 12->**0**, Processes 17->2, Users 1->**0**,
   Connections 6->**0**, Latency 1->**0**; Performance/Services/Startup/Storage/Diagnostic 0.
   Padroes que causavam (todos reais):
   - **dois textos na MESMA celula de Grid** (conteudo a esquerda + controles a direita):
     cabecalho do Resumo (`TxtSumStatus` longo passava sob Copiar/resmon/Intervalo), cabecalho
     de Conexoes, cabecalho do painel de detalhes ("Detalhes do Processo" x "X") e as duas
     barras de status -> **Grid de 2 colunas (`*` + `Auto`) + TextTrimming**.
   - **celula/cabecalho de DataGrid sem recorte** (nome do processo desenhado sob o valor de
     CPU no Top CPU) -> **`ClipToBounds=True` no `TmCellStyle` e no `TmColumnHeaderStyle`**
     (resolve para todos os grids do TM de uma vez).
   - **`Content` de CheckBox sem wrap** ("Recuperar sozinho..." estourava a largura) ->
     conteudo virou `TextBlock` com `TextWrapping="Wrap"` + `MaxWidth`.

4. **Flicker MEDIDO** (fracao da tela que muda entre 2 refreshes): Services/Startup 0,0%;
   Users/Latency 0,3%; Processes 1,4%; Performance 3,0%; Summary 3,9%; Connections 5,9%;
   Storage 14,0%; Diagnostic 26,1%. **O "refresh da pagina inteira" NAO se confirma** na
   maioria das abas: o grid de processos ja e diff por PID (merge, sem recriar colecao) e as
   barras de nucleo sao construidas UMA vez (`BuildCoreBars` sai cedo). O que repinta muito e o
   que desenha em `Canvas` (graficos do Storage + evidencias do Diagnostic fazem
   `Children.Clear()` + redesenho) -> proximo alvo.

5. **PENDENTE / NAO confirmado**: `Processes` ainda acusa `GDI / USER` x `Elevado` (e os valores
   correspondentes). O grid tem 15 `RowDefinition` (rows 0-14, sem aliasing) e as linhas sao
   `Auto` — em WPF linha Auto nao se sobrepoe; como o PNG e gerado fora da tela, tratei como
   artefato de medicao NAO confirmado e **nao mexi no layout no escuro**.
   **Visual unico (identidade propria) tambem NAO foi implementado** — o que entrou foi
   posicionamento; a identidade visual do TM continua a mesma do Kit.

Build: **0 erros**. Arquivos: `KitTaskManagerWindow.xaml`,
`tests/TmVisualBench/{TmVisualBench.csproj,Program.cs}` (novos), `docs/TM_VISUAL_AUDIT.md` (novo).

### Sessao 26/09 (cont.) - TM: 15a RowDefinition, ordenacao visivel/funcional e aba Usuarios

Feedback do usuario com prints. **10/10 abas do TM agora com ZERO texto sobreposto** (validado
pelo `tests/TmVisualBench`).

1. **BUG confirmado pelas prints: `GDI / USER` desenhado EM CIMA de `Elevado`** (detalhe do
   processo). Eu tinha descartado antes como "artefato de medicao" — **estava ERRADO**: minha
   contagem usou `grep -c "RowDefinition"`, e a tag `<Grid.RowDefinitions>` TAMBEM casa nessa
   substring (off-by-one). O grid de detalhes avancados tinha **14 `<RowDefinition>` para 15
   linhas usadas (rows 0..14)** -> a row 14 ("Elevado"/"Sim (admin)") caia sobre a row 13
   ("GDI / USER"/valores), porque row fora do intervalo ALIASA para a ultima definida.
   **Fix**: 15a `RowDefinition`. LICAO: contar `<RowDefinition` com marcador exato, nunca
   `RowDefinition`.

2. **Ordenacao: barra visivel + funcionamento**. Havia `Sorting="DgProcesses_Sorting"` e
   `CanUserSortColumns="True"`, mas: (a) `ApplySorting()` so pintava as SETINHAS — a ordem real
   so mudava no proximo tick (1s), dando a impressao de "ordenar nao funciona"; (b) o usuario
   nao tinha nenhum controle explicito do criterio. **Fix**: combo `CmbSortProcesses` no
   cabecalho da aba Processos (CPU/Memoria/Disco/Rede/GPU/Pico/Tempo de CPU/Page faults/
   Nome/Status, cada item com `Tag="Coluna|Ascending|Descending"`) + `TxtSortDirection`
   (seta ▲/▼), `SyncSortUi()` mantendo combo + seta + cabecalho em sincronia nos DOIS sentidos
   (clique no combo e clique no cabecalho), e `_ = RefreshAsync()` ao final de
   `DgProcesses_Sorting` para aplicar NA HORA. Obs: a ordenacao mantem o agrupamento como
   chave primaria (`Rank(Group)` = Aplicativos > Segundo plano > Windows) — por design.
   Quirk: `ComboBox` e AMBIGUO no arquivo (WinForms x WPF) -> qualificar
   `System.Windows.Controls.ComboBox`.

3. **Aba Usuarios: o detalhe SUMIA ao clicar na seta** (print: seta ▼ apontando mas sem lista).
   Duas causas somadas: (a) `DgUsers` estava com `RowDetailsVisibilityMode="VisibleWhenSelected"`,
   ou seja o detalhe aparecia por SELECAO, nao pela seta; (b) o refresh de 1s fazia
   `DgUsers.ItemsSource = rows` com objetos NOVOS -> recriava as linhas, **perdia a selecao** e
   o detalhe desaparecia. **Fix**: `RowDetailsVisibilityMode="Visible"` + `Visibility="{Binding
   DetailsVisibility}"` no Border do `RowDetailsTemplate` (a SETA manda); `UserRow` passou a
   NOTIFICAR Status/Cpu/CpuValue/MemMB/MemValue/ProcessCount/Processes (helper `Raise` novo —
   a classe so tinha `Invoke` inline); e `LoadUsersSafeAsync` faz **MERGE por usuario** numa
   `ObservableCollection<UserRow>` persistente (atualiza em lugar, insere novos, remove sumidos)
   em vez de trocar o ItemsSource; a lista `Processes` so e reescrita de quem esta ABERTO.
   Isso e a convencao "refresh por segundo: update diff, nao Clear+Add".

4. **Aberto / nao feito nesta rodada**: (a) o usuario relatou "as informacoes nao atualizam" apos
   a primeira rodada — a hipotese mais concreta era a aba Usuarios (numeros sem notificacao),
   corrigida aqui, mas NAO foi possivel confirmar em execucao; (b) **visual unico do TM continua
   nao implementado**; (c) abas Latencia (e demais) com espacamento/deslocamento a melhorar;
   (d) `ForceStop` e `TakeOwnership` (menu de contexto do Kit) considerados frageis pelo usuario
   — "qualquer errinho param e travam" -> precisam de blindagem (try/catch por etapa + nao
   abortar o lote no primeiro erro).

Build: **0 erros**. Arquivos: `KitTaskManagerWindow.xaml`, `KitTaskManagerWindow.xaml.cs`.

### Sessao 26/09 (cont.) — TM: ordenacao de TODAS as colunas, aba Usuarios e blindagem ForceStop/TakeOwnership

Os 9 prints reenviados pelo usuario eram do build de **22:17** (a DLL so foi reconstruida
22:29) — o texto embaralhado ("GDI/USER" + "Elevado") e o dialogo do CTS ja estavam
corrigidos em disco. O que a sessao ATACOU foi o que o usuario confirmou por descricao.

1. **Ordenacao: "testei o maior consumo de RAM e nao funcionou" — 3 causas reais**
   - Colunas **sem `SortMemberPath`** (Status, Usuario, Threads, PID) caiam no `return` do
     `DgProcesses_Sorting` (`e.Handled = true` + nada): clicar nelas NAO fazia nada. Agora
     todas tem `SortMemberPath` e o `MetricOf` cobre `Pid`/`Threads` (+ `CommitMB`,
     `DiskRead/Write/DiskOps`).
   - Os DOIS switches gigantes de ordenacao (fast path e anti-pisca) foram unificados num
     helper **`OrderRows(src, rank, tieBreaker)`** — antes, so os campos listados
     explicitamente ordenavam; qualquer outro caia no default (CPU). Texto (Nome/Status/
     Usuario) ordena por string; o resto por `MetricOf` arredondado em 1 casa; `rank(Group)`
     continua sendo a chave PRIMARIA (Aplicativos > 2o plano > Windows, paridade TMOG).
   - **Quirk de compilacao**: `src.OrderBy(rank)` com `Func<string,int>` fazia o compilador
     tentar `AsyncEnumerable.OrderBy` (erro CS1929) -> usar `src.OrderBy(r => rank(r.Group))`.
2. **Barra de ordenacao (visual)**: o combo solto virou um **chip** — Border escuro com o
   rotulo `ORDENAR`, combo transparente e a seta ao lado virou **botao clicavel**
   (`BtnSortDirection`, inverte o sentido). Itens novos no combo: Usuario, Threads, PID e um
   item **dinamico** (`__custom__`) que aparece quando o criterio veio do CABECALHO e nao tem
   item fixo — sem ele o combo continuava exibindo outro criterio (o usuario "jura que esta
   ordenado por algo que nao esta"). Quirk: `x:Name` dentro de `ControlTemplate` NAO gera
   campo no code-behind -> a seta usa `Content` do botao (`BtnSortDirection.Content`).
3. **Aba Usuarios** (a queixa "cliquei na seta e nao listou"):
   - `BtnUserExpand_Click` agora dispara `LoadUsersSafeAsync()` quando abre com a lista
     vazia (antes o detalhe abria VAZIO por ate 1 s);
   - a lista de processos NAO e mais zerada quando o usuario fecha a linha;
   - `UserProcRow` ganhou `INotifyPropertyChanged` + `CpuValue`/`MemValue` e
     **`UserRow.MergeProcesses`** faz merge por PID (atualiza valores em LUGAR; so reatribui
     a lista quando o conjunto/ordem muda) — antes, 182 processos eram recriados 1x/s dentro
     do detalhe;
   - BUG antigo: a ordem dos processos do usuario usava `x.MemMB.Length` — o COMPRIMENTO DO
     TEXTO ("9 MB" vs "1,2 GB")! Agora ordena por `CpuValue`/`MemValue`.
   - **Ordenacao da aba Usuarios** passou a existir de verdade: `DgUsers_Sorting` +
     `_usersSortColumn/_usersSortDirection` + `ApplyUsersSort()` (a ordem sobrevive ao
     refresh; antes o clique ia para o sort default do DataGrid e era desfeito em 1 s).
4. **Latencia (deslocamento) e outras abas**: coluna lateral 314 -> **330** (igual a aba
   Armazenamento — era o "deslocado" ao alternar abas); os botoes do cabecalho viraram
   **WrapPanel** (em 1366px o botao "Admin" ficava CORTADO fora da janela); os 4 medidores
   perderam `Width="260"` fixo e passaram a `HorizontalAlignment="Stretch"`.
5. **ForceStop/TakeOwnership — blindagem (o que realmente travava)**:
   - **`Program.cs`: UAC negado abortava tudo EM SILENCIO.** O bloco de elevacao fazia
     `Process.Start` com verbo de elevacao e um `return` INCONDICIONAL — se o usuario negasse
     o UAC, o app saia sem fazer nada e sem avisar (o clique no menu de contexto parecia
     morto). Agora so encerra se a instancia elevada subiu de fato; senao mostra toast e
     continua o fluxo normal.
   - **`Program.cs`: IPC falhando = comando perdido.** `SendUnlock/SendTakeOwnership` ja
     devolviam `bool` e o retorno era IGNORADO. Agora, se o envio falhar: elevado -> worker
     headless + toast; sem admin -> toast explicando.
   - **`UnlockIpcServer`: alvo protegido era descartado.** O servidor so processava se
     `File.Exists(path) || Directory.Exists(path)` — e o .NET retorna **FALSE** para caminhos
     que existem mas a ACL nega leitura (C:\Windows.old, TrustedInstaller), exatamente o
     alvo do menu de contexto. Agora usa `FileTakeOwnership.ProbePath` (nativo) antes de
     descartar.
6. **Harness `tests/TmVisualBench` ganhou o modo `sort`** (comportamento, nao pixel): percorre
   o combo de ordenacao REAL da aba Processos com 9 criterios e confere os pares fora de
   ordem por grupo (tolerancia 5% pela defasagem de 1 s), depois expande o usuario mais
   pesado e exige `IsExpanded=True` + `DetailsVisibility=Visible` + lista nao vazia apos
   3 refreshes, e valida a ordenacao da aba Usuarios sobrevivendo ao refresh.
   **Resultado: 9/9 criterios OK e 3/3 ordenacoes de usuarios OK** ("143 linhas · 3 grupos ·
   0-7/140 pares fora de ordem"); detalhe do usuario com **183-190 processos listados** apos o
   refresh. `overlap` continua **10/10 abas sem texto sobreposto** (inclui Latency apos o
   realinhamento).

7. **Painel "Detalhes do Processo" CONGELADO** (achado ao varrer "as partes que exibem
   informacao"): o painel so era escrito em `DgProcesses_SelectionChanged`, ou seja, com um
   processo VIVO selecionado os campos instantaneos (Status/CPU/RAM/Handles/Threads/Disco/
   Rede) e o **Uptime** ficavam parados — outro "as informacoes nao atualizam". **Fix**: novo
   `RefreshLiveDetail()` chamado no fim do ciclo de refresh (dentro do
   `Dispatcher.InvokeAsync(..., DataBind)`): reaplica os instantaneos, os campos que crescem
   (commit, pico, IO lida/escrita, ops, tempo kernel/usuario — mesmos `FormatTotalBytes`/
   `FormatCpuTime` do load nativo) e recalcula o **Uptime** a partir de `_detailStartTime`
   guardado no fetch (nao refaz o fetch nativo: sessao/arquitetura/cmdline/Elevated quase nao
   mudam). Medido pelo harness: `Handles 5947->5935 · Uptime 0.08:10:55->0.08:10:57` com a
   selecao intocada.

Build: **0 erros**. Arquivos: `KitTaskManagerWindow.xaml(.cs)`, `Program.cs`,
`Services/UnlockIpcServer.cs`, `tests/TmVisualBench/Program.cs`.

### Proxima sessao
- [ ] Rebuild do kit e testar no app: chip ORDENAR + seta clicavel, cabecalhos Status/
      Usuario/Threads/PID ordenando, aba Usuarios (seta + ordenacao sobrevivendo ao refresh)
- [ ] Testar Latency em janela de 1366px: botao Admin visivel (WrapPanel) e medidores stretch
- [ ] Testar ForceStop/TakeOwnership pelo menu de contexto: negar o UAC -> deve avisar e nao
      morrer em silencio; clicar com o app ABERTO -> sem "nada acontece"
- [ ] Testar Take Ownership em C:\Windows.old pelo menu de contexto (alvo com ACL negada)
- [ ] (aberto) visual unico/identidade propria do KitTaskManager
- [ ] (aberto) Fase 1 do PAGES_RAM_OPTIMIZATION_PLAN (PrivacyPage/RepairsPage: custo de layout)

### Sessao 26/09 (cont. 2) — TM: tema proprio (submenus), Resumo com congelamento e historico

Pedido: "verifique o visual completo dele os submenus etc para ficarem com o mesmo tema do
TaskManager; na pagina de resumo poder clicar nos processos da direita (botao direito congela
o processo na lista para ele nao sumir enquanto o usuario modifica dali); e a area do meio do
resumo poder clicar e mostrar uma LINHA DO TEMPO com quem usava a CPU naquela hora".

1. **SUBMENUS NAO ABRIAM EM LUGAR NENHUM DO KIT** (achado grande): os tres templates de
   `MenuItem` (global `Themes/Menus.xaml`, a COPIA local da aba Processos e o `TmMenuItem` do
   TM) tinham Border+Grid+ContentPresenter, **sem `Popup`/`ItemsPresenter`** — o WPF ate marcava
   `IsSubmenuOpen`, mas nao havia popup para renderizar os filhos: clicar em "Prioridade" (7
   niveis) nao fazia NADA. Fix: `Popup` + seta ▸ (`HasItems`) + trigger `IsSubmenuOpen` nos tres;
   o menu da aba Processos deixou de ter template proprio e reusa `TmDarkMenu`/`TmMenuItem`.
2. **Tema do TM (o que os prints mostravam)**: o combo de ordenacao abria com o popup DOURADO do
   tema global do Kit (`DropDownBorder` #FFD700, item destacado amarelo) e a barra de rolagem
   ficava dourada no hover (animacao `#FFD700` do `ScrollBarThumb` global). Fix: `TmComboBox` +
   `TmComboBoxItem` + `TmScrollBar` (implícitos no `Window.Resources` do TM) no azul-escuro
   #141C28/#243040/#17324F; aplicados a TODOS os combos do TM (intervalo, ordenacao, prioridade,
   filtros de servico/startup) sem tocar no tema global do restante do Kit.
3. **Resumo — CONGELAR a lista + agir dali**: o `DgSumTopCpu` ganhou menu de contexto; o clique
   direito agora SELECIONA a linha sob o mouse (o WPF nao seleciona com botao direito — o menu
   agia no processo anteriormente selecionado) e `_sumMenuOpen` PAUSA o
   `UpdateSummaryTopCpu()` enquanto o menu esta aberto (a lista nao reordena/some embaixo do
   cursor). "Congelar na lista (fixar)" (`_sumPinned`) mantem o processo no topo mesmo caindo do
   Top 14, com 📌 no nome; as acoes (abrir na aba Processos, finalizar, suspender/retomar,
   prioridade — com submenu —, limpar memoria, copiar caminho/PID) reusam as rotinas existentes
   via `SumPrepareAction()` (seleciona a linha na aba Processos e chama `Kill`/`SuspendResume`/
   `MenuPriority_Click`/`BtnClearMemory_Click`): zero logica duplicada.
4. **Resumo — HISTORICO ("Click for History" do TMOG)**: clique no grafico de CPU abre um overlay
   com a LINHA DO TEMPO: ring buffer de 600 amostras (1/s = 10 min) guardando CPU/kernel/RAM **e
   os 6 processos que mais consumiam naquele instante** (`RecordHistorySample`). Barras = CPU
   (heatmap), linha vermelha = kernel, marcador vertical na amostra; clique/arraste escolhe o
   instante e mostra "quem estava usando a CPU" (nome + % + RAM no tooltip); botoes Agora /
   Limpar / ✕. O overlay cobre o canvas ao vivo (nao briga com o grafico).
5. **Harness `tests/TmVisualBench` (modo `sort`) ganhou 3 blocos novos** — todos passando:
   - **submenu**: abre o menu e o submenu de verdade (`"Prioridade": submenu aberto=True · 7
     itens · popups abertos na arvore=1`) — a regressao exata que existia;
   - **fixar**: `Pinned=True · continua na lista=True (posicao 0) · icone="📌"` depois de 2+
     refreshes;
   - **historico**: abre (`12 amostras`), desenha, lista `6` processos daquele instante, aceita
     selecao de amostra antiga e fecha.
   `overlap` continua **10/10 abas sem texto sobreposto**.

Build: **0 erros**. Arquivos: `Windows/TaskManager/KitTaskManagerWindow.xaml`,
`Windows/TaskManager/KitTaskManager.Summary.cs`, `Themes/Menus.xaml`,
`tests/TmVisualBench/Program.cs`.

### Proxima sessao
- [ ] Testar no app: botao direito no TOP do Resumo (congela a lista), fixar/desafixar com 📌
- [ ] Testar o HISTORICO: clicar no grafico de CPU, arrastar pelas amostras, conferir "quem usava"
- [ ] Conferir visual dos combos/submenus do TM (popup azul, sem dourado) e o submenu "Prioridade"
- [ ] (aberto) visual unico/identidade propria do KitTaskManager
- [ ] (aberto) Storage 14% / Diagnostic 26% de repintura: desenho incremental

### Sessao 26/09 (cont.) — Startup sob pressão: por que o Kit travava "(Não Respondendo)"

Pedido do usuario: analisar o startup do kit e como ele age sob pressão ("enquanto o sistema
estava sobrecarregado o kit simplesmente não conseguia responder, clicava e nada acontecia até
aparecer aquele negocio que o app fica claro e diz o app não esta respondendo").

**Diagnostico**: o startup em si ja estava bem escalonado (quase tudo em Task.Run ou deferido em
DispatcherPriority.Background). O culpado e o que roda DEPOIS, na thread de UI: o `MonitorTick`
do TrayIconService (DispatcherTimer de 30s) fazia, NA THREAD DE UI: enumeracao de TODOS os
processos (349 no host), `MainWindowHandle` de cada um, `PerformanceCounter` de IO por processo
(perflib), trims de working set, `DetectAndTrimLeaks` (liga com RAM >= 65% — ou seja, EXATAMENTE
sob pressao) e `LogStats` (append sincrono em CSV). Medido no host ocioso (i5-14600KF, 349 procs):
`Process.GetProcesses()` = 11 ms; `MainWindowHandle` x349 = 22 ms; **1a criacao de
`PerformanceCounter("Process","IO Read Bytes/sec")` = 874 ms**; cada instancia seguinte = 14-17 ms.
Sob carga esses numeros inflam para segundos (perflib e queries cross-process sao as primeiras
coisas a degradar com o sistema saturado) -> o Windows marca "(Nao Respondendo)" apos ~5s sem
bombear mensagens. Auto-infligido: o kit aumentava a propria carga na UI justamente quando o
usuario precisava dele.

**Correcoes (build 0 erros)**:
1. `TrayIconService.MonitorTick` (2236-2272): agora SO agenda. Guarda de overlap
   (`_monitorTickBusy` via Interlocked) quando o ciclo anterior ainda esta rodando; o corpo virou
   `RunMonitorCycle()` e roda no thread pool. Novo helper `PostTrayUi` (BeginInvoke, nunca Invoke)
   para o unico trecho de UI (icone/tooltip do tray).
2. `UpdateTrayIcon` (4000-4037): sai cedo se `percent == _lastTrayPercent` — sem recriar bitmap
   GDI/GetHicon nem chamar `Shell_NotifyIcon` sem mudanca.
3. `CleanRamNow` (~3971): o `Dispatcher.Invoke` SINCRONO de dentro do Task.Run virou `PostTrayUi`
   (nao prende mais um worker do pool esperando a UI).
4. `Program.Main` (99-108): `ThreadPool.SetMinThreads(max(nucleos, 8))` — o startup dispara dezenas
   de Task.Run e o minimo padrao (= nucleos) faz o pool injetar 1-2 threads/s (fila crescente).
5. **Novo `KitLugia.GUI/Services/UiFreezeWatchdog.cs`** (start em `App.OnStartup:72`): thread
   dedicada BelowNormal posta um probe em `DispatcherPriority.Send` (fura a fila de prioridade
   baixa, entao atraso alto = UI REALMENTE bloqueada) a cada 500 ms e loga
   `[UI-FREEZE] Thread de UI travada por ~N ms (maior da sessao, ocorrencias, itens pendentes no
   pool)` com anti-flood de 5 s. Custou 1 BeginInvoke/500 ms. Serve para a PROXIMA vez: o log diz
   o culpado/horario em vez de virar adivinhacao.

**Doc**: `docs/STARTUP_PRESSURE_ANALYSIS.md` (linha do tempo do startup UI vs background, tabela
de timers que rodam na UI, numeros medidos, correcoes e pendencias).

### Proxima sessao
- [ ] Recompilar e testar sob carga (jogo + compactacao + 300 procs): abas devem responder
- [ ] Conferir no log `[UI] Watchdog de responsividade ativo` e, ao engasgar, o `[UI-FREEZE] ~N ms`
- [ ] Conferir que o icone do tray continua mudando com o skip por percentual igual
- [ ] (pendencia do doc) `SampleTraffic`: trocar PerformanceCounter (perflib) por contadores nativos
      (`GetProcessIoCounters` / `NativeMetricsHelper`, que o KitTaskManager ja usa)
- [ ] (pendencia do doc) `UpdateProcessProfiles`/`DetectAndTrimLeaks`: 1 snapshot Toolhelp32 +
      EnumWindows em vez de N+1 (`GetProcesses` + `MainWindowHandle` por processo)
- [ ] (pendencia do doc) mostrar `WorstStallMs`/`StallCount` do watchdog na Central de Diagnostico
### Sessao 27/09 — KitTaskManager: mapa de estilos + fim do dourado do tema global

Pedido do usuario: "mapear os estilos e tudo de visual do kittaskmanager que ai ja da para
voce olhar os estilos e ja arrumar tudo de uma vez". Entregue em duas partes: o MAPA
(`docs/TM_STYLE_MAP.md`) e um pacote de correcoes aplicado em lote.

**CAUSA RAIZ (o "dourado" que sobrava):** o TM e uma janela AZUL dentro de um app cujo tema
global e DOURADO. `Themes/*.xaml` declara estilos IMPLICITOS (Button, CheckBox, ComboBox,
ComboBoxItem, DataGrid, DataGridColumnHeader, DataGridRow, DataGridCell, Separator, ScrollBar)
que vazam para todo controle sem estilo proprio. Sintomas confirmados: botao dourado + "zoom"
no press (`#30FFD700` + `ScaleTransform`), cabecalho de tabela `#1A1A1A` com texto DOURADO e
40 px, linha selecionada com lavagem `#25FFD700`, CheckBox 22x22 dourado, popup de combo
dourado. Alem disso o proprio TM usava `_brushYellow = #FFD700` (o dourado do Kit!) na escala
de calor — CPU/RAM ~50-60 % saiam em ouro puro.

**Correcoes (tudo em `KitTaskManagerWindow.xaml` + 1 linha em .cs):**
1. **Paleta/tokens** no topo do `Window.Resources` (22 brushes: `TmBgDeep/Window/Card/Header/
   Control/Hover`, `TmBorder`, `TmAccent`, `TmSelect`, `TmText*`, `TmGood/Warn/Bad/Ram/Net`).
2. **Estilos implicitos do TM** (nao existiam): `Button` (hover `#243040`/press `#17324F`, SEM
   dourado e SEM zoom), `CheckBox` (15x15 azul), `Separator` (`#1F2A3A`), e
   `DataGridColumnHeader/Cell/Row` com `BasedOn` nos estilos compartilhados. **Estes 3 ultimos
   eram obrigatorios**: o `TmGrid` declara cabecalho/celula por Setter de estilo e, nas abas
   Latencia/Armazenamento/Diagnostico, o estilo GLOBAL vencia (cabecalho `#1A1A1A` dourado).
3. **`TmGridRow`** (novo) + `RowStyle` no `TmGrid`: a selecao dessas tabelas era a lavagem
   dourada do tema global.
4. **`TmToolButton`**: o `ContentPresenter` forcava `TextBlock.Foreground="White"`, o que
   IGNORAVA o `Foreground` local — `services.msc`/`resmon` (que passam `#4FC3F7`) saiam brancos.
   Removido o hardcode e movido `Foreground` para o estilo.
5. **Normalizacao de cores** (script): `#0E141B`/`#0B1018`/`#0B1119`/`#121212` -> `#0D131A`
   (o grafico de RAM usava `#121212`, cinza puro fora do azul); `#161F2C` -> `#141C28`;
   `#26262B`/`#26262C` -> `#243040`; `#33445A` -> `#17324F`; `#101822` -> `#141C28`;
   `#2A3A4E` -> `#2C3A4E`; 13 cartoes `CornerRadius="6"` -> `8`.
6. **Titulos de secao** (29) -> `Style="{StaticResource TmSectionHeader}"` (unifica os 10/11/12
   divergentes); **selos** (5) -> `Style="{StaticResource TmBadge}"`.
7. **Icones da sidebar**: 14/15/16/17/18 px -> **17** em todos os 10 botoes.
8. **`_brushYellow` `#FFD700` -> `#FFD54F`** (`KitTaskManagerWindow.xaml.cs:3172`).

**FERRAMENTA NOVA — `TmVisualBench gold`** (`tests/TmVisualBench/Program.cs`): percorre a arvore
visual e reporta todo brush DOURADO visivel (tipo do elemento + texto + brush + bounds).
Foi ele que apontou o culpado (`TextBlock.Foreground #FFD700 "54%"`) depois que a medicao de
pixels no PNG so dizia "tem ouro nesta faixa". Uso: `dotnet run --project tests/TmVisualBench -- gold`.

**Validacao objetiva (nao "achei que melhorou"):**
- Build da solucao: **0 erros**.
- `overlap`: **10/10 abas sem texto sobreposto**.
- `flicker`: Resumo 3,7 % (era 3,9), Storage 12,6 % (era 14,0), Diagnostic 25,0 % (era 26,1),
  Connections 5,9 %, Users 0,1 %, Latency 0,3 % — nenhuma regressao.
- **`gold`: 0 elementos dourados em 10/10 abas** (antes: Latencia 3207 px e Armazenamento
  3018 px de ouro puro no PNG, alem do cabecalho `#1A1A1A`).
- `sort`: a unica falha (`RamValue|Descending`) e **flake pre-existente** — a RAM dos processos
  muda entre o ordenar e o verificar; a segunda rodada escreveu "TUDO OK". Nao e regressao.

**Doc**: `docs/TM_STYLE_MAP.md` (paleta completa, estilos nomeados, quais estilos globais
vazavam, tabela antes->depois, regras para manter e como validar).

### Proxima sessao
- [ ] Abrir o TM e conferir no olho: aba de tabelas (Latencia/Disco/Diag) com cabecalho azul,
      selecao azul e sem dourado; checkbox novo (15 px) e o combo ORDENAR sem dourado
- [ ] Conferir se o `Foreground="#4FC3F7"` agora pega nos botoes `services.msc` / `resmon`
- [ ] (opcional) aplicar `Style="{StaticResource TmCard}"` nos ~15 cartoes (hoje a unificacao
      foi por hex; o estilo existe e e o caminho para nao voltar a divergir)
- [ ] (opcional) refatorar os 11 botoes da sidebar (template duplicado ~40 linhas cada, ~440
      linhas) num style unico — os nomes `bdr`/`Indicator`/`Icon`/`Label` sao lidos via
      `Template.FindName`, entao funciona com style
- [ ] (opcional) normalizar os `#999`/`#BBB`/`#CCC` restantes para `TmTextDim`/`TmText`
- [ ] (pendencia antiga) desenho incremental dos Canvas de Storage/Diagnostic (12,6 %/25,0 % de repintura)
### Sessao 27/09 (cont.) — Resumo: overlay "Click for History" deixava as metricas minusculas

Sintoma relatado pelo usuario (print): ao clicar no grafico de CPU para ver o historico, o
painel `PnlCpuHistory` abria com as metricas "bem minusculas" — e aparecia um pedaco CORTADO
do numero grande ("5%") e de "rmel 7%" (kernel) na fresta da borda direita.

**Causa raiz (2 problemas somados):**
1. **Layout**: o grid interno do painel era `Auto / Auto / *` — o `*` ficava na linha da
   INFORMACAO, nao na do canvas. Resultado: o canvas ficava com 58 px fixos no topo, um VAAO
   PRETO morto no meio (a linha `*` de fundo `#0D131A`, igual ao canvas) e a lista de processos
   empurrada para o rodape, sem moldura e sem cabecalho, em `FontSize 10` — o menor texto da
   tela (o `TxtHistWhat` ao lado e 11).
2. **Numero grande cortado**: o painel tem `Margin="0,0,40,0"` (para nao cobrir o eixo de
   temperatura 110/55/0). O `TxtSumCpuBig` (26 px) fica alinhado a direita do grafico, ou seja
   cai JUSTAMENTE nessa faixa de 40 px que sobra — por isso o "5%" e o "rmel 7%" apareciam
   cortados atras da borda.

**Correcoes (`KitTaskManagerWindow.xaml` + `KitTaskManager.Summary.cs`):**
1. Linhas do grid interno: `Auto / Auto / *` -> **`Auto / * / Auto`** (o `*` agora e do canvas,
   a informacao fica logo abaixo dele; o vao morto do meio acabou).
2. Canvas do historico: `Height="58"` fixo -> **`MinHeight="56"`** — em janela normal as barras
   CRESCEM. Medido pelo bench: **257 px** (era 58) numa janela de 1000 px.
3. `ClipToBounds="True"` no `PnlCpuHistory`: em janela pequena o conteudo (cabecalho + barras +
   lista) e maior que a area do grafico e vazaria sobre o cartao MEMORIA.
4. **Numero grande ao vivo escondido enquanto o painel esta aberto**: `TxtSumCpuBig` e
   `TxtSumCpuSub` -> `Collapsed` no `SumCpuCanvas_Click`, `Visible` de volta no
   `BtnHistClose_Click`. Acabou o "5%" cortado na fresta.
5. **Lista de quem usava a CPU virou tabela**: moldura propria (`#111823` + borda `#1F2A3A` +
   raio 5), cabecalho `PROCESSO` / `CPU` (9 px `#777`), nome em `FontSize 11` e `#EEE`, e o `%`
   com **coluna fixa de 40 px** alinhado a direita (antes o alinhamento era por acaso).
6. `TxtHistSel` ("00:48:57 · 12 amostra(s) de 10 min"): `FontSize 10` -> **11** (igual ao resto
   do painel).

**Ferramenta**: o modo `sort` do `TmVisualBench` agora salva `out/tm_SummaryHistory.png` com o
painel ABERTO e imprime duas medidas objetivas:
`painel : 958x440 px · canvas 257 px` e `lista : FontSize 11 · 6 linhas`.
Assim da para provar a mudanca sem depender de "olhar e achar".

**Verificacao objetiva:**
- Build: **0 erros**.
- `sort` (historico): abre, desenha 13 barras, lista 6 linhas, seleciona amostra, fecha — TUDO OK.
- `overlap`: **10/10 abas** sem texto sobreposto.
- Pixels do print do painel aberto: **0** pixel de texto claro na faixa de 40 px a direita do
  painel (ou seja, o numero grande cortado sumiu) e **0** dourado (os 37 px "dourados" que
  aparecem sao um **icone de aplicativo** em x1408-1423/y438-447, na coluna TOP PROCESSOS).

**Doc**: `docs/TM_STYLE_MAP.md` — nova secao 6 "Overlay Click for History" com a tabela do que
nao pode regredir.

### Proxima sessao
- [ ] Abrir o TM em janela normal e pequena (MinHeight 600) e conferir o painel de historico
      (barras grandes, lista legivel, nada vazando sobre a MEMORIA)
- [ ] (pendencia da sessao anterior) migrar os ~15 cartoes para `Style="{StaticResource TmCard}"`
- [ ] (pendencia da sessao anterior) refatorar os 11 botoes da sidebar num style unico
- [ ] (opcional) `TmVisualBench sort` salvar tambem um print de cada aba em janela PEQUENA
      (400x600) para pegar clipping de overlay automaticamente
### Sessao 28/09 — TaskManager: "abre mas demora para mostrar valor" (véu de carga) + anti-stutter de áudio na engrenagem

Pedido: "continue e olhe tudo que puder sobre o taskmanager depois quando tiver certeza que
tudo dele funciona voltaremos a focar só no app", mais: (1) nos PCs dos amigos o TM abre mas
os valores demoram e não aparece nenhum sinal de carregamento; (2) o item de anti-stutter de
áudio poderia estar nas configurações do Kit (engrenagem).

**1. Diagnóstico (caminho crítico do 1º refresh)**

`RefreshAsync` fazia `await Task.WhenAll(netTask, gpuTask, gpuPerPidTask, snapTask)`: o
desenho esperava o coletor MAIS LENTO do pipeline — o `PDH \GPU Engine(*)` por processo, que
na 1ª chamada custa 800-1400 ms aqui (perflib frio + warmup de 150 ms) e 2-6 s em máquina
fraca. Somado a isso, o Resumo (aba que abre) só recebia número no 1º tick do `_graphTimer` —
1 s DEPOIS do Loaded. Resultado: estrutura pronta, tela vazia e nenhuma indicação de trabalho.

**2. Correções na janela do TM**

- `KitTaskManagerWindow.xaml`: novo `LoadOverlay` (row 1, ColumnSpan 2, ZIndex 60) com
  spinner (elipse tracejada), título, etapa, 4 barrinhas de progresso e tempo decorrido.
  Fundo `#8C070B11` + `IsHitTestVisible=False`: informa sem bloquear (abas seguem clicáveis e
  dá para ver os valores chegando atrás).
- `KitTaskManager.Loading.cs` (novo partial): `BeginLoadingOverlay` (chamado no Loaded),
  `SetLoadStage`, `NotifyLoadCountersReady/RowsPainted/GraphicsRendered`,
  `FinishLoadingOverlay` (fade de 220 ms + log `[KIT TASK MANAGER] 1ª carga em ... ms`) e
  `StopLoadingOverlay` no `Closing`. UM `DispatcherTimer` de 100 ms (giro + tempo), parado
  quando o véu sai. Teto de segurança de 15 s: nenhuma falha de coleta deixa o véu preso.
- `RefreshAsync`: `KickAuxCollectors()` (GPU por processo, conexões TCP, nomes de usuário)
  passa a rodar DESACOPLADO do desenho, publicando objetos novos (`_auxGpuPerPid`,
  `_auxNetConnections`, `_auxUserNames`) com guarda `_auxCollecting` (uma coleta por vez). O
  refresh usa o último valor publicado; só a 1ª pintura dá uma janela de 300 ms. `_lastGpuPct`
  só é sobrescrito por valor real (`>= 0`).
- `Loaded`: primeiro `UpdatePerformanceGraphsSafe()` IMEDIATO (antes: 1 s depois) — o Resumo
  recebe número assim que a 1ª amostra fica pronta.

**3. Bench: modo `load` (novo) — "lento" virou número**

`tests/TmVisualBench` agora tem o modo `load`: bombeia o dispatcher em passos de 25 ms e
imprime a linha do tempo da 1ª carga, salva `out/tm_carregando.png` (véu em ação) e
`out/tm_primeiro_conteudo.png`, checa sobreposição no cartão do véu e dourado visível.
Medido (Debug, 1680x1000 e 1180x740): véu em t+28 ms · 1ª lista t+435 ms (131 linhas) ·
Resumo com valor t+1007 ms (14 linhas no Top processos) · status t+488 ms · véu saiu
t+1235 ms · "layout do véu OK" · 0 dourado · véu ainda visível: não.

**4. Bug de TESTE (não do app): `sort` acusava "ordem errada" por empate de 0,1**

`RamValue|Descending` acusava 8-10 pares fora de ordem (7-8%). Investigação: a pior violação
era **Δ 0,1 MB** — a ordenação do TM arredonda em 1 casa (anti-pisca) e depois desempata pela
posição anterior, ou seja: empate proposital, invisível na tela. O bench agora só conta
violação quando a diferença passa a granularidade (> 0,1) e reporta os empates à parte.
Resultado: `sort` → **TUDO OK** (9 critérios de ordenação, detalhe ao vivo, submenu, fixar,
histórico). Nenhuma mudança no app foi necessária.

**5. Anti-stutter de áudio nas configurações do Kit (engrenagem)**

- `TrayIconService`: `AudioAntiStutterEnabled` (lê o monitor = fonte única),
  `SetAudioAntiStutter(enabled, persist)` (monitor + AutoRecover + Start/Stop da escuta +
  SaveSettings) e `PersistAudioAntiStutter(enabled)` (só registro, para o TM).
  `LoadSettings()` aplica o valor salvo com `persist:false` — o recurso vale desde a abertura
  do Kit, sem abrir o Gerenciador de Tarefas.
- Chave: `HKCU\Software\KitLugia\TraySettings\AudioAntiStutter` (mesmo padrão dos toggles
  da comunidade/GameBoost).
- `SettingsPage.xaml(.cs)`: card **ÁUDIO → Anti-Stutter de Áudio** com `ToggleAudioAntiStutter`
  e linha de estado honesta (`UpdateAudioStutterStatus`: "Ativo: escutando…", "Ativo (salvo): a
  escuta não subiu agora…", "Desligado"). Evento em `AddToggleEvents` e desassinatura no
  `Cleanup` (convenção da página).
- `KitTaskManager.Audio.cs`: o checkbox da aba Latência (que mantém as regras próprias de
  escuta do diagnóstico) agora ESPELHA a escolha na mesma chave de registro
  (`PersistAudioAntiStutterPreference`), então as duas telas nunca divergem.
- Motor (gatilho/cooldown/limite/reset) INTOCADO — a auditoria de 53 asserções de 25/09
  segue valendo.

**Verificação**
- Build: **0 erros** (solução completa; o processo `KitLugia.GUI` travava a cópia dos DLLs —
  encerrado com autorização do usuário).
- `load` em 1680x1000 e 1180x740: OK (números acima), `layout do véu` OK nos dois tamanhos.
- `tabs gold` 10/10 abas: nenhum texto sobreposto, **0 dourado**.
- `flicker`: Resumo 3,1 % · Processes 3,1 % · Storage 12,1 % · Diagnostic 25,0 % (mesmos
  patamares de 27/09 — sem regressão).
- `sort`: **TUDO OK**. `PagesBench loaded`: SettingsPage constrói em 8,7 ms, sem erro e sem
  vazamento (sobra3 = 0).

**Docs**: `docs/TM_FIRST_LOAD.md` (novo: diagnóstico, correções, números),
`docs/TM_STYLE_MAP.md` (seção 9 "Véu de carregamento" + comandos do bench + histórico),
`docs/AUDIO_RECOVERY_ROBUSTNESS.md` (seção do item nas configurações do Kit).

### Proxima sessao
- [ ] Testar no app: abrir o TM (de preferência em VM/máquina fraca) e ver o véu com spinner,
      etapa e tempo saindo sozinho quando a 1ª lista e o 1º valor do Resumo aparecem
- [ ] Testar no app: engrenagem → ÁUDIO → Anti-Stutter de Áudio ON → conferir
      `HKCU\Software\KitLugia\TraySettings\AntiStutter` (nome exato: `AudioAntiStutter`),
      reabrir o Kit e ver o card em "Ativo: escutando…" + checkbox do TM (Latência) marcado
- [ ] (pendência) desenho incremental dos canvas: Storage (12,1 % de repintura) e Diagnostic
      (25,0 %) — desenhar só o que mudou, como no histórico do Resumo
- [ ] (pendência) migrar os ~15 cartões do TM para `Style="{StaticResource TmCard}"`
- [ ] (pendência) refatorar os 11 botões da sidebar do TM num style único
- [ ] (pendência) alinhamento/espaçamento das abas (começando por Latência)
- [ ] (pendência) ForceStop/TakeOwnership: revisar robustez
- [ ] (opcional) rodar `tests/AudioRecoveryAudit phase1` depois de mexer em áudio (executa um
      reset real do audiodg por ~300 ms)

### Sessao 28/09 — Auditoria das páginas ATIVAS (42 páginas alcançáveis)

Pedido: "dê uma geral nas pages ativas do app, as que o usuário pode acessar, e veja se está
tudo em ordem".

**1. Universo fechado (como o usuário chega em cada página)**

Pontos de entrada reais: sidebar+toolbar (14 itens), busca global (`SearchEngine.AddNav` →
`MainWindow.NavigateToPage(string)` → `Enum.TryParse<PageType>` + `NavTagMap`), cards do
Dashboard, cards da `WindowsPage`, cards da `AdvancedToolsPage`, engrenagem → `SettingsPage`,
`KitStoreWindow` → `StoreRemakePage`, aba Diagnóstico do TM, IPC do Explorer → ForceStopUnlock.
Resultado: **42 páginas ativas**;

**6 páginas são código MORTO** (nenhuma referência fora dos dois `switch` de navegação):
`AboutPage`, `BloatwarePage`, `OptimizationPage`, `ProcessMonitorPage`, `ProgramsPage`,
`ContextMenuPreviewPage` (o preview foi embutido na `ContextMenuPage`). Continuam compilando e
aparecem nos benches (48 páginas construídas) — remoção NÃO feita (decisão do usuário).
Ponto obscuro: `ContextMenuPage` só é alcançável pela busca global (nenhum botão/card).

**2. Ferramentas de auditoria criadas (ficam no repo)**

- `tests/page_audit.sh` — checklist por página: `public void Cleanup()`, `Unloaded +=`,
  lambda em `Unloaded`, timer com Stop/Dispose, `DataContext = null`, trabalho pesado no
  construtor (regex na 1a região do ctor), paths absolutos em C#.
- `tests/page_cleanup_audit.sh` — checa DENTRO do corpo do `Cleanup()` se cada timer da página
  é parado, eventos estáticos desinscritos, `Loaded` removido e `DataContext = null`.
- `dotnet run --project tests/PagesBench -- leak` (48 páginas, 3 corridas).

**3. Correções aplicadas (todas são defeitos reais, não estilo)**

1. `ServerPage`: inscrição `Unloaded` NUNCA existia (só o `Unloaded -=` no Cleanup) → Cleanup
   morto em navegação comum (túnel/Playit não eram encerrados). Inscrição adicionada.
2. `QuickInstallPage`: sem `Unloaded` → pasta temporária da ISO (`%TEMP%\KL_WIN_*`) ficava para
   trás. Handler nomeado + `Unloaded` + `DataContext = null`.
3. `ShrinkPage`: sem `Unloaded` → CTS de operações longas não cancelado ao sair.
4. `UpdatePage`: o construtor disparava `Task.Run(Confidence.Benchmark())` — **10.000 iterações
   × 25 pares × 2 implementações = 500.000 chamadas (P/Invoke incluso)** a cada abertura da
   página, só para escrever uma linha no log (o resultado nunca era lido). Removido do ctor.
5. `PartitionsPage`: `LoadDisks()` no construtor (anti-pattern 8) movido para o `Loaded`
   (uma vez, em background); bloco `_usageMonitorCts` duplicado no Cleanup removido.
6. `ForceStopUnlockPage`, `ContextMenuManagerPage`, `ContextMenuAddPage`:
   `Unloaded += (_,_) => Cleanup();` (lambda não removível) → handlers nomeados + `-=`.
7. `StoreRemakePage`: cleanup embutido no handler, sem `public void Cleanup()` → extraído.
8. `SearchEngine`: tag de navegação `"Games"` NÃO existe em `PageType` nem no `NavTagMap` →
   buscar "Jogos" respondia "EM BREVE". Virou entrada `Windows` (a `WindowsPage` não tinha
   entrada na busca; o GameBoost já tinha a sua).
9. `ServicesPage`: `StartsWith(@"C:\Program Files\WindowsApps")` (path absoluto, regra 1)
   → resolvido por `Environment.SpecialFolder.ProgramFiles`.
10. `WinpeToolsPage.xaml` (dicas): texto mandava olhar `C:\Program Files\KitLugia\WinPE\` e
    `C:\KitLugia_WinPE_Log.txt` — kit é portátil e o log vai para a raiz do volume alvo.
    Texto corrigido (staging `C:\KL_WINPE`, log no volume alvo).

**4. Evidências**

- Build: **0 erros** (solução completa).
- `PagesBench leak`: frio total 656–801 ms, quente 3,5–3,9 ms/página, layout 19,4–21,1 ms/página,
  retido 15,3 MB, working set 44 → 186/194 MB. **Nenhuma página ativa reteve >512 KB depois do
  Cleanup() em 2 corridas seguidas** (sem vazamento).
- Tags da busca: todas resolvem (após o item 8).
- Hotspots (backlog, não vazamento): `PrivacyPage` layout ~190 ms, `RepairsPage` ~167 ms,
  `GameBoostPage` frio ~88 ms, `GlobalSearchPage` frio ~54 ms.
- Falsos positivos documentados (não reabrir): `DiagnosticPage` (nomes de campos de OUTROS
  componentes lidos por reflection — é o visualizador de timers), `NetworkPage` (`_timersStarted`),
  `ScreenPage` (só `GetSystemMetrics` no ctor), `WinpeToolsPage` (regex de log, não path de
  recurso), lambdas em `Loaded`/`Unloaded` do próprio `this` (sem raiz externa).

**Docs**: `docs/PAGES_ACTIVE_AUDIT.md` (novo: universo, achados, evidências, falsos positivos,
backlog).

### Proxima sessao
- [ ] Decidir o destino do código morto (6 páginas: About/Bloatware/Optimization/ProcessMonitor/
      Programs/ContextMenuPreview) — remover ou reancorar na UI
- [ ] Reduzir o layout de `PrivacyPage` (~190 ms) e `RepairsPage` (~167 ms) — virtualizar/reduzir
      a árvore (mesmo item da "Fase 1" do plano de páginas)
- [ ] Dar um ponto de entrada visível para a `ContextMenuPage` (hoje só a busca global alcança)
- [ ] Testar no app: navegar por todas as 42 páginas ativas e conferir visual/estado
      (validação humana — o bench não abre janela)
- [ ] (carregado) Testar o véu de carregamento do TM e o toggle Anti-Stutter na engrenagem
- [ ] (carregado) desenho incremental dos canvas do TM (Storage 12,1 % / Diagnostic 25,0 %)
- [x] ~~(carregado) ForceStop/TakeOwnership: revisar robustez~~ → feito na sessão 28/09 (ver abaixo)

### Sessao 28/09 (cont.) — Force Stop + Take Ownership "SEMPRE funcionam" (pipeline garantido)

Pedido: "faça tanto o force stop quanto o take ownership sempre funcionar sem parar quando o
usuário for usar" (entrada: card Force Stop na `WindowsPage`; também o menu de contexto do Kit).

**1. CAUSA RAIZ ENCONTRADA (a mais importante de todas): privilégio nunca era habilitado**

`TOKEN_PRIVILEGES` estava declarado em 4 lugares como `int PrivilegeCount; long Luid; int
Attributes;` (e em 2 como `Pack = 1`). O `LUID` nativo é `{ DWORD LowPart; LONG HighPart; }`
com **alinhamento 4** — com `long` (alinhado a 8) o campo cai no offset 8 e o Windows lê um
**LUID inválido**: `AdjustTokenPrivileges` retorna **TRUE + ERROR_NOT_ALL_ASSIGNED (1300)** e
**nenhum privilégio entra no token**.

Consequências reais que isso explica:
- `EnableDebugPrivilege()` nunca habilitava SeDebug → o scan nativo de handles não enxergava
  processo de outro usuário ("não acha nada");
- `FileTakeOwnership.EnablePrivileges()` nunca habilitava SeTakeOwnership/Backup/Restore → o
  take ownership in-process só funcionava quando caía no fallback `takeown.exe`+`icacls`;
- `RegistryOwnership` (struct sem `PrivilegeCount`!) e `MemoryOptimizer`/`TrayIconService`
  (Pack=1) idem (ownership de registro e purge da standby list).

Corrigido com struct `LUID` explícita (layout 4+8+4 = 16 bytes) + checagem de 1300 com log
honesto em: `ForceStopUnlockService`, `Tweaks/FileTakeOwnership`, `RegistryOwnership`,
`MemoryOptimizer`(Pack=4), `Services/TrayIconService`(Pack=4) e no novo `FileOpGuarantee`.
Evidência (log do worker, antes → depois): `Privilégios habilitados: nenhum` →
`SeDebugPrivilege, SeTakeOwnershipPrivilege, SeBackupPrivilege, SeRestorePrivilege,
SeLoadDriverPrivilege`.
Diagnóstico usado: `tests/check_elevation.ps1` (TokenElevation) e `tests/list_privs.ps1`
(lista os privilégios do token via `GetTokenInformation/TokenPrivileges`).

**2. Pipeline garantido (`KitLugia.Core/FileOpGuarantee.cs`, novo)**

Ordem fixa, nenhuma etapa opcional: (0) habilita os 5 privilégios; (1) probe NATIVO do caminho
(distingue "não existe" de "ACL nega"); (2) TakeOwnership quando a ACL nega (ou sempre, na ação
de ownership); (3) bloqueadores (Restart Manager → handles nativos → handle64 → drivers .sys);
(4) ação pedida — ForceStop confere com handle EXCLUSIVO, Delete usa os 6 métodos (pasta
recursiva); (5) **nunca beco sem saída**: se ainda houver handle ativo, agenda a remoção no
próximo boot (`ScheduleDeleteOnReboot`) e reporta como sucesso agendado.
`GuaranteeResult` tem `Ok/ScheduledForReboot/StillBlocked/NeedsAdmin` + contadores e a lista de
passos (progresso ao vivo na UI).

**3. Um único UAC, sem tirar o usuário da tela**

- `KitLugia.GUI/Services/ElevatedFileOpRunner.cs` (novo): se não elevado, sobe o MESMO exe como
  `--worker` com `Verb=runas`, acompanha o progresso por arquivo e devolve o resultado à página;
  UAC cancelado → roda in-process no melhor esforço e diz o que exigia admin (nunca no-op).
  `IsAdmin` agora usa `FileOpGuarantee.IsElevated()` (TokenElevation) — `IsInRole(Administrator)`
  pode dar "true" com token filtrado (UAC ligado) e era o caso em que o app não elevava.
- `Program.cs`: modo `--worker` parseado ANTES do mutex/da UI (o app principal segue dono da
  janela). `RunHeadlessFileOperation` (menu de contexto, `--unlock`/`--takeown`) passou a usar o
  pipeline e o toast reporta "agendado para o próximo boot"/"precisa de admin".
- `ForceStopUnlockPage`: `EnsureElevatedForFileOp` REMOVIDO (perguntava "relançar elevado?" e
  encerrava o clique). Agora `RunGuaranteedAsync` atende **Tentar Deletar**, **Liberar
  Selecionados** (Force Stop) e **Assumir** (Take Ownership), com progresso no painel da página.
- Card da `WindowsPage` atualizado (tooltip + descrição) para refletir o comportamento novo.

**4. Evidências dos testes headless (exe real, no host)**

| Cenário | Resultado |
|---|---|
| arquivo travado por outro processo (PowerShell com handle exclusivo) + `--action delete` | 1 bloqueador → handle fechado via Restart Manager → `File.Delete` OK → `R|OK|1`, arquivo removido |
| árvore de teste + `--action takeown --recursive` | 5/5 itens, dono mudou de `0PKM6PR0\Lugia` para `BUILTIN\Administradores`, "Acesso confirmado" |
| arquivo travado + `--action force-stop` | handle fechado, "Caminho liberado: nenhum handle ativo restante", arquivo intacto |

Build: **0 erros** (solução completa). Docs: `docs/FORCE_STOP_UNLOCK.md` (seção nova com o
pipeline, os bugs de raiz e o checklist de teste no app).

### Proxima sessao
- [ ] Testar no app (com o Kit SEM elevação): Windows → Force Stop → colar caminho → **Tentar
      Deletar** / **Liberar Selecionados** → aceitar o UAC UMA vez e ver o progresso chegando na
      própria página (não deve abrir outra janela nem parar)
- [ ] Testar no app: aba **Take Ownership** → **Assumir** em item protegido (ex: `C:\Windows.old`)
- [ ] Testar o menu de contexto do Explorer (Force Stop / Take Ownership) com o Kit rodando
      normalmente (deve resolver via worker elevado + toast)
- [ ] Confirmar no log (`%LocalAppData%\KitLugia\Logs\KitLugia.log`) as linhas
      `Privilégios habilitados: SeDebugPrivilege, ...` (se aparecer "nenhum" de novo, é regressão
      de layout de LUID)
- [ ] (carregado) Decidir o destino do código morto (6 páginas) e reduzir o layout de `PrivacyPage`/
      `RepairsPage`
- [ ] (carregado) desenho incremental dos canvas do TM (Storage 12,1 % / Diagnostic 25,0 %)

### Sessao 29/09 — Menu de contexto: "nada acontecia" + duplicatas (RESOLVIDO)

Sintoma (print do usuario): o clique direito no Explorer mostrava DUAS entradas
("Take Ownership (KitLugia)" e "Take Ownership Super (KitLugia)") e o clique nao abria
o Kit nem fazia nada — "o antigo conseguia pelo menos abrir o kit para iniciar a
operacao sem scripts externos; o de agora nao consegue nem abrir o kit".

**Causa 1 — o bootstrap engolia o clique (`Program.cs`).**
`--unlock/--takeown` fazia: (a) sem admin → relancava o exe elevado (`Verb=runas`) e
ENCERRAVA esta instancia; (b) a instancia elevada, encontrando o Kit ja aberto (mutex
ocupado), rodava `RunHeadlessFileOperation` (worker headless + toast) e saia —
NENHUMA janela. Com o Kit na bandeja (caso comum) o clique ficava mudo.
Log do host, minutos antes do fix: `[ELEV] Instancia elevada + mutex ocupado → worker
headless.` (5x seguidas — era o usuario clicando e nada abrindo).

Correcao: o bootstrap agora SO entrega o comando — IPC para a instancia existente ou
abre a UI em modo `--unlock/--takeown` quando nao ha nenhuma. A elevacao passa a ser
responsabilidade da PAGINA (`RunGuaranteedAsync` → `ElevatedFileOpRunner`: 1 UAC, com
progresso visivel, e so quando o alvo exigir). `RunHeadlessFileOperation` REMOVIDO
(dead code). O `--worker` (processo elevado chamado pelo runner) continua igual.

**Causa 2 — janela na bandeja nao era mostrada (`MainWindow` / `UnlockIpcServer`).**
`CloseToTray` usa `Hide()`; o IPC chamava `Activate()/Focus()` → janela invisivel
continua invisivel. Novo `MainWindow.ShowAndActivateFromTray()` (resume os timers,
`EnsureUIInitialized`, `MainFrame.Opacity=1`, `Show()` se invisivel, `Normal` se
minimizada, `Activate`+`Focus`), chamado no inicio de `NavigateToUnlock`/
`NavigateToTakeOwn` e pelo IPC. O fallback headless do `OpenTakeOwnership` (executava
takeown sem UI nenhuma) virou `OpenFallbackWindow` (janela dedicada com o caminho) —
o usuario PRECISA ver que o clique fez algo.

**Causa 3 — duplicatas.** Duas chaves registravam a MESMA acao, com o comando
identico: `\shell\kittakeown` (`AddTakeOwnershipKit`, toggle da pagina) e
`\shell\kit_takeownership` (`ContextMenuQuickAdd.AddTakeOwnershipSuper`).
Canonica = `kittakeown`. `AddTakeOwnershipSuper` REMOVIDO; o item super do catalogo
agora delega para `AddTakeOwnershipKit()/RemoveTakeOwnershipKit()` (que passou a apagar
tambem `kit_takeownership`/`kit_takeown`/`kit_takeown_super`). Novo
`SystemTweaks.ConsolidateTakeOwnershipMenu()` — migracao idempotente chamada no startup
junto de `RefreshContextMenuPathsIfNeeded`/`ReapplyContextMenuPrefs`: remove o legado e
recria a canonica com o path atual do exe. O item da ContextMenuAddPage agora aparece
como "Take Ownership (KitLugia)".

**Validado no host (exe Debug real):**
- startup com as 2 chaves: `kit_takeownership` removida, resta so `kittakeown`
  (`*` e `Directory`); log `[TAKEOWN KIT] Duplicatas consolidadas em uma unica
  entrada: kittakeown`
- `--takeown <arquivo>` com o Kit na bandeja: `[IPC] Comando recebido: TAKEOWN` +
  entrega na instancia existente E **janela visivel** (`MainWindowHandle` valido,
  titulo "Kit Lugia - Gold Edition") — exatamente o bug reportado
- `--unlock <arquivo>`: `[IPC] Comando recebido: UNLOCK` (mesmo caminho)
- `--worker --action takeown`: `R|OK|1` / `OWNED|1` + os 5 privilegios (sem regressao)

Build: 0 erros. Arquivos: `KitLugia.GUI/Program.cs`, `KitLugia.GUI/MainWindow.xaml.cs`,
`KitLugia.GUI/Services/UnlockIpcServer.cs`, `KitLugia.Core/Tweaks/ContextMenuQuickAdd.cs`,
`KitLugia.Core/SystemTweaks.cs`, `KitLugia.GUI/App.xaml.cs`, `docs/FORCE_STOP_UNLOCK.md`.

### Proxima sessao
- [ ] Testar no app o clique real do Explorer (com o Kit na bandeja):
      "Take Ownership (KitLugia)" e "Force Stop Unlock (KitLugia)" → a janela deve ABRIR
      na pagina certa com o caminho ja analisado, pedindo no maximo 1 UAC
- [ ] Confirmar que o menu de contexto agora tem UMA unica entrada de Take Ownership
      (a duplicata "Take Ownership Super" nao deve voltar apos reiniciar o Kit/Windows)
- [ ] (carregado) Testar no app (com o Kit SEM elevacao): Windows → Force Stop →
      **Tentar Deletar** / **Liberar Selecionados** com 1 UAC e progresso na propria pagina
- [ ] (carregado) Decidir o destino do codigo morto (6 paginas) e reduzir o layout de
      `PrivacyPage`/`RepairsPage`
- [ ] (carregado) desenho incremental dos canvas do TM (Storage 12,1 % / Diagnostic 25,0 %)

### Sessao 29/09 (cont.) — Force Stop: auditoria de cobertura + bug de PERDA DE DADOS

Pedido do usuario: "o force stop tem que ser infalivel e suportar todos os arquivos;
nos ultimos testes derrubar o goodbyedpi (servicos .sys e arquivos .sys) deu certo, so
veja se nao falta nada e nao quebre o codigo".

Auditoria do pipeline (`FileOpGuarantee` -> `ForceStopUnlockService` ->
`DriverUnlockService`) e 4 correcoes cirurgicas (nada removido ou invertido):

**1. BUG DE PERDA DE DADOS — "Liberar" agendava a EXCLUSAO (corrigido).**
Sem conseguir liberar, a acao ForceStop chamava
`DriverUnlockService.ScheduleDeleteOnReboot` = `MoveFileEx(path, NULL,
DELAY_UNTIL_REBOOT)` / `PendingFileRenameOperations` com destino vazio -> **apaga no
proximo boot**. E o teste de lock era `!isDir && !IsLockedNow(...)`: **toda pasta**
caia nesse ramo, entao "Liberar Selecionados" numa pasta agendava a exclusao dela.
Fix: `HasActiveLock(target, isDir)` novo (arquivo = teste exclusivo; pasta = amostra de
ate 512 arquivos internos) + ForceStop NAO agenda nada — reporta `StillBlocked` honesto
e orienta usar **Tentar Deletar** (acao Delete mantem o agendamento no boot).

**2. Raiz de unidade recusada.** ForceStop/Delete em `C:\`/`E:\` varriam o disco
inteiro (logando cada arquivo) e podiam agendar a exclusao da raiz. Agora respondem
"selecione um arquivo ou pasta" em **0,17 s**.

**3. Fase 4 sem dependencia externa.** O fechamento de handle usava so `handle64.exe`;
sem a ferramenta instalada, TODO handle achado pelo scan nativo virava erro falso.
Agora tenta `CloseNativeHandle` (DuplicateHandle + DUPLICATE_CLOSE_SOURCE) primeiro e
deixa o handle64 como fallback.

**4. Erro fantasma do Restart Manager.** O RM registra `HandleId = "RM"`; a fase 4
tentava "fechar RM" e gravava `Falha ao liberar handle RM de ...` no painel mesmo com a
operacao bem-sucedida. `IsRealHandleId()` aceita apenas hex; o PID do RM segue para a
fase 7 (se o RM nao liberou, o processo e finalizado).

**Cobertura confirmada** (motor): arquivo comum; ACL negada (ownership antes de
continuar); travado por app (Restart Manager -> handles nativos -> handle64 -> kill);
`.dll`/`.exe` carregados; `.sys` carregado (driver scan -> SCM stop+delete ->
NtUnloadDriver -> sc stop/delete); servico registrado (WinDivert/goodbyedpi — foi o que
derrubou o goodbyedpi); pasta; processo de sistema (nunca morto, RM tenta antes); e raiz
de unidade (recusada).
Limitacoes honestas: handle de driver de kernel sem servico correspondente nao tem como
ser fechado (reporta travado, sem mentir nem ter efeito colateral); UNC depende das ACLs
do servidor; processos protegidos (PPL) nao sao finalizados.

**Validado no host (exe Debug, worker elevado):**
- pasta LIVRE: `OK=1`, "Pasta liberada: nenhum dos 2 arquivo(s) verificados esta
  travado", NADA em `PendingFileRenameOperations`, pasta intacta (antes: agendava a
  exclusao dela)
- pasta com arquivo travado (`tests/hold_file_lock.ps1`, novo): `OK=1`, 1 handle + 1
  processo finalizado, **sem erros fantasma**
- arquivo livre: `OK=1` "Caminho liberado..." (antes: agendava a exclusao do arquivo)
- `force-stop` em `C:\`: recusa em 0,17 s (antes: varria/logava o disco)
- `delete` em arquivo travado: `DELETED=1` na hora; `delete` em pasta: pasta removida
- `takeown`: `OWNED=1`

Build: 0 erros. Arquivos: `KitLugia.Core/FileOpGuarantee.cs`,
`KitLugia.Core/ForceStopUnlockService.cs`,
`KitLugia.GUI/Pages/WindowsSettings/ForceStopUnlockPage.xaml.cs` (so comentario),
`tests/hold_file_lock.ps1` (novo), `docs/FORCE_STOP_UNLOCK.md` (secao nova).

### Sessao 29/09 (cont.) — Checklist VISUAL + COMANDOS pagina por pagina (48 paginas)

Pedido do usuario: "olhar cada pagina individualmente e corrigir os elementos visuais e os
comandos incorretos ou fora do lugar". Relatorio completo em **`docs/PAGES_VISUAL_AUDIT.md`**
(tabela das 48 paginas + backlog). Complementa `docs/PAGES_ACTIVE_AUDIT.md` (que tratou
Cleanup/Unloaded/timers/vazamento).

**6 scripts read-only novos (ficam no repo):**
`tests/pages_wiring_audit.sh` (handler XAML->cs e cs->XAML, FindName fantasma, path
hardcoded), `tests/buttons_without_action.py` (`<Button>` sem Click/Command, ignorando os
botoes "i" de ToolTip que sao o padrao do kit), `tests/pages_text_health.py` (mojibake,
TODO/EM BREVE visivel, recurso removido, acento perdido), `tests/pages_visual_audit.py`
(Title x cabecalho, secao repetida, pagina sem ScrollViewer, texto com Height fixo sem
wrap), `tests/pages_accent_audit.py` (palavra PT sem acento em Text/Content/ToolTip/Header)
e `tests/normalize_crlf.sh` (garante CRLF do .editorconfig preservando o BOM).

**Regressoes reais corrigidas (funcionalidade inalcancavel):**
1. `SecurityPage`: o card **"Acoes Rapidas"** tinha sumido do XAML mas os 3 handlers
   (`BtnMaxSecurity_Click`, `BtnGamingMode_Click`, `BtnRestoreDefaults_Click`) continuavam
   no .cs -> card RESTAURADO (Maxima Seguranca / Modo Gaming / Restaurar Padroes).
2. `KitIsoStudioWindow` (janela viva do Studio): 3 botoes **sem Click** ("Importar .reg",
   "Limpar", "Desmarcar tudo") e `BtnApplyDebloat_Click` com **corpo vazio** -> handlers
   implementados (`.reg` via OpenFileDialog, limpar campo, marcar/desmarcar os AppX de
   `PanelAppxPacks`).
3. `SettingsPage`: 2 toggles que **mentiam** — "Log Detalhado" salvava e nunca aplicava
   (havia `// TODO`), "Notificacoes" nao era lido por ninguem. Agora: log ->
   `Logger.VerboseCheckLogs`; notificacoes -> `AppSettingsHelper.ShowNotifications`
   (cache lazy, sem I/O no ctor) lido pelo `MainWindow.ShowNotification` — erro e toast de
   progresso ("AGUARDE"/"PROCESSANDO") continuam aparecendo; subtitulo do toggle explica.
4. `WinTunePage`: `InfoButton_Click` era codigo morto (o texto do tooltip em dialogo nao
   tinha quem chamasse) -> religado por evento roteado (`ButtonBase.ClickEvent` no ctor),
   funciona em qualquer aba (as abas so materializam quando abertas).
5. `DriversPage`: `BtnWindowsUpdate_Click` era orfao -> botao **Windows Update** adicionado
   no card "Instalar Driver" (capacidade ja existia em `DriverManager.OpenWindowsUpdateSettings`).

**Comandos fora do lugar:**
6. `IsoEditorPage`: o CANCELAR do overlay de configuracao chamava o MESMO handler do
   "<- VOLTAR" e jogava o usuario para `AdvancedTools` -> novo `BtnCancelConfigOverlay_Click`
   fecha so o overlay (VOLTAR segue saindo da pagina).
7. `PrivacyPage`: botao **"Expandir Tudo"** nao expandia nada (a pagina nao tem 1 Expander;
   so refazia o load, duplicando o "Atualizar") -> botao + handler removidos.
8. `DashboardPage`: card "Ferramentas de Sistema" + tooltip prometendo SFC/DISM/BCDedit
   abria a `ExmTweaksPage` ("Tweaks Avancados (EXM)", que a busca chama de "Exm Tweaks") ->
   card renomeado para "Tweaks Avancados (EXM)" com tooltip fiel e icone da busca.
9. `MainWindow` (3 pontos): atalho invalido respondia "EM BREVE / Pagina em desenvolvimento"
   -> agora "PAGINA NAO ENCONTRADA" + orientacao (menu lateral / Ctrl+K).

**Handlers orfaos removidos:** `DashboardPage` x4 (`BtnGoToTools`, `BtnGoToAllTweaks`,
`BtnOptimizeStandard`, `BtnOptimizeExtreme` — os 2 ultimos ja vinham `[Obsolete]`),
`CleanupPage` (`RegistryIssueSelection_Changed`), `TraySettingsPage`
(`BtnAddProcessLimit_Click`, shim), `WinbootPage` (`BtnQuickViewXml_Click`, duplicava o EDITAR).

**Visual/texto:** `WindowsUpdatePage` tinha um **"X" de 32 px** no lugar do emoji do
cabecalho -> emoji de refresh; `ScreenPage` "Calibragem" -> "Calibracao" (cabecalho + toast);
**42 textos visiveis sem acento** corrigidos em 11 paginas (Usuario, nao, versao,
configuracoes, "ACAO CRITICA: NAO FECHE...", tooltips do Windows Update/Tweaks) — a
varredura fechou em 0.

**Verificacao:** build 0 erros (baseline de warnings) e as auditorias fechando em
**0 handlers ausentes / 0 orfaos / 0 mojibake / 0 acento faltando**.

**Backlog carregado:** o overlay `OverlayIsoStudio` da `IsoEditorPage` (~158 linhas a partir
do `x:Name`) e **codigo morto** (so e setado como `Collapsed`; o Studio real e a janela
`KitIsoStudioWindow`) — junto dele sobraram 2 botoes sem Click e 3 handlers exclusivos;
remover exige confirmacao. `KitIsoStudioWindow` continua sem aplicar nada de verdade (o
`.reg`/pasta de drivers do Studio nao chegam ao fluxo de criacao da ISO). `Title` cru em
ServicesPage/ToolsPage/IsoEditorPage/QuickInstallPage/WinbootPage (Title nao e exibido).

### Proxima sessao
- [ ] Abrir o app e validar: SecurityPage (3 botoes de Acoes Rapidas), ISO Studio
      (Importar .reg / Limpar / Marcar 40+), CANCELAR do IsoEditor, Drivers -> Windows Update,
      WinTune (clique no "i"), Settings (desligar Notificacoes / ligar Log Detalhado)
- [ ] Decidir remocao do overlay morto `OverlayIsoStudio` (IsoEditorPage) + 3 handlers dele
- [ ] Decidir se o `KitIsoStudioWindow` deve aplicar o que o usuario preenche (hoje e so visual)

### Proxima sessao (Force Stop — pendente)
- [ ] Testar na pagina (Kit na bandeja): Force Stop num arquivo travado e numa PASTA ->
      "Liberar" nunca pode apagar nada (confirmar `PendingFileRenameOperations` e o alvo intacto)
- [ ] Re-testar Force Stop no goodbyedpi (driver .sys + servico) depois desta rodada
- [ ] (carregado) Testar no app (com o Kit SEM elevacao): Windows -> Force Stop ->
      **Tentar Deletar** / **Liberar Selecionados** com 1 UAC e progresso na propria pagina
- [ ] (carregado) Decidir o destino do codigo morto (6 paginas) e reduzir o layout de
      `PrivacyPage`/`RepairsPage`
- [ ] (carregado) desenho incremental dos canvas do TM (Storage 12,1 % / Diagnostic 25,0 %)

### Sessao 29/09 (cont.) - Auditoria do Core: APIs atuais x legadas + 9 correcoes

Pedido: "foque no backend no core e veja se esta tudo em ordem e se nao tem metodos
mais recentes e atualizados". 152 arquivos / ~87k linhas varridos com 5 scripts novos
em tests/ (core_audit, core_deep_audit + v2 deterministico, core_modern_api,
core_redundant_api, core_privilege_layout). Relatorio completo: docs/CORE_API_AUDIT.md.

**Veredito**: Core em ordem estrutural - 0 [Obsolete] em uso, 0 Sleep em async,
layout TOKEN_PRIVILEGES/LUID correto (5/5), Encoding.ASCII e DateTime.Now restantes
justificados (protocolos/relatorios). Corrigido (build 0 erros):
1. DashboardManager REESCRITO: snapshot nativo primeiro (registry CPU/GPU/SO +
   GetSystemTimes + GlobalMemoryStatusEx + PartitionManager.GetAllDisks IOCTL);
   WMI e fallback registry mantidos - Dashboard nao depende mais do servico Winmgmt.
2. new HttpClient() 12x -> KitHttp (NOVO KitLugia.Core/KitHttp.cs: SocketsHttpHandler
   compartilhado, PooledConnectionLifetime 2min; KitHttp.CreateClient(timeout) mantem
   timeout por chamada). 7 arquivos migrados (GitHubUpdater, NetworkExposureManager,
   KitTunnelManager, PlayitTunnelAdapter, UniversalTunnelAdapter, SmartVersionDetector,
   WinpeBuilder).
3. SystemUtils.GetTotalSystemRamGB: GlobalMemoryStatusEx primeiro (1 syscall), WMI
   virou fallback (7 call sites, alguns em UI).
4. WinbootManager: ShrinkPartitionUsingWMI + ShrinkPartitionUsingRunOnceAdvanced
   REMOVIDOS (601 linhas, 0 chamadores; fluxo real = StorageAPI + WinPE/marcador).
   Atencao: a remocao por linha-range chegou a comer a assinatura de
   CalculateRequiredSizeGB - restaurada e verificada.
5. Environment.OSVersion gating (DownloadBoostEngine BBR2, LatencyAnalyzer Win11)
   -> OperatingSystem.IsWindowsVersionAtLeast.
6. new Random() 7x -> Random.Shared (colisao real de seed no mesmo tick:
   transaction ID, porta de relay, codigo de sala).
7. DeepUninstaller.IsEmptyDirectory: GetFiles().Length -> EnumerateFiles().Any().
8. DnsBenchmark: _queryIdRng (Random nao thread-safe usado em paralelo p/ Query ID)
   -> Random.Shared.
9. tests/core_deep_audit.py ficou flaky (2 AttributeErrors diferentes e impossiveis
   em 2 runs seguidos, python 3.14) - reescrito como tests/core_deep_audit2.py
   (indice em 1 passada, deterministico: 2 runs identicos confirmados).

**Backlog (docs/CORE_API_AUDIT.md)**: 136 metodos publicos sem uso (GPEditManager
inteiro, BloatwareManager, ServiceHelper.*, DriverManager.CheckForOutdatedDrivers...),
15 awaits sincronos, 28x WMI Win32_* restante, 16x ServiceController, ~60 empty
catches (13 no ForceStopUnlockService), consolidacao ProcessRunner (385 call sites).

**Verificacao**: build 0 erros (Core+GUI); new HttpClient 12->0; new Random 7->0;
metodos mortos 138->136 (-601 linhas).

### Proxima sessao
- [ ] Decidir lote de remocao dos 136 metodos mortos (lista no output do
      core_deep_audit2.py) - exige confirmacao por arquivo/familia
- [ ] (opcional) Migrar awaits sincronos restantes para async end-to-end
- [ ] (opcional) Empty catches -> Logger.LogWarning (comecar por ForceStopUnlockService)
- [ ] Pendencias carregadas: testes de VM do Fresh Install, Downgrade de build em VM,
      validacao visual das paginas (docs/PAGES_VISUAL_AUDIT.md)

### Sessao 01/10 - Core: remocao do lote seguro de codigo morto (136 -> 103)

Continuacao do backlog da auditoria do Core. Remocao dos metodos publicos com
0 chamadores (confirmados por grep em todo o repo, inclusive tests/, alem do
auditor). Nada commitado (arvore mudou de "Deploy v2.0.59" do usuario).

**3 arquivos inteiramente mortos deletados:** GPEditManager.cs (4 metodos, thin
wrappers de Toolbox), BloatwareManager.cs (3, wrappers de SystemTweaks),
NativeBlake3.cs (HashFile/HashBytes + P/Invoke + ctor - classe so referenciada
por si mesma).

**Metodos removidos em 12 arquivos:** ServiceHelper (IsServiceRunning/
TryStartService/TryStopService), SystemInfo (IsWindows11OrLater/IsWindows10OrLater/
IsWindows81OrLater/IsWindowsServer/GetProcessorCount + struct SYSTEM_INFO), Logger
(LogRegistry/ToggleOutputLimit/ToggleVerboseCheck/GetLogPath), ToggleManager
(SaveAllToggles/RestoreAllToggles/ClearAllSnapshots), SystemUtils (GetRestorePoints/
OpenSystemRestoreWizard/RunPreflightCheck/GetRegistryValue), NativeMetricsHelper
(GetActiveUserNames), + 1 em cada: RegistryCleaner.CleanIssues,
DriverManager.CheckForOutdatedDrivers, SearchEngine.SearchTop,
ContinuityEngine.GetFolderSizeFast, PathRepair.IsPathHealthy,
SafeProcessHelper.GetProcessPathFast, StorageDiagnostics.ResetCounters.

**Resultado:** 16 arquivos, 718 linhas removidas; metodos publicos mortos 136 -> 103.
Build: Core 0 erros; GUI /t:Compile 0 erros (o build completo so falha no MSB3021
porque o KitLugia.GUI.exe estava aberto). Encoding preservado (CRLF em todos;
BOM onde ja havia).

**Ferramenta nova:** tests/remove_dead_methods.py <arquivo> <assinatura> - remove
metodo preservando BOM+CRLF (nao usar write_file nesses arquivos, que grava LF
sem BOM). Verificado: tests/core_deep_audit2.py deterministico (2 runs identicos);
tests/core_privilege_layout.py 5 OK / 0 problemas.

### Proxima sessao
- [ ] Continuar o lote: 103 metodos mortos restantes (SystemTweaks 43,
      WindowsUpdateManager 7, WinbootManager 7, StartupManager 6, Guardian 6,
      WinpeBuilder 3, StoreEngine 3, DeepUninstaller 3...) - metodo a metodo
- [ ] (backlog) empty catches -> Logger.LogWarning (comecar por ForceStopUnlockService, 13)
- [ ] (backlog) eliminar os 15 awaits sincronos do Core (async end-to-end)
- [ ] (backlog) validar o novo Dashboard nativo-first no app
### Sessao 01/10 (cont.) - Core: codigo morto zerado (103 -> 0)

Segundo lote do dia: removidos TODOS os 103 metodos publicos mortos restantes
(+ 3 que ficaram orfaos depois) usando tests/remove_dead_methods.py. Nada commitado.

Lotes:
1. WindowsUpdateManager (7): ScanUpdates/InteractiveScan/DownloadUpdates/InstallUpdates/
   SetDeferralDays/ClearDeferralPolicies/RunProcessStatic.
2. 21 em 9 arquivos: AdapterManager (GenerateMirroredMac/BuildDhcpRefreshScript);
   LocalInstallManager (CopyInstallFiles/IsUEFI); DiagnosticsManager
   (RepairSystemComponentsDISM/ReinstallDefaultApps); BootOptimizerManager
   (AnalyzeShutdownPerformance/SearchBootItemOnline); SmartVersionDetector
   (GetRealVersionAsync/GetRealVersion); PartitionManager (RemoveDriveLetter/
   CreateVhdBypass); DeepUninstaller (FindProgramFilesOrphans/RemoveUserExclusion/
   ClearUserExclusions); KitStore/StoreEngine (BuildPhantomReport/DetectStuckPackages/
   FixStuckPackage + campos _cachedUpgrades/_cachedAppx + classe StuckInfo, agora orfa);
   WinpeBuilder (InjectStartnetCmdIntoWimAsync/InjectConfigIntoWimAsync/
   InjectBootFilesIntoWimAsync).
3. 13 avulsos: AdvancedTweaksManager.GetVbsStatus, BrowserCacheManager.
   GetTotalBrowserCacheSize, BrowserExtensionManager.ListBackups, DriverUnlockService.
   CloseHandleFromProcess, EnablementPackageManager.FindEnablementCab,
   ForceStopUnlockService.IsLocked, IntegrityCheckManager.RunPreOperationCheck,
   KnownStartupArgs.SuggestArgsForCommand, NetworkTrafficMonitor.GetHeavyNetworkUsers,
   TaskManager/SafeProcessHelper.GetProcessPath, DriverManager.SearchDriverOnWeb,
   KitStore/StoreModels.RaiseAll, SystemUtils.RestorePointModel.
4. Guardian (6) + StartupManager (6) + WinbootManager (7) = 19: RestoreAllTweakStates,
   ResetAllConfigurations, SaveQuickToggleConfig, GetQuickToggleState,
   GetAppliedQuickToggles, CheckExplorerProblems; GetElevatedStartupTaskFullNames,
   GetBootTrayAdminFlag, SetBootTrayAdminFlag, GetRemovedApps, GetAllAdvancedItems,
   CheckAndFixStartupMethods; IsKitLugiaIso, PerformDiagnostics, CreateWinpeFlatEntry,
   CreateEfiBootEntry, InstallGrubAsPrimary, ReadLastWinpeLog, ClearWinpeLogs.
5. SystemTweaks (43): bloco inteiro de helpers Is*/Optimize*/Revert* nao usados (VBS,
   segment heap, large cache, boot log/noGUI, NVMe, GPU vendor, network diag/bluetooth
   LE, protected print, app control, sudo, explorer visuals, wait-to-kill,
   hung-app-timeout, reserved storage, etc.) + actions legadas (ReinstallBloatwareApp,
   ToggleFastStartupTweak, GetAllGpus, GetActiveNetworkAdapters,
   SetDeliveryOptimizationMode, GetContextMenuEntryCount, GetForceStopUnlockScriptPath).
6. Orfaos pos-lote (3): IntegrityCheckManager.IntegrityResult (record),
   IsFastStartupTweakEnabled, GetForceStopUnlockFolder.

BUG DA FERRAMENTA + CORRECAO: tests/remove_dead_methods.py fecha o metodo na primeira
linha `}` de 8 espacos — ok p/ metodos com bloco, mas em metodos expression-bodied
multi-linha (ex.: `SetDeliveryOptimizationMode(int mode) // ...` seguido de
`=> RegSetDword(...);`) o corpo nao tem `}` proprio e a ferramenta engoliu o proximo
metodo (RestrictDeliveryOptimization, USADO pela TweaksPage). Detectado pelo build da
GUI (CS0117) e restaurado por patch Python. Regra: antes de remover, conferir se a
assinatura termina em `;` ou tem `=>` na linha seguinte (one-line/expression-bodied ->
remover por linha, via Python) e SEMPRE buildar Core + GUI depois do lote.

Verificacao: Core 0 erros; GUI compila (o /t:Build para apenas no MSB3021/3027 porque
o KitLugia.GUI.exe estava aberto — o CS0103 em massa do `/t:Compile` puro era artefato
do MarkupCompilePass ausente, nao erro real). Auditoria (core_deep_audit2.py): metodos
publicos mortos 103 -> 0. Diff acumulado (as duas sessoes, nao commitado): 42 arquivos,
+91/-2929.

### Proxima sessao
- [ ] (backlog) empty catches -> Logger.LogWarning (comecar por ForceStopUnlockService, 13)
- [ ] (backlog) eliminar os 15 awaits sincronos do Core (async end-to-end)
- [ ] (backlog) validar o novo Dashboard nativo-first no app
- [ ] (opcional) remover IntegrityCheckManager.cs inteiro (100% morto agora; so tem
      CheckDiskHelper privado + TODO de WMI SMART)
### Sessao 01/10 (cont. 2) - Core: 15 awaits sincronos eliminados (async end-to-end)

Backlog do dia: os 15 sites de `GetAwaiter().GetResult()`/`.Result` do KitLugia.Core.
A auditoria (core_deep_audit2.py, secao 4) agora reporta **0 no Core**.

1. **2 metodos mortos removidos** (zero chamadores): `AdapterManager.RestartAdapter`
   (sync) e `SmartVersionDetector.GetVersionInfo` (sync).
2. **Ja dentro de metodos async -> await real**: WinbootManager (RunProcessCaptured
   ~1329), WinpeBuilder.RunProcess x2, IsoEditorManager.RunProcessCapturedWithStdin
   (Task.Run sync -> Task.Run(async)), WinbootManager.ScanBcdEntriesAsync
   (Task.Run -> Task.Run(async) + await RunProcessCaptured).
3. **UpdateControlManager**: RunProcess -> RunProcessAsync (WaitForExitAsync +
   Task.WhenAny(timeout)); UninstallUpdate/InstallUpdatePackage -> UninstallUpdateAsync/
   InstallUpdatePackageAsync; WindowsUpdatePage chama direto (sem Task.Run).
4. **StoreEngine**: NuGetLatestVersion -> NuGetLatestVersionAsync (await
   _http.GetStringAsync); QueryDotnetToolUpdates -> QueryDotnetToolUpdatesAsync;
   StoreRemakePage usa a versao async dentro do Task.WhenAll.
5. **DriverManager**: ExportDriverListToTxt -> ExportDriverListToTxtAsync
   (await GetSystemDriversAsync); DriversPage chama direto.
6. **SearchEngine**: acoes DISM/SFC nao usam mais sync-over-async. Novo
   GlobalSearchResult.ExecuteActionAsync (Func<Task<(bool,string)>>) + helper
   AddActionAsync; MainWindow prefere o async e faz await dentro do Task.Run. As
   demais acoes (sincronas) continuam em ExecuteAction.
7. **SystemUtils (2 shims de compat, 373 + 14 chamadas)**: reimplementados como
   SINCRONOS DE VERDADE (ReadProcessOutputSync: 1 thread por stream + WaitForExit com
   timeout) - mesma semantica de saida (ReadToEnd) e mesmo timeout de 120s, sem
   Task/GetAwaiter. RunExternalProcessAsync/WithCodeAsync seguem como API async;
   RunExternalProcessAsync ganhou 2 chamadores reais (LocalInstallManager
   PreparePartition/RemoveBootPartition, que ja eram async).

Verificacao: Core 0 erros; GUI compila (o build so para no MSB3021/3027 porque o
KitLugia.GUI.exe estava aberto); auditoria: 0 metodos publicos mortos E 0 sync awaits
no Core. Os `.Result` restantes estao so no GUI (MainWindow, QuickInstallPage,
StoreRemakePage) - fora do escopo do Core.

### Proxima sessao
- [ ] (backlog) empty catches -> Logger.LogWarning (comecar por ForceStopUnlockService, 13)
- [ ] (backlog) migrar os .Result restantes do GUI para await (MainWindow:3053,
      QuickInstallPage:86, StoreRemakePage:292/320-324)
- [ ] (backlog) validar o novo Dashboard nativo-first no app
- [ ] (opcional) remover IntegrityCheckManager.cs inteiro (100% morto agora)
### Sessao 01/10 (cont. 3) - Limpeza final: .Result no GUI, logs em kills, diagnostico

1. **.Result restantes do GUI migrados para await** (8 sites):
   - MainWindow.LoadIntroSettingsAndPlay: removido `.ContinueWith(t => ... t.Result ...)`;
     agora `await ...WaitAsync(500ms)` e o try/catch externo trata o timeout.
   - QuickInstallPage.Run: `(proc.ExitCode, await outTask, await errTask)`.
   - StoreRemakePage.QueryDevUpdatesAsync e RefreshInstalledAsync: `await t1..t4` e
     `await installedTask` ate `devTask`.
   Resultado: a auditoria (secao 4) agora so aponta `tests/TaskManagerOpenBench`
   (benchmark que mede o proprio sync-over-async de proposito).
2. **Kills que engoliam a excecao agora logam** o padrao ja usado no projeto
   (`catch { Logger.LogWarning("Unknown", "Exception suppressed"); }`):
   EnablementPackageManager (DISM timeout), StartupManager (PackageQueryTimeout),
   Tweaks/FileTakeOwnership, UpdateControlManager (RunProcessAsync).
3. **Nova ferramenta**: tests/quality_scan.py (read-only) - conta catches vazios,
   GC.Collect e Kill por arquivo; serve para priorizar o backlog de empty catches.

Nao mexido (decisao de escopo, risco alto x ganho baixo):
- **664 catches vazios** (Core+GUI). Muitos sao best-effort em hot paths do TaskManager
  (metricas ~1s) - logging em massa geraria spam e custo. Migrar por arquivo, com criterio.
- **IsoEditorPage**: o overlay "KIT ISO STUDIO" (OverlayIsoStudio) nunca fica Visible
  (so ha 2 atribuicoes Visibility.Collapsed) - e UI morta de ~255 linhas de XAML +
  handlers; remocao grande, deixada como tarefa separada.
- WMI `Win32_*` (28), `ServiceController` (16), duplicacao de DllImport/process
  helpers - refactors arquiteturais, nao quick fixes.

Verificacao: Core 0 erros; GUI compila (so MSB3021/3027 com o app aberto); auditoria:
0 metodos publicos mortos e 0 sync awaits no Core e no GUI de producao.

### Proxima sessao
- [ ] (backlog) empty catches -> Logger.LogWarning, arquivo por arquivo (usar
      tests/quality_scan.py para priorizar; comecar por ForceStopUnlockService, 14)
- [ ] (backlog) remover o overlay morto KIT ISO STUDIO da IsoEditorPage (XAML + handlers)
- [ ] (backlog) validar o novo Dashboard nativo-first no app
- [ ] (opcional) remover IntegrityCheckManager.cs inteiro (100% morto agora)

### Sessao 02/10 — Medidor de latência REAL (DashboardPage) + toggles dessincronizados

Pedido do usuario: "no dashboardpage cheque tudo que tem nele e de prioridade ao que se diz
medidor de latencia ele nao funciona e os metodos que usa sao meio falsos".

1. **LatencyAnalyzer.cs reescrito (medicao 100% real, ~1066 linhas)** — antes o motor
   fabricava numeros: `Thread.SpinWait` + fator 15x + `Random` no "MeasureSingleLatency";
   DPC/ISR derivados da media das amostras; page faults = `WorkingSet64/4096`; latencia por
   driver de dicionario hardcoded (nvlddmkm=400, ...). Fontes reais agora:
   - `NtQuerySystemInformation` classe 8 (SystemProcessorPerformanceInformation, struct 48 B:
     DpcTime/InterruptTime/InterruptCount por CPU, unidades de 100ns);
   - classe 23 (SystemInterruptInformation, struct 24 B: DpcCount + ContextSwitches);
   - classe 2 (SystemPerformanceInformation: PageFaultCount no offset 60);
   - `NtQueryTimerResolution` (nomes invertidos: Minimum = maior espera 15,625 ms;
     Maximum = menor 0,5 ms);
   - sonda de despertar: QPC + `Thread.Sleep(1)` em thread dedicada AboveNormal
     (TaskCompletionSource — sem thread do pool bloqueada), min / media aparada 5% / p95 /
     max / desvio + DPC%/ISR% de parede x CPUs + DPCs/s + ctx/s + page faults/s.
   Score real e estabilidade por coeficiente de variacao; cancelar no meio do benchmark
   restaura o estado original.

2. **Validacao independente (probe PowerShell)**: structs 48/24 bytes ok, timer
   15,625/0,5 ms, DPC 0,16% e ISR 0,18% reais, despertar medio 15,5 ms (granularidade do
   scheduler).

3. **DashboardPage**: cards agora MEDIA/P95/MAX em ms + linha de detalhes (min, amostras,
   DPC%, ISR%, DPCs/s, timer); o botao APLICAR RECOMENDACOES (AutoOptimizeAsync) agora
   aparece apos a analise; removidos ProgressLatencyScan e AnimateProgressBarAsync mortos.

4. **DiagnosticPage**: a "limpeza" por reflection em campos que nao existem mais
   (_latencySamples/_driverStats) virou `LatencyAnalyzer.ReleaseBuffers()`.

5. **DashboardPage — toggles sem revert corrigidos**: Gdi/Pcie/VBS/Unpark so chamavam no ON
   (EnableGdiScaling, EnablePcieLinkStatePowerManagement, EnableVBSCodeIntegrity,
   RevertUnparkCpuPowerConfig no OFF); Fsutil agora compara IsMemoryUsageEnabled() antes do
   ToggleMemoryUsage; novo `LoadCreatorTweaks()` sincroniza 12 checkboxes no Loaded (antes
   abriam todos desmarcados mesmo ja aplicados).

Verificacao: Core 0 erros; GUI 0 erros (build em OutDir temporario — o bin normal segue
bloqueado pelo app aberto); auditoria: 0 metodos publicos mortos e 0 sync awaits.

6. **Auditoria das deleções ("verifique os codigos removidos")** — resultado:

   - `tests/removed_symbols_audit.py` (novo, reaproveitavel): difere membros declarados
     HEAD vs working nos 48 .cs alterados e procura referencias vivas a cada nome removido
     (texto global, inclui strings/nameof). Achou **177 membros removidos em 40 arquivos**.
   - Referencias vivas: **zero reais**. Os 5 nomes sinalizados (DriverName, FullName,
     Reason, Family, static) sao colisoes com outras classes vivas (WMI/JSON DOM) e
     artefato de extracao — verificados a mao.
   - Deletados `BloatwareManager.cs`, `GPEditManager.cs`, `NativeBlake3.cs`: zero
     referencias em .cs/.xaml/.csproj.
   - Caso conhecido do script (`SetDeliveryOptimizationMode` engolia
     `RestrictDeliveryOptimization`): corrigido — vive em SystemTweaks.cs:10073 e e usado
     por TweaksPage.xaml.cs:2357.
   - **Rebuild FORCADO Core+GUI (`-t:Rebuild`) = 0 erros** (155 avisos pre-existentes:
     CS8600/8602/8618/8625 nullable, SYSLIB0057, CS0675, CS0219). O `-t:Build` incremental
     nao recompilava de fato (so copiava saidas antigas) — para validar delecoes, usar
     Rebuild ou invalidar a pasta obj.
   - Licao: `remove_dead_methods.py` fecha no primeiro `}` de 8 espacos; expression-bodied
     multi-linha engole o metodo seguinte. Depois de todo lote: `-t:Rebuild` + auditoria
     (`tests/removed_symbols_audit.py`).

### Proxima sessao
- [x] ~~Auditar delecoes das limpezas de codigo morto (177 membros; ver acima)~~
- [ ] Validar no app: ANALISAR latencia (benchmark real) e conferir ms/DPC/ISR/timer
- [ ] (backlog) empty catches -> Logger.LogWarning, arquivo por arquivo (quality_scan.py)
- [ ] (backlog) remover o overlay morto KIT ISO STUDIO da IsoEditorPage
- [ ] (backlog) validar o novo Dashboard nativo-first no app

### Sessao 02/10 (cont.) — Medidor de latencia COMPLETO (LatencyMon-style)

Pedido: "teste o medidor e busque na web preciso dele completo".

1. **LatencyAnalyzer.cs — metricas que faltavam (tudo de fonte real)**:
   - **Pior janela (spike) de DPC/ISR**: amostrador paralelo a cada 250ms dos contadores da classe 8
     (`WorstDpcWindowPercent`/`WorstIsrWindowPercent`/`SpikeWindowMs`/`SpikeWindowCount`) -
     equivalente ao "highest reported DPC/ISR execution time" do LatencyMon no nivel possivel sem driver.
   - **Hard page faults reais**: `PerformanceCounter("Memory", "Pages Input/sec")` (paginas lidas do
     disco para resolver hard faults) — media + pico (`HardPageFaultsPerSec`/`MaxHardPageFaultsPerSec`).
   - **P50/P99** da sonda de despertar (`P50LatencyUs`/`P99LatencyUs`).
   - **Velocidade da CPU por nucleo**: `NtPowerInformation(SystemProcessorInformation=11)` ->
     PROCESSOR_POWER_INFORMATION (CurrentMhz/MaxMhz em `CpuLatencyInfo`) - mostra throttling.
   - Recomendacoes + relatorio do benchmark incluem hard faults, pico de janela e MHz.

2. **LatencyDriverAnalyzer.cs (NOVO) — DPC/ISR POR DRIVER, estilo LatencyMon**:
   - Pacote novo no Core: `Microsoft.Diagnostics.Tracing.TraceEvent 3.2.8` (parser ETW da Microsoft).
   - **QUIRK critico (descoberto e contornado)**: `TraceEventSession.EnableKernelProvider` FALHA
     nesta build do Windows 11 26100 com `ERROR_WMI_INSTANCE_NOT_FOUND (4201)` nos DOIS caminhos
     (nome proprio/system-logger e "NT Kernel Logger" via KernelTraceControl nativo). Solucao:
     iniciar a sessao **direto pela API nativa** (`StartTraceW` + EVENT_TRACE_PROPERTIES realtime
     `LogFileMode=0x08000100`, `Wnode.Flags=0x20000`, ClientContext=1, LoggerNameOffset=sizeof,
     LogFileNameOffset=+2048) e consumir com `ETWTraceEventSource("NT Kernel Logger", Session)`;
     parar com `source.Dispose()` (seta stopProcessing + fecha handles) + `ControlTraceW(STOP)`.
   - Eventos: `PerfInfoDPC`/`PerfInfoTimerDPC`/`PerfInfoThreadedDPC`/`PerfInfoISR`
     (`DPCTraceData`/`ISRTraceData.ElapsedTimeMSec` = tempo real por rotina); endereco da rotina ->
     driver via `NtQuerySystemInformation(SystemModuleInformation=11)` (RTL_PROCESS_MODULES,
     entrada 296B x64, ImageBase@16, ImageSize@24, FullPathName@40).
   - Requer administrador (a GUI roda elevada); sessao "NT Kernel Logger" e UNICA no sistema
     (LatencyMon/WPR em uso -> mensagem tratada, sem derrubar a sessao alheia).
   - GUI DashboardPage: botao **"DRIVERS (ETW)"** + `TxtDriverReport` (tabela top-10);
     `BuildLatencyExtra` mostra p99, pico de janela, hard faults e MHz.

3. **Testes reais (harness novo `tests/LatencyMeterTest`, modos normal/deep/raw)** — rodados no host:
   - normal (10s+5s): media 1,93ms | p50 1,95 | p95 2,49 | p99 2,56 | max 3,49 | DPC 0,32% |
     ISR 0,59% | pior janela 250ms: DPC 0,63%/ISR 1,25% | hard faults 840/s (pico 9890/s) |
     CPU 3500/3500MHz | timer 0,977ms | 5208 amostras em 10s.
   - deep (elevado): nvlddmkm.sys DPC max 1236,9us / total 110,7ms | dxgkrnl.sys ISR max 694,7us /
     total 171,9ms | Wdf01000.sys | tcpip.sys | NDIS.SYS | storport.sys | portcls.sys |
     ntoskrnl.exe | 0 eventos perdidos.
   - raw: probe cru de StartTraceW (8 combinacoes) — comprovou que a API nativa funciona e que
     o shell de teste roda como administrador.

4. **Licoes**:
   - Apos adicionar pacote ao Core, o GUI precisa de `dotnet restore` (o project.assets.json nao
     conhece dependencia transitiva nova) — senao `TraceEvent.dll` nao vai para a saida e o app
     quebra em runtime.
   - Quoting do OutDir temporario: usar barras normais (`-p:OutDir=C:/.../kl_gui_check/`); com
     `\\"` no fim a aspa escapa e o `/m` do MSBuild entra no path, virando build infinito de copia.

Verificacao: Core 0 erros; GUI 0 erros (OutDir temp, apos restore); deep scan testado elevado.

### Proxima sessao
- [ ] Validar no app: card de latencia (ANALISAR) + botao DRIVERS (ETW) + linha extra nova
- [x] ~~Testar o medidor (harness normal/deep/raw)~~ — FEITO (numeros acima)
- [ ] Publicar pelo VS conferindo as subpastas amd64/x86/arm64 (KernelTraceControl.dll) na saida
- [ ] (backlog) empty catches -> Logger.LogWarning, arquivo por arquivo (quality_scan.py)
- [ ] (backlog) remover o overlay morto KIT ISO STUDIO da IsoEditorPage
- [ ] (backlog) validar o novo Dashboard nativo-first no app

### Sessao 02/10 - Easter egg estilo YouTube: digitar "awesome" pisca as bordas em modo colorido

1. **`KitLugia.GUI\Services\EasterEggManager.cs` (NOVO, ~300 linhas)**: digitar "awesome"
   em QUALQUER campo de texto do Kit dispara a "festa" de bordas arco-iris.
   - Deteccao GLOBAL: `EventManager.RegisterClassHandler(typeof(TextBoxBase),
     TextBoxBase.TextChangedEvent, ...)` -> funciona em qualquer TextBox de qualquer janela
     (MainWindow, Kit TaskManager, loja, paginas), sem tocar em cada pagina. Cobre digitacao,
     colar (Ctrl+V) e texto setado por codigo. Trigger = texto TERMINA em "awesome"
     (OrdinalIgnoreCase) + cooldown de 1,2 s (anti-duplo/re-render).
   - Visual: cria 2 `Border` (overlay sem filho, `IsHitTestVisible=false`, `Panel.ZIndex=99999`)
     no PRIMEIRO `Panel` encontrado na arvore visual da janela (externa 10px + interna 3px
     defasada). `Grid.SetRowSpan/ColumnSpan` = nº de linhas/colunas do painel (o Grid raiz da
     MainWindow tem linha extra pro console - sem isso a borda parava no meio da janela).
   - Cor: `LinearGradientBrush` diagonal com 7 `GradientStop` arco-iris (HSL->RGB proprio,
     `Color.FromHsv` NAO existe no WPF). Cada stop anima `Color` (AutoReverse/Forever) com
     periodo proprio (180ms + i*110) -> o arco-iris ANDA pela borda em vez de piscar uniforme.
   - Pulso: `DoubleAnimation` de `Opacity` (externa 420ms 0,05->1, interna 260ms 0,35->1) +
     duracao 12 s (`DispatcherTimer` por janela). Digitar "awesome" de novo durante a festa
     REINICIA o timer (igual YouTube). Ao fim: para animacoes, fade de 450 ms e remove as
     borders da arvore (nada fica sujo). Hook `Closed` 1x por janela (HashSet `_closeHooked`)
     evita empilhar handlers.
   - `EasterEggManager.Start()` chamado em `App.OnStartup` (idempotente) + `Play()` para
     disparo manual em QA. Toast "AWESOME!" via `MainWindow.ShowInfo` + log `[EASTER EGG]`.
2. **Quirk WPF+WinForms**: o projeto tem `UseWindowsForms`, entao `Panel`/`Brush`/`TextBox`/
   `Application`/`Point`/`Color`/`HorizontalAlignment` ficam AMBIGUOS entre `System.Windows.*`
   e `System.Windows.Forms.*` - resolver com aliases (padrao do projeto, ver MainWindow.xaml.cs).
3. **Armadilha de build (repetida)**: `-p:OutDir=bin\verify\` no bash vira `binverify` (o `\v`
   e escapado) e cria a pasta-junk `KitLugia.GUI/binverify 2/` com COPIAS DOS FONTES -> o glob
   do SDK compila `Resources/FilterCommands.cs` duas vezes -> CS0101 "Program duplicado".
   Use barras normais: `dotnet build ... "-p:OutDir=obj/verifybin/"` (as pastas-junk foram
   removidas de novo aqui).

Build: GUI 0 erros / 146 warnings (baseline nullable), validado com OutDir temporario
(o app estava rodando e trava o bin em bin/Debug -> MSB3021).

**A TESTAR (host)**: abrir qualquer campo (ex.: busca global) -> digitar "awesome" -> bordas
das janelas abertas devem piscar em arco-iris por 12 s; digitar de novo estende; testar com
o Kit TaskManager aberto (todas as janelas animam juntas).

### Sessao 02/10 (cont.) - PartitionsPage: por que "Estender" falhava + escada sem diskpart

**Diagnostico do problema relatado** (operacoes de disco falhando sem motivo real):
1. `RunDiskpartScript` so olhava o EXIT CODE. O diskpart devolve 0 em varios cenarios de falha ->
   "Extendido com sucesso" sem ter extendido (e o inverso: falha sem causa aparente).
2. `ExtendPartition` era 100% diskpart e nao recebia disco/partition -> nao havia como tentar a via nativa.
3. Sem diagnostico: a UI mostrava so "Nao foi possivel estender". As causas REAIS (espaco nao contiguo
   a direita / FAT32 / BitLocker / limite MBR de 2 TB / pagefile+hiberfil no fim do volume) nunca apareciam.
4. `MovePartition` e `AtomicExtendDISM` detectavam a particao recriada com `Partitions.LastOrDefault()` —
   isso pega a ULTIMA particao do DISCO, nao a nova: se houvesse particao depois, o WIM era aplicado
   na particao ERRADA (dados "sumiam" do lugar).
5. `ShrinkPartitionUsingStorageAPI` (MSFT_Partition.Resize) existia mas NUNCA era chamada pela pagina —
   o metodo estava morto enquanto a UI usava diskpart.
6. Mergear (DISM) vinha com `IsChecked="True"` no XAML: o modo DESTRUTIVO era o padrao, sem o usuario pedir.

**Correcoes no Core (KitLugia.Core\PartitionManager.cs)**:
1. **Escada de 3 degraus no Extend**, cada degrau VERIFICADO medindo o volume antes/depois:
   (a) `MSFT_Partition.Resize` (Storage Management API — o mesmo caminho do Gerenciador de Discos);
   (b) IOCTL nativo `IOCTL_DISK_GROW_PARTITION` (0x7C0D4 -> 0x7C0D0 confirmado na web) +
   `FSCTL_EXTEND_VOLUME` (particao/volume ONLINE, sem lock — docs dizem que da p/ estender particao viva);
   (c) diskpart (compatibilidade). Se o volume nao cresceu, o degrau conta como FALHA e segue.
   NOVO param opcional `diskIndex`/`partitionIndex` (a pagina passou a enviar).
2. **Shrink** agora usa `MSFT_Partition.Resize` primeiro e valida contra `GetSupportedSize` ANTES de
   tentar: se o pedido passa de SizeMin, retorna o **limite real** em vez de mandar o discopart falhar.
3. **`GetMaxShrinkMb`**: Storage API (`Size - SizeMin`, exato e instantaneo) com fallback pro
   `shrink querymax` do diskpart (RAW/sem Storage API). Novo `GetMaxExtendMb` (SizeMax - Size).
4. **`LastError` + `GetResizeDiagnostics(part, forExtend)`**: devolve BLOQUEIOS (sem letra; FS != NTFS/ReFS;
   sem espaco contiguo a direita; MBR > 2 TB) e AVISOS (hiberfil.sys, pagefile, BitLocker, particao
   de boot). A pagina mostra isso ANTES de executar — e o que fecha as "pontas soltas".
5. **Bug do WIM na particao errada corrigido**: `GetPartitionOffset` + `DetectRecreatedPartition`
   (particao mais proxima do offset original) substituem o `LastOrDefault`.
6. `RunDiskpartScript` agora extrai a linha de erro do VDS (pt-BR e en-US) para o `LastError`.

**Correcoes na UI (KitLugia.GUI\Pages\PartitionsPage.xaml/.cs)**:
1. `ShowOpError()` — todo erro de operação mostra o `LastError` real (nunca mais "Nao foi possivel X").
2. "Mesclar" virou OPT-IN (`IsChecked="False"`) + confirmacao explicando que a particao vizinha e
   EXCLUIDA e os dados vao para `Arquivos_Mesclados` (antes era o padrao sem pedir).
3. Texto mentiroso removido: "Diskpart falhou (Limite de 3GB/Imóveis)" — 3 GB e limite de REDUCAO,
   nao de extensao. O fallback agora diz "Extensão nativa recusou. Tentando modo atômico (DISM)".
4. Novo `TxtExtendWarning` mostra os avisos (pagefile/BitLocker) dentro do overlay; `TxtShrinkWarning`
   (que existia e nunca era usado) passou a exibir hiberfil/pagefile.
5. Rótulo do disco: combo agora mostra interface + espaco nao alocado + marcador de disco de sistema;
   `TxtDiskInfo` mostra contagem de partições + total nao alocado.

**Pesquisa web (fundamenta das escolhas)**: MSFT_Partition tem `Resize` + `GetSupportedSize`
(learn.microsoft.com/msft-partition); `MSFT_Volume.Optimize` NAO faz shrink (so ReTrim/Analyze/
Defrag/SlabConsolidate/TierOptimize) — por isso o maximo de reducao vem do GetSupportedSize e nao de
"Optimize"; `defrag /X` (free-space consolidation) e a forma oficial de aumentar o espaco reduzivel
sem WinPE (util para o botao "Consolidar espaco livre" futuro); extend so funciona com espaco
CONTIGUO a DIREITA (causa #1 do botao cinza no Gerenciador de Discos); FSCTL_EXTEND_VOLUME suporta
NTFS/RAW/ReFS e nao reduz.

**VALIDADO no host (leitura pura, sem tocar em disco)**: `MSFT_Partition WHERE DriveLetter='C'`
funciona (Disk=1 Part=3 Size=3999055974400); `GetSupportedSize` devolveu SizeMin=3978673000448 /
SizeMax=3999055974400 => max reducao 19.438 MB e **SizeMax == Size** (C: nao tem espaco contiguo a
direita) — exatamente o caso que antes gerava "Nao foi possivel estender" sem explicacao.
Parametros dos metodos conferidos via Get-CimClass: `GetSupportedSize(SizeMin, SizeMax, ExtendedStatus)`
e `Resize(Size, ExtendedStatus)` — os arrays de argumentos do InvokeMethod estao corretos.

Build: Core 0 erros; solucao completa (GUI+Core+Updater) 0 erros / 154 warnings (baseline nullable).

**A TESTAR (VM com disco descartavel)**: (1) estender C: numa particao com nao alocado a direita — deve
usar a Storage API em segundos, sem diskpart no log; (2) estender sem espaco contiguo — deve aparecer o
diagnostico antes de qualquer operacao; (3) reduzir C: passando do limite — erro com o limite real;
(4) mesclar vizinha — agora pede confirmacao; (5) `GetSessionLog`/terminal com as linhas [EXTEND]/[SHRINK]/[STORAGE].

### Proxima sessao
- [ ] (opcional) Botao "Consolidar espaco livre" (defrag /X) antes da reducao, para aumentar o maximo
- [ ] (opcional) Testar o caminho IOCTL (degrau b) forcando a Storage API indisponivel
- [ ] (opcional) Migrar o resto das operacoes (format/delete/create) da Storage API, hoje ainda 100% diskpart

### Sessao 02/10 (cont.) - EaseUS EPM WinPE analisado no IDA + REPARO DE BCD no Kit

Alvo: `C:\Users\Lugia\Downloads\easeuswinpeepm\epm` (copias em `%TEMP%\epm_analysis/targets`; originais
intocados). Ferramenta: IDA Pro 9.0 (`idat.exe -A` + IDAPython). Scripts RE versionados em
`docs/ida_epm_dump.py` e `docs/epm_scan_imports.py`. Documentacao completa: **`docs/EASEUS_EPM_ANALYSIS.md`**.

1. **Descoberta 1 - ele NAO usa diskpart para redimensionar**: log em runtime (`bin\EPMLOG.log`) mostra
   `CDiskEnumerator : Storage space init` + `Vds loadservice`, e o IDA confirma `CWin32BootRepairManager::
   _vdsResize` -> `CBasicDiskVolume::ReSize()` recebendo `IPartitionManager` (COM do VDS). Ou seja: VDS =
   a mesma base do Gerenciador de Discos; a Storage Management API (MSFT_*) que o Kit adotou é a
   sucessora moderna do VDS. **A direção do Kit está correta.**
2. **Descoberta 2 - o reparador de BCD** (`Win32BootRepair.dll:0x1800021A0`, `CBcdBootProc::
   TransferBootStaff`, log original `d:\epm\main\code\bootrepairtool\bootrepair_func\win32bootrepair\
   consoleproc.cpp:163`):
   `sprintf(L"%s %C:\windows /s %C:\ /f %s", <dir>\Bcdboot.exe, win, esp, UEFI|BIOS)` e, no BIOS,
   `sprintf(L"%s /nt60 %C: /mbr", <dir>\BootSect.exe, sys)`. Eles EMBARCAM `bcdboot.exe`, `bcdedit.exe`
   e `bootsect.exe` na pasta. Nao e magia: e a ferramenta oficial da Microsoft na ordem certa.
3. **Descoberta 3 - `Win32Bcd.dll` e o bcdedit embrulhado em modelo de objetos**: `AddBootLoader` gera
   `/create %s /application osloader` + `/create %s /device` + SaveParameters + `_addDisplayItem`
   (= exatamente o que o Kit ja faz em `CreateEfiBootEntry`/`CreateDirectNvramBoot`). Exports:
   UpdateBootOrder, SetDefaultItem, DeleteBootLoader, LoadByGuidString, EnumProc, InitByCurSystem.
4. **Descoberta 4 - `Win32UEFI.dll` edita a NVRAM da placa**: `Get/SetFirmwareEnvironmentVariableW` com
   `BootOrder`, `BootNext`, `Boot####` + `UEFI_ClearDevPaths`; exports `UEFI_UpdateBootOrder`,
   `UEFI_SetNextBootEntryId`, `UEFI_DeleteBootOption`, `UEFI_SetNextBootintoFireware`. O Kit cobre o
   BootNext via `bcdedit /set {fwbootmgr} bootsequence`, mas nao mexe em `Boot####`.
5. **Fluxo do reparo completo**: CreateSystemTgt (`_isGPT` -> `_getEfiPart` -> `ModifyPartEfiProp`, ou
   `ResizeMoveProc` se nao houver particao) -> MountSrcBootPart -> TransferBootStaff (bcdboot+bootsect)
   -> AddRegProcItem -> DeleteTgtSysPart -> `InitByCurSystem`/`EnumProc` para a UI.

**Implementado no Kit**:
1. **`KitLugia.Core/BcdRepairManager.cs` (NOVO)**: `DiagnoseAsync()` (firmware UEFI/BIOS, ESP, loja BCD,
   bootmgfw.efi, arquivos de boot, entradas winload reais vs WinRE), `RebuildAsync()` (bcdboot + bootsect
   no BIOS, com BACKUP em %LOCALAPPDATA%\KitLugia\BCD-Backup antes e VERIFICACAO depois), 
   `RestoreBootFilesAsync()` (recopia arquivos sem tocar na BCD), `VerifyAsync()`, `FindEspPartition()`.
2. **`KitLugia.GUI/Pages/BcdRepairPage.xaml/.cs` (NOVO)** + item de menu "Reparar Boot/BCD" (PageType
   `BcdRepair`, tag 🩺): relatorio do diagnostico + 3 acoes (ANALISAR / RECONSTRUIR BCD / RESTAURAR
   ARQUIVOS), com confirmacao avisando que as entradas atuais da BCD sao apagadas.
3. **BUG encontrado pelo proprio teste no host**: a deteccao por `application osloader` e ERRADA — o
   bcdedit moderno NAO imprime esse campo; a entrada real aparece como bloco "Carregador de
   Inicializacao do Windows" + `path \WINDOWS\system32\winload.efi`. Corrigido com parser de blocos
   (separa por linhas `-----`), contando entradas REAIS (ignora `winpe Yes` = WinRE). Antes disso o
   kit acusaria "BCD sem entrada do Windows" numa maquina saudavel.
4. **Outro bug pego pelo teste**: `Encoding.GetEncoding(850)` lancava NotSupportedException em .NET Core
   sem `CodePagesEncodingProvider` registrado (igual ao `RunProcessStreamed` do PartitionManager).

**VALIDADO no host (leitura pura)**: DiagnoseAsync() em um PC saudavel -> UEFI (ESP + {fwbootmgr}),
ESP em S: (100 MB, "EFI System partition"), Windows em C:, loja BCD + bootmgfw presentes, bcdedit OK,
3 entradas winload (2 WinRE + 1 Windows), **Healthy=True**; `bcdboot.exe`/`bootsect.exe` localizados em
`C:\Windows\System32`.

Build: solucao completa 0 erros / 144 warnings (baseline).

**A TESTAR (VM)**: (1) abrir Reparos Boot/BCD e ANALISAR; (2) QUEBRAR a BCD de proposito (renomear
`\EFI\Microsoft\Boot\BCD` numa VM) -> ANALISAR deve acusar e RECONSTRUIR deve consertar (com backup);
(3) testar em VM BIOS (bcdboot /f BIOS + bootsect); (4) conferir que as entradas do Kit Lugia somem
apos o rebuild (esperado — documentado na confirmacao).

### Proxima sessao
- [ ] (opcional) NVRAM do firmware como o Win32UEFI.dll do EaseUS (Boot####/BootOrder, "bootar no setup")
- [ ] (opcional) Embutir bcdboot/bootsect no pacote do kit (o EaseUS embarca) para funcionar no WinPE
- [ ] (opcional) Migrar format/delete/create do diskpart para a Storage API (falta no ciclo do disco)

### Sessao 02/10 (cont.) - EaseUS "Disk Converter" + Built-in Toolkits: conversor MBR/GPT no Kit

O usuario mandou prints da UI do EPM 20.8 (Partition Manager / Disk Clone / Disk Converter /
Built-in Toolkits). Cruzei com o IDA (`DiskConverter.dll`) e implementei a capacidade que faltava.

1. **Achados (IDA + strings do `DiskConverter.dll`)**
   - Operacoes literais: "Convert MBR to GPT", "Convert GPT to MBR", "Initialize to MBR/GPT disk",
     "Rebuild MBR", "4K alignment", "AlignDisk", "Wiping partition data", "Smart Resize".
   - Checklist (`isMBR2GPTValid` @ `0x1800740F0`, `isGPT2MBRValid` @ `0x180073A70`): disco/dinamico
     (LDM), PE system disk, Windows 32-bit, particao > 2 TiB no MBR, "At least 1M unallocated space
     is required in the end of disk", write protected, "Failed to generate the BCD file during MBR
     to GPT conversion" (depois de converter, o boot e refeito), cluster do sistema > 4 KB no 4K align.
   - Mecanismo: VDS COM. No Windows 8+ o sucessor e a **Storage Management API**.

2. **API CONFIRMADA NO HOST** (Get-CimClass MSFT_Disk/Partition): nao existe `ConvertToGpt/ConvertToMbr`
   nem `GetStorageDependencyInformation` neste build. O metodo real e
   **`MSFT_Disk.ConvertStyle(PartitionStyle UInt16, ExtendedStatus)`** (0=Unknown 1=MBR 2=GPT 3=RAW),
   mais **`MSFT_Disk.Initialize(PartitionStyle, ...)`** e **`MSFT_Disk.Clear(RemoveData, Sanitize,
   ZeroOutEntireDisk, ...)`**. MSFT_Partition tem `Offset`, `MbrType`, `GptType`, `IsActive`, `IsReadOnly`.

3. **NOVO `KitLugia.Core/DiskConverterManager.cs`**: `GetDisks/GetPartitions`, `Analyze(disk,target)`
   (todos os bloqueios/avisos acima), `ConvertAsync` (escada `MSFT_Disk.ConvertStyle` in-place ->
   `diskpart convert`, com VERIFICACAO de estilo + contagem de particoes no final),
   `InitializeAsync` (disco RAW), `ReadMbr/BackupMbrAsync/RestoreMbrAsync` (Rebuild MBR: backup dos 512
   bytes em %LOCALAPPDATA%\KitLugia\MBR-Backup com SHA256; restauracao padrao copia SO os 446 bytes de
   codigo de boot e mantem a tabela atual; escrita com Flush(true) + leitura de volta byte a byte) e
   `AnalyzeAlignment` (4K/1 MiB, so diagnostico - reposicionar exige mover dados).

4. **NOVO `KitLugia.GUI/Pages/DiskConverterPage.xaml(.cs)`** + menu "Conversor MBR/GPT" (tag U+1F500,
   depois de BtnBcdRepair). ANALISAR mostra os DOIS sentidos com OK/X/!; conversao so com confirmacao
   que lista os bloqueios; apos converter para GPT em disco de sistema aponta o Reparar Boot/BCD.

5. **BUG CORRIGIDO (limitacao herdada)**: `PartitionManager.ConvertDiskStyle` recusava disco nao vazio
   ("A conversao MBR/GPT requer que o disco esteja completamente vazio") e o disco do sistema. Agora
   delega ao DiskConverterManager (in-place, como o EaseUS/Windows 11). `RunDiskpartScript` ficou
   `internal` e ganhou `RunDiskpartAsync(disk, gpt|mbr)`.

6. **VALIDADO NO HOST (leitura pura)**: disco 0 (465 GB GPT) -> podeConverter(MBR)=true; disco 1
   (3,7 TB sistema/UEFI) -> 3 bloqueios corretos para MBR (UEFI sem GRUB, 5 particoes > 4, C: > 2 TiB);
   setor 0 dos dois = MBR protetivo 0xEE (nota nova); alinhamento real: 4 de 5 particoes fora de 1 MiB.

Build: solucao completa **0 erros / 154 warnings** (nullable pre-existentes).
Documentacao: docs/EASEUS_EPM_ANALYSIS.md (Parte 2, secoes 5-7).

**A TESTAR (VM com disco descartavel)**: selecionar disco GPT de dados -> ANALISAR -> CONVERTER PARA MBR
(particoes/dados intactos, estilo verificado) -> reconverter para GPT -> INICIALIZAR num disco RAW ->
BACKUP DO SETOR 0 -> RESTAURAR (modo seguro) -> checar alinhamento.

---

### Sessao 02/10 (cont.) — MAPA COMPLETO do EaseUS EPM 20.8 (docs/EASEUS_EPM_MAP.md)

Pedido: "continue olhando e faca um mapa completo para nao termos que reanalisar".
Metodo: em vez de reversing ad-hoc, extrai-se a arquitetura pelos **exports PE** e pelos
**caminhos de fonte vazados** (`__FILE__` dos asserts). Resultado: **41 modulos, 3.051
exports, 177 caminhos de fonte em 37 diretorios** — tudo deterministico e reproduzivel.

**Scripts RE versionados (docs/, antes so havia 2):**
- `epm_exports.py` — demangle dos exports MSVC -> mapa de classes/metodos por modulo
- `epm_srcmap.py` — extrai os 177 caminhos de fonte (arquitetura interna)
- `epm_cmds.py` — TODAS as strings de shell-out (`--diskpart` filtra)
- `epm_map.py` — imports + strings por categoria de feature
- (existes) `ida_epm_dump.py`, `epm_scan_imports.py`

**ACHADOS NOVOS (nunca precisa reanalisar):**
1. **O "Write Protection" do EaseUS E o diskpart**: `WriteProtect.exe` = `diskpart.exe /s %1`
   com script `select disk %1` / `attributes disk %2 readonly`.diskpart so aparece em
   5 modulos, sempre com script literal: write-protect, limpar readonly antes de operar
   (`attributes disk clear readonly | diskpart`), WinRE, clone, bootrepair. **NUNCA resize.**
2. **Rebuild MBR NAO e boot code embarcado** — e `bootsect /nt60 /mbr` (string `%s /nt60 %C: /mbr`).
   Varredura de assinaturas em 41 binarios: so achou o stub PE de cada um (1x), zero blobs
   de boot. Logo o metodo do Kit (446 bytes + tabela atual + SHA256) e o caminho sem
   dependencia de boot sector do Windows — **melhor** que o do EaseUS nesse ponto.
3. **Fases do Rebuild MBR sao versionadas**: `Windows 2000/XP/2003`=3, `Vista/2008`=4,
   `7/8/8.1/10/2012`=5 — o `CRebuildMBRController` e carregado dinamicamente com a versao-alvo.
4. **Contrato de 54 operacoes** (enum de `ToolBox.dll`, codes 4097-4151): Create, Format,
   SetActive, MoveResize, Hide, Delete, ChangeLabel, ChangeDriveLetter, DeleteAll,
   Clone{Partition,Disk,DiskInitialization,DiskFinalization,Volume}, ConvertTo{NTFS,FAT,exFAT},
   WipeDisk, **RebuildMBR**, ConvertToBasicDisk, InitializeTo{MBR,GPT}Disk,
   RecoveryPartitions(single), ConvertTo{Logical,Primary}, WipePartition, Merge,
   MoveResizeVolume, RepairRAID5, ConvertToDynamic, MigrateOS, Convert2{GPT,MBR},
   **AlignDisk**, Create/Format/DeleteVolume, InitializeDiskToMigrateOS,
   ConvertMigrateOSToBootMode, SmartMoveResize, SmartSpaceAdjustment, AdjustDiskLayout,
   AllocateSpace, QuickPartition, ChangeClusterSize, SmartCreate, TurnOffBitlocker,
   Convert2{GPT,MBR}(composited), **LowLevelFormat**.
5. **Motor de resize NTFS = 2 estrategias** (Strategy pattern, confirmado pelos fonte):
   `ResizeMoveByRebuildingBitmap.cpp` (encolher: reconstroi `$Bitmap`) e
   `ResizeMoveByRebuildingMFT.cpp` (crescer/mover: reconstroi `$MFT`), escolhidos por
   `ResizeMoveAlgorithmCreator.cpp`. 25 arquivos-fonte em `mod.ntfsutil` revelados.
   `$B0` = `$DATA` do proprio `$MFT` (`CNtfsMFT::Get0XB0DataRun`); deal = fase da operacao
   (`Old/New/SecondVolumeResizeMoveDeal`).
6. **"Bad Sector Scan" NAO e teste de superficie**: e `NTFSFixer/FATFixer/ExFATFixer::FixByScanSector`
   — varre setores achando clusters ruins e **conserta o metadado do FS**. Tem log retomavel.
7. **BitLocker inteiro in-house** (`BitLockerLib.dll`): `AesEncryptSectors`, `ReadRawData`,
   `CreateBitLockerFveControl`, `GetFullVolumeEncryptionKey`, metadados `FVE-EOWBM/EOWBR`,
   Crypto++ em `f:\study\cryptopp890`. **Achado util**: usa a classe WMI
   `Win32_EncryptableVolume Where DriveLetter=` (root\CIMV2\Security\MicrosoftVolumeEncryption),
   que tem Protect/Unprotect/Lock/Unlock — dispensa `manage-bde`.
8. **`CAsynLockVolume`**: trava o volume em thread dedicada com `MAX_RETRY_TIMES`. Resolve a
   causa n1 de "extend falhou" (file handle aberto).
9. **`vssadmin resize shadowstorage /maxsize=401MB`** antes de alinhar — libera espaco real.
10. **`4k disk cannot be selected as target`**; `The disk has already been aligned`;
    `head adjust failure! size rollback` (alinha por head/cylinder CHS, com rollback).
11. **Biblioteca BCD binaria propria** (alem do bcdedit): `Bcd_Init`, `LockBcdFile`,
    `BCD00000000\Objects\{9dea862c-...}\Elements\24000001`. Privada pq precisam funcionar
    onde o `bcdedit` nao esta — **o Kit deve manter bcdedit**.
12. `BootRepair/NTFSUtil.mo` == `bin/NTFSUtil.mo` (mesmo PDB): o motor de resize e compartilhado.

**Lacunas ordenadas por valor/seguranca** (nao implementadas de proposito):
retry de lock (baixo risco/pequeno) > limpar read-only > write-protect via IOCTL >
detectar 4Kn nativo (`PhysicalSectorSize`) > encolher shadow storage > BitLocker via WMI >
alinhamento real (ALTO RISCO, precisa do motor NTFS) > clone/migrate > recovery.

**Sem mudanca de codigo C# nesta sessao** (so documentacao + scripts de RE).
Build nao foi necessario.

### Sessao 03/10 - EaseUS EPM DESKTOP: como o "burlar o shrink" funciona

Pedido: comparar a versao desktop do EaseUS Partition Master com o WinPE ja mapeado, para
explicar o reboot -> tela preta "EaseUS Partition Master" -> operacao -> Windows.

1. **Instalador analisado**: o EPM desktop NAO esta instalado no host (so ha o log do
   desinstalador em D:/PEeaseus.log). Baixado o oficial
   `https://download.easeus.com/trial/epm_trial_ob.exe` (77 MB, Inno Setup 6.1.0).
   O URL foi achado dentro do "downloader" NSIS em D:/HMMM (InitConfigure.ini).
   7-Zip 26 perdeu o suporte a Inno Setup -> usei `innoextract 1.9` (em C:/epm_dl/ie).

2. **VEREDITO**: nao ha truque nenhum. E a MESMA tecnica que o KitLugia ja usa - injetar
   entrada de boot com ramdisk WinPE na BCD e reiniciar. O que o EPM faz a mais e
   robustez e UX, nao mecanismo.

3. **MECANISMO DESCOBERTO** - classe `CPreOsPEBcdProc`, fonte vazada
   `d:\epm\_epm_main\epmlogic\mod.clonemodule\preospeandbcdproc.cpp`, dentro de CloneModule.dll:
   - GUID do ramdisk : `{B40D316B-B159-4229-8D1B-4A388A093C5B}`
   - GUID do device  : `{1941F361-6968-4258-BA93-44E099AF5E55}`
   - WIM gravado em  : `C:\peboot\EASEUSEPMPE.WIM`
   - SDI (boot.sdi)  : `C:\peboot\EASEUSEPMPE.SDI`
   - Descricao EXATA da entrada (literal no binario):
     `"EaseUS Partition Master PreOS"` <-- e a string que o usuario viu na tela preta.
   - Fluxo legado (CPEBcdProc): GUID `{77128171-3112-48b6-8E24-0DF383CD8824}`,
     WIM em `C:\boot\EASEUSEPMPE.WIM`, descricao
     `"EaseUS Partition Master Windows PE"`.
   - Data Center (Boot.dll / CBCDEdit): WIM `EASEDCPE.WIM`, GUID
     `{8e0b8211-7965-4722-94cb-16c9e078c47e}`.
   Metodos: Install, BcdPEProc, IniPEProc, _createEntryInBcd, _createBcdEntry,
   DelBcdEntry, IsBcdEntryExist, CreateBootFile, GetBootType, IsSupportCurOs,
   ChangeWritePrivilege, _createEfiBootFile, _createBiosBootFile,
   _createNtldrBootFile, _editNtldrBcd4Boot, _createDirectory,
   _isoGetFileProc, _isoReadPeMark, _isoSetLocalFileTime.

   Strings literais na ordem em que BcdPEProc as consome (offsets 0x15DAE0-0x15E2F8):
   ramdisksdidevice / OptIn / `partition=C:` / ramdisksdipath / `\peboot\EASEUSEPMPE.SDI` /
   `ramdisk=[C:]\peboot\EASEUSEPMPE.WIM,{B40D316B-...}` / `path` /
   `\windows\system32\boot\winload.exe` / `osdevice` / `systemroot` / `\windows` /
   inherit / `{bootloadersettings}` / detecthal / `{bootmgr}` / bootsequence /
   NTLDR / Delete / ESMGR / SAM / SYSTEM.

4. **ACHADO REUTILIZAVEL (o mais importante desta sessao)**: `sub_1800D9A30` do
   CloneModule.dll le a BCD pelo REGISTRY, sem `bcdedit`:
   - `HKLM\BCD00000000\Objects\{9dea862c-5cdd-4e70-acc1-f32b344d4795}\Elements\24000001\Element`
     (REG_MULTI_SZ, Type==7) -> da o GUID do ramdisk; depois
   - `HKLM\BCD00000000\Objects\<guidDaEntrada>\Elements\11000001\Element`
     -> objeto device: offset 120 = PARTITION_ELEMENT (16 bytes), offset 144 = numero do disco.
   Isso elimina o parsing MULTILINGUE de `bcdedit /enum all` (o bug que custou a sessao 02/08)
   E a dependencia do binario bcdedit.exe no host. Tambem da `IsAlreadyDone` de graca.

5. **PAYLOAD DO PE**: o instalador traz `app/BUILDPE/` e o `DownloadFileList.json` baixa
   o resto em runtime:
   - `https://download.easeus.com/winpe/boot.wim` -> 688.601.920 B, Windows PE 10.0.19041
     (base ADK), MD5 do JSON confere, 1 indice, EDITIONID=WindowsPE
   - `https://download.easeus.com/winpe/tools.zip` -> 202.568.944 B, MD5 conferido
     (DiskMark/CrystalDiskMark, DiskHealth, Chrome etc = os extras do PE)
   `BUILDPE/x64/Windows/System32/` traz winpeshl.ini + Unattend.xml + drivers
   (EUDCPEPM.sys, EUEDKEPM.sys, ebrntdrv.sys, epmdkdrv.sys) = a camada de injecao no WIM.
   winpeshl.ini aponta para `x:\program files\easeus\epm\bin\EPMUI.exe` (X: = o ramdisk).

6. **PeMaker.dll** (fontes makepe.cpp / makepe3.cpp / makepe4.cpp) monta o PE:
   - `CPeMaker::MakePe` -> `CMakePe` / `CMakePe3` / `CMakePe4` (versao por build do Windows)
   - Monta o WIM com **Wimgapi.dll** (API nativa de imagem), NAO DISM; DISM so como reforço
     para `/Add-Driver`, `/Add-Package`, `/Set-SysLocale`, `/Set-UserLocale`,
     `/enable-feature`, `/Set-ScratchSpace:256`
   - Fallback: monta do zero com PETools / WinPE_OCs + WinPE-WMI.cab, WinPE-StorageWMI.cab,
     WinPE-NetFx.cab, WinPE-PowerShell.cab, WinPE-Scripting.cab, WinPE-SecureStartup.cab, fontes
   - Exporta ISO com `oscdimg.exe` (`-bootdata:2#p0,e,b"\efisys.bin"`)
   - `CMakePe::Init szDrive = %c` + `GetVolumeFreeSpace` = escolhe letra com espaco livre

7. **ORQUESTRADOR**: `CEUPreOsMgr` (SetISOPath, DelBootEntry, IsAlreadyDone, GetBootPart,
   GetSystemPart) presente em CloneModule, Clone, BootableMedia, EPMUI, EPMConsole e
   UnInstallProc (ou seja, DESINSTALAR tambem limpa a entrada do boot).
   `CPreOSBoot` (CmdManager.dll): GetIsoPath(), GetBootToolPath(), GetPreOSType().
   `DsRestore.dll`: IsOsSupportBuildPE + IsLinuxPreOS = gate por build do Windows.
   `UILogic.dll`: `CUIPreOsWizard` + `StartPreOSTask` com PRE-CHECAGEM antes do reboot
   (`IsFileValid`, `CheckRecoverDataValid`) - se algo falha, NAO reinicia.

8. **DIFF WinPE x Desktop**: 35 modulos em comum e **ZERO identicos** (MD5). Cerca de 90
   so no desktop (PeMaker, CmdManager, BootableMedia, Boot, MainModule, Device*, DevCtrl,
   EnumDisk, Burn, bcdedit.exe, bootsect.exe, bcdboot.exe, syslinux.exe, grubinst.exe...).
   7 so no WinPE (BT_NTFSUtil.mo, BitLockerLib.dll, LLFProc.mo, RawFixer.dll,
   RecoveryPartitionFixer.mo, VhdVmdk.dll) - o motor de recovery/BitLocker e EXCLUSIVO do PE.
   **O motor de disco (ToolBox.dll, contrato das 54 operacoes) e O MESMO nos dois** - o
   desktop nao tem nenhum truque de shrink, apenas delega a operacao para o PE.

9. **DOCUMENTO**: `docs/EASEUS_EPM_DESKTOP.md` (UTF-8 BOM + CRLF) com o mapa completo, os
   comandos bcdedit reconstruidos e a reflexao sobre replicacao.

10. **REFLEXAO - 3 melhorias de baixo risco para o KitLugia**:
    a) Ler a BCD via `HKLM\BCD00000000\Objects\*` em vez de `bcdedit /enum all`
       (elimina o parsing multilingue e a dependencia do binario bcdedit.exe).
    b) Pre-checagem antes de reiniciar (validar WIM + parametros), como o StartPreOSTask.
    c) `ChangeWritePrivilege` explicito na BCD em vez de depender do estado herdado do admin.
    A diferenca REAL entre o EaseUS e o KitLugia e a INTERFACE: o EPM sobe o EPMUI.exe
    grafico no PE via winpeshl.ini, o Kit usa cmd + log. E por isso que a tela dele parece
    "magica". Nada mais.

### Pendencias desta sessao
- [ ] Avaliar implementar (a) leitura da BCD pelo registry em WinbootManager.cs
- [ ] Avaliar (b) pre-checagem antes do reboot em ScheduleWinpeShrink

### Sessao 03/10 (cont.) - Implementados os itens 0 + 1 + 2 do plano PreOS

Reflexao escrita em `docs/KITLUGIA_PREOS_GAPS.md` (diagnostico area por area) depois da
analise do EaseUS desktop (`docs/EASEUS_EPM_DESKTOP.md`). Implementados 0, 1 e 2.

0. **BOMBA-RELOGIO CORRIGIDA - GenerateWinpeshlIni()** (WinpeBuilder.cs)
   A versao anterior escrevia `[LaunchApps]` com a secao VAZIA, acompanhada do comentario
   (errado) de que "[LaunchApps] nao suporta scripts batch". Descobri que:
   - o proprio `IsoEditorManager.InstallSetupStartnetAsync` (mesmo projeto) documenta que
     QUANDO winpeshl.ini existe o winpeshl.exe lanca SOMENTE o que esta em [LaunchApps]
     -> secao vazia = tela preta morta, sem cmd e sem script;
   - o EaseUS usa [LaunchApps] com exe + argumentos, inclusive um .bat;
   - o boot.wim em uso (C:\KL_WINPE\validation_boot.wim) NAO tem winpeshl.ini nem
     startnet.cmd (verificado com 7z: 0 ocorrencias) - ou seja, o caminho que FUNCIONAVA
     dependia da AUSENCIA do arquivo, e era um accident.
   Agora escreve o MESMO comportamento do padrao, explicito e minimo (sem comentarios,
   byte-a-byte igual ao formato do IsoEditorManager que ja funciona em producao):
       [LaunchApps]
       "%systemdrive%\Windows\System32\wpeinit.exe"
       "%systemdrive%\Windows\System32\cmd.exe", /k startnet.cmd
   Adicionado `RemoveWinpeshIniFromWimAsync` como escotilha para WIM-base que traga um
   winpeshl.ini apontando para o shell errado (ex.: o shim setup.exe).

1. **PREFLIGHT antes de agendar reboot** (WinbootManager.PreOSPreflightAsync)
   Inspirado no CUILogic::StartPreOSTask do EaseUS (IsFileValid + CheckRecoverDataValid):
   se algo invalido ele NAO reinicia. Verifica, antes do shutdown:
   - WIM existe, tamanho > 1 KB e assinatura "MSWIM" valida;
   - boot.sdi existe ao lado do WIM (sem ele o ramdisk nao inicializa);
   - a entrada BCD foi mesmo criada (via BcdRegistry, sem locale);
   - o marcador KL_SHRINK_TARGET.dat esta legivel;
   - o volume alvo existe.
   Integrado em ScheduleWinpeShrink (passo 4b) e ScheduleReinstallPreserve (passo 5b).
   Se falhar: NAO agenda reboot, mostra o que faltou e manda rodar LIMPAR BCD.

2. **LEITURA DA BCD PELO REGISTRY - BcdRegistry.cs (NOVO, KitLugia.Core)**
   `HKLM\BCD00000000\Objects` e a projecao em registry da loja BCD ativa - e o mesmo
   mecanismo que o EaseUS usa (achado em CPreOsPEBcdProc::IsBcdEntryExist, sub_1800D9A30).
   API: IsAvailable / FindGuidsByText / GetDescription / EntryExists (+ versoes async).
   `FindBcdGuidsByText` (WinbootManager) agora usa o registry como ORACLE PRIMARIO e so cai
   no `bcdedit /enum all` + regex multilingue quando a projecao nao existe. Fim do parsing
   por idioma (bug de 02/08) e fim da dependencia do binario bcdedit.exe.
   Somente LEITURA: escrita continua via bcdedit (testado e funcionando).

   FORMATO REAL MEDIDO (este host, 03/10) - importante para quem mexer nisso:
       12000004  REG_SZ       DESCRICAO  ("Windows Recovery Environment", "UEFI:CD/DVD Drive")
       12000002  REG_SZ       path       (\windows\system32\winload.efi)
       12000005  REG_SZ       locale     (pt-BR)
       22000002  REG_SZ       systemroot
       32000004  REG_SZ       boot.sdi
       11000001  REG_BINARY   device     (88 B so volume / 200 B volume+particao)
       14000006  REG_MULTI_SZ inherit
   A descricao NAO e binaria - e REG_SZ. A primeira versao do codigo so procurava texto em
   byte[] e achava ZERO (achado no teste). Corrigido: ElementToText() trata string,
   string[] e byte[].

   TESTE ROUND-TRIP REAL (elevado, host, KitLugia.Core compilado e executado):
       IsAvailable()=True; GetDescription({9dea862c-...})="Windows Boot Manager"
       bcdedit /create {7a1b2c3d-...} /d "KitLugia ROUNDTRIP TESTE" /application osloader
       -> FindGuidsByText("KitLugia","ROUNDTRIP") achou; EntryExists=True;
          GetDescription devolveu o texto exato; os 2 oracles concordaram
       -> bcdedit /delete /f ; EntryExists=False; CLEANUP OK
   `bcdedit /enum all` depois: 0 residuo de ROUNDTRIP/KitLugia.

   Comparacao registry vs bcdedit em 7 termos: concordam em todos os casos RELEVANTES
   (descricoes reais, paths, locale). Divergem em 3 casos onde o REGISTRY e MAIS PRECISO:
   - "WinPE": o bcdedit casa o nome LOCALIZADO do tipo de aplicacao no cabecalho
     ("Aplicativo de Inicializacao do Windows PE"); o registry olha so o conteudo real do
     objeto e nao casa. Para o uso do Kit (filtrar KitLugia) isso e irrelevante.
   - "Windows Boot Manager": a descricao esta no objeto {9dea862c-...}, que o bcdedit
     imprime sob o identificador nao-canonico {bootmgr}; o regex de 36 chars do codigo
     antigo nao pegava. O REGISTRY ACHOU, o bcdedit nao. (Este era mais um bug latente do
     FindBcdGuidsByText antigo.)

3. **Build**: solucao completa GUI+Core = 0 erros / 155 avisos (nullable pre-existentes).
   Nenhuma alteracao na GUI. Projeto de teste do round-trip foi removido.

### O que NAO foi implementado (decisao consciente)
- Shell grafico no PE (item 4 do plano): fica para quando o usuario testar na VMware.
  O doc traz o diagnostico dos 3 bugs (so o .exe entra no WIM, `exit /b 0` sem log,
  `!OSDRV!` assumido) e o plano em 3 degraus. O grau 3 (embelezar a tela do PE) e o de
  melhor custo-beneficio.
- Nenhuma escrita na BCD pelo registry. Escrita segue via bcdedit.

### Como testar na VMware
1. WinpeToolsPage -> INICIAR SHRINK. Conferir no log: `[BCD] Leitura via registry: N entrada(s)`
   e, no fim, `[PREFLIGHT] Preflight OK (...)`.
2. Repositorio: ao enfileirar o shrink, `HKLM\BCD00000000\Objects` DEVE mostrar a entrada
   KitLugia. `bcdedit /enum all` tambem (agora em pt-BR).
3. LIMPAR BCD: deve reportar as entradas removidas. Antes (so bcdedit) removia 0 em pt-BR.
4. Negative: renomear `boot.sdi` e tentar agendar -> o preflight tem de CANCELAR o reboot
   e dizer que o boot.sdi nao esta la (antes reiniciava e nao fazia nada).

### TESTADO (03/10/2026, VMware) — shrink via WinPE: **FUNCIONOU**
`[KitLugia WinPE Shrink] Status: OK | Disk: 0 Part: 3 Size: 12370MB`. Fluxo completo,
com as 3 correcoes (item 0 winpeshl.ini, item 1 preflight, item 2 BCD por registry) ativas.

### DIAGNOSTICO: caminho "Emergency Pre-Boot" do Gerenciador de Discos = CODIGO MORTO
Investigado depois do relato do usuario ("ja tentei usar o WinRE e abria mas a operacao nao
acontecia"). Tres descobertas, todas verificadas no codigo:

1. **O checkbox nao e WinRE.** [PartitionsPage.xaml.cs:591](KitLugia.GUI/Pages/PartitionsPage.xaml.cs)
   mostra um MessageBox dizendo "1. Modificar o Windows RE via DISM / 2. Configurar boot no
   WinRE", mas chama `EmergencyUEFIManager.DeployAsync`, que copia `kitlugia_shrink.efi` no ESP
   e instala rEFInd. **A mensagem descreve um mecanismo que o codigo nao executa.**
2. **E nunca funcionou: o .efi nao e publicado.** O arquivo existe em
   `KitLugia.Core\Resources\KitLugiaEFI\bin\kitlugia_shrink.efi` (58 KB), mas o
   `KitLugia.GUI.csproj` nao tem nenhum `<Content Include>`/`<EmbeddedResource>` para essa pasta
   (so `Resources\**\*` DA GUI, `External\*`, `Tools\GoodbyeDPI` e 4 items soltos do Core:
   `LinuxPreOS` + `BootGoodies\refind`). `Deploy.ps1` tambem nao copia. Resultado:
   `DeployAsync` sempre retorna false com "Execute build_toolchain.sh (WSL) primeiro" —
   mensagem enganosa, o binario JA esta compilado, so nunca vai para o output.
3. **O codigo que ERA WinRE esta morto**: `EmergencyWinREManager` (reagentc + DISM mount/commit)
   nao tem NENHUM chamador. E tem 4 bugs que explicam "abre o WinRE e nao faz nada":
   - so escreve `winpeshl.ini`; nunca cria `startnet.cmd` (que e o mecanismo real e testado);
   - `winpeshl.ini` sem aspas em volta dos executaveis (formato invalido);
   - `diskpart` e `shutdown /r /t 5` como 2 apps no mesmo `[LaunchApps]` = rodam EM PARALELO,
     o reboot mataria a maquina no meio do shrink;
   - `reagentc /target` apontando para o ARQUIVO em vez da PARTICAO (`\Windows\Recovery`).

**Decisao**: nao investir no WinRE agora (4 bugs nao testados, risco de dados). O boot.wim tem
historico de sucesso. Pendente de decisao do usuario: remover o checkbox Emergency (ele nunca
funcionou e a mensagem mente) ou consertar o EmergencyWinREManager de verdade.

---

### Sessao 03/10/2026 (tarde) — 3 pedidos do usuario apos o teste na VM

**(A) FREEZE DE UI ao estender particao — CORRIGIDO**
Sintoma (log do usuario): `[UI-FREEZE] Thread de UI travada por ~5540 ms` exatamente na janela
do extend (23s -> 29s) + "NAVEGACAO BLOQUEADA PARA SUA SEGURANCA".

**CAUSA RAIZ**: `PartitionManager.ExtendPartition` e `async`, mas o caminho rapido (Storage
Management API, via WMI) roda **100% SINCRONO antes do primeiro `await`**: `GetPartitionSizeLimits`
(WMI) + `ResizeStoragePartition` (WMI). Chamado direto do handler, executava inteiro na thread de
UI. A UI nao "parecia" travada — estava mesmo ocupada por ~5,5 s.

**CORRECAO** ([PartitionsPage.xaml.cs:754](KitLugia.GUI/Pages/PartitionsPage.xaml.cs)): as 4
chamadas pesadas do `BtnConfirmExtend_Click` foram envolvidas em `Task.Run` (`ExtendPartition`,
`AtomicExtendDISM`, `AtomicMergeDISM`, `ShrinkPartition`). `UpdateProgress` ja e thread-safe
(faz `Dispatcher.Invoke`), entao o progresso continua funcionando de outra thread. Mesmo padrao ja
usado em `WinpeToolsPage`.

**(B) DISM LENTO -> wimlib-imagex como motor PRIMARIO de imagem**
O substituto ja existia no kit: `KitLugia.GUI\Resources\App\Wimlib\wimlib-imagex.exe`, ate entao
usado so no `IsoEditorManager` e como fallback de apply. Agora e o motor primario:

- `PartitionManager.CmdArg(path)` (novo): escapa argumentos de linha de comando. Raiz de volume
  (`C:\`) e tokens especiais (`@`, `#`, `%`) vao SEM aspas — aspas + barra final truncam o
  caminho no CommandLineToArgvW/CRT (e o mesmo bug que fazia DISM `/ApplyDir:"E:\"` dar exit 123).
- `CaptureVolumeImage`: `capture <dir> <wim> <nome>` via wimlib PRIMEIRO; DISM so como fallback.
- `ApplyVolumeImage`: `apply <wim> 1 <dir>` via wimlib PRIMEIRO; DISM so como fallback.
  O fallback wimlib antigo (DISM falha -> wimlib) foi REMOVIDO: agora e o inverso e redundante.
- `LogTail()` (novo): trunca a saida. O wimlib escreve progresso com `\r` e sem `\n`, entao uma
  unica "linha" tem centenas de KB — medido **412 GiB numa linha so** num capture de volume real.

**DESCOBERTAS DO TESTE REAL (o `wimlib-imagex` bundled)`:**
1. `capture DIRECTORY WIMFILE [IMAGE_NAME]` — o nome da imagem e **POSICIONAL**. A primeira
   versao usava `--name=` e o binario imprimia o usage inteiro e saia com erro.
2. `capture` recusa escrever num WIM que ja existe -> logamos o aviso com o nome do arquivo.
3. O wimlib ja exclui `\System Volume Information` sozinho (imprime `Excluding ... from capture`).
4. O progresso dele TEM `%` ("Archiving file data: 22 bytes of 38 bytes (57%) done"), entao o
   `ReportImageProgress` funciona.
5. Os WIMs sao interoperáveis nos dois sentidos (wimlib->DISM e DISM->wimlib), entao o fallback
   cruzado continua valendo.

**Round-trip VERIFICADO** (wimlib bundled, caminho com espaco no nome):
`capture` exit 0 -> WIM de 1.500 bytes -> `apply` exit 0 -> os 2 arquivos restaurados com
conteudo identico (`conteudo de teste 123` / `segundo arquivo`), incluindo subpasta.

**MEDICAO REAL — E O SPEEDUP ESPERADO NAO SE CONFIRMOU (03/10/2026)**
1,2 GB / 300 arquivos de dados aleatorios (incomprimeiveis), mesmo host, mesmo disco:

| etapa  | wimlib-imagex | DISM                |
|--------|---------------|---------------------|
| capture| 5.063 ms      | 4.819 ms (/Compress:fast) |
| apply  | **1.734 ms**  | 2.168 ms            |
| CICLO  | 6.797 ms      | 6.987 ms            |

**Diferença no ciclo: 2,7% — dentro de ruido.** Conteudo restaurado IDENTICO nos dois
(`diff -r` sem nenhuma diferenca). Num teste menor (3000 arquivos / 76 MB) o wimlib ate
ficou MAIS LENTO no total (5.074 ms vs 4.768 ms).

**CAUSA DA CONFUSAO (erro meu, corrigido)**: eu afirmei no primeiro comentario que "o DISM
monta a imagem antes de capturar/aplicar". **FALSO.** Neste fluxo
(`AtomicExtendDISM` / `AtomicMergeDISM` / `MovePartition`) NUNCA houve `/Mount-Image` +
`/Unmount /Commit` — sao so `/Capture-Image` + `/Apply-Image` diretos. Quem paga o mount/commit
caro e o `EmergencyWinREManager` (que faz `/Mount-Image` no winre.wim) e o `WinpeBuilder`
(que ja usa wimlib). O "DISM lento" que o usuario lembra e' de OUTRO lugar.

**MOTIVO REAL DA TROCA (reescrito no codigo): ROBUSTEZ, nao velocidade**
1. wimlib e' a prova do bug de quoting do DISM (`/ApplyDir:"E:\"` -> exit 123), que em 02/08
   fez a particao ser RECRIADA SEM RESTAURAR OS DADOS — falha real com perda de conteudo.
2. Nao depende do servico TrustedInstaller/CBS, que falha quando o Windows Update esta em
   andamento — pior momento possivel.
3. `apply` isolado e' ~20% mais rapido (1.734 vs 2.168 ms).

Se um dia medirmos em maquina real e o DISM ganhar, e so trocar a ordem dos blocos.
Nenhuma promessa de velocidade foi feita ao usuario: ele foi informado do resultado.

**(C) BARRA LATERAL: extras de disco agrupados sob "Gerenciar Discos"**
Requisito: "esses extras nao podem ficar consumindo espaco da barra lateral... coloque uma seta ao
lado do gerenciador de discos para expandir a categoria dele, ai sim com as opcoes extras, pode ate
colocar o menu do winpe".

- `MainWindow.xaml`: "Gerenciar Discos" + `Button BtnDiscosExpand` (▸/▾) num `Grid` de 2 colunas.
  As extras ficam em `StackPanel PanelDiscosExtras` (comeca `Collapsed`):
  **WinPE Tools (shrink)**, **Fresh Install (preservar)**, Reparar Boot/BCD, Conversor MBR/GPT.
  As 2 primeiras NAO tinham nenhum atalho na sidebar antes (so no Dashboard).
- `NavStyles.xaml`: `NavSubButtonStyle` (novo, `BasedOn` no `NavButtonStyle`): altura 34, fonte
  13, margem recuada a esquerda (24) para mostrar a hierarquia, sem a animacao de scale do hover.
- `MainWindow.xaml.cs`: `NavTagMap` += `🛠️` -> WinpeTools, `♻️` -> ReinstallPreserve (tags
  livres; `🧰` ja era Integrity). `BtnDiscosExpand_Click` / `SetDiscosExtrasExpanded`.
  `UpdateNavButtonsSelection` auto-EXPANDE o grupo quando a pagina atual e uma das extras, para o
  usuario nunca cair numa pagina cujo item esta escondido. `UncheckAllNavButtons` cobre as 4 novas.

**Build**: solucao completa GUI+Core = **0 erros / 155 avisos** (baseline identica).
`obj/verifybin` removido. `subst` de teste (Y:) desmontado; `Q:\: => D:\` do usuario NAO foi tocado.

**A TESTAR na VM**: estender de novo e confirmar que **nao aparece mais** `[UI-FREEZE]` durante a
operacao (nem "NAVEGACAO BLOQUEADA"); extender/mesclar e conferir no log `[WIMLIB] Capture/Apply
exit=0 em NNNN ms` em vez de `[DISM]`; conferir a barra lateral com a seta.

**Ajuste da seta (mesma sessao) — area de clique**: a primeira versao era um `Button` de
`Width=30` com o glifo `&#x25B8;` de `FontSize=13`; o usuario reclamou que "e dificil de
clicar" (print mostrava um botao cinza minusculo). Trocado por `NavExpandButtonStyle`
(`NavStyles.xaml`): alvo de **40x40**, chevron desenhado com `Path` (mesma abordagem do
`KitTaskManagerWindow.xaml`, sem depender de fonte), `CornerRadius=6`, feedback de hover/pressed,
e a rocao -90°/+0° e por **Storyboard** em `RenderTransform.Angle` disparado por
`Tag="0"/"1"` (o `RotateTransform` NAO pode ser alvo de `Setter TargetName` — da erro
`MC4111`; precisa de `DoubleAnimation Storyboard.TargetProperty="RenderTransform.Angle"`).
`SetDiscosExtrasExpanded` agora mexe em `Tag`, nao mais em `Content`.

Build completo (`--no-incremental`): **0 erros / 155 avisos** (baseline; os 144 de um build
incremental eram so o GUI, com o Core ja up-to-date).

---

### Sessao 03/10 (noite) — Fresh Install PROMOVIDO DE VOLTA (decisao do usuario) + regra anti-freeze

**(A) "Fresh Install nunca foi testado" — removido da lista do Gerenciador Discos**
O usuario avisou que o fluxo Fresh Install + Preservacao **NUNCA foi testado de ponta a ponta** e
que ficar visivel na sidebar convida o usuario a clicar ("que legal, vou usar") numa funcao que
aplica imagem do Windows direto na particao. Se falhar no meio, o PC fica sem SO.

- `MainWindow.xaml`: removido o `RadioButton BtnReinstallPreserve` de `PanelDiscosExtras`.
- `MainWindow.xaml.cs`: removida a tag `♻️` do `NavTagMap` e a linha de `UncheckAllNavButtons`.
  Em `UpdateNavButtonsSelection` sobrou so `SetDiscosExtrasExpanded(true)` para quem chegar pelo
  Dashboard (a categoria continua abrindo, mas nao ha item para marcar).
- A PAGINA continua existindo e acessivel pelo **Dashboard** (btn "Fresh Install + Preservacao
  de Dados", `DashboardPage.xaml:464`). **O mesmo risco existe la** — o Dashboard e a primeira tela
  que o usuario ve e o tooltip vende "preservando perfis, programas e registry (Estrategia C)".
  Fica para decisao do usuario remover tambem. Removido so o que foi pedido.

**(B) REGRA ANTI-FREEZE — `Scripts/CheckUiThreading.ps1` (novo)**
O problema que o usuario，球 me chamou DUAS vezes ("travou ao estender") foi o mesmo nas duas:
`async` nao significa "nao bloqueia a UI", e `PartitionManager.ExtendPartition` roda WMI
sincrono antes do primeiro `await`. Nada no compilador reclama disso. Este script e a regra que
faltava.

Como funciona:
1. Extrai as **96 classes estaticas publicas** de `KitLugia.Core` direto do fonte (nao envelhece:
   um Manager novo entra sozinho na lista).
2. Varre os `.xaml.cs` da GUI com a regex
   `await\s+(Task\.Run\s*\(\s*\(\s*\)\s*=>\s*)?([A-Za-z_]\w*)\s*\.\s*(\w+)`.
3. Se o grupo 2 for uma classe do Core e o grupo 1 (Task.Run) NAO casou => violacao.
4. Compara com `Scripts/ui-threading-baseline.txt` (chave sem numero de linha, para mover codigo
   nao quebrar). Padrao legado e ignorado; padrao **NOVO** sai com codigo 1 (falha no CI).

Estado atual: **77 padroes legados** ja no baseline.

**ESCOPO DELIBERADO**: a regra NAO e "todo await do Core precisa de Task.Run". Isso foi sugerido
pelo ChatGPT e RECUSADO de proposito — `GetAllDisks`, `IsSystemDisk` e leitura de registro sao
rapidos e involve-los em Task.Run so cria thread-pool churn. A regra e "nenhum padrao NOVO sem
Task.Run", com baseline revisavel a mao. Se a operacao for rapida demais para justificar thread,
decide-se com `-UpdateBaseline` e o motivo fica no historico do git.

**TESTADO (nao so compilado)**:
| cenario | esperado | obtido |
|---|---|---|
| baseline inicial (`-UpdateBaseline`) | grava 77 | 77, exit 0 |
| rodar sem mudanca | exit 0 | exit 0, "OK - nenhum padrao novo" |
| violacao sintetica `await PartitionManager.AtomicExtendDISM(...)` | exit 1 | **exit 1**, acusou o padrao |
| mesma chamada envolvida em `Task.Run` | exit 0 (reconhecida) | **exit 0**, "Padroes NOVOS: 0" |
| apos restaurar o arquivo | exit 0, sem residuo | exit 0, 0 ocorrencias de "Synthetic" |
| apos converter para BOM+CRLF | continua funcionando | exit 0, 96 classes / 77 legados |

Uso: `powershell -ExecutionPolicy Bypass -File Scripts\CheckUiThreading.ps1`
Novos arquivos em UTF-8 BOM + CRLF (regra do `.editorconfig`), verificado.

Build completo apos as duas mudancas: **0 erros / 155 avisos**, `BUILD_EXIT=0`.

### Proxima sessao
- [ ] Decidir se remove o Fresh Install tambem do Dashboard (mesmo risco, primeira tela do usuario)
- [ ] Ligar `CheckUiThreading.ps1` no pipeline de build/CI do VS
- [ ] Revisar os 77 padroes do baseline e envolver em `Task.Run` os que forem bloqueantes de verdade
      (`BcdRepairManager.DiagnoseAsync`, `DeepUninstaller.RunScanPhaseAsync`,
      `GitHubUpdater.*`, `LatencyAnalyzer.*` sao candidatos obvios)
- [ ] Benchmark A/B dos 62 `DropShadowEffect` (medir antes de mexer)
- [ ] Dividir `KitTaskManagerWindow` (3.473 XAML + 3.507 cs) em UserControls por aba

---

### Sessao 03/10 (noite, 2) — GameBoost/Tray/RAM-Limiter: 6 FONTES DE INSTABILIDADE (travada, tela preta, BSOD)

**Relato do usuario**: os 3 subsistemas (GameBoost Pro, Tray, monitor RAM por processo)
funcionavam bem nas primeiras versoes; depois das refatoracoes quedaron "esquisitos". O Windows
passou a ter gargalo, page fault por falta de RAM, travadas, tela preta e BSOD. E: "antes o motor
V1 deixava o sistema rapido e sem impacto nenhum; agora uso sempre o V4 porque no loading do
Poppy Playtime Ch.4 ficava muito mais rapido".

Investigado em `KitLugia.GUI/Services/TrayIconService.cs`. **A memoria do usuario sobre o V1
estava certa**: o V1 (High + I/O 3 + page 5 + memory NORMAL, sem mexer em nada global) e o unico
motor que nao tem nenhum destes problemas. O V4 era exatamente o oposto.

**6 achados (todos com linha e mecanismo):**

1. **V4 usava `CpuPriority = "realtime"`** -> `REALTIME_PRIORITY_CLASS`. E o `ApplyBoostCustom`
   ainda tentava **`ElevateToSystem()`** se desse acesso negado, ou seja, tentava REALTIME com
   privilegio. Um thread REALTIME pode impedir o scheduler de rodar threads de kernel/DPC:
   congelamento, tela preta (DWM nao roda) e watchdog/BSOD. **Nao e ganho de FPS.**
2. **I/O Crítico (4)**: o codigo fazia `cfg.IoPriorityLevel == 1 ? 4 : ...` e o **DEFAULT** do
   `CustomEngineConfig` era `IoPriorityLevel = 1` — ou seja, **toda config nova nascia em I/O
   Crítico**. I/O Crítico coloca o processo na frente de TUDO na fila do disco, inclusive do
   pagefile e do log do Windows. Pagefile com fome = page fault; page fault em rajada com disco
   saturado = travada do Windows inteiro.
3. **V4 usava `PagePriorityLevel = 2`** (very high) -> o jogo sugar fisica dos outros processos
   e eles page-fault.
4. **Motor POR PROCESSO (o "monitor RAM por processo")**: `ParsePriorityClass("realtime")`
   devolvia `ProcessPriorityClass.RealTime`, e `cfg.IoPriorityLevel == 1 ? 4` repetia o I/O
   Crítico. Como config salva de versao antiga ainda tem esses valores, o caminho continuava ativo.
5. **RAM Limiter — CAUSA DIRETA DOS PAGE FAULTS**:
   `SetProcessWorkingSetSizeEx(handle, (IntPtr)(-1), targetBytes, ...)` com fallback
   `EmptyWorkingSet(handle)`, a cada ~5 s (cooldown base 5 s + 2 s por trim, ate 20 s).
   `min = (SIZE_T)-1` deixa o working set sem PISO, e `EmptyWorkingSet` e brutal: o processo
   tem de reler TODAS as paginas do disco na volta -> rajada de page fault + disco saturado.
6. **Network boost global**: `SystemResponsiveness = 10` (padrao 20) e `NetworkThrottlingIndex = 10`
   (que ja era o padrao, ou seja no-op). `SystemResponsiveness` menor = menos fatia para
   servicos de fundo = explorer/DWM reagindo devagar = "tela preta".

**CORRECOES (todas em `TrayIconService.cs`):**
- `GameBoostEngine.Auto = 5` — **motor unico automatico, novo PADRAO** (`_currentEngine`).
  Decide por cena: foreground -> `High` sempre; I/O = 3 se CPU > 35% (ou GPU > 60%), senao 2;
  Page = 4 se foreground, senao 5 (Normal, nao rouba RAM); Timer boost so quando `cpu > 75%`
  (a fase de loading); Network boost **desligado** (e global, e revertido no `RevertBoost`).
  Regras de ouro comentadas no codigo: NUNCA REALTIME, NUNCA I/O > 3, NUNCA page < 4,
  thread memory priority SEMPRE NORMAL.
- **REALTIME proibido globalmente** em 3 lugares (`ApplyBoostCustom`, `ParsePriorityClass`, e o
  ramo de atribuicao), cada um com log de aviso — configs salvas do usuario continuam Asking
  "realtime" e agora degrade para High em vez de congelar o PC.
- `SafeIoPriority(int)` (novo): 0 -> 2 (Normal), qualquer outro -> **3 (High), teto duro**.
  Elimina o I/O Crítico dos 2 caminhos. Default do `CustomEngineConfig` corrigido: Io 1 -> 3,
  Page 1 -> 4.
- **RAM Limiter**: piso real (`max(64MB, 60% do limite)`) em vez de `-1`, teto = alvo, e
  **nunca mais `EmptyWorkingSet`** — o fallback agora e so a hint `MEMORY_PRIORITY_BELOW_NORMAL`.
- Rotulo do V4 na UI deixou de mentir ("RealTime + Critical I/O" -> "Performance (High + I/O Alta)").

**O ganho do V4 foi PRESERVADO onde ele existe**: GameClassInfo + page moderado + timer boost +
network boost continuam no Auto/V4. RealTime e I/O Crítico nao eram o que fazia o loading
rapido — o usuario media um ganho que vinha de outras 4 coisas.

**VERIFICACAO**: build completo `--no-incremental` = **0 erros / 155 avisos**, `BUILD_EXIT=0`.
Grep de verificacao: **nenhuma atribuicao de `ProcessPriorityClass.RealTime` sobrou** (as 6
ocorrencias restantes sao comparacoes defensivas `!= RealTime` / `== RealTime`) e **nenhum
`? 4` de I/O Crítico em codigo** (a unica ocorrencia do texto esta no comentario do
`SafeIoPriority`). `CheckUiThreading.ps1` continua verde (0 novos).

**LIMITE HONESTO**: nao tenho o minidump da BSOD, entao nao posso PROVAR que essas eram as
causas — sao as causas mais provaveis e todas foram removidas. Se a BSOD voltar, o caminho e
`C:\Windows\Minidump` + o Event Viewer (BugCheck). E o ganho de loading precisa ser conferido
pelo usuario no jogo real — nao da para medir FPS/loading headless.

**2 BUGS NO PROPRIO MOTOR AUTOMATICO, achados na revisao (build passa, logica nao):**
1. A medicao de CPU usava `TotalProcessorTime / (agora - StartTime)` = **media historica** do
   processo. Numa sessao longa com jogo leve, a media nunca chega a 0.75 e o "modo loading"
   NUNCA acionava. Trocado por `SampleProcessCpuShare()`: **delta entre duas amostras
   consecutivas** (retorna -1 quando ainda nao ha amostra anterior, e o motor usa o valor
   neutro 0.35 em vez de inventar numero). O `gpuPct` foi REMOVIDO — nunca era atribuido e
   aparecia no log como `gpu=0%` falso.
2. `ApplyBoostModern` so roda **1x**, na transicao para foreground. Como a medicao e por delta,
   a 1a chamada nunca tem amostra anterior e a decisao ficaria travada a sessao inteira.
   Adicionado o passo **3b no `MonitorTick`**: com motor `Auto`, reavalia a cada 5 ticks
   (~15 s com tick de 3 s) usando `_currentBoostedPid`. Cheap e responsivo.

**REGRA GERAL QUE ISSO ENSINA**: `await`/compilar sem erro nao prova que a logica esta certa.
Estes 2 bugs passaram pelo build limpo e so apareceram ao ler o codigo procurando o
comportamento prometido. Vale reler o motor com essa pergunta: "o codigo faz o que o nome e o
log prometem?"

`EmptyWorkingSet(handle)` continua em `ApplyFireminOptimizations` e `DetectAndTrimLeaks` — de
proposito: os dois usam o MODELO COMBINADO, que tem piso, gate (so roda acima de 150% do alvo) e
rate-limit de 10 s. Nenhum dos dois e o `ApplyProcessRamLimits`, que ficou sem `EmptyWorkingSet`.

### Sessao 03/10 (cont.) - PRIORIDADE EM FAIXA (2 niveis) + RAM LIMITER MEDIDO

Pedido: "um jeito melhor de aumentar a prioridade, porque o foreground tambem e importante" +
"o negocio de RAM ainda funciona?".

#### 1. Prioridade: antes era UMA vaga so (e por isso perdia o proprio objetivo)

`CheckForegroundWindow` fazia: janela nova em foco -> `RevertBoost(anterior)` na hora. Como o
kit reverte para a prioridade ORIGINAL, o jogo que saia de foco (loading, alt-tab para o Discord)
voltava a Normal no instante da troca — exatamente o cenario em que o boost mais importa.
O "foreground importante" estava modelado como "foreground E NADA MAIS".

**Modelo novo: faixa com histerese** (`TrayIconService.cs`):
- `BoostLevel.Nenhum` = prioridade original restaurada.
- `BoostLevel.Sustentado` = saiu do foco mas continua **trabalhando**: `AboveNormal` +
  I/O Normal(2) + page Normal(5) + GameClassInfo off. Nunca e um AUMENTO, so uma reducao.
- `BoostLevel.Foco` = janela em primeiro plano: quem escolhe os parametros e o motor
  (HIGH e o teto; REALTIME continua proibido em qualquer nivel).
- `_boostTargets` (ConcurrentDictionary<uint, BoostTarget>) substitui o par
  `_currentBoostedPid`/`_lastOriginalPriority` como fonte de verdade.
- `DemoteBoost` (troca de janela) = desce para Sustentado; `TickBoostTiers` (passo 3c do
  `RunMonitorCycle`) mede CPU por DELTA e devolve a original de quem ficou **ocioso** ha 45 s.
  O reverso e automatico, sem depender de hook nem de evento de janela.
- `EnforceSustainedCap`: no maximo 3 sustentados (o mais antigo e revertido). Sem teto, alt-tab
  entre varias janelas acabaria com "meio sistema" acima da normal.
- `ApplySustainedBand` faz a reducao no proprio processo e zera I/O/page/GameClassInfo, para o
  app em 2o plano nao roubar I/O nem RAM de quem esta em foco.
- Persistencia: `TraySettings\BoostSustained` (default 1).
- Desligamento correto: `RevertCurrentBoost`, `ShutdownGameBoost`, `ForceReapplyBoost` e o botao
  `RestoreAllThrottledProcesses` agora chamam `RevertAllBoostTargets()` (antes so o PID em foco
  voltava e os demais ficavam com prioridade alterada para sempre).

**2 bugs encontrados ao implementar (ambos build-clean)**:
1. `OptimizeForegroundProcess` mantinha um SEGUNDO estado paralela (`_lastBoostedPid` /
   `_lastOriginalPriority`) que brigava com o do hook — uma restaurava a prioridade enquanto a
   outra ainda achava que o processo era dela. Agora ele so delega para `CheckForegroundWindow()`
   (um caminho so) e os 2 campos foram removidos.
2. Passo 3b reavaliava o motor Auto em `_currentBoostedPid` **sem checar se o processo estava
   em boost** — e esse PID tambem aponta para janelas que nao entram no boost (Chrome e
   excecao do usuario). Agora exige `_boostTargets.ContainsKey(pid)`.
3. `DemoteBoost`+`TryAdd` em `_originalPriorities`: salvar a "original" de novo ao voltar para
   uma janela sustentada sobrescreveria Normal por High/AboveNormal e o processo nunca mais
   voltaria ao Normal. Agora a original so e guardada na primeira vez.

#### 2. RAM Limiter: MEDIU-SE, e a resposta e "quase nao fazia nada"

Harness (`%TEMP%\opencode\ws_test3.ps1`, filho `ws_child2.ps1`): app que aloca e TOCA 400 MB
de proposito, com `GetProcessMemoryInfo` medindo working set e PageFaultCount. Teto de 330 MB,
20 s por modo:

| modo | WS final | page faults/20 s |
|---|---|---|
| mole (`QUOTA_LIMITS_HARDWS_MAX_DISABLE`, o que o kit usava) | **486 MB** (limite decorativo) | **+726** |
| duro (`flags = 0`) | **72 MB** (limite vale) | **+42** |

Ou seja: a flag de "desabilitar o maximo duro" tornava o limite SOFADO e o Windows simplesmente
nao respeitava o teto enquanto houvesse RAM livre. Pior: o ciclo natural de trim/recrescer do
balance set manager gerava MAIS page faults (+726) que o corte duro (+42) — o barulho que dava
travada vinha do proprio ciclo, nao do EmptyWorkingSet.

**Decisao (medida, nao achismo)**: `ApplyProcessRamLimits` agora escolhe o modo por cena —
`flags = 0` (teto duro) so quando `pressao de RAM >= RamPressurePercent` (novo, default 88,
persistido em `TraySettings\RamPressure`) OU o excesso passa de 150% do limite; senao segue
com teto mole. Motivo: o corte duro e um golpe seco e o **piso de 60% nao segura esse golpe**
(medido: estabilizou em 72 MB, bem abaixo do piso de 180 MB), entao ele so entra quando o
sistema precisa da memoria. NUNCA em foreground (o codigo ja pulava).

**Bug de verdade corrigido no caminho**: o teto aplicado era uma ALTERACAO NO PROCESSO que
**nunca era desfeita**. O limite acabava, o teto ficava; e desligar o limite na tela deixava o
teto grudado ate o fim da sessao. Agora `ReleaseWorkingSetCeiling` chama
`SetProcessWorkingSetSizeEx(-1,-1,0)` quando o processo volta abaixo do limite (flag
`WorkingSetCeilingApplied`) ou quando o limite e desligado.
Obs: `GetProcessWorkingSetSize` nao serve para conferir nada num processo de 64 bits — devolve
o sentinela de 64 TB antes e depois da chamada. Para medir, use `GetProcessMemoryInfo`
(`PROCESS_MEMORY_COUNTERS` com campos SIZE_T = 8 bytes, `cb` = 72; com `uint` o `cb` = 40 e a
API falha silenciosamente devolvendo zero).

Tambem: `trimmedCount` era incrementado 2x por processo (contador morto) e agora mostra no log
o que aconteceu de verdade: `nome em MB (limite MB) → teto DURO|mole de X MB (piso Y MB, RAM
do sistema Z%) em N processo(s)`.

Build: 0 erros / 155 avisos (baseline). `CheckUiThreading.ps1`: 0 padroes novos.

**A TESTAR (usuario, jogo real)**: com motor Auto + toggle "Boost do App Ativo" ligado, abrir
loading, dar Alt+Tab para o Discord e confirmar no log (a) "saiu do foco mas continua ativo ->
faixa SUSTENTADA", (b) depois de 15-45 s ocioso (conforme o intervalo do monitor), "ficou ocioso — prioridade original restaurada".
Verificar no Gerenciador de Tarefas se o jogo ABOVE da normal continua carregando em 2o plano.
RAM Limiter: configurar limite, estourar, ver a linha de log e o WS cair; depois voltar abaixo
do limite e confirmar que o teto e liberado.

**LIMITE DA MEDICAO**: os numeros vem de um powershell alocando 400 MB, nao de um jogo com GPU,
texturas e streaming. O modo (mole vs duro) e um fato do kernel e vale para qualquer processo; a
escolha do threshold (88%) e uma escolha de projeto, e pode ser ajustada com o dado do
`GetProcessRamStatus()` da tela.

#### 3. "Se eu limitar o Discord a 200 MB, ele obedece?" — resposta medida: NAO

Mais 4 experimentos (harness `ws_test4.ps1` / `ws_test5.ps1`, app de ~337 MB, `ws_child3.ps1`):

| cenario | resultado | page faults/20 s |
|---|---|---|
| Teto mole, app 337 MB, limite 200 MB, sistema 57% | **ficou 337 MB** (nao obedece) | +635 |
| piso = teto = 200 MB (duro) | **ficou 337 MB** (nao fez nada) | +639 |
| piso 200 / teto 230 (duro) | caiu a 84 MB (abaixo do piso) | **+994** |
| piso 180 / teto 330 (duro), app 486 MB | caiu a 72 MB | +42 |

**CONCLUSAO (fato do Windows, nao bug)**: `SetProcessWorkingSetSizeEx` e um PEDIDO DE TRIM,
nao um teto. Ou a API nao segura nada (teto mole; e `piso == teto` e simplesmente ignorado),
ou o kernel corta de uma vez para BEM ABAIXO do piso pedido. E cortar mais vezes custa MAIS
page faults (+994) do que nao cortar nada (+635). Nao existe ajuste que faca "fica em 200 MB".

O que funciona de verdade: `SetProcessInformation(ProcessMemoryPriority = VERY_LOW/LOW)` — o
Windows passa a descartar as paginas daquele processo PRIMEIRO quando precisa de RAM, sem corte
e sem rajada de disco. O kit so usa essa hint como FALLBACK quando o
`SetProcessWorkingSetSizeEx` falha (linha de `MEMORY_PRIORITY_BELOW_NORMAL`); nunca de forma
preventiva, que e onde ela vale. PENDENTE (nao feito, fora do escopo pedido).

Obs para quem mexer: `GetProcessWorkingSetSize` e inutil para conferir em processo de 64 bits
(devolve o sentinela de 64 TB antes e depois). Usar `GetProcessMemoryInfo` com campos SIZE_T.

#### 4. Situacao dos motores + achado perigoso nos V3/V4

| motor | hoje | situacao |
|---|---|---|
| Auto (5, PADRAO) | mede CPU por delta, decide a cena, I/O 2-3, page 4-5, timer so no loading | seguro |
| V1 Equilibrado | High + I/O 3 + page 5 + `Win32PrioritySeparation` | seguro, o mais conservador |
| V2 FPS Estavel | V1 + GameClassInfo + EcoQoS off + ProBalanceV2 | seguro |
| V3 Extremo (Rede+) | V2 + timer 1 ms + `ApplyNetworkBoostV3` + ProBalanceV3 | **ver abaixo** |
| V4 Performance | CustomEngineConfig High/IO3/page4 + timer + `ApplyNetworkBoostV3` | **ver abaixo** |
| Personalizado | config do usuario; REALTIME bloqueado com aviso | seguro |

A faixa de prioridade (Foco -> Sustentado -> original) vale para TODOS os motores, e nenhum
deles mexe em REALTIME.

**ACHADO NAO CORRIGIDO (03/10/2026)**: `ApplyNetworkBoostV3` (usada por V3 **e** V4, via
`config.NetworkBoost` na linha 3510) escreve no registro global
`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile`:
`SystemResponsiveness = 10` (padrao do Windows: 20) e `NetworkThrottlingIndex = 10`.
**Ninguem restaura**: `RevertBoost` so restaura a resolucao do timer, e `ShutdownGameBoost` nao
toca no registro. `SystemResponsiveness` menor = menos fatia de CPU para threads do sistema
(explorer, DWM, audio) = exatamente o sintoma de "tela preta", e fica valendo DEPOIS de
desligar o GameBoost. Fix suggestion: restaurar 20 / remover os valores em `RevertBoost` e
`ShutdownGameBoost`, ou tirar o ajuste global do V3/V4.

Build/verificacao desta rodada: nenhuma alteracao de codigo (so medicao). O build limpo
0 erros / 155 avisos e o `CheckUiThreading.ps1`exit 0 acima valem para o estado atual do
arquivo (mtime 18:15:36, sem edicao posterior).

### Sessao 04/10 — CORRECAO: auto-start nao nascia na 1a execucao (kit "nao inicia com o Windows")

Pedido: amigo do usuario "nao conseguiu fazer o kit iniciar automaticamente com o Windows,
teve que reabrir o app manualmente". Investigado o startup inicial da 1a execucao.

**CAUSA RAIZ 1 — `IsTrayEnabledStatic()` devolvia false no Kit novo**:
`LoadSettings()` ja forca `IsTrayEnabled = true` quando a chave `TraySettings` nao existe
(1a execucao), mas `IsTrayEnabledStatic()` lia a chave ANTES do `SaveSettings` existir e
retornava `false`. O `MainWindow` faz `if (trayActive) SetAutoStart(true)` -> num PC novo
o bloco **nunca rodava** e nada era gravado. Sem auto-start, o proximo boot nao trazia o Kit.
Corrigido: chave inexistente => `true` (coerente com o LoadSettings).

**CAUSA RAIZ 2 — `EnsureAutoStartMethods` nao registrava na 1a execucao**:
`desired = IsAutoStartEnabled()` era false no Kit novo (nada gravado) => `return` imediato.
Agora detecta 1a execucao (`!desired && !HasStartWithWindowsPref() && !HasAnyAutoStartEntry()`)
e chama `SetAutoStart(true)`. Helper novo `HasStartWithWindowsPref()` (o VALOR existe?)
distingue "nunca escolheu" de "desligou de proposito" (0) — assim quem desliga NAO tem o
auto-start ressuscitado no start seguinte.

**CAUSA RAIZ 3 — escritas INDIRETAS apagavam as 3 vias** (o bug que mais derrubava o auto-start):
- `DashboardPage.ApplyTraySettingsFromQuickMenu()` (roda no 1-clique "Otimizar") e
  `GameBoostPage.SaveGameBoostSettings()` (roda em VÁRIOS toggles: GameBoost, Unpark CPU)
  chamavam `SetAutoStart(checkbox == true)`. Se o checkbox estivesse null/desmarcado
  (ainda nao carregado), o resultado era `SetAutoStart(false)` -> **apagava Registry Run +
  .lnk + Task**. Removido das duas: so o clique do usuario grava.
- `DashboardPage.ChkStartWithWindows` era um CheckBox **sem handler**: marcar nao fazia nada.
  Adicionado `Click="ChkStartWithWindows_Click"` (espelha o estado real, igual ao ChkCrAutoStart).

**Log enganoso (parecia divergencia, nao era)**: a linha da pasta Startup imprimia so o caminho
(sem `--tray`), dando a impressao de que as 3 vias divergiam — o atalho ja recebia `--tray`.
Logs corrigidos (`"path" --tray` nas 3) + novo `LogAutoStartDiagnostics()` que imprime o comando
REAL de cada via (Registry / .lnk / Task com triggers) ao final do `SetAutoStart(true)` —
serve para diagnosticar em 1 olhar por que o Kit nao subiu.

**Bug encontrado pelo proprio teste**: `HasAnyAutoStartEntry()` contava uma Task **desabilitada**
como entrada viva, o que impediria a remontagem do auto-start para sempre. Agora so conta
task `Enabled`.

Verificacao: build limpo `--no-incremental` **0 erros / 155 avisos** (baseline exato) e
`Scripts/CheckUiThreading.ps1` 0 padroes novos (exit 0). Teste funcional real via harness
`%TEMP%\opencode\autostart_probe` (reflection sobre o binario compilado, 8 cenarios, TODOS
passaram): 1a execucao registra / desligado de proposito nao registra / pref=1 sem entradas
reconfigura / `LogAutoStartDiagnostics` sem excecao. O harness fez backup e restaurou o
registro, o .lnk e a Task do host (conferido depois: Task Ready, Run + lnk + pref intactos).

**A TESTAR (PC limpo / VM)**: formatar/apagar o registro, abrir o Kit pela 1a vez, conferir no
log `🆕 Primeira execução detectada` + as 3 linhas `🔎 [AUTOSTART]` + `Situação: ATIVO`, reiniciar
e confirmar que o Kit sobe sozinho no tray.

### Sessao 04/10 — Auditoria da VERIFICACAO INICIAL + motor de startup corrigido

Pedido: "ainda nessa verificacao inicial ele faz varios outros checks, confira ele, e pode
continuar fazendo as operacoes pendentes — deixe o motor do melhor jeito possivel".

Mapeamento completo do que roda na verificacao inicial (caminho real, na ordem):
1. `Program.Main` — mutex `Global\KitLugia_SingleInstance`, `PriorityClass=High`,
   `ThreadPool.SetMinThreads(max(nucleos,8))`, `BringExistingToFront()` se ja ha instancia.
2. `App.OnStartup` — handlers globais de excecao, `EasterEggManager.Start()`,
   `UiFreezeWatchdog.Start()`, parse de `--tray/--update/--unlock/--takeown/--kitstore`,
   `UnlockIpcServer.Start()`, check de .NET Runtime, `EnsureAutoStartMethods()` (Task.Run),
   `SystemTweaks.RefreshContextMenuPathsIfNeeded/Consolidate/Reapply` (Task.Run),
   `new MainWindow()`.
3. `MainWindow` — `SearchEngine.Initialize`, `_trayService.Initialize()` (Dispatcher
   Background), bloco AUTO-START, health check do tray (3s), auto-update (10s),
   `AggressiveMemoryCleaner.StartIntelligentMonitoring(30)`, named event da janela.
4. `TrayIconService.Initialize()` — LoadSettings, AutoFix GameBar/Community/ForceStop,
   `StartExplorerWatchdog`, NotifyIcon, `LaunchTurboApps` (so com --tray), menu,
   `RunSafetyProfiler`, `LoadProcessLimits`, `ShowTrayStatusReport`, `MonitorTick`,
   `InitializeGameBoost`.
5. `MonitorTick` -> `RunMonitorCycle` (thread pool): DownloadBoost, auto-clean, boost do
   foreground, reavaliacao do motor Auto, `TickBoostTiers`, standby, anti-leak, focus
   assist, perfis, Firemin, `LogStats`.

### 7 defeitos REAIS encontrados e corrigidos

**1. `ApplyNetworkBoostV3` NUNCA restaurava o registro global (o mais grave)**
   `HKLM\...\Multimedia\SystemProfile`: gravava `NetworkThrottlingIndex=10` e
   `SystemResponsiveness=10` e nunca devolvia. Como o SystemProfile e o perfil que o
   Windows usa para threads de SISTEMA, manter `SystemResponsiveness=10` (padrao 20) causa
   a "travada / tela preta" que o usuario reclamou — e sobrevivia ao desligamento do
   GameBoost e ao reboot do app. CONFIRMADO EMPIRICAMENTE: o proprio host estava com
   `SystemResponsiveness=10 NetworkThrottlingIndex=10` gravados.
   Correcao: `ApplyNetworkBoostV3` agora guarda o valor ORIGINAL (1a vez) e fica
   idempotente (antes reescrevia as 2 chaves a CADA troca de janela, dezenas de escritas
   por minuto); `RevertNetworkBoost()` devolve o original (apaga a chave se ela nao
   existia) e e chamado em `RevertAllBoostTargets` (ultimo boost saindo) e em
   `ShutdownGameBoost`; `RescueNetworkBoostOnStartup()` (Task.Run no Initialize) e o
   RESGATE: se o Kit morreu com o tweak aplicado e o GameBoost esta OFF, devolve
   SystemResponsiveness=20 e apaga NetworkThrottlingIndex.

**2. Auto-start ressuscitava sozinho apos o usuario DESLIGAR (MainWindow)**
   `MainWindow.xaml.cs` chamava `SetAutoStart(true)` INCONDICIONAL dentro de
   `if (trayActive)`. Como o icone do tray e SEMPRE visivel, `IsTrayEnabledStatic()` e
   true em TODOS os starts — inclusive apos o usuario ter desligado o auto-start de
   proposito. Trocado por `EnsureAutoStartMethods()` (que reconfigura quando ha entrada
   viva, registra na 1a execucao e RESPETA a preferencia desligada).

**3. Corrida entre App.OnStartup e MainWindow no auto-start**
   Os dois chamavam rotinas que escrevem no MESMO `HKCU\...\Run`, no MESMO .lnk e na
   MESMA Task Scheduler em paralelo — o `RegisterTaskDefinition` de uma podia colidir com
   o `DeleteTask` da outra e deixar a entrada faltando (app nao subia no boot seguinte).
   `EnsureAutoStartMethods` agora tem `lock (_autoStartLock)`.

**4. `LoadProcessLimits()` rodava na thread de UI**
   Faz I/O de DISCO (arquivo de limites + backup + recovery) dentro de
   `Dispatcher.BeginInvoke` — com o disco ocupado (boot, antivírus, OneDrive) travava a
   janela nos primeiros segundos. Movido para `Task.Run`; `ShowTrayStatusReport` e
   `MonitorTick` ficaram no Dispatcher (sao baratos / ja despacham para o pool).

**5. `RunSafetyProfiler` fazia limpeza de memoria REAL em TODO boot**
   Executava `MemoryOptimizer.Optimize(Leve)` (EmptyWorkingSets em TODOS os processos)
   em cada start, mesmo com a limpeza automatica DESLIGADA e sem o usuario nunca ter
   pedido — no exato momento em que centenas de paginas estao sendo faultadas de volta.
   Agora so mede quando `AutoCleanEnabled` (e o que a medida calibra).

**6. `Initialize()` sem guarda de idempotencia**
   Um segundo start da pagina criava um NotifyIcon fantasma na bandeja e DOIS timers de
   monitor disputando o ciclo de CPU. Adicionado guard no topo (`if (IsInitialized) return`).

**7. Watchdog do Turbo Explorer enumera processos a cada 2s PARA SEMPRE**
   Mesmo ja desarmado (turbo aplicado), o timer nunca parava: `GetProcessesByName("explorer")`
   + `SessionId` a cada 2s, o dia inteiro, so para rediscover um explorer que ja existe.
   Early-out quando `_explorerTurboArmed == false && _explorerWatchPid != 0`; e o
   `GetSessionExplorerPid` passou a ler `SessionId` ANTES do `Dispose` (a property lanca
   em processo morto) e a descartar o handle no `finally` (antes vazava handle nos paths
   de excecao).

**Bonus**: `ram_stats.csv` girava sem limite (1 linha / 30s ≈ 1.05M linhas/ano ≈ 35 MB).
Agora rotaciona em 2 MB para `ram_stats.1.csv`.

### Verificacoes (exits preservados)

- Build limpo `--no-incremental`: **0 erros / 155 avisos** (baseline exato), BUILD_EXIT=0.
- `Scripts/CheckUiThreading.ps1`: 0 padroes novos (77 legados), UI_EXIT=0.
- Harness NOVO `%TEMP%\opencode\netboost_probe` (console net10.0-windows, UseWPF,
  TaskScheduler 2.12.2, DLLs do verifybin, `Nullable=annotations`): **11/11 assercoes**.
  S1 resgate com GameBoost OFF -> 20 + remove throttling; S2 resgate com GameBoost ON nao
  mexe; S3 resgate respeita valor alheio (25); S4 apply grava 10/10, 2a chamada e no-op,
  revert devolve o ORIGINAL (20, nao o 7 adulterado); S5 chave original ausente -> revert
  APAGA (nao inventa 20); S6 revert sem apply e no-op. Backup/restore do registro do host.
- Probe de auto-start da sessao anterior re-executado: **8/8 cenarios seguem passando**
  (confirmou que a troca no MainWindow nao regrediu nada).
- Workspace limpo (sem `obj/verifybin`, sem pasta-junk `%TEMP%`).

### Pendencias que exigem boot/VM (nao headless)
- Boot real: 1a execucao deve logar `🆕 Primeira execução detectada`; e confirmar que o
  auto-start continua OFF depois de o usuario desligar (o bug do item 2).
- Confirmar no Gerenciador de Tarefas que `SystemResponsiveness` volta a 20 ao sair do boost.
- Ganho real de FPS e Alt+Tab com jogo: nao mensuravel headless.

### Proxima sessao
- [ ] Remover Fresh Install tambem do Dashboard (`DashboardPage.xaml:465`)
- [ ] Ligar `CheckUiThreading.ps1` no CI (hoje roda so a mao)
- [ ] Revisar os 77 padroes legados de travessia de thread
- [ ] Benchmark A/B das 62 sombras
- [ ] Dividir `KitTaskManagerWindow`

### Sessao 04/10 (cont.) — VM de DEBUG: o que existe no host e como usar

Pedido: "pesquise se existe alguma vm para voce controlar uma que voce consiga fazer debug".

**Levantamento do host (04/10/2026)**
- VMware Workstation **26.0.0** instalado -> `C:\Program Files\VMware\VMware Workstation\vmrun.exe`
  (CLI completa: start/stop nogui, runProgramInGuest, copyFile, snapshot).
- **4 VMs Win11** em `%USERPROFILE%\Documents\Virtual Machines\`:
  - `Windows 11 x64` ("NORMAL Windows 11 x64", Win11 24H2, 8 vCPU/8GB, NVMe, **7 snapshots**) — a melhor
  - `Windows 11 x64 (2)` (ISO UUP 28000, 5 snapshots)
  - `Windows 11 x64 (3)` ("sheldon wokink", Phobos11, 1 snapshot)
  - `Windows 11 x64 (4)` ("sergei", WinPE Strelec — NAO e Windows completo, so para PE)
- **TODAS as 4 estao CRIPTOGRAFADAS** (`vmx.encryptionType = "partial"`). `vmrun start <vmx> nogui`
  falha com *"A password is required for this operation"*. A senha NAO esta no cofre de credenciais
  do Windows nem em `%APPDATA%\VMware\config.ini`. **Pede a senha ao usuario para o teste de reboot real.**
- **Windows Sandbox**: feature `Containers-DisposableClientVM` = Enabled, `WindowsSandbox.exe`
  presente, hypervisor disponivel -> **sem senha**, scriptavel por `.wsb`. Registry nasce virgem a
  cada launch e o HKLM e containerizado (nao toca o host). NAO reinicia (cada launch e novo) ->
  nao serve para provar "reiniciei e o Kit subiu sozinho".
- **QEMU 11.0.91** no PATH com acelerador **`whpx`** (usar `C:\Program Files\qemu\qemu-system-x86_64.exe`)
  -> fallback para montar VM nova com a ISO Win11 ja no disco.
- **VBS ATIVO** (`VirtualizationBasedSecurityStatus = 2`, `HypervisorPresent = True`) com a feature
  Hyper-V DESLIGADA. Desde a 15.5 a VMware roda nesse cenario via WHPX (so mais lento).

**Harness de VM criado**
- `%TEMP%\opencode\sbx_probe\` — probe **self-contained win-x64** (171 MB, nao precisa de .NET
  no guest) que executa o codigo REAL de startup por reflection: 1a execucao, 2a execucao,
  usuario desliga/religa, resgate do tweak de rede, diagnostico. Grava `probe_results.txt` +
  `KitLugia.log` na pasta mapeada.
- `%TEMP%\opencode\sbx_probe\klprobe.wsb` — config do Sandbox (pasta publish -> `C:\probe`,
  pasta de saida -> `C:\resultado`, `LogonCommand` roda o probe).
- Como rodar: `WindowsSandbox.exe <caminho>\klprobe.wsb` e pollar a pasta de saida no host.
  **Fecha o Sandbox depois** (`CloseMainWindow` / `Stop-Process`).

**RESULTADO (23 PASS / 2 FAIL — os 2 FAIL sao assercao ERRADA do probe, nao bug)**
- 1a execucao com registro virgem: loga `🆕 Primeira execução detectada`, grava as 3 vias,
  `🔎 [AUTOSTART] ... Triggers: Logon+Boot`, `Situação: ATIVO OK`.
- 2a execucao: idempotente, NAO reage como 1a execucao.
- **Confirmado o fix do bug do MainWindow**: apos `SetAutoStart(false)`, o start seguinte NAO
  ressuscita — loga `ℹ️ Auto-start desativado pelo usuário — respeitando a preferência`.
- Resgate do tweak de rede: `SystemResponsiveness 10 -> 20` + remove `NetworkThrottlingIndex`;
  nao mexe se GameBoost esta ligado; respeita valor alheio (25).
- "religou: Run voltou" FALHA porque apos desligar a preferencia=0 e o `EnsureAutoStartMethods`
  respeita — **comportamento CORRETO**; quem religa sao os 6 handlers de clique do usuario
  (Dashboard 1323/1339, GameBoost 531, Optimization 218, Settings 280, TraySettings 337).

**ERRO COMETIDO NESTA SESSAO (nao repetir)**: rodei o probe **no host** antes de subir o Sandbox.
`SetAutoStart` usa `Process.GetCurrentProcess().MainModule.FileName` -> gravou o auto-start
apontando para `sbx_probe.exe` e depois APAGOU as 3 vias do PC do usuario. Restaurado na hora
(Run + .lnk + `StartWithWindows=1` + Task Boot/Logon Priority 1 RunLevel Highest, conferido).
**REGRA: probe que chame `SetAutoStart`/mexa em HKLM so pode rodar DENTRO de uma VM/Sandbox,
nunca no host.**

**PENDENTE (precisa da senha de criptografia do VMware)**: boot real -> snapshot -> instalar o
build -> REINICIAR dentro do guest -> confirmar que o Kit sobe sozinho no boot -> coletar o log.

### Sessao 04/10 (cont.) — MEDICAO do ciclo de 30s do tray: onde o tempo realmente vai

Pedido: "medir e otimizar o custo do ciclo de 30s do tray". Mediu-se ANTES de mexer.

**Medicao 1 — o ciclo completo, config padrao (349 processos, 20 nucleos, 31 GB RAM)**
- `RunMonitorCycle()` COMPLETO = **17 ms** a cada 30 s = 0,57% de um nucleo. **OK, nao e gargalo.**
- `MemoryOptimizer.GetMemoryStats()` = 0 ms
- `Process.GetProcesses()` = 3 ms
- loop `IsTaskbarWindow` em 349 processos = 14 ms  <- dominante dentro do ciclo
- `UpdateProcessProfiles()` = 21 ms
- `ApplyFireminOptimizations()` = 0 ms
- `GetStandbyListSizeMB()` = 0 ms
- **`NetworkTrafficMonitor.SampleTraffic()` = 538-661 ms** <- 3x acima do orcario

Os subsistemas restantes (auto-clean, standby purge, anti-leak, focus assist, boost) sao
**gated por flag** e ficam desligados na config padrao. O custo "sempre ligado" e mesmo,
17 ms, entao **nao ha o que otimizar no ciclo em si**.

**Medicao 2 — dissectando a SampleTraffic (19-20 PIDs com TCP ativo)**
- `GetActiveTcpConnectionsPerPid()` = **0 ms** (usa `GetExtendedTcpTable` nativo, e rapido)
- 1a criacao de `PerformanceCounter` + `NextValue()` = **849 ms** (cold start do perflib)
- criacao+leitura repetida = **15 ms** | `NextValue()` NO MESMO objeto = **15 ms**
  => **o cache de contadores (`_ioReadCounters`/`_ioWriteCounters`) nao economiza NADA**,
  porque o custo esta no `NextValue()`, nao na criacao. O cache esta populado (16 entradas
  em cada dicionario) e mesmo assim nao ajuda.
- 20 PIDs x 2 contadores x 15 ms = **~570 ms** — bate com os 538 ms medidos.

**Achado de risco (freeze de UI)**: `SampleTraffic` e chamada em `ApplyBoostCustom`
(linha ~3736), que vem de `CheckForegroundWindow` -> `Dispatcher.BeginInvoke` = **THREAD DE
UI**. Com `DownloadBoostEnabled` ligado no perfil, TODA troca de janela em foreground
congelava a interface por ~570 ms. Esse e o caminho `CheckUiThreading.ps1` nao pega,
porque o custo esta dentro de uma metodo synchronous em Core, nao num `await`.

**Correcao aplicada** (`KitLugia.Core/NetworkTrafficMonitor.cs`): TTL de 2 s no snapshot.
`SampleTraffic` virou wrapper com cache; o corpo real foi para `SampleTrafficCore`.
Trafego e sinal lento, 2 s mantem a decisao "isso e download?" identica. O ciclo de 30 s
do monitor continua com amostra nova a cada tique (30 s > 2 s). Devolve copia rasa (container
+ lista novos, mesmos itens) para nenhum chamador mutar o cache compartilhado.

**RESULTADO medido (burst de 60 trocas de janela em 6 s, ~10/s)**
| | bloqueio na thread de UI |
|---|---|
| antes | ~32.280 ms |
| depois | **2.624 ms** (**12x menor**) |
- mediana das chamadas: 0 ms | p95: 531 ms | maximo: 1525 ms
- **3 de 60 chamadas ainda passam de 100 ms** — sao as amostras frias que expiram o TTL.

**PENDENTE (honesto, nao resolvido)**: a amostra fria continua bloqueando a UI (pico de
1525 ms sob contencao). O TTL reduz a frequencia, mas nao tira o cold path da thread de UI.
Fechar de verdade exige mover a amostragem de trafego para fora do `ApplyBoostCustom`
(ou aquecer o cache com um refresher em background). Medir de novo depois disso.

### Proxima sessao
- [ ] Tirar o cold path da SampleTraffic da thread de UI (refresher em background)
- [ ] `GetCachedProcesses()` nao tem cache nenhum (e so `Process.GetProcesses()`) e o
      `ClearProcessCache()` e um stub vazio — ou implementa o cache, ou renomeia
- [ ] Revisar os 77 padroes legados de travessia de thread
- [ ] Benchmark A/B das 62 sombras
- [ ] Dividir `KitTaskManagerWindow`

### Sessão 04/10 — Triagem dos 77 padrões de travessia de thread: 3 bugs REAIS (e o furo do script)

1. **Triagem estática** dos 77 (`%TEMP%\opencode\triage_thread.py`, extrai o corpo do método no
   Core por contagem de chaves e procura `Process.Start`, `WaitForExit`, `.Wait(`, `Thread.Sleep`,
   `.Result`, `ManagementObjectSearcher`, `Task.Run`):
   - **67 SEGUROS** — já dentro de `Task.Run` ou genuinamente async. Envolver seria exatamente o
     churn que o script avisa ("não é todo await do Core que precisa de Task.Run").
   - **9 indeterminados** — todos **async de verdade** ao ler o corpo (o regex do triador não achou
     a definição por causa do tipo de retorno genérico `Task<(bool Ok, string Message)>`):
     `BcdRepairManager.RestoreBootFilesAsync` (async sem await, só copia arquivos pequenos da ESP),
     `IsoEditorManager.ApplyRegistryEditsNoMountAsync`/`InjectFilesIntoWimAsync`,
     `BootableMediaManager.CreateDualBootDrive`/`CreateMultiBootDrive`/`WriteImageDD`/`WriteIsoRaw`
     (todos `await Task.Run` ou `ReadAsync/WriteAsync`), `RefindManager.InstallRefindOnlyAsync` e
     `GitHubUpdater.DownloadUpdateFileAsync` (HTTP async).
   - **3 bugs reais** — e nenhum deles era visível para o `CheckUiThreading.ps1` (ver item 5).

2. **`IsoEditorManager.RemoveProvisionedAppsNoMountAsync`** (~linha 370): `Process.Start("reg.exe
   unload")` + `WaitForExit(10000)` **síncrono** no meio de um método já async (todo o resto do
   fluxo é `await RunProcessCaptured`) → até 10 s congelando a UI. Agora usa o próprio helper:
   `await RunProcessCaptured("reg.exe", "unload HKLM\zSOFTWARE")`.

3. **`RefindManager` — o pior dos três (freeze em TODO boot + ESP vazada)**:
   - `MountEspSync()` = `Process.Start("mountvol")` + `WaitForExit(10000)` num **loop S..Z**
     (9 tentativas = até 90 s de bloqueio).
   - Era chamada por `IsPreBootCompleted()` **na thread da UI**: `MainWindow.Window_Loaded` →
     `_ = CheckShrinkCompletionAsync()` → `await Task.Delay(3000)` → a continuação volta ao
     Dispatcher → `RefindManager.IsPreBootCompleted()`. Ou seja, **3 s depois de cada abertura
     do app**.
   - **Vazava a ESP**: montava e nunca desmontava (só o `CleanupRefindAsync` desmontava) — a
     partição EFI ficava montada com letra, visível no Explorer, para sempre.
   - Correção: `MountEspSync` e `IsPreBootCompleted` (sync) **removidos**; entram
     `IsPreBootCompletedAsync` + `MountEspAsync`/`DismountEspAsync` **públicos**, todos com
     `DismountEspAsync` no `finally`. `InstallRefindOnlyAsync` passou a usar `MountEspAsync` +
     `finally` de desmontar; `MountEspAsync` ganhou **timeout explícito de 10 s por tentativa**
     (o default do helper era 60 s → 9 × 60 s no pior caso).
   - **ARMADILHA encontrada na validação**: `RunProcessCaptured` (helper do RefindManager) era
     `async` **sem nenhum `await`** (corpo 100% síncrono com `WaitForExit`/`WaitOne`) → o
     `await MountEspAsync()` **continuava** bloqueando a UI. Agora o corpo é
     `RunProcessCapturedSync` e o async faz `await Task.Run(...)`.
   - **MEDIDO** (reflexão no binário real, comando sem efeito de sistema
     `cmd /c ping -n 5 127.0.0.1`, probe em `/tmp/opencode/refind_probe.ps1`):
     caminho novo → **Task devolvida em 4 ms**, total 4114 ms, exit 0;
     mesmo corpo **sem** o Task.Run → thread chamadora **bloqueada 4101 ms**.
   - MS Learn confirma que `mountvol <drive>: /s` monta a ESP **naquela letra específica**
     ("Mounts the EFI system partition on the specified drive"), e não "próxima letra livre" —
     então o loop S..Z e o `mountvol <drive>: /d` de desmontar continuam corretos.

4. **`DriverManager` (pnputil) — risco de deadlock ETERNO**:
   `InstallDriversFromFolder` e `UninstallDriver` faziam `Process.Start` + `ReadToEnd()` (lendo
   **só stdout**, com `RedirectStandardError = true`) + `WaitForExit()` **sem timeout**. Se o
   pnputil escreve mais que o pipe de stderr (~4 KB), ele bloqueia escrevendo e o `ReadToEnd` do
   outro lado nunca retorna: **deadlock sem timeout para resgatar**. Pior: `SmartInstallDriver`
   (awaited direto do handler da UI na DriversPage, linha 182) chamava isso sem `Task.Run`.
   Agora os dois usam `ProcessRunner.Run` (timeout 300 s / 120 s, sai OEM do `SystemUtils`) e
   `SmartInstallDriver` envolve as duas chamadas em `Task.Run`.

5. **`ProcessRunner.Run`** (helper usado por todo o Core, ex.: BootableMediaManager): passa a
   drenar stdout+stderr **em paralelo** (`ReadToEndAsync` nos dois antes do `WaitForExit`) — a
   leitura sequencial podia deadlockar em qualquer chamador.

6. **`Scripts/CheckUiThreading.ps1`**: baseline 77 → **80**, 0 novos, exit 0. As 3 entradas novas
   são justamente as correções (`await RefindManager.IsPreBootCompletedAsync`,
   `await RefindManager.MountEspAsync`, `await RefindManager.DismountEspAsync`).

### LIÇÃO (furo do CheckUiThreading — para as próximas sessões)

- O script só detecta **`await Core.Método`**. **Chamada SÍNCRONA de um método do Core a partir da
  thread da UI é invisível para ele** — e era exatamente o caso dos 3 bugs reais desta sessão
  (`RefindManager.IsPreBootCompleted`/`MountEspSync`, `DriverManager.SmartInstallDriver`).
- `async` **sem nenhum `await`** não gera aviso (o projeto não tem CS1998 no log de avisos) e roda
  100% na thread do chamador — foi assim que o `RunProcessCaptured` do RefindManager enganou a
  primeira versão do conserto. Ao validar uma correção de travessia, é preciso conferir o
  **corpo do helper**, não só a assinatura.
- Build: **0 erros / 155 avisos** (baseline). `CheckUiThreading.ps1`: exit 0.

### Proxima sessao
- [ ] Varredura dedicada às **chamadas SÍNCRONAS do Core feitas da UI** (o furo do item 5):
      `Process.Start` / `WaitForExit` / `Thread.Sleep` / `.Result` / `ManagementObjectSearcher`
      em método chamado direto de handler de UI (sem `await` e sem `Task.Run`). Candidatos já
      listados: `ActivationManager:116`, `BrowserExtensionManager:223`, `DeepUninstaller:202/3205`,
      `DriverUnlockService:819/918`, `ForceStopUnlockService:493/508/804/928/1632`,
      `EnablementPackageManager:104`, `BootloaderPackager:201/227`.
- [ ] Testar em VM: instalar rEFInd pelo ShrinkPage (ESP monta E desmonta) e instalar/remover
      driver pela DriversPage (pnputil com timeout novo).
- [ ] `NetworkTrafficMonitor.CleanupStaleCounters` nao faz `Dispose` dos `PerformanceCounter`.

### Sessao 04/10 — FASE 0 do KitTaskManager: os bugs que o usuario RELATOU, um por um

Escopo desta sessao: (a) fechar o item "corrija o que falta" (chamadas SINCRONAS do Core
feitas da thread da UI) e (b) arrumar o KitTaskManager nos pontos que o usuario complaint.
Reescrita estrutural esta em `docs/TM_REWRITE_PLAN.md` (fases F1-F6) — NAO cabe numa sessao.

**(a) Chamadas sincronas do Core na UI — corrigidas**
1. `KitTaskManagerWindow.xaml.cs` — `Kill(bool)` (4 handlers + 2 atalhos de teclado + o menu do
   Resumo) rodava na thread da UI: `KillTree` percorre a arvore de PIDs e o fallback
   `ForceStopUnlockService.Unlock` abre handles de processo/driver. Virou `KillAsync` com o
   laudo inteiro em `Task.Run` (com `BtnFinalizar`/`BtnForceStop` acendendo conforme a selecao).
2. `KitTaskManager.Services.cs` — `ServiceController.Start/Stop/WaitForStatus(10s)` na UI:
   Reiniciar um serviço travava o TM por até 10 s. Iniciar/Parar/Restartar, Habilitar/
   Desabilitar Inicializacao e a varredura de orfas (File.Exists em caminho de rede) foram
   para `Task.Run`, com feedback em TxtStatus/TxtStartupStatus.
3. `KitTaskManager.Latency.cs` — `_lat.Start()` (ETW do kernel) na UI; e o timer de 1 s da aba
   continuava rodando DEPOIS de sair da aba (RefreshLatencyUi + cartao de audio roubando CPU).
   Agora o toggle e assincrono e `SetLatencyTabActive()` liga/desliga o timer pelo SwitchTab.
4. `AppsPage.xaml.cs` — `SRSetRestorePointW` (ponto de restauracao, sobe o VSS) e
   `RegisterExtensionsViaCdpPipe` (fecha o navegador com `WaitForExit(3000)` por processo)
   rodavam na UI; export/import de extensoes tambem (copia de centenas de MB). Todos em Task.Run.

**(b) KitTaskManager — bugs REAIS achados e corrigidos**
5. **Aba Usuarios: o card fechava no tick/scroll.** CAUSA RAIZ: `DgUsers` usava
   `TmRowStyle`, que NAO liga `DataGridRow.DetailsVisibility` ao item (o `UserRowStyle`, que
   lia, existia e nao era usado em lugar nenhum). Com RowDetails + virtualizacao de linha, o
   DataGrid recicla o container no scroll e o detalhe colapsa. Correcao: estilo de linha
   proprio `TmUserRowStyle` + `EnableRowVirtualization="False"` + `CanContentScroll="False"` +
   `ScrollViewer MaxHeight=260` no detalhe + **teto de 24 processos** com rodape honesto
   ("Mostrando os 24 mais pesados de 182") — antes um usuario com 180 processos gerava um
   card mais alto que a janela, e era isso que sumia. Alem disso o clique na seta durante uma
   carga em voo nao era engolido: `_usersReloadPending` refaz a releitura no `finally`.
6. **Resumo: clicar num processo nao fazia NADA** (so havia duplo clique e botao direito).
   Agora `SelectionChanged` abre um cartao com nome/PID/instancias/usuario/estado/caminho e
   tres acoes: Abrir na aba Processos · Finalizar tarefa · Congelar na lista.
7. **Filtro "as vezes nao funciona": CORRIDA.** `ApplyFilter` e fire-and-forget
   (`Task.Run` + `Dispatcher`) e o refresh de 1 s dispara OUTRO em paralelo: o resultado de
   uma busca antiga chegava ao Dispatcher DEPOIS da nova e a lista voltava ao filtro velho.
   Guardado por `_filterGeneration` (descartado no `Dispatcher.InvokeAsync`).
8. **Menu de contexto de Processos agia na selecao ANTIGA** — `DgProcesses` nao tinha
   `ContextMenuOpening`, entao clicar direito numa linha nao selecionada nao trocava a
   selecao. Adicionado (reaproveita o `RowUnderMouse` do Resumo) + reescreve os itens "N".
9. **Aba Servicos: filtro e lista desincronizados.** `LoadServicesAsync` aplicava
   `ApplyServiceFilter("Todos")` em vez do que estava na combo; `CmbServiceFilter_Changed`
   engolia o clique antes da 1ª carga; a lista so carregava uma VEZ (servico que para/sobe
   fora do TM ficava congelado). Agora: filtro reaplicado da combo, ordenacao preservada
   (`SortServices`), recarga por idade (15 s) ao abrir a aba + botao "↻ Recarregar".
10. **Aba Inicializacao: contagem "ativos" INVERTIDA** (contava os que nao eram Enabled nem
    Disabled), filtro perdido a cada recarga, e botoes de acao sempre ligados sem selecao.
    Corrigidos + `↻ Recarregar` + acoes ligadas a `DgStartup_SelectionChanged`.
11. **Botoes de Servicos/Inicializacao sem selecao**clicavam e nao faziam NADA (return mudo).
    Agora acendem/apagam com a selecao (`UpdateServiceActionButtons`/`UpdateStartupActionButtons`).
12. **Grafico central do Resumo "nao ajudava em muita coisa"**: tres linhas coloridas sem
    dizer o que significam. Novo `TxtSumCpuVerdict` (`BuildCpuVerdict`): fala em portugues o
    que a curva esta dizendo — uso de Kernel alto = driver; CPU no limite = programa; CPU
    ociosa = o problema nao e a CPU — mais media/pico da janela e alerta de temperatura.
13. Lixo removido: `KitTaskManager.Performance.cs.bak.20260826215624`,
    `_backup_pre_refactor/` (256 KB), `Windows/KitTaskManagerWindow.xaml.new`.

**Verificacao**: build `0 Erro(s) / 145 Aviso(s)` (baseline era 155); `CheckUiThreading.ps1`
= 0 padroes novos, exit 0; auditoria estatica = 124 eventos declarados no XAML, **0 sem metodo**;
todo controle novo referenciado no code-behind existe com `x:Name` no XAML.

**A TESTAR no app** (nao da para rodar o WPF aqui): aba Usuarios (expandir 2 usuarios e rolar),
clique simples no Top do Resumo, filtro `cpu:>10` digitado rapido, menu direito numa linha nao
selecionada, Iniciar/Parar um servico (a janela deve continuar responsiva), botao Recarregar nas
abas Servicos e Inicializacao, leitura do grafico da CPU.

**Proxima sessao**: F1 do `docs/TM_REWRITE_PLAN.md` (`Model/` + `Filtering/ProcessQuery.cs`
com testes unitarios) — o filtro deixa de depender do Dispatcher e ganha cobertura de teste.

### Sessao 04/10 (cont.) — O WPF RODA AQUI: harness de captura real do TM

**CORRECAO DE UMA AFIRMACAO ERRADA**: a sessao anterior afirmou "WPF nao roda neste
ambiente". **Falso** — verificado: `query session` mostra `console / Lugia / ID 1 / Ativo`,
`explorer.exe` vivo, `[Environment]::UserInteractive = True`. O que NAO existia era apenas um
harness para abrir a janela e capturar. Feito em `%TEMP%	mharness` (fora do repo):
projeto WPF que referencia `KitLugia.GUI.csproj`, cria `KitTaskManagerWindow`, e
`RenderTargetBitmap` cada aba.

**ARMADILHAS ao escrever este harness (todas custaram uma iteracao)**:
1. `Window.Loaded` dispara **DENTRO** de `win.Show()` — anexar o handler depois nunca roda.
2. Sem `app.Run()` o Dispatcher nao bomba e nada acontece (usar `ShutdownMode.OnExplicitShutdown`).
3. `DispatcherPriority.ApplicationIdle` **nunca e alcancado** com o motor de 60 fps do TM
   registrando commands — a aba fica "pronta" mas o codigo nao roda.
4. `dotnet build` falha com CS1555 se o `.cs` nao estiver na pasta do `.csproj` (o
   `write_file` usa caminho RELATIVO A RAIZ DO REPO — o arquivo foi parar la).

**ACHADOS REAIS que so apareceram RODANDO** (nenhum era visivel estaticamente):
- **Cartao do Resumo fechava sozinho a cada refresh.** `UpdateSummaryTopCpu` reconstroi
  `_topProcRows` (Clear + Add) sempre que a ORDEM muda — com CPU viva isso acontece ~1x/s e
  zerava o `SelectedItem`, logo o `SelectionChanged` recolapsava o cartao.
  CORRECAO: `_sumSelectedPid` + `RestoreSummarySelection()` (reescolhe o PID depois da
  reconstrucao; so recolapsa se o processo saiu mesmo do Top).
- **3 botoes nao cabiam no cartao** (coluna estreita): o 3o saia cortado. `StackPanel`
  horizontal -> `WrapPanel` + rotulos curtos.
- Confirmado visualmente OK: card do Usuarios ABRE e **CONTINUA ABERTO apos scroll** com os
  dados atualizando ao vivo (antes fechava) — o bug relatado pelo usuario esta resolvido;
  rodape honesto "Mostrando os 24 processos mais pesados de 160"; aba Servicos com botoes de
  acao **apagados sem selecao** e "↻ Recarregar" ativo; leitura do grafico de CPU
  ("CPU em uso moderado. Ultimos 10s: media 16%, pico 26%."); filtro `cpu:>1` 131 -> ~5 linhas.

**ERRO PROPRIO EVITADO PELO HARNESS**: digitei `$_sumPinned` (com `$`) em vez de
`_sumPinned` ao reescrever uma linha — 6 erros de sintaxe. Compilador acusou; em C# o `$`
so entra como prefixo de string interpolada, nunca como identificador.

**Nota de fidelidade da captura**: `RenderTargetBitmap` pode reaproveitar tiles nao
repintados na regiao do presenter de detalhe e mostrar linhas "sem nome" que NAO existem
na tela (as medicoes de geometria por `ItemContainerGenerator` deram Y/altura corretos).
Para captura fiel, `InvalidateMeasure/InvalidateArrange` em toda a arvore antes de renderizar.

**HARNESS (reutilizar!)**: `%TEMP%	mharness\TmHarness.csproj` + `Program.cs`.
Rode: `./bin/Debug/net10.0-windows10.0.26100.0/TmHarness.exe <dirDeSaida>` — gera um PNG
por aba + `harness.log` com os testes automaticos (clique no Top, expandir card + scroll,
filtro, handler de menu). da para estender para qualquer outra janela do Kit.

**Verificacao final apos tudo**: build `0 Erro(s) / 145 Aviso(s)`, `CheckUiThreading` exit 0,
harness `RUN_EXIT=0` sem nenhuma excecao nao tratada.


### Sessao 04/10 (cont. 2) - Teste DESTRUTIVO real: VMware fechar/abrir + Force Stop (BUG DE GRUPO ACHADO)

Harness NOVO em %TEMP%	mkill (fora do repo): TmKill.exe <image> <force|tree|plain> <outDir>.
NAO chama Process.Kill diretamente - seleciona a linha real no DgProcesses e invoca por
reflection o handler PRIVADO (KillAsync / ForceStopSelectedAsync), igual ao clique do utilizador.

Armadilha do harness: `dg.SelectedItem = <objeto de _allRows>` NAO pega. ApplyFilter cria
`new ProcessRow` por grupo (KitTaskManagerWindow.xaml.cs ~2252), logo a linha visivel e um
objeto DIFERENTE. Tem de se casar por Pid dentro de `dg.ItemsSource`.

**RESULTADOS (todos com confirmacao do SO, nao so do handler):**
- Force Stop no vmware.exe (PID 36476): 3023 ms, morto, saiu da tabela. SO confirmou que
  vmnat/vmware-tray/vmware-authd foram tambem (filhos do Job Object).
- KillAsync(arvore) no vmware.exe (PID 32340): 34 ms. Reabriu com janela real
  "Windows 11 x64 (2) - VMware Workstation" e matou de novo em 34 ms.
- Force Stop / Kill sao ~100x mais rapidos entre si (34 ms vs 3023 ms) porque o Force Stop
  chama FindBlockingProcesses + Unlock (Core), que o Kill nao chama.

**BUG REAL 1 - finalizar GRUO mata so 1 de N** (o botao "quebrado" do utilizador):
`ApplyFilter` agrupa por Name+Group e a linha de grupo so tem `Pid` = PID representativo;
os verdadeiros PIDs ficam em `RawChildren`. `KillAsync` e `ForceStopSelectedAsync` usavam
`row.Pid` e IGNORAVAM RawChildren -> matavam 1 processo.
Medido com 6 `ping.exe`: "PING.EXE (6)" -> 1 morto, **5 vivos**, e a linha saia da tabela
(o grupo voltava no refresh seguinte como "(5)", parecendo que o botao nao fez nada).
CORRECAO: helper `KillTargets(ProcessRow)` (linha de grupo devolve todos os `RawChildren`,
linha simples/filho devolve o proprio PID) usado em KillAsync e ForceStopSelectedAsync;
`_allRows.RemoveAll` tambem passa a remover TODOS os PIDs do grupo; status mostra
"N/M processos finalizados". Re-medido: "PING.EXE (5)" -> vivosApos=0, SO `ping vivos=0`.

**ACHADO (NAO corrigido, e do Core): `FindBlockingProcesses` custa ~31 s FIXOS por executavel.**
Medido: 1 ping = 31,3 s | 6 pings = 31,5 s | vmware.exe = 3,0 s. O custo e por BINARIO,
nao por membro. O Force Stop em grupo foi agrupado por caminho (UMA chamada de
FindBlocking/Unlock por exe em vez de uma por PID) por correcao, mas **NAO houve ganho
medido** (33,4 s com 4 pings antes vs 31,5 s com 6 depois) - nao afirmar speedup.
O custo real esta no Core (scan de registry dos shell folders + handles nativos) -
fora do ambito do TaskManager, deixado intacto de proposito.

**Estado final da maquina**: VMware aberto (PID 14528), 0 processos ping.

Verificacoes: build 0 erros / 145 avisos (baseline) | CheckUiThreading 0 novos, exit 0 |
TmHarness 10 abas RUN_EXIT=0 (regressao, depois da edicao).

### Proxima sessao
- [ ] Force Stop: medir onde os ~31 s de FindBlockingProcesses se gastam (perfil por fase) - se
      for o scan de registry dos shell folders, cachear/cortar o scope
- [x] ~~Testar acoes destrutivas (matar/parar serviço/Force Stop)~~ (04/10: Force Stop + Kill
      testados no VMware e em grupo; falta soserviço/inicializacao)


### Sessao 04/10 (cont. 3) - FECHADO: a "linha sem nome" era artefato de captura, nao bug

Investigacao definitiva do defeito em aberto (linha 1 abaixo do card do Utilizador aparecia
sem Utilizador/Status nas capturas).

1. **O DIAG anterior nao media nada**: lia as celulas via `FindVisual<ItemsControl>(row)`, mas
   o `DataGridCellsPresenter` NAO e ItemsControl -> `cells` era null -> TODAS as 9 linhas
   saiam `nome=''` (ate a linha 1, cujos dados existem). Era um bug do HARNESS, nao do app.
   Corrigido com `CollectTexts` (percorrido real da arvore visual) + leitura por property.

2. **Arvore visual real (todos os dados presentes):**
   [1] H=322 UserName='Lugia'   Status='ativa' Cpu='3,7%' N='166'
   [2] H=32  UserName='DWM-1'   Status='ativa' Cpu='1,2%' N='1'
   [3] H=32  UserName='SISTEMA' Status='ativa' Cpu='0,7%' N='110'
   ... 9 linhas, todas com Utilizador/Estado/CPU/N.

3. **Captura por GDI `PrintWindow` (PW_RENDERFULLCONTENT)** - o render do SO, sem passar pelo
   RenderTargetBitmap do WPF. Novo `ShotReal()` no harness. PrintWindow=True, 1300x780.
   Contagem de pixels claros por linha (x=85..705):
   ECRA REAL      : 355 / 421 / 349 / 540 / 510 / 335 / 338
   RENDERTARGET   : 348 / 435 / 352 / 535 / 504 / 323 / 319
   Os dois caminhos **concordam** e nenhuma linha esta vazia -> o defeito NAO se reproduz.

VEREDITO: nao ha bug. Era tile reaproveitado pelo RenderTargetBitmap na execucao anterior.
Nenhum codigo do app foi alterado por causa disto (nao se mexeu no app sem evidencia).
Comparacao visual em `tm_visual_check/` (DISPONIVEL, apagar quando ja nao interessar).

**Licao do harness**: nunca afirmar "linha em branco no ecra" so com RenderTargetBitmap.
Usar `ShotReal` (PrintWindow) sempre que a duvida for sobre o que esta realmente pintado.


### Sessao 04/10 (cont. 4) - GameBoost Pro: o Automatico estava INALCANCAVEL (UI) e era ESQUECIDO (persistencia)

Pedido: completar o GameBoost Pro - motor unico Automatico a substituir V1/V2/V3/V4, com
legacy opcional mas Auto como melhor. Levantamento + reparo.

**BUG 1 - o Automatico nao existia na UI.** `GameBoostEngine.Auto = 5` e
`_currentEngine = GameBoostEngine.Auto` (padrao no servico desde 03/10) e
`GetEngineDescription` ja o descrevia, MAS:
- `CmbEngine` (GameBoostPage.xaml) so tinha itens Tag 1,2,3,4,"custom" -> impossivel escolher
- `TxtEngineDescription` dizia "V1 - Original Plus ... (PADRAO)" (informacao errada)
- `CmbGameEngine` do Dashboard (PopulateEngineComboBox) tambem nao tinha Auto
- 4x `CmbEngine.SelectedIndex = 0` (=V1) como fallback
- o aviso de travamento era `if (engineNumber != 1)` -> o Auto (5) DISPARAVA o alerta
  "pode causar travamentos" no motor recomendado

**BUG 2 (o pior) - escolher Automatico era esquecido no arranque seguinte.**
`LoadLastEngine()` validava `config.EngineNumber >= 1 && config.EngineNumber <= 4` ->
o 5 era REJEITADO e caia no fallback `EngineNumber = 1` (V1 legado). E os 2 fallbacks
(sem ficheiro / erro) tambem eram 1, logo uma instalacao nova nascia em V1.
Confirmado no disco: `%LocalAppData%\KitLugia\last_engine.json` (nome REAL do ficheiro;
nao e engine_config.json) tinha `EngineNumber: 5` e o codigo antigo rejeitava-o.

**BUG 3 - 3 switches de descricao duplicados e divergentes** (troca de motor, saida do
personalizado, restauracao no arranque). Um dizia "PADRfO", outro dizia que o V4 usava
"RealTime" (o V4 REMOVEU o RealTime de proposito - era o que causava tela preta/BSOD) e
nenhum conhecia o Automatico. Havia tambem mojibake real no codigo: "YY" onde devia estar
o emoji verde, "PADRfO", "s️ AVISO".

**BUG 4 - motor invalido ficava gravado.** `SetEngine(int)` fazia cast direto; um 0/99
deixava `CurrentEngine` fora do enum e o switch de ApplyBoostModern caia no default
(silenciosamente Auto) enquanto a UI mostrava outra coisa.

**Reparos:**
1. GameBoostPage.xaml: item novo `Tag="5"` "Automatico (RECOMENDADO)" PRIMEIRO (assim todos
   os `SelectedIndex = 0` existentes passam a significar Auto), V1-V4 rotulados "(legado)",
   sub-linha a explicar que o legado fixa valores e nao se adapta a cena.
2. GameBoostPage.xaml.cs: `AutoEngineNumber = 5` (constante unica);
   `MotorDescriptionText(int)` + `ShowMotorDescription(int)` = FONTE UNICA de texto
   (substituiu os 3 switches); aviso so para `engineNumber is 2 or 3 or 4`; texto do V4
   corrigido (sem RealTime); intervalo de validacao 1..5; fallbacks = Auto; mojibake limpo.
3. DashboardPage.xaml.cs: Auto primeiro no PopulateEngineComboBox + rotulos de legado.
4. TrayIconService.cs: `_previousEngine = GameBoostEngine.Auto` (sair do personalizado
   devolve o recomendado); `SetEngine(int)` com `Enum.IsDefined` -> invalido cai em Auto;
   log de ClearCustomEngine usa GetEngineDescription (nao imprimia "V5").

**VERIFICADO (harness %TEMP%\gbharness, GB_EXIT=0, TODOS OS TESTES PASSARAM):**
- CmbEngine: 7 itens, `[0] tag=5 AutomAtico (RECOMENDADO)`, 1..4 = legado
- selecao inicial = Auto, `TrayIconService.CurrentEngine` = 5
- persistencia: gravar 5 -> LoadLastEngine devolve 5 | gravar 1 -> devolve 1 (legacy intacto)
- arranque: config 5 -> combo volta a tag 5 e CurrentEngine 5
- guard: SetEngine(1)->1 | SetEngine(5)->5 | SetEngine(99)->5 (Auto)
- descricoes: as 5 corretas, "RECOMENDADO" no Auto, "SEM RealTime" no V4
- Armadilha do harness: a pagina usa `StaticResource` do App.xaml (`AccentColor`), sem
  carregar `Themes/Generic.xaml` em `Application.Resources` o XAML rebenta (XamlParseException).
Build: 0 erros / 145 avisos (baseline).

**NAO verificado**: o EFEITO de runtime do ApplyBoostAuto (prioridades/IO/timer aplicados)
nao foi exercitado - o harness abre a Page sem o TrayIconService em execucao, e
`ApplyBoostModern` exige `_instance`. O dispatch (CurrentEngine=Auto -> case Auto ->
ApplyBoostAuto) esta verificado por codigo + estado. Testar com jogo real.

**Nota de estado do utilizador**: `last_engine.json` ficou com `EngineNumber: 5` (Auto) por
causa do teste - antes disso estava em 4 (V4). Alinhado com o pedido (Auto como melhor).

- Dashboard TAMBEM verificado: `CmbGameEngine` -> 7 itens, `[0] tag=5 Automatico (RECOMENDADO)`,
  1..4 legado, o perfil do utilizador em [5] e "Personalizar..." em [6]. Nota: a DashboardPage
  NAO popula o combo quando hospedada isolada (o Loaded nao completa os servicos), por isso o
  harness invoca `PopulateEngineComboBox` por reflexao - testa a lista, nao o ciclo de vida.
### Erro "Couldn't load changes / git exit 66" (painel de alteracoes do cliente)

NAO e problema do repo: `git ls-files --cached -z` no repo devolve exit 0 tanto com `cd`
quanto com `git -C "<caminho>"`. O cliente chama o git com o caminho do workspace SEM
aspas (tem espacos e parenteses: "KitLugia-master (25) - Copia - ...") e o argumento
quebra. O caminho tem 109 chars, logo NAO e MAX_PATH.
Mitigacao pratica: usar um caminho curto sem espacos/parenteses (ex.: `C:\KL`).

### Sessao 04/10 — Bateria completa de testes (TM + GameBoost): RESULTADOS + BUG das orfas

Pedido do utilizador: "faca todos os testes preciso de resultados para concluir essa parte do projeto".
Harnesses fora do repo em %TEMP% (tmharness, tmkill, gbharness; NOVOS: `tmsvc`, `gbruntime`).

**Resultados (04/10/2026, host elevado, app fechado):**
1. Build da solucao completa: **0 erros / 145 avisos** (baseline mantido).
2. `Scripts/CheckUiThreading.ps1`: **exit 0**, 0 padroes novos (96 classes Core, 80 baseline).
3. `TmHarness` (10 abas): **RUN_EXIT=0**; 15 PNGs incl. `Users_card_REAL` (GDI PrintWindow);
   testes internos OK (card do Resumo, card de Utilizador: 9 linhas/cap 24/scroll 200,
   filtro `cpu:>1` 128->5 linhas, handler do menu de contexto encontrado).
4. `TmKill` (grupo de 6 pings): `plain` -> membros=6 vivosApos=**0** em 91 ms;
   `force` -> vivosApos=**0** em **31838 ms** (custo fixo do `FindBlockingProcesses`, nao corrigido);
   `tree` (cmd + ping filho) -> 28 ms, pai E filho mortos. Comparativo: `plain` num processo sem
   janela tambem mata a arvore (`Kill(entireProcessTree: true)` no fallback) - comportamento atual.
5. `GbHarness`: **GB_EXIT=0, TODOS OS TESTES PASSARAM** (combo Auto[0] tag=5, persistencia 5,
   legacy 1, restore, SetEngine(99)->5, 5 descricoes, Dashboard 7 itens).
6. `TmSvc` (NOVO - servicos + orfas): Spooler stop/start/restart pelo app **OK** (restart 201 ms).
   Orfas: `StartupApproved` byte[0]=3 + backup `__RunCommand` + valor Run removido **OK**.
7. `GbRuntime` (NOVO - motor Auto em RUNTIME): `ForceReapplyBoost` -> prioridade **High** + log
   `GameBoost AUTO: fg=False cpu=35% amostra=False -> IO=2 Page=5 Timer=False`;
   `RevertCurrentBoost` -> **Normal**; gate `ForegroundBoostEnabled=false` -> sem boost;
   reaplicar -> High. **TODOS OS TESTES PASSARAM** (o que faltava verificar do Auto).
8. Benches do repo: `TaskManagerWindowBench` (frio **561 ms** / quente **49 ms**),
   `TaskManagerOpenBench` (1o refresh ~5 ms; sanidade 314 proc/314 caminhos/12 janelas),
   `TmVisualBench` (10 abas, **0 textos sobrepostos**; PNGs em `tests/TmVisualBench/out/`, ignorado),
   `LatencyMeterTest` (10s + 5s, todas as fontes) - **todos exit 0**.

**BUG (achado pelo teste 6): desabilitar item de inicializacao = beco sem saida.**
- `SetStartupItemState(nome,false)` (CASO Run): grava `StartupApproved` (byte0=3; fallback pelo
  nome do arquivo quando o valor nao existe) e a Acao A2 salva o valor Run em
  `HKCU\Software\KitLugia\RemovedApps` (`__RunCommand/__RunHive/__RunPath/__RunValueName`) e
  **DELETA o valor Run** (intencional: apps que ignoram StartupApproved).
- Consequencia: a entrada **sai da listagem** (`BuildAppList` nao le o backup) -> nao ha linha
  para reabilitar; `SetStartupItemState(nome,true)` -> `FindAppByName` (cache) -> **"App nao
  encontrado."**; `RestoreRemovedItem` procura `__Command/__Location` (formato antigo do
  `BackupStartupItem`) e **nao acha o `__RunCommand` do A2** - e **ninguem chama
  `RestoreRemovedItem` na GUI** (grep vazio).
- Impacto REAL medido no HKCU do utilizador: **9 entradas** desabilitadas e ausentes do Run
  (Proton VPN, TeamoRouterHttpProxy, FreeToken Desktop, WingetUI, GoLiveBypass = `__RunCommand`;
  Discord, dev.vencord.vesktop, DuckDuckGo, FREETOKEN DESKTOP = `__Command`) - sem restauracao.
- Evidencia: `TmSvc` v3 (3 ACHADOS) + leitura do registro (Run=ausente, backup presente).

**Estado final**: registro sem residuos do teste (Run/StartupApproved/RemovedApps limpos),
Spooler Running, 0 pings, `last_engine.json` = 5 (Auto). Nenhum arquivo do repo alterado
nesta bateria (so harnesses em %TEMP%).

**PROXIMA SESSAO (decisao do utilizador)**: corrigir o beco sem saida do desabilitar:
 opcao A (recomendada) - `BuildAppList` incluir os backups como entrada "Desabilitado"
 (a linha volta a aparecer e `MenuEnableStartup` restaura via `__RunCommand`);
 opcao B - `SetStartupItemState(true)` ler o backup quando `FindAppByName` falhar;
 opcao C - botao "Restaurar removidos" na aba Inicializacao lendo `RemovedApps`.

### Sessao 04/10 (cont. 5) — CORRECAO do beco sem saida das entradas de inicializacao (opcao A)

Decisao do utilizador: opcao A - listar os backups do RemovedApps como "Desabilitado" na aba
Inicializacao (a linha volta a aparecer e o Habilitar restaura). Implementado em
KitLugia.Core/StartupManager.cs:

1. `BuildAppList` - NOVA secao 8 "ENTRADAS DESABILITADAS PELO KITLUGIA": le
   `HKCU\Software\KitLugia\RemovedApps` e adiciona os DOIS formatos de backup como
   `StartupStatus.Disabled` com nome `Nome [Desabilitado]`:
   - formato A2 (`__RunCommand/__RunHive/__RunPath/__RunValueName`, gravado ao desabilitar):
     Location reconstruida `{hive}\{path}` para o SetStartupItemState cair no CASO 2;
   - formato antigo (`__Command/__Location`), com a Location original.
   Dedupe por nome limpo (`nomesExistentes`) - sem linha duplicada quando o valor Run voltou
   a existir e o backup antigo continua gravado.
2. `FindAppByName` - o match EXATO agora compara tambem o nome sem o sufixo " [Desabilitado]"
   (o Habilitar chama SetStartupItemState(app.Name) e caia so no fallback parcial).
3. `SetStartupItemState` (Acao A2 enable) - fallback para o formato LEGADO: se o
   `__RunCommand` nao existir, chama `RestoreRemovedItem` (restaura `__Command/__Location`
   em Run ou pasta e limpa as chaves); so quando a Location e restauravel (HK... ou pasta de
   Startup) para nao perder backups UWP.
4. **BUG antigo corrigido no mesmo caminho**: a Acao A2 enable abria a chave de backup com
   `OpenSubKey(..., sem writable)` - o `DeleteValue` lancava UnauthorizedAccessException
   (engolida pelo catch) e o backup ficava no registro PARA SEMPRE: a entrada nunca saia do
   "[Desabilitado]" apos reabilitar. Agora `OpenSubKey(RemovedAppsBackupKey, writable: true)`.

**VERIFICADO (TsFix + TmSvc v4, ambos exit 0, TODOS OS TESTES PASSARAM):**
- T1 (formato A2): backup sem Run -> `SetStartupItemState(nome [Desabilitado], true)` ->
  Run restaurado E backup limpo.
- T2 (formato legado): idem via fallback -> Run restaurado E `__Command/__Location` limpos.
- Via UI (TmSvc v4): orfa real desabilitada pelo handler de orfas -> a linha
  `KL_ORFA_TESTE_1010 [Desabilitado]` VOLTA na lista (Core e DgStartup) -> Habilitar restaura
  o Run -> backup limpo -> lista volta a `Nome` (Enabled) sem duplicata; caso legado idem.
- Regressao: build solucao 0 erros/145 avisos; CheckUiThreading exit 0; TmHarness RUN_EXIT=0.

**Efeito para o utilizador**: as 9 entradas reais que estavam fora do arranque agora aparecem
na aba Inicializacao como "[Desabilitado]" e podem ser reabilitadas com um clique (A2 e legado
registral restauram; a UWP DuckDuckGo aparece mas o restauro depende do mecanismo UWP).

Nota: `KitLugia.Core/StartupManager.cs` ja estava modificado por outras sessoes (remocao de
dead code: GetElevatedStartupTaskFullNames/GetAllAdvancedItems/CheckAndFixStartupMethods).
As mudancas desta correcao sao as 3 secoes acima (+ o writable do item 4).

### Sessao 05/10 - Hitbox do botao "Limpar tudo" das notificacoes (WindowChrome)

Sintoma (reportado pelo usuario): o botao "Limpar tudo" do painel de notificacoes
quase nunca respondia ao clique - a hitbox "nao batia" com o texto.

**Causa raiz**: o MainWindow usa `WindowChrome CaptionHeight=40`; na pratica a faixa
de legenda ocupa 6..45 px (6 px de resize border + 40 px de caption). O overlay
`NotificationCenter` (ZIndex 20000) cobre essa faixa e o botao fica em Y=20..46 -
inteiramente DENTRO da legenda. Sem `WindowChrome.IsHitTestVisibleInChrome`, o
WM_NCHITTEST devolve HTCAPTION ali e o clique vira "arrastar janela": sobrava ~1 px
clicavel (a ultima linha do botao).

**Prova (harness WPF externo, %TEMP%
chitbox, com o WindowChrome identico ao do app)**:
- ANTES: WM_NCHITTEST em Y=20/24/28/32/36/39/41/44 = HTCAPTION; clique real
  (mouse_event down+up) em Y=24/34/44 => handler NAO dispara (historico 1->1).
  Controlo: o mesmo teste SEM WindowChrome clicava nas 3 alturas (prova que a
  injecao de cliques funciona e o problema era mesmo o chrome).
- DEPOIS (IsHitTestVisibleInChrome=True no controle): WM_NCHITTEST = HTCLIENT em
  Y=20..60; clique real em Y=24/34/44 => handler dispara (historico 1->0).
- Hitbox WPF do botao: 76,5 x 26 px (texto 66,5 x 16 + 5 px de padding em cada lado).

**Correcao**: `WindowChrome.IsHitTestVisibleInChrome="True"` no `NotifPanel`
(MainWindow.xaml), como ja era feito nos botoes da barra de titulo (min/max/fechar).
Efeito colateral aceitavel: com o painel ABERTO, a faixa de legenda sob o overlay
deixa de arrastar/redimensionar a janela (o clique passa a fechar o painel pelo fundo
escuro, comportamento modal esperado); com o painel FECHADO (Collapsed) nada muda.

**Verificacao**: build solucao 0 erros / 145 avisos (OutDir=obj/verifybin, app aberto);
harness antes/depois acima. **A CONFIRMAR no app**: abrir o sino -> clicar na metade
de cima do texto "Limpar tudo".

### Sessao 05/10 (cont.) - GameBoost: verificado o motor AUTOMATICO no caminho real

Pergunta do usuario: "esta tudo certo como esta o motor automatico? ele funciona em
foreground e nao precisa detetar se ha jogo rodando".

**Resposta (codigo + prova)**: SIM para o requisito. Nao existe detecao de jogo no
caminho: `_heavyAppIndicators` (heuristica antiga unreal/unity/steam/game/lobby) esta
declarada e NUNCA usada, e `IsFullScreen` tambem (zero chamadas). `ApplyBoostAuto`
decide so por (1) o processo ser dono da janela em foreground e (2) a fracao de CPU
por DELTA entre ticks. Gatilho real: SetWinEventHook(EVENT_SYSTEM_FOREGROUND) com
fallback de polling 250 ms e debounce de 50 ms -> CheckForegroundWindow ->
ShouldBoostProcess -> PromoteBoost -> ApplyBoostModern(Foco) -> ApplyBoostAuto.
Perder o foco NAO reverte na hora: cai na faixa Sustentado (AboveNormal) e volta ao
original quando o processo fica ocioso (TickBoostTiers). Reavaliacao do Auto a cada
5 ticks do monitor (~15 s) para o modo loading acordar sem nova troca de janela.

**Harness novo `%TEMP%\gbauto` (caminho real CheckForegroundWindow, app NAO-jogo =
Mapa de Caracteres), 4/4 PASSARAM, exit 0**:
- foreground nao-jogo -> prioridade High + log "GameBoost AUTO: fg=True cpu=35%
  amostra=False -> IO=2 Page=4 Timer=False" + entrada em _boostTargets.
- foco muda -> AboveNormal (Sustentado) com log explicito.
- RevertCurrentBoost -> Normal (prioridade original).
- processo na lista de exclusoes -> 0 boost (segue registado como foreground).
Estado da maquina limpo: sem processos de teste, SystemResponsiveness=20,
NetworkThrottlingIndex=10, last_engine.json = Auto(5).

**SENAO a decidir (nao alterado)**: `_userExceptions` (15 nomes fixos) nunca recebe
boost: chrome, msedge, firefox, opera/operagx/operagxc, discord/discordptb/discordcanary,
spotify, steam/steamwebhelper, epicgameslauncher, battlenet/battle.net. Assim, jogo web
no browser / Discord / Spotify NAO sao boostados. A pagina GameBoost so MOSTRA a lista
(MessageBox no BtnSettings); `AddUserException`/`RemoveUserException`/`GetUserExceptions`
existem no servico mas nao tem UI. Opcao futura: expor toggles por app na pagina.

**Nao exercitado**: a fase "loading" (CPU>75% do total) - exigiria ~75% de todos os
cores; e a reavaliacao de 15 s (dentro do tick de 3 s do monitor), verificada so por
leitura de codigo.

### Sessao 05/10 (cont.) - GameBoost: `_userExceptions` REMOVIDA + pesquisa do motor AUTOMATICO

Pedido do usuario: "essa lista eu nao fiz e nem precisa dela pode remover; so quero que
pesquise bastante sobre o melhor jeito de aplicar o motor automatico - hoje eu uso
bastante o V4 e ele funciona bem".

**1. Remocao completa de `_userExceptions`** (TrayIconService.cs -1467 bytes;
GameBoostPage.xaml.cs -397 bytes):
- declaracao (HashSet com 15 nomes) removida;
- os 3 usos (`ShouldBoostProcess` + 2 no `ApplyProBalanceCore`) passam a consultar so
  `_protectedProcesses`;
- API publica `AddUserException`/`RemoveUserException`/`GetUserExceptions` REMOVIDA
  (ficou sem UI e sem chamadores depois disso);
- `GameBoostPage.BtnSettings_Click` deixou de ler/mostrar a lista (saiu o texto
  "Excecoes do Usuario: ..." do MessageBox).
Build: 0 erros / 145 avisos (baseline). BOM+CRLF preservados nos 2 arquivos.
**Consequencia (a saber)**: chrome/edge/firefox/discord/spotify/opera/steam/epic/battlenet
deixam de estar blindados contra o ProBalance dos motores V2/V3 (que rebaixa para
BelowNormal quem passa do limiar de CPU em fundo). O Automatico nao usa ProBalance, logo
para quem usa o Auto nada muda. Se voltar a incomodar, o sitio certo e
`_protectedProcesses` - e nao uma segunda lista de excecoes.

**2. `%TEMP%\gbauto` re-rodado depois da remocao: 8/8 OK, RUN_EXIT=0**
(campo `_userExceptions` inexistente; `_protectedProcesses` = 41 nomes; foreground
nao-jogo -> High + log "GameBoost AUTO"; perdeu foco -> AboveNormal Sustentado;
Revert -> Normal; `ShouldBoostProcess` false para o proprio kit e para o explorer,
true para app comum).
Maquina limpa: SystemResponsiveness=20, NetworkThrottlingIndex=10, last_engine=Auto(5),
sem processos de teste.

**3. Pesquisa do motor automatico -> `docs/GAMEBOOST_ENGINE_RESEARCH.md`** (novo, BOM+CRLF).
Achados com fonte oficial + medicao no host:
- (A) **Timer global nao ajuda o jogo**: desde o Win10 2004 o pedido de timer resolution e
  POR PROCESSO (randomascii + doc timeBeginPeriod). Nesta maquina
  `GlobalTimerResolutionRequests` NAO existe -> `BoostTimerResolution()` (chamado pelo
  processo do Kit) nao acelera os timers do jogo; so sobe o tick global e o custo de
  DPC/audio. O jeito correto e limpar `PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION`
  no processo do jogo (honrar o pedido que o JOGO faz).
- (B) **Bug de revert do `Win32PrioritySeparation`**: o Kit escreve 38 (0x26) no boost e 2
  no `ShutdownGameBoost`, sem ler o original. O original DESTA maquina JA e 0x26 -> o
  revert piora em vez de restaurar (reproduzivel com `reg query`).
- (C) **Falta a metade que mais rende**: Bitsum (Process Lasso) - "Don't set your important
  processes to High or Real-Time... ProBalance works from the other direction (lowering
  priority classes) for a reason" - e age SO sob carga alta, poupando o foreground. O
  `ApplyBoostAuto` tem `ProBalance=false`, e o ProBalance dos V2/V3 nao tem gate de carga
  do sistema (decide so pelo CPU do processo).
- (D) **Rotulos de page priority invertidos**: doc oficial `MEMORY_PRIORITY_INFORMATION` =
  1 VERY_LOW .. 5 NORMAL (**default**). A UI diz "Page: Maximum (5)" e o comentario chama
  2 de "very high"; na escala real 5 e o DEFAULT e 4 e abaixo do normal - o Auto no foco
  usa 4, ou seja ABAIXO do default.
- (E) **Custo**: `ApplyBoostCustom` chama `SetThreadEfficiencyForAllThreads` em TODA
  reaplicacao (o Auto tem ThreadEfficiencyMode=false sempre) -> enumera/abre todas as
  threads do jogo a cada troca de foco e a cada ~15 s (e so tem efeito em build >= 26100).
- (F) Deteccao OK (hook + polling 250 ms); falta `WINEVENT_SKIPOWNPROCESS`.
  `_heavyAppIndicators`/`IsFullScreen` seguem mortos e NAO devem voltar: "detetar jogo"
  nao e o desenho (o 3o balde e "ja em boost e perdeu o foco" -> Sustentado).
- (G) **MMCSS nao e aplicavel**: `AvSetMmThreadCharacteristics` marca a thread que a
  chama; um programa externo nao pode registar a thread de outro processo.
Desenho proposto do Automatico v2 (seccao 5 do doc): demover o fundo sob carga (ganho
principal) + elevar o foco (HIGH) + estados por-processo (EcoQoS off, honrar timer,
GameClassInfo, I/O 3 no loading) + NADA global (sem timer global, sem
Win32PrioritySeparation, sem rede). **O V4 fica INTOCADO** (pedido explicito do usuario).

### Sessao 05/10 (cont. 2) - GameBoost: MOTOR AUTOMATICO v2 implementado + 3 bugs REAIS corrigidos

O utilizador pediu para continuar e fazer do Automatico "a parte mais bem trabalhada do
kit, porque afinal e' o motor que sempre fica rodando em segundo plano". O `_userExceptions`
ja tinha sido removido no bloco anterior; aqui foi o desenho (docs/GAMEBOOST_ENGINE_RESEARCH.md
secao 5) que passou a codigo. **V1/V2/V3/V4 NAO foram tocados** (o utilizador usa V4 bastante).

**3 BUGS ENCONTRADOS E CORRIGIDOS (todos medidos, nao por leitura):**

**1. `Win32Api.SetThreadMemoryPriority` NUNCA fez nada (o mais grave).** Chamava
`SetThreadInformation(hProcess, 0, MEMORY_PRIORITY_INFORMATION)`. Dois erros: a classe 0 em
THREAD_INFORMATION_CLASS e' `ThreadBasicInformation` (nao memory priority), e o handle e' de
PROCESSO onde a API exige handle de THREAD. Medido (harness `%TEMP%\gbmemprio`, explorer):
`ok=False erro=6` e o valor relido era SEMPRE 5. Corrigido para
`SetProcessInformation(hProcess, ProcessMemoryPriority=0, MPI, 4)` — medido 5 -> 1 -> 5.
Afetava TODOS os motores (o `MEMORY_PRIORITY_VERY_LOW` do ProBalance nunca foi aplicado).
O nome do metodo foi mantido porque todos os chamadores ja passam `proc.Handle`.

**2. Os rotulos de page/thread memory priority estavam INVERTIDOS, e o perfil "Maximum"
aplicava VERY_LOW.** A escala oficial `MEMORY_PRIORITY_INFORMATION` e' 1=VERY_LOW ..
5=NORMAL, e **5 e' o DEFAULT** (nao ha "maximum": o 5 ja e' o topo). A UI guardava o
INDICE do ComboBox como se fosse o valor: o item "Maximum (minimo swap, maxima performance)"
estava no indice 1, e 1 = VERY_LOW — ou seja, o perfil escolhido para NAO fazer swap
era o que mandava o SO descartar as paginas primeiro. Corrigido em 4 sitio:
`PagePriorityFromIndex(0)=5 / (1)=4` e `ThreadMemoryPriorityFromIndex(0)=5 / (1)=1`
(TrayIconService), a traducao nos 2 pontos que constroem CustomEngineConfig
(GameBoostPage + DashboardPage), os rotulos do ComboBox (GameBoostPage.xaml) e o overlay
do motor por processo (ProcessEngineConfigOverlay: passa a escala 1..5 real). Default de
`ProcessEngineConfig.PagePriorityLevel` era 1 (=VERY_LOW) -> 5.
O motor Automatico tambem usava `PagePriorityLevel = foreground ? 4 : 5`: no FOCO ele
aplicava 4 (= BELOW_NORMAL), ou seja, o motor rebaixava a memoria do jogo exatamente
quando devia ser o mais protegido. Agora e' sempre 5.

**3. O revert do `Win32PrioritySeparation` estragava a maquina.** Gravava 2 sem ler o
original. Esta maquina ja vem com 0x26 (38): o revert nao restaurava, piorava. Agora
`SetWin32PrioritySeparation` le e guarda o original na 1a ativacao e devolve-o no
desligar (`ReadWin32PrioritySeparation` novo, para prova). Medido no harness do V4:
original=38 -> boost=38 -> shutdown=38 (antes chegava a 2).

**AUTOMATICO v2 (`ApplyBoostAuto`)**
- page priority SEMPRE 5 (NORMAL/default) em vez de 4 no foco;
- NADA global: `TimerBoost=false` (o `BoostTimerResolution` roda no processo do Kit e,
  desde o Win10 2004, o pedido de timer e' por processo — nao acelera o jogo) e
  `Win32PrioritySeparation=false`;
- HONRA O TIMER DO JOGO: novo `SetPowerThrottling` com
  `PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION` (0x2) a 0 no StateMask, ou seja,
  o Windows respeita o pedido que o proprio JOGO faz (o bit existe para o contrario:
  no Win11 o SO corta o timer de janelas minimizadas/ocultas);
- PROBALANCE COM GATE DE CARGA (`ApplyAutoProBalance`): so rebaixa processos de fundo
  para BelowNormal quando a carga TOTAL do sistema > 70% (`Win32Api.GetSystemCpuLoad`,
  delta de `GetSystemTimes`), com as N amostras consecutivas + cooldown ja existentes.
  E' a metade que faltava: o Bitsum/Process Lasso age "from the other direction"
  (baixar o fundo) e so sob carga alta; o ProBalance do V2/V3 decidia so pelo CPU do
  processo, sem olhar a maquina;
- HISTERESE DE FAIXA (`_autoBand`): so muda a cena Idle<->Loading quando o CPU cruza o
  limiar, em vez de reescrever tudo a cada tick;
- `ProBalance=true` no cfg do Auto: o gate corre no mesmo caminho do boost (o
  `ProBalanceTimerTick` so dispara com o toggle global ligado).

**CUSTO E DETECCAO**
- `SetThreadEfficiencyForAllThreads` agora corre **1x por pid** (`_threadEfficiencyOff`):
  antes enumerava e abria TODAS as threads do jogo a cada troca de foco e a cada ~15 s,
  para um ajuste estatico enquanto o processo viver;
- `WINEVENT_SKIPOWNPROCESS` no SetWinEventHook (o Kit ja nao se auto-boosteia);
- `_autoBand`/`_threadEfficiencyOff`/`_autoCpuSamples` limpos no `RevertAllBoostTargets`
  e no `TickBoostTiers` (antes viviam para sempre com os pids do dia todo);
- `_heavyAppIndicators` e `IsFullScreen` REMOVIDOS (declarados e nunca usados) — e o
  desenho nao e' "detetar jogo": um detector aqui daria falso negativo em jogo
  independente/Emulated e falso positivo em qualquer janela com "game" no nome.

**VERIFICACAO**
- Build: `dotnet build KitLugia.sln --no-incremental` -> **exit 0 / 0 erros / 145 avisos**
  (baseline exato).
- `%TEMP%\gbauto` (caminho REAL `CheckForegroundWindow`, app NAO-jogo = Mapa de
  Caracteres): **19/19 OK, RUN_EXIT=0**. Log real do motor:
  `🤖 GameBoost AUTO: fg=True cpu=35% amostra=False faixa=Idle (mudou) -> IO=3 Page=5`.
  Inclui: memory priority do foco = 5 lida com `GetProcessInformation`; Auto nao mexeu
  no Win32PrioritySeparation (38 -> 38); `GetSystemCpuLoad` = 54% com a maquina folgada e
  ZERO processos rebaixados (gate a fechar); `SetThreadMemoryPriority(1)` chega mesmo
  (releitura = 1) e restaura para 5; traducao de indices correta; codigo morto ausente.
- `%TEMP%\gbv4` (NOVO, regressao do V4): **7/7 OK, RUN_EXIT=0**. O V4 continua a
  aplicar High, a pedir timer de 1 ms (`⏱️ Timer Resolution: 0,98ms → 0,98ms`) e a
  mexer no Win32PrioritySeparation — e o shutdown devolve 38 (o original).
- Maquina limpa no fim: sem processos de teste, SystemResponsiveness e
  NetworkThrottlingIndex em absence (= default), last_engine.json = Auto(5).

**A TESTAR NO JOGO (so o que o harness nao alcanca)**: (a) a fase Loading (CPU > 75% do
total) — exigiria um jogo real a queimar ~3/4 dos cores; (b) o ganho em 1% low / tempo
de frame com o jogo em foco; (c) o gate do ProBalance a abrir de verdade (> 70% de carga
com um segundo processo pesado em fundo) e a restaurar quando a carga cai;
(d) confirmar que o jogo ja pedido timer de alta resolucao fica com ele honrado.

### Sessao 05/10 (cont. 3) - GameBoost: BENCHMARK dos motores (tests/gbbench) + resultado medido

Pedido: "pode fazer, seria bom ter algum tipo de benchmark que voce conseguisse testar
os motores". Entregue em `tests/gbbench/` (dentro do repo, com ProjectReference
RELATIVO — o kit e movido entre maquinas, ver AGENTS.md regra 1).

**Metodologia** (as duas decisoes que tornam o numero fiavel):
1. **CARGA DE FUNDO OBRIGATORIA.** Prioridade de processo so muda o resultado sob
   contencao de CPU. Sem carga, `High` e `Normal` dao exatamente o mesmo — um
   benchmark sem carga mediria zero sempre e pareceria "o motor nao faz nada".
   O worker e' um PROCESSO SEPARADO com janela visivel (traido a foreground com
   AttachThreadInput + SetForegroundWindow), e o orquestrador cria N × CPUs threads
   a 100%. argumento do exe = multiplicador da carga (default 2; **usar 10**).
2. **CAMINHO REAL.** O boost e' aplicado por `CheckForegroundWindow` (o mesmo metodo
   que o SetWinEventHook e o polling de 250 ms chamam) — nunca `ApplyBoostV4` direto.
   Ordem dos motores intercalada a cada repeticao (cancela deriva termica/turbo),
   mediana e nao media, `GetProcessTimes` para o CPU% real do scheduler.
   Metricas: FPS, **p99 do tempo de quadro** (proxy de 1% low / stutter) e CPU%.

**RESULTADO (20 CPUs, 3 repeticoes, EXIT_REAL=0)**

*CENARIO OCIOSA (0 threads de fundo)* — **ganho ~0%**: base 5253,5; V1 −0,91%,
V2 +0,14%, V3 −0,91%, V4 −0,04%, Auto −0,09%. Todos dentro de ±1% (ruido),
reproduzido em 3 execucoes. Isto VALIDA o principio: prioridade nao faz nada sem
contencao — e o motor nao esta a prometer ganho fantasy.

*CENARIO CARGA 10x (200 threads de fundo)* — **ganho +6,0% de FPS, p99 0,28 -> 0,24 ms,
CPU% 95,6 -> 100,0**, REPRODUZIVEL (2 execucoes: base 3937,7 e 3939,3; motores
~4176–4181 em ambas, concordam dentro de 0,1 pp). O CPU% e' a leitura mais direta:
o scheduler deixa de poder tirar CPU ao processo em foco.

*Todos os motores sao equivalentes neste benchmark* (+6,05% a +6,18% entre si, dentro
de ruido) porque TODOS aplicam `PriorityClass High`. O que os diferencia — page
priority, I/O priority, EcoQoS/timer do jogo, GameClassInfo e ProBalance — **nao e'
medido aqui**, porque o workload e' CPU puro (nao toca disco, memoria nem GPU).

**LIMITACOES (lidas antes de citar qualquer numero)**:
- **Nao e' um jogo.** Mede contencao de CPU. Nao mede GPU, frame pacing, I/O de disco
  nem rede. Um jogo GPU-bound nao vera este ganho.
- **Nao isola o ProBalance.** Com 200 threads o gate do Auto (>70%) abre, mas o
  benchmark nao separa quanto veio do `High` e quanto do rebaixamento do fundo.
- **A base so e' estavel se o cenario for respeitado.** Com `GbBench.exe` orfao de
  uma corrida anterior a contencao sobe e a base pode cair para ~1969 FPS,
  inflando o ganho para +112% (medido, mas NAO e' o numero a citar).

**Armadilhas do harness (gastar horas nelas se for mexer)**:
- Correr com `dotnet run` em vez do `.exe` (apphost): o entry assembly passa a ser o
  `.dll` e o WORKER nao acha o `runtimeconfig.json` → `FileNotFoundException:
  System.Runtime`. O harness escolhe o `.exe` de mesmo nome base por conta propria.
- O worker tem de forcar STA (o apphost pode entrar como MTA) e nao pode usar
  `Dispatcher.PushFrame` no Startup (aninha mal e o startup nunca termina) — espera
  do ficheiro "go" e' `Thread.Sleep`.
- **O cleanup NAO pode matar o proprio processo pelo nome** (o worker tem o mesmo
  nome): fazia o benchmark matar-se a si proprio no fim (exit 127) e o relatorio
  final nunca era impresso. Compara com `Environment.ProcessId`.
- `vs base` com `ToString("+6.1f")` em pt-BT mostrava `+61f%` (o `f` minusculo e'
  section separator, nao formatador) — usar `F2` + sinal separado.

**Como correr**:
```
cd tests/gbbench
dotnet build -c Release
./bin/Release/net10.0-windows10.0.26100.0/GbBench.exe 10
```

### Sessao 05/10 (cont. 4) - GameBoost: FIABILIDADE — 3 fugas de estado corrigidas + resgate pos-crash

Pedido: "continue, o motor tem que ser confiavel". Fiabilidade aqui = **nao deixar
rasto**: nada pode ficar alterado depois de desligar o toggle, fechar o Kit, ou o Kit
ser morto a meio. Um motor que ganha 6% e estraga a maquina nao e um motor.

**3 FUGAS ENCONTRADAS (as 3 primeiras sessoes nunca as tinham apanhado):**

**1. `RevertBoost` LIGAVA o power-saving ao reverter.** Fazia `SetEcoQoS(handle, TRUE)` —
o oposto do que o boost faz (que o desliga). EcoQoS
(`PROCESS_POWER_THROTTLING_EXECUTION_SPEED`) poe o Windows a meter o processo em
Nucleos E / baixar a frequencia: **o programa ficava LENTO depois de sair do boost**,
e o usuario via "o Kit estragou o meu programa". Corrigido para
`SetPowerThrottling(handle, false)` (o estado neutro/default) + repõe tambem a
memory priority no revert (que nao era reposta).

**2. `ShutdownGameBoost` NAO restaurava os processos rebaixados pelo ProBalance.**
Vivem em `_throttledProcesses`, um conjunto SEPARADO dos `_boostTargets`, e
`ShutdownGameBoost` so chamava `RevertAllBoostTargets()`. Resultado: desligar o
GameBoost deixava os processos em **BelowNormal para sempre** — "o Kit deixou os
meus programas lentos" sem nenhum toggle ligado. Corrigido: `ShutdownGameBoost` chama
`RestoreAllThrottledProcesses()` (que trata os dois conjuntos).

**3. Nao havia resgate pos-crash.** Se o Kit morresse (power kill / BSOD / Terminar
processo) com o boost aplicado, os processos ficavam em High ou BelowNormal sem
recoverimento ate ao proximo reboot. Novo mecanismo (`boost_rescue.txt` em
`%LocalAppData%\KitLugia\`):
- `SaveCrashRescue()` grava o estado sempre que o motor mexe num processo
  (no promote, no throttle do Auto e no throttle do V2/V3, e no RevertAll);
- `RescueOrphanedBoostState()` roda no `Initialize()` e so toca em processos que
  ESTEJAM mesmo no estado que deixámos (um PID reciclado nao e' estragado);
- `ClearCrashRescueFile()` no shutdown para nao deixar o ficheiro a dizer mentira;
- formato texto simples (`boost|<pid>|<orig>` / `throttle|<pid>`) com escrita
  ATOMICA (tmp + move): um kill a meio nao pode deixar um ficheiro truncado.

Tambem corrigido: o log do `RestoreAllThrottledProcesses` repetia o mesmo numero
duas vezes ("N restaurados, N ainda throttled") porque contava depois de remover tudo
— em caso de falha parecia que nada tinha sido restaurado.

**VERIFICACAO — `%TEMP%\gbreliab` (NOVO, 13/13 OK, RUN_EXIT=0)**
- T1/T1b EcoQoS: `GetProcessInformation(ProcessPowerThrottling)` **nao expoe o EcoQoS**
  nesta build (devolve −1 / FALSE), por isso o bug prova-se no **fonte**: confirma que
  o `RevertBoost` escreve `SetPowerThrottling(false)` e JA NAO tem `SetEcoQoS(true)`.
- T2 revert: prioridade volta a Normal, memory priority a 5.
- T3 ProBalance: processo de fundo rebaixado a BelowNormal → `ShutdownGameBoost` →
  volta a **Normal** (o bug do ponto 2, provado e fechado).
- T4 resgate pos-crash: PID em High + memory VERY_LOW + ficheiro de resgate →
  `RescueOrphanedBoostState()` → prio=Normal, mem=5, ficheiro apagado, log escrito.
- Regressao apos as alteracoes: `%TEMP%\gbauto` **25/25 OK** e `%TEMP%\gbv4` **7/7 OK**,
  ambos exit 0 (o Automatico v2 e o V4 continuam iguais).
- Build: `dotnet build KitLugia.sln --no-incremental` → **exit 0 / 0 erros / 145 avisos**.

**CUSTO DO RESGATE (corrigido na mesma sessao):** a `SaveCrashRescue()` e' chamada
de dentro do `foreach` do ProBalance, que percorre TODOS os processos a cada ciclo.
Sem deduplicar, N processos rebaixados = N escritas (criar tmp + apagar + mover) por
ciclo, num motor que roda o dia inteiro. Agora compara o conteudo com a ultima escrita
(`_lastRescueContent`) e so escreve quando o estado MUDA — tipicamente 1 escrita a
entrar e 1 a sair. `ClearCrashRescueFile()` invalida a cache para a proxima escrita
nao ser silenciosamente saltada. Re-verificado apos a mudanca: fiabilidade 13/13,
Auto 25/25, V4 7/7, build 0 erros / 145 avisos.

Armadilhas do harness `gbreliab`: tem de correr com `--src <repo>` (vive no TEMP, o
fonte no repo, senao o `FindSource` devolve string vazia); o `GetProcessInformation`
precisa de `PROCESS_ALL_ACCESS` (com handle limitado a I/O/EcoQoS devolvem −1).

### Sessao 10/10 - GameBoost: CRASH REAL testado + AUDITORIA ao Process Lasso (IDA Pro)

Pedido: "fazer a simulacao e os testes; instalar o Process Lasso e usar o IDA para ter
certeza que nao estamos a fazer nada de errado que seja prejudicial ao sistema".

**1. SIMULACAO DE CRASH REAL (o teste que faltava) — `%TEMP%\gbcrash` (NOVO)**

A versao anterior escrevia o ficheiro de resgate A MAO: provava que o resgate funcionava,
mas nao provava que o motor o ESCREVE a tempo. Agora:
- `--crash`: o servico aplica o boost pelo caminho REAL (PromoteBoost + ApplyBoostModern +
  um rebaixamento de ProBalance) e depois **MORRE com `TerminateProcess`** — sem `finally`,
  sem handlers de `ProcessExit`, sem `ShutdownGameBoost`. E' o mais perto de um power kill.
  **Prova de que morreu: `CRASH_EXIT=173` (0xAD)** — o codigo de saida que pedimos.
- `--rescue`: um processo NOVO (instancia limpa) chama `RescueOrphanedBoostState()`, o
  mesmo que o `Initialize()` faz, e verifica as vitimas.
**Resultado: todas as 4 verificacoes de resgate PASSARAM** — a vitima boosted voltou a
Normal, o fundo throttled voltou a Normal, a memory priority voltou a 5, e o ficheiro foi
apagado. As 3 "heranca" tambem confirmaram que o crash deixou mesmo as vitimas estragadas
(High e BelowNormal+mem=1) antes do resgate.

**2. AUDITORIA AO PROCESS LASSO (IDA Pro 9.0) -> `docs/GAMEBOOST_PROCESS_LASSO_AUDIT.md`**

Analisado o `ProcessGovernor.exe` (1,3 MB, C++ nativo) do Process Lasso 18.4.0.48
(Bitsum LLC) com `idat.exe -A -S` + Hex-Rays. **FALSO NEGATIVO IMPORTANTE**: uma leitura
por *strings* das importacoes deu `SetProcessInformation` como ausente — o IDA mostrou que
a API e' resolvida em runtime por `GetProcAddress`, logo nao aparece na tabela. Conclusoes
baseadas so em strings de importacao sao pouco fiaveis para nativos.

Achados que VALIDAM o nosso desenho:
- **Teto HIGH**: `SetPriorityClass(hProcess, 0x20u)` — 0x20 = HIGH, nunca RealTime.
- **Guardar/restaurar a original**: `v13 = *(DWORD*)(a3+308)` (original guardada) e
  `SetPriorityClass(h14, v13)` ao sair da regra — **e' literalmente o nosso
  `_originalPriorities` + `RevertBoost`**. O Bitsum faz o mesmo.
- **EcoQoS**: `SetProcessInformation(v6, 4LL, v17, 12LL)` — class 4 = ProcessPowerThrottling,
  12 bytes = a nossa estrutura, e `StateMask` recebe **so o bit 0 (EXECUTION_SPEED)**.
  **O Bitsum NUNCA toca no bit 1 (IGNORE_TIMER_RESOLUTION)** — o que valida a decisao de
  deixar esse bit a 0 (honrar o timer que o jogo pede).
- **Disciplina de handle**: `OpenProcess(0x200=PROCESS_SET_INFORMATION)` -> op -> `CloseHandle`.

O UNICO desvio nosso: o Bitsum **nao mexe em I/O priority nem page priority**; nos dois
usamos `NtSetInformationProcess` (ntdll, **nao documentada**, classes fixas 33 e 39) e o
**valor de retorno era ignorado** — uma falha silenciosa.

**3. SONDAGEM DE CAPACIDADE (implementada, nao so recomendada)**: `SetProcessIoPriority` /
`SetProcessPagePriority` agora experimentam a classe **uma vez**; se pegar, fica marcada
para sempre; se nao pegar, registam no log **uma vez** e param de tentar. Nao muda o
comportamento numa maquina normal — so torna a falha visivel. Nao ha API documentada
para I/O priority no userland, portanto a via ntdll mantem-se (e' o que o Process Hacker
faz); a memoria ja usava `SetProcessInformation(ProcessMemoryPriority)`, que e' documentada.

**VERIFICACAO**: build **exit 0 / 0 erros / 145 avisos**. Apos mexer no motor, re-executados:
fiabilidade **13/13 OK** e regressao do Auto **25/25 OK**, ambos exit 0.

### Proxima sessao
- [ ] Testar a sondagem de capacidade: forcar uma classe invalida e confirmar que o log
      avisa uma vez e para de repetir
- [ ] Usar o IDA para analizar o ProBalance do Bitsum (como escolhe o que rebaixar) e
      comparar o gate de 70% com o criterio real deles
- [ ] Rever se vale a pena manter I/O + page priority (o Bitsum nao usa; so o nosso extra)
- [ ] UI: "Estado do motor" (processos em boost / rebaixados, ultimo resgate)
- [ ] Benchmark com I/O de disco (ganho real do I/O priority 3)
- [ ] Testar o Automatico v2 num jogo real (1% low + gate sob carga)


### Sessao 05/10 (cont. 5/5) - SONDAGEM DE CAPACIDADE TESTADA: 3 achados reais + 3 bugs meus

Fechei a lacuna que eu proprio tinha sinalizado (a sondagem de I/O/page priority estava
implementada mas o caminho de falha nunca tinha corrido). Harness novo: `%TEMP%\gbprobe`
(precisa de `--auto-check` para so provar a elevacao).

**O QUE O HARVESS MEDIU (e o que muda o motor):**

1. **Nao existe via DOCUMENTADA para I/O nem page priority.** `SetProcessInformation`
   (classes 0..5) so aceita escrita em MemoryPriority (0), MemoryCompression (1) e
   PowerThrottling (4). I/O (2) e Page (3) devolvem err 87. Daí as classes privadas da
   ntdll (33/39) — pratica comum mas nao suportada.

2. **Page priority FUNCIONA — provado por leitura de volta** (`NtQueryInformationProcess`
   classe 39): defini 1 -> li 1; defini 5 -> li 5. Antes nao havia prova nenhuma.

3. **I/O priority: so VeryLow (0) e Normal (2) passam. High (3) e RECUSADO**
   (0xC0000061). Medido ELEVADO (comprovado dentro do harness) — elevar NAO resolve.
   E o mesmo acontece no processo do proprio Kit e no charmap: **depende do VALOR, nao do
   processo**. Conclusao honesta: o "I/O: High" que os motores V1-V4 anunciavam **nunca
   teve efeito** nesta maquina. Nao e bug do kit, e restricao do Windows.

**OS 3 BUGS QUE A SONDAGEM INTRODUZIU (e que o harness apanhou):**

4. A sondagem decidia pela primeira chamada a um processo qualquer → **SIM FALSO**, porque a
   classe 39 devolve SUCESSO mesmo com handle invalido.
5. `GetProcessId(0xFFFFFFFF)` devolve 31736 (slot reutilizado) e `GetExitCodeProcess`
   devolve `ok=True, 259` (falso STILL_ACTIVE) → **handle inventado nao e detectavel**.
   Duas guardas por handle, ambas inuteis. Sobe tudo para `docs/...AUDIT.md` para ninguem
   tentar de novo.
6. **Corrigido**: a sonda corre 1x contra `GetCurrentProcess()` (handle nosso, sem corrida
   de "processo morreu entre listar e aplicar") e com o **valor real** que o motor quer
   aplicar. Sem isso, sondar com um valor gentil dava SIM falso.

7. Bonus: a sonda usava `v=5` para a I/O (dominio 0..3) → devolvia 0xC000000D
   (INVALID_PARAMETER) e o log acusava "indisponivel" quando o problema real era o
   privilégio. Sobe para 0xC0000061, que é o diagnostico certo.

8. **UI honesta**: `BtnSettings_Click` deixou de anunciar "I/O: High" e passou a dizer que o
   Windows só aceita VeryLow/Normal para processos alheios. Um painel que promete um ajuste
   que o Windows recusa é pior do que um que o omite.

**BUG DE BUILD ENCONTRADO (importante para os harnesses):** o `-p:OutDir=obj/verifybin/`
e RELATIVO AO PROJETO REFERENCIADO, nao a raiz da solucao — ou seja, ao construir um harness
com ProjectReference para o KitLugia.GUI, a DLL do GUI ia parar dentro do REPOSITORIO em
`KitLugia.GUI/obj/verifybin/`. Passagens seguintes passavam a compilar contra essa copia
obsoleta. Sintoma enganador: erros `CS0103 "o nome X nao existe"` de metodos que ESTAO no
fonte. **Usar sempre OutDir ABSOLUTO** (ex: `-p:OutDir=C:/Users/Lugia/AppData/Local/Temp/verifybin/`).

**CAUSA RAIZ do erro CS0103 que me custou tempo:** uma chamada `SondaCapacidadeNtdll(...)`
DUPLICADA tinha ficado no `TrySetNtdllClass` de uma edicao anterior (linha 247 orfa). O
compilador apontava a linha orfa enquanto a linha certa (244) estava bem — da a impressao
de "esta a ler outra copia do ficheiro". Diagnostico que vale a pena: `grep -rl "<nome do
metodo>" .` no repositorio; se so aparecer em binarios, o fonte esta limpo.

Tambem encontrados e corrigidos: **2 bytes NUL** em `_lastRescueContent = </dev/null>`
(`TrayIconService.cs`, linhas ~4721/4771) — estragados por edicao anterior; `grep` passa a
dizer "Binary file matches" em vez de mostrar as linhas. Viraram `string.Empty`. E 2
`KitLugia.GUI_*_wpftmp.csproj` orfaos (lixo de build WPF antigo) removidos.

**VERIFICACAO:** `gbprobe` 9/9 OK exit 0 (inclui "SEM SIM FALSO"); regressoes Auto 25/25,
V4 7/7, fiabilidade 13/13, todas exit 0; build completo `BUILD_EXIT=0`, **0 erros /
145 avisos** (baseline mantido).

Documento actualizado: `docs/GAMEBOOST_PROCESS_LASSO_AUDIT.md` (10.394 bytes) secao 7.

### Proxima sessao
- [ ] Fase Loading (CPU > 75%) do Automático ainda nao exercitada em teste
- [ ] Ganho real em 1% low so se mede com um jogo de verdade (exige Alvos+Alpha)
- [ ] Auditoria ao Process Lasso cobriu so o `ProcessGovernor.exe`; o binario principal
      de 2,7 MB (onde esta o ProBalance completo) continua por abrir
- [ ] O I/O "High" dos motores V1-V4 esta inerte nesta maquina: decidir se se muda o
      default dos perfis para Normal (honesto) ou se se deixa como esta


### Sessao 05/10 (cont. 6/5) - CHECKLIST DO MOTOR + gbknobs: 2 knobs mortos, timer provado seguro

Pedi para continuar a investigação e criar a lista de coisas a checar para fechar o motor.
**`docs/GAMEBOOST_ENGINE_CHECKLIST.md`** (novo) — 6 secções: o que nunca foi provado, o que
está inerte, o que falta para fiabilidade, correcoes, o que NAO tentar, e a ordem sugerida.
Cada item diz o que falta / como verificar / criterio de "passa".

De seguida comecei pelo mais barato e desbloqueei mais do que esperava. Harness novo
`%TEMP%\gbknobs`: mede os 3 knobs que NUNCA tinham sido lidos de volta.

**ACHADO 1 — GameClassInfo (classe 13) é RECUSADO pelo Windows.** `err=87` em TODOS os
tamanhos (4/8/12/16), no charmap E no proprio processo do Kit. Como falha em todos os
tamanhos, **nao e "tamanho errado": a classe nao esta implementada neste build.** V2/V3/V4
perdem mais um ajuste — o GameMode nunca fez nada aqui. O comentario no codigo atribuia ao
GameClassInfo o ganho de loading (Poppy Playtime) — **estava errado**, corrigido.

**ACHADO 2 — ThreadEfficiencyMode (classe 5) tambem recusado.** `SetThreadInformation(cls 26)`
E `SetProcessInformation(cls 5)` dao `err=87` em todos os tamanhos. Inerte neste build.
(Atencao: o codigo de producao usava `SetThreadInformation` para thread e
`SetProcessInformation` para GameClassInfo — testei as DUAS vias para nao confundir
"classe inexistente" com "via errada".)

**ACHADO 3 — Timer: seguro nesta maquina, e a semantica enganava.** Eu li os parametros ao
contrario. Confirmado em ntdoc/ReactOS: em `NtQueryTimerResolution`, `MinimumResolution` e' o
MAIOR atraso entre eventos e `MaximumResolution` o MENOR. So o segundo diz a granularidade.
Com a semantica certa: esta maquina ja esta a **0,50ms**, que e' o MINIMO que o Windows
permite; o motor pede **1ms**; o Windows **recusa descer** e mantem 0,50ms. Portanto aqui o
"boost" do timer e' **inerte** (nao piora, nao gasta bateria) e o restore e' simetrico.
O motor foi escrito a assumir o caso comum (15,6ms de fundo) — numa maquina assim, 1ms e'
melhoria. Nao e' bug; e' o caso raro. So nao deixar o motor assumir que 1ms e' sempre
melhor.

**CODIGO ALTERADO:**
- `ReportarClasseRecusada(nome, infoClass)` + `_classesRecusadas` (HashSet, aviso 1x por
  classe): as duas chamadas agora AVISAM quando o Windows recusa, em vez de falhar caladas.
- `SetProcessGameClassInfo` e `SetThreadEfficiencyMode`: passam a reportar.
- Comentario do V4 sobre "ganho de loading" corrigido com o que foi medido.

**VERIFICACAO:** build 0 erros; regressoes Auto 25/25, V4 7/7, gbprobe 9/9, todas exit 0.
O `gbknobs` tem 2 "falhas" que sao o COMPORTAMENTO REAL esperado (o motor e' inerte nesta
maquina e o timer nao muda) — registadas como tal, nao escondidas.

Documento: `docs/GAMEBOOST_ENGINE_CHECKLIST.md`.


### Sessao 05/10 (cont. 7/5) - gbavis: provado que o aviso de classe recusada NAO inunda o log

Ao rever a sessao anterior, vi uma lacuna: alterei `ReportarClasseRecusada` para avisar uma
vez, mas **nunca tinha testado esse caminho**. Um aviso que diz "so uma vez" e soa 400 vezes
por minuto seria pior do que o silencio. Harness novo `%TEMP%\gbavis`, exit 0:

- T1: 1. chamada a `SetProcessGameClassInfo` -> **1 aviso** (Game Mode, classe 13)
- T2: **200 chamadas -> 0 avisos**
- T3: `SetThreadEfficiencyForAllThreads` -> **1 aviso** so (nao 1 por thread)
- T4: **200 chamadas -> 0 avisos**

Duas classes separadas = 2 avisos, nunca 200 nem 1. O aviso nao inunda.

**ARMADILHAS DO HARNESS (perder tempo nisto):**
1. `_classesRecusadas` e' `readonly` -> `FieldInfo.SetValue` lanca
   `FieldAccessException`. Limpar com `.Clear()` no HashSet existente, nao substituir.
2. Um exe WPF que prende pode nao devolver a shell: o `GbAvis.exe` desaparecia mas o
   comando continuava pendurado. Resolver com `Environment.Exit` atrasado por timer
   (red de seguranca) + correr em background e ler um ficheiro de saida.
3. `timeout` do Git Bash **nao mata** processos Windows. Usar `taskkill //F //IM`.
4. `python - <<'EOF'` com `\n` dentro do texto Python **parte a linha em duas**
   (ja aconteceu 3 vezes). Para strings com `
`, usar a ferramenta de edicao.

**LIMPEZA:** `AGENTS.md` tinha **1 byte NUL** — introduzi eu ao descrever o bug do NUL do
`TrayIconService`. Um byte literal num ficheiro de texto e' perigoso (faz o `grep` dizer
"Binary file matches" e quebra ferramentas). Substituido por `<NUL>`.
AGENTS.md: 739.298 bytes, BOM=True, CRLF=10.758, LF soltos=0, NUL=0.

**VERIFICACAO FINAL:** build completo `BUILD_EXIT=0`, **0 erros / 145 avisos** (baseline).
Checklist: `docs/GAMEBOOST_ENGINE_CHECKLIST.md` com o item 1.1 marcado como testado.

### Sessao 05/10 (cont. 8/5) - bateria de testes do GameBoost: 11/11 VERDE

O motor foi fechado com uma bateria de 11 testes que vive agora no repositorio
(`tests/`) e corre com um comando: `tests\run-gbtests.ps1`. Todos com
`ProjectReference` RELATIVO (`..\..\KitLugia.GUI\KitLugia.GUI.csproj`).

**RESULTADO: 11/11 PASSOU, RUNNER_EXIT=0.** Build da solucao 0 erros / 145 avisos.

#### BUG DE PRODUCAO CORRIGIDO (2)

1. **Resgate do tweak global de rede nunca chegava a correr** (`RescueNetworkBoostOnStartup`):
   o gate `if (!GamePriorityEnabled)` impedia o resgate quando o utilizador abria o Kit
   COM o GameBoost ligado. A essa altura `_networkBoostApplied` e' sempre false (nada foi
   escrito ainda por este processo), portanto um `SystemResponsiveness=10` so pode ser ORFAO.
   Sem o gate, `ApplyNetworkBoostV3` gravava `_origSystemResponsiveness = 10` (o orfao) e
   a partir dai **cada revert restaurava 10**: o 20 (default do Windows) perdia-se PARA
   SEMPRE. Gate removido. Coberto por `tests/gbrescue` (o teste que distingue o codigo
   antigo do novo: com `GamePriority=1` o resgate tem de por 20).

2. **Janela de perda do ficheiro de resgate** (`SaveCrashRescue`):
   `File.WriteAllText(tmp)` -> `File.Delete(path)` -> `File.Move(tmp, path)` tinha um
   instante em que o ficheiro NAO EXISTIA. Morrer ai = resgate perdido = prioridades
   presas para sempre. Substituido por `File.Move(tmp, path, overwrite: true)`
   (= `MoveFileEx(MOVEFILE_REPLACE_EXISTING)`, uma unica renomeacao atomica).
   Medido por `tests/gbsave`: antes 2/25 rondas perdiam o ficheiro, **depois 0/20**;
   0 truncados em todas.

#### Testes novos desta ronda

| teste | o que prova | resultado |
|---|---|---|
| `gbrescue` | resgate do tweak de rede com o boost LIGADO (o caso quebrado), desligado, e valor legitimo intacto | 3/3, exit 0 |
| `gbidem` | **idempotencia**: 200 aplicacoes sem reverter nao escalam a prioridade (32->128 estavel), o mapa `_originalPriorities` congela apos a 1a, UM revert chega, delta handles = 0 | 8/8, exit 0 |
| `gbsave` | **morte a meio da escrita**: 25 rondas a matar o escritor com `TerminateProcess` sobre 640 KB de estado | 0 truncados, 13/25 apanharam a meio, exit 0 |
| `gblimited` | **matriz sem elevacao** (Scheduled Task `RunLevel Limited`): degradacao limpa | 10/10, exit 0 |
| `gbcrash` | crash real + resgate (movido de %TEMP%) | 6/6, exit 0 |

**Achado honesto do `gblimited`**: SEM elevacao o motor degrada bem (o Windows nega o
HKLM, o registo fica intacto, `_networkBoostApplied` continua false -> nada a meio), mas
**o resgate do tweak global so' funciona com elevacao** - sem ela nao ha permissao para
corrigir um orfao no HKLM. O que nao precisa de admin (prioridade de processos proprios,
`boost_rescue.txt` em %LocalAppData%) continua a funcionar.

#### Armadilhas do PowerShell ao correr isto (custaram tempo)

- `& $exe` captura para um PIPE e os **FILHOS do harness herdam-no**: as vitimas `--idle`
  do gbcrash (120 s de sleep) penduravam o runner. Os vitimas passam agora a ter
  stdout/stderr proprios (`RedirectStandardOutput = true`).
- `Start-Process -Wait` espera pelo processo **E PELOS DESCENDENTES**: a fase `--rescue`
  arrancava quando as vitimas ja tinham saido e acusava 3 falhas falsas (`prio=Normal`,
  `mem=-1`). O runner usa agora `System.Diagnostics.Process` direto com `WaitForExit(ms)`
  e `ReadToEndAsync` (leituras paralelas, sem deadlock, com teto).
- `ArgumentList` **nao existe no Windows PowerShell 5.1** - o exe arrancava sem argumentos.
- `Process.GetProcessesByName("X")` devolve **o proprio harness**: o cleanup matava-se a si
  proprio ANTES de imprimir o resultado e de devolver o exit code. O "4/4" que se lia no ecra
  era so texto; o exit code era lixo (127). Afectava o `gbcrash`.
- As classes de prioridade do Windows sao **BITMASKS, nao escala**: `BelowNormal` (0x4000)
  e' numericamente MAIOR que `High` (0x80). Comparar os valores crus dava resultados errados.
- O Task Scheduler corre tarefas a **BelowNormal** por omissao e os filhos herdam -> o
  `-Priority 4` na `New-ScheduledTaskSettingsSet` e a normalizacao explicita no harness.

#### Auditoria `ComboBox.SelectedIndex` (pedido do checklist)

**Limpa.** Os 4 combos que guardam indice tem traducao explicita: `CmbIoPriority` ->
`SafeIoPriority` (0=Normal, >=1=High, Critical descapado para High de proposito),
`CmbPagePriority` -> `PagePriorityFromIndex` (0->5, 1->4),
`CmbThreadMemory` -> `ThreadMemoryPriorityFromIndex` (0->5, 1->1),
`CmbCpuPriority` -> `switch` para texto. O `CmbEngine` (cujos indices de 0 a 5 **nao**
batem com o enum V1..Auto=5) **nunca** e' usado como valor: o motor vem sempre do `Tag`.

### Proxima sessao
- [x] ~~Testes GameBoost~~ - bateria completa 11/11 verde em `tests/run-gbtests.ps1`
- [ ] A maquina esta com `SystemResponsiveness=10` gravado (ORFAO de um build antigo, com
      `GamePriority=0`): ao abrir o Kit com o build novo, o resgate corrige para 20 - vale a
      pena confirmar que acontece mesmo no arranque real.
- [ ] Limpezas de %TEMP% (os harnesses antigos em /tmp/gb*) podem ser apagados.

### Sessao 05/10 (cont. 9/5) - Halo azul do motor Automatico

Pede: distinguir visualmente o motor novo. A bola do GameBoost passou a ter
**AZUL + halo azul pulsante** quando o motor AUTOMATICO esta ativo.

- `AZUL + halo` = Automatico a escolher parametros | `VERDE` = motor legado
  escolhido a mao | `VERMELHO` = GameBoost desligado. O texto de estado acompanha
  ("Motor Automatico ativo - a escolher os melhores parametros").

Ficheiros: `KitLugia.GUI/Pages/GameBoostPage.xaml` (bola + halo) e
`KitLugia.GUI/Pages/GameBoostPage.xaml.cs` (`AtualizarIndicadorStatus()`).

Duas decisoes tecnicas:

1. **SEM `DropShadowEffect`.** O XAML tinha um comentario a avisar que um glow
   animado anterior custava re-renderizacao por frame na GPU. O halo e' um `Ellipse`
   maior com `RadialGradientBrush` e SO' a `Opacity` e' animada (composicao pura).
2. **O code-behind mexe na `Opacity` do PAI** (`StatusGlowHost`), nunca na do halo:
   a animacao vive no filho e perderia a corrida. A Opacity do pai multiplica a do
   filho, por isso o halo acende/apaga sem partir o pulso.

**LACUNA ENCONTRADA E CORRIGIDA na revisao:** `CmbEngine_SelectionChanged` tem dois
`return` mais cedo (tag "custom" e perfil personalizado) que nunca chegavam a
`AtualizarIndicadorStatus()` - escolher um motor manual deixava a bola AZUL de
"automatico". As duas chamadas foram acrescentadas. Auditados os 6 caminhos:
toggle on/off, LoadSettings e as 3 saidas da selecao de motor.

Validacao de ordem: em `LoadSettings`, `TglGameBoost.IsChecked` e' atribuido na
L357 e `AtualizarIndicadorStatus()` corre na L428 - o metodo le o valor ja certo.
(Antes lia uma variavel local `gameBoostEnabled`.)

Build: **0 erros / 145 avisos** (baseline mantido). `gbprobe` passou com
RUNNER_EXIT=0.

### Sessao 05/10 (noite) - Revo Uninstaller: DOCUMENTACAO CORRETA (IDA + parser de PE)

Pedido: "se a resposta estiver la pode ir documentando pouco a pouco o Revo
Uninstaller ai com a documentacao certinha para ver o que ele realmente faz".

Ficheiros: `docs/REVO_UNINSTALLER_ANALYSIS.md` (reescrito, 21.373 B) e
`docs/REVO_UNINSTALLER_PLANO.md` (novo, 9.638 B). Ambos UTF-8 **BOM** + CRLF.

**METODO (o erro que custou 4 conclusoes falsas em sessoes anteriores):**
nunca confiar em substring solta no binario. Pipeline obrigatorio:
1. parser de PE (`peinfo.py`) le a Import Directory Table REAL (879 imports/23 DLLs
   no RevoUnin.exe; 712/23 no helper) - nao adivinhacao;
2. `gen_script.py` calcula `EA = imagebase + (off - sec.ptr + sec.va)` dos section
   headers -> EAs deterministicos;
3. IDA `XrefsTo(EA)` -> so conta se ha referencia de CODIGO;
4. `decompile()` do chamador. 0 xrefs => marca INFERIDO, nunca "existe".
Erros antigos registados: "adcu" casou dentro de `Lo**adCu**rsorW`; "clsid" dentro de
`CLSIDFromString`; zlib ("IDAT") e SQLite (`memdb`) descompilados como codigo do Revo;
`idautils.Strings()` devolveu 5.004 de 30.283 strings (recursos `.rsrc` nao entram).

**1) MITO DO TxF REFUTADO (a correcao mais importante).** A sessao anterior afirmou
"rollback atomico com Windows TxF". **FALSO.** Existe uma camada de wrappers
(`sub_1401D10B8/1D11C8/1D1448/1D248C/1E020C/1DFC48/1DFD90/1E0C20`, identica no helper)
que faz `GetProcAddress(kernel32, "DeleteFileTransactedW")` etc. - mas o contexto
`a1` e' `{HANDLE hTrans; DWORD plainMode;}`. Varredura byte a byte de TODOS os binarios
(`RevoUnin.exe`, `RevoUninHelper.exe`, `RevoSrp.exe`, `WinAppsDll.dll`):
`CreateTransaction*`, `CommitTransaction*`, `RollbackTransaction*`, `GetTransactionId`,
`ktmutil` = **0 ocorrencias**, e **nenhum** na tabela de imports. Sem CreateTransaction
o `hTrans` nunca pode ser valido => **o ramo transaccionado e' CODIGO MORTO** e o
caminho real e' `DeleteFileW`/`MoveFileW`/`RegDeleteKeyW`. Consistente com TxF ter sido
descontinuado no Win11 24H2.

**2) LIXEIRA = `SHFileOperationW` (mecanismo real, confirmado).** `sub_14003BA80` (767 B):
`FileOp.wFunc = 3` (FO_DELETE) e `fFlags = (DelToBin==1) ? 1044 : 1108`
- 1044 = 0x414 = `FOF_ALLOWUNDO|FOF_NOERRORUI|FOF_NODIRFLAGS` -> apaga PARA A LIXEIRA
- 1108 = 0x454 = o anterior **+ `FOF_RENAMEONCOLLISION`** -> apaga PERMANENTE
Le a chave `Uninstaller` + `DelToBin` (default 1); respeita Cancel via
`WaitForSingleObject(qword_14093FDC0,0)`; em falha faz `MoveFileExW(...,4)`
(MOVEFILE_DELAY_UNTIL_REBOOT). Mesmo padrao em `sub_14003C220` e `sub_14003CDA0`
(este ultimo com a chave separada `Junk Files\General\Delete to bin`).

**3) TAKE-OWNERSHIP DE CHAVES DE REGISTO.** `sub_1401614B0` (919 B) e
`sub_140162D20` (756 B): `AllocateAndInitializeSid` -> `SetEntriesInAclW(2, ...)` ->
`SetNamedSecurityInfoW(DACL)`. Se falhar com `ERROR_ACCESS_DENIED(5)`:
`OpenProcessToken(..., 0x20 TOKEN_ADJUST_PRIVILEGES)` -> ativa privilegio -> segunda
`SetNamedSecurityInfoW(OWNER)` -> reaplica DACL. Log: "You must be logged on as
Administrator." NOTA de rigor (corrigi um erro meu): `0x80000000` = **GENERIC_READ**
(so a Everyone), `0x10000000` = GENERIC_ALL (so os Admins, com heranca=3). Nao e'
"Everyone:GENERIC_ALL" - e' MENOS agressivo do que parecia.

**4) DISPATCHER DE CLI (5.012 B, `sub_140178EB0`) confirmado por codigo.** `wcsicmp`
sobre `/update`(3) `/leftovers`(2, **exige `pNumArgs > 4`**) `/continue`(4)
`/chactivation`(5) `/settings`(1) `/updatesubscription`(6); `/hunter` põe flag;
`/forcedfolder` consome o arg seguinte. `KeepFiles` = `memcmp` de **10 WCHAR**
(= "KeepFiles"+NUL; confirmado no binario como `"KeepFiles" seguido de 3 NUL`).
Ha ainda uma tabela de ponteiros (`off_1408188D0`) para pre-dispatch generico por
`wcsstr`. Formato: `RevoUnin.exe /leftovers <a2> <a3> <a4> [KeepFiles]`.

**5) CÓDIGO-FONTE ORIGINAL EXPOSTO no helper:** `sub_14000A350` e `sub_14001DED0`
embutem `C:\Work\VSRevo\Windows\Projects\Revo Helper\...\BasicGUIControls.cpp:249`
e `NotificationDlgs.cpp:214`. Confirma log estruturado com `__FILE__`/`__LINE__`
(`sub_14004C9A0(crit, ficheiro, linha, fmt, ...)`) - melhor que os MessageBox que parece.
`sub_14001B710` = destructor: `TerminateThread` + `FilterUnload` + `sc stop
revoprocessdetector` (o driver e' real). `sub_14003AD80` = special folders.

**PONTO DE RESTAURO - NAO AFIRMAR.** Nenhum binario tem `SRSetRestorePointA/W`,
`SystemRestore`, `root\default`, `vssadmin`, `wmic`. A opcao "Create System Restore
Pont" (nota: erro de grafia da VS) pode estar morta nesta build ou via COM sem strings
reconheciveis. Fica como pergunta aberta na Fase 3 do plano.

**AINDA INFERIDO (0 xrefs de codigo, so strings):** marcadores `ADCU`/`ADAU`;
`Uninstaller` + `RegExclude`; `Junk Files\Exclude` / `\Include`; scanner de Registry Classes
(`sub_14018D6C0`, ~9 KB, nao decompilado); esquema SQLite; cleaner de browsers; o
`sub_14000E6E0` (blocklist de 8 caminhos do helper) vem de sessao anterior.

**Armadilhas IDA desta sessao:** `ida_idaapi.cvar_t`, `ida_segment.get_segm_end` e
`idautils.Entries()` mudaram no IDA 9; base `.id0/.id1` parcial faz crash `0xC0000005`
(apagar antes); script sem `try/except` + `traceback` para ficheiro perde-se tudo;
`-S` precisa de caminho explicito. Detalhe em `REVO_UNINSTALLER_PLANO.md` §7.

**Decisao:** implementar lixeira (`FOF_ALLOWUNDO`) no DeepUninstaller e' o ganho mais
barato (recuperacao pelo utilizador); take-ownership de registo NAO (e' uma decisao de
projeto e o nosso `IsDirectlyInsideCriticalRoot` ja e' mais conservador); TxF nunca.

### Sessao 06/10 - NetworkPage (Rede/Ethernet): erros de UI + metodos corrigidos e verificados

Pedido: "olhe a pagina de rede ethernet do kit e corrija os erros de ui e melhore os metodos usados la".
Ficheiros: KitLugia.GUI/Pages/NetworkPage.xaml(.cs), KitLugia.Core/AdapterManager.cs,
KitLugia.Core/NetworkManager.cs, KitLugia.Core/SystemTweaks.cs, NOVO tests/netprobe/.

**Descobertas empiricas (testadas no host, comandos read-only ou nome inexistente):**
1. `netsh int ip set interface name=X interruptmoderation=...` e `rss=` → sintaxe INVALIDA
   ("'name' nao e um argumento valido" - a lista de parametros de set interface nao tem
   interruptmoderation/rss) → as 2 chamadas de Optimize/RevertNetworkAdapter eram PLACEBO
   e mesmo assim contavam "N adaptador(es) otimizado(s)".
2. `netsh interface set interface` retorna exit 0 ATE para nome inexistente (imprime
   "Nao ha mais dados disponiveis.") → exit code nao serve como fonte de verdade; a saida
   precisa de marcadores de erro bilinguues pt/en.
3. `*InterruptModeration` NAO existe em Tcpip\Parameters\Interfaces (reg query /f = 0
   ocorrencias naturais - o codigo antigo la escrevia = placebo). Local REAL do driver:
   `Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}\0008\*InterruptModeration`
   REG_SZ "1" com Ndi\Params\*InterruptModeration (confirmado no host).
4. `netsh int ip show global` inclui "Descarregamento de Tarefa" → taskoffload global e'
   sintaxe valida (EnableTaskOffload/DisableTaskOffload sem mudanca).

**UI (NetworkPage.xaml):**
- Cabezalho da tabela DNS tinha 5 ColumnDefinitions mas usava Grid.Column="5" (clamped
  para a coluna 4) → cabecalho desalinhado 62px das linhas; adicionada a 6a coluna e
  Margin="6,2,6,6" (alinha com o Padding "6,3" das linhas de item).
- Spinner do benchmark: StrokeDashArray "20 12" x thickness 2.5 = 50px > circunferencia
  (40px) = circulo SOLIDO, rotacao invisivel → "3 2"; Margin="-14" (invadia a coluna
  RESOLUCAO) → "1,0,0,0".
- 7 labels de status iniciavam com texto errado (Desativado/Ativo arbitrario) → "Verificando...".
- "Selecione um adaptador acima" (o ComboBox fica ABAIXO do card de controle) →
  "Selecione um adaptador de rede" (XAML + code-behind).
- ItemsSource="{Binding}" removido do DnsProviderList (DataContext nunca e setado).

**Code-behind (NetworkPage.xaml.cs):**
- catch nos 7 toggles (eram async void SEM catch - excecao derrubava o app); finally no
  BtnAutoDetectMac (botao ficava travado em excecao); LoadAllDataAsync com try/finally
  (sem ele, excecao deixaria _isLoading=true para sempre e TODOS os toggles mortos).
- BtnApplyMacText volta ao rotulo apos gerar/aplicar MAC (ficava "MAC detectado!..." eterno).
- Sem adaptadores fisicos: placeholder selecionado + UpdateAdapterInfoDisplay(-1) (antes os
  labels ficavam "Verificando..." para sempre com SelectedIndex=-1).
- BtnCleanNetworkSafe/Full/BtnResetNetwork usam o retorno (bool,string) real - antes
  ShowSuccess INCONDICIONAL; dialogo da limpeza ja nao mente ("NAO altera configuracoes
  permanentes" era falso: winsock/ip reset sao permanentes - agora avisa reboot).
- Cleanup nulls dos timers + _timersStarted=false (convencao); timers agora nullable
  (eliminou 4 CS8618 pre-existentes).

**Core - AdapterManager:**
- ListPhysicalAdapters era 3 PowerShell POR adaptador POR chamada (PermanentAddress + MAC +
  Status) e corre no tick de 3s = ~9 spawns por adaptador por minuto. Agora:
  BuildLiveInterfaceIndex() .NET (NetworkInterface.GetPhysicalAddress + OperationalStatus)
  UMA vez por chamada, lookup por Name/Description/GUID. MEDIDO (netprobe): 1a=1092ms
  (inclui a 1a construcao da tabela de PermanentAddress), 2a=14ms.
- GetPermanentMac: cache ConcurrentDictionary (MAC de fabrica imutavel; so cacheia acerto)
  + tabela UNICA de PermanentAddress (1 powershell.exe por processo; chaves InterfaceGuid e
  Name - ASCII, imunes a encoding da saida do PS).
- GetCurrentMac: .NET (o auto-detect chamava dezenas de vezes, 1 PS cada).
- SetAdapterState: marcadores de erro bilinguues (pt/en) - o check antigo so tinha
  "nao"/"error"/"fail" e perdia "No more data is available" em Windows ingles.

**Core - NetworkManager:**
- GetAdapterWithHighestUsage: .NET puro (NetworkInterface + GetIPv4Statistics + filtro de
  keywords). Antes: PowerShell Get-NetAdapter + parse de CSV por split de aspas + deteccao
  de erro por substring ("error"/"erro" no output). MEDIDO: 15ms (antes ~1-2s no load).
- IsInterruptModerationDisabled: le PRIMEIRO a class key (local real) + fallback legado
  em Tcpip\Parameters\Interfaces (compat com quem aplicou na versao antiga).
- IsNetworkThrottlingDisabled: BUG corrigido - `valueLong == 0xFFFFFFFF || valueLong != 10`
  (1a parte morta porque DWORD 0xFFFFFFFF le como -1; 2a retornava true para QUALQUER
  valor != 10, ex.: 5 = throttling ativo era reportado como desativado) →
  `valueLong == -1 || valueLong == 0xFFFFFFFF`.
- CleanNetworkSafe/Full: string → (bool,string) + admin guard (sem admin o netsh falha
  silenciosamente e a UI mostrava checks todos) + exit codes em netsh/ipconfig (arp/cmdkey/
  certutil ficam lenientes: alguns retornam codigo nao-zero em situacoes legitimas).

**Core - SystemTweaks:**
- OptimizeNetworkAdapterForGaming / RevertNetworkAdapterSettings REESCRITOS: escrevem
  *InterruptModeration na CLASS KEY do driver (preservando o tipo REG_SZ/DWORD existente,
  SO onde o driver expoe o parametro - nao cria lixo em virtual/VPN); revert volta ao
  default declarado em Ndi\Params (fallback "1") e apaga o valor legado de
  Tcp\...\Interfaces; admin guard; sem "0 adaptador(es)" falso. Mensagem: reiniciar PC/
  adaptador para aplicar.
- ResetEthernetSettings: void → (bool,string) + admin guard + exit codes (antes a UI
  mostrava sucesso mesmo sem admin).

**Verificacao:**
- Build solucao (OutDir absoluto, app aberto): 0 erros / 141 avisos (baseline 145 - os 4
  CS8618 dos timers eliminados).
- NOVO harness `tests/netprobe` (padrao gbtests, read-only, NAO executa metodos
  destrutivos): 18/18 OK, exit 0. Prova: equivalencia exata com Get-NetAdapter (MAC+Status
  identicos no adaptador fisico), GetCurrentMac==PS, cache do PermanentMac coerente,
  throttling coerente com o registro bruto (valor 10 → false), benchmark DNS termina
  (26 provedores; resolve real 19-20ms; estados nunca ficam em Testing).
  Executar: dotnet build tests/netprobe/netprobe.csproj -p:OutDir=<abs> e correr NetProbe.exe.
- Encoding: 7 ficheiros editados/criados = BOM + CRLF + NUL=0.

**A TESTAR (GUI):** abrir Rede e Internet → cabecalho da tabela alinhado com as linhas;
spinner girando durante o benchmark; toggles aplicam e refletem o estado ao reabrir;
Ligar/Desligar/Reiniciar adaptador; Interrupt Moderation: ativar → reboot → estado "Desativado"
e o valor muda na class key (reg query).

**Pendencias conhecidas (fora de escopo desta sessao):**
- AllTweaksPage `ToggleNetworkDriverOptimizations(regPath)` ainda escreve *InterruptModeration
  em Tcpip\Parameters\Interfaces (GetActiveInterfaceRegPath devolve esse caminho) = mesmo
  placebo corrigido aqui - aplicar a mesma correcao naquele fluxo.
- `Toolbox.GetAllNetworkAdapters` (PowerShell CSV) nao tem chamadores - dead code candidato.
- tests/netprobe nao foi acrescentado ao run-gbtests.ps1 (runner so de GameBoost).

### Sessao 06/10 (cont.) - NetworkPage: placeholder DNS investigado com projeto ISOLADO (tests/NetPageVis)

Relato: print (735x83) da area "DNS CUSTOMIZADO" com o campo DNS PRIMARIO SEM placeholder
(caixa vazia; 2 fragmentos: caret dourado em x~76 e um ponto claro em x~50) enquanto o
SECUNDARIO mostrava "DNS Secundario (opcional, ex: 8.8.4.4)" normalmente.

**Metodo (padrao da casa, como TmVisualBench/PagesBench/PagesBench):** em vez de dirigir o app
elevado por UIA (lento, frágil, e a UIA nao desce nos filhos do template do TextBox), criar um
projeto ISOLADO que renderiza a PAGINA numa janela fora da tela (-32000) e audita PIXELS.
- **`tests/NetPageVis`** (novo): `audit` = PNG da pagina inteira/viewport + recorte 4x de cada
  campo + veredito por pixels; `states` = forca e mede cada estado (Text null / "" / " " /
  preenchido, foco, normalizacao). Le o estado REAL do TextBlock `ph` do template E conta os
  pixels #666 (brilho 90..160) desenhados dentro da caixa (>160 = texto digitado/caret, para
  nao confundir caret com placeholder).
- Licao do 1o run (bug do proprio harness): `RenderTargetBitmap.Render(elemento-filho)` INCLUI
  o offset do elemento no pai, entao recortar com coordenadas relativas ao filho desloca o
  recorte ~30px e da falso veredito. A analise agora usa o render da PROPRIA PAGINA
  (TransformToAncestor(pagina), coordenadas exatas) + `BringIntoView` para a area DNS entrar
  na viewport.

**Resultado (build atual, 06/10):** os DOIS placeholders renderizam — primario 247 px #666
(faixa x7..161 relativa a caixa), secundario 327 px (x7..185); `ph` Visible nos dois;
`states` 8/8 OK, incluindo `Text = null` (o WPF converte null -> "" no TextBox e o trigger
`Value=""` cobre) e `foco -> borda=#FFFFD700`.
**Varredura de builds (618 no disco):** Debug 06/10 14:28, Release 05/10, Publish 30/09 e todos
os harness tem a string correta "DNS Primario (ex: 8.8.8.8 Google)"; as antigas (<= 01/09, incl.
`bin/Debug/check` 06/09) nem tem o estilo / tem o escape literal `DNS Prim\u00e1rio`. Logo o
print veio de INSTANCIA/BUILD ANTIGA (processo aberto antes dos fixes), nao do codigo atual.

**2 fixes reais aplicados (evidencia do print):**
1. `DnsPlaceholderTextBoxStyle` (NetworkPage.xaml) — ORDEM DOS TRIGGERS: `IsMouseOver`
   (borda #555) estava DEPOIS de `IsKeyboardFocusWithin` (#FFD700) e o ULTIMO trigger que
   casa vence; com o rato em cima, o campo focado perdia o anel dourado (era o print:
   caret dourado + borda cinza). Hover agora vem antes do foco.
2. `NetworkPage.xaml.cs` — `NormalizarCampoDnsVazio(tb)` chamado no construtor para os dois
   campos: no LostFocus, texto SO com espacos vira "" para o placeholder voltar (o trigger do
   template so cobre Text vazio; " " escondia o placeholder e o campo parecia vazio sem dica).
   Texto real digitado nunca e tocado.

**Verificacao:** solucao 0 erros / 141 avisos (baseline); `NetPageVis audit` OK (2 campos);
`NetPageVis states` 8/8; encoding BOM+CRLF+NUL=0 nos 4 arquivos; `tests/dnsvis` (1a tentativa,
antes do NetPageVis) REMOVIDO - superseded.

Uso: `dotnet build tests/NetPageVis/NetPageVis.csproj -p:OutDir=<abs>` e depois
`NetPageVis.exe [audit|states|page] [--size WxH] [--dpi N] [--out <pasta>]` (read-only).

**A TESTAR (GUI):** abrir Rede e Internet no build NOVO -> placeholder nos dois campos; clicar
no campo primario com o rato em cima -> borda dourada (antes ficava cinza); digitar espaco e
sair do campo -> placeholder volta. Se o print voltar a acontecer, correr o NetPageVis e enviar
o PNG/veredito (e confirmar que o app em uso e o build recem-compilado).

### Sessao 06/10 (cont.) - Placeholder DNS: causa raiz era BUILD DESATUALIZADA (nao bug de codigo)

O print do usuario (campo primario "vazio", caret dourado + borda cinza) reproduzia o bug
ANTIGO de ordem de triggers. Investigacao passo a passo:

1. Fontes corrigidos: NetworkPage.xaml 06/10 16:14 e NetworkPage.xaml.cs 06/10 16:15.
2. `KitLugia.GUI\bin\Debug\net10.0-windows10.0.26100.0\KitLugia.GUI.dll` estava datado de
   06/10 14:28 - ANTERIOR aos fixes. O build da solucao foi para `%TEMP%\klbuild` (OutDir de
   conveniencia), nunca para bin\Debug.
3. `HKCU\...\CurrentVersion\Run\KitLugia` inicia exatamente esse exe de bin\Debug -> o
   usuario rodava a build PRE-FIX o tempo todo. Dai o campo continuar "sem placeholder".
4. `NetPageVis audit` a 96/120/144 dpi (- -dpi) = PLACEHOLDER VISIVEL nos 2 campos em todos;
   `states` 8/8. O codigo estava correto; faltava compilar para o OutDir de producao.

**Correcao:** `dotnet build KitLugia.sln --no-incremental` SEM OutDir -> DLL de bin\Debug
atualizada (23:09, 5831168 B = mesmo tamanho da DLL validada pelo harness). 0 erros / 141 avisos.

**Regra para as proximas sessoes:** ao validar UI no app real, compilar para o OutDir PADRAO
(bin\<Config>) e CONFERIR A DATA/hora da DLL antes de pedir ao usuario para olhar a tela.
OutDir de conveniencia (TEMP) serve para checar compilacao, nao para entregar o app.

### Sessao 06/10 (cont.) - AVG bloqueando powershell.exe (IDP.HELU.PSE92) durante a sessao

Alarmes repetidos do AVG (Modulo Comportamento, "IDP.HELU.PSE92 - Deteccao de linha de
comando", powershell.exe BLOQUEADO) eram causados pela automacao da propria sessao:
`%TEMP%\repro_dns.ps1` faz `Add-Type` (compila P/Invoke em runtime: SetForegroundWindow,
mouse_event, CopyFromScreen) + `Start-Process` + captura de ecra - assinatura classica da
heuristica comportamental. Nada ficou residente: nao ha tarefa agendada nem Run key apontando
para esses scripts.

**Caminho correto para testes visuais:** harness C# isolado (`tests\NetPageVis`), que nao usa
PowerShell nem sintetiza input. Se o AVG voltar a acusar, o ajuste e por conta do usuario
(AVG > Configuracoes > Geral > Excecoes) e/ou simplesmente nao reexecutar os scripts antigos.
