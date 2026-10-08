# Reflexão: onde o KitLugia repete o EaseUS — e onde repete errado

**Base:** [EASEUS_EPM_DESKTOP.md](EASEUS_EPM_DESKTOP.md) (mecanismo do desktop),
[EASEUS_EPM_MAP.md](EASEUS_EPM_MAP.md) (motor do WinPE), AGENTS.md (histórico de 30+ sessões de teste).
**Data:** 03/10/2026

---

## 1. O veredito, em uma frase

O mecanismo do EaseUS e o do KitLugia são **o mesmo mecanismo**. O que falta no KitLugia não é
técnica — são **três peças de engenharia**: leitura da BCD sem depender de `bcdedit`,
verificação antes de reiniciar, e um launcher de verdade no PE.

E há uma **bomba-relógio no código atual** que precisa ser consertada antes de qualquer teste novo.

---

## 2. A bomba-relógio: `GenerateWinpeshlIni()`

### O que o código faz

`KitLugia.Core/WinpeBuilder.cs:441-448`:

```csharp
// Gera o winpeshl.ini correto (WinPE só precisa do startnet.cmd padrão).
// Documentação Microsoft: [LaunchApps] não suporta scripts batch — usa startnet.cmd.
// Deixamos vazio para o WinPE rodar o cmd.exe padrão + startnet.cmd.
public static string GenerateWinpeshlIni()
{
    ...
    sb.AppendLine("[LaunchApps]");   // <-- VAZIO
    return sb.ToString();
}
```

### Por que o comentário está errado

O próprio repositório **contradiz esse comentário** em
`KitLugia.Core/IsoEditorManager.cs:239-247`:

> *"SEM winpeshl.ini o winpeshl.exe tenta lançar `%SystemDrive%\$Windows.~BT\sources\setup.exe`
> ... Com winpeshl.ini presente, o winpeshl lança **SO o que o [LaunchApps] mandar**"*

E o EaseUS confirma pelo outro lado: o `winpeshl.ini` dele é

```ini
[LaunchApps]
"%systemdrive%\Windows\System32\wpeinit.exe"
"x:\program files\Other\Tools\launch.exe",  0 "x:\program files\Other\explorer\auto.bat"
"x:\program files\easeus\epm\bin\EPMUI.exe"
```

Repare: ele lança um **`.bat`** (`auto.bat`) como argumento de outro executável. `[LaunchApps]`
aceita executável + argumentos, com segurança.

### Por que isso importa agora

Verifiquei o `boot.wim` que o Kit realmente usa (`C:\KL_WINPE\validation_boot.wim`, 232 MB, 2 imagens):

```
busca por "winpeshl" | "startnet"  ->  0 ocorrências
```

**O WIM não tem nenhum dos dois arquivos.** É por isso que o shrink funciona: sem
`winpeshl.ini`, o `winpeshl.exe` cai no comportamento padrão, que é
`cmd.exe /k startnet.cmd`; e o `wimlib add startnet.cmd` fornece o script.

Ou seja: **o caminho que funciona é justamente o caminho por acidente.** Ele depende de o
WIM **não** ter `winpeshl.ini`.

Se `CustomizeWinpeWimAsync` (o caminho DISM mount/commit) rodar algum dia, ele escreve
`winpeshl.ini` com `[LaunchApps]` **vazio** — e pela regra que o próprio `IsoEditorManager`
documenta, o `winpeshl` passa a lançar **nada**. O PE entra numa tela preta morta, sem cmd e
sem script.

**Ação: remover `GenerateWinpeshlIni()` do caminho de customização, ou fazê-lo escrever um
`[LaunchApps]` válido.** Isso é pré-requisito, não melhoria.

---

## 3. Por que o shell gráfico no PE falhou (e por que não era o Windows)

Você disse que já tentou — inclusive em VM — e falhou. Li o código. Não foi falta de
suporte do Windows; foram três defeitos concretos:

### Defeito 1 — só o `.exe` entra no WIM

`WinpeBuilder.cs:816`:

```csharp
string commands = $"add \"{explorerpp}\" /Windows/System32/Explorer++.exe";
```

Um único arquivo. O Explorer++ é uma aplicação **portátil**: precisa da pasta de config ao
lado (`Explorer++.ini`) e de DLLs próprias. Injetado sozinho em `System32`, ele sobe e morre
ou abre uma janela quebrada. O EaseUS injeta a **pasta inteira**:

