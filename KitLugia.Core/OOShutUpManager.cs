using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class OOShutUpManager
    {
        #region Estruturas

        public enum PrivacyLevel
        {
            Recommended,    // Seguro, não quebra nada
            Limited,        // Privacidade moderada
            NotRecommended  // Máximo, pode quebrar recursos (Cortana, Loja, etc)
        }

        public class PrivacySetting
        {
            public string Name { get; set; } = string.Empty;
            public string Description { get; set; } = string.Empty;
            public string RegistryPath { get; set; } = string.Empty;
            public string ValueName { get; set; } = string.Empty;
            public object? SafeValue { get; set; }   // Valor para "Proteger" (Desativar recurso)
            public object? UnsafeValue { get; set; } // Valor Padrão do Windows
            public PrivacyLevel Level { get; set; }
            public string Category { get; set; } = string.Empty;
            public bool IsService { get; set; } = false;
            public string? ServiceName { get; set; }
        }

        // ================================================================
        // LISTA MESTRA — Baseada no O&O ShutUp10++ e documentação Microsoft
        // ================================================================

        // Típico: 130+ configurações de privacidade (O&O ShutUp10++ completo)
        private static readonly List<PrivacySetting> PrivacySettings = new List<PrivacySetting>(150)
        {
            // ====================== TELEMETRIA ======================
            new() {
                Name = "Telemetria do Windows",
                Description = "Impede o envio de dados de uso e diagnóstico para a Microsoft.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                ValueName = "AllowTelemetry",
                SafeValue = 0, UnsafeValue = 3,
                Level = PrivacyLevel.Recommended, Category = "Telemetria"
            },
            new() {
                Name = "Serviço DiagTrack",
                Description = "Serviço de Experiência do Usuário Conectado e Telemetria.",
                IsService = true, ServiceName = "DiagTrack",
                SafeValue = 4, UnsafeValue = 2,
                Level = PrivacyLevel.Recommended, Category = "Telemetria"
            },
            new() {
                Name = "Serviço dmwappushservice",
                Description = "Serviço de roteamento de mensagens push WAP (telemetria).",
                IsService = true, ServiceName = "dmwappushservice",
                SafeValue = 4, UnsafeValue = 3,
                Level = PrivacyLevel.Recommended, Category = "Telemetria"
            },
            new() {
                Name = "Relatório de Erros do Windows",
                Description = "Impede envio de relatórios de erros para a Microsoft.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting",
                ValueName = "Disabled",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Telemetria"
            },
            new() {
                Name = "Dados de Diagnóstico Personalizados",
                Description = "Impede envio de dados de diagnóstico personalizados.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                ValueName = "LimitDiagnosticLogCollection",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Telemetria"
            },

            // ====================== PERSONALIZAÇÃO DE INPUT ======================
            new() {
                Name = "Envio de Dados de Digitação",
                Description = "Impede que dados de digitação sejam enviados para a Microsoft.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Input\TIPC",
                ValueName = "Enabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Input"
            },
            new() {
                Name = "Envio de Dados de Digitação (LM)",
                Description = "Impede que dados de digitação sejam enviados para a Microsoft (Política Global).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Input\TIPC",
                ValueName = "Enabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Input"
            },
            new() {
                Name = "Personalização de Escrita à Mão",
                Description = "Desativa envio de dados de escrita à mão para a Microsoft.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\TabletPC",
                ValueName = "PreventHandwritingDataSharing",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Input"
            },
            new() {
                Name = "Relatórios de Erros de Escrita à Mão",
                Description = "Desativa envio de relatórios de erros de escrita à mão.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\HandwritingErrorReports",
                ValueName = "PreventHandwritingErrorReports",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Input"
            },
            new() {
                Name = "Coletor de Inventário",
                Description = "Desativa o Inventory Collector (telemetria de compatibilidade).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppCompat",
                ValueName = "DisableInventory",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Input"
            },
            new() {
                Name = "Câmera na Tela de Bloqueio",
                Description = "Desativa câmera na tela de bloqueio.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Personalization",
                ValueName = "NoLockScreenCamera",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Limited, Category = "Input"
            },
            new() {
                Name = "Publicidade via Bluetooth",
                Description = "Desativa anúncios via Bluetooth.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\PolicyManager\current\device\Bluetooth",
                ValueName = "AllowAdvertising",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Input"
            },
            new() {
                Name = "Windows Customer Experience Improvement Program",
                Description = "Desativa o programa de melhoria da experiência do cliente (CEIP).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\SQMClient\Windows",
                ValueName = "CEIPEnable",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Input"
            },
            new() {
                Name = "Backup de Mensagens na Nuvem",
                Description = "Permite backup de mensagens na nuvem (padrão ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Messaging",
                ValueName = "AllowMessageSync",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Input"
            },
            new() {
                Name = "Recursos Biométricos",
                Description = "Ativa recursos biométricos (padrão ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Biometrics",
                ValueName = "Enabled",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Input"
            },

            // ====================== LOCALIZAÇÃO ======================
            new() {
                Name = "Rastreamento de Localização",
                Description = "Desativa a localização global do dispositivo.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                ValueName = "DisableLocation",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Localização"
            },

            // ====================== CORTANA & PESQUISA ======================
            new() {
                Name = "Cortana",
                Description = "Desativa a assistente virtual Cortana.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                ValueName = "AllowCortana",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Limited, Category = "Cortana & Pesquisa"
            },
            new() {
                Name = "Pesquisa na Web (Menu Iniciar)",
                Description = "Impede que o Menu Iniciar pesquise no Bing.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                ValueName = "DisableWebSearch",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Cortana & Pesquisa"
            },
            new() {
                Name = "Sugestões de Pesquisa na Nuvem",
                Description = "Impede sugestões de pesquisa na nuvem na barra de tarefas.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                ValueName = "AllowCloudSearch",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Cortana & Pesquisa"
            },

            // ====================== COPILOT ======================
            new() {
                Name = "Windows Copilot",
                Description = "Desativa o assistente Copilot do Windows 11.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot",
                ValueName = "TurnOffWindowsCopilot",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Limited, Category = "Copilot & IA"
            },
            new() {
                Name = "Botão Copilot na Barra de Tarefas",
                Description = "Remove o botão do Copilot da barra de tarefas.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                ValueName = "ShowCopilotButton",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Limited, Category = "Copilot & IA"
            },
            new() {
                Name = "Windows Recall",
                Description = "Desativa a função Recall (captura de tela automática por IA).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI",
                ValueName = "DisableAIDataAnalysis",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Copilot & IA"
            },

            // ── Novos toggles Windows 11 2025/2026 ──────────────────────────
            new() {
                Name = "Copilot Runtime (Serviço de IA)",
                Description = "Desativa o serviço de runtime do Copilot que roda em segundo plano.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI",
                ValueName = "DisableCopilotRuntime",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Copilot & IA"
            },
            new() {
                Name = "Copilot no File Explorer",
                Description = "Remove o botão do Copilot do File Explorer (Windows 11 24H2+).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI",
                ValueName = "TurnOffWindowsCopilotForFileExplorer",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Limited, Category = "Copilot & IA"
            },
            new() {
                Name = "Sugestões de IA no Editor de Texto",
                Description = "Desativa sugestões de IA no Notepad e WordPad.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Notepad",
                ValueName = "ShowCopilotSuggestions",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Limited, Category = "Copilot & IA"
            },
            new() {
                Name = "Recall — Snapshots de Tela",
                Description = "Impede que o Recall salve snapshots de tela no disco.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI",
                ValueName = "DisableRecallSnapshots",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Copilot & IA"
            },
            new() {
                Name = "Recall — Indexação de Conteúdo",
                Description = "Desativa a indexação de conteúdo pelo Recall para busca semântica.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI",
                ValueName = "DisableRecallContentIndexing",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Copilot & IA"
            },
            new() {
                Name = "IA no Paint (Generative Fill)",
                Description = "Desativa o Generative Fill e outras funções de IA no Paint.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI",
                ValueName = "DisablePaintAIFeatures",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Limited, Category = "Copilot & IA"
            },
            new() {
                Name = "IA no Photos (Background Blur/Remove)",
                Description = "Desativa funções de IA no aplicativo Fotos (remoção de fundo, etc.).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsAI",
                ValueName = "DisablePhotosAIFeatures",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Limited, Category = "Copilot & IA"
            },
            new() {
                Name = "Telemetria de IA do Edge",
                Description = "Desativa o envio de dados de uso das funções de IA do Microsoft Edge.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "AIChatEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Copilot & IA"
            },
            new() {
                Name = "Sugestões de IA na Pesquisa do Windows",
                Description = "Desativa resultados de IA na barra de pesquisa do Windows.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\SearchSettings",
                ValueName = "IsAADCloudSearchEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Copilot & IA"
            },
            new() {
                Name = "Widgets com IA (News Feed)",
                Description = "Desativa o painel de Widgets com feed de notícias e IA.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Dsh",
                ValueName = "AllowNewsAndInterests",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Copilot & IA"
            },
            new() {
                Name = "Telemetria de IA do Office",
                Description = "Desativa o envio de dados de uso das funções de IA do Microsoft Office.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Policies\Microsoft\office\16.0\common\privacy",
                ValueName = "sendtelemetry",
                SafeValue = 3, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Copilot & IA"
            },

            // ====================== PUBLICIDADE ======================
            new() {
                Name = "ID de Publicidade",
                Description = "Impede que apps usem seu ID de publicidade.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo",
                ValueName = "Enabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Publicidade"
            },
            new() {
                Name = "ID de Publicidade (Política Global)",
                Description = "Desativa ID de publicidade via Group Policy para todos os usuários.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo",
                ValueName = "DisabledByGroupPolicy",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Publicidade"
            },
            new() {
                Name = "Sugestões na Timeline",
                Description = "Desativa sugestões na Timeline do Windows.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                ValueName = "SubscribedContent-353698Enabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Publicidade"
            },
            new() {
                Name = "Sugestões no Menu Iniciar (Timeline)",
                Description = "Desativa sugestões no Menu Iniciar relacionadas à Timeline.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                ValueName = "SubscribedContent-338388Enabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Publicidade"
            },
            new() {
                Name = "Sugestões de Setup",
                Description = "Desativa sugestões para finalizar configuração do dispositivo.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                ValueName = "SubscribedContent-310093Enabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Publicidade"
            },
            new() {
                Name = "Sugestões de Setup (UserProfileEngagement)",
                Description = "Desativa sugestões de engajamento do usuário.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\UserProfileEngagement",
                ValueName = "ScoobeSystemSettingEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Publicidade"
            },
            new() {
                Name = "Notificações de Apps",
                Description = "Desativa notificações de apps (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\PushNotifications",
                ValueName = "NoToastApplicationNotification",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Publicidade"
            },
            new() {
                Name = "Idioma Local para Browsers",
                Description = "Permite acesso ao idioma local para browsers (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\Control Panel\International\User Profile",
                ValueName = "HttpAcceptLanguageOptOut",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Publicidade"
            },
            new() {
                Name = "Sugestões de Texto (Teclado Tátil)",
                Description = "Desativa sugestões de texto ao digitar no teclado tátil.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\TabletTip\1.7",
                ValueName = "EnableTextPrediction",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Limited, Category = "Publicidade"
            },
            new() {
                Name = "URLs para Windows Store",
                Description = "Desativa envio de URLs de apps para Windows Store (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\AppHost",
                ValueName = "EnableWebContentEvaluation",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.NotRecommended, Category = "Publicidade"
            },
            new() {
                Name = "Instalação Automática de Apps",
                Description = "Impede instalação silenciosa de apps sugeridos (bloatware).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                ValueName = "SilentInstalledAppsEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Publicidade"
            },
            new() {
                Name = "Sugestões no Menu Iniciar",
                Description = "Remove apps sugeridos do Menu Iniciar.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                ValueName = "SystemPaneSuggestionsEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Publicidade"
            },
            new() {
                Name = "Sugestões em Configurações",
                Description = "Remove sugestões de apps no aplicativo Configurações.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                ValueName = "SubscribedContent-338393Enabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Publicidade"
            },
            new() {
                Name = "Dicas e Sugestões do Windows",
                Description = "Desativa notificações de 'Dicas do Windows' e sugestões.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                ValueName = "SubscribedContent-338389Enabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Publicidade"
            },

            // ====================== CLIPBOARD & TIMELINE ======================
            new() {
                Name = "Histórico de Área de Transferência",
                Description = "Desativa o histórico de clipboard (Win+V) - DEIXE PARA O USUÁRIO DECIDIR.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                ValueName = "AllowClipboardHistory",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.NotRecommended, Category = "Clipboard & Timeline"
            },
            new() {
                Name = "Sincronização de Clipboard na Nuvem",
                Description = "Impede sincronização do clipboard entre dispositivos - DEIXE PARA O USUÁRIO DECIDIR.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                ValueName = "AllowCrossDeviceClipboard",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.NotRecommended, Category = "Clipboard & Timeline"
            },
            new() {
                Name = "Timeline (Feed de Atividades)",
                Description = "Desativa o rastreamento de atividades do usuário.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                ValueName = "EnableActivityFeed",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Clipboard & Timeline"
            },
            new() {
                Name = "Publicação de Atividades",
                Description = "Impede publicação de atividades do usuário para a Microsoft.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                ValueName = "PublishUserActivities",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Clipboard & Timeline"
            },
            new() {
                Name = "Upload de Atividades",
                Description = "Impede upload de histórico de atividades.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System",
                ValueName = "UploadUserActivities",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Clipboard & Timeline"
            },

            // ====================== ONEDRIVE ======================
            new() {
                Name = "Sincronização OneDrive",
                Description = "Desativa a sincronização automática de arquivos com o OneDrive.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\OneDrive",
                ValueName = "DisableFileSyncNGSC",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "OneDrive"
            },
            new() {
                Name = "Oferta de Backup na Nuvem",
                Description = "Bloqueia a oferta de backup de pastas no OneDrive.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\OneDrive",
                ValueName = "KFMBlockOptIn",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Limited, Category = "OneDrive"
            },

            // ====================== WI-FI SENSE ======================
            new() {
                Name = "Wi-Fi Sense (Hotspot Automático)",
                Description = "Desativa conexão automática a hotspots sugeridos.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\WcmSvc\wifinetworkmanager\config",
                ValueName = "AutoConnectAllowedOEM",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Wi-Fi"
            },

            // ====================== WINDOWS UPDATE ======================
            new() {
                Name = "Otimização de Entrega (P2P)",
                Description = "Impede que seu PC envie updates para outros na internet.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config",
                ValueName = "DODownloadMode",
                SafeValue = 0, UnsafeValue = 3,
                Level = PrivacyLevel.Recommended, Category = "Updates"
            },
            new() {
                Name = "Instalação Automática de Drivers",
                Description = "Impede que o Windows Update instale drivers automaticamente.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate",
                ValueName = "ExcludeWUDriversInQualityUpdate",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Limited, Category = "Updates"
            },

            // ====================== APPS & LOJA ======================
            new() {
                Name = "Apps em Segundo Plano",
                Description = "Impede que apps da loja rodem sem você abrir.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications",
                ValueName = "GlobalUserDisabled",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Limited, Category = "Apps"
            },
            new() {
                Name = "Acesso à Conta de Usuário",
                Description = "Bloqueia acesso de apps à conta de usuário.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\userAccountInformation",
                ValueName = "Value",
                SafeValue = "Deny", UnsafeValue = "Allow",
                Level = PrivacyLevel.Recommended, Category = "Apps"
            },
            new() {
                Name = "Rastreamento de Apps",
                Description = "Desativa rastreamento de início de apps.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                ValueName = "Start_TrackProgs",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Apps"
            },
            new() {
                Name = "Acesso a Diagnósticos de Apps",
                Description = "Bloqueia acesso de apps a informações de diagnóstico.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\appDiagnostics",
                ValueName = "Value",
                SafeValue = "Deny", UnsafeValue = "Allow",
                Level = PrivacyLevel.Recommended, Category = "Apps"
            },
            new() {
                Name = "Acesso à Localização (NonPackaged)",
                Description = "Bloqueia acesso de apps não empacotados à localização.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location\NonPackaged",
                ValueName = "Value",
                SafeValue = "Deny", UnsafeValue = "Allow",
                Level = PrivacyLevel.Recommended, Category = "Apps"
            },
            new() {
                Name = "Ativação por Voz",
                Description = "Desativa ativação por voz de apps (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Speech_OneCore\Settings\VoiceActivation\UserPreferenceForAllApps",
                ValueName = "AgentActivationEnabled",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Apps"
            },
            new() {
                Name = "Ativação por Voz (Tela Bloqueada)",
                Description = "Desativa ativação por voz com tela bloqueada.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Speech_OneCore\Settings\VoiceActivation\UserPreferenceForAllApps",
                ValueName = "AgentActivationOnLockScreenEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Apps"
            },
            new() {
                Name = "Botão de Headset",
                Description = "Desativa app padrão para botão de headset.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Speech_OneCore\Settings\VoiceActivation\UserPreferenceForAllApps",
                ValueName = "AgentActivationLastUsed",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Limited, Category = "Apps"
            },
            new() {
                Name = "Acesso a Movimentos",
                Description = "Bloqueia acesso de apps a movimentos (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\activity",
                ValueName = "Value",
                SafeValue = "Allow", UnsafeValue = "Deny",
                Level = PrivacyLevel.NotRecommended, Category = "Apps"
            },
            new() {
                Name = "Acesso a Calendário",
                Description = "Bloqueia acesso de apps ao calendário.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\appointments",
                ValueName = "Value",
                SafeValue = "Deny", UnsafeValue = "Allow",
                Level = PrivacyLevel.Recommended, Category = "Apps"
            },
            new() {
                Name = "Acesso a Chamadas Telefônicas",
                Description = "Bloqueia acesso de apps a chamadas telefônicas.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\phoneCall",
                ValueName = "Value",
                SafeValue = "Deny", UnsafeValue = "Allow",
                Level = PrivacyLevel.Recommended, Category = "Apps"
            },
            new() {
                Name = "Acesso a Histórico de Chamadas",
                Description = "Bloqueia acesso de apps ao histórico de chamadas.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\phoneCallHistory",
                ValueName = "Value",
                SafeValue = "Deny", UnsafeValue = "Allow",
                Level = PrivacyLevel.Recommended, Category = "Apps"
            },
            new() {
                Name = "Acesso a Email",
                Description = "Bloqueia acesso de apps a email (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\email",
                ValueName = "Value",
                SafeValue = "Allow", UnsafeValue = "Deny",
                Level = PrivacyLevel.NotRecommended, Category = "Apps"
            },
            new() {
                Name = "Acesso a Tarefas",
                Description = "Bloqueia acesso de apps à lista de tarefas.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\userDataTasks",
                ValueName = "Value",
                SafeValue = "Deny", UnsafeValue = "Allow",
                Level = PrivacyLevel.Recommended, Category = "Apps"
            },
            new() {
                Name = "Acesso a Mensagens",
                Description = "Bloqueia acesso de apps a mensagens.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\chat",
                ValueName = "Value",
                SafeValue = "Deny", UnsafeValue = "Allow",
                Level = PrivacyLevel.Recommended, Category = "Apps"
            },
            new() {
                Name = "Acesso a Radios",
                Description = "Bloqueia acesso de apps a rádios (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\radios",
                ValueName = "Value",
                SafeValue = "Allow", UnsafeValue = "Deny",
                Level = PrivacyLevel.NotRecommended, Category = "Apps"
            },
            new() {
                Name = "Acesso a Dispositivos Bluetooth",
                Description = "Bloqueia acesso de apps a dispositivos Bluetooth não emparelhados.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\bluetoothSync",
                ValueName = "Value",
                SafeValue = "Deny", UnsafeValue = "Allow",
                Level = PrivacyLevel.Recommended, Category = "Apps"
            },
            new() {
                Name = "Acesso a Documentos",
                Description = "Bloqueia acesso de apps à pasta Documentos (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\documentsLibrary",
                ValueName = "Value",
                SafeValue = "Allow", UnsafeValue = "Deny",
                Level = PrivacyLevel.NotRecommended, Category = "Apps"
            },
            new() {
                Name = "Acesso a Imagens",
                Description = "Bloqueia acesso de apps à pasta Imagens (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\picturesLibrary",
                ValueName = "Value",
                SafeValue = "Allow", UnsafeValue = "Deny",
                Level = PrivacyLevel.NotRecommended, Category = "Apps"
            },
            new() {
                Name = "Acesso a Vídeos",
                Description = "Bloqueia acesso de apps à pasta Vídeos (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\videosLibrary",
                ValueName = "Value",
                SafeValue = "Allow", UnsafeValue = "Deny",
                Level = PrivacyLevel.NotRecommended, Category = "Apps"
            },
            new() {
                Name = "Acesso ao Sistema de Arquivos",
                Description = "Bloqueia acesso de apps ao sistema de arquivos completo (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\broadFileSystemAccess",
                ValueName = "Value",
                SafeValue = "Allow", UnsafeValue = "Deny",
                Level = PrivacyLevel.NotRecommended, Category = "Apps"
            },
            new() {
                Name = "Acesso a Eye Tracking",
                Description = "Bloqueia acesso de apps a eye tracking.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\gazeInput",
                ValueName = "Value",
                SafeValue = "Deny", UnsafeValue = "Allow",
                Level = PrivacyLevel.Recommended, Category = "Apps"
            },
            new() {
                Name = "Acesso à Câmera (Global)",
                Description = "Bloqueia o acesso de todos os apps à webcam.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy",
                ValueName = "LetAppsAccessCamera",
                SafeValue = 2, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Apps"
            },
            new() {
                Name = "Acesso ao Microfone (Global)",
                Description = "Bloqueia o acesso de todos os apps ao microfone.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy",
                ValueName = "LetAppsAccessMicrophone",
                SafeValue = 2, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Apps"
            },
            new() {
                Name = "Acesso a Contatos (Global)",
                Description = "Bloqueia acesso de apps aos contatos.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy",
                ValueName = "LetAppsAccessContacts",
                SafeValue = 2, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Apps"
            },
            new() {
                Name = "Acesso a Notificações (Global)",
                Description = "Bloqueia acesso de apps às notificações.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy",
                ValueName = "LetAppsAccessNotifications",
                SafeValue = 2, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Apps"
            },

            // ====================== FEEDBACK ======================
            new() {
                Name = "Solicitações de Feedback",
                Description = "Impede que o Windows peça feedback.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DataCollection",
                ValueName = "DoNotShowFeedbackNotifications",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Feedback"
            },
            new() {
                Name = "Frequência de Feedback",
                Description = "Define frequência de solicitação de feedback como 'Nunca'.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Siuf\Rules",
                ValueName = "NumberOfSIUFInPeriod",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Feedback"
            },

            // ====================== EXPLORER & PRIVACIDADE ======================
            new() {
                Name = "Histórico de Arquivos Recentes",
                Description = "Impede que o Explorer mostre arquivos recentes no Acesso Rápido.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Explorer",
                ValueName = "ShowRecent",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Explorer"
            },

            // ====================== SEGURANÇA ======================
            new() {
                Name = "Botão Revelar Senha",
                Description = "Desativa o botão de revelar senha em campos de login.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CredUI",
                ValueName = "DisablePasswordReveal",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Limited, Category = "Segurança"
            },
            new() {
                Name = "Gravador de Passos",
                Description = "Desativa a ferramenta de gravação de passos (Steps Recorder).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\AppCompat",
                ValueName = "DisableUAR",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Segurança"
            },

            // ====================== TELA DE BLOQUEIO ======================
            new() {
                Name = "Destaques do Windows (Spotlight)",
                Description = "Desativa imagens e dicas da Microsoft na tela de bloqueio.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CloudContent",
                ValueName = "DisableWindowsSpotlightFeatures",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Limited, Category = "Tela de Bloqueio"
            },
            new() {
                Name = "Dicas e Truques na Tela de Bloqueio",
                Description = "Remove 'Você sabia?' e outras dicas da tela de bloqueio.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                ValueName = "RotatingLockScreenOverlayEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Tela de Bloqueio"
            },
            new() {
                Name = "Conteúdo Sugerido na Nuvem",
                Description = "Desativa conteúdo sugerido pela Microsoft (dicas, apps, etc.).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\CloudContent",
                ValueName = "DisableSoftLanding",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Tela de Bloqueio"
            },

            // ====================== EDGE ======================
            new() {
                Name = "Do Not Track (Edge)",
                Description = "Habilita cabeçalho 'Do Not Track' no Microsoft Edge.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\MicrosoftEdge\Main",
                ValueName = "DoNotTrack",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Edge"
            },
            new() {
                Name = "Sugestões de Pesquisa (Edge)",
                Description = "Desativa sugestões de pesquisa no Edge.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\MicrosoftEdge\SearchScopes",
                ValueName = "ShowSearchSuggestionsGlobal",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge"
            },
            // ====================== EDGE (NEW - Chromium) ======================
            new() {
                Name = "Edge - Tracking (Do Not Track)",
                Description = "Habilita Do Not Track no Edge Chromium.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "ConfigureDoNotTrack",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Métodos de Pagamento",
                Description = "Desativa verificação de métodos de pagamento salvos.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "PaymentMethodQueryEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Publicidade Personalizada",
                Description = "Desativa personalização de publicidade, busca e notícias.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "PersonalizationReportingEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Autocomplete de Endereços",
                Description = "Desativa autocompletar de endereços web na barra de endereço.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "AddressBarMicrosoftSearchInBingProviderEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Feedback na Toolbar",
                Description = "Desativa feedback do usuário na toolbar.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "UserFeedbackAllowed",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Autocomplete de Cartões de Crédito",
                Description = "Desativa salvamento e autocompletar de cartões de crédito.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "AutofillCreditCardEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Sugestões de Formulário",
                Description = "Desativa sugestões de formulário (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "AutofillAddressEnabled",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Sugestões de Provedores Locais",
                Description = "Desativa sugestões de provedores locais.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "LocalProvidersEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Sugestões de Pesquisa",
                Description = "Desativa sugestões de pesquisa e sites.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "SearchSuggestEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Shopping Assistant",
                Description = "Desativa assistente de compras no Edge.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "EdgeShoppingAssistantEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Edge Bar",
                Description = "Desativa Edge Bar.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "WebWidgetAllowed",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Sidebar",
                Description = "Desativa Sidebar no Edge.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "HubsSidebarEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Serviço Web para Erros de Navegação",
                Description = "Desativa uso de serviço web para erros de navegação (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "ResolveNavigationErrorsUseWebService",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Sites Similares",
                Description = "Desativa sugestão de sites similares quando site não é encontrado (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "AlternateErrorPagesEnabled",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Preload de Páginas",
                Description = "Desativa preload de páginas para navegação mais rápida (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "NetworkPredictionOptions",
                SafeValue = 2, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Salvar Senhas",
                Description = "Desativa salvamento de senhas para sites (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "PasswordManagerEnabled",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Serviços de Segurança de Sites",
                Description = "Desativa serviços de segurança de sites (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "SiteSafetyServicesEnabled",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - SmartScreen Filter",
                Description = "Desativa SmartScreen Filter (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "SmartScreenEnabled",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Edge (New)"
            },
            new() {
                Name = "Edge - Typosquatting Checker",
                Description = "Desativa verificador de typosquatting (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Edge",
                ValueName = "TyposquattingCheckerEnabled",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Edge (New)"
            },
            // ====================== EDGE LEGACY ======================
            new() {
                Name = "Edge Legacy - Do Not Track",
                Description = "Habilita Do Not Track no Edge Legacy.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Storage\microsoft.microsoftedge_8wekyb3d8bbwe\MicrosoftEdge\Main",
                ValueName = "DoNotTrack",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Edge (Legacy)"
            },
            new() {
                Name = "Edge Legacy - Page Prediction",
                Description = "Desativa predição de página (FlipAhead).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Storage\microsoft.microsoftedge_8wekyb3d8bbwe\MicrosoftEdge\FlipAhead",
                ValueName = "FPEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge (Legacy)"
            },
            new() {
                Name = "Edge Legacy - Sugestões de Pesquisa",
                Description = "Desativa sugestões de pesquisa no Edge Legacy.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Storage\microsoft.microsoftedge_8wekyb3d8bbwe\MicrosoftEdge\Main",
                ValueName = "ShowSearchSuggestionsGlobal",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge (Legacy)"
            },
            new() {
                Name = "Edge Legacy - Cortana",
                Description = "Desativa Cortana no Edge Legacy.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Storage\microsoft.microsoftedge_8wekyb3d8bbwe\MicrosoftEdge\ServiceUI",
                ValueName = "EnableCortana",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge (Legacy)"
            },
            new() {
                Name = "Edge Legacy - Histórico de Pesquisa",
                Description = "Desativa mostrando histórico de pesquisa.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Storage\microsoft.microsoftedge_8wekyb3d8bbwe\MicrosoftEdge\ServiceUI\ShowSearchHistory",
                ValueName = "@",
                SafeValue = 0, UnsafeValue = "",
                Level = PrivacyLevel.Recommended, Category = "Edge (Legacy)"
            },
            new() {
                Name = "Edge Legacy - Sugestões de Formulário",
                Description = "Desativa sugestões de formulário no Edge Legacy.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Storage\microsoft.microsoftedge_8wekyb3d8bbwe\MicrosoftEdge\Main",
                ValueName = "Use FormSuggest",
                SafeValue = "no", UnsafeValue = "yes",
                Level = PrivacyLevel.Recommended, Category = "Edge (Legacy)"
            },
            new() {
                Name = "Edge Legacy - Licenças de Mídia",
                Description = "Desativa salvamento de licenças de mídia protegidas.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Storage\microsoft.microsoftedge_8wekyb3d8bbwe\MicrosoftEdge\Privacy",
                ValueName = "EnableEncryptedMediaExtensions",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge (Legacy)"
            },
            new() {
                Name = "Edge Legacy - Screen Reader",
                Description = "Não otimiza resultados de busca para screen reader.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Storage\microsoft.microsoftedge_8wekyb3d8bbwe\MicrosoftEdge\Main",
                ValueName = "OptimizeWindowsSearchResultsForScreenReaders",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge (Legacy)"
            },
            new() {
                Name = "Edge Legacy - SmartScreen",
                Description = "Desativa SmartScreen no Edge Legacy (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppContainer\Storage\microsoft.microsoftedge_8wekyb3d8bbwe\MicrosoftEdge\PhishingFilter",
                ValueName = "EnabledV9",
                SafeValue = 4, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Edge (Legacy)"
            },
            new() {
                Name = "Edge Legacy - Dropdown na Barra de Endereço",
                Description = "Desativa dropdown na barra de endereço (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\PolicyManager\current\device\Browser",
                ValueName = "AllowAddressBarDropdown",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Edge (Legacy)"
            },
            new() {
                Name = "Edge Legacy - Tab Preloading",
                Description = "Desativa carregamento de abas em background (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\MicrosoftEdge\TabPreloader",
                ValueName = "AllowTabPreloading",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Edge (Legacy)"
            },
            new() {
                Name = "Edge Legacy - Prelaunch",
                Description = "Desativa prelanch do Edge Legacy.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\MicrosoftEdge\Main",
                ValueName = "AllowPrelaunch",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Edge (Legacy)"
            },
            // ====================== SYNCHRONIZATION ======================
            new() {
                Name = "Sync - Todas as Configurações",
                Description = "Desativa sincronização de todas as configurações.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\SettingSync",
                ValueName = "SyncPolicy",
                SafeValue = 0, UnsafeValue = 5,
                Level = PrivacyLevel.Limited, Category = "Sincronização"
            },
            new() {
                Name = "Sync - Configurações de Design",
                Description = "Desativa sincronização de configurações de design (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\SettingSync\Groups\Personalization",
                ValueName = "Enabled",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Sincronização"
            },
            new() {
                Name = "Sync - Configurações de Browser",
                Description = "Desativa sincronização de configurações de browser (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\SettingSync\Groups\BrowserSettings",
                ValueName = "Enabled",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Sincronização"
            },
            new() {
                Name = "Sync - Credenciais",
                Description = "Desativa sincronização de credenciais (senhas).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\SettingSync\Groups\Credentials",
                ValueName = "Enabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Sincronização"
            },
            new() {
                Name = "Sync - Configurações de Idioma",
                Description = "Desativa sincronização de configurações de idioma (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\SettingSync\Groups\Language",
                ValueName = "Enabled",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Sincronização"
            },
            new() {
                Name = "Sync - Configurações de Acessibilidade",
                Description = "Desativa sincronização de configurações de acessibilidade (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\SettingSync\Groups\Accessibility",
                ValueName = "Enabled",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Sincronização"
            },
            new() {
                Name = "Sync - Configurações Avançadas",
                Description = "Desativa sincronização de configurações avançadas do Windows (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\SettingSync\Groups\Windows",
                ValueName = "Enabled",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Sincronização"
            },
            // ====================== CORTANA ======================
            new() {
                Name = "Cortana - Consentimento",
                Description = "Desativa e reseta Cortana.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Windows Search",
                ValueName = "CortanaConsent",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Limited, Category = "Cortana"
            },
            new() {
                Name = "Cortana - Input Personalization",
                Description = "Desativa personalização de entrada.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Personalization\Settings",
                ValueName = "AcceptedPrivacyPolicy",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Limited, Category = "Cortana"
            },
            new() {
                Name = "Cortana - Coleta de Texto Implícito",
                Description = "Restringe coleta de texto implícito.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\InputPersonalization",
                ValueName = "RestrictImplicitTextCollection",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Limited, Category = "Cortana"
            },
            new() {
                Name = "Cortana - Coleta de Tinta Implícito",
                Description = "Restringe coleta de tinta implícito.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\InputPersonalization",
                ValueName = "RestrictImplicitInkCollection",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Limited, Category = "Cortana"
            },
            new() {
                Name = "Cortana - Harvest Contacts",
                Description = "Desativa coleta de contatos.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\InputPersonalization\TrainedDataStore",
                ValueName = "HarvestContacts",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Limited, Category = "Cortana"
            },
            new() {
                Name = "Cortana - Online Speech Recognition",
                Description = "Desativa reconhecimento de fala online.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\InputPersonalization",
                ValueName = "AllowInputPersonalization",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Limited, Category = "Cortana"
            },
            new() {
                Name = "Cortana - Resultados Web",
                Description = "Desativa resultados web na busca.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                ValueName = "ConnectedSearchUseWeb",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Cortana"
            },
            new() {
                Name = "Cortana - Download de Modelos de Fala",
                Description = "Desativa download de modelos de fala.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Preferences",
                ValueName = "ModelDownloadAllowed",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Limited, Category = "Cortana"
            },
            new() {
                Name = "Cortana - Localização na Busca",
                Description = "Cortana e busca não podem usar localização.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                ValueName = "AllowSearchToUseLocation",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Cortana"
            },
            new() {
                Name = "Cortana - Acima da Tela de Bloqueio",
                Description = "Desativa Cortana acima da tela de bloqueio (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                ValueName = "AllowCortanaAboveLock",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Cortana"
            },
            new() {
                Name = "Cortana - Cloud Search",
                Description = "Desativa busca na nuvem.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Windows Search",
                ValueName = "AllowCloudSearch",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Cortana"
            },
            // ====================== EXPLORER ======================
            new() {
                Name = "Explorer - Sugestões de Apps",
                Description = "Desativa sugestões ocasionais de apps no Menu Iniciar.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                ValueName = "SystemPaneSuggestionsEnabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Explorer"
            },
            new() {
                Name = "Explorer - Jump Lists",
                Description = "Não mostra itens recentes em Jump Lists.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                ValueName = "Start_TrackDocs",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Explorer"
            },
            new() {
                Name = "Explorer - Anúncios",
                Description = "Desativa anúncios no Windows Explorer/OneDrive.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced",
                ValueName = "ShowSyncProviderNotifications",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Explorer"
            },
            new() {
                Name = "Explorer - Histórico de Arquivos Recentes",
                Description = "Impede que o Explorer mostre arquivos recentes no Acesso Rápido.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Explorer",
                ValueName = "ShowRecent",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Explorer"
            },
            new() {
                Name = "OneDrive - Acesso à Rede Antes do Login",
                Description = "Desativa acesso à rede do OneDrive antes do login.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\OneDrive",
                ValueName = "PreventNetworkTrafficPreUserSignIn",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Explorer"
            },
            // ====================== LOCK SCREEN ======================
            new() {
                Name = "Lock Screen - Notificações",
                Description = "Desativa notificações na tela de bloqueio.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Notifications\Settings",
                ValueName = "NOC_GLOBAL_SETTING_ALLOW_TOASTS_ABOVE_LOCK",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Tela de Bloqueio"
            },
            new() {
                Name = "Lock Screen - Fun Facts",
                Description = "Desativa fatos curiosos, dicas e truques na tela de bloqueio.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager",
                ValueName = "SubscribedContent-338387Enabled",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Tela de Bloqueio"
            },
            // ====================== TASKBAR ======================
            new() {
                Name = "Taskbar - Ícone de Pessoas",
                Description = "Desativa ícone de Pessoas na barra de tarefas.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced\People",
                ValueName = "PeopleBand",
                SafeValue = 0, UnsafeValue = 2,
                Level = PrivacyLevel.Recommended, Category = "Taskbar"
            },
            new() {
                Name = "Taskbar - Caixa de Pesquisa",
                Description = "Desativa caixa de pesquisa na barra de tarefas (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Search",
                ValueName = "SearchboxTaskbarMode",
                SafeValue = 2, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Taskbar"
            },
            new() {
                Name = "Taskbar - Meet Now",
                Description = "Desativa botão Meet Now na barra de tarefas.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer",
                ValueName = "HideSCAMeetNow",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Taskbar"
            },
            new() {
                Name = "Taskbar - News and Interests",
                Description = "Desativa News and Interests na barra de tarefas.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Feeds",
                ValueName = "ShellFeedsTaskbarViewMode",
                SafeValue = 2, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Taskbar"
            },
            // ====================== MISC ======================
            new() {
                Name = "Misc - Media Player Diagnostics",
                Description = "Desativa diagnósticos do Windows Media Player.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\MediaPlayer\Preferences",
                ValueName = "UsageTracking",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Misc"
            },
            new() {
                Name = "Misc - Extensão de Busca com Bing",
                Description = "Desativa extensão de busca do Windows com Bing.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Policies\Microsoft\Windows\Explorer",
                ValueName = "DisableSearchBoxSuggestions",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Misc"
            },
            new() {
                Name = "Misc - Network Activity Status Indicator",
                Description = "Desativa indicador de atividade de rede (recomendado manter ativado).",
                RegistryPath = @"HKEY_CURRENT_USER\System\CurrentControlSet\Services\NlaSvc\Parameters\Internet",
                ValueName = "EnableActiveProbing",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Misc"
            },
            new() {
                Name = "Misc - PC Health Check",
                Description = "Desativa instalação do PC Health Check.",
                RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\PCHC",
                ValueName = "PreviousUninstall",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Misc"
            },
            new() {
                Name = "Misc - KMS Online Activation",
                Description = "Desativa ativação online do KMS.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows NT\CurrentVersion\Software Protection Platform",
                ValueName = "NoGenTicket",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Misc"
            },
            new() {
                Name = "Misc - Download de Mapas",
                Description = "Desativa download automático de dados de mapas (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Maps",
                ValueName = "AutoDownloadAndUpdateMapData",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Misc"
            },
            new() {
                Name = "Misc - Network Traffic em Offline Maps",
                Description = "Desativa tráfego de rede não solicitado em configurações de mapas offline (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\Maps",
                ValueName = "AllowUntriggeredNetworkTrafficOnSettingsPage",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Misc"
            },
            new() {
                Name = "Misc - Sensores",
                Description = "Desativa sensores para localização e orientação (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                ValueName = "DisableSensors",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Misc"
            },
            new() {
                Name = "Misc - Windows Location Provider",
                Description = "Desativa provedor de localização do Windows.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                ValueName = "DisableWindowsLocationProvider",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Misc"
            },
            new() {
                Name = "Misc - Windows Geolocation Service",
                Description = "Desativa serviço de geolocalização do Windows.",
                RegistryPath = @"HKEY_CURRENT_USER\System\CurrentControlSet\Services\lfsvc\Service\Configuration",
                ValueName = "Status",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Misc"
            },
            new() {
                Name = "Misc - Location Scripting",
                Description = "Desativa scripting de localização.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LocationAndSensors",
                ValueName = "DisableLocationScripting",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Misc"
            },
            new() {
                Name = "Misc - Microsoft SpyNet",
                Description = "Desativa participação no Microsoft SpyNet.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Spynet",
                ValueName = "SpyNetReporting",
                SafeValue = 0, UnsafeValue = 2,
                Level = PrivacyLevel.Recommended, Category = "Misc"
            },
            new() {
                Name = "Misc - Submit Samples",
                Description = "Desativa envio de amostras para a Microsoft.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender\Spynet",
                ValueName = "SubmitSamplesConsent",
                SafeValue = 2, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Misc"
            },
            new() {
                Name = "Misc - Malware Infection Reporting",
                Description = "Desativa relatório de infecção de malware.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\MRT",
                ValueName = "DontReportInfectionInformation",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Misc"
            },
            new() {
                Name = "Misc - DRM Online",
                Description = "Desativa acesso à internet do Windows Media DRM.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\WMDRM",
                ValueName = "DisableOnline",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Limited, Category = "Misc"
            },
            new() {
                Name = "Misc - Speech Model Update",
                Description = "Desativa atualização de modelos de fala.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Speech",
                ValueName = "AllowSpeechModelUpdate",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Limited, Category = "Misc"
            },
            new() {
                Name = "Misc - Defer Updates",
                Description = "Adia atualizações do Windows.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate",
                ValueName = "DeferUpgrade",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Misc"
            },
            new() {
                Name = "Misc - Allow Experimentation",
                Description = "Desativa configuração dinâmica e rollouts de atualizações.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\PolicyManager\current\device\System",
                ValueName = "AllowExperimentation",
                SafeValue = 0, UnsafeValue = 1,
                Level = PrivacyLevel.Recommended, Category = "Misc"
            },
            new() {
                Name = "Misc - Auto Download Store Apps",
                Description = "Desativa download automático de apps da loja (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsStore\WindowsUpdate",
                ValueName = "AutoDownload",
                SafeValue = 2, UnsafeValue = 4,
                Level = PrivacyLevel.NotRecommended, Category = "Misc"
            },
            new() {
                Name = "Misc - Device Metadata",
                Description = "Desativa download automático de apps e ícones de fabricantes.",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Device Metadata",
                ValueName = "PreventDeviceMetadataFromNetwork",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.Recommended, Category = "Misc"
            },
            new() {
                Name = "Misc - Windows Update for Other Products",
                Description = "Desativa Windows Updates para outros produtos (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Services\7971f918-a847-4430-9279-4a52d1efe18d",
                ValueName = "RegisteredWithAU",
                SafeValue = 1, UnsafeValue = 0,
                Level = PrivacyLevel.NotRecommended, Category = "Misc"
            },
            new() {
                Name = "Misc - Windows Update Service",
                Description = "Desativa serviço do Windows Update (recomendado manter ativado).",
                RegistryPath = @"HKEY_LOCAL_MACHINE\System\CurrentControlSet\Services\wuauserv",
                ValueName = "Start",
                SafeValue = 3, UnsafeValue = 4,
                Level = PrivacyLevel.NotRecommended, Category = "Misc",
                IsService = true, ServiceName = "wuauserv"
            },
        };

        #endregion

        #region Métodos de Gerenciamento

        public static List<PrivacySetting> GetPrivacySettings() => PrivacySettings;

        public static Dictionary<string, List<PrivacySetting>> GetPrivacyCategories()
        {
            return PrivacySettings.GroupBy(s => s.Category)
                                 .ToDictionary(g => g.Key, g => g.ToList());
        }

        /// <summary>
        /// Verifica se a configuração de privacidade está aplicada (protegida).
        /// CORRIGIDO: usa RegistryPath diretamente (não trunca mais).
        /// </summary>
        public static bool IsPrivacySettingApplied(PrivacySetting setting)
        {
            try
            {
                if (setting.IsService && !string.IsNullOrEmpty(setting.ServiceName))
                {
                    var startMode = SystemUtils.GetServiceStartMode(setting.ServiceName);
                    string safeValStr = setting.SafeValue?.ToString() ?? "4";
                    if (safeValStr == "4") return startMode == "Disabled";
                    return false; 
                }
                else
                {
                    // Registry.GetValue espera: keyName (caminho completo com hive), valueName, defaultValue
                    var val = Registry.GetValue(setting.RegistryPath, setting.ValueName, null);
                    
                    if (val == null) return false; // Se não existe, assume padrão (inseguro)
                    
                    return val.ToString() == setting.SafeValue?.ToString();
                }
            }
            catch { return false; }
        }

        public static bool ApplyPrivacySetting(PrivacySetting setting)
        {
            try
            {
                if (setting.IsService && !string.IsNullOrEmpty(setting.ServiceName))
                {
                    int mode = Convert.ToInt32(setting.SafeValue);
                    string modeStr = mode == 4 ? "disabled" : (mode == 2 ? "auto" : "demand");
                    SystemUtils.RunExternalProcess("sc", $"config \"{setting.ServiceName}\" start= {modeStr}", true);
                    if (mode == 4) SystemUtils.RunExternalProcess("sc", $"stop \"{setting.ServiceName}\"", true);
                    return true;
                }
                else
                {
                    string key = setting.RegistryPath;
                    string val = setting.ValueName;
                    object data = setting.SafeValue ?? 0;
                    
                    RegistryKey hive = key.StartsWith("HKEY_LOCAL_MACHINE") ? Registry.LocalMachine : Registry.CurrentUser;
                    string subKey = key.Substring(key.IndexOf('\\') + 1);

                    using var rk = hive.CreateSubKey(subKey, true);
                    rk.SetValue(val, data, data is string ? RegistryValueKind.String : RegistryValueKind.DWord);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"ApplyPrivacy[{setting.Name}]", ex.Message);
                return false;
            }
        }

        public static bool RevertPrivacySetting(PrivacySetting setting)
        {
            try
            {
                if (setting.IsService && !string.IsNullOrEmpty(setting.ServiceName))
                {
                    int mode = Convert.ToInt32(setting.UnsafeValue);
                    string modeStr = mode == 2 ? "auto" : "demand";
                    SystemUtils.RunExternalProcess("sc", $"config \"{setting.ServiceName}\" start= {modeStr}", true);
                    return true;
                }
                else
                {
                    string key = setting.RegistryPath;
                    string val = setting.ValueName;

                    RegistryKey hive = key.StartsWith("HKEY_LOCAL_MACHINE") ? Registry.LocalMachine : Registry.CurrentUser;
                    string subKey = key.Substring(key.IndexOf('\\') + 1);

                    using var rk = hive.OpenSubKey(subKey, true);
                    if (rk != null)
                    {
                        if (setting.UnsafeValue != null)
                            rk.SetValue(val, setting.UnsafeValue);
                        else
                            rk.DeleteValue(val, false);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"RevertPrivacy[{setting.Name}]", ex.Message);
                return false;
            }
        }

        public static (bool Success, string Message) ApplyPreset(PrivacyLevel targetLevel)
        {
            try
            {
                int successCount = 0;
                foreach (var s in PrivacySettings)
                {
                    if ((int)s.Level <= (int)targetLevel)
                    {
                        if (ApplyPrivacySetting(s))
                            successCount++;
                    }
                }
                return (true, $"Preset aplicado com sucesso. {successCount} configurações ajustadas.");
            }
            catch (Exception ex) { return (false, $"Erro ao aplicar preset: {ex.Message}"); }
        }

        public static (bool Success, string Message) RestoreDefaults()
        {
            try
            {
                int successCount = 0;
                foreach (var s in PrivacySettings)
                {
                    if (RevertPrivacySetting(s))
                        successCount++;
                }
                return (true, $"Restaurados {successCount} de {PrivacySettings.Count} padrões.");
            }
            catch (Exception ex)
            {
                Logger.LogError("RestoreDefaults", ex.Message);
                return (false, ex.Message);
            }
        }

        public static (bool Success, string Message) SaveUserConfig()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "privacy_config.json");
                var config = new Dictionary<string, bool>();

                foreach (var setting in PrivacySettings)
                {
                    bool isApplied = IsPrivacySettingApplied(setting);
                    config[setting.Name] = isApplied;
                }

                string json = System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);

                return (true, $"Configuração salva em: {configPath}");
            }
            catch (Exception ex)
            {
                Logger.LogError("SaveUserConfig", ex.Message);
                return (false, ex.Message);
            }
        }

        public static (bool Success, string Message) RestoreUserConfig()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "privacy_config.json");

                if (!File.Exists(configPath))
                {
                    return (false, "Nenhuma configuração salva encontrada.");
                }

                string json = File.ReadAllText(configPath);
                var config = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(json);

                if (config == null)
                {
                    return (false, "Erro ao carregar configuração.");
                }

                int successCount = 0;
                foreach (var setting in PrivacySettings)
                {
                    if (config.TryGetValue(setting.Name, out bool shouldBeEnabled))
                    {
                        if (shouldBeEnabled)
                        {
                            if (ApplyPrivacySetting(setting))
                                successCount++;
                        }
                        else
                        {
                            if (RevertPrivacySetting(setting))
                                successCount++;
                        }
                    }
                }

                return (true, $"Restaurados {successCount} de {PrivacySettings.Count} configurações.");
            }
            catch (Exception ex)
            {
                Logger.LogError("RestoreUserConfig", ex.Message);
                return (false, ex.Message);
            }
        }

        #endregion
    }
}
