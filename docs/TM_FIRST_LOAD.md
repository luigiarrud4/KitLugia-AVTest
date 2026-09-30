# KitTaskManager — primeira carga (abrir e ver valor na tela)

**Data:** 28/09/2026 · **Escopo:** `KitLugia.GUI\Windows\TaskManager\` (janela do TM) ·
**Relato que originou isto:** *"no PC dos meus amigos o TaskManager até abre, mas é muito
lento: ele abre a interface e os valores não aparecem na tela por um bom tempo — e não
aparece nenhum sinal de que esteja carregando."*

## 1. Por que a tela ficava vazia (caminho crítico)

O `Loaded` da janela é **fire-and-forget** de propósito (a interface aparece em < 50 ms) e
o dado real chega depois. O problema estava em **quanto** depois:

| Etapa da 1ª carga | Custo típico (host i5-14600KF, Debug) | Máquina fraca / perfil frio |
|---|---|---|
| `InitializeComponent` (prewarm já resolve) | ~0 (janela pré-aquecida) | 1–3 s sem prewarm |
| Snapshot nativo dos processos (`NtQSI`) | 20–40 ms | 60–150 ms |
| `WTS` + tradução de SID (`GetUserNames`) | ~300 ms na 1ª chamada (cache 15 s) | 300–900 ms |
| Conexões TCP por PID (`GetExtendedTcpTable`) | 10–40 ms | 40–150 ms |
| **GPU por processo (`PDH \GPU Engine(*)`)** | **800–1400 ms na 1ª chamada** (perflib frio) | **2–6 s** |
| `PerformanceCounter` de CPU/mem/disk | 800 ms na 1ª criação (perflib) | 1–3 s |
| Amostra de desempenho + desenho do Resumo | ~100 ms | 300–800 ms |

O culpado do "abre e fica vazio" era o pipeline do primeiro refresh:

```csharp
// ANTES: o desenho esperava TODOS os coletores — inclusive o mais lento de todos
await Task.WhenAll(netTask, gpuTask, gpuPerPidTask, snapTask);
```

O `\GPU Engine(*)` do PDH (a coleta mais lenta, que ainda tem um *warmup* de 150 ms no
Core) estava no **caminho crítico do desenho**: nada era pintado antes dele. E o Resumo —
a aba que abre — só recebia número no primeiro tick do timer de gráficos, **1 s depois**
do `Loaded`, somado ao custo da coleta.

## 2. O que mudou

### 2.1 Véu de carregamento (visível desde o primeiro quadro)

Novo elemento `LoadOverlay` no `KitTaskManagerWindow.xaml` (linha do rodapé do XAML) +
partial `KitTaskManager.Loading.cs`:

- aparece **já visível no XAML** (não existe um centésimo de segundo sem feedback);
- **spinner** (elipse tracejada girando 36°/tick), **etapa atual**, **4 barrinhas de
  progresso** e **tempo decorrido** ("1,4 s — a primeira leitura é a mais lenta…");
- etapas avançam pelos **eventos reais** dos dados (lista pintada → contadores prontos →
  primeira amostra desenhada), nunca por tempo inventado;
- `IsHitTestVisible="False"` e fundo translúcido (`#8C070B11`): dá para ver os valores
  chegando atrás e **clicar nas abas** enquanto ele existe;
- sai com fade de 220 ms quando a **lista e os gráficos** existem, e tem **teto de
  segurança de 15 s** — nenhuma falha de coleta pode deixar o véu preso na tela;
- fechar a janela durante a carga para o timer (`StopLoadingOverlay` no `Closing`);
- custo: **um** `DispatcherTimer` de 100 ms enquanto o véu existe, parado no mesmo
  instante em que ele sai.

### 2.2 Coletores auxiliares fora do caminho crítico

`KickAuxCollectors()` (novo, em `KitTaskManagerWindow.xaml.cs`) roda GPU-por-processo,
conexões TCP e nomes de usuário **desacoplados do desenho**:

- cada coleta publica **objetos novos** (`_auxGpuPerPid`, `_auxNetConnections`,
  `_auxUserNames`) — quem desenha nunca vê um dicionário pela metade;
- o refresh usa o **último valor publicado**; se a coleta ainda não terminou, a coluna
  mostra o valor anterior (uma coluna com dados de 1 s atrás em vez de lista atrasada);
- guarda `_auxCollecting` garante **uma** coleta em andamento (sem empilhar em máquina
  fraca);
- só a **primeira** pintura dá uma janela curta (300 ms) aos auxiliares, para a 1ª tela de
  uma máquina boa já sair com GPU/Rede preenchidos;
- `_lastGpuPct` só é sobrescrito por valor real (`>= 0`), para o coletor de desempenho não
  ser apagado com `-1` pelo auxiliar atrasado.

### 2.3 Primeiro tick de desempenho imediato

O `Loaded` agora chama `UpdatePerformanceGraphsSafe()` na hora (antes o primeiro tick era
1 s depois). O Resumo recebe número assim que a **primeira amostra** fica pronta, sem
somar um segundo inteiro de espera fixa.

## 3. Como isto é medido (e não "achado no olho")

Modo novo do `tests/TmVisualBench` — **`load`**:

```bash
dotnet run --project tests/TmVisualBench -- load                     # janela 1680x1000
dotnet run --project tests/TmVisualBench -- load --size 1180x740     # janela pequena
```

Ele bombeia o dispatcher em passos de 25 ms (os timers reais do TM rodam de verdade) e
imprime a linha do tempo: véu na tela, 1ª lista (`DgProcesses`), Resumo com valor
(`TxtSumCpuFooter` + Top processos), status, saída do véu — mais um **print do véu em
ação** (`out\tm_carregando.png`), um print do conteúdo já carregado
(`out\tm_primeiro_conteudo.png`), a checagem de **texto sobreposto no cartão do véu** e a
de **dourado visível** (não pode haver vazamento do tema global).

> O marcador do Resumo é o `TxtSumCpuFooter`, e não o `TxtSumSubtitle`: o subtítulo tem
> texto estático no XAML e acusaria "valor na tela" antes de existir valor nenhum.

### Resultado (host i5-14600KF, build Debug, 1680x1000 e 1180x740)

```
véu na tela      : t+28 ms
1ª lista         : t+435 ms  (131 linhas em DgProcesses)
Resumo c/ valor  : t+1007 ms (14 linhas no Top processos)
status final     : t+488 ms
véu saiu         : t+1235 ms
última etapa     : "Montando a tela"  (0,9 s)
layout do véu    : OK — nenhum texto sobreposto
dourado visível  : 0 (ok)
véu ainda visível: não
```

Antes da correção, o mesmo intervalo continha a espera **integral** do `PDH \GPU Engine(*)`
(1,0–1,4 s aqui; 2–6 s em máquina fraca) **mais** 1 s do primeiro tick de gráficos, sem
nenhuma indicação na tela.

## 4. O que não pode regredir

1. A lista não pode esperar GPU/Rede/usuários: eles são **enriquecimento**, não requisito.
2. O véu não pode bloquear clique (`IsHitTestVisible=False`) nem ficar preso (teto de 15 s).
3. O véu não pode reaparecer em reabertura da janela (`_loadOverlayStarted`/`_loadFinished`)
   — quem volta da bandeja tem dado quente, não precisa de espera anunciada.
4. Sem dourado dentro do TM e sem texto sobreposto no cartão do véu (modos `gold` e `overlap`
   do bench cobrem os dois).
