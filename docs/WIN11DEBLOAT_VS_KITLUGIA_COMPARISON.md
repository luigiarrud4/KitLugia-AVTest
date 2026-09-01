# Win11Debloat vs KitLugia — Comparação Detalhada

**Data:** 31/08/2026  
**Win11Debloat:** v2026.08.24 (PowerShell 5.1)  
**KitLugia:** v2.0.51 (C# WPF .NET 10)

---

## Resumo Executivo

O Win11Debloat é um **desbloatador dedicado** — foca 100% em remover bloatware, desabilitar telemetria, e customizar Windows 11. O KitLugia é um **suíte completa** que inclui DESBLOAT como UMA das muitas funcionalidades (WinPE, WinBoot, GameBoost, RAM Limiter, Limpeza, Store, etc.).

**Onde o Win11Debloat é melhor:** Safety nets (restore point + backup registry), capacidade de desfazer, remoção de apps com 2 métodos, e granularidade de controle do Explorer/Taskbar.

**Onde o KitLugia é melhor:** Escopo total, visual, performance tweaks, e integração com outras ferramentas.

---

## 1. SEGURANÇA (Onde o Win11Debloat Brilha)

| Feature | KitLugia | Win11Debloat |
|---------|----------|--------------|
| System Restore Point antes de mudanças | ❌ Não cria | ✅ Automático (toggle) |
| Backup do registry antes de mudanças | ❌ Apenasbackup parcial por toggle | ✅ Full snapshot em Backups/ |
| Desfazer QUALQUER mudança | ⚠️ Parcial (só OOShutUp) | ✅ Undo para todas as 100+ features |
| Undo files (.reg) organizados | ❌ | ✅ Pasta Regfiles/Undo/ com .reg para cada feature |
| Configurações salvas (LastUsedSettings) | ❌ | ✅ LastUsedSettings.json |
| Import/Export de configuração | ❌ | ✅ JSON config importável |
| Modo WhatIf (dry run) | ❌ | ✅ -WhatIf flag |
| Validação antes de aplicar | ⚠️ Parcial | ✅ Test-ConfigConsistency |
| Aviso se domínio-joinado | ❌ | ✅ Detecta e avisa |
| Requer PowerShell 5.1 | N/A | ✅ Verifica e bloqueia pwsh 7 |

### O que KitLugia deveria copiar:
1. **System Restore Point automático** antes de qualquer mudança de privacidade/tweaks
2. **Undo registry** — salvar o estado ANTES de cada toggle
3. **LastUsedSettings** — lembrar o que o usuário configurou

---

## 2. REMOÇÃO DE APPS

| Feature | KitLugia | Win11Debloat |
|---------|----------|--------------|
| Lista de apps com descrição | ⚠️ Básica (AppsPage) | ✅ 100+ apps com descrição, Safety rating |
| Safety rating (safe/optional/unsafe) | ⚠️ Sem classificação | ✅ Cada app tem level |
| Método de remoção: Appx | ✅ Get-AppxPackage | ✅ |
| Método de remoção: WinGet | ✅ winget uninstall | ✅ Combina ambos |
| Presets (Xbox, OEM, etc.) | ❌ | ✅ Xbox Gaming, OEM (HP/Dell/Lenovo/LG) |
| Remoção Edge (force) | ❌ | ✅ Invoke-ForceRemoveEdge |
| Verificação pós-remoção | ⚠️ | ✅ Verifica se desinstalou |
| Multiusuário | ⚠️ | ✅ -User ou AllUsers |
| Sysprep support | ❌ | ✅ |
| Conta padrão do sistema | ❌ | ✅ Detecta e previne |

### O que KitLugia deveria copiar:
1. **Safety ratings** para cada app (safe/optional/unsafe)
2. **Presets** de remoção (OEM HP/Dell/Lenovo, Xbox, etc.)
3. **Dual method** — tentar Appx primeiro, depois WinGet

---

## 3. PRIVACIDADE E TELEMETRIA

| Feature | KitLugia (OOShutUp) | Win11Debloat |
|---------|---------------------|--------------|
| Telemetria (AllowTelemetry=0) | ✅ | ✅ |
| DiagTrack service | ✅ Disable | ✅ Disable |
| Telemetry scheduled tasks | ✅ BackgroundProcessManager | ✅ |
| Bing Search desabilitar | ✅ | ✅ + remove o app BingSearch |
| Copilot desabilitar | ✅ Policy | ✅ + remove o app Copilot |
| Recall desabilitar | ✅ (2026 additions) | ✅ |
| Click To Do | ❌ Não tem | ✅ |
| AI Service Auto-Start (WSAIFabricSvc) | ❌ | ✅ |
| Edge AI Features | ❌ | ✅ |
| Paint AI Features | ✅ | ✅ |
| Notepad AI Features | ❌ | ✅ |
| Desktop Spotlight (3 opções) | ❌ Só disable | ✅ Enable/Hide icon/Disable |
| Suggestions & Tips | ✅ | ✅ |
| Lock Screen Tips | ✅ | ✅ |
| Location Services | ✅ | ✅ |
| Find My Device | ✅ | ✅ |
| Notifications (all apps) | ❌ | ✅ |
| Settings 365 Ads | ❌ | ✅ |
| Settings Home page | ❌ | ✅ Hide |
| Brave Bloat | ❌ | ✅ |
| Storage Sense | ❌ | ✅ Disable |
| Fast Startup | ❌ | ✅ Disable |
| BitLocker Auto-Encryption | ❌ | ✅ Disable |
| Modern Standby Networking | ❌ | ✅ Disable |
| Device Auto App Download | ✅ | ✅ |
| Delivery Optimization | ❌ | ✅ Disable |
| Search Highlights | ❌ | ✅ |
| Search History | ✅ | ✅ |
| Phone Link in Start | ❌ | ✅ |

### Novidades 2026 que o KitJÁ adicionou (acerto):
- AgentConnector policies ✅
- AllowRecallEnablement ✅ 
- RemoveCopilotApp ✅

### O que Kit deveria adicionar:
1. **Click To Do** disable (novo feature AI do Win11)
2. **AI Service Auto-Start** (WSAIFabricSvc → manual)
3. **Edge AI Features** control
4. **Notepad AI Features** control
5. **Notifications** disable global
6. **Settings 365 Ads** disable
7. **Desktop Spotlight** 3 opções (não só disable)
8. **Storage Sense** disable
9. **BitLocker Auto-Encryption** disable
10. **Modern Standby Networking** disable

---

## 4. CUSTOMIZAÇÃO DO EXPLORER E TASKBAR

| Feature | KitLugia | Win11Debloat |
|---------|----------|--------------|
| Dark Mode | ✅ | ✅ |
| Context Menu clássico (Win10) | ✅ Classic Context Menu | ✅ |
| File Extensions | ✅ | ✅ |
| Hidden Files | ✅ | ✅ |
| Open to This PC | ✅ | ✅ |
| **Open to Downloads** | ❌ | ✅ |
| **Open to OneDrive** | ❌ | ✅ |
| **Open to Home** | ❌ | ✅ (default) |
| **Drive Letter Position** (first/last/hide/network) | ❌ | ✅ 4 opções |
| **3D Objects folder** hide | ❌ | ✅ |
| **Music folder** hide | ❌ | ✅ |
| **OneDrive folder** hide | ❌ | ✅ |
| **Gallery** hide from Explorer | ❌ | ✅ |
| **Home** hide from Explorer | ❌ | ✅ |
| **Duplicate removable drives** hide | ❌ | ✅ |
| **Include in Library** context menu hide | ❌ | ✅ |
| **Give Access To** context menu hide | ❌ | ✅ |
| **Share** context menu hide | ❌ | ✅ |
| Taskbar alignment (left/center) | ❌ | ✅ |
| **Search bar style** (box/icon/label/hide) | ❌ | ✅ 4 opções |
| **Task View** hide | ❌ | ✅ |
| **Chat icon** hide | ❌ | ✅ |
| **Combine taskbar buttons** (always/when full/never) | ❌ | ✅ 3× (main+MM) |
| **Multi-monitor taskbar mode** | ❌ | ✅ 3 opções |
| **Enable End Task** in taskbar menu | ❌ | ✅ (Win11 23H2+) |
| **Last Active Click** | ❌ | ✅ |
| **Widgets disable** | ❌ (só toggle) | ✅ + remove 3 packages |
| **Alt+Tab tab visibility** | ❌ | ✅ hide/3/5/20 |
| **Window Snapping** disable | ❌ | ✅ |
| **Snap Assist** disable | ✅ ExmTweaks | ✅ |
| **Snap Layouts** disable | ❌ | ✅ |
| **Drag Tray** disable | ❌ | ✅ (Win11 25H2) |
| Start Menu Recommended hide | ✅ | ✅ |
| Start Menu All Apps | ❌ | ✅ hide/category/grid/list |
| Start Menu pins clear | ❌ | ✅ |

### O que Kit deveria adicionar:
1. **File Explorer default location** (Home/This PC/Downloads/OneDrive)
2. **Drive Letter Position** (4 opções)
3. **Taskbar search style** (4 opções)
4. **Combine taskbar buttons** (always/when full/never)
5. **Enable End Task** in taskbar right-click
6. **Last Active Click** in taskbar
7. **Snap Layouts** disable
8. **Alt+Tab tab visibility** control
9. **Window Snapping** disable
10. **Drag Tray** disable (Win11 25H2)
11. **Multi-monitor taskbar** options

---

## 5. GAMING

| Feature | KitLugia | Win11Debloat |
|---------|----------|--------------|
| GameBoost Pro (V1-V4, custom) | ✅ | ❌ |
| RAM Limiter (inteligente) | ✅ | ❌ |
| DVR disable | ❌ | ✅ |
| Game Bar Integration disable | ⚠️ Rename .exe | ✅ Policy + registry |
| Xbox app removal | ✅ (BloatwarePage) | ✅ Preset |
| GameBarPresenceWriter disable | ✅ Rename | ❌ |

### KitJÁ é melhor em gaming (só falta DVR + GameBar policy)

---

## 6. PERFORMANCE TWEAKS

| Feature | KitLugia (AllTweaks) | Win11Debloat |
|---------|---------------------|--------------|
| Power Throttling | ✅ | ❌ |
| Core Parking | ✅ | ❌ |
| Timer Coalescing | ✅ | ❌ |
| Win32 Priority Separation | ✅ | ❌ |
| Global Timer Resolution | ✅ | ❌ |
| MPO disable | ✅ | ❌ |
| Nagle Algorithm | ✅ | ❌ |
| RSS (Receive Side Scaling) | ✅ | ❌ |
| Network Throttling | ✅ | ❌ |
| GPU MSI Mode | ✅ | ❌ |
| VRAM Dedicada | ✅ | ❌ |
| Mouse Acceleration | ✅ ExmTweaks | ✅ |
| Snap Assist | ✅ ExmTweaks | ✅ |
| Disable Animations | ✅ ExmTweaks | ✅ |

### KitJÁ é muito superior em performance tweaks

---

## 7. ARQUITETURA E UX

| Aspecto | KitLugia | Win11Debloat |
|---------|----------|--------------|
| **Framework** | C# WPF .NET 10 | PowerShell 5.1 |
| **Visual** | UI moderna dark | Console com ASCII art |
| **GUI** | ✅ WPF nativo | ✅ WPF (via PowerShell) |
| **CLI** | ❌ | ✅ Completo com switches |
| **Organização de código** | Uma classe gigante (OOShutUpManager) | Modular (50+ arquivos .ps1) |
| **Registry .reg files** | ❌ Usa C# Registry API | ✅ Arquivos .reg organizados |
| **Features.json declarativo** | ❌ | ✅ |
| **Categories com ícones** | ⚠️ Sem categorias | ✅ 12 categorias |
| **Tooltips detalhados** | ✅ InfoButton (PrivacyPage) | ✅ ToolTip em cada feature |
| **Progress bar** | ✅ | ✅ (GUI) |
| **Cancel** | ⚠️ | ✅ |
| **Logging** | ✅ | ✅ (transcript) |
| **Domain-join check** | ❌ | ✅ |

---

## 8. PLANO DE AÇÃO — O que incorporar ao KitLugia

### Prioridade ALTA (impacto direto na segurança do usuário):
1. **System Restore Point automático** antes de mudanças de privacidade
2. **Registry backup + undo** — salvar estado ANTES de cada toggle (pasta Backups/)
3. **Click To Do disable** — feature AI nova
4. **AI Service Auto-Start disable** (WSAIFabricSvc)
5. **Edge AI Features** control
6. **Notepad AI Features** control

### Prioridade MÉDIA (completa a paridade):
7. **Notifications global disable**
8. **Settings 365 Ads** disable
9. **Desktop Spotlight** 3 opções
10. **Storage Sense** disable
11. **BitLocker Auto-Encryption** disable
12. **File Explorer default location** (4 opções)
13. **Drive Letter Position** (4 opções)
14. **Taskbar search style** (4 opções)
15. **Combine taskbar buttons** (3 opções)
16. **Enable End Task** in taskbar
17. **Last Active Click**
18. **Snap Layouts** disable
19. **Alt+Tab tab visibility**
20. **Window Snapping** disable
21. **Drag Tray** disable

### Prioridade BAIXA (nice-to-have):
22. **Presets de remoção** (OEM HP/Dell/Lenovo/LG)
23. **Safety ratings** para apps
24. **LastUsedSettings** persistência
25. **Import/Export** de configuração
26. **Sysprep** support
27. **WhatIf** mode
28. **Multi-monitor taskbar** options
29. **Start Menu All Apps view** control

---

## Conclusão

O KitLugia NÃO precisa copiar tudo do Win11Debloat — ele já faz MUITA coisa melhor (performance, gaming, WinPE, etc.). Mas as **safety nets** (restore point + backup + undo) são CRÍTICAS e o Kit não tem. O ideal é:

1. **Importar as safety nets** (restore point + registry backup antes de mudanças)
2. **Adicionar as features AI 2026** que faltam (Click To Do, AI Service, Edge AI, Notepad AI)
3. **Melhorar a organização** — dividir OOShutUpManager em módulos por categoria (como o Features.json do Win11Debloat)
4. **Corrigir os bugs** já encontrados (toggles desativando sozinhos, duplicatas)
