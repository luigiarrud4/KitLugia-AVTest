# ISO Editor Wimlib — Planejamento (12/08/2026)

## Objetivo
Redesenhar a aba "KIT ISO EDITOR" (`IsoEditorPage`):
- Trocar o motor lento (DISM mount/commit em 100% dos casos) por **wimlib-first**
  (modifica o WIM sem montar - 1-2s para injetar, x5 mais rapido na lista/edições).
- Corrigir **BUG CRITICO**: a ISO gerada era **NAO BOOTAVEL** (`IsoManager.CreateIso`
  usava oscdimg sem `-bootdata`).
- Visual "profissional" no estilo da `WinbootPage` (cards `CardStyle`, ComboBox de edições).
- Fluxo: selecionar ISO -> analise instantanea lista as edicoes -> escolher opcoes ->
  cria ISO rapidamente. Modo completo (DISM) fica como opcao para AppX/drivers/WinSxS.
- **Incorporar as tecnicas mais recentes de 2026** (pesquisa web) no editor.

## Pesquisa 2026 (fontes novas — 12/08/2026)
- **WinUtil Win11 Creator** (docs atualizados 2026-07-01): verifica ISO, dropdown de
  edicoes, remocao de 40+ apps bloat, remover OneDrive da imagem, bypass de requisitos,
  autounattend com conta local, desabilitar BitLocker/device encryption, desabilitar
  icone do Chat, **strip de edicoes** (~1-2GB por edicao, ja temos), **pin da edicao
  durante o setup** (ei.cfg/PID para OEM key nao forcar outra edicao) e **limpeza do
  component store**.
- **Win11IsoBuilder** (vqtuan90): **fix do Setup ConX 24H2/25H2** — injeta `winpeshl.ini`
  no boot.wim que lanca `setup.exe /legacy`, restaurando instalação desatendida nas
  versoes novas (o ConX ignora o unattend sem o /legacy). Tambem injeta driver Intel RST
  no boot.wim. Confirma o fluxo ESD->WIM + oscdimg dual boot.
- **NTLite v2026.04.10936** (2026-05-04): **remove Copilot e Windows Recall de imagens
  25H2** (DISM); **extracao multi-threaded** (padrao que adotamos com `-mmt=on`).
- **MS Learn "Add a Custom Script to Windows Setup"**: `%WINDIR%\Setup\Scripts\
  SetupComplete.cmd` roda com **privilegio SYSTEM logo apos o setup** (antes do primeiro
  logon), garantindo automacao de primeiro boot; `ErrorHandler.cmd` em erro fatal;
  SEM reboot dentro do script; log em `%WINDIR%\Panther\UnattendGC\Setupact.log`.
  -> Adotado: injetamos o SetupComplete.cmd no WIM via wimlib (sem montar) para lançar
  a `_KitLugiaSetup\bootstrap.bat` no 1º boot.

## Recursos 2026 implementados (12/08/2026)
1. **SetupComplete.cmd** (modo rapido, wimlib): injeta `/Windows/Setup/Scripts/
   SetupComplete.cmd` no install.wim — roda com SYSTEM pos-setup e executa
   `_KitLugiaSetup\bootstrap.bat` encontrado em qualquer letra. Checkbox `ChkSetupComplete`.
2. **Fix ConX 24H2/25H2** (modo rapido, wimlib update): injeta `winpeshl.ini` no
   `sources\boot.wim` index 2 lancando `setup.exe /legacy` — restaura instalação
   desatendida (padrao Win11IsoBuilder 2026). Checkbox `ChkConXLegacyFix`.
3. **Remoção Copilot/Recall 25H2** (modo rápido, registro no-mount — **SEM DISM**):
   políticas `WindowsCopilot\TurnOffWindowsCopilot=1` + `WindowsAI\
   DisableAIDataAnalysis=1` + `AllowRecallUnenrollment=0`, aplicadas via wimlib como
   qualquer outro tweak. Checkbox `ChkRemoveAI` (NÃO entra no deepMode).
4. **Política RemoveDefaultMicrosoftStorePackages** (modo rapido, registro): grava
   `SOFTWARE\Policies\Microsoft\Windows\Appx\RemoveDefaultMicrosoftStorePackages=1`
   (24H2/25H2) — remove apps padrão da Store no provisionamento e sobrevive a feature
   updates. Checkbox `ChkRemoveDefaultStorePackages`.
5. **Extracao multi-threaded**: `-mmt=on` no 7z (extracao completa + analise rapida) —
   acelera ISO de ~10GB (padrao NTLite 2026).

## Objetivo
Redesenhar a aba "KIT ISO EDITOR" (`IsoEditorPage`):
- Trocar o motor lento (DISM mount/commit em 100% dos casos) por **wimlib-first**
  (modifica o WIM sem montar - 1-2s para injetar, x5 mais rapido na lista/edições).
