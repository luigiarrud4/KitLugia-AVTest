using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;

namespace KitLugia.Core
{
    [Flags]
    public enum PathProblem
    {
        None = 0,
        Duplicate = 1,
        MissingDirectory = 2,
        SyntaxError = 4,
        LongPath = 8,
        TooManyEntries = 16,
        RelativePath = 32,
        UnquotedSpace = 64,
        TempPath = 128,
        UserPathWithoutVariable = 256,
        DevelopmentJunk = 512,
        PathTooLong = 1024,
        EncodingIssue = 2048
    }

    [Flags]
    public enum ExplorerProblem
    {
        None = 0,
        // Problemas de clique duplo (double-click)
        DoubleClickFolderAssociationBroken = 1 << 0,      // Pasta abre programa errado
        DoubleClickFolderNotOpening = 1 << 1,             // Duplo clique em pasta não faz nada
        DoubleClickFileNotOpening = 1 << 2,               // Duplo clique em arquivo não funciona
        DoubleClickExeBroken = 1 << 3,                    // Executável não abre ao clicar

        // Problemas de menu de contexto (right-click)
        ContextMenuExplorerCrash = 1 << 4,                // Clique direito trava ou fecha o Explorer
        ContextMenuItemsMissing = 1 << 5,                 // Itens do menu de contexto sumiram
        ContextMenuSlowOrFreeze = 1 << 6,                 // Menu demora segundos para abrir

        // Problemas de Shell Extension
        InvalidContextMenuHandler = 1 << 7,               // Handler malicioso/corrompido
        BlockedContextMenuExtension = 1 << 8,             // Extensão bloqueada (ex.: por CCleaner)

        // Problemas de Explorer
        ExplorerDoesNotStart = 1 << 9                     // Explorer não inicia ou morre
    }

    [SupportedOSPlatform("windows")]
    public static class Guardian
    {
        #region Definições de Tweaks (Lista Completa e Detalhada)

    // Mudei de private para internal/static acessível via método abaixo
    [ThreadStatic]
    private static RegistryBatch? _currentBatch;

    // GATE GLOBAL do scan: abrir a Integridade + digitar na busca + toggles
    // disparavam scans CONCORRENTES (cada um com bcdedit + varredura de PATH
    // propria) e o disco/CPU saturam - o app parecia travado. Com o lock, o
    // 2o scan espera o 1o terminar (chamadores sao background threads).
    private static readonly object _scanGate = new();

    // Cache do bcdedit /enum COMPARTILHADO entre scans/toggles (TTL 15s):
    // o antigo ThreadStatic forçava um bcdedit /enum por thread/scan.
    private static string? _bcdEnumOutput;
    private static string? _bcdEnumError;
    private static bool _bcdEnumAttempted;
    private static DateTime _bcdEnumTimeUtc;
    private const double BcdEnumCacheTtlSeconds = 15;

    /// <summary>
    /// Serviços que o KitLugia desativa DE PROPÓSITO (toggles de telemetria, diagnóstico,
    /// fix de memory leak). O Integrity não deve marcá-los como vulnerabilidade nem
    /// reativá-los no "Restaurar Todos" - reativar desfazeria os toggles do usuário.
    /// </summary>
    private static readonly Dictionary<string, string> KitIntentionalServiceStart = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DPS"] = "Disabled",
        ["WdiServiceHost"] = "Disabled",
        ["WdiSystemHost"] = "Disabled",
        ["DiagTrack"] = "Disabled",
        ["dmwappushservice"] = "Disabled",
        ["WerSvc"] = "Disabled",
        ["PcaSvc"] = "Disabled",
        ["NDU"] = "Disabled",
    };

