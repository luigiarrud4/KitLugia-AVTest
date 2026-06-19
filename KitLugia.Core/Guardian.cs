using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;
using System.Security;
using System.Text.RegularExpressions;

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
            Name = "Proteção de Kernel em Tempo Real (Real-time Kernel Protection)",
            Description = "Monitoramento em tempo real de atividades do kernel. Desativar permite que rootkits operem sem detecção, comprometendo performance e estabilidade.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureKernel", ValueName = "Enabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção contra Injeção de DLL",
            Description = "Previne injeção de DLL maliciosa em processos legítimos. Malware pode sequestrar processos do sistema, consumindo recursos e causando lentidão.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager", ValueName = "ProtectionMode", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção de Heap do Sistema",
            Description = "Protege estruturas de heap do sistema contra corrupção. Malware pode exploitar heap para escalar privilégios e degradar performance.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Heap", ValueName = "ProtectionEnabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Validação de Chamadas de Sistema (Syscall Validation)",
            Description = "Valida chamadas de sistema para prevenir abusos. Desativar permite que malware abuse de syscalls para comprometer o sistema e causar lentidão.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SystemCall", ValueName = "ValidationEnabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Isolamento de Processo Crítico (CIS)",
            Description = "Isola processos críticos do sistema. Desativar permite que malware comprometa processos essenciais, causando consumo excessivo de CPU e lentidão generalizada.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CriticalProcessIsolation", ValueName = "Enabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção de Estruturas do Kernel",
            Description = "Protege estruturas internas do kernel contra modificação. Desativar permite que rootkits modifiquem o kernel, causando instabilidade severa e lentidão.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\KernelProtection", ValueName = "Enabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Controle de Integridade de Drivers",
            Description = "Monitora integridade de drivers carregados. Desativar permite drivers maliciosos operem, comprometendo performance e estabilidade do sistema.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DriverIntegrity", ValueName = "MonitoringEnabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção de Pilha de Hardware",
            Description = "Mecanismo de segurança que previne ataques de buffer overflow no nível de hardware. Desativar torna o sistema vulnerável a exploits de memória.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "FeatureSettingsOverrideMask", HarmfulValue = 0, DefaultValue = 3
        },
        new() {
            Name = "Integridade de Memória (HVCI)",
            Description = "Proteção de integridade de código do hypervisor. Desativar expõe o sistema a injeção de kernel e rootkits. Sistemas sem HVCI são suscetíveis a ataques de persistência avançados.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard", ValueName = "HypervisorEnforcedCodeIntegrity", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção contra DMA",
            Description = "Proteção contra ataques Direct Memory Access. Dispositivos maliciosos podem ler/escrever diretamente na memória, comprometendo completamente o sistema e causando lentidão crônica.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard", ValueName = "AllowExternalStorageDevices", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Bloqueio de Driver Vulnerável (Microsoft Blocklist)",
            Description = "Lista atualizada de drivers vulneráveis. Desativar permite instalação de drivers com exploits conhecidos que podem comprometer o kernel, causar BSODs e lentidão extrema.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard", ValueName = "DriverBlocklistEnabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção de Integridade do Sistema (SIP)",
            Description = "Protege arquivos críticos do sistema contra modificação não autorizada. Ransomware pode modificar arquivos do sistema, causando corrupção e lentidão.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemProtection", ValueName = "Enable", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Validação de Assinatura de Código (Code Signing)",
            Description = "Verifica assinaturas digitais de drivers e executáveis. Desativar permite execução de malware assinado digitalmente, comprometendo o sistema e degradando performance.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options", ValueName = "MitigationOptions", HarmfulValue = 0, DefaultValue = 256
        },
        new() {
            Name = "Proteção de Endereço de Retorno (Return Flow Guard)",
            Description = "Mecanismo que protege contra ataques de retorno de função. Desativar permite exploits que podem comprometer a estabilidade do sistema.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "EnableRfg", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Validação de Integridade de Boot",
            Description = "Verifica integridade do processo de boot. Malware pode persistir através de reinicializações, causando lentidão crônica.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot", ValueName = "Verify", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção contra Exploits de Zero-Day",
            Description = "Sistema de proteção contra exploits desconhecidos. Desativar expõe o sistema a vulnerabilidades zero-day que podem causar comprometimento completo e degradação de performance.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ExploitProtection", ValueName = "Enabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Controle de Integridade de Memória Virtual",
            Description = "Protege memória virtual contra corrupção. Desativar permite que malware corrompa estruturas de memória, causando vazamentos e lentidão progressiva.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Memory\Integrity", ValueName = "Enabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Bloqueio de Acesso Remoto ao Registro",
            Description = "Protege o registro contra modificações remotas. Permite que administradores remotos ou malwares modifiquem configurações críticas do sistema remotamente.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecurePipeServers\winreg", ValueName = "RemoteRegAccess", HarmfulValue = 1, DefaultValue = 0
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
            Name = "Política de Bloqueio de Conta",
            Description = "Sem bloqueio de conta após tentativas de login inválidas, atacantes podem tentar senhas indefinidamente (força bruta).",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SECURITY\Policy\PolAccountLockout", ValueName = "LockoutBadCount", HarmfulValue = 0, DefaultValue = 5
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
            Name = "Segurança de Canal Nulo (Null Session)",
            Description = "Sessões nulas permitem que atacantes se conectem ao PC sem autenticação. Desativar essa proteção expõe informações do sistema a qualquer um na rede.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa", ValueName = "restrictanonymous", HarmfulValue = 0, DefaultValue = 1
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
            Name = "Auditoria de Logon (Registro de Tentativas)",
            Description = "Sem auditoria de logon, ataques de força bruta podem passar despercebidos. Impede a detecção de tentativas de invasão ao sistema.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SECURITY\Policy\PolAdtEv", ValueName = "AuditLogonEvents", HarmfulValue = 0, DefaultValue = 3
        },
        new() {
            Name = "Proteção UEFI Secure Boot",
            Description = "Impede que bootkits e rootkits de firmware sejam carregados durante a inicialização. Desativar permite malware persistente de nível de firmware.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot", ValueName = "SecureBootEnabled", HarmfulValue = 0, DefaultValue = 1
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
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Spynet", ValueName = "SubmitSamplesConsent", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção de Rede do Defender",
            Description = "Bloqueia conexões de saída para IPs maliciosos conhecidos. Desativar permite que malware se comunique com servidores de comando e controle.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Features", ValueName = "DisableNetworkProtection", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Proteção contra Exploits do Defender",
            Description = "Proteção integrada contra exploração de vulnerabilidades. Desativar expõe o sistema a ataques de dia zero sem camada extra de defesa.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Features", ValueName = "DisableExploitProtection", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Scan de Arquivos Baixados (Defender)",
            Description = "Verifica arquivos baixados da internet em busca de malware antes de permitir a execução. Desativar permite que downloads maliciosos sejam executados.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Attachments", ValueName = "SaveZoneInformation", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção contra Aplicativos Potencialmente Indesejados (PUP)",
            Description = "Bloqueia instalação de programas que podem não ser maliciosos mas são indesejados (adware, toolbars, cryptominers). Desativar permite que PUP se instalem.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Features", ValueName = "PUAProtection", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção de Acesso a Pastas (Ransomware Protection)",
            Description = "Impede que aplicativos não autorizados modifiquem pastas protegidas. Desativar expõe documentos a ataques de ransomware que criptografam arquivos.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access", ValueName = "EnableControlledFolderAccess", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proteção contra Scripts Maliciosos",
            Description = "Bloqueia scripts PowerShell, VBS e JS maliciosos de executarem. Desativar permite que malwares usem scripts para infectar o sistema sem detecção.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Features", ValueName = "DisableScriptScanning", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Serviço de Segurança do Windows (SecurityHealthService)",
            Description = "Serviço que monitora a saúde geral da segurança. Desativar impede que o sistema alerte sobre firewall, antivírus ou outros problemas de segurança.",
            Category = "Defesa e Antivírus", Type = TweakType.Service, ServiceName = "SecurityHealthService", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Proteção contra Web Threats (Filtro de Rede)",
            Description = "Bloqueia acesso a sites de phishing e malware conhecidos. Desativar permite que o navegador acesse sites maliciosos sem alertas de segurança.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Features", ValueName = "DisableWebProtection", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Scan de Email do Outlook (Defender)",
            Description = "Verifica anexos de email em busca de malware. Desativar permite que malwares escondidos em anexos infectem o sistema.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Features", ValueName = "DisableEmailScanning", HarmfulValue = 1, DefaultValue = 0
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
            Name = "Windows PowerShell (Bloqueado)",
            Description = "Bloqueia a execução do PowerShell. Malware frequentemente desativa o PowerShell para impedir que scripts de remediação sejam executados.",
            Category = "Restrições do Sistema", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "DisallowRun", HarmfulValue = 1, DefaultValue = 0
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
            Name = "Restauração do Sistema (Bloqueada)",
            Description = "Desativa a proteção do sistema e impede a criação de pontos de restauração. Malware frequentemente faz isso para impedir recuperação de danos.",
            Category = "Restrições do Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", ValueName = "DisableSR", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Windows Update (Bloqueado via Política)",
            Description = "Bloqueia completamente o Windows Update via política de grupo. Impede o recebimento de correções críticas de segurança e mantém o sistema vulnerável.",
            Category = "Restrições do Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", ValueName = "NoAutoUpdate", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Remoção de Programas (Bloqueada)",
            Description = "Impede a desinstalação de programas via Painel de Controle. Malware usa isso para impedir que você remova programas indesejados.",
            Category = "Restrições do Sistema", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Policies\Uninstall", ValueName = "NoRemovePages", HarmfulValue = 1, DefaultValue = 0
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
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "BTHSSVC", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Serviço de BIOS do Windows (Wbios)",
            Description = "Fornece informações de firmware e BIOS para o sistema. Desativar pode afetar detecção de hardware e informações do sistema.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "wbiosrvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Serviço de Configuração Automática de WLAN",
            Description = "Gerencia conexões Wi-Fi automaticamente. Desativar impede que o Windows se conecte a redes sem fio automaticamente.",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "Wlansvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
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
            Name = "Throttling de Rede (Configuração Inválida)",
            Description = "Valores incorretos no registro podem limitar a velocidade da internet ou causar instabilidade em jogos online.",
            Category = "Rede e Conectividade", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", ValueName = "NetworkThrottlingIndex", HarmfulValue = unchecked((int)0xFFFFFFFF), DefaultValue = 10
        },
        new() {
            Name = "Web Services on Devices (WSD) Desativado",
            Description = "WSD permite descoberta automática de dispositivos de rede como impressoras e scanners. Desativar impede que o Windows encontre dispositivos na rede.",
            Category = "Rede e Conectividade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", ValueName = "EnableWsd", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "TCP/IP Stack Vulnerability",
            Description = "Vulnerabilidade no TCP/IP que causa alto uso de CPU e perda de pacotes. Afeta taxas de sucesso de conexões TCP e pode causar lentidão extrema na rede.",
            Category = "Rede e Conectividade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", ValueName = "TcpAckFrequency", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "SMBv3 Compression Vulnerability",
            Description = "Vulnerabilidade no protocolo SMBv3 que permite execução remota de código. Desativar proteções expõe o sistema a ataques de rede que podem comprometer performance.",
            Category = "Rede e Conectividade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", ValueName = "EnableCompression", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Windows Network Driver Interface",
            Description = "Vulnerabilidade em drivers de rede. Configurações incorretas podem causar instabilidade na rede e degradação de performance em aplicações de rede.",
            Category = "Rede e Conectividade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", ValueName = "DisableTaskOffload", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "DNS Client Cache Poisoning",
            Description = "Permite envenenamento de cache DNS. Configurações incorretas podem causar lentidão na resolução de nomes e redirecionamento malicioso.",
            Category = "Rede e Conectividade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", ValueName = "CacheHashTableBucketSize", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Firewall Bypass",
            Description = "Permite bypass do firewall. Configurações incorretas podem expor o sistema a ataques de rede que comprometem performance.",
            Category = "Rede e Conectividade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters", ValueName = "EnableFirewall", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Remote Desktop Protocol (RDP) Vulnerability",
            Description = "Vulnerabilidade no RDP. Configurações inseguras podem permitir acesso remoto não autorizado e degradação de performance.",
            Category = "Rede e Conectividade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server", ValueName = "fDenyTSConnections", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "TCP Chimney Offload (Desativado)",
            Description = "Descarrega processamento TCP para a placa de rede. Desativar aumenta uso de CPU e pode reduzir performance de rede em servidores e downloads pesados.",
            Category = "Rede e Conectividade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", ValueName = "EnableTCPChimney", HarmfulValue = 0, DefaultValue = 1
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
            Name = "NetBIOS sobre TCP/IP (Desativado)",
            Description = "Protocolo legado de resolução de nomes. Desativar em ambientes que ainda dependem de NetBIOS pode causar falhas de descoberta de rede.",
            Category = "Rede e Conectividade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\NetBT\Parameters", ValueName = "NetbiosOptions", HarmfulValue = 2, DefaultValue = 0
        },
        new() {
            Name = "MTU (Maximum Transmission Unit) Incorreto",
            Description = "Valor de MTU muito baixo causa fragmentação excessiva e lentidão. Muito alto causa perda de pacotes e retransmissões.",
            Category = "Rede e Conectividade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces", ValueName = "MTU", HarmfulValue = 576, DefaultValue = 1500
        },
        new() {
            Name = "Windows Name Resolution (LLMNR) Desativado",
            Description = "Resolução de nomes local. Desativar pode causar lentidão na descoberta de dispositivos na rede local e compartilhamento de arquivos.",
            Category = "Rede e Conectividade", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LLMNR", ValueName = "EnableLLMNR", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Network Discovery (Descoberta de Rede Desativada)",
            Description = "Impede que o computador veja outros dispositivos na rede e seja visto por eles. Causa problemas de compartilhamento de arquivos e impressoras.",
            Category = "Rede e Conectividade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Network", ValueName = "NetworkDiscoveryEnabled", HarmfulValue = 0, DefaultValue = 1
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
            Name = "Transferência Inteligente (BITS)",
            Description = "Gerencia downloads em segundo plano. Se desativado, Windows Update e outros apps podem falhar ao baixar conteúdo.",
            Category = "Atualizações e Loja", Type = TweakType.Service, ServiceName = "BITS", HarmfulStartMode = "Disabled", DefaultStartMode = "Delayed-Auto"
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
            Category = "Atualizações e Loja", Type = TweakType.Service, ServiceName = "PushNotificationsUserService", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Windows Update (Pausado por Política)",
            Description = "Atualizações pausadas por política de grupo. Impede que correções de segurança críticas sejam instaladas, mesmo via Windows Update manual.",
            Category = "Atualizações e Loja", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings", ValueName = "PauseUpdatesExpiryTime", HarmfulValue = "2099-01-01", DefaultValue = ""
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
        new() {
            Name = "Store Update Auto-Download (Desativado)",
            Description = "Impede que apps da Microsoft Store baixem atualizações automaticamente. Apps podem ficar desatualizados e vulneráveis.",
            Category = "Atualizações e Loja", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsStore", ValueName = "AutoDownload", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Update Driver Exclusion",
            Description = "Impede que drivers importantes sejam atualizados pelo Windows Update. Drivers desatualizados causam instabilidade e problemas de hardware.",
            Category = "Atualizações e Loja", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", ValueName = "ExcludeWUDriversInQualityUpdate", HarmfulValue = 1, DefaultValue = 0
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
            Name = "DisablePagingExecutive (Desativar Paginação de Executáveis)",
            Description = "Impede que drivers e executáveis de sistema sejam paginados para o disco. CAUSA INSTABILIDADE SEVERA, crashes e erros de memória insuficiente em sistemas com uso intenso.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "DisablePagingExecutive", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Hibernação (Fast Startup)",
            Description = "Necessário para a Inicialização Rápida do Windows funcionar. Se desativado, o boot será mais lento.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power", ValueName = "HibernateEnabled", HarmfulValue = 0, DefaultValue = 1
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
            Name = "HPET (High Precision Event Timer) - Desativado",
            Description = "Desativar o HPET completamente pode piorar a precisão dos timers do sistema e causar instabilidade em aplicações que dependem de timing preciso.",
            Category = "Estabilidade", Type = TweakType.Bcd, ValueName = "useplatformclock", HarmfulValue = "No", DefaultValue = "Yes"
        },
        new() {
            Name = "CPU Core Parking (Desativado)",
            Description = "Gerencia núcleos de CPU inativos para economia de energia. Desativar pode aumentar consumo de energia e temperatura sem ganho real de performance.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", ValueName = "CoreParkingDisabled", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "CPU Frequency Scaling (Desativado)",
            Description = "Gerencia frequência da CPU para balancear performance e energia. Desativar mantém CPU sempre na frequência máxima, gerando calor excessivo e redução de vida útil.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", ValueName = "FrequencyScalingDisabled", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Esquema de Energia (Forçado para Alto Desempenho)",
            Description = "Configurar plano de energia forçado para 'Alto Desempenho' permanente. Causa desgaste prematuro da CPU e aumento desnecessário da conta de luz.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Power", ValueName = "UseHighPerformancePowerPlan", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Proteção de Escrever no Cache de Disco (Desativada)",
            Description = "Desativar o flush do cache de disco aumenta risco de perda de dados em queda de energia. Arquivos podem ser corrompidos se o sistema desligar inesperadamente.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "DisableWriteCache", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Limite de Tempo de Desligamento de Serviço",
            Description = "Tempo muito curto para serviços desligarem. Causa 'O Windows está fechando...' infinito e perda de dados não salvos durante desligamento.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", ValueName = "WaitToKillServiceTimeout", HarmfulValue = "1000", DefaultValue = "5000"
        },
        new() {
            Name = "IO Cycle Limit (Limite Excessivo)",
            Description = "Limite de ciclos de E/S muito baixo. Causa gargalos de disco e lentidão em operações de leitura/escrita, afetando boot e carregamento de programas.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\I/O System", ValueName = "IoCycleLimit", HarmfulValue = 0, DefaultValue = 100
        },
        new() {
            Name = "NTFS Memory Usage (Limite Baixo)",
            Description = "Limita a memória que o NTFS pode usar para cache. Causa lentidão em operações de arquivo, especialmente em sistemas com muitos arquivos pequenos.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", ValueName = "NtfsMemoryUsage", HarmfulValue = 1, DefaultValue = 2
        },
        new() {
            Name = "DisableDeleteNotification do NTFS",
            Description = "Desativa notificações de exclusão de arquivos. Pode causar inconsistências em aplicativos que monitoram mudanças no sistema de arquivos.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", ValueName = "DisableDeleteNotification", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "NTFS Short Name Creation (Desativado de Forma Insegura)",
            Description = "Criação de nomes 8.3 para compatibilidade. Desativar completamente causa problemas em aplicativos antigos e ferramentas de backup.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", ValueName = "NtfsDisable8dot3NameCreation", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Limite de Memória do Cache de Arquivos",
            Description = "Limite muito baixo para cache de arquivos. Causa lentidão na reabertura de arquivos recentes e aumento do uso de disco.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "FileCacheLimit", HarmfulValue = 0, DefaultValue = 1024
        },
        new() {
            Name = "Pool Usage Maximum (Pool Usage Max)",
            Description = "Limite máximo de pool de memória muito baixo. Causa falhas de alocação de memória em aplicativos que usam muitos recursos do sistema.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "PoolUsageMaximum", HarmfulValue = 40, DefaultValue = 80
        },
        new() {
            Name = "Sistema de Arquivos (8.3 Name Creation - Comportamento Inconsistente)",
            Description = "Configuração inconsistente de nomes 8.3 entre volumes. Pode causar erros de 'arquivo não encontrado' em aplicativos que dependem de caminhos curtos.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", ValueName = "NtfsDisable8dot3NameCreationOnAllVolumes", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Limite de Memória para Subalocações de Kernel",
            Description = "Limite muito baixo para alocações de kernel. Causa instabilidade em drivers e aplicativos que fazem muitas chamadas de sistema.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "PagedPoolSize", HarmfulValue = 0, DefaultValue = 192
        },

        // ==================================================================================
        // 8. PRIVACIDADE GLOBAL
        // ==================================================================================
        new() {
            Name = "Acesso Global à Câmera (Bloqueado)",
            Description = "Um bloqueio total via registro. Impede que Zoom, Teams, Discord e outros apps usem sua webcam.",
            Category = "Privacidade Global", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam", ValueName = "Value", HarmfulValue = "Deny", DefaultValue = "Allow"
        },
        new() {
            Name = "Acesso Global ao Microfone (Bloqueado)",
            Description = "Um bloqueio total via registro. Nenhum aplicativo conseguirá capturar áudio do seu microfone.",
            Category = "Privacidade Global", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone", ValueName = "Value", HarmfulValue = "Deny", DefaultValue = "Allow"
        },
        new() {
            Name = "Aceleração de Mouse",
            Description = "Verifica se a aceleração do ponteiro está desativada (comum em otimizações gamer) ou no padrão do Windows.",
            Category = "Acessibilidade", Type = TweakType.Mouse
        },
        new() {
            Name = "Acesso Global a Notificações (Bloqueado)",
            Description = "Bloqueia todas as notificações do sistema. Impede que você veja alertas de segurança, mensagens e lembretes.",
            Category = "Privacidade Global", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\notifications", ValueName = "Value", HarmfulValue = "Deny", DefaultValue = "Allow"
        },
        new() {
            Name = "Acesso Global à Localização (Bloqueado)",
            Description = "Bloqueia acesso à localização para todos os aplicativos. Impede uso de Mapas, Clima e serviços baseados em localização.",
            Category = "Privacidade Global", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", ValueName = "Value", HarmfulValue = "Deny", DefaultValue = "Allow"
        },
        new() {
            Name = "Acesso Global a Contatos (Bloqueado)",
            Description = "Bloqueia acesso à lista de contatos. Impede que aplicativos de comunicação como Mail e People funcionem corretamente.",
            Category = "Privacidade Global", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\contacts", ValueName = "Value", HarmfulValue = "Deny", DefaultValue = "Allow"
        },
        new() {
            Name = "Acesso Global a Calendário (Bloqueado)",
            Description = "Bloqueia acesso ao calendário. Impede que o Windows e aplicativos exibam compromissos e lembretes.",
            Category = "Privacidade Global", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\calendar", ValueName = "Value", HarmfulValue = "Deny", DefaultValue = "Allow"
        },
        new() {
            Name = "Acesso Global a Chamadas Telefônicas (Bloqueado)",
            Description = "Bloqueia acesso a chamadas telefônicas em dispositivos com suporte a LTE/5G. Impede que apps de comunicação façam chamadas.",
            Category = "Privacidade Global", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\phoneCall", ValueName = "Value", HarmfulValue = "Deny", DefaultValue = "Allow"
        },
        new() {
            Name = "Acesso Global a Histórico de Chamadas (Bloqueado)",
            Description = "Bloqueia acesso ao histórico de chamadas. Impede que aplicativos exibam logs de chamadas recentes.",
            Category = "Privacidade Global", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\phoneCallHistory", ValueName = "Value", HarmfulValue = "Deny", DefaultValue = "Allow"
        },
        new() {
            Name = "Acesso Global a Email (Bloqueado)",
            Description = "Bloqueia acesso a contas de email. Impede que o aplicativo Mail e outros clientes acessem suas contas de email.",
            Category = "Privacidade Global", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\email", ValueName = "Value", HarmfulValue = "Deny", DefaultValue = "Allow"
        },
        new() {
            Name = "Acesso Global a Tarefas (Bloqueado)",
            Description = "Bloqueia acesso a tarefas. Impede que aplicativos como Microsoft To Do e lembretes funcionem corretamente.",
            Category = "Privacidade Global", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\userNotificationListener", ValueName = "Value", HarmfulValue = "Deny", DefaultValue = "Allow"
        },
        new() {
            Name = "Acesso Global a Rádio (Bloqueado)",
            Description = "Bloqueia controle de rádios (Bluetooth, Wi-Fi). Impede que o sistema gerencie conexões sem fio adequadamente.",
            Category = "Privacidade Global", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\radios", ValueName = "Value", HarmfulValue = "Deny", DefaultValue = "Allow"
        },
        new() {
            Name = "Acesso Global a Dispositivos Bluetooth (Bloqueado)",
            Description = "Bloqueia emparelhamento com dispositivos Bluetooth. Impede conexão com mouses, teclados e fones Bluetooth.",
            Category = "Privacidade Global", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\bluetooth", ValueName = "Value", HarmfulValue = "Deny", DefaultValue = "Allow"
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
            Name = "Último Acesso a Arquivos (NTFS Last Access Time)",
            Description = "Atualiza timestamp de 'último acesso' em arquivos. Causa lentidão em pastas com muitos arquivos devido a escritas extras no disco.",
            Category = "Saúde do Disco", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", ValueName = "NtfsDisableLastAccessUpdate", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Caracteres Estendidos em Nomes 8.3 (Ativado)",
            Description = "Permite caracteres estendidos em nomes de arquivo 8.3. Pode causar problemas de compatibilidade com aplicativos antigos que não suportam Unicode.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", ValueName = "NtfsAllowExtendedCharacterIn8dot3Name", HarmfulValue = 1, DefaultValue = 0
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
            Name = "Recuperação de Disco (Chkdsk Automático Desativado)",
            Description = "Impede que o Windows verifique e corrija erros no disco durante a inicialização. Erros de disco não corrigidos podem levar à perda de dados.",
            Category = "Saúde do Disco", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager", ValueName = "BootExecute", HarmfulValue = "", DefaultValue = "autocheck autochk *"
        },
        new() {
            Name = "Caching de Disco (Write Cache Enabled no Lugar Errado)",
            Description = "Cache de escrita habilitado em disco removível. Pode causar perda de dados se o dispositivo for removido sem ejeção segura.",
            Category = "Saúde do Disco", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Disk", ValueName = "EnableWriteCacheOnRemovable", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Arquivos Offline (Client Side Caching Desativado)",
            Description = "Gerencia arquivos offline para pastas de rede. Desativar impede acesso a arquivos de rede quando desconectado.",
            Category = "Saúde do Disco", Type = TweakType.Service, ServiceName = "CscService", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "S.M.A.R.T. Monitoring (Desativado na BIOS/Registro)",
            Description = "Monitoramento S.M.A.R.T. desativado. Impede alertas precoces de falha iminente do disco rígido/SSD.",
            Category = "Saúde do Disco", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Disk", ValueName = "SMARTEnabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Disk Quota (Cotas de Disco Desabilitadas em Sistema Compartilhado)",
            Description = "Cotas de disco gerenciam espaço por usuário. Desativar permite que um usuário encha todo o disco, afetando todos os outros usuários.",
            Category = "Saúde do Disco", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", ValueName = "NtfsDisableQuota", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Corrupção de Índice de Busca do Windows",
            Description = "Banco de dados de busca corrompido faz o indexador consumir 100% de CPU tentando reconstruir o índice continuamente. Causa lentidão extrema.",
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
        new() {
            Name = "Cache de Miniaturas (Thumbnail Cache Corrompido)",
            Description = "Cache de miniaturas de imagens corrompido. Causa exibição lenta de pastas com imagens e miniaturas em branco ou incorretas.",
            Category = "Saúde do Disco", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", ValueName = "DisableThumbnailCache", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Cache de Ícones (IconCache Corrompido ou Desativado)",
            Description = "Cache de ícones mantém ícones de programas em RAM. Corrupção ou desativação causa ícones em branco e lentidão no carregamento de pastas.",
            Category = "Saúde do Disco", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", ValueName = "Max Cached Icons", HarmfulValue = 0, DefaultValue = 2048
        },
        new() {
            Name = "Arquivos de Log do Windows (Tamanho Ilimitado)",
            Description = "Logs de eventos sem limite de tamanho. Com o tempo, logs podem consumir GBs de espaço em disco e causar lentidão no sistema.",
            Category = "Saúde do Disco", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\EventLog\System", ValueName = "MaxSize", HarmfulValue = -1, DefaultValue = 20971520
        },

        // ==================================================================================
        // 10. CORRUPÇÃO DE SISTEMA E COMPONENTES
        // ==================================================================================
        new() {
            Name = "Windows Modules Installer (Serviço Desativado)",
            Description = "Serviço que instala, modifica e repara componentes do Windows. Desativar impede correção de componentes corrompidos do sistema.",
            Category = "Corrupção de Sistema", Type = TweakType.Service, ServiceName = "TrustedInstaller", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Component-Based Servicing (CBS) Corrompido",
            Description = "Repositório de componentes do Windows corrompido. Causa falhas em atualizações, instalação de novos componentes e reparos do sistema.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing", ValueName = "PackageVersion", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Servicing Stack (Corrompido)",
            Description = "Stack de manutenção do Windows corrompido. Impede que atualizações sejam instaladas, criando um ciclo onde o sistema não consegue se auto-reparar.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Servicing", ValueName = "Version", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Repositório de Drivers (Driver Store Corrompido)",
            Description = "Driver Store é onde drivers são armazenados antes da instalação. Corrupção causa falhas ao instalar novos dispositivos e drivers.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverStore", ValueName = "RepositoryVersion", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "WMI Repository (Corrompido)",
            Description = "Windows Management Instrumentation repository corrompido. Causa falhas em ferramentas de monitoramento, diagnóstico e scripts que dependem de WMI.",
            Category = "Corrupção de Sistema", Type = TweakType.Service, ServiceName = "Winmgmt", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = ".NET Framework NGEN (Corrompido)",
            Description = "Imagens nativas .NET corrompidas. Causa lentidão em aplicativos .NET e falhas de execução com 'System.BadImageFormatException'.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\.NETFramework\NGEN", ValueName = "DisableNativeImageGeneration", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "DirectX Component Registry (Corrompido)",
            Description = "Registro de componentes DirectX corrompido. Causa falhas em jogos e aplicativos gráficos com erros de 'DLL não encontrada' ou 'DirectX Error'.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\DirectX", ValueName = "InstalledVersion", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Visual C++ Redistributable Registry (Corrompido)",
            Description = "Registro de runtime Visual C++ corrompido. Aplicativos que dependem do VC++ podem falhar ao iniciar com erro 'A side-by-side configuration is incorrect'.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\VisualStudio\VC\VCRedist", ValueName = "Installed", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Imaging Component (WIC) Corrompido",
            Description = "Componente de imagem do Windows corrompido. Causa falhas ao abrir imagens, miniaturas em branco e erros em aplicativos de edição de imagem.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\WIC", ValueName = "Version", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Media Foundation Platform (Corrompido)",
            Description = "Plataforma de mídia do Windows corrompida. Causa falhas ao reproduzir áudio/vídeo, codecs quebrados e erros em players de mídia.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Media Foundation", ValueName = "Version", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Print Spooler Registry (Corrompido)",
            Description = "Registro do spooler de impressão corrompido. Causa drivers de impressora corrompidos, trabalhos de impressão presos e erro 'Print Spooler not running'.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Print\Monitors", ValueName = "Version", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Security Center Corrompido",
            Description = "Centro de Segurança do Windows corrompido. Mostra status incorreto de proteção, alertas falsos de antivírus desativado ou não detecta ameaças.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Security Center", ValueName = "ProvidersVersion", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Corrupção de Certificados Raiz",
            Description = "Certificados raiz confiáveis corrompidos. Causa erros de certificado em sites HTTPS, avisos de segurança em navegadores e falhas em atualizações.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\EnterpriseCertificates\Root\Certificates", ValueName = "Version", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Font Registry (Registro de Fontes Corrompido)",
            Description = "Registro de fontes do sistema corrompido. Causa fontes faltando em aplicativos, texto exibido como quadrados e lentidão em aplicativos de design.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", ValueName = "Version", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Performance Counter Registry Corrompido",
            Description = "Registro de contadores de performance corrompido. Causa falhas no Monitor de Recursos, Performance Monitor e ferramentas de diagnóstico.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib", ValueName = "Last Counter", HarmfulValue = 0, DefaultValue = 10000
        },
        new() {
            Name = "Windows Error Reporting Registry Corrompido",
            Description = "Registro de relatório de erros corrompido. Causa looping de erro 'Windows Explorer parou de funcionar', impedindo que relatórios de erro sejam enviados.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Windows Error Reporting", ValueName = "Disabled", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Aplicação de Temas Corrompida",
            Description = "Aplicação de temas visuais corrompida. Causa tema 'Windows Classic' ou tema quebrado, barras pretas e elementos visuais faltando.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes", ValueName = "ThemeVersion", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Shell Experience Host Corrompido",
            Description = "Host de experiência do shell Windows corrompido. Causa falhas no menu Iniciar, barra de tarefas que congela e área de notificação que não responde.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions", ValueName = "Blocked", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Manifesto de Aplicativos Side-by-Side Corrompido",
            Description = "Manifestos de assemblies side-by-side corrompidos. Causa erros 'Activation context generation failed' e aplicativos que não iniciam.",
            Category = "Corrupção de Sistema", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\SideBySide", ValueName = "Version", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Restauração do Sistema (Frequência de Pontos Alterada)",
            Description = "Frequência de criação automática de pontos de restauração. Valor 0 significa 'criar a cada oportunidade' (comportamento padrão no Windows 11).",
            Category = "Backup e Restauração", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", ValueName = "SystemRestorePointCreationFrequency", HarmfulValue = 0, DefaultValue = 1440, IsOptional = true
        },

        // ==================================================================================
        // 11. PERFORMANCE DEGRADADA COM O TEMPO
        // ==================================================================================
        new() {
            Name = "Memória em Pé (Non-Paged Pool Leak)",
            Description = "Vazamento de memória non-paged pool. Causa lentidão progressiva que piora com o tempo de uso, eventualmente travando o sistema com tela azul.",
            Category = "Performance Degradada", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "NonPagedPoolSize", HarmfulValue = 0, DefaultValue = 256
        },
        new() {
            Name = "Memória Paginada (Paged Pool Leak)",
            Description = "Vazamento de memória paged pool. Com o passar dos dias, o sistema fica cada vez mais lento até que aplicativos comecem a falhar com erro 'Out of memory'.",
            Category = "Performance Degradada", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "PagedPoolSize", HarmfulValue = 0, DefaultValue = 192
        },
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
            Name = "Vazamento de Memória do Explorador (Shell)",
            Description = "Explorador de arquivos com memory leak. O uso de RAM do explorer.exe aumenta com o tempo, causando lentidão na interface e necessidade de reiniciar o explorer.",
            Category = "Performance Degradada", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer", ValueName = "ShellState", HarmfulValue = 0, DefaultValue = 55
        },
        new() {
            Name = "Cache de DNS Corrompido com Falta de Memória",
            Description = "Cache DNS corrompido que cresce sem limite. Com o tempo, causa lentidão na resolução de nomes e uso excessivo de RAM pelo svchost.exe do DNS.",
            Category = "Performance Degradada", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", ValueName = "CacheHashTableSize", HarmfulValue = 0, DefaultValue = 512
        },
        new() {
            Name = "Arquivo de Paginação Fragmentado",
            Description = "Page file altamente fragmentado. Causa lentidão geral do sistema em operações de memória virtual, especialmente em sistemas com pouca RAM.",
            Category = "Performance Degradada", Type = TweakType.PageFile, KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "PagingFiles", HarmfulValue = new string[] { "" }, DefaultValue = new string[] { @"C:\pagefile.sys" }
        },
        new() {
            Name = "Driver de Exibição Com Falha (Timeout Detection Recovery)",
            Description = "TDR (Timeout Detection Recovery) com parâmetros inadequados. Causa tela preta por segundos, falhas em jogos e o erro clássico 'Display driver stopped responding'.",
            Category = "Performance Degradada", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", ValueName = "TdrLevel", HarmfulValue = 0, DefaultValue = 3
        },
        new() {
            Name = "Tempo de Resposta do Driver (TDR Delay)",
            Description = "Delay do TDR muito curto. Causa falsos positivos de 'driver travou' em jogos pesados, resultando em crashes e telas pretas.",
            Category = "Performance Degradada", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", ValueName = "TdrDdiDelay", HarmfulValue = 2, DefaultValue = 5
        },
        new() {
            Name = "Memória Compartilhada de GPU (VRAM Falsa)",
            Description = "Quantidade excessiva de memória compartilhada de GPU. Causa uso desnecessário de RAM do sistema para vídeo, reduzindo memória disponível para aplicativos.",
            Category = "Performance Degradada", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000", ValueName = "DedicatedSegmentSize", HarmfulValue = 4096, DefaultValue = 512
        },
        new() {
            Name = "Processos Órfãos (Zombie Processes)",
            Description = "Acúmulo de processos órfãos que não foram finalizados corretamente. Consomem handles, memória e IDs de processo, degradando performance com o tempo.",
            Category = "Performance Degradada", Type = TweakType.Registry, IsOptional = true,
            KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager", ValueName = "PendingFileRenameOperations", HarmfulValue = "EXISTS", DefaultValue = "CLEARED"
        },
        new() {
            Name = "Tabela de Arquivos MTF do NTFS Fragmentada",
            Description = "MFT (Master File Table) altamente fragmentada. Causa lentidão extrema na abertura de pastas com muitos arquivos e na busca por arquivos.",
            Category = "Performance Degradada", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", ValueName = "NtfsMftZoneReservation", HarmfulValue = 1, DefaultValue = 2
        },
        new() {
            Name = "Desfragmentação de Arquivos de Boot (Desativada)",
            Description = "Impede que arquivos de boot sejam desfragmentados. Com o tempo, arquivos de inicialização ficam fragmentados, aumentando o tempo de boot em minutos.",
            Category = "Performance Degradada", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\OptimalLayout", ValueName = "EnableAutoLayout", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Layout de Inicialização (Boot Layout Corrompido)",
            Description = "Layout de inicialização corrompido. Impede que o Windows otimize a posição dos arquivos de boot no disco, resultando em inicialização cada vez mais lenta.",
            Category = "Performance Degradada", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\OptimalLayout", ValueName = "LayoutFilePath", HarmfulValue = "", DefaultValue = @"C:\Windows\Prefetch\Layout.ini"
        },
        new() {
            Name = "Serviço de Diagnóstico de Inicialização",
            Description = "Serviço que diagnostica problemas de inicialização. Desativar impede que o Windows identifique e corrija causas de boot lento.",
            Category = "Performance Degradada", Type = TweakType.Service, ServiceName = "DiagTrack", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Windows System Assessment Tool (WinSAT) Desativado",
            Description = "WinSAT mede performance do sistema para otimizar experiências. Desativar impede que o Windows ajuste configurações para melhor performance.",
            Category = "Performance Degradada", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\WinSAT", ValueName = "EnableWinSAT", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Rastreamento de Eventos (ETW) Corrompido",
            Description = "Event Tracing for Windows corrompido. Causa vazamento de memória em sessões ETW e falhas em ferramentas de diagnóstico como Xperf e WPR.",
            Category = "Performance Degradada", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\WMI\Autologger", ValueName = "Start", HarmfulValue = 0, DefaultValue = 1
        },

        // ==================================================================================
        // 12. INÍCIO E BOOT (PROBLEMAS DE INICIALIZAÇÃO)
        // ==================================================================================
        new() {
            Name = "Boot Configuration Data (BCD) Corrompido",
            Description = "BCD corrompido impede a inicialização do Windows ou causa múltiplas telas azuis durante o boot. É o arquivo que diz ao Windows como iniciar.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "bootstatuspolicy", HarmfulValue = "ignoreallfailures", DefaultValue = "displayallfailures"
        },
        new() {
            Name = "Inicialização Rápida (Fast Startup) Desativada para HD",
            Description = "Fast Startup reduz tempo de boot usando hibernação híbrida. Desativar em HDDs aumenta o tempo de inicialização de 10 segundos para 1-2 minutos.",
            Category = "Boot e Inicialização", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", ValueName = "HiberbootEnabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Tempo Limite de Boot (Baixo)",
            Description = "Tempo limite de boot muito curto em sistemas multi-boot. Pode causar falha ao selecionar o sistema operacional desejado e boot no SO errado.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "timeout", HarmfulValue = "0", DefaultValue = "30"
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
            Name = "Número Máximo de Núcleos no Boot (Limitado)",
            Description = "Limita o número de núcleos de CPU usados durante a inicialização. Pode forçar boot com apenas 1 ou 2 núcleos em sistemas multi-core.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "numproc", HarmfulValue = "1", DefaultValue = ""
        },
        new() {
            Name = "Limite de Memória RAM no Boot",
            Description = "Limita a quantidade de RAM disponível durante o boot. Pode configurar para usar apenas 1GB ou menos, causando instabilidade severa.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "maxmem", HarmfulValue = "1024", DefaultValue = ""
        },
        new() {
            Name = "Boot com PCI Configuration (Forçado)",
            Description = "Força reconfiguração de barramento PCI a cada boot. Causa atraso significativo na inicialização enquanto o sistema reenumera todos os dispositivos PCI.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "pci", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "Boot sem Detecção de PAE (Physical Address Extension)",
            Description = "Desativa PAE durante o boot. Impede que o Windows use mais de 4GB de RAM mesmo em sistemas com 64 bits e muita memória.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "pae", HarmfulValue = "No", DefaultValue = "Yes"
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
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "recoveryenabled", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "Boot com Last Known Good Configuration (Desativado)",
            Description = "Última configuração válida conhecida desativada. Após um boot com falha, o sistema não oferece a opção de restaurar a última configuração que funcionava.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "lastknowngood", HarmfulValue = "No", DefaultValue = "Yes"
        },
        new() {
            Name = "Boot sem WinPE (Windows Preinstallation Environment)",
            Description = "Configuração de BCD para boot como WinPE mesmo em sistema completo. Causa falta de drivers, limitações de recursos e funcionalidades quebradas.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "winpe", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "Boot com Channel (Canal de Depuração IEEE 1394)",
            Description = "Canal de depuração FireWire (IEEE 1394) ativado no boot. Causa delay extra procurando dispositivo FireWire que não existe na inicialização.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "debugchannel", HarmfulValue = "0x1", DefaultValue = ""
        },
        new() {
            Name = "Boot COM Port Debug (Depuração Serial Ativada)",
            Description = "Porta serial de depuração ativada no boot. Causa delay de 5-10 segundos tentando conectar a uma porta serial de depuração inexistente.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "debugport", HarmfulValue = "0x1", DefaultValue = ""
        },
        new() {
            Name = "Boot com Baud Rate Inválido",
            Description = "Taxa de transmissão serial inválida configurada para depuração de boot. Causa erro de configuração e atraso no processo de inicialização.",
            Category = "Boot e Inicialização", Type = TweakType.Bcd, ValueName = "baudrate", HarmfulValue = "115200", DefaultValue = ""
        },

        // ==================================================================================
        // 13. REDE E CONECTIVIDADE (PROBLEMAS ACUMULADOS)
        // ==================================================================================
        new() {
            Name = "Winsock Catalog Corrompido",
            Description = "Catálogo Winsock (LSP) corrompido. Causa impossibilidade de conectar à internet, erro 'No Internet Access' mesmo com rede funcionando e falhas em jogos online.",
            Category = "Rede (Problemas)", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\WinSock2\Parameters", ValueName = "WinsockVersion", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Cache ARP Corrompido",
            Description = "Cache de resolução de endereços (ARP) corrompido. Causa impossibilidade de acessar dispositivos na rede local e lentidão em jogos LAN.",
            Category = "Rede (Problemas)", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", ValueName = "ArpCacheLife", HarmfulValue = 0, DefaultValue = 300
        },
        new() {
            Name = "Windows Filtering Platform (WFP) Corrompida",
            Description = "Plataforma de filtragem do Windows corrompida. Causa falhas em firewalls de terceiros, VPNs que não conectam e política de segurança quebrada.",
            Category = "Rede (Problemas)", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\BFE", ValueName = "Start", HarmfulValue = 4, DefaultValue = 2
        },
        new() {
            Name = "Network Location Awareness (NLA) Corrompido",
            Description = "Serviço de reconhecimento de localização de rede corrompido. Causa perfil de rede incorreto (público em vez de privado) e firewall bloqueando tudo.",
            Category = "Rede (Problemas)", Type = TweakType.Service, ServiceName = "NlaSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "IP Helper (Serviço de Tradução de Endereços IPv6)",
            Description = "Serviço de auxílio IP desativado. Causa falhas em conectividade IPv6, Teredo e DirectAccess, afetando jogos Xbox e Remote Desktop.",
            Category = "Rede (Problemas)", Type = TweakType.Service, ServiceName = "iphlpsvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Proxy Automático (Web Proxy Auto-Discovery Quebrado)",
            Description = "Descoberta automática de proxy quebrada. Causa lentidão extrema ao carregar páginas web, enquanto o Windows tenta encontrar um proxy inexistente.",
            Category = "Rede (Problemas)", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings", ValueName = "AutoDetectProxy", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Proxy Manual Configurado (Em Redes sem Proxy)",
            Description = "Proxy manual configurado em uma rede que não usa proxy. Causa impossibilidade de acessar a internet em redes corporativas ou domésticas.",
            Category = "Rede (Problemas)", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings", ValueName = "ProxyEnable", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "QoS Packet Scheduler (Agendador de Pacotes QoS Desativado)",
            Description = "QoS Packet Scheduler desativado na placa de rede. Causa perda de priorização de tráfego e pode afetar jogos online e chamadas VoIP.",
            Category = "Rede (Problemas)", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\QoS", ValueName = "EnableQoS", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Large Send Offload (LSO) Desativado",
            Description = "Large Send Offload da placa de rede desativado. Causa maior uso de CPU em transferências de rede e redução de velocidade em conexões rápidas.",
            Category = "Rede (Problemas)", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", ValueName = "DisableLargeSendOffload", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Checksum Offload (Desativado)",
            Description = "Checksum offload da placa de rede desativado. A CPU precisa calcular checksums manualmente, reduzindo performance de rede e aumentando uso de CPU.",
            Category = "Rede (Problemas)", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", ValueName = "DisableIPChecksumOffload", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Network Profile Corruption (Perfil de Rede Corrompido)",
            Description = "Perfil de rede salvo corrompido. Causa pedido constante 'Deseja permitir que seu PC seja detectável?' a cada conexão e perda de configurações da rede.",
            Category = "Rede (Problemas)", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\NetworkList\Profiles", ValueName = "Corrupted", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Hosts File (Arquivo de Hosts Corrompido)",
            Description = "Arquivo hosts com entradas maliciosas ou corrompidas. Pode redirecionar sites legítimos para IPs maliciosos ou bloquear completamente acesso a sites.",
            Category = "Rede (Problemas)", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", ValueName = "DataBasePath", HarmfulValue = "", DefaultValue = @"%SystemRoot%\System32\drivers\etc"
        },
        new() {
            Name = "LMHosts File Corrompido",
            Description = "Arquivo LMHosts corrompido. Causa falhas na resolução de nomes NetBIOS em redes locais e problemas para acessar compartilhamentos de rede.",
            Category = "Rede (Problemas)", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\NetBT\Parameters", ValueName = "EnableLMHOSTS", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "BranchCache (Serviço em Estado Inválido)",
            Description = "BranchCache em estado incorreto em redes corporativas. Causa lentidão no acesso a arquivos compartilhados em filiais e tráfego WAN desnecessário.",
            Category = "Rede (Problemas)", Type = TweakType.Service, ServiceName = "PeerDistSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
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
            Name = "Driver Search Order (Ordem de Busca Inválida)",
            Description = "Ordem de busca de drivers inválida. Impede que o Windows encontre drivers corretos para novos dispositivos, resultando em 'Driver not found'.",
            Category = "Driver e Hardware", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Device Installer", ValueName = "SearchOrder", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Code Integrity (Integridade de Código Desativada)",
            Description = "Verificação de integridade de código do kernel desativada. Permite que código malicioso não verificado seja executado no kernel, causando instabilidade.",
            Category = "Driver e Hardware", Type = TweakType.Bcd, ValueName = "nointegritychecks", HarmfulValue = "Yes", DefaultValue = "No"
        },
        new() {
            Name = "PnP Device Enumeration (Enumeração de Hardware Desativada)",
            Description = "Enumeração de dispositivos Plug and Play desativada. Impede que o Windows detecte novo hardware conectado (USB, PCIe, etc).",
            Category = "Driver e Hardware", Type = TweakType.Service, ServiceName = "PlugPlay", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
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
        new() {
            Name = "Driver Maintenance (Manutenção de Driver Desativada)",
            Description = "Tarefa de manutenção de drivers desativada. Drivers problemáticos não são identificados e corrigidos automaticamente, acumulando problemas com o tempo.",
            Category = "Driver e Hardware", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\DriverSearching", ValueName = "DriverMaintenanceDisabled", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Interrupção MSI para Dispositivos de Armazenamento",
            Description = "Message Signaled Interrupts desativado para NVMe/SATA. Causa maior uso de CPU em operações de disco e menor performance de transferência.",
            Category = "Driver e Hardware", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Enum", ValueName = "MSISupported", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Hardware Error Architecture (WHEA) Corrompido",
            Description = "Arquitetura de erro de hardware do Windows corrompida. Impede detecção e registro de falhas de hardware, permitindo que problemas passem despercebidos.",
            Category = "Driver e Hardware", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\WHEA", ValueName = "WHEALogLevel", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "StorPort (Gerenciamento de Armazenamento) Corrompido",
            Description = "Gerenciador de portas de armazenamento corrompido. Causa BSODs com erro STORPORT.sys, perda de dados e falhas em unidades NVMe e SSD.",
            Category = "Driver e Hardware", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\StorPort", ValueName = "Start", HarmfulValue = 4, DefaultValue = 0
        },
        new() {
            Name = "ClassPnp (Classe de Dispositivos PnP) Corrompido",
            Description = "Controlador de classe de dispositivos PnP corrompido. Causa falhas ao enumerar dispositivos USB e erro 'Unknown USB Device (Device Descriptor Request Failed)'.",
            Category = "Driver e Hardware", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\USB", ValueName = "Start", HarmfulValue = 4, DefaultValue = 3
        },

        // ==================================================================================
        // 15. VARIÁVEIS DE AMBIENTE E PATH
        // ==================================================================================
        new() {
            Name = "PATH do Sistema Incompleto",
            Description = "Variável PATH do sistema sem caminhos essenciais do Windows. Causa 'command not found' para ferramentas do sistema.",
            Category = "Variáveis de Ambiente", Type = TweakType.Registry, IsOptional = false,
            KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", ValueName = "Path", HarmfulValue = null, DefaultValue = null
        },
        new() {
            Name = "PATH do Usuário Incompleto",
            Description = "PATH do usuário sem caminhos de ferramentas de desenvolvimento instaladas. Causa falhas ao executar dotnet, git, node, etc.",
            Category = "Variáveis de Ambiente", Type = TweakType.Registry, IsOptional = false,
            KeyPath = @"HKEY_CURRENT_USER\Environment", ValueName = "Path", HarmfulValue = null, DefaultValue = null
        },
        new() {
            Name = "PATH com Entradas Duplicadas",
            Description = "Múltiplas entradas idênticas no PATH. Causa lentidão na busca de executáveis e comportamento imprevisível de qual programa será executado.",
            Category = "Variáveis de Ambiente", Type = TweakType.Registry, IsOptional = true,
            KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", ValueName = "Path", HarmfulValue = null, DefaultValue = null
        },
        new() {
            Name = "PATH com Caminhos Inexistentes",
            Description = "Entradas no PATH que apontam para pastas que não existem. Causa erros 'file not found' e lentidão no sistema.",
            Category = "Variáveis de Ambiente", Type = TweakType.Registry, IsOptional = false,
            KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", ValueName = "Path", HarmfulValue = null, DefaultValue = null
        },
        new() {
            Name = "PATH com Lixo de Desenvolvimento",
            Description = "Caminhos de SDKs internos e ferramentas de build no PATH. Causa poluição e lentidão na busca de executáveis.",
            Category = "Variáveis de Ambiente", Type = TweakType.Registry, IsOptional = true,
            KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", ValueName = "Path", HarmfulValue = null, DefaultValue = null
        },
        new() {
            Name = "PATH Vulnerável a Hijacking",
            Description = "PATH com ordem incorreta que permite hijacking de executáveis. Vulnerabilidade crítica de segurança que permite execução de malware.",
            Category = "Variáveis de Ambiente", Type = TweakType.Registry, IsOptional = false,
            KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", ValueName = "Path", HarmfulValue = null, DefaultValue = null
        },
        new() {
            Name = "TEMP/TMP (Variável de Ambiente Inválida)",
            Description = "Variável TEMP/TMP apontando para diretório inválido ou inexistente. Causa falhas ao instalar programas e erros 'Cannot create temporary file'.",
            Category = "Perfil de Usuário", KeyPath = @"HKEY_CURRENT_USER\Environment", ValueName = "TEMP", HarmfulValue = "", DefaultValue = @"%USERPROFILE%\AppData\Local\Temp"
        },

        // 16. PERFIL DE USUÁRIO
        // ==================================================================================
        new() {
            Name = "Perfil de Usuário Corrompido (NTUSER.DAT)",
            Description = "Arquivo NTUSER.DAT corrompido. Causa configurações que não salvam, tema que volta ao padrão após reinicialização e aplicativos que esquecem preferências.",
            Category = "Perfil de Usuário", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", ValueName = "CorruptedProfile", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "SID de Usuário Duplicado",
            Description = "SID de usuário duplicado no registro. Causa confusão de permissões, acesso negado a pastas pessoais e perfil temporário carregado (Temp Profile).",
            Category = "Perfil de Usuário", Type = TweakType.Registry, IsOptional = true,
            KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", ValueName = "ProfileImagePath", HarmfulValue = "DUPLICATE", DefaultValue = "UNIQUE"
        },
        new() {
            Name = "Caminho de Perfil Inválido",
            Description = "Caminho do diretório de perfil inválido no registro. Causa erro 'User Profile Service service failed to logon' e impossibilidade de fazer login.",
            Category = "Perfil de Usuário", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", ValueName = "ProfileLoadTimeout", HarmfulValue = 60, DefaultValue = 30
        },
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
            Name = "Pasta Meus Documentos Redirecionada para OneDrive (Quebrado)",
            Description = "Documentos redirecionados para OneDrive com sincronização quebrada. Causa perda de arquivos e erros 'File not syncing' constantes.",
            Category = "Perfil de Usuário", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", ValueName = "Documents", HarmfulValue = "", DefaultValue = @"%USERPROFILE%\Documents"
        },
        new() {
            Name = "Variável de Ambiente PATH Corrompida",
            Description = "Variável PATH com entradas inválidas ou mal formatadas. Causa erro 'X is not recognized as an internal or external command' mesmo com programas instalados.",
            Category = "Perfil de Usuário", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Environment", ValueName = "Path", HarmfulValue = "", DefaultValue = @"%SystemRoot%\system32;%SystemRoot%;%SystemRoot%\System32\Wbem;%SYSTEMROOT%\System32\WindowsPowerShell\v1.0\"
        },
        new() {
            Name = "TEMP/TMP (Variável de Ambiente Inválida)",
            Description = "Variável TEMP/TMP apontando para diretório inválido ou inexistente. Causa falhas ao instalar programas e erros 'Cannot create temporary file'.",
            Category = "Perfil de Usuário", KeyPath = @"HKEY_CURRENT_USER\Environment", ValueName = "TEMP", HarmfulValue = "", DefaultValue = @"%USERPROFILE%\AppData\Local\Temp"
        },
        new() {
            Name = "Aliases do PowerShell Quebrados (Comandos Essenciais)",
            Description = "Aliases no perfil do PowerShell apontando para executáveis inexistentes (ex: winget, dotnet). Causa erro 'X is not recognized' mesmo com programas instalados.",
            Category = "Variáveis de Ambiente", Type = TweakType.Registry, IsOptional = false,
            KeyPath = @"HKEY_CURRENT_USER\Environment", ValueName = "PowerShellAliases", HarmfulValue = null, DefaultValue = null
        },
        new() {
            Name = "Arquivo de Hosts Corrompido (DNS Local)",
            Description = @"Arquivo %SystemRoot%\System32\drivers\etc\hosts com entradas corrompidas. Pode redirecionar sites legítimos ou bloquear completamente o acesso.",
            Category = "Perfil de Usuário", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", ValueName = "DataBasePath", HarmfulValue = "", DefaultValue = @"%SystemRoot%\System32\drivers\etc"
        },
        new() {
            Name = "AppData Roaming (Caminho Corrompido)",
            Description = "Caminho AppData Roaming corrompido. Aplicativos perdem configurações de usuário, temas, favoritos e senhas salvas.",
            Category = "Perfil de Usuário", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders", ValueName = "AppData", HarmfulValue = "", DefaultValue = @"%USERPROFILE%\AppData\Roaming"
        },
        new() {
            Name = "Restrições de Armazenamento de Senhas (Credential Manager Corrompido)",
            Description = "Gerenciador de Credenciais corrompido. Causa perda de senhas salvas, pedidos constantes de login e falhas em conexões de rede mapeadas.",
            Category = "Perfil de Usuário", Type = TweakType.Service, ServiceName = "VaultSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Perfil Temporário (Temp Profile) Carregado",
            Description = "Windows carregou um perfil temporário. Alterações feitas durante esta sessão serão perdidas ao deslogar. Forte indicador de corrupção de perfil.",
            Category = "Perfil de Usuário", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList", ValueName = "State", HarmfulValue = 0, DefaultValue = 1
        },

        // ==================================================================================
        // 16b. REGISTRO E CONFIGURAÇÃO CORROMPIDA
        // ==================================================================================
        new() {
            Name = "Registro (Registry Size Limit Excedido)",
            Description = "Tamanho do registro próximo ou excedendo o limite máximo. Causa lentidão geral, falhas ao salvar configurações e erro 'Registry may be corrupted'.",
            Category = "Registro Corrompido", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "RegistryQuota", HarmfulValue = 0, DefaultValue = 262144
        },
        new() {
            Name = "Registry Hive Corrompido (SAM)",
            Description = "Registry hive SAM corrompido. Causa impossibilidade de fazer login, senhas rejeitadas e erro 'The security account manager (SAM) has failed'.",
            Category = "Registro Corrompido", KeyPath = @"HKEY_LOCAL_MACHINE\SAM", ValueName = "Corrupted", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Registry Hive Corrompido (SECURITY)",
            Description = "Registry hive SECURITY corrompido. Causa falhas em auditoria, política de segurança quebrada e permissões de usuário incorretas.",
            Category = "Registro Corrompido", KeyPath = @"HKEY_LOCAL_MACHINE\SECURITY", ValueName = "Corrupted", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Registry Hive Corrompido (SOFTWARE)",
            Description = "Registry hive SOFTWARE corrompido. Causa aplicativos que não instalam, configurações que não salvam e erros de classe não registrada.",
            Category = "Registro Corrompido", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE", ValueName = "Corrupted", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Registry Hive Corrompido (SYSTEM)",
            Description = "Registry hive SYSTEM corrompido. Causa falhas de boot, serviços que não iniciam e devices que não funcionam.",
            Category = "Registro Corrompido", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM", ValueName = "Corrupted", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Entradas de Desinstalação Órfãs",
            Description = "Entradas de desinstalação de programas que não existem mais. Acumulam com o tempo, poluindo a lista de programas instalados e causando erros 'This program does not exist'.",
            Category = "Registro Corrompido", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", ValueName = "OrphanedEntries", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "ActiveX/COM Registration Corrompida",
            Description = "Registro de componentes COM/ActiveX corrompido. Causa erros 'Class not registered' e 'ActiveX component can't create object' em aplicativos.",
            Category = "Registro Corrompido", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID", ValueName = "Corrupted", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "File Association (Associação de Arquivos Corrompida)",
            Description = "Associações de arquivos corrompidas. Causa duplo clique em arquivos abrindo programa errado ou erro 'How do you want to open this file?' mesmo para programas instalados.",
            Category = "Registro Corrompido", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts", ValueName = "Corrupted", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Context Menu Handlers (Corrompidos ou Bloqueados)",
            Description = "Manipuladores de menu de contexto corrompidos. Causa menu de contexto que demora 10-30 segundos para aparecer, explorer travando ao clicar com direito.",
            Category = "Registro Corrompido", KeyPath = @"HKEY_CLASSES_ROOT\*\shellex\ContextMenuHandlers", ValueName = "Corrupted", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Shell Extensions (Extensões de Shell Corrompidas)",
            Description = "Extensões de shell corrompidas que não foram carregadas. Causa explorer.exe que trava, ícones de arquivo em branco e pastas que demoram para abrir.",
            Category = "Registro Corrompido", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked", ValueName = "BlockedExtensions", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Registry ACL (Permissões Corrompidas)",
            Description = "Permissões de acesso ao registro corrompidas ou ausentes. Causa erro 'Cannot open registry key' mesmo sendo administrador.",
            Category = "Registro Corrompido", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Setup", ValueName = "RegistryPermissions", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Installed Components Registry (Órfão)",
            Description = "Componentes instalados registrados no registro mas que não existem mais no disco. Acumulam entradas mortas que deixam o registro inchado e lento.",
            Category = "Registro Corrompido", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData", ValueName = "OrphanedComponents", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Shared DLLs Reference (Referências Órfãs de DLL)",
            Description = "Referências a DLLs compartilhadas que não existem mais. Impede a limpeza segura de DLLs não usadas e incha o registro com entradas mortas.",
            Category = "Registro Corrompido", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", ValueName = "OrphanedRefs", HarmfulValue = 1, DefaultValue = 0
        },

        // ==================================================================================
        // 17. EVENTOS E DIAGNÓSTICO (PROBLEMAS)
        // ==================================================================================
        new() {
            Name = "Log de Eventos do Sistema (Corrompido)",
            Description = "Arquivos .evtx do log de eventos corrompidos. Causa erros 'Windows Event Log is corrupted' e impossibilidade de acessar logs de sistema para diagnóstico.",
            Category = "Eventos e Diagnóstico", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\EventLog\System", ValueName = "File", HarmfulValue = "", DefaultValue = @"%SystemRoot%\System32\winevt\Logs\System.evtx"
        },
        new() {
            Name = "Log de Eventos de Aplicação (Corrompido)",
            Description = "Log de eventos de aplicações corrompido. Impede diagnóstico de falhas de programas e erros de aplicativos específicos.",
            Category = "Eventos e Diagnóstico", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\EventLog\Application", ValueName = "File", HarmfulValue = "", DefaultValue = @"%SystemRoot%\System32\winevt\Logs\Application.evtx"
        },
        new() {
            Name = "Log de Eventos de Segurança (Corrompido)",
            Description = "Log de eventos de segurança corrompido. Causa perda de registros de tentativas de login, alterações de segurança e auditoria.",
            Category = "Eventos e Diagnóstico", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\EventLog\Security", ValueName = "File", HarmfulValue = "", DefaultValue = @"%SystemRoot%\System32\winevt\Logs\Security.evtx"
        },
        new() {
            Name = "Tamanho Máximo de Log Excedido (Sem Rotação)",
            Description = "Log de eventos sem rotação automática. Logs de sistema podem crescer até GBs, consumindo espaço em disco e causando lentidão no acesso aos logs.",
            Category = "Eventos e Diagnóstico", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\EventLog\System", ValueName = "AutoBackupLogFiles", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Depuração do Windows (Crash Dump Desativado)",
            Description = "Criação de crash dump desativada. Quando o sistema trava (BSOD), nenhum dump é gerado, impossibilitando diagnóstico da causa da tela azul.",
            Category = "Eventos e Diagnóstico", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CrashControl", ValueName = "CrashDumpEnabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Mini Dump de Memória (Desativado)",
            Description = "Mini dump de memória desativado. BSODs não geram arquivos .dmp que podem ser analisados para identificar drivers ou componentes com falha.",
            Category = "Eventos e Diagnóstico", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CrashControl", ValueName = "MinidumpsEnabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Dump de Memória Completo (Escrita com Sobrescrita)",
            Description = "Dump completo configurado para sempre sobrescrever. Em sistemas com 32GB+ de RAM, cada BSOD gera um arquivo de 32GB+, enchendo o disco rapidamente.",
            Category = "Eventos e Diagnóstico", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CrashControl", ValueName = "Overwrite", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Reinicialização Automática em Falha (Desativada)",
            Description = "Reinicialização automática em caso de BSOD desativada. O sistema fica travado na tela azul indefinidamente até ser reiniciado manualmente.",
            Category = "Eventos e Diagnóstico", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CrashControl", ValueName = "AutoReboot", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Registro de Eventos de Performance (Desativado)",
            Description = "Diagnóstico de performance desativado. Causa loops de erro 'O Performance Monitor detectou que um serviço de log está consumindo CPU excessivamente'.",
            Category = "Eventos e Diagnóstico", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\PerfProc", ValueName = "Performance Counters Enabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Performance Recorder (WPR) Corrompido",
            Description = "Gravador de performance do Windows corrompido. Causa falhas ao tentar gravar traces de performance para diagnóstico de lentidão.",
            Category = "Eventos e Diagnóstico", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Windows Performance Recorder", ValueName = "Enabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Serviço de Relatório de Erros do Windows (WER) Desativado",
            Description = "Windows Error Reporting desativado. Impede que relatórios de erro sejam enviados para análise, dificultando a correção de problemas recorrentes.",
            Category = "Eventos e Diagnóstico", Type = TweakType.Service, ServiceName = "WerSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Windows Error Reporting (Loop de Crash)",
            Description = "WER em loop devido a aplicativo travando repetidamente. Causa consumo de CPU pelo WerFault.exe e relatórios de erro infinitos.",
            Category = "Eventos e Diagnóstico", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\Windows Error Reporting", ValueName = "ReportQueue", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Arquivos de Log do Windows (Modo Apenas Leitura)",
            Description = "Logs de eventos configurados como somente leitura. Impede que novos eventos sejam registrados, deixando o sistema sem histórico de diagnósticos.",
            Category = "Eventos e Diagnóstico", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\EventLog\System", ValueName = "RestrictGuestAccess", HarmfulValue = 1, DefaultValue = 0
        },

        // ==================================================================================
        // 18. VULNERABILIDADES CRÍTICAS (NOTA: verificações de registro apenas como indicadores;
        // a detecção real de zero-days requer patch management e ferramentas dedicadas)
        // ==================================================================================
        new() {
            Name = "Virtualização Baseada em Segurança (VBS)",
            Description = "VBS pode reduzir performance em jogos. Se desativado, recomenda-se ter um antivírus ativo.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard", ValueName = "EnableVirtualizationBasedSecurity", HarmfulValue = 0, DefaultValue = 1, IsOptional = true
        },
        new() {
            Name = "Desktop Window Manager Information Disclosure - CVE-2026-20805",
            Description = "Vulnerabilidade zero-day no Desktop Window Manager. Permite disclosure de informações de memória localmente, expondo endereços ALPC remotas. Ativamente explorada.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\DWM", ValueName = "Enable", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Graphics Component Elevation of Privilege - CVE-2026-20822",
            Description = "Vulnerabilidade crítica no componente gráfico. Use-after-free que permite escalonamento de privilégios para SYSTEM após race condition.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Graphics", ValueName = "HardwareAcceleration", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "VBS Enclave Elevation of Privilege - CVE-2026-20876",
            Description = "Heap-based buffer overflow no VBS Enclave. Permite ganhar Virtual Trust Level 2 (VTL2), comprometendo virtualização baseada em segurança.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard", ValueName = "HypervisorEnforcedCodeIntegrity", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "LSASS Remote Code Execution - CVE-2026-20854",
            Description = "Vulnerabilidade crítica no LSASS. Use-after-free permite execução remota de código. Compromete autenticação e credenciais do sistema.",
            Category = "Segurança Crítica", Type = TweakType.Service, ServiceName = "LSASS", HarmfulStartMode = "Disabled", DefaultStartMode = "Automatic"
        },
        new() {
            Name = "Secure Boot Certificate Expiration - CVE-2026-21265",
            Description = "Vulnerabilidade na expiração de certificados Secure Boot de 2011. Permite bypass completo do Secure Boot em sistemas não atualizados.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot", ValueName = "SecureBootEnabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows SMB Server Elevation of Privilege - CVE-2026-20919",
            Description = "Vulnerabilidade no servidor SMB. Permite escalonamento de privilégios através do protocolo SMB, comprometendo acesso a recursos de rede compartilhados.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", ValueName = "EnableSMB1Protocol", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Windows URL Parsing Remote Code Execution - CVE-2025-59295",
            Description = "Vulnerabilidade no parsing de URLs do Windows. Permite execução remota de código através de URLs maliciosas construídas para overflow de ponteiros de função.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings", ValueName = "URLSecurity", HarmfulValue = 0, DefaultValue = 1
        },

        // ==================================================================================
        // 19. VULNERABILIDADES ADICIONAIS CONFIRMADAS 2025-2026
        // ==================================================================================
        new() {
            Name = "NTLM Hash Leak Zero-Click - CVE-2026-32202",
            Description = "Vulnerabilidade zero-click no NTLM que vaza hashes de autenticação. Adicionada ao catálogo CISA KEV em abril/2026. Pode ser encadeada com pass-the-hash para comprometimento de domínio.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa\MSV1_0", ValueName = "RestrictSendingNTLMTraffic", HarmfulValue = 0, DefaultValue = 2
        },
        new() {
            Name = "MSHTML Remote Code Execution - CVE-2026-21513",
            Description = "RCE no motor MSHTML (Trident), explorado pelo grupo APT28 em campanhas de spear-phishing contra alvos diplomáticos e de defesa. Atua via renderização de conteúdo malicioso.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Internet Explorer\Main", ValueName = "DisableMSHTML", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Security Feature Bypass (Protected View) - CVE-2026-21509",
            Description = "Bypass de segurança que permite execução de código malicioso mesmo com Protected View ativado no Office. Explorado em campanhas de phishing.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Office\Common\Security", ValueName = "ProtectedView", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Kernel Privilege Escalation - CVE-2025-62215",
            Description = "Vulnerabilidade double-free no kernel Windows. Permite escalonamento de privilégios local. Explorado ativamente após comprometimento inicial via phishing ou RCE.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\kSecPkg", ValueName = "Start", HarmfulValue = 4, DefaultValue = 3
        },
        // ==================================================================================
        // 20. PROBLEMAS DO WINDOWS EXPLORER 2026
        // ==================================================================================
        new() {
            Name = "Associação de Pasta (Folder) Corrompida",
            Description = "O registro HKEY_CLASSES_ROOT\\Folder\\shell\\open\\command está ausente ou corrompido. Causa erro 'Este arquivo não possui um programa associado' ao clicar duas vezes numa pasta.",
            Category = "Corrupção de Sistema", 
            Type = TweakType.Registry,
            KeyPath = @"HKEY_CLASSES_ROOT\Folder\shell\open\command",
            ValueName = "",
            HarmfulValue = "EMPTY_OR_WRONG",
            DefaultValue = "%SystemRoot%\\System32\\explorer.exe"
        },
        new() {
            Name = "Associação de Executável (.exe) Corrompida",
            Description = "A chave HKEY_CLASSES_ROOT\\exefile\\shell\\open\\command pode estar vazia, ausente ou mal configurada. Arquivos .exe não abrem ao dar duplo clique, mas funcionam via CMD.",
            Category = "Corrupção de Sistema",
            Type = TweakType.Registry,
            KeyPath = @"HKEY_CLASSES_ROOT\exefile\shell\open\command",
            ValueName = "",
            HarmfulValue = "EMPTY_OR_WRONG",
            DefaultValue = "\"%1\" %*"
        },
        new() {
            Name = "Extensão de Contexto Bloqueada (CCleaner/Third-Party)",
            Description = "Extensões de menu de contexto (shell extensions) bloqueadas por CCleaner ou ferramentas similares com prefixo '[CC]'. Itens como 7-Zip podem não aparecer.",
            Category = "Defesa e Antivírus",
            Type = TweakType.Registry,
            KeyPath = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked",
            ValueName = "HasCCleanerBlockedEntries",
            HarmfulValue = true,
            DefaultValue = false
        },
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
        new() {
            Name = "Abrir Local do Arquivo Quebrado (diretório shell)",
            Description = "Ação 'Open file location' no menu de contexto abre CMD (ou não faz nada) ao invés de abrir a pasta no Explorer. Chave HKEY_CLASSES_ROOT\\Directory\\shell está corrompida.",
            Category = "Corrupção de Sistema",
            Type = TweakType.Registry,
            KeyPath = @"HKEY_CLASSES_ROOT\Directory\shell",
            ValueName = "",
            HarmfulValue = "EXPLORER_OVERRIDDEN",
            DefaultValue = "none"
        },
        new() {
            Name = "Handler de Menu de Contexto Inválido",
            Description = "ContextMenuHandlers corrompidos ou com GUIDs inválidos. Causa travamento do Explorer ao clicar com botão direito em arquivos ou pastas.",
            Category = "Corrupção de Sistema",
            Type = TweakType.Registry,
            KeyPath = @"HKEY_CLASSES_ROOT\*\shellex\ContextMenuHandlers",
            ValueName = "CorruptedHandlers",
            HarmfulValue = true,
            DefaultValue = false
        },
        new() {
            Name = "Shell Extensions Desativadas em Massa",
            Description = "Muitas extensões de shell desativadas em Disabled registry key. Pode indicar malware ou ferramenta de limpeza agressiva que removeu funcionalidades essenciais.",
            Category = "Corrupção de Sistema",
            Type = TweakType.Registry,
            KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Disabled",
            ValueName = "TooManyDisabled",
            HarmfulValue = true,
            DefaultValue = false
        },
        new() {
            Name = "Microsoft Graphics Component VM Escape - CVE-2025-49708",
            Description = "Vulnerabilidade crítica com CVSS 9.9. Permite escape completo de máquinas virtuais, comprometendo todas as VMs no mesmo host com privilégios SYSTEM.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", ValueName = "TdrLevel", HarmfulValue = 0, DefaultValue = 3
        },
        new() {
            Name = "ASP.NET Security Feature Bypass - CVE-2025-55315",
            Description = "Vulnerabilidade crítica com CVSS 9.9. Permite bypass de controles de segurança através de smuggling de requisições HTTP maliciosas dentro de requisições autenticadas.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\ASP.NET", ValueName = "RequestValidation", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Server Update Service (WSUS) RCE - CVE-2025-59287",
            Description = "Vulnerabilidade crítica com CVSS 9.8. Permite execução remota de código no WSUS, comprometendo sistema de atualizações da rede.",
            Category = "Segurança Crítica", Type = TweakType.Service, ServiceName = "WSUS", HarmfulStartMode = "Disabled", DefaultStartMode = "Automatic"
        },
        new() {
            Name = "Windows NPU Power Management Bug - KB5074109",
            Description = "Bug em laptops com NPU que impede sleep adequado, causando consumo excessivo de bateria e lentidão do sistema devido a throttling térmico.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power", ValueName = "SleepInactivityTimeout", HarmfulValue = 0, DefaultValue = 1800
        },
        new() {
            Name = "Windows Update Boot Failure - KB5074109 Regression",
            Description = "Regressão crítica na atualização KB5074109 causando boot failures com UNMOUNTABLE_BOOT_VOLUME. Requer recuperação manual e pode causar perda de dados.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update", ValueName = "RebootRequired", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "OneDrive/Dropbox Crash Bug - KB5074109 Regression",
            Description = "Bug na atualização KB5074109 causando crashes e não-responsividade no OneDrive e Dropbox, afetando sincronização de arquivos e performance geral.",
            Category = "Estabilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer", ValueName = "CloudStoreSync", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "WinSqlite3.dll Vulnerability - CVE-2025-6965",
            Description = "Vulnerabilidade no componente WinSqlite3.dll. Ferramentas de segurança detectam como vulnerável mesmo em sistemas totalmente atualizados.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel", ValueName = "SqliteSecurity", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows TPM 2.0 Out-of-Bounds Read - CVE-2025-2884",
            Description = "Vulnerabilidade na implementação TPM 2.0. Out-of-bounds read na função CryptHmacSign pode expor dados sensíveis do Trusted Platform Module.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\TPM", ValueName = "TpmSecurity", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Kernel Memory Corruption - CVE-2026-20941",
            Description = "Vulnerabilidade no Host Process for Windows Tasks. Permite escalonamento de privilégios através de corrupção de memória do kernel.",
            Category = "Segurança Crítica", Type = TweakType.Service, ServiceName = "TaskHost", HarmfulStartMode = "Disabled", DefaultStartMode = "Automatic"
        },
        new() {
            Name = "Kernel Streaming Service - CVE-2025-23511",
            Description = "Vulnerabilidade de escalonamento de privilégios no driver ks.sys. Permite que atacante local execute código no kernel com privilégios SYSTEM.",
            Category = "Segurança Crítica", Type = TweakType.Service, ServiceName = "KS", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Windows Kerberos - CVE-2025-23625",
            Description = "Vulnerabilidade de bypass de recurso de segurança no Kerberos. Permite que atacante ignore validação de tickets de autenticação.",
            Category = "Segurança Crítica", Type = TweakType.Service, ServiceName = "Kerberos", HarmfulStartMode = "Disabled", DefaultStartMode = "Demand"
        },
        new() {
            Name = "Windows DNS Server - CVE-2025-23700",
            Description = "Vulnerabilidade de execução remota de código no servidor DNS do Windows. Permite que atacante execute código arbitrário no contexto do serviço DNS.",
            Category = "Segurança Crítica", Type = TweakType.Service, ServiceName = "DNS", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Windows NTLM - CVE-2025-24089",
            Description = "Vulnerabilidade de escalonamento de privilégios no NTLM. Permite que atacante local comprometa o hash NTLM de outro usuário no mesmo sistema.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa\MSV1_0", ValueName = "RestrictSendingNTLMTraffic", HarmfulValue = 0, DefaultValue = 2
        },
        new() {
            Name = "Windows BitLocker - CVE-2025-25076",
            Description = "Vulnerabilidade de bypass de recurso de segurança no BitLocker. Permite que atacante desative proteção de unidade no criptografada.",
            Category = "Segurança Crítica", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\FVE", ValueName = "EnableBDEWithNoTPM", HarmfulValue = 0, DefaultValue = 1
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
            Name = "Net Logon (Serviço de Logon de Rede Desativado)",
            Description = "Serviço de logon de rede desativado. Causa falhas em logon em domínios, problemas de autenticação em redes corporativas.",
            Category = "Rede (Problemas)", Type = TweakType.Service, ServiceName = "Netlogon", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Computer Browser (Serviço de Navegação Desativado)",
            Description = "Serviço de navegação de computadores na rede desativado. Computador não aparece na lista de dispositivos da rede no Explorer.",
            Category = "Rede (Problemas)", Type = TweakType.Service, ServiceName = "Browser", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
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
        new() {
            Name = "Sleep Study (Diagnóstico de Sono Desativado)",
            Description = "Diagnóstico de suspensão desativado. Impede que o Windows analise por que o computador está consumindo bateria durante o sono (Modern Standby).",
            Category = "Energia", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power", ValueName = "SleepStudyEnabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Energy Efficiency (Eficiência Energética de CPU Desativada)",
            Description = "Eficiência energética da CPU desativada. Causa maior consumo de energia e temperatura mais alta sem ganho perceptível de performance.",
            Category = "Energia", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Processor", ValueName = "EnergyEfficiencyEnabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Heterogeneous Policy (Política de CPU Heterogênea Desativada)",
            Description = "Política de escalonamento para CPUs com núcleos big.LITTLE (Intel Hybrid). Desativar causa baixa performance em tarefas leves e maior consumo de bateria.",
            Category = "Energia", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Processor", ValueName = "HeterogeneousPolicy", HarmfulValue = 0, DefaultValue = 5
        },
        new() {
            Name = "Duty Cycling (Ciclagem de Núcleos de CPU Desativada)",
            Description = "Ciclagem de núcleos de CPU desativada. Em CPUs modernas, núcleos ficam ligados desnecessariamente, consumindo mais energia e gerando mais calor.",
            Category = "Energia", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Processor", ValueName = "CPMinCores", HarmfulValue = 100, DefaultValue = 0
        },
        new() {
            Name = "Coalescing Timer (Timer de Coalescência Desativado)",
            Description = "Timer de coalescência de interrupções desativado. Causa maior número de interrupções de timer, aumentando consumo de energia em idle.",
            Category = "Energia", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", ValueName = "CoalescingTimerInterval", HarmfulValue = 0, DefaultValue = 100
        },
        new() {
            Name = "Processor Performance Boost (Boost de CPU Desativado)",
            Description = "Boost de performance da CPU desativado. Impede que a CPU aumente sua frequência temporariamente para tarefas intensivas, causando lentidão.",
            Category = "Energia", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Processor", ValueName = "PerformanceBoostMode", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Prochot (CPU Thermal Throttling Desativado)",
            Description = "Throttling térmico da CPU desativado. CPU pode superaquecer e causar dano permanente ao hardware se não reduzir frequência quando quente.",
            Category = "Energia", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Processor", ValueName = "ProchotDisabled", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Graphics Power Management (Gerenciamento de Energia de GPU Desativado)",
            Description = "Gerenciamento de energia da GPU desativado. GPU fica sempre em alta performance, consumindo mais energia e gerando mais calor mesmo em idle.",
            Category = "Energia", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000", ValueName = "EnableUlps", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Panel Self Refresh (PSR) Desativado",
            Description = "Auto-atualização de painel desativada em laptops. Causa maior consumo de bateria em telas de laptop sem ganho visível de qualidade.",
            Category = "Energia", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Intel\Display", ValueName = "PSREnabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Adaptive Brightness (Brilho Adaptativo Desativado)",
            Description = "Brilho adaptativo de tela desativado. Em laptops, a tela fica sempre no brilho máximo ou manual, sem ajuste automático para economizar bateria.",
            Category = "Energia", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\ContentDeliveryManager", ValueName = "BrightnessSlider", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Battery Saver (Economia de Bateria Desativada)",
            Description = "Economia de bateria desativada. Em laptops, a bateria descarrega mais rápido pois o sistema não reduz automaticamente o consumo quando está em bateria.",
            Category = "Energia", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Power", ValueName = "EnergySaverDisabled", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Battery Charge Limit (Limite de Carga da Bateria Zero)",
            Description = "Limite de carga de bateria zerado. Causa desgaste prematuro da bateria de laptops que ficam sempre conectados à tomada.",
            Category = "Energia", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Battery", ValueName = "ChargeLevelLimit", HarmfulValue = 0, DefaultValue = 80
        },
        new() {
            Name = "Modern Standby (Suspensão Moderna Desativada)",
            Description = "Modern Standby desativado. Em laptops Surface e modernos, o sistema não entra em estado de baixo consumo quando a tela é fechada.",
            Category = "Energia", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power", ValueName = "CsEnabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "USB Selective Suspend (Suspensão Seletiva USB Desativada)",
            Description = "Suspensão seletiva de USB desativada. Dispositivos USB ficam sempre ligados consumindo energia mesmo quando não estão em uso.",
            Category = "Energia", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\USB", ValueName = "DisableSelectiveSuspend", HarmfulValue = 1, DefaultValue = 0
        },

        // ==================================================================================
        // 21. PROBLEMAS DE MEMÓRIA E CACHE
        // ==================================================================================
        new() {
            Name = "Standby List (Lista de Espera de Memória Não Limpa)",
            Description = "Lista de páginas de memória em espera não é limpa adequadamente. Causa lentidão progressiva após horas de uso, mesmo com RAM livre disponível.",
            Category = "Memória", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "ClearStandbyListAtShutdown", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Memory Compression (Compressão de Memória Desativada)",
            Description = "Compressão de memória do Windows desativada. Causa maior uso de páginação em disco quando a RAM está cheia, resultando em lentidão severa.",
            Category = "Memória", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "DisableMemoryCompression", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Working Set Trim (Poda de Working Set Desativada)",
            Description = "Poda automática de working set desativada. Processos acumulam páginas de memória que não usam mais, causando inchaço de memória RAM.",
            Category = "Memória", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "DisablePagedSystemCaching", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Session Pool (Pool de Sessão Pequeno)",
            Description = "Pool de sessão pequeno causa falhas de alocação de memória para sessões de usuário, resultando em aplicativos que não abrem.",
            Category = "Memória", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "SessionPoolSize", HarmfulValue = 0, DefaultValue = 256
        },
        new() {
            Name = "Large Pages (Páginas Grandes de Memória Desabilitadas)",
            Description = "Páginas grandes de memória desabilitadas. Causa performance reduzida em aplicações que se beneficiam de páginas grandes como bancos de dados e VMs.",
            Category = "Memória", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "LargePageMinimum", HarmfulValue = 0, DefaultValue = 1048576
        },
        new() {
            Name = "Physical Address Extension (PAE) Desabilitado",
            Description = "Extensão de endereço físico desabilitada. Impede que sistemas 32-bit usem mais de 4GB de RAM, mesmo com /PAE suportado pelo hardware.",
            Category = "Memória", Type = TweakType.Bcd, ValueName = "pae", HarmfulValue = "No", DefaultValue = "Yes"
        },
        new() {
            Name = "Kernel Memory Dump (Dump de Memória do Kernel Insuficiente)",
            Description = "Dump de memória do kernel muito pequeno. Em caso de BSOD, informações insuficientes são coletadas para diagnóstico da causa.",
            Category = "Memória", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\CrashControl", ValueName = "KernelDumpOnly", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Speculative Store Bypass (SSDB) Mitigation Desativada",
            Description = "Mitigação de segurança SSDB desativada. Expõe o sistema a vulnerabilidades de execução especulativa que podem vazar dados entre processos.",
            Category = "Memória", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "SpeculationControl", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "L1 Terminal Fault (L1TF) Mitigation Desativada",
            Description = "Mitigação L1 Terminal Fault desativada. Expõe o sistema a vulnerabilidade que permite leitura de dados protegidos em cache L1 da CPU.",
            Category = "Memória", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "L1TFMitigation", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Meltdown/L1TF (CVE-2018-3620) Mitigation Desativada",
            Description = "Mitigação Meltdown/L1TF para kernels Intel desativada. Permite que processos não privilegiados leiam memória do kernel em CPUs vulneráveis.",
            Category = "Memória", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "MeltdownMitigation", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Memória do Sistema para Drivers (Limitada)",
            Description = "Limite máximo de memória para drivers muito baixo. Drivers podem falhar ao alocar memória, causando telas azuis e falhas de hardware.",
            Category = "Memória", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "MaximumMemoryAllocation", HarmfulValue = 0, DefaultValue = 4096
        },
        new() {
            Name = "Kernel Memory Protection (Proteção de Memória do Kernel Desativada)",
            Description = "Proteção de memória do kernel desativada. Permite que drivers modifiquem estruturas protegidas do kernel, causando instabilidade e crashes.",
            Category = "Memória", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "EnforceWriteProtection", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Data Execution Prevention (DEP) no Kernel Desativada",
            Description = "Proteção de execução de dados no kernel desativada. Permite execução de código em páginas de dados do kernel, expondo a ataques de buffer overflow.",
            Category = "Memória", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "KernelDEP", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Extended Process (ASLR Forçado para Alta Densidade)",
            Description = "ASLR (Address Space Layout Randomization) forçado para máxima randomização. Causa consumo excessivo de memória em aplicativos sem benefício de segurança adicional.",
            Category = "Memória", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "MoveImages", HarmfulValue = 1, DefaultValue = 0
        },

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
            Name = "NetBIOS sobre TCP/IP (Configuração Insegura)",
            Description = "NetBIOS sobre TCP/IP ativado em redes inseguras. Expõe informações do computador (nome, usuários, compartilhamentos) para qualquer um na rede.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\NetBT\Parameters", ValueName = "TransportBindName", HarmfulValue = "", DefaultValue = @"\Device\"
        },
        new() {
            Name = "LLMNR (Link-Local Multicast Name Resolution) Ativado)",
            Description = "LLMNR é um protocolo de resolução de nomes inseguro. Permite ataques de envenenamento de resposta onde um atacante na rede pode interceptar tráfego.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\LLMNR", ValueName = "EnableMulticast", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "mDNS (Multicast DNS) Ativado em Rede Corporativa",
            Description = "mDNS ativado em rede corporativa pode vazar informações de dispositivos e causar tráfego de rede desnecessário.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", ValueName = "EnableMdns", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "WPAD (Web Proxy Auto-Discovery) Ativado",
            Description = "WPAD ativado permite que atacantes na rede configurem um proxy malicioso através de resposta WPAD falsa. Causa interceptação de tráfego web.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Internet Settings\Wpad", ValueName = "WpadOverride", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Firewall Rule para RDP (3389) Aberto Globalmente",
            Description = "Regra de firewall permitindo RDP de qualquer IP. Expõe a porta 3389 para toda a internet, permitindo ataques de força bruta ao RDP.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules", ValueName = "RemoteDesktop-In-TCP", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Firewall regras de Bloqueio de Entrada Desativadas",
            Description = "Todas as regras de bloqueio de entrada do firewall desativadas. Qualquer serviço no computador fica acessível da internet sem proteção.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile", ValueName = "EnableFirewall", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Firewall Default Inbound Action (Ação Padrão Permitir)",
            Description = "Ação padrão do firewall para conexões de entrada é 'Permitir'. Qualquer conexão não solicitada da internet é aceita por padrão.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile", ValueName = "DefaultInboundAction", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Firewall Logging Desativado",
            Description = "Log de firewall desativado. Conexões bloqueadas não são registradas, impedindo detecção de tentativas de invasão e varreduras de porta.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile\Logging", ValueName = "LogEnabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Defender Firewall Regra para Compartilhamento de Arquivos",
            Description = "Compartilhamento de arquivos e impressoras (NetBIOS, SMB) exposto em perfil público. Dispositivos na mesma rede Wi-Fi pública podem acessar seus arquivos.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile\AuthorizedApplications", ValueName = "List", HarmfulValue = 1, DefaultValue = 0
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
            Name = "Restauração do Sistema (Tamanho Mínimo de Disco)",
            Description = "Espaço em disco alocado para Restauração do Sistema muito pequeno (< 1%). Pontos de restauração são sobrescritos rapidamente, perdendo histórico.",
            Category = "Backup e Restauração", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", ValueName = "DiskPercent", HarmfulValue = 0, DefaultValue = 5
        },
        new() {
            Name = "Restauração do Sistema (Intervalo entre Pontos)",
            Description = "Intervalo muito longo entre pontos de restauração. Pontos são criados a cada 7+ dias, deixando grandes lacunas sem proteção.",
            Category = "Backup e Restauração", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore", ValueName = "RPGlobalInterval", HarmfulValue = 604800, DefaultValue = 86400
        },
        new() {
            Name = "File History (Histórico de Arquivos Desativado)",
            Description = "Histórico de arquivos desativado. Versões anteriores de documentos não são salvas, impossibilitando recuperação de arquivos sobrescritos ou deletados.",
            Category = "Backup e Restauração", Type = TweakType.Service, ServiceName = "fhsvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "File History (Backup para Unidade de Rede Desconectada)",
            Description = "Histórico de arquivos configurado para unidade de rede que não está mais disponível. Backups falham silenciosamente sem notificação ao usuário.",
            Category = "Backup e Restauração", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\FileHistory", ValueName = "TargetUrl", HarmfulValue = "", DefaultValue = @"C:\FileHistory"
        },
        new() {
            Name = "Volume Shadow Copy (Cópias de Sombra Excluídas)",
            Description = "Cópias de sombra (snapshots) de volume foram excluídas ou não estão sendo criadas. Nenhuma versão anterior de arquivos está disponível.",
            Category = "Backup e Restauração", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\BackupRestore\FilesNotToSnapshot", ValueName = "Version", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Windows Backup (Backup do Windows Desativado)",
            Description = "Serviço de backup do Windows desativado. Backups agendados de arquivos e imagem do sistema não são executados.",
            Category = "Backup e Restauração", Type = TweakType.Service, ServiceName = "SDRSVC", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        // Removido: era duplicata do check "Volume Shadow Copy" e usava a chave errada (NtfsDisableLastAccessUpdate)
        new() {
            Name = "Windows Recovery Environment (WinRE) Desativado",
            Description = "Ambiente de Recuperação do Windows desativado. Impede inicialização em modo de recuperação para reparar o sistema quando ele não inicia.",
            Category = "Backup e Restauração", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\RecoveryEnvironment", ValueName = "Enabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Custom Recovery Image (Imagem de Recuperação Customizada Inválida)",
            Description = "Imagem de recuperação personalizada configurada mas inválida ou ausente. O Windows não consegue usar a recuperação em caso de falha de boot.",
            Category = "Backup e Restauração", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Recovery", ValueName = "CustomImagePath", HarmfulValue = "", DefaultValue = ""
        },

        // ==================================================================================
        // 24. PROBLEMAS DE WINDOWS DEFENDER E SEGURANÇA ADICIONAIS
        // ==================================================================================
        new() {
            Name = "Windows Defender Scheduled Scan (Varredura Agendada Desativada)",
            Description = "Varredura antivírus agendada desativada. O sistema não é verificado periodicamente em busca de malware em segundo plano.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Scan", ValueName = "DisableScheduledScanning", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Windows Defender MAPS (Microsoft Active Protection Service Desativado)",
            Description = "Proteção ativa MAPS desativada. O Windows Defender não recebe informações em tempo real sobre novas ameaças detectadas em outros computadores.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Spynet", ValueName = "LocalSettingOverrideSpynet", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Defender Cloud Protection Level (Nível de Proteção Reduzido)",
            Description = "Nível de proteção em nuvem do Defender reduzido. Bloqueio de novos malwares é mais lento pois a verificação na nuvem não é feita.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\MpEngine", ValueName = "MpCloudBlockLevel", HarmfulValue = 0, DefaultValue = 2
        },
        new() {
            Name = "Windows Defender PUA (PUAProtection Desativada)",
            Description = "Proteção contra aplicativos potencialmente indesejados do Defender desativada. Adwares, cryptominers e bundlers podem ser instalados sem alerta.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\PUA", ValueName = "PUAEnabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Defender Low Priority Scan",
            Description = "Varredura do Defender configurada para prioridade baixa. A varredura pode não ser concluída se o sistema estiver ocupado, deixando malwares não detectados.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Scan", ValueName = "ScanPriority", HarmfulValue = 0, DefaultValue = 1
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
            Name = "Windows Defender ASR (Attack Surface Reduction) Desativado",
            Description = "Regras de redução de superfície de ataque desativadas. Proteções contra macros do Office, scripts e comportamentos suspeitos não são aplicadas.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\ASR", ValueName = "ExploitGuard_ASR_Rules", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Windows Defender Network Protection (Proteção de Rede em Modo Apenas Auditoria)",
            Description = "Proteção de rede do Defender configurada apenas para auditoria. Conexões a IPs maliciosos são registradas mas não bloqueadas.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Features", ValueName = "NetworkProtectionMode", HarmfulValue = 1, DefaultValue = 2
        },
        new() {
            Name = "Windows Defender Quarantine (Arquivos em Quarentena não Removidos)",
            Description = "Arquivos em quarentena do Defender não são removidos automaticamente. Podem acumular GBs de espaço em disco na pasta Quarantine.",
            Category = "Defesa e Antivírus", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Quarantine", ValueName = "PurgeItemsAfterDelay", HarmfulValue = 0, DefaultValue = 30
        },

        // ==================================================================================
        // 25. PROBLEMAS DE INICIALIZAÇÃO DE APLICATIVOS
        // ==================================================================================
        new() {
            Name = "Inicialização de Aplicativos (Delay de Boot)",
            Description = "Atraso de inicialização de aplicativos via registro. Causa delay de 10-30 segundos após o boot para que programas da inicialização comecem a carregar.",
            Category = "Inicialização", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", ValueName = "StartupDelayInMSec", HarmfulValue = 120000, DefaultValue = 0
        },
        new() {
            Name = "RunOnce Excedente (Acúmulo de Execuções Únicas)",
            Description = "Entradas RunOnce que falharam e nunca foram removidas. Acumulam com o tempo e tentam executar programas inexistentes a cada boot, causando lentidão.",
            Category = "Inicialização", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", ValueName = "StaleEntries", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Run Keys Corrompidas (Entradas Inválidas de Inicialização)",
            Description = "Entradas Run no registro que apontam para programas que não existem mais. Causam erro 'Windows cannot find...' a cada boot e retardam a inicialização.",
            Category = "Inicialização", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "InvalidEntries", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Run Keys com Caminhos UNC (Inicialização Lenta)",
            Description = "Entradas Run apontando para caminhos de rede UNC. O Windows aguarda o time-out de rede antes de continuar o boot, causando delay de 30-60 segundos.",
            Category = "Inicialização", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "NetworkTimeout", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Run (HKEY_CURRENT_USER) Com Mais de 10 Entradas",
            Description = "Muitos programas na inicialização do usuário. Cada entrada adiciona tempo ao boot e consome recursos em segundo plano permanentemente.",
            Category = "Inicialização", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "Count", HarmfulValue = 10, DefaultValue = 0
        },
        new() {
            Name = "Run (HKEY_LOCAL_MACHINE) Com Mais de 10 Entradas",
            Description = "Muitos programas na inicialização do sistema. Acumulam com instalações e desinstalações incompletas, degradando performance de boot com o tempo.",
            Category = "Inicialização", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", ValueName = "Count", HarmfulValue = 10, DefaultValue = 0
        },
        new() {
            Name = "Serviço de Inicialização Automática (Serviços com Start=Auto mas Parados)",
            Description = "Serviços configurados para iniciar automaticamente mas que falharam e estão parados. Tentativas constantes de reinicialização consomem CPU e memória.",
            Category = "Inicialização", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services", ValueName = "AutoStartFailed", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Shell do Windows (Explorer.exe) Substituído",
            Description = "Shell do Windows substituído por programa diferente no registro. Pode ser malware tentando sequestrar a interface do Windows ou configuração incorreta.",
            Category = "Inicialização", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", ValueName = "Shell", HarmfulValue = "", DefaultValue = "explorer.exe"
        },
        new() {
            Name = "Userinit (Inicialização de Usuário) Substituído",
            Description = "Programa de inicialização de usuário substituído. Pode ser malware carregado antes do Explorer, dificultando remoção.",
            Category = "Inicialização", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon", ValueName = "Userinit", HarmfulValue = "", DefaultValue = @"C:\Windows\system32\userinit.exe,"
        },
        new() {
            Name = "Notificação de Logon (AppInit_DLLs) Ativada",
            Description = "AppInit_DLLs carrega DLLs em todos os processos que carregam user32.dll. Causa lentidão em todos os aplicativos e pode ser usado por malware para se injetar em tudo.",
            Category = "Inicialização", Type = TweakType.Registry, IsOptional = true,
            KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", ValueName = "AppInit_DLLs", HarmfulValue = "LOADED", DefaultValue = "EMPTY"
        },
        new() {
            Name = "LoadAppInit_DLLs (Carregamento de DLLs via AppInit)",
            Description = "Permite que DLLs AppInit sejam carregadas em todos os processos. Causa lentidão generalizada e instabilidade em aplicativos.",
            Category = "Inicialização", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows", ValueName = "LoadAppInit_DLLs", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "GPO (Group Policy Objects) com Erro de Aplicação",
            Description = "Políticas de grupo com falha de aplicação. Cada boot o Windows tenta aplicar políticas corrompidas, causando delay de 30 segundos a vários minutos.",
            Category = "Inicialização", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\History", ValueName = "FailedPolicyCount", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Logon Script Timout (Scripts de Logon Pendentes)",
            Description = "Scripts de logon com timeout excessivo. Causa tela preta por minutos após digitar a senha enquanto scripts de logon falham ou demoram.",
            Category = "Inicialização", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "LogonScriptTimeout", HarmfulValue = 600, DefaultValue = 30
        },
        new() {
            Name = "Inicialização de Aplicativos via Scheduled Tasks (Acumuladas)",
            Description = "Tarefas agendadas na inicialização que executam aplicativos. Acumulam com o tempo, cada uma adicionando delay ao boot e consumindo recursos.",
            Category = "Inicialização", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule", ValueName = "TaskCache", HarmfulValue = 1, DefaultValue = 0
        },

        // ==================================================================================
        // 26. PROBLEMAS DE COMPATIBILIDADE E APLICATIVOS
        // ==================================================================================
        new() {
            Name = "Application Compatibility Cache (Cache de Compatibilidade Corrompido)",
            Description = "Cache de compatibilidade de aplicativos (ShimCache) corrompido. Causa lentidão na primeira execução de aplicativos e uso excessivo de CPU.",
            Category = "Compatibilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\AppCompatibility", ValueName = "AppCompatCache", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Program Compatibility Assistant (PCA) Desativado",
            Description = "Assistente de compatibilidade de programas desativado. Aplicativos com problemas de compatibilidade não são detectados e corrigidos automaticamente.",
            Category = "Compatibilidade", Type = TweakType.Service, ServiceName = "PcaSvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
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
            Name = "Windows Image Acquisition (WIA) Desativado",
            Description = "Serviço de aquisição de imagem desativado. Scanners e câmeras não são detectados pelo Windows, causando erro 'Scanner not found'.",
            Category = "Compatibilidade", Type = TweakType.Service, ServiceName = "stisvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
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
            Name = "Windows Location Service (LFS) Desativado",
            Description = "Serviço de localização desativado. Aplicativos de mapa, clima e serviços baseados em localização não conseguem determinar sua posição.",
            Category = "Compatibilidade", Type = TweakType.Service, ServiceName = "lfsvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
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
            Category = "Compatibilidade", Type = TweakType.Service, ServiceName = "SyncHost", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "GameDVR/Game Bar (Desativado mas Jogos com Performance Reduzida)",
            Description = "Game Bar/DVR pode reduzir performance em jogos. Mas desativá-lo via registro não beneficia e pode quebrar recursos de captura de tela.",
            Category = "Compatibilidade", KeyPath = @"HKEY_CURRENT_USER\System\GameConfigStore", ValueName = "GameDVR_Enabled", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Hardware Accelerated GPU Scheduling (HAGS) Desativado",
            Description = "Agendamento de GPU acelerado por hardware desativado. Causa maior latência em jogos e menor performance em GPUs modernas.",
            Category = "Compatibilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", ValueName = "HwSchMode", HarmfulValue = 0, DefaultValue = 2
        },
        new() {
            Name = "Variable Refresh Rate (VRR/G-Sync/FreeSync) Desativado",
            Description = "Taxa de atualização variável desativada. Causa screen tearing e menor suavidade em jogos em monitores compatíveis.",
            Category = "Compatibilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\GraphicsDrivers", ValueName = "VariableRefreshRateDisabled", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "WDDM Driver Model (Driver Desatualizado ou Corrompido)",
            Description = "Driver de vídeo WDDM (Windows Display Driver Model) desatualizado ou corrompido. Causa telas azuis, crashes em jogos e baixa performance gráfica.",
            Category = "Compatibilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000", ValueName = "DriverVersion", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "OpenGL ICD (Driver OpenGL Desatualizado)",
            Description = "Driver OpenGL desatualizado ou corrompido. Aplicativos que usam OpenGL (CAD, emuladores, jogos) podem falhar ou ter baixa performance.",
            Category = "Compatibilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\OpenGL", ValueName = "ICDVersion", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Vulkan Driver (Driver Vulkan Desatualizado)",
            Description = "Driver Vulkan desatualizado ou corrompido. Jogos e aplicativos que usam Vulkan podem não iniciar ou ter performance reduzida.",
            Category = "Compatibilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Khronos\Vulkan", ValueName = "DriverVersion", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "NVIDIA/AMD Profile Corruption (Perfil de GPU Corrompido)",
            Description = "Perfil de driver de GPU NVIDIA/AMD corrompido. Causa configurações de jogo perdidas, performance abaixo do esperado e crashes em aplicativos 3D.",
            Category = "Compatibilidade", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\NVIDIA Corporation\Global", ValueName = "ProfileCorrupted", HarmfulValue = 1, DefaultValue = 0
        },

        // ==================================================================================
        // 27. PROBLEMAS DE SEGURANÇA DE REDE AVANÇADOS
        // ==================================================================================
        new() {
            Name = "SMB Signing (Assinatura SMB Desativada)",
            Description = "Assinatura de pacotes SMB desativada. Permite ataques Man-in-the-Middle em compartilhamentos de rede, onde atacantes podem modificar dados em trânsito.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", ValueName = "EnableSecuritySignature", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "SMB Encryption (Criptografia SMB Desativada)",
            Description = "Criptografia SBM desativada. Dados transferidos em compartilhamentos de rede não são criptografados, permitindo interceptação por sniffers na rede.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", ValueName = "EncryptData", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "LDAP Signing (Assinatura LDAP Desativada)",
            Description = "Assinatura de consultas LDAP desativada em domínios. Permite que atacantes modifiquem consultas LDAP em trânsito em redes corporativas.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\NTDS\Parameters", ValueName = "LDAPServerIntegrity", HarmfulValue = 0, DefaultValue = 2
        },
        new() {
            Name = "SMB Guest Fallback (Fallback para Convidado Habilitado)",
            Description = "Fallback para acesso SMB como convidado quando credenciais falham. Permite que atacantes acessem compartilhamentos sem autenticação válida.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\LanmanServer\Parameters", ValueName = "AllowGuestAccess", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Anonymous Enumeration of SAM Accounts",
            Description = "Enumeração anônima de contas SAM permitida. Atacantes podem listar todos os usuários do sistema sem autenticação, facilitando ataques direcionados.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Lsa", ValueName = "RestrictAnonymous", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Remote Desktop (RDP) Network Level Authentication Desativada",
            Description = "Autenticação de nível de rede (NLA) desativada para RDP. Permite que atacantes iniciem sessão RDP antes da autenticação, consumindo recursos e testando credenciais.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp", ValueName = "UserAuthentication", HarmfulValue = 0, DefaultValue = 1
        },
        new() {
            Name = "Remote Desktop (RDP) Desativado no Firewall",
            Description = "RDP sem proteção de firewall. Atacantes podem escanear e tentar força bruta na porta RDP. O firewall deve bloquear RDP em perfis públicos.",
            Category = "Segurança de Rede", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\PublicProfile\GloballyOpenPorts", ValueName = "3389:TCP", HarmfulValue = "3389:TCP:*:Enabled", DefaultValue = ""
        },
        new() {
            Name = "Serviço de Compartilhamento de Porta Net.Tcp",
            Description = "Gerencia compartilhamento de portas TCP. Desativar impede que aplicativos .NET compartilhem portas via WCF. O padrão do Windows é Manual (iniciado sob demanda).",
            Category = "Serviços Essenciais", Type = TweakType.Service, ServiceName = "NetTcpPortSharing", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
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
            Name = "Gerenciador de Tarefas Lento / Travado em Segundo Plano",
            Description = "Corrige o bug do Windows 11 onde o Gerenciador de Tarefas continua rodando em segundo plano após fechar ou trava ao clicar no X. O problema é causado por overrides de recursos experimentais (Feature Management Overrides) obsoletos ou corrompidos no grupo 14.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FeatureManagement\Overrides\14", ValueName = "", HarmfulValue = "EXISTS", DefaultValue = "DELETE_KEY"
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
            Name = "Plano de Energia de Alto Desempenho",
            Description = "O Windows pode estar configurado com plano de energia 'Economia de Energia' ou 'Balanceado', reduzindo frequência de CPU e causando lentidão geral. O plano 'Alto Desempenho' garante que o processador opere na frequência máxima constantemente.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\bc5038f7-23e0-4960-96da-33abaf5935ec", ValueName = "Attributes", HarmfulValue = 2, DefaultValue = 0, IsOptional = true
        },
        new() {
            Name = "Prefetch / Superfetch Desativado",
            Description = "O Prefetch e Superfetch (SysMain) aceleram a abertura de aplicativos carregando-os previamente na memória RAM. Desativá-los causa abertura mais lenta de programas e boot mais demorado, especialmente em HDDs.",
            Category = "Desempenho", Type = TweakType.Service, ServiceName = "SysMain", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Compressão de Memória RAM Desativada",
            Description = "A compressão de memória do Windows 10/11 compacta páginas inativas na RAM, permitindo que mais programas caibam em memória física. Desativá-la aumenta o uso do arquivo de paginação e lentifica o sistema em PCs com pouca RAM.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "DisableMemoryCompression", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "NTFS - Last Access Time Update (Atualização de Data de Acesso)",
            Description = "Por padrão, o Windows registra a data/hora de cada acesso a arquivo no NTFS. Em discos com muita leitura, isso gera escritas desnecessárias, aumentando a latência. Desativar melhora a performance de I/O, especialmente em HDDs.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", ValueName = "NtfsDisableLastAccessUpdate", HarmfulValue = 0, DefaultValue = 1, IsOptional = true
        },
        new() {
            Name = "NTFS - 8.3 Short Name Generation",
            Description = "O Windows gera automaticamente nomes curtos (8.3) para compatibilidade com programas antigos. Isso adiciona overhead a cada criação de arquivo. Desativar melhora a performance de I/O em volumes com muitos arquivos.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", ValueName = "NtfsDisable8dot3NameCreation", HarmfulValue = 0, DefaultValue = 1, IsOptional = true
        },
        new() {
            Name = "Modo de Suspensão Híbrido (Fast Startup) Configurado Incorretamente",
            Description = "O Fast Startup (Inicialização Rápida) pode causar problemas de boot após atualizações do Windows, estado corrompido após desligamento e drivers não recarregados corretamente, resultando em lentidão acumulada.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Power", ValueName = "HiberbootEnabled", HarmfulValue = 1, DefaultValue = 0, IsOptional = true
        },
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
            Name = "Timer de Resolução do Sistema (Platform Timer)",
            Description = "O timer do sistema com resolução baixa (15ms padrão) causa micro-stuttering em jogos e aplicações de baixa latência. Definir para o valor mínimo (1ms) melhora a fluidez e precisão de agendamento de threads.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", ValueName = "NetworkThrottlingIndex", HarmfulValue = 0, DefaultValue = 268435455, IsOptional = true
        },
        new() {
            Name = "Throttling de Rede em Aplicativos Multimídia Desativado",
            Description = "O Windows limita automaticamente o uso de rede durante reprodução de mídia para evitar buffering. Desativar permite máximo throughput de rede sem impacto na latência de áudio/vídeo.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", ValueName = "NetworkThrottlingIndex", HarmfulValue = 10, DefaultValue = 268435455, IsOptional = true
        },
        new() {
            Name = "Paginação do Kernel (Kernel Paging) Ativa",
            Description = "Por padrão, partes do kernel e drivers podem ser paginadas para o disco. Manter o kernel na RAM melhora significativamente a responsividade do sistema, especialmente ao alternar entre aplicativos.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "DisablePagingExecutive", HarmfulValue = 0, DefaultValue = 1, IsOptional = true
        },
        new() {
            Name = "Tamanho do Cache de E/S do Sistema (LargeSystemCache)",
            Description = "Em workstations (não servidores), o LargeSystemCache deve estar desativado. Ativado, o sistema reserva mais memória para cache de arquivos em detrimento dos processos do usuário, causando lentidão em programas.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", ValueName = "LargeSystemCache", HarmfulValue = 1, DefaultValue = 0
        },
        new() {
            Name = "Modo de Gerenciamento de Energia do Processador (Throttling de CPU)",
            Description = "O Windows pode aplicar throttling agressivo de CPU em estados de ociosidade, causando demora para o processador atingir a frequência máxima (até 500ms). Configurar corretamente elimina essa latência em rajadas de trabalho.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\893dee8e-2bef-41e0-89c6-b55d0929964c", ValueName = "ACSettingIndex", HarmfulValue = 0, DefaultValue = 100, IsOptional = true
        },
        new() {
            Name = "Latência de DPC Elevada (DPC Watchdog Desconfigurado)",
            Description = "Latência de DPC (Deferred Procedure Call) elevada causa travamentos e engasgos de áudio/vídeo. Pode indicar drivers mal otimizados ou configuração incorreta do DPC Watchdog no registro.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}\0000", ValueName = "EnableUlps", HarmfulValue = 1, DefaultValue = 0, IsOptional = true
        },
        new() {
            Name = "Windows Search Indexing em Discos (Impacto de I/O)",
            Description = "O serviço de indexação do Windows Search pode gerar alta carga de I/O em discos mecânicos (HDDs), causando lentidão durante a indexação. Em SSDs, o impacto é menor, mas o serviço pode ser configurado para menor prioridade.",
            Category = "Desempenho", Type = TweakType.Service, ServiceName = "WSearch", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Serviço de Mapeamento de Porta (RPC/DCOM) Lento",
            Description = "O serviço de mapeamento de porta RPC pode apresentar lentidão na resolução de chamadas COM/DCOM, afetando aplicativos que dependem dessas tecnologias. Deve permanecer em execução automática.",
            Category = "Desempenho", Type = TweakType.Service, ServiceName = "RpcEptMapper", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Desempenho Visual do Windows (Animações e Efeitos)",
            Description = "Efeitos visuais excessivos (animações, sombras, transparências) consomem recursos de CPU e GPU, especialmente em PCs mais antigos. Ajustar para 'Melhor desempenho' pode reduzir latência visual percebida.",
            Category = "Desempenho", KeyPath = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", ValueName = "VisualFXSetting", HarmfulValue = 0, DefaultValue = 3, IsOptional = true
        },
        new() {
            Name = "ClearType e Renderização de Fonte (Font Smoothing)",
            Description = "A renderização de fontes ClearType com configuração incorreta pode causar sub-renderização de texto, gerando mais carga de CPU na renderização de interfaces com muito texto.",
            Category = "Desempenho", KeyPath = @"HKEY_CURRENT_USER\Control Panel\Desktop", ValueName = "FontSmoothing", HarmfulValue = 0, DefaultValue = 2, IsOptional = true
        },
        new() {
            Name = "Modo de Suspensão de Disco (HDD/SSD Spindown)",
            Description = "Quando o Windows desliga o disco após inatividade, o próximo acesso causa uma demora de 1-3 segundos (wake-up do HDD ou reinicialização de SSD). Desativar o spindown elimina essa latência.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\0012ee47-9041-4b5d-9b77-535fba8b1442\6738e2c4-e8a5-4a42-b16a-e040e769756e", ValueName = "ACSettingIndex", HarmfulValue = 1, DefaultValue = 0, IsOptional = true
        },
        new() {
            Name = "AutoEndTasks (Fechamento Forçado de Aplicativos Travados)",
            Description = "O Windows aguarda um longo tempo antes de forçar o fechamento de aplicativos que não respondem. Reduzir os timeouts de HungApp e WaitToKill melhora a responsividade ao fechar programas travados, incluindo o Gerenciador de Tarefas.",
            Category = "Desempenho", KeyPath = @"HKEY_CURRENT_USER\Control Panel\Desktop", ValueName = "AutoEndTasks", HarmfulValue = 0, DefaultValue = 1, IsOptional = true
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
            Name = "WaitToKillServiceTimeout (Tempo de Espera para Parar Serviços)",
            Description = "Tempo de espera para parar serviços no desligamento. O padrão de 12.000ms atrasa o desligamento quando serviços não respondem. Reduzir para 3.000ms elimina esse atraso.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control", ValueName = "WaitToKillServiceTimeout", HarmfulValue = "12000", DefaultValue = "3000", IsOptional = true
        },
        new() {
            Name = "Modo de Baixa Latência para Áudio (MMCSS - Audio)",
            Description = "O MMCSS (Multimedia Class Scheduler Service) precisa estar configurado corretamente para garantir baixa latência de áudio. Sem ele, o áudio pode apresentar crackles, pops e stuttering sob carga de CPU.",
            Category = "Desempenho", Type = TweakType.Service, ServiceName = "MMCSS", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto"
        },
        new() {
            Name = "Gerenciador de Filas de Impressão (Print Spooler) Impactando Desempenho",
            Description = "O serviço Print Spooler causa lentidão de inicialização e pode consumir CPU periodicamente mesmo sem impressoras. Se não usa impressoras, é seguro configurá-lo como Manual.",
            Category = "Desempenho", Type = TweakType.Service, ServiceName = "Spooler", HarmfulStartMode = "Disabled", DefaultStartMode = "Auto", IsOptional = true
        },
        new() {
            Name = "SSD TRIM Agendado (Manutenção de SSD)",
            Description = "O serviço de desfragmentação e otimização de unidades inclui TRIM agendado para SSDs. Desativá-lo impede a manutenção automática de SSDs, causando degradação de performance com o tempo.",
            Category = "Desempenho", Type = TweakType.Service, ServiceName = "defragsvc", HarmfulStartMode = "Disabled", DefaultStartMode = "Manual"
        },
        new() {
            Name = "Atualizações em Segundo Plano (Banda Larga para Windows Update)",
            Description = "O Windows Update pode usar parte da banda larga em segundo plano para baixar atualizações, causando lentidão de rede e picos de uso de disco e CPU inesperados durante o uso normal do PC.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", ValueName = "DODownloadMode", HarmfulValue = 3, DefaultValue = 1, IsOptional = true
        },
        new() {
            Name = "Feedback de Diagnóstico (Telemetria de Alto Impacto)",
            Description = "O nível máximo de telemetria ('Full') causa alto I/O de disco e uso de CPU para coleta e envio de dados diagnósticos. Reduzir para 'Basic' ou 'Security' diminui o impacto no desempenho.",
            Category = "Desempenho", KeyPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", ValueName = "AllowTelemetry", HarmfulValue = 3, DefaultValue = 1, IsOptional = true
        }
    };

        #endregion

        #region Métodos Públicos (Interface Gráfica)

        // *** NOVO: EXPOE A LISTA DE TWEAKS PARA A BUSCA ***
        public static List<ScannableTweak> GetAllTweaksDefinition()
        {
            return HarmfulTweaks;
        }

        public static List<ScannableTweak> GetHarmfulTweaksWithStatus()
        {

            // Reduz alocações e melhora performance em hot code paths
            var tweaksCopy = new List<ScannableTweak>(HarmfulTweaks.Count);
            

            foreach (var tweak in HarmfulTweaks)
            {
                CheckTweak(tweak);
                // Criar uma cópia shallow do tweak para evitar modificação acidental da lista estática
                tweaksCopy.Add(tweak);
            }
            return tweaksCopy;
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
                        if (string.IsNullOrEmpty(tweak.KeyPath))
                            return (false, "Configuração de registro inválida.");

                        // Limpar o prefixo do hive do caminho
                        string path = tweak.KeyPath
                            .Replace(@"HKEY_LOCAL_MACHINE\", "")
                            .Replace(@"HKEY_CURRENT_USER\", "")
                            .Replace(@"HKEY_CLASSES_ROOT\", "")
                            .Replace(@"HKLM\", "");

                        // Determinar o hive correto
                        RegistryKey baseKey;
                        if (tweak.KeyPath.StartsWith("HKEY_LOCAL_MACHINE") || tweak.KeyPath.StartsWith("HKLM"))
                            baseKey = Registry.LocalMachine;
                        else if (tweak.KeyPath.StartsWith("HKEY_CLASSES_ROOT"))
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
                                Logger.Log($"[PATH REPAIR] PATH {pathType} reparado. Ações: {string.Join("; ", actions)}");
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
                        break;

                    case TweakType.Service:
                        if (string.IsNullOrEmpty(tweak.ServiceName) || string.IsNullOrEmpty(tweak.DefaultStartMode))
                            return (false, "Configuração de serviço inválida.");

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
                        var configResult = SystemUtils.RunExternalProcess("sc.exe", scCmd, true);
                        Logger.Log($"[TOGGLE] SC Config Result: {configResult}");

                        if (applySafeValue && mode != "Disabled")
                        {
                            Logger.Log($"[TOGGLE] Iniciando serviço: {tweak.ServiceName}");
                            var startResult = SystemUtils.RunExternalProcess("sc.exe", $"start {tweak.ServiceName}", true);
                            Logger.Log($"[TOGGLE] SC Start Result: {startResult}");
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
                            Logger.Log($"[TOGGLE] BCD falhou com código {exitCode}: {error}");
                            return (false, $"Falha ao modificar BCD: {error}");
                        }
                        
                        Logger.Log($"[TOGGLE] BCD sucesso: {output}");
                        break;

                    case TweakType.PageFile:
                        const string pfKey = @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
                        if (applySafeValue)
                            Registry.SetValue(pfKey, "PagingFiles", new string[] { @"C:\pagefile.sys" }, RegistryValueKind.MultiString);
                        else
                            Registry.SetValue(pfKey, "PagingFiles", Array.Empty<string>(), RegistryValueKind.MultiString);
                        break;
                }

                // Para tweaks do Explorer (que usam CheckExplorerProblems), não usar CheckTweak
                // pois CheckExplorerProblems é genérico e pode reportar problemas não relacionados.
                if (tweak.Category == "Corrupção de Sistema" || tweak.Category == "Defesa e Antivírus" || tweak.Category == "Restrições do Sistema")
                {
                    // Após escrever o valor seguro, forçar OK
                    tweak.Status = TweakStatus.OK;
                }
                else
                {
                    CheckTweak(tweak);
                }
                return (true, $"{tweak.Name} foi {action}.");
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

        public static void RestoreAllTweakStates()
        {
            try
            {
                using (RegistryKey? configKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KitLugia\Config", true))
                {
                    if (configKey == null) return;
                    var allTweaks = GetAllTweaksDefinition();
                    int restoredCount = 0;
                    
                    foreach (var tweak in allTweaks)
                    {
                        string valueKey = $"Tweak_{GetSafeKeyName(tweak)}";
                        string? savedStatus = configKey.GetValue(valueKey) as string;
                        
                        if (!string.IsNullOrEmpty(savedStatus) && Enum.TryParse<TweakStatus>(savedStatus, out var status))
                        {
                            if (status == TweakStatus.MODIFIED)
                            {
                                // Restaurar para o padrão seguro
                                ToggleTweak(tweak);
                                restoredCount++;
                            }
                        }
                    }
                    
                    Logger.Log($"Configurações restauradas: {restoredCount} tweaks");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao restaurar configurações: {ex.Message}");
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

        public static void ResetAllConfigurations()
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"SOFTWARE\KitLugia\Config");
                Logger.Log("Todas as configurações foram resetadas.");
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao resetar configurações: {ex.Message}");
            }
        }

        public static void SaveQuickToggleConfig(string tweakName, bool isEnabled)
        {
            try
            {
                using (var configKey = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\KitLugia\QuickToggles", true))
                {
                    configKey.SetValue(tweakName, isEnabled, RegistryValueKind.DWord);
                    Logger.Log($"Quick toggle '{tweakName}' salvo como: {(isEnabled ? "ATIVADO" : "DESATIVADO")}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao salvar quick toggle: {ex.Message}");
            }
        }

        public static bool GetQuickToggleState(string tweakName)
        {
            try
            {
                using (RegistryKey? configKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KitLugia\QuickToggles", true))
                {
                    if (configKey == null) return false;
                    object? value = configKey.GetValue(tweakName);
                    return value != null && value is int intValue && intValue == 1;
                }
            }
            catch
            {
                return false;
            }
        }

        public static List<string> GetAppliedQuickToggles()
        {
            var appliedToggles = new List<string>();
            
            try
            {
                using (RegistryKey? configKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KitLugia\QuickToggles", true))
                {
                    if (configKey == null) return appliedToggles;
                    foreach (var valueName in configKey.GetValueNames())
                    {
                        object? value = configKey.GetValue(valueName);
                        if (value != null && value is int intValue && intValue == 1)
                        {
                            appliedToggles.Add(valueName);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Erro ao carregar toggles: {ex.Message}");
            }
            
            return appliedToggles;
        }

        #endregion

        #region Detecção de Problemas PATH

        private static PathProblem AnalyzePathProblems(string pathValue)
        {
            if (string.IsNullOrEmpty(pathValue)) return PathProblem.None;
            
            var entries = pathValue.Split(';', StringSplitOptions.RemoveEmptyEntries);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var problems = PathProblem.None;
            
            // Limite de entradas
            if (entries.Length > 50) problems |= PathProblem.TooManyEntries;
            if (pathValue.Length > 2048) problems |= PathProblem.PathTooLong;
            
            foreach (var entry in entries)
            {
                var clean = entry.Trim().Trim('"').Trim();
                if (string.IsNullOrEmpty(clean)) continue;
                
                // Duplicado
                if (!seen.Add(clean)) problems |= PathProblem.Duplicate;
                
                // Caminho relativo (vulnerabilidade)
                if (clean.StartsWith('.') || clean.StartsWith(".."))
                    problems |= PathProblem.RelativePath;
                
                // Espaço sem aspas
                if (clean.Contains(' ') && !clean.StartsWith('"'))
                    problems |= PathProblem.UnquotedSpace;
                
                // Caminho temporário
                if (clean.Contains("\\Temp\\", StringComparison.OrdinalIgnoreCase) ||
                    clean.Contains("\\Tmp\\", StringComparison.OrdinalIgnoreCase))
                    problems |= PathProblem.TempPath;
                
                // Caminho de usuário sem variável (ex: C:\Users\fulano\AppData)
                if (clean.Contains("\\Users\\", StringComparison.OrdinalIgnoreCase) &&
                    !clean.Contains("%USERPROFILE%"))
                    problems |= PathProblem.UserPathWithoutVariable;
                
                // Lixo de desenvolvimento
                if (clean.Contains("\\node_modules\\") || clean.Contains("\\vendor\\") ||
                    clean.Contains("\\.git\\") || clean.Contains("\\dotnet\\sdk\\"))
                    problems |= PathProblem.DevelopmentJunk;
                
                // Sintaxe inválida
                if (clean.Contains(',') || clean.Contains("\"\"") || clean.Contains("\\\\"))
                    problems |= PathProblem.SyntaxError;
                
                // Caminho longo (>260)
                if (clean.Length > 260) problems |= PathProblem.LongPath;
                
                // Caracteres não ASCII (pode indicar corrupção)
                if (clean.Any(c => c > 127)) problems |= PathProblem.EncodingIssue;
                
                // Verifica existência (expande variáveis)
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
                    string sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows).ToLower().TrimEnd('\\');
                    string expanded = pathValue.ToLower();
                    bool hasSystem32 = expanded.Contains($"{sysRoot}\\system32");
                    bool hasWbem = expanded.Contains("system32\\wbem");
                    bool hasPowerShell = expanded.Contains("windowspowershell");
                    bool hasOpenSSH = expanded.Contains("openssh");

                    // Se faltar qualquer caminho essencial, marcar como MODIFIED
                    return !hasSystem32 || !hasWbem || !hasPowerShell || !hasOpenSSH;
                }
                // Para User PATH: verificar se faltam programas instalados
                else
                {
                    bool hasMissingPaths = false;
                    foreach (var kvp in installedPaths)
                    {
                        string expanded = Environment.ExpandEnvironmentVariables(kvp.Value).TrimEnd('\\');
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
                "CORRUPTED" => pathProblems != PathProblem.None,
                "DUPLICATE" => (pathProblems & PathProblem.Duplicate) != 0,
                "MISSING" => (pathProblems & PathProblem.MissingDirectory) != 0,
                "DEV_JUNK" => (pathProblems & PathProblem.DevelopmentJunk) != 0,
                "VULNERABLE" => (pathProblems & (PathProblem.MissingDirectory | PathProblem.RelativePath |
                                  PathProblem.UnquotedSpace | PathProblem.TempPath |
                                  PathProblem.UserPathWithoutVariable)) != 0,
                _ => false
            };
        }

        #endregion

        #region Detecção de Problemas do Windows Explorer

        public static ExplorerProblem CheckExplorerProblems()
        {
            ExplorerProblem problems = ExplorerProblem.None;
            
            // 1. Verificar pasta (Folder) - click duplo vai pra lugar errado
            // Registry: HKEY_CLASSES_ROOT\Folder\shell\open\command
            string folderDefaultCmd = Registry.GetValue(@"HKEY_CLASSES_ROOT\Folder\shell\open\command", "", null) as string ?? "";
            if (!string.IsNullOrEmpty(folderDefaultCmd) && 
                !folderDefaultCmd.Contains("explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                problems |= ExplorerProblem.DoubleClickFolderAssociationBroken;
            }
            
            // 2. Verificar se a ação padrão da pasta está ausente
            string folderDefaultValue = Registry.GetValue(@"HKEY_CLASSES_ROOT\Folder\shell", "", null) as string ?? "";
            if (string.IsNullOrEmpty(folderDefaultValue) || 
                (folderDefaultValue != "open" && folderDefaultValue != "explore"))
            {
                problems |= ExplorerProblem.DoubleClickFolderNotOpening;
            }
            
            // 3. Verificar associação padrão de pastas
            string folderOpenCommand = Registry.GetValue(@"HKEY_CLASSES_ROOT\Folder\shell\open\command", "", null) as string ?? "";
            if (string.IsNullOrEmpty(folderOpenCommand) || 
                !folderOpenCommand.Contains("%SystemRoot%\\explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                problems |= ExplorerProblem.DoubleClickFolderAssociationBroken;
            }
            
            // 4. Verificar Directory (comportamento similar)
            string dirDefaultCmd = Registry.GetValue(@"HKEY_CLASSES_ROOT\Directory\shell\open\command", "", null) as string ?? "";
            if (!string.IsNullOrEmpty(dirDefaultCmd) && 
                !dirDefaultCmd.Contains("explorer.exe", StringComparison.OrdinalIgnoreCase))
            {
                problems |= ExplorerProblem.DoubleClickFolderAssociationBroken;
            }
            
            // 5. Verificar exefile (arquivos executáveis)
            string exeOpenCommand = Registry.GetValue(@"HKEY_CLASSES_ROOT\exefile\shell\open\command", "", null) as string ?? "";
            if (string.IsNullOrEmpty(exeOpenCommand) || !exeOpenCommand.Contains("\"%1\"%*") &&
                !exeOpenCommand.Contains("\"%1\" %*"))
            {
                problems |= ExplorerProblem.DoubleClickExeBroken;
            }
            
            // 6. Verificar ContextMenu Handlers problemáticos
            using (RegistryKey? handlerKey = Registry.ClassesRoot.OpenSubKey(@"*\shellex\ContextMenuHandlers"))
            if (handlerKey != null)
            {
                foreach (string handlerName in handlerKey.GetSubKeyNames())
                {
                    // Handler vazio ou GUID inválido
                    using (RegistryKey? key = handlerKey.OpenSubKey(handlerName))
                    if (key != null)
                    {
                        string? guid = key.GetValue(null) as string;
                        if (string.IsNullOrEmpty(guid) || !guid.StartsWith("{"))
                        {
                            problems |= ExplorerProblem.InvalidContextMenuHandler;
                            break;
                        }
                        
                        // Verificar se o GUID existe em HKEY_CLASSES_ROOT\CLSID
                        if (guid.Length > 2 && guid.StartsWith("{") && guid.EndsWith("}"))
                        {
                            using (RegistryKey? clsidKey = Registry.ClassesRoot.OpenSubKey($@"CLSID\{guid}"))
                            {
                                if (clsidKey == null)
                                {
                                    problems |= ExplorerProblem.InvalidContextMenuHandler;
                                    break;
                                }
                                
                                // Verificar se a DLL do handler existe
                                using (RegistryKey? inprocKey = clsidKey.OpenSubKey("InprocServer32"))
                                {
                                    if (inprocKey != null)
                                    {
                                        string? dllPath = inprocKey.GetValue(null) as string;
                                        if (!string.IsNullOrEmpty(dllPath))
                                        {
                                            var expandedPath = Environment.ExpandEnvironmentVariables(dllPath);
                                            if (!File.Exists(expandedPath))
                                            {
                                                problems |= ExplorerProblem.InvalidContextMenuHandler;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            
            // 7. Verificar extensões bloqueadas
            using (RegistryKey? blockedKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Blocked"))
            if (blockedKey != null && blockedKey.ValueCount > 0)
            {
                // Se houver extensões bloqueadas com prefixo [CC], foi via CCleaner
                foreach (string valueName in blockedKey.GetValueNames())
                {
                    string? value = blockedKey.GetValue(valueName) as string;
                    if (value != null && value.Contains("[CC]"))
                    {
                        problems |= ExplorerProblem.BlockedContextMenuExtension;
                        break;
                    }
                }
            }
            
            // 8. Verificar se menu de contexto do Explorer está desabilitado por política
            int? noViewContextMenu = Registry.GetValue(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoViewContextMenu", 0) as int?;
            if (noViewContextMenu == 1)
            {
                problems |= ExplorerProblem.ContextMenuItemsMissing;
            }
            
            // 9. Verificar lixeira de extensões corrompidas (shell extensions desativadas)
            using (RegistryKey? disabledKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Disabled"))
            if (disabledKey != null && disabledKey.ValueCount > 5) // Muitas desativadas pode indicar problema
            {
                problems |= ExplorerProblem.InvalidContextMenuHandler;
            }
            
            return problems;
        }

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

        private static void CheckTweak(ScannableTweak tweak)
        {
            try
            {
                // Log detalhado apenas se verbosidade estiver ativa
                if (Logger.VerboseCheckLogs)
                {
                    Logger.Log($"[CHECK] Verificando: {tweak.Name} (Tipo: {tweak.Type})");
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
                        // Registry.GetValue aceita ValueName vazio para ler o valor padrão
                        string valName = tweak.ValueName ?? "";
                        currentValue = Registry.GetValue(tweak.KeyPath, valName, null);
                        
                        // Se for chave HKEY_CLASSES_ROOT, Registry.GetValue pode não funcionar
                        // Nesse caso, tentar abrir a chave diretamente
                        if (currentValue == null && tweak.KeyPath.StartsWith("HKEY_CLASSES_ROOT"))
                        {
                            try
                            {
                                string checkPath = tweak.KeyPath.Replace(@"HKEY_CLASSES_ROOT\", "");
                                using var checkKey = Registry.ClassesRoot.OpenSubKey(checkPath);
                                if (checkKey != null)
                                {
                                    currentValue = checkKey.GetValue(valName);
                                }
                            }
                            catch { /* ignora */ }
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
                            isHarmful = false;
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
                        string? startMode = ServiceHelper.GetServiceStartMode(tweak.ServiceName);

                        if (startMode == null)
                        {
                            if (Logger.VerboseCheckLogs)
                                Logger.Log($"[CHECK] Serviço {tweak.ServiceName} não encontrado");
                            tweak.Status = TweakStatus.NOT_FOUND;
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

                    var (exitCode, output, error) = ProcessRunner.Run("bcdedit", "/enum", 10000);
                    if (exitCode != 0)
                    {
                        Logger.Log($"[CHECK] Erro ao obter BCD: {error}");
                        tweak.Status = TweakStatus.ERROR;
                        return;
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
                                // Extrai o valor após o nome do parâmetro
                                var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                                if (parts.Length >= 2)
                                {
                                    string actualValue = parts[1];
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
                    var m = Regex.Match(line.Trim(), @"Set-Alias\s+-Name\s+(\S+)\s+-Value\s+""([^""]+)""", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        string target = Environment.ExpandEnvironmentVariables(m.Groups[2].Value);
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
                    var m = Regex.Match(line.Trim(), @"Set-Alias\s+-Name\s+(\S+)\s+-Value\s+""([^""]+)""", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        string target = Environment.ExpandEnvironmentVariables(m.Groups[2].Value);
                        string aliasName = m.Groups[1].Value;
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
    }
}
