# PLANO: Downgrade de Build Insider -> Stable com preservacao de dados

> Documento de planejamento (31/07/2026) - KitLugia
> Objetivo: sair de um build Insider/Canary alto (ex.: 28000) para um build
> alvo mais antigo (ex.: 26300 / 26200) **sem perder arquivos, apps e settings**.

---

## 1. VEREDITO DA PESQUISA

**E possivel.** O bloqueio da Microsoft nao e tecnico, e uma decisao de produto:
o `setup.exe` so oferece "Manter arquivos e apps" quando a build instalada e
**igual ou mais antiga** que a build da midia. O check esta numa DLL unica:

```
sources/setupcompat.dll  (na MIDIA de destino, nao no sistema)
```

- Funcao: `ConX::Setup::Common::CWindowsVersion::IsLaterThan`
- Quando o setup detecta que a build instalada e "mais nova" que a midia,
  `IsLaterThan` retorna `true` (MOV eax, 1) e o modo preservacao de dados
  e desabilitado.
- **Patch**: alterar o byte `B8 01` (MOV eax, 1) para `B8 00` (MOV eax, 0)
  no final da funcao. O setup passa a tratar o downgrade como upgrade normal
  e habilita "Keep personal files and apps".

**Metodo comprovado na pratica** (thread Reddit r/Windows11 qtw8fq, TriATK):
- Testado: Win 11 Insider Dev **22494.1000** -> stable **22000.318** sem perder dados.
- Outro relato: **22518.1012** -> 22000.318 funcionou.
- Funciona tambem Win11 -> Win10 (com reparo da Store apos).

**Pontos fortes do metodo:**
- E um "upgrade install": se falhar no boot, o setup faz auto-rollback.
- Gera pasta `Windows.old` por 10 dias como rede de seguranca.
- Nao toca em rede, VPN/SSH keys, settings.

**Pontos fracos / riscos (aceitos pelo usuario):**
- Updates de qualidade do canal Insider ficam instalados e podem causar
  erros residuais (ex.: registry de mp3 invalido, Microsoft Store com
  problemas de verificacao). Limpar apos com o card "Remover KB" do kit.
- Configuracoes criadas no build novo podem ser "desconhecidas" para o
  build antigo (risco teorico, relatado como raro na pratica).
- Nao ha relatos publicos recentes (2024-2026) confirmando o patch em
  midias 24H2/25H2 - **precisa validar na ISO 26300 real** (Fase 2 do plano).

---

## 2. LINKS DE REFERENCIA (pesquisa completa)

| Fonte | Link | O que diz |
|---|---|---|
| Reddit - metodo original | https://old.reddit.com/r/Windows11/comments/qtw8fq/finally_find_a_way_to_upgrade_windows_from/ | Guia completo do patch setupcompat.dll (IDA + HxD) |
| MS Answers - sair do Insider | https://learn.microsoft.com/en-us/answers/questions/2357498/how-to-switch-from-dev-channel-to-release-preview | Metodo semi-oficial: apagar chave WindowsSelfHost + ISO in-place (funciona quando alvo e mais NOVO) |
| MS Answers - build antiga 25231 | https://learn.microsoft.com/en-us/answers/questions/5622679/escaping-ancient-insider-build-25231-will-the-regi | Mesmo procedimento; mod confirmou que funciona para 25H2 |
| Flyoobe discussao #201 | https://github.com/builtbybel/Flyoobe/discussions/201 | Pergunta igual a nossa (27881 -> 26100) - sem resposta, mas valida o interesse |
| W10UI README (abbodi1406) | https://github.com/abbodi1406/BatUtil/blob/master/W10UI/README.md | Instalador/integrador offline de updates (nao faz downgrade de build) |
| Instalacao manual de updates | https://woshub.com/manually-install-cab-msu-updates-windows/ | .cab -> DISM, .msu -> wusa |
| UUP Dump (site) | https://uupdump.net/ | Baixa builds especificas dos servidores MS e monta ISO (unico jeito de obter build 26300+ sem WU) |
| UUP Dump (fonte) | https://git.uupdump.net/uup-dump | Codigo fonte dos scripts |
| Guia UUP Dump (XDA) | https://www.xda-developers.com/uup-dump-windows-11-10-iso-update/ | Passo a passo do uup_download_windows.cmd |
| UUP Media Creator | https://github.com/gus33000/UUPMediaCreator/releases | Alternativa: UUP -> ISO |
| MediaCreationTool.bat (aveyo) | https://github.com/AveYo/MediaCreationTool.bat | Autor confirma: patch trivial de automatizar (tipo bypass TPM do winsetup.dll); ele desistiu por causa de updates residuais |
| HxD hex editor | https://mh-nexus.de/en/hxd/ | Editor hex usado no metodo manual |
| IDA Free | https://hex-rays.com/ida-free/ | Descompilador usado para achar a funcao |
| Rollback 25H2 (guia) | https://unanswered.io/guide/how-to-roll-back-downgrade-windows-11-feature-updates | Metodos oficiais: Go back 10 dias, uninstall enablement package, clean install |
| Pureinfotech UUP | https://pureinfotech.com/download-windows-11-insider-preview-build-iso | UUP Dump para builds preview (26300.8142, 28020.1797 etc.) |