    private static readonly List<ScannableTweak> HarmfulTweaks = new()
    {

        // ==================================================================================
        // 1. SEGURANÇA CRÍTICA DO SISTEMA
        // ==================================================================================
        new() {
            Name = "Mitigações de CPU (Spectre/Meltdown)",
            Description = "Correções de segurança para o processador. Protege contra vulnerabilidades que permitem que programas maliciosos leiam dados sensíveis da memória do sistema.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "FeatureSettingsOverride", HarmfulValue = 3, DefaultValue = 0
        },
        new() {
            Name = "Control Flow Guard (CFG)",
            Description = "Uma defesa contra exploits que impede que malwares sequestrem o fluxo de execução de programas legítimos para rodar código malicioso.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "EnableCfg", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Controle de Conta de Usuário (UAC)",
            Description = "A janela de confirmação 'Sim/Não'. Impede que vírus ou scripts façam alterações administrativas no sistema sem o seu consentimento explícito.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "EnableLUA", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção de Execução de Dados (DEP)",
            Description = "Impede que códigos maliciosos sejam executados em áreas da memória reservadas apenas para dados. Essencial para evitar ataques de buffer overflow.",
            Category = "Segurança Crítica", Type = TweakType.Bcd, ValueName = "nx", HarmfulValue = "AlwaysOff", DefaultValue = "OptIn"
        },
        new() {
            Name = "Protocolo Inseguro SMBv1",
            Description = "Um protocolo de compartilhamento de rede antigo e obsoleto, famoso por ser o vetor de ataque do ransomware WannaCry. Deve permanecer desativado.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", ValueName = "SMB1", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Execução Automática de USB",
            Description = "Impede que vírus de Pen Drive (autorun.inf) infectem o computador automaticamente assim que a mídia é conectada.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoDriveTypeAutoRun", HarmfulValue = 0, DefaultValue = 145
        },

        new() {
            Name = "Proteção contra Injeção de DLL",
            Description = "Previne injeção de DLL maliciosa em processos legítimos. Malware pode sequestrar processos do sistema, consumindo recursos e causando lentidão.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager", ValueName = "ProtectionMode", HarmfulValue = 0, DefaultValue = 1
        },

        new() {
            Name = "Integridade de Memória (HVCI)",
            Description = "Proteção de integridade de código do hypervisor. Desativar expõe o sistema a injeção de kernel e rootkits. Sistemas sem HVCI são suscetíveis a ataques de persistência avançados.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", ValueName = "Enabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Bloqueio de Driver Vulnerável (Microsoft Blocklist)",
            Description = "Lista atualizada de drivers vulneráveis. Desativar permite instalação de drivers com exploits conhecidos que podem comprometer o kernel, causar BSODs e lentidão extrema.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CI\Config", ValueName = "VulnerableDriverBlocklistEnable", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção de SAM (Security Account Manager)",
            Description = "Impede acesso não autorizado ao banco de dados de senhas do Windows. Desativar permite que ferramentas de quebra de senha acessem hashes de senha.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa", ValueName = "RestrictAnonymous", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Mitigação de NTLMv1",
            Description = "Protocolo de autenticação antigo e inseguro. Permite ataques de relay e pass-the-hash. Desativar NTLMv1 impede que atacantes capturem credenciais na rede.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa\MSV1_0", ValueName = "NtlmMinClientSec", HarmfulValue = 0, DefaultValue = 537395200
        },
        new() {
            Name = "Proteção de LSASS (Credential Guard)",
            Description = "Protege o processo LSASS que armazena credenciais. Desativar permite que ferramentas como Mimikatz extraiam senhas e hashes da memória.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa", ValueName = "RunAsPPL", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção contra Enumeração de Usuários",
            Description = "Impede que usuários anônimos enumerem contas de usuário do sistema. Atacantes podem usar isso para mapear contas válidas para ataques direcionados.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa", ValueName = "RestrictAnonymousSAM", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção de Cache de Logon",
            Description = "Controla quantos logons anteriores são armazenados em cache. Valores altos permitem que atacantes com acesso físico ao disco extraiam credenciais criptografadas.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", ValueName = "CachedLogonsCount", HarmfulValue = 50, DefaultValue = 10
        },
        new() {
            Name = "Exigência de Ctrl+Alt+Del para Login",
            Description = "Impede ataques de 'Trojan de tela de login' que simulam a tela de login para capturar senhas. Desativar permite que keyloggers capturem credenciais.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "DisableCAD", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Proteção de Kernel Driver Signature",
            Description = "Exige que drivers de kernel sejam assinados digitalmente. Desativar permite que drivers maliciosos não assinados sejam carregados, comprometendo a estabilidade.",
            Category = "Segurança Crítica", Type = TweakType.Bcd, ValueName = "nointegritychecks", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "Test Mode (Modo de Teste do Windows)",
            Description = "Modo de teste permite executar código não assinado no kernel. Águas no canto da tela indicam que o sistema está em modo inseguro.",
            Category = "Segurança Crítica", Type = TweakType.Bcd, ValueName = "testsigning", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "Exigência de Senha ao Acordar",
            Description = "Sem exigência de senha ao acordar, qualquer pessoa com acesso físico pode usar o computador desbloqueado, expondo dados pessoais.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "DisableLockScreen", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Proteção WDigest (Credenciais em Texto Claro)",
            Description = "Controla se as credenciais são armazenadas em texto claro na memória (WDigest). Ativado permite que ferramentas de extração capturem senhas em texto puro.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecurityProviders\WDigest", ValueName = "UseLogonCredential", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Proteção contra Ataques de DLL Search Order",
            Description = "Impede que malwares explorem a ordem de busca de DLL para carregar versões maliciosas no lugar de DLLs legítimas do sistema.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager", ValueName = "SafeDllSearchMode", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção UEFI Secure Boot",
            Description = "Impede que bootkits e rootkits de firmware sejam carregados durante a inicialização. Desativar permite malware persistente de nível de firmware.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State", ValueName = "UEFISecureBootEnabled", HarmfulValue = 0, DefaultValue = 1
        },
        // ── Checks que faltavam de verdade (06/10/2026) ──
        // Todos verificados contra o registro real desta maquina antes de entrarem.
        new() {
            Name = "Assinatura SMB Obrigatória (Servidor)",
            Description = "Exige assinatura SMB em todas as comunicações. Sem isso, um atacante na rede local consegue fazer man-in-the-middle e sequestrar sessões.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", ValueName = "Smb2SigningEnabled", HarmfulValue = 0, DefaultValue = 1, MissingIsHarmful = true
        },
        new() {
            Name = "Assinatura SMB Obrigatória (Cliente)",
            Description = "Exige assinatura SMB também na saída. Sem isso, o tráfego para servidores de rede pode ser interceptado e alterado.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters", ValueName = "Smb2SigningEnabled", HarmfulValue = 0, DefaultValue = 1, MissingIsHarmful = true
        },
        new() {
            Name = "Hash de Senha LM Desabilitado (Somente NTLM)",
            Description = "Impede que o Windows guarde o hash de senha em LAN Manager, que é quebrado e quebra em segundos. A ausência da chave significa que o hash LM é permitido.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa", ValueName = "NoLMHash", HarmfulValue = 0, DefaultValue = 1, MissingIsHarmful = true
        },
        new() {
            Name = "Convidados treated como Anônimos Desligado",
            Description = "Desligado, o grupo Everyone deixa de ser tratado como anônimo e o bypass de permissões em compartilhamentos de rede não abre.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa", ValueName = "EveryoneIncludesAnonymous", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "RDP Não Salva Credenciais",
            Description = "Salvar a senha do acesso remoto deixa a credencial recuperável por qualquer processo do seu usuário. Só pesa se o acesso remoto estiver ligado, por isso é opcional.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", ValueName = "fPromptForPassword", HarmfulValue = 0, DefaultValue = 1
        },

        // ==================================================================================
        // 2. DEFESA E ANTIVÍRUS
        // ==================================================================================
        new() {
            Name = "Antivírus Windows Defender",
            Description = "Proteção essencial em tempo real contra vírus e ameaças. Se você não possui outro antivírus instalado, desativar isso deixa o PC vulnerável.",
            Category = "Defesa e Antivírus", Type = TweakType.Service, ServiceName = "WinDefend", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Firewall do Windows",
            Description = "Filtra o tráfego de rede de entrada e saída. Desativar permite que hackers ou worms acessem seu PC diretamente pela internet.",
            Category = "Defesa e Antivírus", Type = TweakType.Service, ServiceName = "MpsSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Proteção contra Violação",
            Description = "Recurso de autodefesa do Windows que impede que malwares ou scripts desativem o antivírus e suas configurações de segurança.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Features", ValueName = "TamperProtection", HarmfulValue = 0, DefaultValue = 5
        },
        new() {
            Name = "Central de Segurança (WSC)",
            Description = "Monitora o status do antivírus, firewall e manutenção. Se desligado, o Windows não alertará sobre falhas na sua proteção.",
            Category = "Defesa e Antivírus", Type = TweakType.Service, ServiceName = "wscsvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Delayed-Auto"
        },
        new() {
            Name = "Filtro SmartScreen",
            Description = "Verifica sites e downloads em busca de ameaças conhecidas antes de executá-los, protegendo contra phishing e malwares novos.",
            IsOptional = true,
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\System", ValueName = "EnableSmartScreen", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção em Tempo Real do Defender",
            Description = "Monitora continuamente atividades suspeitas no sistema. Desativar permite que malware opere sem ser detectado, consumindo recursos em segundo plano.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Real-Time Protection", ValueName = "DisableRealtimeMonitoring", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Proteção Baseada em Nuvem do Defender",
            Description = "Usa inteligência em nuvem da Microsoft para detectar ameaças novas em segundos. Desativar reduz a eficácia do antivírus contra malware zero-day.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Spynet", ValueName = "SpynetReporting", HarmfulValue = 0, DefaultValue = 2
        },
        new() {
            Name = "Envio Automático de Amostras",
            Description = "Envia amostras de arquivos suspeitos para análise na Microsoft. Desativar impede que novos malwares sejam identificados e bloqueados rapidamente.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Spynet", ValueName = "SubmitSamplesConsent", HarmfulValue = 2, DefaultValue = 1
        },
        new() {
            Name = "Scan de Arquivos Baixados (Defender)",
            Description = "Verifica arquivos baixados da internet em busca de malware antes de permitir a execução. Desativar permite que downloads maliciosos sejam executados.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments", ValueName = "SaveZoneInformation", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção contra Aplicativos Potencialmente Indesejados (PUP)",
            Description = "Bloqueia instalação de programas que podem não ser maliciosos mas são indesejados (adware, toolbars, cryptominers). Desativar permite que PUP se instalem.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows Defender", ValueName = "PUAProtection", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção de Acesso a Pastas (Ransomware Protection)",
            Description = "Impede que aplicativos não autorizados modifiquem pastas protegidas. Desativar expõe documentos a ataques de ransomware que criptografam arquivos.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access", ValueName = "EnableControlledFolderAccess", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Serviço de Segurança do Windows (SecurityHealthService)",
            Description = "Serviço que monitora a saúde geral da segurança. Desativar impede que o sistema alerte sobre firewall, antivírus ou outros problemas de segurança.",
            Category = "Defesa e Antivírus", Type = TweakType.Service, ServiceName = "SecurityHealthService", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
                 // ==================================================================================
        // 3. RESTRIÇÕES DO SISTEMA (SINAIS DE ALERTA)
        // ==================================================================================
        new() {
            Name = "Gerenciador de Tarefas (Bloqueado)",
            Description = "Se estiver bloqueado, é um forte indício de vírus ou restrição administrativa tentando impedir que você veja e encerre processos.",
            Category = "Restrições do Sistema", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "DisableTaskMgr", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Editor do Registro (Bloqueado)",
            Description = "Malwares frequentemente bloqueiam o Regedit para impedir que você remova suas chaves de inicialização ou corrija o sistema.",
            Category = "Restrições do Sistema", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "DisableRegistryTools", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Prompt de Comando (Bloqueado)",
            Description = "O bloqueio do CMD impede a execução de ferramentas de reparo, scripts de limpeza e diagnósticos avançados.",
            Category = "Restrições do Sistema", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "DisableCMD", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Instalador Windows MSI (Bloqueado)",
            Description = "Impede a instalação ou remoção de programas .msi. Frequentemente usado para impedir a instalação de ferramentas de segurança.",
            Category = "Restrições do Sistema", Type = TweakType.Service, ServiceName = "msiserver", HarmfulStartMode = "Disabled", DefaultStartMode = "Demand"
        },
        new() {
            Name = "Modo de Segurança (Bloqueado)",
            Description = "Impede a inicialização em Modo de Segurança via F8. Malware frequentemente desativa isso para impedir que você inicie o sistema com drivers mínimos para remoção.",
            Category = "Restrições do Sistema", Type = TweakType.Bcd, ValueName = "safebootalternateshell", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "Ferramentas de Administração (Bloqueadas)",
            Description = "Bloqueia acesso ao Painel de Controle e Configurações. Impede que o usuário faça alterações no sistema ou desinstale programas maliciosos.",
            Category = "Restrições do Sistema", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoControlPanel", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Alteração de Senha (Bloqueada)",
            Description = "Impede que o usuário mude sua própria senha. Se ativado sem seu conhecimento, um invasor pode estar mantendo você preso em uma conta comprometida.",
            Category = "Restrições do Sistema", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "DisableChangePassword", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Windows Update (Bloqueado via Política)",
            Description = "Bloqueia completamente o Windows Update via política de grupo. Impede o recebimento de correções críticas de segurança e mantém o sistema vulnerável.",
            Category = "Restrições do Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", ValueName = "NoAutoUpdate", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Remoção de Programas (Bloqueada)",
            Description = "Impede a desinstalação de programas via Painel de Controle. Malware usa isso para impedir que você remova programas indesejados.",
            Category = "Restrições do Sistema", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Uninstall", ValueName = "NoRemovePage", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Adicionar Impressora (Bloqueado)",
            Description = "Impede a instalação de novas impressoras. Pode indicar restrições excessivas de Grupo Doméstico ou configuração maliciosa.",
            Category = "Restrições do Sistema", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoAddPrinter", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Disco Rígido (Oculto no Explorer)",
            Description = "Unidades de disco inteiras foram ocultadas do Explorer. Pode ser configuração maliciosa para esconder partições de recuperação ou dados.",
            Category = "Restrições do Sistema", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoDrives", HarmfulValue = 67108863, DefaultValue = 0
        },
        new() {
            Name = "Downloads (Bloqueado no Explorer)",
            Description = "Impede o download de arquivos da internet. Configuração excessivamente restritiva que pode indicar infecção ou política mal aplicada.",
            Category = "Restrições do Sistema", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoFileMenu", HarmfulValue = 1, DefaultValue = 0
        },

        // ==================================================================================
        // 4. SERVIÇOS ESSENCIAIS
        // ==================================================================================
        new() {
            Name = "Serviço de Áudio",
            Description = "Responsável por gerenciar o som do Windows. Se desativado, o computador ficará sem áudio.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "AudioSrv", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Serviço de Perfis de Usuário",
            Description = "Carrega e descarrega as configurações do usuário. Falha neste serviço pode impedir o login ou corromper o perfil.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "ProfSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Serviço de Eventos (Log)",
            Description = "Registra erros e atividades do sistema. Essencial para diagnóstico de falhas e para o funcionamento de vários serviços.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "eventlog", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Agendador de Tarefas",
            Description = "Permite configurar e executar tarefas automatizadas. Vital para manutenção do sistema e inicialização de muitos programas.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "Schedule", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Chamada Remota (RPC)",
            Description = "O 'sistema nervoso' do Windows. Permite a comunicação entre processos. Quase tudo depende disso. Nunca deve ser desativado.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "RpcSs", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Mecanismo de Filtragem Base (BFE)",
            Description = "Gerencia políticas de firewall e IPsec. Se desativado, reduz drasticamente a segurança da rede e quebra o Firewall.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "BFE", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Gerenciador de Credenciais (Vault)",
            Description = "Armazena e recupera senhas salvas de sites, redes e aplicativos com segurança.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "VaultSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Área de Transferência",
            Description = "Habilita a funcionalidade de copiar e colar moderna, incluindo histórico (Win+V) e sincronização.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "cbdhsvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Cache de Fontes",
            Description = "Otimiza o desempenho de renderização de texto. Desativar pode causar lentidão na abertura de aplicativos.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "FontCache", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Gerenciamento de Sessão de Desktop",
            Description = "Gerencia sessões de desktop e Terminal Server. Essencial para multi-sessão e Remote Desktop.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "SessionEnv", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Serviço de Política de Diagnóstico",
            Description = "Detecta e resolve problemas de rede e sistema. Desativar impede que o Windows diagnostique e corrija problemas automaticamente.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "DPS", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Serviço de Transferência Inteligente (BITS)",
            Description = "Gerencia transferências de arquivos em segundo plano. Windows Update e muitos aplicativos dependem dele para baixar atualizações.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "BITS", HarmfulStartMode = "Disabled", DefaultStartMode = "Delayed-Auto"
        },
        new() {
            Name = "Serviço de Plug and Play",
            Description = "Detecta e configura automaticamente novo hardware conectado ao sistema. Desativar impede que dispositivos USB, impressoras e outros funcionem.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "PlugPlay", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Serviço de Relatório de Erros",
            Description = "Envia relatórios de erro para a Microsoft. Essencial para diagnóstico de falhas e identificação de problemas de driver.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "WerSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Serviço de Criptografia (CryptSvc)",
            Description = "Gerencia certificados e criptografia. Desativar impede instalação de programas, atualizações e acesso a sites HTTPS.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "CryptSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Serviço de Licenciamento de Software",
            Description = "Gerencia licenças de software da Microsoft Store e aplicativos. Desativar impede a ativação de aplicativos e verificações de licença.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "ClipSVC", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Serviço de Experiência do Usuário (Themes)",
            Description = "Gerencia temas visuais do Windows. Desativar faz o sistema parecer Windows 95 e pode causar problemas de renderização.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "Themes", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Serviço de Imagem do Windows (WIA)",
            Description = "Gerencia scanners e câmeras. Desativar impede que scanners e softwares de imagem funcionem corretamente.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "stisvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Serviço de Bluetooth",
            Description = "Gerencia dispositivos Bluetooth. Desativar impede conexão de mouses, teclados, fones e outros dispositivos Bluetooth.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "BthServ", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Serviço de BIOS do Windows (Wbios)",
            Description = "Fornece informações de firmware e BIOS para o sistema. Desativar pode afetar detecção de hardware e informações do sistema.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "wbiosrvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Serviço de Geolocalização",
            Description = "Fornece dados de localização para aplicativos. Desativar afeta Mapas, Clima e outros apps que dependem de localização.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "lfsvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Serviço de Impressão (Spooler)",
            Description = "Gerencia fila de impressão. Desativar impede completamente a impressão de documentos.",
            IsOptional = true,
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "Spooler", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Serviço de Notificações de Eventos de Sistema",
            Description = "Notifica componentes registrados sobre eventos do sistema. Desativar pode quebrar funcionalidades de áudio, rede e hardware.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "SENS", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Serviço de Hospedagem de Dispositivo UPnP",
            Description = "Gerencia dispositivos de rede UPnP. Desativar impede descoberta de dispositivos de rede como consoles e media servers.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "upnphost", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Serviço de Auxílio de Compatibilidade de Programas",
            Description = "Ajuda programas antigos a funcionar no Windows moderno. Desativar pode causar falhas em aplicativos legados.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "PcaSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        // Removido: duplicata de wscsvc (já em 'Defesa e Antivírus' como 'Central de Segurança')

        // ==================================================================================
        // 5. REDE E CONECTIVIDADE
        // ==================================================================================
        new() {
            Name = "Cliente DNS",
            Description = "Traduz nomes de sites (ex: google.com) para endereços IP. Sem ele, a navegação na internet para de funcionar.",
            Category = "Rede e Conectividade", Type = TweakType.Service, ServiceName = "Dnscache", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Cliente DHCP",
            Description = "Obtém um endereço IP automaticamente do seu roteador. Necessário para conectar à maioria das redes.",
            Category = "Rede e Conectividade", Type = TweakType.Service, ServiceName = "Dhcp", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Configuração de Wi-Fi (WLAN)",
            Description = "Gerencia conexões sem fio. Essencial para laptops e computadores que utilizam Wi-Fi.",
            Category = "Rede e Conectividade", Type = TweakType.Service, ServiceName = "WlanSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Lista de Redes (Network List)",
            Description = "Identifica as redes às quais o computador se conectou e suas configurações. Necessário para ícone de rede na barra.",
            Category = "Rede e Conectividade", Type = TweakType.Service, ServiceName = "netprofm", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Remote Desktop Protocol (RDP) Vulnerability",
            Description = "Vulnerabilidade no RDP. Configurações inseguras podem permitir acesso remoto não autorizado e degradação de performance.",
            Category = "Rede e Conectividade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server", ValueName = "fDenyTSConnections", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "TCP Window Scaling (Desativado)",
            Description = "Permite janelas TCP maiores para melhor throughput. Desativar limita a velocidade máxima de download em conexões de alta latência.",
            Category = "Rede e Conectividade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", ValueName = "Tcp1323Opts", HarmfulValue = 0, DefaultValue = 3
        },
        new() {
            Name = "RSS (Receive Side Scaling) Desativado",
            Description = "Distribui processamento de rede entre múltiplas CPUs. Desativar causa gargalos de CPU em conexões de rede de alta velocidade.",
            Category = "Rede e Conectividade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", ValueName = "EnableRSS", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "LLMNR Ativado (Envenenamento de Nome na Rede Local)",
            Description = "O LLMNR resolve nomes na rede local por multicast, sem autenticação: qualquer máquina da mesma rede pode responder no lugar do destino e capturar hashes NTLM (ataque com Responder). O Windows 11 mais novo já vem com ele desligado; esta política (DNS Client > Turn off multicast name resolution) é o desligamento explícito.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", ValueName = "EnableMulticast", HarmfulValue = 1, DefaultValue = 0
        },
                 // ==================================================================================
        // 6. ATUALIZAÇÕES E LOJA
        // ==================================================================================
        new() {
            Name = "Windows Update",
            Description = "Mantém o sistema seguro e atualizado. Desativar permanentemente impede o recebimento de correções críticas.",
            Category = "Atualizações e Loja", Type = TweakType.Service, ServiceName = "wuauserv", HarmfulStartMode = "Disabled", DefaultStartMode = "Demand"
        },
        new() {
            Name = "Serviço de Instalação da Loja",
            Description = "Necessário para baixar e atualizar aplicativos da Microsoft Store (incluindo apps como Calculadora e Fotos).",
            Category = "Atualizações e Loja", Type = TweakType.Service, ServiceName = "InstallService", HarmfulStartMode = "Disabled", DefaultStartMode = "Demand"
        },
        new() {
            Name = "Otimização de Entrega (DoSvc)",
            Description = "Ajuda a baixar atualizações mais rápido. Desativar completamente pode causar falhas no Windows Update.",
            Category = "Atualizações e Loja", Type = TweakType.Service, ServiceName = "DoSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Delayed-Auto"
        },
        new() {
            Name = "Bloqueio de Drivers (Busca)",
            Description = "Configuração que impede o Windows de buscar drivers automaticamente para novos dispositivos conectados.",
            Category = "Atualizações e Loja", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", ValueName = "SearchOrderConfig", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Update Medic Service",
            Description = "Serviço de proteção do Windows Update que repara componentes corrompidos. Desativar impede que o Windows Update seja recuperado automaticamente.",
            Category = "Atualizações e Loja", Type = TweakType.Service, ServiceName = "WaaSMedicSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Windows Update Orchestrator",
            Description = "Orquestra o download e instalação de atualizações. Desativar pode causar falhas misteriosas no Windows Update.",
            Category = "Atualizações e Loja", Type = TweakType.Service, ServiceName = "UsoSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Microsoft Store (Serviço de Suporte)",
            Description = "Fornece suporte para aplicativos da Microsoft Store. Desativar impede que apps da Store sejam atualizados ou iniciados.",
            Category = "Atualizações e Loja", Type = TweakType.Service, ServiceName = "PushToInstall", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Windows Update (Metered Connection)",
            Description = "Configura conexão como limitada para evitar downloads de atualizações. Pode impedir atualizações críticas por semanas ou meses.",
            Category = "Atualizações e Loja", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\DefaultMediaCost", ValueName = "3G", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Atualizações de Produtos Microsoft (Desativado)",
            Description = "Impede que Office, Visual Studio e outros produtos Microsoft recebam atualizações pelo Windows Update.",
            Category = "Atualizações e Loja", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\WindowsUpdate\UpdatePolicy\PolicyState", ValueName = "DeferFeatureUpdates", HarmfulValue = 1, DefaultValue = 0
        },
                 // ==================================================================================
        // 7. ESTABILIDADE E HARDWARE
        // ==================================================================================
        new() {
            Name = "Arquivo de Paginação (Page File)",
            Description = "Memória virtual no disco. Desativar pode causar fechamento repentino de jogos e erros de 'Memória Insuficiente'.",
            Category = "Estabilidade", Type = TweakType.PageFile
        },
        new() {
            Name = "LargeSystemCache (Cache de Sistema Grande)",
            Description = "Configuração que força o Windows a manter um cache de sistema muito grande. CAUSA MEMORY LEAKS frequentes e consumo excessivo de RAM, degradando performance ao longo do tempo.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "LargeSystemCache", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Horário do Windows (Time)",
            Description = "Mantém a data e hora sincronizadas. Hora errada causa erros de certificado na internet e problemas em jogos.",
            Category = "Estabilidade", Type = TweakType.Service, ServiceName = "W32Time", HarmfulStartMode = "Disabled", DefaultStartMode = "Demand"
        },
        new() {
            Name = "Dynamic Tick (BCD)",
            Description = "Recurso de gerenciamento de energia da CPU. Desativá-lo (Yes) é um mito de performance antigo que não traz benefícios reais.",
            Category = "Estabilidade", Type = TweakType.Bcd, ValueName = "disabledynamictick", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "Limite de Tempo de Desligamento de Serviço",
            Description = "Tempo muito curto para serviços desligarem. Causa 'O Windows está fechando...' infinito e perda de dados não salvos durante desligamento.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", ValueName = "WaitToKillServiceTimeout", HarmfulValue = "1000", DefaultValue = "5000"
        },
        new() {
            Name = "NTFS Memory Usage (Limite Baixo)",
            Description = "Limita a memória que o NTFS pode usar para cache. Causa lentidão em operações de arquivo, especialmente em sistemas com muitos arquivos pequenos.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", ValueName = "NtfsMemoryUsage", HarmfulValue = 1, DefaultValue = 2
        },
                 // ==================================================================================
        // 8. PRIVACIDADE GLOBAL
        // ==================================================================================
        new() {
            Name = "Aceleração de Mouse",
            Description = "Verifica se a aceleração do ponteiro está desativada (comum em otimizações gamer) ou no padrão do Windows.",
            Category = "Acessibilidade", Type = TweakType.Mouse
        },
                 // ==================================================================================
        // 9. SAÚDE DO DISCO E ARQUIVOS
        // ==================================================================================
        new() {
            Name = "TRIM de SSD (Desativado)",
            Description = "Comando TRIM mantém SSDs rápidos ao limpar blocos não usados. Desativar causa degradação severa de performance em SSDs ao longo do tempo.",
            Category = "Saúde do Disco", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", ValueName = "DisableDeleteNotify", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Desfragmentação Automática (Desativada)",
            Description = "A desfragmentação automática mantém HDDs organizados. Desativar causa fragmentação severa e lentidão progressiva em discos mecânicos.",
            Category = "Saúde do Disco", Type = TweakType.Service, ServiceName = "defragsvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Volume Shadow Copy (Serviço Desativado)",
            Description = "Serviço de cópias de sombra (Volume Shadow Copy) desativado. Impede criação de pontos de restauração e backups do Windows.",
            Category = "Saúde do Disco", Type = TweakType.Service, ServiceName = "VSS", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Microsoft Software Shadow Copy Provider",
            Description = "Provedor de cópias de sombra desativado. Impede que aplicativos de backup criem snapshots consistentes do disco.",
            Category = "Saúde do Disco", Type = TweakType.Service, ServiceName = "swprv", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Arquivos Offline (Client Side Caching Desativado)",
            Description = "Gerencia arquivos offline para pastas de rede. Desativar impede acesso a arquivos de rede quando desconectado.",
            Category = "Saúde do Disco", Type = TweakType.Service, ServiceName = "CscService", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Serviço de Indexação de Busca (WSearch) Desativado",
            Description = "O WSearch mantém o índice de busca do menu Iniciar, do Explorador e da pesquisa de arquivos. Desativado, a busca fica sem resultados (e o próprio Windows pode reativá-lo ao usar o campo de busca).",
            Category = "Saúde do Disco", Type = TweakType.Service, ServiceName = "WSearch", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Prefetch do Windows (Desativado)",
            Description = "Prefetch acelera o carregamento de aplicativos. Desativar aumenta o tempo de abertura de programas em 30-50%.",
            Category = "Saúde do Disco", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", ValueName = "EnablePrefetcher", HarmfulValue = 0, DefaultValue = 3
        },
        new() {
            Name = "Superfetch/SysMain (Desativado para SSD)",
            Description = "SysMain (Superfetch) gerencia cache de aplicativos. Desativar não beneficia SSDs e pode na verdade aumentar o tempo de carregamento de apps.",
            Category = "Saúde do Disco", Type = TweakType.Service, ServiceName = "SysMain", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        // Removido: ReadyBoost é funcionalidade do SysMain, já verificado acima
                 // ==================================================================================
        // 10. CORRUPÇÃO DE SISTEMA E COMPONENTES
        // ==================================================================================
        new() {
            Name = "Windows Modules Installer (Serviço Desativado)",
            Description = "Serviço que instala, modifica e repara componentes do Windows. Desativar impede correção de componentes corrompidos do sistema.",
            Category = "Corrupção de Sistema", Type = TweakType.Service, ServiceName = "TrustedInstaller", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Instrumentação de Gerenciamento do Windows (WMI) Desativada",
            Description = "O Winmgmt hospeda o WMI, usado por diagnóstico, monitoramento, inventário e por vários instaladores. Desativado, ferramentas que consultam WMI (incluindo packages de app e scripts de suporte) falham.",
            Category = "Corrupção de Sistema", Type = TweakType.Service, ServiceName = "Winmgmt", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        // REMOVIDO (06/11): check "Restauração do Sistema (Frequência de Pontos Alterada)"
        // era falso positivo. Segundo a doc oficial da Microsoft (SRSetRestorePoint), a
        // chave SystemRestorePointCreationFrequency NAO existe por padrao (ausente =
        // 1440 min = 1 ponto/dia) e valor 0 significa "nao pular a criacao" = MAIS pontos
        // de restauracao, nao menos — nenhum valor dessa chave enfraquece o sistema.
        // O item vivia MODIFICADO porque um .bat de tweak externo (AncelsPerformanceBatch /
        // EXMPremiumTweaker, em Downloads) gravou 0 uma vez; nada no boot reescreve isso
        // (verificado: Run/RunOnce/tarefas agendadas/WMI/pasta Startup/TweakRegistry).
        // O check legitimo de restauracao do sistema e' o DisableSR logo abaixo.
        //
        // ==================================================================================
        // 11. PERFORMANCE DEGRADADA COM O TEMPO
        // ==================================================================================
        new() {
            Name = "Cache de Identificação de Processo (Handle Leak)",
            Description = "Vazamento de handles de processo. Com o tempo, o sistema fica incapaz de abrir novos programas ou arquivos devido à exaustão de handles.",
            Category = "Performance Degradada", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", ValueName = "GDIProcessHandleQuota", HarmfulValue = 1000, DefaultValue = 10000
        },
        new() {
            Name = "Limite de USER Handles (Baixo)",
            Description = "Limite baixo de USER handles. Causa falhas ao abrir múltiplas janelas, menus que não aparecem e botões que não respondem.",
            Category = "Performance Degradada", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", ValueName = "USERProcessHandleQuota", HarmfulValue = 1000, DefaultValue = 10000
        },
        new() {
            Name = "Driver de Exibição Com Falha (Timeout Detection Recovery)",
            Description = "TDR (Timeout Detection Recovery) com parâmetros inadequados. Causa tela preta por segundos, falhas em jogos e o erro clássico 'Display driver stopped responding'.",
            Category = "Performance Degradada", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", ValueName = "TdrLevel", HarmfulValue = 0, DefaultValue = 3
        },
        new() {
            Name = "Serviço de Diagnóstico de Inicialização",
            Description = "Serviço que diagnostica problemas de inicialização. Desativar impede que o Windows identifique e corrija causas de boot lento.",
            Category = "Performance Degradada", Type = TweakType.Service, ServiceName = "DiagTrack", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
                 // ==================================================================================
        // 12. INÍCIO E BOOT (PROBLEMAS DE INICIALIZAÇÃO)
        // ==================================================================================
        new() {
            Name = "Falhas de Boot Ignoradas (bootstatuspolicy)",
            Description = "Com 'ignoreallfailures' o Windows esconde falhas de inicialização e segue adiante, o que mascara um boot quebrado (e o Recovery não aparece quando você precisa). O padrão é 'displayallfailures'.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "bootstatuspolicy", HarmfulValue = "ignoreallfailures", DefaultValue = "displayallfailures"
        },
        new() {
            Name = "Inicialização Rápida (Fast Startup) Desativada para HD",
            Description = "Fast Startup reduz tempo de boot usando hibernação híbrida. Desativar em HDDs aumenta o tempo de inicialização de 10 segundos para 1-2 minutos.",
            Category = "Boot e Inicialização", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", ValueName = "HiberbootEnabled", HarmfulValue = 0, DefaultValue = 1, IsOptional = true
        },
        new() {
            Name = "Modo de Depuração de Boot (Ativado)",
            Description = "Modo de depuração de boot ativado. Causa inicialização extremamente lenta pois o Windows carrega símbolos de depuração e logs extensivos.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "debug", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "Modo de Boot com Log (Ativado)",
            Description = "Registro detalhado de boot ativado. Gera logs enormes de inicialização que consomem tempo de boot e espaço em disco no arquivo ntbtlog.txt.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "bootlog", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "Boot com Video Padrão (VGA Mode)",
            Description = "Modo de vídeo VGA forçado na inicialização. Causa resolução baixa em todo o boot e pode impedir que drivers de vídeo carreguem corretamente.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "vga", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "Solicitação de Boot SOS (Ativada)",
            Description = "Modo SOS exibe cada driver carregado durante o boot. Causa inicialização extremamente lenta pois cada driver é listado individualmente na tela.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "sos", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "Boot com PCI Configuration (Forçado)",
            Description = "Força reconfiguração de barramento PCI a cada boot. Causa atraso significativo na inicialização enquanto o sistema reenumera todos os dispositivos PCI.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "pci", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "Advanced Configuration and Power Interface (ACPI) Desativado",
            Description = "ACPI desativado durante boot. Causa problemas de gerenciamento de energia e pode impedir que o sistema detecte hardware corretamente.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "acpi", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "Boot com Force Failure (Simulação de Falha)",
            Description = "Configuração de BCD que simula falhas de boot para teste. Ativada acidentalmente, causa telas azuis a cada 5 minutos de uso.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "bootems", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "Boot Menu Legacy (Ativado)",
            Description = "Menu de boot legado ativado para sistemas UEFI modernos. Causa delay extra desnecessário de 30 segundos a cada inicialização.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "displaybootmenu", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "Recovery Enabled (Tela de Recuperação no Boot)",
            Description = "Opção de recuperação automática ativada incorretamente. Causa tela 'Seu PC precisa ser reparado' a cada boot mesmo sem problemas reais.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "recoveryenabled", HarmfulValue = "No", DefaultValue = "Yes"
        },
        new() {
            Name = "Boot sem WinPE (Windows Preinstallation Environment)",
            Description = "Configuração de BCD para boot como WinPE mesmo em sistema completo. Causa falta de drivers, limitações de recursos e funcionalidades quebradas.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "winpe", HarmfulValue = "Yes", DefaultValue = "No"
        },
                 // ==================================================================================
        // 13. REDE E CONECTIVIDADE (PROBLEMAS ACUMULADOS)
        // ==================================================================================
        new() {
            Name = "Windows Filtering Platform (BFE) Desativada",
            Description = "Plataforma de filtragem do Windows corrompida. Causa falhas em firewalls de terceiros, VPNs que não conectam e política de segurança quebrada.",
            Category = "Rede (Problemas)", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\BFE", ValueName = "Start", HarmfulValue = 4, DefaultValue = 2
        },
        new() {
            Name = "Network Location Awareness (NLA) Desativado",
            Description = "O NLA identifica a rede conectada para aplicar o perfil certo (domínio/privado/público). Desativado, o Windows cai em perfil Público: firewall mais restritivo, descoberta e compartilhamento de arquivos param de funcionar.",
            Category = "Rede (Problemas)", Type = TweakType.Service, ServiceName = "NlaSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "IP Helper (Serviço de Tradução de Endereços IPv6)",
            Description = "Serviço de auxílio IP desativado. Causa falhas em conectividade IPv6, Teredo e DirectAccess, afetando jogos Xbox e Remote Desktop.",
            Category = "Rede (Problemas)", Type = TweakType.Service, ServiceName = "iphlpsvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
                 // ==================================================================================
        // 14. DRIVER E HARDWARE (PROBLEMAS)
        // ==================================================================================
        new() {
            Name = "Driver Signature Enforcement (Desativado via BCD)",
            Description = "Exigência de assinatura de driver desativada. Permite que drivers não assinados sejam carregados, causando telas azuis e instabilidade do sistema.",
            Category = "Driver e Hardware", Type = TweakType.Bcd, ValueName = "disableintegritychecks", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "Driver Rollback (Proteção de Reversão Desativada)",
            Description = "Impede reversão de drivers para versões anteriores. Se um novo driver causa problemas, não é possível voltar para a versão estável anterior.",
            Category = "Driver e Hardware", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall", ValueName = "DisableDriverRollback", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Device Installation Restrictions (Restrições de Hardware)",
            Description = "Restrições que impedem instalação de novos dispositivos. Impede que qualquer hardware novo (mouse, teclado, impressora) seja instalado.",
            Category = "Driver e Hardware", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\DeviceInstall\Restrictions", ValueName = "DenyDeviceIDs", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Serviço de Informações de Aplicativos (Aplicativos Elevados)",
            Description = "Serviço que permite execução de aplicativos com privilégios administrativos. Desativar impede que muitos instaladores sejam executados corretamente.",
            Category = "Driver e Hardware", Type = TweakType.Service, ServiceName = "AppInfo", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Windows Driver Foundation (WDF) Desativado",
            Description = "Windows Driver Foundation gerencia drivers modo usuário. Desativar causa falhas em drivers de impressora, scanner e câmera.",
            Category = "Driver e Hardware", Type = TweakType.Service, ServiceName = "wudfsvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Device Setup Manager (Gerenciador de Configuração Desativado)",
            Description = "Gerencia configuração inicial de novos dispositivos. Desativar impede que novos hardwares sejam configurados corretamente ao serem conectados.",
            Category = "Driver e Hardware", Type = TweakType.Service, ServiceName = "DsmSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
                 // ==================================================================================
        // 15. VARIÁVEIS DE AMBIENTE E PATH
        // ==================================================================================
        new() {
            Name = "PATH do Sistema Incompleto",
            Description = "Variável PATH do sistema sem caminhos essenciais do Windows. Causa 'command not found' para ferramentas do sistema.",
            Category = "Variáveis de Ambiente", Type = TweakType.Registry, IsOptional = false,
            KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", ValueName = "Path", HarmfulValue = "INCOMPLETE", DefaultValue = null
        },
        new() {
            Name = "PATH do Usuário Incompleto",
            Description = "PATH do usuário sem caminhos de ferramentas de desenvolvimento instaladas. Causa falhas ao executar dotnet, git, node, etc.",
            Category = "Variáveis de Ambiente", Type = TweakType.Registry, IsOptional = true,
            KeyPath = @"HKEY_CURRENT_USER\Environment", ValueName = "Path", HarmfulValue = "INCOMPLETE", DefaultValue = null
        },
        new() {
            Name = "PATH com Entradas Duplicadas",
            Description = "Múltiplas entradas idênticas no PATH. Causa lentidão na busca de executáveis e comportamento imprevisível de qual programa será executado.",
            Category = "Variáveis de Ambiente", Type = TweakType.Registry, IsOptional = true,
            KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", ValueName = "Path", HarmfulValue = "DUPLICATE", DefaultValue = null
        },
        new() {
            Name = "PATH com Caminhos Inexistentes",
            Description = "Entradas no PATH que apontam para pastas que não existem. Causa erros 'file not found' e lentidão no sistema.",
            Category = "Variáveis de Ambiente", Type = TweakType.Registry, IsOptional = false,
            KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", ValueName = "Path", HarmfulValue = "MISSING", DefaultValue = null
        },
        new() {
            Name = "PATH com Lixo de Desenvolvimento",
            Description = "Caminhos de SDKs internos e ferramentas de build no PATH. Causa poluição e lentidão na busca de executáveis.",
            Category = "Variáveis de Ambiente", Type = TweakType.Registry, IsOptional = true,
            KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", ValueName = "Path", HarmfulValue = "DEV_JUNK", DefaultValue = null
        },
        new() {
            Name = "PATH Vulnerável a Hijacking",
            Description = "PATH com ordem incorreta que permite hijacking de executáveis. Vulnerabilidade crítica de segurança que permite execução de malware.",
            Category = "Variáveis de Ambiente", Type = TweakType.Registry, IsOptional = false,
            KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", ValueName = "Path", HarmfulValue = "VULNERABLE", DefaultValue = null
        },
        new() {
            Name = "TEMP/TMP (Variável de Ambiente Inválida)",
            Description = "Variável TEMP/TMP apontando para diretório inválido ou inexistente. Causa falhas ao instalar programas e erros 'Cannot create temporary file'.",
            Category = "Perfil de Usuário", KeyPath = @"HKEY_CURRENT_USER\Environment", ValueName = "TEMP", HarmfulValue = "", DefaultValue = @"%USERPROFILE%\AppData\Local\Temp"
        },

        // 16. PERFIL DE USUÁRIO
        // ==================================================================================
        new() {
            Name = "Pasta de Usuário Redirecionada Incorretamente",
            Description = "Redirecionamento de pastas do usuário (Documentos, Downloads) para caminhos inválidos. Causa perda de dados e erro 'Cannot access this folder'.",
            Category = "Perfil de Usuário", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", ValueName = "Personal", HarmfulValue = "", DefaultValue = @"%USERPROFILE%\Documents"
        },
        new() {
            Name = "Pasta Downloads Redirecionada (Incorreta)",
            Description = "Pasta Downloads redirecionada para local inválido ou inacessível. Downloads falham com erro 'Cannot download to this location'.",
            Category = "Perfil de Usuário", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", ValueName = "{374DE290-123F-4565-9164-39C4925E467B}", HarmfulValue = "", DefaultValue = @"%USERPROFILE%\Downloads"
        },
        new() {
            Name = "Variável de Ambiente PATH Corrompida",
            Description = "Variável PATH com entradas inválidas ou mal formatadas. Causa erro 'X is not recognized as an internal or external command' mesmo com programas instalados.",
            Category = "Perfil de Usuário", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", ValueName = "Path", HarmfulValue = "CORRUPTED", DefaultValue = @"%SystemRoot%\system32;%SystemRoot%;%SystemRoot%\System32\Wbem;%SYSTEMROOT%\System32\WindowsPowerShell\v1.0\"
        },
        new() {
            Name = "Aliases do PowerShell Quebrados (Comandos Essenciais)",
            Description = "Aliases no perfil do PowerShell apontando para executáveis inexistentes (ex: winget, dotnet). Causa erro 'X is not recognized' mesmo com programas instalados.",
            Category = "Variáveis de Ambiente", Type = TweakType.Registry, IsOptional = false,
            KeyPath = @"HKEY_CURRENT_USER\Environment", ValueName = "PowerShellAliases", HarmfulValue = null, DefaultValue = null
        },
        new() {
            Name = "AppData Roaming (Caminho Corrompido)",
            Description = "Caminho AppData Roaming corrompido. Aplicativos perdem configurações de usuário, temas, favoritos e senhas salvas.",
            Category = "Perfil de Usuário", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", ValueName = "AppData", HarmfulValue = "", DefaultValue = @"%USERPROFILE%\AppData\Roaming"
        },
        new() {
            Name = "Perfil Temporário (Temp Profile) Carregado",
            Description = "Windows carregou um perfil temporário. Alterações feitas durante esta sessão serão perdidas ao deslogar. Forte indicador de corrupção de perfil.",
            Category = "Perfil de Usuário", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", ValueName = "State", HarmfulValue = 1, DefaultValue = 0
        },

        // ==================================================================================
        // 16b. REGISTRO E CONFIGURAÇÃO CORROMPIDA
        // ==================================================================================
                 // ==================================================================================
        // 17. EVENTOS E DIAGNÓSTICO (PROBLEMAS)
        // ==================================================================================
        new() {
            Name = "Depuração do Windows (Crash Dump Desativado)",
            Description = "Criação de crash dump desativada. Quando o sistema trava (BSOD), nenhum dump é gerado, impossibilitando diagnóstico da causa da tela azul.",
            Category = "Eventos e Diagnóstico", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CrashControl", ValueName = "CrashDumpEnabled", HarmfulValue = 0, DefaultValue = 7
        },
        new() {
            Name = "Reinicialização Automática em Falha (Desativada)",
            Description = "Reinicialização automática em caso de BSOD desativada. O sistema fica travado na tela azul indefinidamente até ser reiniciado manualmente.",
            Category = "Eventos e Diagnóstico", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CrashControl", ValueName = "AutoReboot", HarmfulValue = 0, DefaultValue = 1
        },
                 // ==================================================================================
        // 18. SEGURANÇA CRÍTICA (configurações de proteção do sistema)
        // ==================================================================================
        new() {
            Name = "Virtualização Baseada em Segurança (VBS)",
            Description = "VBS pode reduzir performance em jogos. Se desativado, recomenda-se ter um antivírus ativo.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard", ValueName = "EnableVirtualizationBasedSecurity", HarmfulValue = 0, DefaultValue = 1, IsOptional = true
        },
                 // ==================================================================================
        // 19. SEGURANÇA ADICIONAL (configurações de proteção do sistema)
        // ==================================================================================
        new() {
            Name = "Restrição de Tráfego NTLM (Mitigação de vazamento de hash)",
            Description = "Restringe envio de tráfego NTLM para prevenir vazamento de hashes de autenticação. Ataques pass-the-hash podem comprometer credenciais de domínio se configurado incorretamente.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa\MSV1_0", ValueName = "RestrictSendingNTLMTraffic", HarmfulValue = 0, DefaultValue = 2
        },
                 // ==================================================================================
        // 20. PROBLEMAS DO WINDOWS EXPLORER
        // ==================================================================================
        new() {
            Name = "Janela de Propriedades Não Abre",
            Description = "O menu 'Propriedades' não aparece ao clicar com botão direito > Propriedades (ou Alt+Enter). Política NoPropertiesMyComputer pode estar ativa ou chave de registro corrompida.",
            Category = "Restrições do Sistema",
            Type = TweakType.Registry,
            KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer",
            ValueName = "NoPropertiesMyComputer",
            HarmfulValue = 1,
            DefaultValue = 0
        },
        new() {
            Name = "Menu de Contexto do Explorer Desabilitado (Política)",
            Description = "NoViewContextMenu=1 desativa completamente o menu de contexto do botão direito no Explorer. Nenhum item de menu aparece, travando produtividade.",
            Category = "Restrições do Sistema",
            Type = TweakType.Registry,
            KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer",
            ValueName = "NoViewContextMenu",
            HarmfulValue = 1,
            DefaultValue = 0
        },
                 // ==================================================================================
        // 21. SERVIÇOS DE REDE CRÍTICOS ADICIONAIS
        // ==================================================================================
        new() {
            Name = "Serviço de Compartilhamento de Rede (LanmanServer Desativado)",
            Description = "Serviço de compartilhamento de servidor desativado. Impede que pastas compartilhadas na rede sejam acessadas por outros computadores.",
            Category = "Rede (Problemas)", Type = TweakType.Service, ServiceName = "LanmanServer", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Serviço de Estação de Trabalho (LanmanWorkstation Desativado)",
            Description = "Serviço de estação de trabalho desativado. Impede acesso a pastas compartilhadas e recursos de rede como impressoras.",
            Category = "Rede (Problemas)", Type = TweakType.Service, ServiceName = "LanmanWorkstation", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Remote Access Connection Manager",
            Description = "Gerenciador de conexão de acesso remoto desativado. Causa falhas em conexões VPN e discagem direta.",
            Category = "Rede (Problemas)", Type = TweakType.Service, ServiceName = "RasMan", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Remote Access Auto Connection Manager",
            Description = "Gerenciador automático de conexão remota desativado. Conexões VPN não são estabelecidas automaticamente quando necessário.",
            Category = "Rede (Problemas)", Type = TweakType.Service, ServiceName = "RasAuto", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Secure Socket Tunneling Protocol (SSTP) Service",
            Description = "Serviço SSTP para VPNs desativado. Causa falhas em conexões VPN que usam protocolo SSTP.",
            Category = "Rede (Problemas)", Type = TweakType.Service, ServiceName = "SstpSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Network Connectivity Assistant",
            Description = "Assistente de conectividade de rede desativado. Causa falta de notificações de rede, ícone de rede incorreto e troubleshooting automático desabilitado.",
            Category = "Rede (Problemas)", Type = TweakType.Service, ServiceName = "NcaSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Diagnostic Policy Service Política de Diagnóstico Corrompida",
            Description = "Serviço de política de diagnóstico corrompido. Causa impossibilidade de executar diagnósticos de rede e sistema automaticamente.",
            Category = "Rede (Problemas)", Type = TweakType.Service, ServiceName = "DPS", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Diagnostic Service Host",
            Description = "Host de serviço de diagnóstico desativado. Causa falhas no solucionador de problemas do Windows, que não consegue diagnosticar e corrigir problemas.",
            Category = "Rede (Problemas)", Type = TweakType.Service, ServiceName = "WdiServiceHost", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Diagnostic System Host",
            Description = "Host de sistema de diagnóstico desativado. Causa falhas no diagnóstico de hardware e sistema operacional via solucionador de problemas.",
            Category = "Rede (Problemas)", Type = TweakType.Service, ServiceName = "WdiSystemHost", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },

        // ==================================================================================
        // 22. PROBLEMAS DE ENERGIA E DISPOSITIVOS MÓVEIS
        // ==================================================================================
                 // ==================================================================================
        // 21. PROBLEMAS DE MEMÓRIA E CACHE
        // ==================================================================================
         // ==================================================================================
        // 22. SEGURANÇA AVANÇADA DE REDE E FIREWALL
        // ==================================================================================
        new() {
            Name = "IPsec Policy Agent (Desativado)",
            Description = "Agente de política IPsec desativado. Impede que políticas de segurança de rede IPsec sejam aplicadas, expondo o tráfego de rede.",
            Category = "Segurança de Rede", Type = TweakType.Service, ServiceName = "PolicyAgent", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "ICMP Redirect (Redirecionamento ICMP Aceito)",
            Description = "Aceitar redirecionamentos ICMP. Atacantes na rede local podem redirecionar tráfego para servidores maliciosos através de falsos redirecionamentos ICMP.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", ValueName = "EnableICMPRedirect", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Source Routing (Roteamento por Origem Aceito)",
            Description = "Roteamento por origem IP aceito. Permite que atacantes especifiquem a rota que os pacotes devem seguir, contornando firewalls e roteadores.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", ValueName = "DisableIPSourceRouting", HarmfulValue = 0, DefaultValue = 2
        },
        new() {
            Name = "Dead Gateway Detection (Detecção de Gateway Morto Desativada)",
            Description = "Detecção de gateway offline desativada. Quando o roteador principal cai, o Windows não tenta automaticamente um gateway alternativo.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", ValueName = "EnableDeadGWDetect", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Firewall regras de Bloqueio de Entrada Desativadas",
            Description = "Todas as regras de bloqueio de entrada do firewall desativadas. Qualquer serviço no computador fica acessível da internet sem proteção.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile", ValueName = "EnableFirewall", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Firewall Default Inbound Action (Ação Padrão Permitir)",
            Description = "Ação padrão do firewall para conexões de entrada é 'Permitir'. Qualquer conexão não solicitada da internet é aceita por padrão.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile", ValueName = "DefaultInboundAction", HarmfulValue = 0, DefaultValue = 1
        },
                 // ==================================================================================
        // 23. PROBLEMAS DE BACKUP E RESTAURAÇÃO
        // ==================================================================================
        new() {
            Name = "Restauração do Sistema (Proteção Desativada)",
            Description = "Proteção do sistema desativada. Nenhum ponto de restauração é criado, impossibilitando reverter o sistema para um estado anterior funcional.",
            Category = "Backup e Restauração", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", ValueName = "DisableSR", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "File History (Histórico de Arquivos Desativado)",
            Description = "Histórico de arquivos desativado. Versões anteriores de documentos não são salvas, impossibilitando recuperação de arquivos sobrescritos ou deletados.",
            Category = "Backup e Restauração", Type = TweakType.Service, ServiceName = "fhsvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Windows Backup (Backup do Windows Desativado)",
            Description = "Serviço de backup do Windows desativado. Backups agendados de arquivos e imagem do sistema não são executados.",
            Category = "Backup e Restauração", Type = TweakType.Service, ServiceName = "SDRSVC", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        // Removido: era duplicata do check "Volume Shadow Copy" e usava a chave errada (NtfsDisableLastAccessUpdate)
                 // ==================================================================================
        // 24. PROBLEMAS DE WINDOWS DEFENDER E SEGURANÇA ADICIONAIS
        // ==================================================================================
        new() {
            Name = "Windows Defender Scheduled Scan (Varredura Agendada Desativada)",
            Description = "Varredura antivírus agendada desativada. O sistema não é verificado periodicamente em busca de malware em segundo plano.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Scan", ValueName = "DisableScheduledScanning", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Windows Defender Archive Scan (Varredura de Arquivos Compactados Desativada)",
            Description = "Varredura de arquivos ZIP, RAR e outros compactados desativada. Malwares escondidos em arquivos compactados não são detectados.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Scan", ValueName = "DisableArchiveScanning", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Windows Defender Removable Drive Scan (Varredura de Mídia Removível Desativada)",
            Description = "Varredura de unidades removíveis (USB, SD) desativada. Malwares em pendrives não são detectados ao conectar o dispositivo.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Scan", ValueName = "DisableRemovableDriveScanning", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Windows Defender Email Scanning (Varredura de Email Desativada)",
            Description = "Varredura de anexos de email do Windows Defender desativada. Malwares enviados por email não são detectados automaticamente.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Scan", ValueName = "DisableEmailScanning", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Windows Defender Behavior Monitoring (Monitoramento Comportamental Desativado)",
            Description = "Monitoramento de comportamento suspeito desativado. Malwares que se comportam de forma anormal não são detectados pelo Defender.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Real-Time Protection", ValueName = "DisableBehaviorMonitoring", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Windows Defender IOAV (Scan de Downloads da Internet Desativado)",
            Description = "Scan de downloads da Internet desativado. Arquivos baixados via Internet Explorer/Edge não são verificados automaticamente.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Real-Time Protection", ValueName = "DisableIOAVProtection", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Windows Defender On Access Protection (Proteção ao Acesso Desativada)",
            Description = "Proteção em tempo real ao acessar arquivos desativada. Malwares não são detectados quando arquivos são abertos ou executados.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Real-Time Protection", ValueName = "DisableOnAccessProtection", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Windows Defender Network Protection (Proteção de Rede em Modo Apenas Auditoria)",
            Description = "Proteção de rede do Defender configurada apenas para auditoria. Conexões a IPs maliciosos são registradas mas não bloqueadas.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Features", ValueName = "NetworkProtectionMode", HarmfulValue = 2, DefaultValue = 1
        },
                 // ==================================================================================
        // 25. PROBLEMAS DE INICIALIZAÇÃO DE APLICATIVOS
        // ==================================================================================
        new() {
            Name = "Shell do Windows (Explorer.exe) Vazio",
            Description = "Shell do Windows substituído por programa diferente no registro. Pode ser malware tentando sequestrar a interface do Windows ou configuração incorreta.",
            Category = "Inicialização", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", ValueName = "Shell", HarmfulValue = "", DefaultValue = "explorer.exe"
        },
        new() {
            Name = "LoadAppInit_DLLs (Carregamento de DLLs via AppInit)",
            Description = "Permite que DLLs AppInit sejam carregadas em todos os processos. Causa lentidão generalizada e instabilidade em aplicativos.",
            Category = "Inicialização", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", ValueName = "LoadAppInit_DLLs", HarmfulValue = 1, DefaultValue = 0
        },
                 // ==================================================================================
        // 26. PROBLEMAS DE COMPATIBILIDADE E APLICATIVOS
        // ==================================================================================
        new() {
            Name = "Application Identity Service (AppIDSvc) Desativado",
            Description = "Serviço de identidade de aplicativo desativado. AppLocker e políticas de controle de aplicativo não funcionam corretamente.",
            Category = "Compatibilidade", Type = TweakType.Service, ServiceName = "AppIDSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Virtual Disk Service (VDS) Desativado",
            Description = "Serviço de disco virtual desativado. Gerenciamento de discos (mount, unmount, RAID) via software falha, impedindo montagem de ISOs e VHDs.",
            Category = "Compatibilidade", Type = TweakType.Service, ServiceName = "vds", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Windows Media Player Network Sharing (Desativado)",
            Description = "Compartilhamento de mídia em rede desativado. Impede que outros dispositivos na rede (Xbox, Smart TV) acessem sua biblioteca de mídia.",
            Category = "Compatibilidade", Type = TweakType.Service, ServiceName = "WMPNetworkSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Portable Device Enumerator (WPD) Desativado",
            Description = "Serviço de enumerador de dispositivos portáteis desativado. Celulares, tablets e câmeras não são reconhecidos quando conectados via USB.",
            Category = "Compatibilidade", Type = TweakType.Service, ServiceName = "WPDBusEnum", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Windows Biometric Service (WbioSrvc) Desativado",
            Description = "Serviço biométrico desativado. Leitores de impressão digital, reconhecimento facial (Windows Hello) e outros sensores biométricos não funcionam.",
            Category = "Compatibilidade", Type = TweakType.Service, ServiceName = "WbioSrvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Windows Sensor Service (SensorService) Desativado",
            Description = "Serviço de sensores desativado. Sensores de luz ambiente, proximidade, acelerômetro e outros não funcionam, afetando ajuste automático de brilho e rotação.",
            Category = "Compatibilidade", Type = TweakType.Service, ServiceName = "SensorService", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Windows Push Notifications (WPN) Desativado",
            Description = "Serviço de notificações push desativado. Aplicativos não recebem notificações em tempo real (mensagens, emails, lembretes).",
            Category = "Compatibilidade", Type = TweakType.Service, ServiceName = "WpnService", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Windows Push Notifications User Service (Desativado)",
            Description = "Serviço de notificações push do usuário desativado. Notificações de aplicativos da Microsoft Store não funcionam.",
            Category = "Compatibilidade", Type = TweakType.Service, ServiceName = "WpnUserService", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Sync Host (Host de Sincronização Desativado)",
            Description = "Host de sincronização desativado. Configurações do Windows, senhas e preferências não são sincronizadas entre dispositivos.",
            Category = "Compatibilidade", Type = TweakType.Service, ServiceName = "OneSyncSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
                 // ==================================================================================
        // 27. PROBLEMAS DE SEGURANÇA DE REDE AVANÇADOS
        // ==================================================================================
        new() {
            Name = "SMB Encryption (Criptografia SMB Desativada)",
            Description = "Criptografia SBM desativada. Dados transferidos em compartilhamentos de rede não são criptografados, permitindo interceptação por sniffers na rede.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", ValueName = "EncryptData", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "SMB Guest Fallback (Fallback para Convidado Habilitado)",
            Description = "Fallback para acesso SMB como convidado quando credenciais falham. Permite que atacantes acessem compartilhamentos sem autenticação válida.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", ValueName = "AllowGuestAccess", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Remote Desktop (RDP) Network Level Authentication Desativada",
            Description = "Autenticação de nível de rede (NLA) desativada para RDP. Permite que atacantes iniciem sessão RDP antes da autenticação, consumindo recursos e testando credenciais.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", ValueName = "UserAuthentication", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Remote Desktop (RDP) Drive Redirection",
            Description = "Redirecionamento de unidades no RDP ativado. Usuários remotos podem acessar unidades locais do servidor, potencialmente copiando dados confidenciais.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server\Wds\rdpwd", ValueName = "fDisableCdm", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "SMB1 (Desativado mas Protocolo Vulnerável Presente)",
            Description = "SMB1 desativado mas suporte ao protocolo ainda instalado. Drivers ou componentes do SMB1 ainda presentes podem ser explorados mesmo com SMB1 desativado.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\mrxsmb10", ValueName = "Start", HarmfulValue = 3, DefaultValue = 4
        },
        new() {
            Name = "SystemResponsiveness (Prioridade de Threads)",
            Description = "Evita micro-stuttering e engasgos de áudio/vídeo sob uso intenso de CPU. Algumas otimizações gamer definem este valor para 0, o que prejudica a alternância e a prioridade de threads no Windows.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", ValueName = "SystemResponsiveness", HarmfulValue = 0, DefaultValue = 20
        },

        // ==================================================================================
        // 28. DESEMPENHO GERAL DO PC
        // ==================================================================================
        new() {
            Name = "Prioridade de GPU para Jogos (NVIDIA/AMD)",
            Description = "O perfil de prioridade de GPU para jogos não está configurado. O valor recomendado é 8, garantindo que jogos e aplicativos 3D recebam prioridade máxima no agendador de GPU.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", ValueName = "GPU Priority", HarmfulValue = 0, DefaultValue = 8, IsOptional = true
        },
        new() {
            Name = "Prioridade de CPU para Jogos",
            Description = "A prioridade de CPU para jogos não está configurada. O valor recomendado é 6 (Alta), garantindo que processos de jogos recebam mais tempo de CPU que processos em segundo plano.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", ValueName = "Priority", HarmfulValue = 0, DefaultValue = 6, IsOptional = true
        },
        new() {
            Name = "Throttling de Rede em Aplicativos Multimídia Desativado",
            Description = "O Windows limita automaticamente o uso de rede durante reprodução de mídia para evitar buffering. Desativar permite máximo throughput de rede sem impacto na latência de áudio/vídeo.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", ValueName = "NetworkThrottlingIndex", HarmfulValue = 10, DefaultValue = 268435455, IsOptional = true
        },
        new() {
            Name = "Modo de Gerenciamento de Energia do Processador (Throttling de CPU)",
            Description = "O Windows pode aplicar throttling agressivo de CPU em estados de ociosidade, causando demora para o processador atingir a frequência máxima (até 500ms). Configurar corretamente elimina essa latência em rajadas de trabalho.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\893dee8e-2bef-41e0-89c6-b55d0929964c", ValueName = "ACSettingIndex", HarmfulValue = 0, DefaultValue = 100, IsOptional = true
        },
        new() {
            Name = "Serviço de Mapeamento de Porta (RPC/DCOM) Lento",
            Description = "O serviço de mapeamento de porta RPC pode apresentar lentidão na resolução de chamadas COM/DCOM, afetando aplicativos que dependem dessas tecnologias. Deve permanecer em execução automática.",
            Category = "Desempenho", Type = TweakType.Service, ServiceName = "RpcEptMapper", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Timeout de Aplicativo Travado (HungAppTimeout)",
            Description = "O timeout padrão de 5000ms antes de exibir 'Programa não está respondendo' é muito alto. Valores menores (ex: 2000ms) permitem detectar e fechar aplicativos travados mais rapidamente, evitando que o sistema fique inacessível.",
            Category = "Desempenho", KeyPath = @"HKEY_CURRENT_USER\Control Panel\Desktop", ValueName = "HungAppTimeout", HarmfulValue = "30000", DefaultValue = "5000", IsOptional = true
        },
        new() {
            Name = "WaitToKillAppTimeout (Tempo de Espera para Matar Aplicativo)",
            Description = "Tempo de espera para forçar encerramento de aplicativos no desligamento. O padrão de 20.000ms (20 segundos) torna o desligamento lento. Reduzir para 2.000-5.000ms acelera o desligamento sem perda de dados.",
            Category = "Desempenho", KeyPath = @"HKEY_CURRENT_USER\Control Panel\Desktop", ValueName = "WaitToKillAppTimeout", HarmfulValue = "20000", DefaultValue = "5000", IsOptional = true
        },
        new() {
            Name = "Modo de Baixa Latência para Áudio (MMCSS - Audio)",
            Description = "O MMCSS (Multimedia Class Scheduler Service) precisa estar configurado corretamente para garantir baixa latência de áudio. Sem ele, o áudio pode apresentar crackles, pops e stuttering sob carga de CPU.",
            Category = "Desempenho", Type = TweakType.Service, ServiceName = "MMCSS", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Feedback de Diagnóstico (Telemetria de Alto Impacto)",
            Description = "O nível máximo de telemetria ('Full') causa alto I/O de disco e uso de CPU para coleta e envio de dados diagnósticos. Reduzir para 'Basic' ou 'Security' diminui o impacto no desempenho.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", ValueName = "AllowTelemetry", HarmfulValue = 3, DefaultValue = 1
        },
        new() {
            Name = "Autenticação de Convidado Insegura (AllowInsecureGuestAuth Ativado)",
            Description = "O SMB permite autenticação de convidado insegura quando AllowInsecureGuestAuth=1, expondo compartilhamentos de rede a conexões sem autenticação real (risco em redes não confiáveis).",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters", ValueName = "AllowInsecureGuestAuth", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Assinatura SMB Desativada (RequireSecuritySignature=0)",
            Description = "A assinatura de pacotes SMB desativada permite ataques de man-in-the-middle em compartilhamentos de rede (CVE-2018-8440 e variantes). Manter habilitada protege a integridade do tráfego SMB.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters", ValueName = "RequireSecuritySignature", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Nível de Compatibilidade LM/NTLM Baixo (LmCompatibilityLevel=1)",
            Description = "LmCompatibilityLevel baixo permite autenticação LAN Manager/NTLMv1, protocolos obsoletos e vulneráveis a ataques de retransmissão (relay). O nível 3 (NTLMv2) é o padrão seguro do Windows.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa", ValueName = "LmCompatibilityLevel", HarmfulValue = 1, DefaultValue = 3
        },
        new() {
            Name = "Smart App Control Desativado (VerifiedAndReputablePolicyState=0)",
            Description = "O Smart App Control (Windows 11) bloqueia apps não assinados/não confiáveis. Desativá-lo deixa o PC vulnerável a malware disfarçado de programa legítimo.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CI\Policy", ValueName = "VerifiedAndReputablePolicyState", HarmfulValue = 0, DefaultValue = 1, IsOptional = true
        },
        new() {
            Name = "Instalação de Drivers Restrita Desativada (PrintNightmare)",
            Description = "RestrictDriverInstallationToAdministrators=0 permite que usuários comuns instalem drivers de impressora via ponto e imprimir (Point and Print), vetor do exploit PrintNightmare (CVE-2021-34527).",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows NT\Printers\PointAndPrint", ValueName = "RestrictDriverInstallationToAdministrators", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Anúncios Personalizados Ativados (AdvertisingInfo=1)",
            Description = "A ID de publicidade do dispositivo permite que apps rastreiem e personalizem anúncios. Desativá-la (AdvertisingInfo=0) protege a privacidade sem quebrar funcionalidade.",
            Category = "Privacidade Global", KeyPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\AdvertisingInfo", ValueName = "Enabled", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Experiências Sob Medida Ativadas (TailoredExperiences=1)",
            Description = "As experiências sob medida usam dados de diagnóstico para mostrar dicas e anúncios personalizados. Desativá-las reduz o rastreamento sem impacto em funcionalidade.",
            Category = "Privacidade Global", KeyPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy", ValueName = "TailoredExperiencesWithDiagnosticDataEnabled", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Atividade do Feed Ativada (EnableActivityFeed=1)",
            Description = "O feed de atividades (linha do tempo) coleta e exibe histórico de atividades do usuário, armazenando dados localmente e enviando parte para a Microsoft. Desativá-lo preserva a privacidade.",
            Category = "Privacidade Global", KeyPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Privacy", ValueName = "EnableActivityFeed", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Busca na Web com Bing Ativada (BingSearchEnabled=1)",
            Description = "A busca na web integrada ao menu Iniciar envia consultas de pesquisa para a Microsoft. Desativá-la faz a busca funcionar localmente, sem enviar dados de consulta.",
            Category = "Privacidade Global", KeyPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Search", ValueName = "BingSearchEnabled", HarmfulValue = 1, DefaultValue = 0
        }
    };

        #endregion

        #region Métodos Públicos (Interface Gráfica)

        public static List<ScannableTweak> GetAllTweaksDefinition()
        {
            return HarmfulTweaks;
        }

        /// <summary>
        /// Preenche o Id ESTÁVEL de cada check.
        ///
        /// POR QUE: o estado "aguardando reinício" é gravado em disco
        /// e precisa casar com o MESMO check no próximo boot. O Name é um texto
        /// em português que pode ser reescrito a qualquer momento — se alguém
        /// ajusta a redação ("Integridade de Memória" -> "Integridade de memória
        /// (HVCI)"), a pendência vira órfã e some em silêncio.
        ///
        /// O Id deriva do que REALMENTE identifica o check (tipo + chave + valor,
        /// ou o nome do serviço), que não muda quando o texto muda. É o mesmo
        /// principio do StableId do Windows.
        /// </summary>
        private static void EnsureIds()
        {
            foreach (var t in HarmfulTweaks)
            {
                if (!string.IsNullOrEmpty(t.Id)) continue;

                string chave;
                switch (t.Type)
                {
                    case TweakType.Service:
                        chave = "svc|" + t.ServiceName + "|" + t.DefaultStartMode;
                        break;
                    case TweakType.Bcd:
                        chave = "bcd|" + t.ValueName;
                        break;
                    case TweakType.PageFile:
                        chave = "pgf|" + t.KeyPath + "|" + t.ValueName;
                        break;
                    case TweakType.Mouse:
                        chave = "mouse|" + t.ValueName;
                        break;
                    default:
                        chave = "reg|" + t.KeyPath + "|" + t.ValueName;
                        break;
                }
                t.Id = chave;
            }
        }

        public static List<ScannableTweak> GetHarmfulTweaksWithStatus()
        {
            // Single-flight: scans concorrentes (Integridade + busca global +
            // FixAll + toggles) agora SERIALIZAM - um unico bcdedit e uma unica
            // varredura de PATH por janela de cache em vez de N em paralelo.
            lock (_scanGate)
            {
                EnsureIds();
                var tweaksCopy = new List<ScannableTweak>(HarmfulTweaks.Count);

                using var batch = new RegistryBatch();
                _currentBatch = batch;
                // Cache BCD compartilhado: invalida SO quando expira (TTL 15s),
                // nao a cada scan - o 1o scan de uma janela roda o bcdedit e os
                // scans/toggles seguintes reusam o output sem spawnar processo.
                if (!_bcdEnumAttempted || (DateTime.UtcNow - _bcdEnumTimeUtc).TotalSeconds >= BcdEnumCacheTtlSeconds)
                {
                    _bcdEnumOutput = null;
                    _bcdEnumError = null;
                    _bcdEnumAttempted = false;
                }
                try
                {
                    // BCD: UM único bcdedit /enum para todo o scan (antes: 1 processo por tweak BCD).
                    if (HarmfulTweaks.Any(t => t.Type == TweakType.Bcd))
                    {
                        if (!_bcdEnumAttempted)
                        {
                            var bcd = ProcessRunner.Run("bcdedit", "/enum", 10000);
                            // Ordem: escrever dados ANTES do flag (leitor nunca ve
                            // flag=true com output pela metade).
                            _bcdEnumOutput = bcd.Output;
                            _bcdEnumError = bcd.Error;
                            _bcdEnumTimeUtc = DateTime.UtcNow;
                            _bcdEnumAttempted = true;
                            if (bcd.ExitCode != 0)
                                Logger.Log($"[CHECK] Erro ao obter BCD (cache): {bcd.Error}");
                        }
                    }

                    foreach (var tweak in HarmfulTweaks)
                    {
                        CheckTweak(tweak);
                        tweaksCopy.Add(tweak);
                    }
                }
                finally
                {
                    _currentBatch = null;
                    // Cache BCD MANTIDO entre scans (TTL cuida da validade) -
                    // o reset no finally destruiria o ganho entre scans/toggles.
                }

                // Correcoes que so valem depois de reiniciar entram em PENDING
                // (amarelo) em vez de fingirem protecao. No proximo boot o Store
                // confere se o efeito veio e invalida o check se nao veio.
                PendingRepairStore.Apply(tweaksCopy);
                return tweaksCopy;
        }
            }


        public static (bool Success, string Message) ToggleTweak(ScannableTweak tweak)
        {
            try
            {
                Logger.Log($"[TOGGLE] Iniciando alteração: {tweak.Name}");
                Logger.Log($"[TOGGLE] Status atual: {tweak.Status}, Tipo: {tweak.Type}");

                bool applySafeValue = tweak.Status == TweakStatus.MODIFIED;
                string action = applySafeValue ? "restaurado para o padrão seguro" : "alterado (personalizado)";

                switch (tweak.Type)
                {
                    case TweakType.Registry:
                        // Secure Boot e' decisao da FIRMWARE (UEFI). Gravar no registro
                        // deixava o check verde e nao ligava NADA - correcao aparente.
                        // Aqui a ferramenta diz a verdade em vez de fingir.
                        if (IsSecureBootCheck(tweak))
                        {
                            if (!TryReadSecureBootFirmware(out bool ligado, out string motivo))
                                return (false, $"Nao foi possivel verificar o Secure Boot: {motivo}.");

                            if (ligado)
                                return (true, "Secure Boot ja esta ligado.");

                            return (false,
                                "O Secure Boot e' decisao da FIRMWARE (UEFI), nao do Windows: " +
                                "gravar no registro deixa o check verde sem ligar a protecao. " +
                                "Ative em Configuracoes > Recuperacao > Inicializacao Segura > " +
                                "Solucao de problemas > Configuracoes de Inicializacao UEFI > " +
                                "Seguranca de Inicializacao UEFI (o PC precisa reiniciar).");
                        }

                        if (string.IsNullOrEmpty(tweak.KeyPath))
                            return (false, "Configuração de registro inválida.");

                        // Limpar o prefixo do hive do caminho (longo e curto)
                        string path = tweak.KeyPath
                            .Replace(@"HKEY_LOCAL_MACHINE\", "")
                            .Replace(@"HKEY_CURRENT_USER\", "")
                            .Replace(@"HKEY_CLASSES_ROOT\", "")
                            .Replace(@"HKLM\", "")
                            .Replace(@"HKCU\", "")
                            .Replace(@"HKCR\", "");

                        // Determinar o hive correto
                        RegistryKey baseKey;
                        if (tweak.KeyPath.StartsWith("HKEY_LOCAL_MACHINE") || tweak.KeyPath.StartsWith("HKLM"))
                            baseKey = Registry.LocalMachine;
                        else if (tweak.KeyPath.StartsWith("HKEY_CLASSES_ROOT") || tweak.KeyPath.StartsWith("HKCR"))
                            baseKey = Registry.ClassesRoot;
                        else
                            baseKey = Registry.CurrentUser;

                        // Determinar o nome do valor ("" = valor padrão da chave)
                        string actualValueName = tweak.ValueName ?? "";

                        // REPARO AUTOMÁTICO DE PATH
                        object? valueToSet;
                        if (tweak.Name.Contains("PATH") && tweak.ValueName == "Path")
                        {
                            // Para PATH, sempre usar verificação dinâmica, independente de applySafeValue
                            object? currentRaw = Registry.GetValue(tweak.KeyPath, tweak.ValueName, null);
                            string currentPath = currentRaw?.ToString() ?? "";

                            // Determinar se é System ou User PATH baseado no KeyPath
                            string pathType = tweak.KeyPath.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase) ? "System" : "User";

                            // Diagnóstico avançado do PATH
                            var entries = PathRepair.DiagnosePath(currentPath, pathType);
                            var (repairedPath, actions) = PathRepair.RepairPathEntries(entries, pathType);

                            // Garantir caminhos essenciais do Windows (para System PATH)
                            if (pathType == "System")
                            {
                                repairedPath = PathRepair.EnsureSystemPathMinimum(repairedPath);
                            }
                            // Adicionar caminhos de programas instalados (para User PATH)
                            else if (pathType == "User")
                            {
                                var installedPaths = PathRepair.GetInstalledProgramPaths();
                                // Fallback dinamico: se o registro falhou, escanear os .exe no disco
                                if (installedPaths.Count == 0)
                                {
                                    installedPaths = PathRepair.RecoverFromExecutableScan();
                                }
                                var (userPath, addedPaths) = PathRepair.EnsureUserPathMinimum(repairedPath, installedPaths);
                                repairedPath = userPath;
                                foreach (var added in addedPaths)
                                {
                                    Logger.Log($"[PATH REPAIR] {added}");
                                }
                            }

                            bool changed = !currentPath.Equals(repairedPath, StringComparison.OrdinalIgnoreCase);
                            if (changed)
                            {
                                valueToSet = repairedPath;
                                // Resumo CURTO: `actions` só tem mudanças de verdade e o
                                // log leva a contagem (a linha antiga chegava a 1000+
                                // caracteres com os "Mantido" que nada faziam).
                                string resumo = actions.Count == 0
                                    ? "essenciais do sistema ajustados"
                                    : $"{actions.Count} ajuste(s): {string.Join("; ", actions)}";
                                Logger.Log($"[PATH REPAIR] PATH {pathType} reparado — {resumo}");
                            }
                            else
                            {
                                // Se não mudou, não fazer nada e retornar sucesso
                                Logger.Log($"[PATH REPAIR] PATH {pathType} já está correto. Nenhuma alteração necessária.");
                                return (true, $"PATH {pathType} já está correto.");
                            }
                        }
                        else if (tweak.Name.Contains("Aliases do PowerShell"))
                        {
                            if (applySafeValue)
                            {
                                FixPowerShellAliases();
                                return (true, "Aliases quebrados do PowerShell foram removidos. Reinicie o PowerShell para aplicar.");
                            }
                            return (true, "Nenhum alias quebrado para corrigir.");
                        }
                        else
                        {
                            valueToSet = applySafeValue ? tweak.DefaultValue : tweak.HarmfulValue;
                        }

                        // Se valueToSet é null (PATH não mudou), retornar sucesso
                        if (valueToSet == null)
                        {
                            return (true, "Nenhuma alteração necessária.");
                        }

                        if (valueToSet is string valStr && valStr == "DELETE_KEY")
                        {
                            try
                            {
                                baseKey.DeleteSubKeyTree(path, false);
                                Logger.Log($"[TOGGLE] Chave de registro '{path}' deletada com sucesso.");
                                return (true, $"{tweak.Name} foi {action}.");
                            }
                            catch (Exception ex)
                            {
                                Logger.Log($"[TOGGLE] Erro ao deletar chave '{path}': {ex.Message}. Tentando forçar posse...");
                                if (RegistryOwnership.ForceTakeOwnership(baseKey, path))
                                {
                                    try
                                    {
                                        baseKey.DeleteSubKeyTree(path, false);
                                        Logger.Log($"[TOGGLE] Chave de registro '{path}' deletada com sucesso após obter posse.");
                                        return (true, $"{tweak.Name} foi {action}.");
                                    }
                                    catch (Exception ex2)
                                    {
                                        Logger.Log($"[TOGGLE] Erro ao deletar chave '{path}' mesmo após obter posse: {ex2.Message}");
                                        return (false, $"Erro ao deletar chave '{tweak.Name}': {ex2.Message}");
                                    }
                                }
                                return (false, $"Erro ao deletar chave '{tweak.Name}': {ex.Message}");
                            }
                        }

                        // Usar o TrySetValueWithOwnershipFallback para chaves protegidas
                        if (!RegistryOwnership.TrySetValueWithOwnershipFallback(baseKey, path, actualValueName, valueToSet, tweak.ValueKind))
                        {
                            // Fallback: tentar Registry.SetValue direto (funciona para HKEY_CLASSES_ROOT, etc.)
                            Logger.Log($"[TOGGLE] Ownership falhou, tentando Registry.SetValue direto...");
                            try
                            {
                                Registry.SetValue(tweak.KeyPath, actualValueName, valueToSet);
                                Logger.Log($"[TOGGLE] Registry.SetValue direto funcionou para '{tweak.Name}'");
                            }
                            catch (Exception directEx)
                            {
                                Logger.Log($"[TOGGLE] Registry.SetValue direto também falhou: {directEx.Message}");
                                return (false, $"Falha ao modificar '{tweak.Name}': {directEx.Message}");
                            }
                        }
                        // Integridade de Memoria (HVCI) so passa a valer DEPOIS de
                        // reiniciar e se a CPU/firmware suportarem. Sem este aviso o
                        // usuario acha que ficou protegido na hora.
                        //
                        // O registro e' so a CONFIGURACAO PEDIDA; o estado real so
                        // aparece em NtQuerySystemInformation(103), consultado no
                        // CheckTweak. Se essa leitura estiver disponivel, dizemos o
                        // que o kernel mostra AGORA em vez de so pedir reinicio.
                        if (tweak.Name.Contains("HVCI", StringComparison.OrdinalIgnoreCase))
                        {
                            if (NativeCodeIntegrity.TryGetHvci(out bool impoe, out bool audit,
                                                               out bool disp, out uint _, out string motHvci))
                            {
                                _notaPosCorrecao = tweak.Name;
                                _avisoPosCorrecao = impoe && !audit
                                    ? "A Integridade de Memoria JA esta ativa agora (confirmada pelo kernel)."
                                    : (audit
                                        ? "HVCI esta em modo de AUDITORIA: registra eventos, mas nao bloqueia. " +
                                          "Para proteger de fato e preciso ativar no proximo boot."
                                        : "A configuracao foi gravada, mas o kernel ainda nao esta aplicando HVCI. " +
                                          "So passa a valer depois de reiniciar, e apenas se a CPU e a firmware suportarem.");
                            }
                            else
                            {
                                _notaPosCorrecao = tweak.Name;
                                _avisoPosCorrecao =
                                    "A configuracao foi gravada. Nao da para ler o estado real do HVCI aqui (" +
                                    motHvci + "); ele so e confirmavel apos reiniciar, e depende de CPU/firmware.";
                            }
                        }

                        break;

                    case TweakType.Service:
                        if (string.IsNullOrEmpty(tweak.ServiceName) || string.IsNullOrEmpty(tweak.DefaultStartMode))
                            return (false, "Configuração de serviço inválida.");

                        // Serviço desativado de propósito pelo KitLugia (telemetria/diagnóstico):
                        // restaurar via Integrity reativaria o que o usuário desligou nos toggles.
                        if (applySafeValue &&
                            KitIntentionalServiceStart.TryGetValue(tweak.ServiceName, out var intentionalMode) &&
                            intentionalMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                        {
                            Logger.Log($"[TOGGLE] {tweak.ServiceName} está desativado de propósito pelo KitLugia - não reativado. Use o toggle do Kit (Tweaks) para reativar.");
                            tweak.Status = TweakStatus.OK;
                            return (true, $"{tweak.Name} permanece desativado (intencional do KitLugia - reative pelo toggle do Kit).");
                        }

                        string mode = applySafeValue ? tweak.DefaultStartMode : tweak.HarmfulStartMode ?? "Disabled";
                        string scMode = mode.ToLower();

                        // Corrigir mapeamento de modos para o comando sc.exe
                        switch (scMode)
                        {
                            case "manual":
                                scMode = "demand";
                                break;
                            case "automatic":
                                scMode = "auto";
                                break;
                            case "delayed-auto":
                                scMode = "delayed-auto";
                                break;
                            case "disabled":
                                scMode = "disabled";
                                break;
                        }

                        string scCmd = $"config {tweak.ServiceName} start= {scMode}";

                        Logger.Log($"[TOGGLE] Executando: sc.exe {scCmd}");
                        var (scExitCode, scResult) = SystemUtils.RunExternalProcessWithCode("sc.exe", scCmd, true);
                        Logger.Log($"[TOGGLE] SC Config Result: {scResult.Trim()}");

                        if (scExitCode != 0)
                        {
                            Logger.Log($"[TOGGLE] SC Config falhou com código {scExitCode}");
                            return (false, $"Falha ao configurar o serviço '{tweak.ServiceName}' (código {scExitCode}).");
                        }

                        if (applySafeValue && mode != "Disabled")
                        {
                            Logger.Log($"[TOGGLE] Iniciando serviço: {tweak.ServiceName}");
                            var (startExitCode, startResult) = SystemUtils.RunExternalProcessWithCode("sc.exe", $"start {tweak.ServiceName}", true);
                            Logger.Log($"[TOGGLE] SC Start Result: {startResult.Trim()}");
                            // 5 = Acesso negado (serviços protegidos como Wdi*), 1056 = já em execução:
                            // não são falhas do toggle, só avisos.
                            if (startExitCode != 0 && startExitCode != 5 && startExitCode != 1056)
                            {
                                Logger.Log($"[TOGGLE] Aviso: start do serviço retornou código {startExitCode} (serviços protegidos podem negar o start).");
                            }
                        }
                        break;

                    case TweakType.Mouse:
                        bool setStandard = applySafeValue;
                        Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseSpeed", setStandard ? "1" : "0", RegistryValueKind.String);
                        Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseThreshold1", setStandard ? "6" : "0", RegistryValueKind.String);
                        Registry.SetValue(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseThreshold2", setStandard ? "10" : "0", RegistryValueKind.String);
                        break;

                    case TweakType.Bcd:
                        if (string.IsNullOrEmpty(tweak.ValueName)) return (false, "BCD inválido.");
                        string bcdValue = (applySafeValue ? tweak.DefaultValue?.ToString() : tweak.HarmfulValue?.ToString()) ?? "";
                        string bcdCommand;

                        if (bcdValue == "delete")
                            bcdCommand = $"/deletevalue {tweak.ValueName}";
                        else
                            bcdCommand = $"/set {tweak.ValueName} {bcdValue}";

                        Logger.Log($"[TOGGLE] Executando BCD: bcdedit {bcdCommand}");
                        var (exitCode, output, error) = ProcessRunner.Run("bcdedit", bcdCommand, 10000);

                        if (exitCode != 0)
                        {
                            Logger.Log($"[TOGGLE] BCD falhou com código {exitCode} - Saída: '{output.Trim()}' - Erro: '{error.Trim()}'");
                            return (false, $"Falha ao modificar BCD: {error}");
                        }

                        Logger.Log($"[TOGGLE] BCD sucesso: {output}");

                        // Invalida o cache do bcdedit /enum ANTES do CheckTweak pós-toggle:
                        // senão a re-verificação lê o output antigo (TTL 15s) e o item
                        // continua MODIFIED mesmo após corrigido — "FALHA: alteração não
                        // aplicada" falso em todo comando BCD da IntegrityPage.
                        _bcdEnumAttempted = false;
                        _bcdEnumOutput = null;
                        _bcdEnumError = null;
                        break;

                    case TweakType.PageFile:
                        const string pfKey = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
                        if (applySafeValue)
                                Registry.SetValue(pfKey, "PagingFiles", new string[] { Path.Combine(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\", "pagefile.sys") }, RegistryValueKind.MultiString);
                        else
                            Registry.SetValue(pfKey, "PagingFiles", Array.Empty<string>(), RegistryValueKind.MultiString);
                        break;
                }

                // Para tweaks do Explorer (que usam CheckExplorerProblems), não usar CheckTweak
                // pois CheckExplorerProblems é genérico e pode reportar problemas não relacionados.
                // SEMPRE re-verificar depois de escrever.
                //
                // BUG CORRIGIDO (06/10/2026): este bloco antes terminava com
                //     if (categoria em {Corrupcao, Defender, Restricoes})
                //         tweak.Status = TweakStatus.OK;   // <- forca verde
                //     else CheckTweak(tweak);
                // ou seja, nas 3 categorias o status virava PROTEGIDO SEM reler o
                // registro. Sao 74 checks (24 + 34 + 16) que ficavam verdes depois
                // de "corrigir" sem nunca confirmar que o valor mudou. O comentario
                // justificava com um CheckExplorerProblems que o CheckTweak nao usa
                // ha muito tempo: era resto de codigo antigo que ficou.
                //
                // Sem re-verificar, um check e' exatamente o tipo de check que nao
                // funciona: parece corrigido e nunca foi confirmado.
                CheckTweak(tweak);

                // A verificação genérica abaixo assume que escrevemos o valor
                // "seguro" (applySafeValue=true) e portanto o status esperado
                // é OK. Mas ToggleTweak também é usado para APLICAR o valor
                // otimizado/customizado (applySafeValue=false, escreve o valor
                // "harmful"). Nesse caso o status esperado é MODIFIED — e se
                // o check reportar OK, é porque o Windows reverteu a escrita.
                //
                // Sem essa distinção, itens opcionais (ex.: Spooler) que foram
                // desativados de propósito pelo usuário viravam "foi gravado
                // mas continua com o valor errado" mesmo com a alteração
                // aplicada com sucesso (caso do log de 30/10/2026).
                bool wroteSafe = applySafeValue;
                bool failed = (wroteSafe && tweak.Status == TweakStatus.MODIFIED) ||
                              (!wroteSafe && tweak.Status == TweakStatus.OK);

                if (failed)
                {
                    // PATH: dizer QUAL entrada ainda está errada (antes só dizia
                    // "valor nao mudou ou foi reverteido" e não dava para agir).
                    string pendencia = "";
                    if (tweak.Name.Contains("PATH") && tweak.ValueName == "Path")
                    {
                        try
                        {
                            var atual = Registry.GetValue(tweak.KeyPath, tweak.ValueName, null)?.ToString() ?? "";
                            string tipo = tweak.KeyPath.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase) ? "System" : "User";
                            string problemas = PathRepair.DescribeProblems(atual, tipo);
                            if (!string.IsNullOrEmpty(problemas)) pendencia = $" Pendências: {problemas}";
                        }
                        catch { }
                    }
                    Logger.Log($"[TOGGLE] '{tweak.Name}' continua MODIFIED apos a escrita (valor nao mudou ou foi reverteido).{pendencia}");
                    return (false, $"'{tweak.Name}' foi gravado mas continua com o valor errado. " +
                                   "Esse valor costuma ser gerenciado pelo proprio Windows e volta ao estado anterior.");
                }

                string msgFinal = $"{tweak.Name} foi {action}.";

                // Correções que o Windows só aplica no próximo boot (HVCI, VBS,
                // pagefile...): dizer "foi corrigido" aqui seria mentira — o
                // hypervisor ainda não leu essa configuração. O item entra em
                // PENDING (amarelo, fora do score) e no próximo boot o Store
                // confere se o efeito realmente veio.
                if (PendingRepairStore.NeedsRebootAfterFix(tweak))
                {
                    const string motivo = "o Windows só aplica esta configuração na próxima inicialização";
                    PendingRepairStore.MarkPending(tweak, motivo);
                    msgFinal += " AGUARDANDO REINÍCIO: " + motivo +
                                ". Depois de reiniciar o check é conferido de novo; " +
                                "se o efeito não vier, o item volta ao estado normal (MODIFIED).";
                }

                if (!string.IsNullOrEmpty(_notaPosCorrecao) && _notaPosCorrecao == tweak.Name)
                {
                    msgFinal += " " + _avisoPosCorrecao;
                    _notaPosCorrecao = null;
                }
                return (true, msgFinal);
            }
            catch (UnauthorizedAccessException uae)
            {
                Logger.Log($"[TOGGLE] ERRO DE ACESSO NEGADO em '{tweak.Name}': {uae.Message}");
                Logger.Log($"[TOGGLE] StackTrace: {uae.StackTrace}");
                return (false, $"Acesso negado ao modificar '{tweak.Name}'. Execute como administrador.");
            }
            catch (InvalidCastException ice)
            {
                Logger.Log($"[TOGGLE] ERRO DE CONVERSÃO em '{tweak.Name}': {ice.Message}");
                Logger.Log($"[TOGGLE] StackTrace: {ice.StackTrace}");
                return (false, $"Erro de tipo de dados ao modificar '{tweak.Name}': {ice.Message}");
            }
            catch (SecurityException se)
            {
                Logger.Log($"[TOGGLE] ERRO DE SEGURANÇA em '{tweak.Name}': {se.Message}");
                return (false, $"Acesso de segurança negado ao modificar '{tweak.Name}'. O recurso pode ser protegido pelo TrustedInstaller. {se.Message}");
            }
            catch (System.Management.ManagementException me)
            {
                Logger.Log($"[TOGGLE] ERRO WMI em '{tweak.Name}': {me.Message}");
                return (false, $"Erro de WMI ao modificar '{tweak.Name}': {me.Message}");
            }
            catch (ArgumentException ae)
            {
                Logger.Log($"[TOGGLE] ERRO DE ARGUMENTO em '{tweak.Name}': {ae.Message}");
                return (false, $"Erro de tipo/conversão ao modificar '{tweak.Name}': {ae.Message}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[TOGGLE] ERRO GENÉRICO em '{tweak.Name}': {ex.Message}");
                Logger.Log($"[TOGGLE] Tipo: {ex.GetType().Name}");
                Logger.Log($"[TOGGLE] StackTrace: {ex.StackTrace}");
                return (false, $"Erro ao alternar '{tweak.Name}': {ex.Message}");
            }
        }

        public static Dictionary<string, bool> GetTweakStates()
        {

            // Típico: 20-50 tweaks
            var states = new Dictionary<string, bool>(50, StringComparer.OrdinalIgnoreCase);

            try
            {
                using (RegistryKey? configKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KitLugia\Config", true))
                {
                    if (configKey == null) return states;
                    var allTweaks = GetAllTweaksDefinition();

                    foreach (var tweak in allTweaks)
                    {
                        string valueKey = $"Tweak_{GetSafeKeyName(tweak)}";
                        string? savedStatus = configKey.GetValue(valueKey) as string;

                        if (!string.IsNullOrEmpty(savedStatus) && Enum.TryParse<TweakStatus>(savedStatus, out var status))
                        {
                            states[tweak.Name] = (status == TweakStatus.MODIFIED);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao carregar estados: {ex.Message}");
            }

            return states;
        }

        #endregion

        #region Detecção de Problemas PATH

        private static PathProblem AnalyzePathProblems(string pathValue)
        {
            if (string.IsNullOrEmpty(pathValue)) return PathProblem.None;

            if (NativePath.UseNative)
                return NativePath.Analyze(pathValue);

            var entries = pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var problems = PathProblem.None;

            if (entries.Length > 50) problems |= PathProblem.TooManyEntries;
            if (pathValue.Length > 2048) problems |= PathProblem.PathTooLong;

            foreach (var entry in entries)
            {
                var clean = entry.Trim().Trim('"').Trim();
                if (string.IsNullOrEmpty(clean)) continue;

                if (!seen.Add(clean)) problems |= PathProblem.Duplicate;
                if (clean.StartsWith('.') || clean.StartsWith(".."))
                    problems |= PathProblem.RelativePath;
                if (clean.Contains(' ') && !clean.StartsWith('"'))
                    problems |= PathProblem.UnquotedSpace;
                if (clean.Contains("\\Temp\\", StringComparison.OrdinalIgnoreCase) ||
                    clean.Contains("\\Tmp\\", StringComparison.OrdinalIgnoreCase))
                    problems |= PathProblem.TempPath;
                if (clean.Contains("\\Users\\", StringComparison.OrdinalIgnoreCase) &&
                    !clean.Contains("%USERPROFILE%"))
                    problems |= PathProblem.UserPathWithoutVariable;
                if (clean.Contains("\\node_modules\\") || clean.Contains("\\vendor\\") ||
                    clean.Contains("\\.git\\") || clean.Contains("\\dotnet\\sdk\\"))
                    problems |= PathProblem.DevelopmentJunk;
                if (clean.Contains(',') || clean.Contains("\"\"") || clean.Contains("\\\\"))
                    problems |= PathProblem.SyntaxError;
                if (clean.Length > 260) problems |= PathProblem.LongPath;
                if (clean.Any(c => c > 127)) problems |= PathProblem.EncodingIssue;

                try
                {
                    var expanded = Environment.ExpandEnvironmentVariables(clean);
                    if (!Directory.Exists(expanded))
                        problems |= PathProblem.MissingDirectory;
                }
                catch
                {
                    problems |= PathProblem.SyntaxError;
                }
            }
            return problems;
        }

        private static bool CheckPathProblems(string pathValue, string? problemType, string? keyPath = null)
        {
            // Registry.GetValue expande REG_EXPAND_SZ, mas RegistryKey.GetValue (via batch) não.
            // Normalizar para garantir que %SystemRoot% etc. estejam expandidos.
            pathValue = Environment.ExpandEnvironmentVariables(pathValue);

            // Para PATH Incompleto (ou quando problemType é null), verificar se faltam caminhos essenciais
            if (problemType == "INCOMPLETE" || problemType == null)
            {
                // Determinar se é System ou User PATH baseado no KeyPath
                string pathType = (keyPath != null && keyPath.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase)) ? "System" : "User";
                var entries = PathRepair.DiagnosePath(pathValue, pathType);
                var installedPaths = PathRepair.GetInstalledProgramPaths();

                // Para System PATH: verificar se faltam caminhos essenciais do Windows
                if (pathType == "System")
                {
                    string sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\');
                    string expanded = pathValue.ToLower();

                    // Essencial so e exigido se a pasta EXISTE no disco. Exigir OpenSSH
                    // numa maquina sem o recurso opcional instalado (ou PowerShell 7 sem o
                    // PS7 instalado) faz o item nunca sair de "modificado" — o reparo nao
                    // pode (e nao deve) adicionar um caminho cuja pasta nao existe.
                    string[] requiredDirs =
                    {
                        $@"{sysRoot}\system32",
                        $@"{sysRoot}\system32\wbem",
                        $@"{sysRoot}\system32\windowspowershell\v1.0",
                        $@"{sysRoot}\system32\openssh"
                    };

                    foreach (var dir in requiredDirs)
                    {
                        if (Directory.Exists(dir) && !expanded.Contains(dir.ToLower()))
                            return true; // instalado no disco mas ausente do PATH
                    }
                    return false;
                }
                // Para User PATH: verificar se faltam programas instalados
                else
                {
                    // Programas instalados (winget/dotnet/git/node/...) so sao exigidos no
                    // User PATH se a pasta EXISTE no disco E nao esta em NENHUM dos dois
                    // paths (User OU System). Instalacoes de maquina vivem no System PATH:
                    // exigir copia no User PATH e falso positivo eterno — e o reparo
                    // (EnsureUserPathMinimum) ja nao duplica mais System -> User.
                    var sysEntries = PathRepair.GetSystemPathValue()
                        .Split(';', StringSplitOptions.RemoveEmptyEntries);

                    bool hasMissingPaths = false;
                    foreach (var kvp in installedPaths)
                    {
                        string expanded = Environment.ExpandEnvironmentVariables(kvp.Value).TrimEnd('\\');
                        if (!Directory.Exists(expanded)) continue; // pasta nao existe -> nao exigir

                        bool found = false;
                        foreach (var entry in pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        {
                            string entryExpanded = Environment.ExpandEnvironmentVariables(entry).TrimEnd('\\');
                            if (entryExpanded.Equals(expanded, StringComparison.OrdinalIgnoreCase))
                            {
                                found = true;
                                break;
                            }
                        }

                        if (!found)
                        {
                            foreach (var s in sysEntries)
                            {
                                try
                                {
                                    string sExpanded = Environment.ExpandEnvironmentVariables(s).TrimEnd('\\');
                                    if (sExpanded.Equals(expanded, StringComparison.OrdinalIgnoreCase))
                                    {
                                        found = true;
                                        break;
                                    }
                                }
                                catch { }
                            }
                        }

                        if (!found)
                        {
                            hasMissingPaths = true;
                            break;
                        }
                    }
                    return hasMissingPaths;
                }
            }

            // Lógica original para outros tipos de problemas
            var pathProblems = AnalyzePathProblems(pathValue);
            return problemType switch
            {
                "CORRUPTED" => (pathProblems & (PathProblem.MissingDirectory | PathProblem.SyntaxError |
                               PathProblem.RelativePath | PathProblem.TooManyEntries |
                               PathProblem.PathTooLong | PathProblem.EncodingIssue)) != 0,
                "DUPLICATE" => (pathProblems & PathProblem.Duplicate) != 0,
                "MISSING" => (pathProblems & PathProblem.MissingDirectory) != 0,
                "DEV_JUNK" => (pathProblems & PathProblem.DevelopmentJunk) != 0,
                "VULNERABLE" => (pathProblems & (PathProblem.MissingDirectory | PathProblem.RelativePath |
                                  PathProblem.TempPath)) != 0,
                _ => false
            };
        }

        #endregion

        /// <summary>
        /// Reparo consolidado de UMA vez: System PATH (duplicatas, pastas mortas,
        /// essenciais ausentes) + User PATH (duplicatas, pastas mortas, PROGRAMAS
        /// INSTALADOS fora do PATH — winget, git, node, dotnet, npm, 7z, cargo...).
        /// E o que o botao unico "CORRIGIR PATH" do IntegrityPage executa (nao
        /// precisa mais abrir o menu ➕ para adicionar cada caminho ausente).
        /// </summary>
        public static (bool Changed, string Summary) RepairAllPathsOnce()
        {
            var steps = new List<string>();

            // Acoes "Mantido (WrongLocation)" e "Ignorado" sao informativas (nada mudou)
            // - nao entram no resumo para o dialogo nao virar um muro de texto.
            static string Meaningful(IEnumerable<string> actions) => string.Join("; ", actions.Where(a =>
                !a.StartsWith("Mantido", StringComparison.OrdinalIgnoreCase) &&
                !a.StartsWith("Ignorado", StringComparison.OrdinalIgnoreCase)));

            // ---- System PATH ----
            string sys = PathRepair.GetSystemPathValue();
            if (!string.IsNullOrEmpty(sys))
            {
                var sysEntries = PathRepair.DiagnosePath(sys, "System");
                var (sysRepaired, sysActions) = PathRepair.RepairPathEntries(sysEntries, "System");
                sysRepaired = PathRepair.EnsureSystemPathMinimum(sysRepaired);
                bool sysChanged = !sys.Equals(sysRepaired, StringComparison.OrdinalIgnoreCase);
                if (sysChanged)
                {
                    if (PathRepair.SetSystemPathValue(sysRepaired))
                    {
                        string m = Meaningful(sysActions);
                        if (string.IsNullOrEmpty(m)) m = "essenciais do sistema ajustados";
                        steps.Add("[SISTEMA] " + m);
                        string m2 = string.IsNullOrEmpty(m) ? "essenciais do sistema ajustados" : m;
                        Logger.Log($"[PATH REPAIR] System PATH reparado — {m2}");
                    }
                    else
                    {
                        steps.Add("[SISTEMA] FALHA ao gravar (requer admin).");
                    }
                }
            }

            // ---- User PATH ----
            string usr = PathRepair.GetUserPathValue();
            if (!string.IsNullOrEmpty(usr))
            {
                var usrEntries = PathRepair.DiagnosePath(usr, "User");
                var (usrRepaired, usrActions) = PathRepair.RepairPathEntries(usrEntries, "User");
                var installed = PathRepair.GetInstalledProgramPaths();
                if (installed.Count == 0)
                    installed = PathRepair.RecoverFromExecutableScan();
                var (usrFinal, usrAdded) = PathRepair.EnsureUserPathMinimum(usrRepaired, installed);
                bool userChanged = !usr.Equals(usrFinal, StringComparison.OrdinalIgnoreCase);
                if (userChanged)
                {
                    var allUser = usrActions.Concat(usrAdded).ToList();
                    if (PathRepair.SetUserPathValue(usrFinal))
                    {
                        string m = Meaningful(allUser);
                        if (string.IsNullOrEmpty(m)) m = "programas instalados adicionados ao PATH";
                        steps.Add("[USUARIO] " + m);
                        string m2 = string.IsNullOrEmpty(m) ? "programas instalados adicionados" : m;
                        Logger.Log($"[PATH REPAIR] User PATH reparado — {m2}");
                    }
                    else
                    {
                        steps.Add("[USUARIO] FALHA ao gravar (requer admin).");
                    }
                }
            }

            bool changed = steps.Count > 0;
            return (changed, changed
                ? string.Join(Environment.NewLine, steps)
                : "PATH do sistema e do usuario ja estao corretos (sem duplicatas, sem pastas mortas e todos os programas instalados no PATH).");
        }

        #region Detecção de Problemas do Windows Explorer

        #endregion

        #region Métodos Auxiliares

        private static string GetSafeKeyName(ScannableTweak tweak)
        {
            // Remove caracteres inválidos para nome de valor do registro
            var safeName = string.Concat(tweak.Category, "_", tweak.Name)
                               .Replace('\\', '_').Replace('/', '_').Replace(':', '_')
                               .Replace('?', '_').Replace('*', '_').Replace('"', '_')
                               .Replace('<', '_').Replace('>', '_').Replace('|', '_');
            return safeName.Length > 255 ? safeName[..255] : safeName;
        }

        #endregion

        #region Verificação Interna

        // ─── Secure Boot: a fonte autoritativa e a FIRMWARE, nao o registro ───
        //
        // BUG CORRIGIDO (06/10/2026): o valor
        // HKLM\SYSTEM\CurrentControlSet\Control\SecureBoot\State\UEFISecureBootEnabled
        // e' ESCREVIVEL. O proprio Guardian escrevia 1 nele, o check relia 1 e
        // ficava PROTEGIDO — com o Secure Boot CONTINUANDO DESLIGADO, porque o
        // Secure Boot e' decisao da firmware (UEFI). Esse valor so REFLETE o
        // estado; nao o liga.
        //
        // A verdade e a variavel UEFI "SecureBoot", lida por
        // GetFirmwareEnvironmentVariable. Quando a leitura nao e possivel (sem
        // admin, ou maquina que nao expoe a variavel), o check tem de dizer que
        // NAO CONSEGUE VERIFICAR (NOT_FOUND, que a UI ja tem como filtro) em vez
        // de fingir que esta protegido.
        // GUID da namespace de variaveis UEFI globais (EFI_GLOBAL_VARIABLE).
        private const string EfiGlobalVariableGuid = "{8BE4DF61-93CA-11D2-AA0D-00E098032B8C}";

        // Assinatura REAL: DWORD GetFirmwareEnvironmentVariableW(
        //     LPCWSTR lpName, LPCWSTR lpGuid, PVOID pBuffer, DWORD nSize)
        // A declaracao antiga omitia lpGuid: o byte[] era interpretado como GUID,
        // o tamanho (1/8/64) como PONTEIRO de buffer e um out nao inicializado
        // como nSize — escrita em endereco invalido. Retorno = bytes lidos (0 = falha).
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode,
            EntryPoint = "GetFirmwareEnvironmentVariableW")]
        private static extern uint GetFirmwareEnvironmentVariableW(
            string lpName, string lpGuid, byte[] pBuffer, uint nSize);

        private static string? _notaPosCorrecao;
        private static string? _avisoPosCorrecao;
        private static bool _jaAvisouSecureBoot;
        private static bool _jaAvisouHvci;

        private static bool TryReadSecureBootFirmware(out bool enabled, out string motivo)
        {
            enabled = false;

            // CORRIGIDO (12/11/2026): a assinatura da P/Invoke estava errada
            // (faltava lpGuid). O "fix" de 06/10 so aumentava o buffer e nao
            // resolvia a causa raiz. SecureBoot tem 1 byte; tentamos 1/8/64
            // porque alguns firmwares exigem folga, e retentamos em 87/122.
            foreach (int tamanho in new[] { 1, 8, 64 })
            {
                try
                {
                    var buf = new byte[tamanho];
                    uint bytesLidos = GetFirmwareEnvironmentVariableW(
                        "SecureBoot", EfiGlobalVariableGuid, buf, (uint)tamanho);
                    if (bytesLidos > 0)
                    {
                        enabled = buf[0] == 1;
                        motivo = "variavel UEFI SecureBoot = " + buf[0];
                        return true;
                    }

                    int err = Marshal.GetLastWin32Error();

                    // 87 = ERROR_INVALID_PARAMETER, 122 = ERROR_INSUFFICIENT_BUFFER
                    if ((err == 87 || err == 122) && tamanho < 64) continue;

                    // 1 = variavel inexistente (Secure Boot desligado de fato)
                    if (err == 1)
                    {
                        enabled = false;
                        motivo = "a variavel UEFI SecureBoot nao existe (Secure Boot desligado ou firmware sem suporte)";
                        return true;
                    }

                    enabled = false;
                    motivo = err switch
                    {
                        5 => "acesso negado (requer Administrador)",
                        // Confirmado em maquina real (probe): sem elevacao a API
                        // devolve 1314 (ERROR_PRIVILEGE_NOT_HELD), nao 5.
                        1314 => "sem privilegio para ler variaveis UEFI (requer Administrador)",
                        1117 => "esta maquina nao expoe a variavel UEFI SecureBoot",
                        _ => $"GetFirmwareEnvironmentVariable falhou (erro {err})"
                    };
                    return false;
                }
                catch (Exception ex)
                {
                    enabled = false;
                    motivo = ex.Message;
                    return false;
                }
            }

            enabled = false;
            motivo = "a variavel UEFI SecureBoot nao pode ser lida mesmo com buffer maior";
            return false;
        }

        private static bool IsSecureBootCheck(ScannableTweak tweak)
            => tweak.Type == TweakType.Registry &&
               tweak.Name.Contains("Secure Boot", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// HVCI e' lido pelo kernel (NtQuerySystemInformation 103), nao pelo
        /// registro — ver o bloco correspondente em CheckTweak.
        /// </summary>
        private static bool IsHvciCheck(ScannableTweak tweak)
            => tweak.Type == TweakType.Registry &&
               (tweak.Name.Contains("HVCI", StringComparison.OrdinalIgnoreCase) ||
                (tweak.KeyPath ?? "").Contains("HypervisorEnforcedCodeIntegrity", StringComparison.OrdinalIgnoreCase));


        private static void CheckTweak(ScannableTweak tweak)
        {
            try
            {
                // Log detalhado apenas se verbosidade estiver ativa
                if (Logger.VerboseCheckLogs)
                {
                    Logger.Log($"[CHECK] Verificando: {tweak.Name} (Tipo: {tweak.Type})");
                }

                // Secure Boot: NUNCA pelo registro (o valor e sobrescrevivel e o
                // proprio Guardian escreve nele). Pela variavel UEFI; se nao der
                // para ler, fica NAO VERIFICAVEL em vez de PROTEGIDO.
                if (IsSecureBootCheck(tweak))
                {
                    if (TryReadSecureBootFirmware(out bool ligado, out string motivo))
                    {
                        tweak.Status = ligado ? TweakStatus.OK : TweakStatus.MODIFIED;
                    }
                    else
                    {
                        if (!_jaAvisouSecureBoot)
                        {
                            _jaAvisouSecureBoot = true;
                            Logger.Log($"[CHECK] Secure Boot nao verificavel: {motivo}. " +
                                       "Marcado como NAO VERIFICAVEL em vez de protegido.");
                        }
                        tweak.Status = TweakStatus.NOT_FOUND;
                    }
                    return;
                }

                // HVCI: NUNCA pelo registro (o DWORD "Enabled" pode ser escrito
                // por qualquer programa e so vale a partir do proximo boot).
                // Usa NtQuerySystemInformation(103), que devolve o bitmask que o
                // KERNEL esta aplicando AGORA (0x400 HVCI impondo / 0x800 so
                // auditoria). Sem isso, "corrigir" deixava o check verde sem
                // provar nada — e em maquina sem HVCI o item ficava acusando
                // para sempre.
                if (IsHvciCheck(tweak))
                {
                    if (NativeCodeIntegrity.TryGetHvci(out bool impoe, out bool audit,
                                                      out bool disponivel, out uint opcoes, out string mot))
                    {
                        // Audit mode registra eventos mas NAO bloqueia: para o
                        //|score de protecao e' o mesmo que desligado.
                        tweak.Status = (impoe && !audit) ? TweakStatus.OK : TweakStatus.MODIFIED;
                        if (Logger.VerboseCheckLogs)
                            Logger.Log($"[CHECK] HVCI em runtime: {NativeCodeIntegrity.Descrever()}");
                    }
                    else
                    {
                        // A API nao respondeu: NAO inventar estado. Fica
                        // NAO VERIFICAVEL e o texto diz que o registro e so a
                        // configuracao pedida.
                        if (!_jaAvisouHvci)
                        {
                            _jaAvisouHvci = true;
                            Logger.Log($"[CHECK] HVCI nao verificavel em runtime: {mot}. " +
                                       "O registro so informa a CONFIGURACAO PEDIDA (vale no " +
                                       "proximo boot e depende de CPU/firmware), entao o item " +
                                       "fica NAO VERIFICAVEL em vez de protegido.");
                        }
                        tweak.Status = TweakStatus.NOT_FOUND;
                    }
                    return;
                }

                if (tweak.Type == TweakType.Registry && string.IsNullOrEmpty(tweak.KeyPath))
                {
                    tweak.Status = TweakStatus.ERROR;
                    return;
                }

                // Para tweaks do Explorer com Category específica, usar apenas verificação de registro
                // CheckExplorerProblems() é muito genérico e detecta problemas não relacionados.
                // Como todos os tweaks do Explorer têm KeyPath/ValueName reais, usamos a lógica normal de registro.
                if (tweak.Type == TweakType.Registry)
                {
                    if (string.IsNullOrEmpty(tweak.KeyPath))
                    {
                        tweak.Status = TweakStatus.ERROR; return;
                    }

                    object? currentValue = null;
                    try
                    {
                        string valName = tweak.ValueName ?? "";

                        if (_currentBatch != null)
                        {
                            var (val, err) = _currentBatch.GetValue(tweak.KeyPath, valName);
                            if (err != null)
                            {
                                if (err is SecurityException se2)
                                {
                                    if (Logger.VerboseCheckLogs)
                                        Logger.Log($"[SECURITY] Acesso negado em '{tweak.Name}': {se2.Message}");
                                    tweak.Status = TweakStatus.NOT_FOUND;
                                    return;
                                }
                                if (err is UnauthorizedAccessException uae2)
                                {
                                    if (Logger.VerboseCheckLogs)
                                        Logger.Log($"[ACCESS] Sem permissão em '{tweak.Name}': {uae2.Message}");
                                    tweak.Status = TweakStatus.NOT_FOUND;
                                    return;
                                }
                                if (err is ArgumentException ae2)
                                {
                                    if (Logger.VerboseCheckLogs)
                                        Logger.Log($"[PATH] Caminho inválido em '{tweak.Name}': {ae2.Message}");
                                    tweak.Status = TweakStatus.NOT_FOUND;
                                    return;
                                }
                                if (err is InvalidCastException ice2)
                                {
                                    Logger.Log($"[ERROR] Falha de conversão em '{tweak.Name}': {ice2.Message}");
                                    tweak.Status = TweakStatus.ERROR;
                                    return;
                                }
                                throw err;
                            }
                            currentValue = val;
                        }
                        else
                        {
                            currentValue = Registry.GetValue(tweak.KeyPath, valName, null);

                            // HKCR fallback: Registry.GetValue pode não funcionar
                            if (currentValue == null && tweak.KeyPath.StartsWith("HKEY_CLASSES_ROOT"))
                            {
                                try
                                {
                                    string checkPath = tweak.KeyPath.Replace(@"HKEY_CLASSES_ROOT\", "");
                                    using var checkKey = Registry.ClassesRoot.OpenSubKey(checkPath);
                                    if (checkKey != null)
                                        currentValue = checkKey.GetValue(valName);
                                }
                                catch { /* ignora */ }
                            }
                        }
                    }
                    catch (System.Security.SecurityException se)
                    {
                        if (Logger.VerboseCheckLogs)
                            Logger.Log($"[SECURITY] Acesso negado em '{tweak.Name}': {se.Message}");
                        tweak.Status = TweakStatus.NOT_FOUND;
                        return;
                    }
                    catch (System.UnauthorizedAccessException uae)
                    {
                        if (Logger.VerboseCheckLogs)
                            Logger.Log($"[ACCESS] Sem permissão em '{tweak.Name}': {uae.Message}");
                        tweak.Status = TweakStatus.NOT_FOUND;
                        return;
                    }
                    catch (System.ArgumentException ae)
                    {
                        if (Logger.VerboseCheckLogs)
                            Logger.Log($"[PATH] Caminho inválido em '{tweak.Name}': {ae.Message}");
                        tweak.Status = TweakStatus.NOT_FOUND;
                        return;
                    }
                    catch (InvalidCastException ice)
                    {
                        Logger.Log($"[ERROR] Falha de conversão em '{tweak.Name}': {ice.Message}");
                        tweak.Status = TweakStatus.ERROR;
                        return;
                    }

                    bool isHarmful;
                    // Detecção especial para PATH - usa verificação dinâmica
                    if (tweak.Name.Contains("PATH") && tweak.ValueName == "Path")
                    {
                        string? pathValue = currentValue?.ToString();
                        if (!string.IsNullOrEmpty(pathValue))
                        {
                            isHarmful = CheckPathProblems(pathValue, tweak.HarmfulValue?.ToString(), tweak.KeyPath);
                        }
                        else
                        {
                            isHarmful = false;
                        }
                    }
                    else if (tweak.Name.Contains("Aliases do PowerShell"))
                    {
                        isHarmful = CheckPowerShellAliases();
                    }
                    else if (tweak.HarmfulValue is string hVal && hVal == "EXISTS")
                    {
                        try
                        {
                            string path = tweak.KeyPath
                                .Replace(@"HKEY_LOCAL_MACHINE\", "")
                                .Replace(@"HKEY_CURRENT_USER\", "")
                                .Replace(@"HKEY_CLASSES_ROOT\", "")
                                .Replace(@"HKLM\", "");
                            RegistryKey baseKey = tweak.KeyPath.StartsWith("HKEY_LOCAL_MACHINE") || tweak.KeyPath.StartsWith("HKLM") ? Registry.LocalMachine : Registry.CurrentUser;
                            using var checkKey = baseKey.OpenSubKey(path);
                            isHarmful = (checkKey != null);
                        }
                        catch
                        {
                            isHarmful = false;
                        }
                    }
                    else if (tweak.HarmfulValue == null)
                    {
                        isHarmful = (currentValue != null);
                    }
                    else
                    {
                        if (currentValue == null)
                        {
                            // AUSENTE nao e o mesmo que PROTEGIDO: o Windows nem
                            // sempre cria o valor de um hardening, e ai o check
                            // ficava verde para sempre sem nunca ter verificado
                            // nada. So o opt-in por tweak muda esse comportamento,
                            // entao o resto do scan se mantem igual.
                            isHarmful = tweak.MissingIsHarmful;
                        }
                        else
                        {
                            // Normalizar HarmfulValue e currentValue para comparação
                            object? nv = RegistryOwnership.NormalizeValue(tweak.HarmfulValue);
                            object? cv = (currentValue is bool) ? RegistryOwnership.NormalizeValue(currentValue) : currentValue;

                            if (cv is int or long && nv is int or long)
                            {
                                long val = Convert.ToInt64(cv);
                                long harm = Convert.ToInt64(nv);
                                isHarmful = (val == harm);
                            }
                            else
                            {
                                // Lógica padrão para outros valores
                                if (cv is string[] cvArray && nv is string[] nvArray)
                                {
                                    isHarmful = cvArray.SequenceEqual(nvArray, StringComparer.OrdinalIgnoreCase);
                                }
                                else
                                {
                                    string cvStr = cv?.ToString() ?? "";
                                    string nvStr = nv?.ToString() ?? "";
                                    isHarmful = cvStr.Equals(nvStr, StringComparison.OrdinalIgnoreCase);
                                }
                            }
                        }
                    }

                    tweak.Status = isHarmful ? TweakStatus.MODIFIED : TweakStatus.OK;
                }
                else if (tweak.Type == TweakType.Service)
                {
                    if (string.IsNullOrEmpty(tweak.ServiceName)) return;

                    try
                    {
                        // Fast path: lê o DWORD Start de HKLM\SYSTEM\CurrentControlSet\Services\<nome>
                        // via RegistryBatch (cache de chaves) - 10-50x mais rápido que ServiceController.
                        string? startMode = null;
                        if (_currentBatch != null)
                        {
                            var (val, err) = _currentBatch.GetValue(
                                $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{tweak.ServiceName}", "Start");
                            if (err == null && val != null)
                                startMode = StartDwordToMode(Convert.ToInt32(val));
                        }
                        if (startMode == null)
                            startMode = ServiceHelper.GetServiceStartMode(tweak.ServiceName);

                        if (startMode == null)
                        {
                            if (Logger.VerboseCheckLogs)
                                Logger.Log($"[CHECK] Serviço {tweak.ServiceName} não encontrado");
                            tweak.Status = TweakStatus.NOT_FOUND;
                            return;
                        }

                        // Serviço desativado de propósito pelo KitLugia (telemetria/diagnóstico/
                        // memory leak) - não é vulnerabilidade e não deve ser "restaurado".
                        if (KitIntentionalServiceStart.TryGetValue(tweak.ServiceName, out var intentionalMode) &&
                            startMode.Equals(intentionalMode, StringComparison.OrdinalIgnoreCase))
                        {
                            tweak.Status = TweakStatus.OK;
                            return;
                        }

                        bool matchesHarmful = startMode.Equals(tweak.HarmfulStartMode, StringComparison.OrdinalIgnoreCase);
                        tweak.Status = matchesHarmful ? TweakStatus.MODIFIED : TweakStatus.OK;

                        // Log apenas se houver problema ou verbosidade ativa
                        if (tweak.Status == TweakStatus.MODIFIED || Logger.VerboseCheckLogs)
                        {
                            Logger.Log($"[CHECK] Serviço {tweak.ServiceName}: {startMode} -> {tweak.Status}");
                        }
                    }
                    // WMI catch removed; ServiceHelper now handles everything via ServiceController
                    catch (Exception ex)
                    {
                        Logger.Log($"[ERROR] Erro crítico no serviço '{tweak.ServiceName}': {ex.Message}");
                        tweak.Status = TweakStatus.ERROR;
                    }
                }
                else if (tweak.Type == TweakType.Mouse)
                {
                    // Lê os três valores para verificação completa da aceleração
                    int speed = 1, threshold1 = 6, threshold2 = 10;
                    try
                    {
                        speed = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseSpeed", 1) as int? ?? 1;
                        threshold1 = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseThreshold1", 6) as int? ?? 6;
                        threshold2 = Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\Mouse", "MouseThreshold2", 10) as int? ?? 10;
                    }
                    catch (System.Security.SecurityException)
                    {
                        // Sem permissão - usa padrão
                    }
                    catch (System.UnauthorizedAccessException)
                    {
                        // Sem permissão - usa padrão
                    }
                    catch (System.ArgumentException)
                    {
                        // Caminho inválido - usa padrão
                    }

                    // Aceleração desativada se todos estiverem em zero (padrão gamer)
                    bool isModified = (speed == 0 && threshold1 == 0 && threshold2 == 0);
                    tweak.Status = isModified ? TweakStatus.MODIFIED : TweakStatus.OK;
                }
                else if (tweak.Type == TweakType.Bcd)
                {
                    if (string.IsNullOrEmpty(tweak.ValueName) || tweak.HarmfulValue == null) return;

                    // Usa o cache compartilhado do scan (1 único bcdedit /enum,
                    // TTL 15s) ou roda pontualmente quando o cache expirou
                    // (toggle individual / uso isolado).
                    string output;
                    if (_bcdEnumAttempted && (DateTime.UtcNow - _bcdEnumTimeUtc).TotalSeconds < BcdEnumCacheTtlSeconds)
                    {
                        if (!string.IsNullOrEmpty(_bcdEnumError))
                        {
                            Logger.Log($"[CHECK] Erro ao obter BCD (cache): {_bcdEnumError}");
                            tweak.Status = TweakStatus.ERROR;
                            return;
                        }
                        output = _bcdEnumOutput ?? "";
                    }
                    else
                    {
                        var (exitCode, outp, error) = ProcessRunner.Run("bcdedit", "/enum", 10000);
                        if (exitCode != 0)
                        {
                            Logger.Log($"[CHECK] Erro ao obter BCD: {error}");
                            tweak.Status = TweakStatus.ERROR;
                            return;
                        }
                        // Alimenta o cache compartilhado (dados antes do flag).
                        _bcdEnumOutput = outp;
                        _bcdEnumError = error;
                        _bcdEnumTimeUtc = DateTime.UtcNow;
                        _bcdEnumAttempted = true;
                        output = outp;
                    }

                    var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    bool isHarmful = false;
                    string? harmfulStr = tweak.HarmfulValue?.ToString();

                    if (harmfulStr != null)
                    {
                        foreach (var line in lines)
                        {
                            var trimmed = line.Trim();
                            if (trimmed.StartsWith(tweak.ValueName, StringComparison.OrdinalIgnoreCase))
                            {
                                // Extrai o valor após o nome do parâmetro (suporta valores com espaços)
                                var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length >= 2)
                                {
                                    string actualValue = string.Join(" ", parts, 1, parts.Length - 1);
                                    if (actualValue.Equals(harmfulStr, StringComparison.OrdinalIgnoreCase))
                                    {
                                        isHarmful = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    tweak.Status = isHarmful ? TweakStatus.MODIFIED : TweakStatus.OK;
                }
                else if (tweak.Type == TweakType.PageFile)
                {
                    tweak.Status = SystemTweaks.IsPageFileDisabled() ? TweakStatus.MODIFIED : TweakStatus.OK;
                }
            }
                catch (Exception ex)
            {
                tweak.Status = TweakStatus.ERROR;
                Logger.Log($"Erro ao verificar tweak '{tweak.Name}': {ex.Message}");
            }
        }

        /// <summary>
        /// Mapeia o DWORD Start de HKLM\SYSTEM\CurrentControlSet\Services\&lt;nome&gt; para o
        /// modo textual usado nos checks (Boot/System/Auto/Manual/Disabled).
        /// </summary>
        private static string? StartDwordToMode(int start)
        {
            return start switch
            {
                0 => "Boot",
                1 => "System",
                2 => "Auto",
                3 => "Manual",
                4 => "Disabled",
                _ => null,
            };
        }

        private static string[] GetAllPowerShellProfiles()
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string ps7Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7");
            string winPsDir = Path.Combine(sysDir, "WindowsPowerShell", "v1.0");
            return new[] {
                // Windows PowerShell 5.1 - CurrentUserCurrentHost
                Path.Combine(docs, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1"),
                // Windows PowerShell 5.1 - CurrentUserAllHosts
                Path.Combine(docs, "WindowsPowerShell", "profile.ps1"),
                // Windows PowerShell 5.1 - AllUsersCurrentHost
                Path.Combine(winPsDir, "Microsoft.PowerShell_profile.ps1"),
                // Windows PowerShell 5.1 - AllUsersAllHosts
                Path.Combine(winPsDir, "profile.ps1"),
                // PowerShell 7 - CurrentUserCurrentHost
                Path.Combine(docs, "PowerShell", "Microsoft.PowerShell_profile.ps1"),
                // PowerShell 7 - CurrentUserAllHosts
                Path.Combine(docs, "PowerShell", "profile.ps1"),
                // PowerShell 7 - AllUsersCurrentHost
                Path.Combine(ps7Dir, "Microsoft.PowerShell_profile.ps1"),
                // PowerShell 7 - AllUsersAllHosts
                Path.Combine(ps7Dir, "profile.ps1")
            };
        }

        private static bool CheckPowerShellAliases()
        {
            foreach (string profile in GetAllPowerShellProfiles())
            {
                if (!File.Exists(profile)) continue;
                foreach (string line in File.ReadAllLines(profile))
                {
                    var m = NativeRegex.Match(line.Trim(), @"Set-Alias\s+-Name\s+(\S+)\s+-Value\s+""([^""]+)""", caseInsensitive: true);
                    if (m.Success)
                    {
                        string target = Environment.ExpandEnvironmentVariables(m.Groups[2]);
                        if (!File.Exists(target)) return true;
                    }
                }
            }
            return false;
        }

        private static void FixPowerShellAliases()
        {
            foreach (string profile in GetAllPowerShellProfiles())
            {
                if (!File.Exists(profile)) continue;
                var lines = File.ReadAllLines(profile);
                var newLines = new List<string>();
                bool changed = false;
                foreach (string line in lines)
                {
                    var m = NativeRegex.Match(line.Trim(), @"Set-Alias\s+-Name\s+(\S+)\s+-Value\s+""([^""]+)""", caseInsensitive: true);
                    if (m.Success)
                    {
                        string target = Environment.ExpandEnvironmentVariables(m.Groups[2]);
                        string aliasName = m.Groups[1];
                        if (!File.Exists(target))
                        {
                            changed = true;
                            newLines.Add($"# {line.Trim()} -- REMOVIDO: alias '{aliasName}' aponta para '{target}' (inexistente)");
                            Logger.Log($"[FIX] Alias '{aliasName}' removido de {profile}: destino '{target}' não existe.");
                            continue;
                        }
                    }
                    newLines.Add(line);
                }
                if (changed) File.WriteAllLines(profile, newLines);
            }
        }

        #endregion

        private sealed class RegistryBatch : IDisposable
        {
            private readonly Dictionary<string, (RegistryKey? Key, Exception? Error)> _cache = new(StringComparer.OrdinalIgnoreCase);

            public (object? Value, Exception? Error) GetValue(string keyPath, string valueName)
            {
                if (!_cache.TryGetValue(keyPath, out var entry))
                {
                    entry = OpenKey(keyPath);
                    _cache[keyPath] = entry;
                }

                if (entry.Error != null)
                    return (null, entry.Error);

                if (entry.Key == null)
                    return (null, null);

                try
                {
                    return (entry.Key.GetValue(valueName), null);
                }
                catch (Exception ex)
                {
                    return (null, ex);
                }
            }

            private static (RegistryKey? Key, Exception? Error) OpenKey(string keyPath)
            {
                try
                {
                    RegistryKey baseKey;
                    string subPath;

                    if (keyPath.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.Ordinal))
                    {
                        baseKey = Registry.LocalMachine;
                        subPath = keyPath[19..];
                    }
                    else if (keyPath.StartsWith("HKLM\\", StringComparison.Ordinal))
                    {
                        baseKey = Registry.LocalMachine;
                        subPath = keyPath[5..];
                    }
                    else if (keyPath.StartsWith("HKEY_CURRENT_USER\\", StringComparison.Ordinal))
                    {
                        baseKey = Registry.CurrentUser;
                        subPath = keyPath[18..];
                    }
                    else if (keyPath.StartsWith("HKCU\\", StringComparison.Ordinal))
                    {
                        baseKey = Registry.CurrentUser;
                        subPath = keyPath[5..];
                    }
                    else if (keyPath.StartsWith("HKEY_CLASSES_ROOT\\", StringComparison.Ordinal))
                    {
                        baseKey = Registry.ClassesRoot;
                        subPath = keyPath[19..];
                    }
                    else if (keyPath.StartsWith("HKCR\\", StringComparison.Ordinal))
                    {
                        baseKey = Registry.ClassesRoot;
                        subPath = keyPath[5..];
                    }
                    else
                    {
                        return (null, new ArgumentException($"Unknown hive prefix in: {keyPath}"));
                    }

                    var key = baseKey.OpenSubKey(subPath);
                    return (key, null);
                }
                catch (SecurityException ex) { return (null, ex); }
                catch (UnauthorizedAccessException ex) { return (null, ex); }
                catch (ArgumentException ex) { return (null, ex); }
            }

            public void Dispose()
            {
                foreach (var (key, _) in _cache.Values)
                    key?.Dispose();
                _cache.Clear();
            }
        }
        /// <summary>
        /// Checks cuja correção foi aplicada mas só vale depois de reiniciar
        /// (HVCI, VBS, pagefile...). Marcados em PENDING (amarelo) em vez de
        /// fingirem estar protegidos. Sem isso, gravar o valor no registro e
        /// mostrar verde na hora era mentira: o Windows só aplica a configuração
        /// no boot seguinte. No próximo boot o Store confere se o efeito veio e,
        /// se não veio, invalida o check em vez de continuar mentindo.
        /// </summary>
        public static int PendingCount => PendingRepairStore.PendingCount;
    }
}
