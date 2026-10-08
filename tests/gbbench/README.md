# gbbench — benchmark dos motores GameBoost

Mede se os motores do GameBoost dão **ganho real e mensurável**, e qual deles.

## Como correr

```bash
cd tests/gbbench
dotnet build -c Release
./bin/Release/net10.0-windows10.0.26100.0/GbBench.exe        # carga 2x CPUs (default)
./bin/Release/net10.0-windows10.0.26100.0/GbBench.exe 10     # carga 10x CPUs (recomendado)
```

O primeiro argumento é o **multiplicador de carga** (threads de fundo = N × CPUs).
Usar o `.exe` (apphost) e não `dotnet run`: sob `dotnet run` o entry assembly é o
`.dll` e o processo *worker* não encontra o `runtimeconfig.json`
(`FileNotFoundException: System.Runtime`). O apphost é auto-suficiente.

## Metodologia

| Decisão | Porquê |
|---|---|
| **Carga de fundo obrigatória** | Prioridade de processo só muda o resultado sob contenção de CPU. Numa máquina ociosa, `High` e `Normal` dão exatamente o mesmo. Um benchmark sem carga mediria zero sempre. |
| **Caminho real** (`CheckForegroundWindow`) | É o mesmo método que o `SetWinEventHook` e o polling de 250 ms chamam. Não chama `ApplyBoostV4` diretamente. |
| **Processo separado = "app em foreground"** | O worker tem janela visível real e é trazido a foreground com `AttachThreadInput` + `SetForegroundWindow`. |
| **Ordem intercalada** | Cada repetição inverte a ordem dos motores, para cancelar deriva térmica/turbo. |
| **Mediana, não média** | Uma corrida perdida (janela que não veio ao foreground) não distorce o resultado. |
| **`GetProcessTimes` para CPU%** | Mede o share real do scheduler — não o que pedimos, o que o Windows deu. |

### Métricas

- **FPS** — quadros (lotes de 40 000 operações) por segundo.
- **p99** — 99.º percentil do tempo de quadro. É o proxy de *1% low* / stutter.
- **CPU%** — fração de CPU do processo (`GetProcessTimes` / wall).

## Resultados medidos (05/10/2026, 20 CPUs, 3 repetições)

### Cenário OCIOSA (0 threads de fundo)

| Motor | prio | FPS | vs base | p99 ms | CPU% |
|---|---|---|---|---|---|
| NORMAL | Normal | 5253,5 | +0,00% | 0,20 | 100,6 |
| V1 (1) | High | 5205,7 | **−0,91%** | 0,22 | 98,7 |
| V2 (2) | High | 5260,6 | **+0,14%** | 0,20 | 100,0 |
| V3 (3) | High | 5205,8 | **−0,91%** | 0,22 | 100,0 |
| V4 (4) | High | 5251,5 | **−0,04%** | 0,20 | 100,0 |
| Auto (5) | High | 5248,6 | **−0,09%** | 0,20 | 100,0 |

**Leitura: zero.** Todos os motores dentro de ±1% — ruído. Confirma o princípio:
**prioridade não faz nada quando não há contenção.** Reproduzido em 3 execuções
independentes, sempre dentro de ±1%.

### Cenário CARGA 10× (200 threads de fundo)

Execução 1:

| Motor | prio | FPS | vs base | p99 ms | CPU% |
|---|---|---|---|---|---|
| NORMAL | Normal | 3937,7 | +0,00% | 0,30 | 95,6 |
| V1 (1) | High | 4175,9 | **+6,05%** | 0,24 | 100,0 |
| V2 (2) | High | 4179,0 | **+6,13%** | 0,24 | 100,0 |
| V3 (3) | High | 4176,7 | **+6,07%** | 0,24 | 100,0 |
| V4 (4) | High | 4181,2 | **+6,18%** | 0,24 | 100,0 |
| Auto (5) | High | 4180,4 | **+6,16%** | 0,24 | 100,3 |

Execução 2 (independente, noutro processo):

| Motor | prio | FPS | vs base | p99 ms | CPU% |
|---|---|---|---|---|---|
| NORMAL | Normal | 3939,3 | +0,00% | 0,28 | 95,6 |
| V1 (1) | High | 4178,6 | **+6,07%** | 0,24 | 100,0 |
| V2 (2) | High | 4177,6 | **+6,05%** | 0,24 | 99,4 |
| V3 (3) | High | 4178,9 | **+6,08%** | 0,24 | 100,0 |
| V4 (4) | High | 4179,5 | **+6,10%** | 0,24 | 100,0 |
| Auto (5) | High | 4181,5 | **+6,15%** | 0,24 | 100,0 |

**Este cenário é reprodutível**: as duas execuções concordam dentro de 0,1 pp
(base 3937,7 / 3939,3; motores ~4176–4181 em ambas).

O `CPU%` é a leitura mais direta: **95,6% sem boost contra 100,0% com boost**. É
isto que o scheduler estava a fazer — e com boost deixa de poder tirar CPU ao
processo em foco.

Numa execução onde sobraram threads de uma corrida anterior (contensão maior do que
o cenário pede), a mesma comparação deu **+112%** e p99 de **88,4 ms sem boost contra
0,24 ms com boost**. Serve de exemplo de quão mau fica o `p99` quando o jogo é
starved, mas **não** é o número a citar: o cenário standard é o das tabelas acima.

## Conclusões

1. **O ganho é real, mas só sob contenção.** É a validação de que o motor faz o
   que promete — e de que não faz promessas vãs quando a máquina está livre.
2. **Todos os motores são equivalentes neste benchmark**, porque todos aplicam
   `PriorityClass High`. O que os diferencia é page priority, I/O priority,
   EcoQoS/timer, GameClassInfo e ProBalance — **e nada disso é medido aqui**, porque
   o workload é CPU puro e não toca em disco, memória nem GPU.
3. **O `p99` é onde o boost se nota mais**: 0,30 → 0,24 ms no cenário CARGA 10×.
   É a métrica mais próxima do que o utilizador sente como "jogabilidade".

## Limitações (ler antes de tirar conclusões)

- **Não é um jogo.** Mede contenção de CPU. Não mede GPU, frame pacing, I/O de disco,
  latência de rede, nem efeito de DLLs/gráficos. Um jogo GPU-bound não verá este ganho.
- **A linha de base `NORMAL` só é estável se o cenário for respeitado.** No cenário
  standard CARGA 10× é reprodutível (3937,7 / 3939,3 em duas execuções). Mas se
  houver processos/threads órfãos de uma corrida anterior, a contenção effectively
  sobe e a base pode cair para ~1969 FPS, inflando o ganho para +112%. **Antes de
  citar um número, confirme que não sobrou `GbBench.exe` nem carga externa**
  (`tasklist | findstr GbBench`) e use `n=3` ou mais.
- **Não mede 1% low real.** `p99` do tempo de quadro de um workload single-threaded é
  um proxy, não a métrica de um motor gráfico com o seu próprio pacing.
- **O ganho do ProBalance (rebaixar o fundo) não está isolado.** O motor Auto tem o
  gate de 70%; com 200 threads ele abre, mas o benchmark não separa o quanto veio do
  `High` e quanto veio do rebaixamento do fundo.