- Corrigir **BUG CRITICO**: a ISO gerada era **NAO BOOTAVEL** (`IsoManager.CreateIso`
  usava oscdimg sem `-bootdata`).
- Visual "profissional" no estilo da `WinbootPage` (cards `CardStyle`, ComboBox de edições).
- Fluxo: selecionar ISO -> analise instantanea lista as edicoes -> escolher opcoes ->
  cria ISO rapidamente. Modo completo (DISM) fica como opcao para AppX/drivers/WinSxS.

## Pesquisa (fonte)
- **wimlib** (ebiggers): em Windows NAO monta WIM, mas `extract`/`update`/`export`/
  `optimize` funcionam sem montar (README.WINDOWS.md). `wimlib-imagex` nao entende
  "packages" (appx) -> remocao de AppX continua DISM.
- **Chris Titus WinUtil** (`Invoke-WinUtilISOScript.ps1`): extrai ISO, `autounattend.xml`,
  remove `support\`, `ei.cfg`, drivers via DISM mount, empacota com oscdimg dual boot.
- **MS Learn** (editing registry of WIM without recapture, migreene/doxley): `reg load`
  num hive extraido + `reg unload` + commit = editar registro SEM aplicar/recapturar.
  No Kit isso vira: `wimlib extract hive` -> `reg load` -> `reg add` -> `reg unload` ->
  `wimlib update` (re-injetar) -> SEM MOUNT.
- **Win11IsoBuilder** (vqtuan90, .NET 8 WPF = mesmo perfil do Kit): confirma o fluxo
  extrator -> ESD->WIM -> drivers em boot/install.wim -> DISM mount p/ AppX -> autounattend
  -> oscdimg dual boot. Ou seja: AppX/drivers pedem DISM; o resto nao.
- **Forged** (fernbacher, Linux): export de SO UMA edicao + wimlib inject + xorriso —
  valida o passo "stripar edicao unica" como caminho de redução.
- **MODWIN** (01101010110): usa montagem completa (que queremos evitar) mas tem o menu
  de "LOADED REGISTRY HIVE" (reg load) — mesmo truque que adotamos sem o mount.

## Arquitetura nova

### 1. Motor wimlib (rapido, SEM montar) — `IsoEditorManager`
- `AnalyzeIsoAsync(wimPath)`: `wimlib-imagex info "<wim>"` -> indices + nomes/descricoes
  das edicoes. Detecta ESD (extensao .esd / solid).
- `ExportSingleEditionAsync(wim, index, dest, compress)`: `wimlib-imagex export`
  `--compress={lzms|lzx}` (strip da ISO multi-edicao + instalacao unica <= 4GB FAT32);
  ESD->WIM free.
- `InjectFilesIntoWimAsync(wim, index, files[])`: `wimlib-imagex update --command-file`
  (`add "local" /destino`), mesmo padrao do `WinpeBuilder` (~L1786).
- `ApplyRegistryTweaksNoMountAsync(wim, index, regAdds[], regDel[]):` extract das hives
  (SOFTWARE/SYSTEM/NTUSER/DEFAULT) -> `reg load HKLM\zXXX` -> `reg add` -> `reg unload`
  -> re-injetar via `wimlib update`. Fallback: DISM mount (modo completo).

### 2. Correcao de boot (bug) — `IsoManager.CreateIso`
oscdimg passa a incluir setores de boot quando existirem no conteudo extraido:
```
-bootdata:2#p0,e,b"<conteudo>\boot\etfsboot.com"#pEF,e,b"<conteudo>\efi\microsoft\boot\efisys.bin"
```
(mesmo padrao ja usado por `WinpeBuilder.GerarIsoFinalAsync` ~L922). Detecta tambem
`efi\microsoft\boot\etfsboot.com`. Sem os arquivos -> sem -bootdata (aviso no log).

### 3. GUI `IsoEditorPage` (estilo Winboot)
- Card ISO (CardStyle) + botao ANALISAR -> `ComboEditions` (DarkComboBoxStyle) com as
  edicoes da analise + info tipo WIM/ESD/tamanho.
- Card "Reducao" : "Manter apenas edicao [Combo]" + recompressao (LZMS/LZX).
- Card "Customizacao rapida (sem montar)": autounattend/eicfg padrao, _KitLugiaSetup,
  registry tweaks (bypass/telemetria/onedrive/etc), remover `support\`.
- Card "Customizacao profunda (DISM, lento)": injetar drivers, remover bloatware AppX,
  limpar WinSxS /ResetBase.
- Overlay de config + busy + log + footer LIMPAR LIXO / VOLTAR / INICIAR.
- Sem DISM em lugar nenhum: TODO fluxo e no-mount (wimlib + reg offline + pnputil + $).

### 4. Pipeline do INICIAR
1. Extrai ISO com 7-Zip (ja existe).
2. Localiza `install.wim`/`install.esd` + indice escolhido.
3. (opcional) strip+recompress da edicao escolhida via wimlib -> substitui install.wim.
4. Registry tweaks via wimlib (reg load/unload offline), bloat AppX (wimlib ls+delete + Deprovisioned),
5. Arquivos soltos: `autounattend.xml`, `ei.cfg`, `_KitLugiaSetup\*`, `.kitlugia` na media.
6. `support\` removida (se marcado).
7. ISO final via oscdimg bootavel (corrigido).

## Quirks / riscos
- wimlib `update` em WIM multi-imagem precisa de `--rebuild`? nao - o update in-place
  funciona; porem re-injetar hive grande pode exigir `wimlib optimize` depois. Testar.
- ESD (solid) nao aceita `update` in-place? wimlib permite modificar ESD transformando
  em WIM (o proprio `export` faz). Por isso: se a imagem e ESD e ha edicao >1 ou strip,
  SEMPRE exportar para install.wim antes dos tweaks.
- `reg load`/`reg unload` exigem admin (o kit ja roda elevado em ISO tools).
- Extrair hive + re-injetar muda o custo de stream: usar `--rebuild` no update se o
  wimlib reclamar de "would exceed" (excesso > 4GB em FAT32 nao se aplica aqui - UDF).

## Proximos passos / teste
- [x] Compilar (0 erros).
- [x] Recursos 2026 implementados (SetupComplete.cmd, ConX fix, Copilot/Recall, politica
      RemoveDefaultMicrosoftStorePackages, extracao -mmt).
- [ ] Testar analise de ISO (multi-edicao) -> lista + tipo.
- [ ] Testar strip edicao unica + recompressao -> reducao de tamanho.
- [ ] Testar registry tweaks SEM mount (reg load/re-inject) num install.wim.
- [ ] Testar SetupComplete.cmd injetado (bootstrap roda no 1º boot em VM).
- [ ] Testar fix ConX 24H2/25H2 (instalação desatendida funcionando em VM).
- [ ] Testar ISO final bootavel em VM (UEFI + Legacy).
- [ ] Comparar tempo: wimlib (minutos) vs DISM (20-40min).

## Sessao 12/08 - "So nativas": DISM 100% removido do editor

Pedido do usuario: usar somente ferramentas nativas/legais (wimlib, 7-Zip, registro offline),
nunca as ferramentas lentas da Microsoft. Implementado:

1. **AppX bloat sem DISM** (IsoEditorManager.RemoveProvisionedAppsNoMountAsync):
   - wimlib ls lista pastas de Program Files/WindowsApps (parse: `<num>\t<path>`, nome = ultimo segmento)
   - wimlib update --command-file deleta as pastas que casam o prefixo
   - Hive SOFTWARE: extract -> reg load -> delete `AppxAllUserStore\Applications\<fullname>`
     + add `AppxAllUserStore\Deprovisioned\<fullname>` (marcador MS Learn que impede
     re-provisionamento em feature updates) -> unload -> re-inject via wimlib.
     Espelha 1:1 o Remove-AppxProvisionedPackage (CleanupPackageFromPerMachineStore).
2. **Drivers sem DISM**: export com **pnputil /export-driver** (nativo, instantaneo) e copia
   para `$` na raiz da midia - o Setup.exe do WinPE varre recursivamente *.inf
   e injeta no driverstore do OS instalado (metodo documentado MS Learn). SEM boot.wim.
3. **WinSxS**: `wimlib optimize` (reconstroi WIM, remove espaco dos updates) - /ResetBase
   era inutil em midia nova (nao ha WinSxS\Backup).
4. **Scheduled tasks**: `wimlib update delete` (Windows/System32/Tasks/...) no-mount.
5. **Modo profundo DISM DELETADO** (IsoEditorPage): sem MountWim/UnmountWim/ApplyRegistryTweaks/
   InjectBootWimDrivers/DeleteScheduledTaskFiles (mount). Fluxo unico no-mount com todas as
   opcoes; UpdateModeHint = "Modo: NATIVO (wimlib + registro offline, sem montar)".
6. Helpers mortos removidos da page; CleanupIsoEdit sem mountDir.

Build: 0 erros / 0 avisos. A testar: fluxo completo com bloat+drivers+WinSxS marcados
(antes exigia montagem) - conferir no log "wimlib update delete", "Deprovisioned",
"pnputil", "copiados para $" e "WIM otimizado".