---

## 3. O METODO PASSO A PASSO (referencia original)

1. Decompactar a ISO alvo em uma pasta (ex.: `C:\ISO\26300`).
2. Abrir `C:\ISO\26300\sources\setupcompat.dll` no IDA Free.
3. Buscar texto: `ConX::Setup::Common::CWindowsVersion::IsLaterThan`
   (Windows 10: marcar "Find all occurrences" e escolher o resultado curto).
4. Rolar ate o fim da funcao: achar `MOV eax, 1` (diz ao instalador que a
   versao atual e mais nova, entao nao pode preservar dados).
5. Anotar o offset e trocar `B8 01` por `B8 00` no HxD (Ctrl+G no offset).
6. Salvar e rodar `setup.exe` da pasta decompactada.
7. "Keep personal files and apps" aparece habilitado -> prosseguir.

### Pendo ao apos o downgrade (so se Win11 -> Win10):
- Apagar `StateRepository*` em `C:\ProgramData\Microsoft\Windows\AppRepository` (sob WinPE, senao BSOD)
- Re-registrar apps do sistema e da Store (add-appxpackage ...)
- Win11 -> Win11: nada a fazer apos.

---

## 4. NOSSO CAMINHO: automacao no KitLugia

Em vez de IDA + HxD manuais, o KitLugia vai:

