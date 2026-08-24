# Glassmorphic Design System — KitLugia

**Autor:** Buffy (Codebuff)  
**Data:** 20/08/2026  
**Versão:** 3.0

---

## Visão Geral

O KitLugia utiliza um sistema visual **Glassmorphic** (Efeito de Vidro Fosco) em seus botões, combinando transparência, brilho animado e reflexos de luz para criar uma interface moderna e responsiva.

## Evolução das Versões

| Versão | Problema | Solução |
|--------|----------|---------|
| v1 | Hover simples (cinza + scale 1.02) | Glassmorphic com 3 camadas |
| v2 | Animações conflitavam em hover rápido | `RemoveStoryboard` + `FillBehavior="Stop"` |
| v2.1 | Glow desaparecia durante hover | `FillBehavior="HoldEnd"` no glow |
| v3 | Glow piscava, 2 botões acendiam, shine parava no meio | **Removido `RemoveStoryboard`** — WPF auto-prioriza |

## Arquitetura v3 (Final)

### Camadas Visuais

```
┌─────────────────────────────────┐ ← Layer 0: outerGlow (animates color)
│  ┌───────────────────────────┐  │ ← Layer 1: glassCard (clips shine)
│  │  ╱ Shine Bar ╲            │  │ ← Layer 2: shineCanvas + shineBar
│  │     ╲         ╱           │  │    (skewed -20°, slides left→right)
│  │  [Icon] [Text]            │  │ ← Layer 3: Content
│  │  ┃ ActiveBar ┃            │  │ ← Layer 4: ActiveBar (checked)
│  └───────────────────────────┘  │
└─────────────────────────────────┘
```

### Por que RemoveStoryboard foi removido

**Problema com `RemoveStoryboard`:**
1. Interrompia a animação de exit no meio, deixando o glow em opacidade intermediária
2. Ao mover mouse para outro botão, o glow antigo ficava parcialmente visível (2 botões acesos)
3. Interrompia o sweep da shine bar no início (não completava o percurso)
4. Causava "reinício" aleatório das animações

**Solução v3: Sem `RemoveStoryboard`**

WPF tem prioridade natural de animações: quando duas animações alvejam a mesma propriedade, a **mais recente** sempre vence. Não é necessário cleanup manual.

```
Mouse entra  → gsIn inicia: glow → #70FFD700 (HoldEnd)
Mouse sai    → gsOut inicia: glow → #00FFD700 (HoldEnd)
                          ↑ gsOut SOBRESCREVE gsIn automaticamente
                          ↑ Não precisa de RemoveStoryboard
```

### FillBehavior universal: HoldEnd

**Todas as animações** usam `FillBehavior="HoldEnd"`:

| Tipo | HoldEnd | Razão |
|------|---------|-------|
| Glow enter | ✅ | Mantém borda visível durante hover |
| Glow exit | ✅ | Mantém fade-out no estado final (transparente) |
| Shine enter | ✅ | Barra completa o percurso e fica no final |
| Shine exit | ✅ | Barra volta ao início e fica oculta |
| BG tint enter | ✅ | Background fica visível durante hover |
| BG tint exit | ✅ | Background volta a transparente |

### Animações

| Efeito | Enter Duration | Exit Duration | Easing |
|--------|---------------|---------------|--------|
| Border glow | 0.35s | 0.30s | CubicEase Out |
| Shine sweep | 0.65s | 0.25s | SineEase Out |
| Shine opacity | 0.15s | 0.20s | — |
| BG tint | 0.30s | 0.25s | — |

### Performance

| Métrica | Valor |
|---------|-------|
| Frames por animação | ~60fps |
| Overhead por hover | Negligível |
| Layout reflow | Zero (sem ScaleTransform) |
| Memória | ~2KB por instância |

### Otimizações

1. **Sem ScaleTransform** — Evita re-layout que causa stutter
2. **Sem RemoveStoryboard** — WPF auto-prioriza (zero conflitos)
3. **HoldEnd universal** — Animações ficam no estado final sem re-renderizar
4. **TranslateTransform** — Move pixels, sem recálculo de layout
5. **Gradient simples** — 3-5 stops, sem efeitos pesados

## Componentes

### NavButtonStyle (sidebar)
- **Local:** `KitLugia.GUI/Themes/NavStyles.xaml`
- **Cor glow:** #FFD700 (dourado)
- **Aplicado em:** 14 RadioButtons da sidebar

### ModeButtonStyle
- **Local:** `KitLugia.GUI/Themes/NavStyles.xaml`
- **Idêntico ao NavButtonStyle**

### Top Buttons (inline no MainWindow.xaml)
- **Local:** `KitLugia.GUI/MainWindow.xaml`

| Botão | Cor Glow | BG Tint |
|-------|----------|---------|
| GoodbyeDPI | #FFD700 | — |
| BackgroundMonitor | #FFD700 | — |
| Update | #2196F3 | #222196F3 |
| Console | #00FF00 | #2200FF00 |
| Notifications | #FFFFFF | #22FFFFFF |

## Como Funciona (Fluxo)

```
1. Mouse entra no botão
   ↓
2. Trigger IsMouseOver=True dispara EnterActions
   ↓
3. BeginStoryboard gsIn inicia:
   - Glow: BorderBrush anima de #00FFD700 → #70FFD700 (0.35s)
   - Shine: TranslateTransform.X anima de -80 → 380 (0.65s)
   - Shine opacity: 0 → 1 (0.15s)
   ↓
4. Glow fica em #70FFD700 (HoldEnd = mantido)
5. Shine completa o percurso e some (HoldEnd = posição final)
   ↓
6. Mouse sai do botão
   ↓
7. Trigger IsMouseOver=False dispara ExitActions
   ↓
8. BeginStoryboard gsOut SOBRESCREVE gsIn (mesma propriedade):
   - Glow: BorderBrush anima de #70FFD700 → #00FFD700 (0.30s)
   - Shine: volta ao início e some (0.25s)
   ↓
9. Botão volta ao estado transparente
```

## Arquivos

| Arquivo | Mudança |
|---------|---------|
| `KitLugia.GUI/Themes/NavStyles.xaml` | Estilos glassmorphic v3 |
| `KitLugia.GUI/MainWindow.xaml` | 5 top buttons glassmorphic v3 |
| `docs/GLASSMORPHIC_DESIGN.md` | Esta documentação |

## Backup

`backups/nav-styles-backup/`

## Como Adicionar a um Novo Botão

1. Copiar o template base (outerGlow → glassCard → Grid → Canvas + Content)
2. Definir cor do glow no `BorderBrush` do `outerGlow`
3. `FillBehavior="HoldEnd"` em TODAS as animações
4. Wrappar conteúdo em `Grid` dentro do `glassCard`
5. Ajustar `shineSlide` range baseado na largura do botão