```
\BuildPE\EaseUS-X64\epm\bin\*     <- curinga, não arquivo solto
\BUILDPE\EaseUS\epm\res\*
\BUILDPE\EaseUS\epm\multi\*
```

### Defeito 2 — o script sai sem verificar nada

`TestShellStartnetCmd()` termina com `exit /b 0` incondicional. Se o Explorer++ não subiu,
o `start` não retorna erro, o script encerra, e o PE fica parado. **Não há log, não há
código de saída, não há nada.** Daí a sensação de "sem garantia": na verdade era
"sem diagnóstico".

### Defeito 3 — `!OSDRV!` assumido

O script monta `!OSDRV!:\Windows\System32\Explorer++.exe`. Em boot por ramdisk o WIM é `X:`.
Se `OSDRV` resolver diferente do esperado, o caminho simplesmente não existe e o `start`
falha calado.

**Nenhum desses três é um bloqueio do WinPE.** São três bugs de empacotamento e de
observabilidade.

---

## 4. Área por área: diagnóstico e plano

### Área A — Leitura da BCD (o achado do EaseUS)

**Estado atual:** `WinbootManager.FindBcdGuidsByText` (linha 1070) roda `bcdedit.exe /enum all`
e faz regex linha a linha procurando o GUID. Chamado por `CleanupOldWinpeEntries`,
`CleanupOldRamdiskEntries`, `RemoveWinpeAsync`, `RemoveCustomWinpe`, `EPMUI` WinPE tools,
`CreateDirectNvramBoot`, `CreateLegacyBootEntry`, `CreateEfiBootEntry`.

**Problemas concretos:**
1. **Spawn de processo a cada chamada.** `bcdedit /enum all` em um disco grande leva ~1-2 s.
   O fluxo de shrink chama o cleanup mais de uma vez.
2. **Depende de locale.** Foi exatamente o bug de 02/08: o código procurava `identifier` /
   `description` em inglês; em pt-BR são `Identificador` / `Descricao`, e o cleanup
   silenciosamente removia 0 entradas.
3. **Depende do binário `bcdedit.exe` existir** no host (PATH, System32, ou WinSxS).

**A correção que o EaseUS usa:** `HKLM\BCD00000000\Objects\*` é a projeção em registry da
loja BCD ativa. Sem processo, sem locale, sem binário.

```
HKLM\BCD00000000\Objects\{9dea862c-5cdd-4e70-acc1-f32b344d4795}\Elements\24000001\Element
    REG_MULTI_SZ -> GUID do ramdisk referenciado
HKLM\BCD00000000\Objects\{guid-da-entrada}\Elements\11000001\Element
    objeto device -> offset 120 = PARTITION_ELEMENT (16 bytes), offset 144 = número do disco
```

**Ganhos concretos no Kit:**
- `IsAlreadyDone` de graça: se a entrada `{2c9f4b6a-…}` já existe, não reinicia à toa.
- `CleanupOldWinpeEntries` passa a ser exato, não heurístico.
- Removing o risco de locale **de vez** — nunca mais regex de tradução.

**Risco:** baixo, mas a leitura é **read-only**. Manter o `bcdedit /enum all` como fallback
para quando o registry não responder é obrigatório — não é para_replace, é para *complementar*.

**Teste:** comparar os dois oracles lado a lado em 3 estados (sem entrada / com entrada
antiga / com entrada duplicada) e exigir **mesma** resposta. Só depois trocar o chamador.

---

### Área B — Verificação antes de reiniciar

**Estado atual:** `ScheduleWinpeShrink` grava config, cria a entrada e agenda `shutdown /r /t 10`.
Se o WIM não estiver presente, o PE entra e não faz nada. O histórico registra esse padrão:
02/08 — *"o botão INICIAR SHRINK ficava cinza"* e a correção foi `PrepareWinpeBoot()` automático.

**O que o EaseUS faz:** `CUILogic::StartPreOSTask` roda `IsFileValid` + `CheckRecoverDataValid`
**antes** do reboot. Se falhar, não reinicia — mostra o erro na UI.

