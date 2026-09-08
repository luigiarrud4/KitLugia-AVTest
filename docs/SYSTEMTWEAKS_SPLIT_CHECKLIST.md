# ✂️ CHECKLIST — Divisão/Organização do `SystemTweaks.cs`

> **Estado atual (07/09/2026):** 10.781 linhas, **617 métodos públicos estáticos**, 20 `#region`,
> 19 arquivos do GUI consomem `SystemTweaks.*`. Maior região: `WinTune Optimizations` (~3.200 linhas).
> Prefixos: 187 `Is*`, 92 `Disable*`, 91 `Enable*`, 29 `Revert*`, 19 `Get*`, 18 `Apply*`…

---

## 🎯 Estratégia escolhida: **partial class** (zero risco)

A classe é `public static partial class SystemTweaks`. Dividir em **vários arquivos com a MESMA
classe parcial** = nenhuma chamada muda nos 19 consumidores (`SystemTweaks.DisableNagleAlgorithm()`
continua compilando igual). Cada fase termina com build 0 erros — se quebrar, `git checkout` do arquivo.

**Regra de ouro:** mover código é MECÂNICO (recortar região → colar em arquivo novo com o mesmo
namespace + cabeçalho `public static partial class SystemTweaks`). Renomear/reagrupar métodos é
OUTRA fase, separada, opcional.

---

## FASE 0 — Rede de segurança

- [ ] Build verde antes de começar: `dotnet build KitLugia.GUI/KitLugia.GUI.csproj -c Debug` → 0 erros
- [ ] `git status` limpo (commitar ou guardar o diff da sessão atual antes)
- [ ] Criar branch de trabalho (ex.: `refactor/systemtweaks-split`)
- [ ] Ler o cabeçalho de `SystemTweaks.cs`: anotar os usings que os arquivos novos vão precisar
      (copiar o bloco de usings inteiro + os aliases `using File = System.IO.File;` /
      `using Task = System.Threading.Tasks.Task;` — SEM eles o código quebrará)

## FASE 1 — Inventário (mapa região → arquivo)

Regiões medidas hoje (linha início → fim, tamanho):

| # | Região | Linhas | Tamanho | Arquivo destino proposto |
|---|--------|--------|---------|--------------------------|
| 1 | Bloatware Logic | 71–735 | ~664L | `SystemTweaks.Bloatware.cs` |
| 2 | Registry Tweaks (UI & General) | 737–903 | ~166L | `SystemTweaks.UiExplorer.cs` |
| 3 | Performance & System + Turbo Boot + Latency & Timer | 905–1101 | ~196L | `SystemTweaks.Performance.cs` |
| 4 | GPU & Gaming | 1103–1842 | ~739L | `SystemTweaks.GpuGaming.cs` |
| 5 | Hardware-Aware (L3/PowerMizer/NVMe/IRQ) | 1844–2373 | ~529L | `SystemTweaks.Hardware.cs` |
| 6 | Network & Driver | 2375–2533 | ~158L | `SystemTweaks.Network.cs` |
| 7 | Network Diagnostics & Troubleshooting | 2535–2893 | ~358L | `SystemTweaks.Network.cs` (mesmo arquivo do 6) |
| 8 | Power & Events | 2895–2951 | ~56L | `SystemTweaks.Power.cs` |
| 9 | Startup (TaskScheduler Wrapper) | 2953–3349 | ~396L | `SystemTweaks.Startup.cs` |
| 10 | Novas Otimizações 2025-2026 | 3351–3612 | ~261L | `SystemTweaks.Modern.cs` |
| — | **SEM REGIÃO (gap não rotulado)** | 3612–4381 | **~769L** | ⚠️ inventariar antes de mover (ver FASE 2) |
| 11 | Gaming Latency Profile (Khorvie) | 4381–4772 | ~391L | `SystemTweaks.GpuGaming.cs` (mesmo do 4) |
| 12 | GDI Scaling Control | 4774–4824 | ~50L | `SystemTweaks.UiExplorer.cs` |
| 13 | Windows 11 Additional Tweaks | 4826–4911 | ~85L | `SystemTweaks.Modern.cs` |
| 14 | Tweaks Diversos | 4913–5211 | ~298L | `SystemTweaks.Misc.cs` |
| 15 | WinTune Optimizations | 5213–8437 | **~3.224L** | dividir em 2: `SystemTweaks.WinTune.cs` + `SystemTweaks.WinTune.Privacy.cs` |
| 16 | TweaksPage UI - Info Methods | 8439–8485 | ~46L | `SystemTweaks.Query.cs` |
| 17 | TweaksPage UI - Check Methods | 8487–10145 | **~1.658L** | ⚠️ os 187 `Is*` — dividir POR DOMÍNIO junto dos seus setters (não em 1 arquivo) |
| 18 | Telemetria e Relatorios | 10147–10309 | ~162L | `SystemTweaks.Telemetry.cs` |
| 19 | Top Achados 2025 | 10309–10777 | ~468L | `SystemTweaks.Modern.cs` |

- [x] Inventariado o GAP 3612–4381 (código sem região): é a **SEÇÃO SLIDE ENGINE**
      (Ultra-Low Latency: OptimizeInputLatency, RevertInputLatency, UsbPowerSaving, PcieLinkState,
      HardDiskTimeout, GamingLatency + os Is* correspondentes) → destino `SystemTweaks.GpuGaming.cs`
      (ou `SystemTweaks.Slide.cs` próprio, ~769L)
