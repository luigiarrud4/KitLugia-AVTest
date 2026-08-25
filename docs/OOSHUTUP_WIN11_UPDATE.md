# OOShutUp — Atualização para Windows 11 (2026)

## Resumo

Atualização completa do sistema de privacidade OOShutUp para funcionar corretamente no Windows 11 24H2/25H2/26H1.

## Problemas Encontrados

### Configurações Obsoletas (Removidas)

| Configuração | Motivo |
|-------------|--------|
| Edge Legacy (12 settings) | Edge Legacy removido no Win11, substituído por Edge Chromium |
| Wi-Fi Sense | Removido no Windows 10 1803+ |
| People Band | Removido no Win11 22H2+ |
| Meet Now | Removido no Win11 |
| Cortana integrado | Cortana agora é app separado no Win11 |

### Configurações Corrigidas

| Configuração | Antes | Depois |
|-------------|-------|--------|
| DODownloadMode | SafeValue=0 | SafeValue=1 (LAN only) |
| Category "Cortana" | 11 settings | Renomeado para "Busca & Voz" |

### Novas Configurações Win11 (30+)

#### Windows Recall (3 settings)
- `DisableAIDataAnalysis` — Desativa Recall completamente
- `DisableRecallSnapshots` — Impede snapshots de tela
- `DisableRecallContentIndexing` — Desativa indexação

#### Copilot & IA (7 settings)
- `DisableCopilotRuntime` — Desativa runtime do Copilot
- `TurnOffWindowsCopilotForFileExplorer` — Remove do Explorer
- `DisablePaintAIFeatures` — IA no Paint
- `DisablePhotosAIFeatures` — IA no Photos
- `ShowCopilotSuggestions` — IA no Notepad
- `AIChatEnabled` — Telemetria IA do Edge
- `IsAADCloudSearchEnabled` — IA na Pesquisa

#### Windows Spotlight (5 settings)
- `DisableWindowsSpotlightFeatures` — Spotlight na Tela de Bloqueio
- `DisableSoftLanding` — Conteúdo Sugerido
- `RotatingLockScreenEnabled` — Spotlight no Desktop
- `RotatingLockScreenOverlayEnabled` — Dicas na Tela de Bloqueio
- `SubscribedContent-338387Enabled` — Fatos Curiosos

#### Widgets (2 settings)
- `AllowNewsAndInterests` — Feed de Notícias MSN

#### Segurança (2 settings)
- `SmartScreenEnabled` (Explorer) — SmartScreen do Explorador
- `SmartScreenEnabled` (Store) — SmartScreen para Apps Store

#### Telemetria (2 settings)
- `LimitDiagnosticLogCollection` — Experiência do Dispositivo
- `DoNotShowFeedbackNotifications` — Experiência de Uso

## Resultado

| Métrica | Antes | Depois |
|---------|-------|--------|
| Total de configurações | ~130 | 176 |
| Categorias | ~15 | 22 |
| Configurações Recommended | ~80 | 102 |
| Edge Legacy settings | 12 | 0 |
| Win11-specific settings | 0 | 30+ |

## Categorias Atualizadas

1. **Apps** (26) — Permissões de apps
2. **Busca** (8) — Pesquisa e voz (era "Cortana")
3. **Clipboard & Timeline** (5) — Área de transferência
4. **Copilot & IA** (13) — Copilot e assistentes IA
5. **Cortana & Pesquisa** (3) — Configurações restantes do Cortana
6. **Edge** (2) — Edge Chromium (DNT, Search Suggestions)
7. **Edge (New)** (19) — Edge Chromium policies
8. **Explorer** (5) — Explorador de arquivos
9. **Feedback** (2) — Feedback do Windows
10. **Input** (10) — Digitação e entrada
11. **Localização** (1) — Rastreamento de localização
12. **Misc** (22) — Configurações diversas
13. **OneDrive** (2) — Sincronização OneDrive
14. **Publicidade** (15) — Anúncios e ID de publicidade
15. **Recall & IA** (10) — Recall e funções de IA (NOVO)
16. **Segurança** (4) — SmartScreen e segurança
17. **Sincronização** (7) — Sincronização de configurações
18. **Taskbar** (1) — Barra de tarefas
19. **Tela de Bloqueio** (10) — Spotlight e bloqueio
20. **Telemetria** (7) — Telemetria e diagnósticos
21. **Updates** (2) — Windows Update
22. **Widgets** (2) — Painel de Widgets (NOVO)

## Verificação no Sistema

- **Sistema testado**: Windows 11 26H1 (Build 28000)
- **Caminhos verificados**: 70+ caminhos de registro
- **Serviços verificados**: DiagTrack, dmwappushservice, lfsvc
- **Build**: 0 erros em KitLugia.Core e KitLugia.GUI
- **UI**: Dinâmica — novas categorias aparecem automaticamente

## Arquivos Modificados

- `KitLugia.Core/OOShutUpManager.cs` — Atualização completa