**Plano:** um método `PreOSPreflightAsync` que, antes do `shutdown`, verifica e **loga**:
- `boot.wim` existe, tamanho > 0, abre, tem índice 1;
- `boot.sdi` existe ao lado;
- a partição alvo tem o WIM no caminho esperado (`\KL_WINPE\...`);
- o marcador (`KL_SHRINK_TARGET.dat` / config injetada) está legível;
- a entrada BCD foi mesmo criada (via Área A).

Falhou → **não reinicia**, mostra o motivo. É a mudança com melhor relação custo/benefício
de toda a lista: ela transforma "reboot que não faz nada" em "erro claro na hora".

---

### Área C — Shell no PE (a tela "mágica")

**Estado atual:** `ScheduleTestWinpeShell` injeta o Explorer++ e reza.

**Plano em 3 degraus — parar de investir no degrau 2 se o 1 falhar:**

**Degrau 1 (barato, prova o conceito):**
1. Remover a bomba-relógio da §2.
2. Injetar a **pasta inteira** do shell, não o `.exe`.
3. Escrever um `winpeshl.ini` **real** via wimlib, no formato do EaseUS:
   ```ini
   [LaunchApps]
   "%systemdrive%\Windows\System32\wpeinit.exe"
   "x:\KitLugia\KLShell.exe"
   ```
4. `startnet.cmd` deixa de sair mudo: grava log com `%ERRORLEVEL%` e com o resultado do
   `dir` do caminho esperado.
5. Teste na VM: **o PE sobe, o launcher aparece?**

Se o degrau 1 não mostrar janela, o problema é o **modelo de sessão** do WinPE em ramdisk
(não existe shell de verdade — o `winpeshl` roda num modo restrito) e nenhum empacotamento
resolve. Nesse caso, **parar aqui** e investir em Enhancing: um TUI dentro do `startnet.cmd`.

**Degrau 2 (só se o 1 provar a sessão gráfica):** um executável nativo pequeno do próprio Kit
que desenha a UI e chama o mesmo `PartitionManager`. Reaproveita 100% do motor que já existe.

**Degrau 3 (o que o EaseUS realmente faz e é o mais defensável):** manter o `cmd` com log
como está, mas **embelezar a tela** — banner, barra de progresso, cor. Barato, funciona em
qualquer PE, e resolve 80% da percepção de "mágico".

> **Honestidade:** o degrau 3 entrega mais que o 1 com uma fração do risco. Se o objetivo é
> produto confiável, comece por ele.

---

## 5. O que NÃO copiar do EaseUS

1. **Baixar 850 MB do CDN em runtime.** O Kit monta/injeta localmente. Vantagem nossa.
2. **Biblioteca BCD binária própria** (`Win32Bcd.dll`). O EaseUS a tem porque precisa
   funcionar onde não há `bcdedit`. O Kit lê o registry — 90% do ganho, 0% do custo.
3. **Gerar ISO com `oscdimg` a cada operação.** Só é necessário para mídia USB.
4. **Espelho do WMI.** Não copia — é problema deles.

---

## 6. Ordem sugerida

| # | Item | Esforço | Risco | Retorno |
|---|------|---------|-------|---------|
| 0 | Remover a bomba `winpeshl.ini` vazio | 15 min | baixo | **impede regressão** |
| 1 | B — preflight antes do reboot | 1-2 h | baixo | alto |
| 2 | A — leitor da BCD por registry (com fallback) | 2-3 h | baixo | alto |
| 3 | C3 — embelezar a tela do PE | 1-2 h | baixo | médio-alto |
| 4 | C1 — provar sessão gráfica (degrau 1) | 3-4 h | médio | incerto |
| 5 | C2 — shell nativo do Kit | 1-2 dias | médio | alto, se o 4 passar |

**Regra:** o item 4 é o único com resultado **binário** (funciona ou não). Fazê-lo cedo é
barato em informação; fazê-lo tarde é caro. Se houver um único teste a fazer esta semana, é esse —
mas **depois** dos itens 0-2, que são improvements puros.

---

## 7. Pergunta para o próximo passo

Os itens 0-3 são improvements puros (nada pode piorar). O item 4 é uma aposta.

Sugestão: fazer **0 + 1 + 2** juntos (todos no Core, nenhum na GUI, build verificado), e
deixar o 4 para um ciclo de teste seu na VM — onde você tem o histórico de VM funcionando.