### 4.1 Obter a ISO alvo (ja em andamento)
- UUP Dump gera pacote de download: **ja baixado** em
  `KitLugia.GUI\Tools\uup-dump\26300.9032_amd64_pt-br\`
  - Build: **26300.9032** (Insider Feature Update, x64, pt-br, Professional)
  - Arquivo `uup_download_windows.cmd` baixa os UUP files dos servidores MS
    (via aria2) e monta a ISO com wimlib (converter).
  - Requer: ~8-10 GB de espaco, admin, ~1h dependendo da internet.
- Alternativa quando o alvo for build "publica" (25H2 26200.xxxx):
  site oficial Microsoft + ID do pacote, ou UUP Dump igual.

### 4.2 Patch automatico da setupcompat.dll (componente novo: SetupCompatPatcher)
- Extrair ISO com 7z (kit ja tem `Resources/App/7Zip/7z.exe`).
- Abrir `sources/setupcompat.dll`, localizar a string
  `ConX::Setup::Common::CWindowsVersion::IsLaterThan` (ASCII e UTF-16).
- Encontrar a funcao (xrefs) e o padrao `B8 01 00 00 00` antes do retorno.
- Trocar por `B8 00 00 00 00`; manter backup do arquivo original
  (`setupcompat.dll.orig`) para restauracao.
- Logar offset patcheado; validar tamanho/locale da DLL.
- **Se o padrao nao for encontrado** (build nova mudou a funcao): parar e
  reportar - precisara da Fase 2 manual (IDA) para mapear o novo offset.

### 4.3 Remover inscricao no Insider (evitar "consertar" de volta)
- Backup e delecao de `HKLM\SOFTWARE\Microsoft\WindowsSelfHost`
- (Metodo semi-oficial do MS Answers - tambem remove a inscricao do programa)

### 4.4 Rodar o setup
- `setup.exe /auto upgrade /quiet /noreboot` (ou GUI para escolha manual)
- Moniitar saida; apos reboot, validar build com `winver`/registry.

### 4.5 Pos-instalacao
- O card "Controle de Updates (nao-Insider)" do KitLugia (ja implementado)
  lista KBs instalados e permite remover os residuais do canal Insider.
- Windows Update volta a funcionar normal (fora do Insider).

---

## 5. FASE 2 - VALIDACAO MANUAL (obrigatoria antes de automatizar)

Como nao ha relatos recentes do patch em midia 25H2, validar com IDA Pro 9.0 (batch IDAPython):

1. Gerar a ISO 26200.8973 (rodar `uup_download_windows.cmd` em `C:\uup\26200.8973_amd64_pt-br` — copiado de `KitLugia.GUI\Tools\uup-dump\26200.8973_amd64_pt-br` porque o script aborta com espacos no path).
2. Extrair ISO; analisar `sources/setupcompat.dll` com:
   `idat.exe -A -S"C:\ida_test\analyze_setupcompat.py" -L"C:\ida_test\ida.log" <setupcompat.dll>`
   (script procura string `IsLaterThan`, xrefs, desmonta o fim da funcao e acha o padrao `B8 01 00 00 00` com o FILE OFFSET; relatorio em `C:\ida_test\setupcompat_report.txt`).
3. Confirmar que `IsLaterThan` existe com o mesmo padrao `B8 01`.
4. Se mudou: mapear o novo offset/padrao e atualizar o SetupCompatPatcher.
5. Testar em VM (VMware Workstation ja instalado no host).

### RESULTADO DA FASE 2 (31/07/2026) - CONCLUIDA, PATCH CONFIRMADO na 25H2 26200.8973

- **A funcao EXISTE e esta intacta** na 25H2, so foi "empacotada" em nova estrutura:
  `CWindowsVersion::IsLaterThan` -> chamada por `CSystemAbstraction::HostIsNewer`
  -> consumida pelo checker `HostIsNewerCheckerImpl::OnInvoke` (Issue ID 11 = HardBlock,
  quando host > target). O nome decorado e preservado pela analise do IDA (sem PDB):
  `?IsLaterThan@CWindowsVersion@Common@Setup@ConX@@QEBAHAEBU1234@@Z` @ VA 0x180002CE4.
- **Ponto de patch (confirmado por decompilacao)**: FILE OFFSET **0x2DFC** na
  `setupcompat.dll` da midia = epilogo unico fundido do compilador
  (`B8 01 00 00 00 C3` = todos os "return 1", seguido de `33 C0 C3` = "return 0").
  **Patch: byte 0x2DFD `01` -> `00`** (vira `B8 00 00 00 00 C3 33 C0 C3`).
  Nao ha outro `mov eax,1` no corpo da funcao.
- **Verificado no IDA apos o patch**: a decompilacao da DLL patcheada mostra TODOS
  os `return 1LL` virarem `return 0LL` - `IsLaterThan` passa a sempre reportar
  "nao mais novo" -> `HostIsNewer` = false -> checker reporta `NoIssue` -> setup
  habilita "Keep personal files and apps".
- Notas tecnicas do batch IDA: usar `ida_auto.auto_wait()` no inicio do script
  (sem isso o decompiler so desmonta 1 instrucao e o corpo fica `JUMPOUT`), e
  apagar o `.i64` obsoleto antes de reanalisar o mesmo arquivo.
- Arquivos: DLL original extraida em `C:\ida_test\isofiles\setupcompat.dll` (374.248 bytes);
  DLL patcheada em `C:\ida_test\patched\setupcompat.dll`; scripts em `C:\ida_test\`
  (`decomp_hostisnewer.py`, `locate_islaterthan.py`, `decomp_islaterthan.py`,
  `verify_patched.py`, `scan_names.py`, `analyze_setupcompat.py`).

---

## 6. FERRAMENTAS - STATUS DE INSTALACAO

| Ferramenta | Status | Onde |
|---|---|---|
| 7-Zip (7z.exe) | JA EXISTE no kit | `Resources/App/7Zip/7z.exe` |
| wimlib (wimlib-imagex) | JA EXISTE no kit | `Resources/App/Wimlib/wimlib-imagex.exe` |
| HxD | INSTALADO (31/07/2026) | `C:\Program Files\HxD\HxD.exe` |
| **IDA Professional 9.0** | **INSTALADO e FUNCIONANDO (31/07/2026)** | `C:\Users\Lugia\Downloads\IDA Professional 9.0\IDA Professional 9.0\` — idat.exe (console), ida.exe (GUI), idapython3.dll, hexx64.dll (decompiler), idapro.hexlic (licenca OK). IDAPython configurado com Python 3.11.9 via `idapyswitch.exe --force-path "C:\Users\Lugia\AppData\Local\Programs\Python\Python311\python3.dll"`. Batch IDAPython TESTADO (idat -A -S script) |
| Gepetto (IA no IDA) | INSTALADO p/ IDA Pro 9.0 (faltam API keys) | `%APPDATA%\Hex-Rays\IDA Pro\plugins\gepetto.py` + `gepetto\` (config.ini: preencher API_KEY de OpenAI/Anthropic/Gemini) |
| IDA Free 8.4 | DESCARTADO (nao tem IDAPython; batch so roda IDC e licenca EULA precisa das chaves `EULA 90-93` no HKCU\Software\Hex-Rays\IDA) | `C:\Program Files\IDA Freeware 8.4\` |
| aria2 (para UUP) | INSTALADO | `KitLugia.GUI\Tools\aria2\aria2-1.37.0-win-64bit-build1\aria2c.exe` (o script UUP baixa o proprio, mas ja temos) |
| UUP Dump package 26300.9032 (Insider) | BAIXADO (nao usado - alvo mudou) | `KitLugia.GUI\Tools\uup-dump\26300.9032_amd64_pt-br\` |
| **UUP Dump package 26200.8973 (25H2 estavel - ALVO)** | **CONCLUIDO (31/07/2026)** - ISO gerada e validada | ISO: `C:\uup\26200.8973_amd64_pt-br\26200.8973.260724-1524.25H2_GE_RELEASE_SVC_PROD3_CLIENTPRO_OEMRET_X64FRE_PT-BR.ISO` (9,86 GB, 31/07 22:26); working copy: `C:\uup\26200.8973_amd64_pt-br\` (log: `C:\uup\download.log`); backup: `KitLugia.GUI\Tools\uup-dump\26200.8973_amd64_pt-br\` |
| setupcompat.dll (da ISO 26200.8973) | EXTRAIDA e ANALISADA (31/07/2026) | original: `C:\ida_test\isofiles\setupcompat.dll` (374.248 bytes); **patcheada: `C:\ida_test\patched\setupcompat.dll`** (offset 0x2DFD `01`->`00`) |
| **Ferramenta Downgrade (2 cliques)** | **PRONTA e TESTADA (31/07 noite)** | `KitLugia.GUI\Tools\Downgrade\DowngradePatch.cmd` + `patch_setupcompat.ps1` — auto-detecta 7z e ISO em `C:\uup`, extrai, patcha (backup `.orig`), registro Insider opcional (`noreg` pula), SHA256 e instrucoes. Testado: args+EOF (PATCHED/ALREADY_PATCHED, exit 0), ISO inexistente (exit 1), fluxo interativo (console OK; p/ automacao usar args `"iso" "pasta" noreg`) |
| VMware Workstation | JA INSTALADO no host | `C:\Program Files\VMware\VMware Workstation\vmware.exe` |

---

## 7. PROXIMOS PASSOS (checklist)

- [x] Conferir ISO 26200.8973 (CONCLUIDA 31/07/2026 22:26, 9,86 GB em `C:\uup\26200.8973_amd64_pt-br\`)
- [x] Validar `setupcompat.dll` da ISO no IDA Pro 9.0 (Fase 2 CONCLUIDA: funcao `CWindowsVersion::IsLaterThan` @ VA 0x180002CE4; patch FILE 0x2DFD `01`->`00`; verificado por decompilacao da DLL patcheada)
- [ ] (opcional) Preencher API key no `gepetto/config.ini` para analise assistida por IA
- [x] Implementar `SetupCompatPatcher` em KitLugia.Core -> **SUBSTITUIDO** pela ferramenta standalone `KitLugia.GUI\Tools\Downgrade\DowngradePatch.cmd` (decisao do usuario): auto-detecta ISO em `C:\uup`, extrai com 7z, chama `patch_setupcompat.ps1` (padrao 9B com fallback 6B unico, backup `.orig`, exit codes 0/1/2/3/4), registro Insider opcional, SHA256. TESTADA (31/07 noite)
- [ ] Testar em VM: criar VM com build atual (28000), aplicar patch, rodar setup
- [ ] Implementar UI no WinbootPage/UpdatesPage (opcao "Downgrade de build")
- [ ] Limpar KBs residuais com o card de updates apos o downgrade