- [ ] Decidir destino dos ~187 `Is*` da região 17: **regra = cada `IsXxx` vai para o MESMO
      arquivo do `DisableXxx`/`EnableXxx` correspondente** (query perto do mutation do domínio)
- [ ] Campos estáticos/P/Invoke do topo (linhas 1–70): ficam em `SystemTweaks.Core.cs`
      (P/Invoke powrprof + `GetActivePowerSchemeGuid` + constantes compartilhadas)

## FASE 2 — Corte mecânico (partial class, 1 arquivo por vez)

Para CADA arquivo da tabela, repetir o ciclo (nunca 2 de uma vez):

- [ ] Criar `SystemTweaks.<Dominio>.cs` com:
      mesmos usings do original + `namespace KitLugia.Core` + `public static partial class SystemTweaks`
- [ ] Recortar a(s) região(ões) inteiras (do `#region` ao `#endregion`) e colar
- [ ] Build → **0 erros** obrigatório antes do próximo
- [ ] Commit com mensagem por domínio (`refactor: move GPU & Gaming region to partial file`)
- [ ] Ao final, `SystemTweaks.cs` deve conter só o cabeçalho + usings + P/Invoke + `#region` Core

**Ordem sugerida (do menor/menos arriscado para o maior):**
1. [ ] `Power` (56L) — aquecimento, valida o processo
2. [ ] `GdiScaling` + `Win11 Additional` (135L)
3. [ ] `Network` (516L junto as 2 regiões)
4. [ ] `Startup` (396L)
5. [ ] `Modern` (Top Achados + Novas + Win11) (814L)
6. [ ] `Telemetry` (162L)
7. [ ] `Misc` (298L)
8. [ ] `UiExplorer` (216L)
9. [ ] `Performance` (196L)
10. [ ] `Hardware` (529L)
11. [ ] `GpuGaming` (1.130L)
12. [ ] `Bloatware` (664L)
13. [ ] `WinTune` (3.224L) — **por último e em 2 commits** (WinTune base / WinTune Privacy)
14. [ ] `Query` — redistribuir os `Is*` por domínio (última etapa, exige o mapa do FASE 1)

## FASE 3 — Verificação pós-migração

- [ ] `dotnet build KitLugia.GUI/KitLugia.GUI.csproj` → 0 erros
- [ ] Grep de sanidade: `grep -c "public static" KitLugia.Core/SystemTweaks*.cs` → **total = 617**
      (nada se perdeu no caminho)
- [ ] Grep duplicado: nenhum método definido 2x
      `grep -hoE "public static [A-Za-z0-9_<>,? ]+ [A-Za-z0-9_]+\(" KitLugia.Core/SystemTweaks*.cs | sort | uniq -d` → vazio
- [ ] Smoke test no app: abrir TweaksPage, WinTunePage, NetworkPage, SecurityPage
      (toggle 1 de cada domínio ligar/desligar)
- [ ] Testar tray: ToggleManager/TweakRegistry continuam salvando estado
      (registry `TraySettings` + JSON) após 1 reboot

## FASE 4 — (OPCIONAL) Limpeza durante a organização

Só depois do split 100% estável, aproveitar para (1 PR por item):

- [ ] **Matar os 6 placebos do julgamento** (Cache L2, IoPageLockLimit, Fila Input 200,
      IRQ8Priority, RmCacheLoc, LargeSystemCache) — métodos + toggles da UI
- [ ] **Fundir duplicatas internas** (WaitToKill 2x feito; falta PCA/PcaSvc e Nagle/SLIDE)
- [ ] Renomear região "HPET" para o que ela realmente faz (PlatformClock/Tick/1ms)
- [ ] Extrair helpers duplicados (`RunWevtutilQuery`, parsing de schtasks) para
      `SystemTweaks.Helpers.cs` internal
- [ ] Avaliar `#pragma`/nullable warnings do arquivo (baseline atual do projeto)

## 🔄 Rollback

Qualquer fase quebre sem solução óbvia:
```bash
git checkout -- KitLugia.Core/SystemTweaks.cs
rm KitLugia.Core/SystemTweaks.<Dominio>.cs
```
Como cada arquivo é movido em commit separado, também dá para reverter só um domínio:
```bash
git revert <commit-do-dominio>
```

## ⚠️ Armadilhas conhecidas deste arquivo

1. **Aliases de using** (`File`, `Task`) — obrigatórios em TODOS os arquivos novos que tocarem
   `File.*` ou `Task.*` (a classe original resolve ambiguidade com IWshRuntimeLibrary removida).
2. **`[SupportedOSPlatform("windows")]`** — replicar o atributo em cada partial.
3. **`static partial class`** — o modificador `static` precisa estar em TODAS as partes.
4. Métodos chamados por **reflection/strings** (TweakRegistry usa `def.IsApplied()` lambdas —
   verificar que nenhuma lambda referencia método que ficou no arquivo antigo por engano; o build pega).
5. `Logger.Log` dentro de métodos movidos — nada especial, mas confirmar que o using de
   `KitLugia.Core` está herdado (mesmo namespace = ok).
