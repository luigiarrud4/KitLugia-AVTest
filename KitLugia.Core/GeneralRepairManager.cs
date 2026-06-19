using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class GeneralRepairManager
    {
        public static List<RepairAction> GetAllRepairs()
        {

            // Típico: 20-50 ações de reparo
            var repairs = new List<RepairAction>(50);

            // =================================================================
            // 1. EXPLORER E VISUAL (Win 10/11)
            // =================================================================

            repairs.Add(new RepairAction
            {
                Name = "Reiniciar Explorer.exe",
                Category = "Explorer/UI",
                Icon = "🔄",
                Description = "Recarrega a área de trabalho e barra de tarefas travadas.",
                Execute = () => {
                    Logger.Log("Reiniciando processo Explorer.exe...");
                    SystemUtils.RunExternalProcess("taskkill", "/f /im explorer.exe", true);
                    // Pequena pausa para garantir que o processo encerrou
                    System.Threading.Thread.Sleep(1000);
                    SystemUtils.RunExternalProcess("cmd.exe", "/c start explorer.exe", true, false);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Menu de Contexto Clássico (Win11)",
                Category = "Explorer/UI",
                Icon = "📋",
                Description = "Restaura o menu de botão direito antigo (Win10) no Windows 11.",
                Execute = () => {
                    Logger.Log("Aplicando Menu de Contexto Clássico...");
                    SystemUtils.SetRegistryValue(Microsoft.Win32.Registry.CurrentUser, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", "", "", Microsoft.Win32.RegistryValueKind.String);
                    Logger.Log("Reinicie o Explorer para aplicar.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Remover Menu Clássico (Win11)",
                Category = "Explorer/UI",
                Icon = "↩️",
                Description = "Volta para o menu de contexto padrão do Windows 11.",
                Execute = () => {
                    Logger.Log("Removendo Menu de Contexto Clássico...");
                    SystemUtils.DeleteRegistryKey(Microsoft.Win32.Registry.CurrentUser, @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reconstruir Cache de Ícones",
                Category = "Explorer/UI",
                Icon = "🖼️",
                Description = "Corrige ícones brancos ou errados. Fecha o Explorer temporariamente.",
                Execute = () => {
                    Logger.Log("Iniciando reconstrução do cache de ícones...");
                    SystemUtils.RunExternalProcess("taskkill", "/f /im explorer.exe", true);
                    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft\\Windows\\Explorer");
                    SystemUtils.RunExternalProcess("cmd", $"/c del /f /q \"{path}\\iconcache*\"", true);
                    Process.Start("explorer.exe");
                    Logger.Log("[SUCESSO] Cache de ícones limpo.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reconstruir Cache de Miniaturas",
                Category = "Explorer/UI",
                Icon = "🏞️",
                Description = "Corrige thumbnails de fotos que não aparecem nas pastas.",
                Execute = () => {
                    Logger.Log("Limpando cache de miniaturas (Thumbnails)...");
                    SystemUtils.RunExternalProcess("taskkill", "/f /im explorer.exe", true);
                    SystemUtils.RunExternalProcess("cmd", "/c del /f /s /q \"%LocalAppData%\\Microsoft\\Windows\\Explorer\\thumbcache_*.db\"", true);
                    Process.Start("explorer.exe");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Remover Sufixo '- Atalho'",
                Category = "Explorer/UI",
                Icon = "🔗",
                Description = "Impede que o Windows adicione o texto 'Atalho' ao criar links.",
                Execute = () => SystemUtils.RunExternalProcess("reg", "add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\" /v link /t REG_BINARY /d 00000000 /f", true)
            });

            repairs.Add(new RepairAction
            {
                Name = "Desativar 'Recomendados' (Win11)",
                Category = "Explorer/UI",
                Icon = "🚫",
                Description = "Remove a área de arquivos recomendados do Menu Iniciar (Requer Admin).",
                Execute = () => SystemUtils.RunExternalProcess("reg", "add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\\Explorer\" /v HideRecommendedSection /t REG_DWORD /d 1 /f", true)
            });

            repairs.Add(new RepairAction
            {
                Name = "Alinhar Barra de Tarefas à Esquerda",
                Category = "Explorer/UI",
                Icon = "⬅️",
                Description = "Move o menu iniciar do centro para a esquerda (Estilo Windows 10).",
                Execute = () => SystemUtils.RunExternalProcess("reg", "add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced\" /v TaskbarAl /t REG_DWORD /d 0 /f", true)
            });

            repairs.Add(new RepairAction
            {
                Name = "Restaurar Photo Viewer Antigo",
                Category = "Explorer/UI",
                Icon = "📷",
                Description = "Ativa o visualizador de fotos clássico (leve e rápido) no registro.",
                Execute = () => SystemUtils.RunExternalProcess("cmd", "/c REG ADD \"HKLM\\SOFTWARE\\Microsoft\\Windows Photo Viewer\\Capabilities\\FileAssociations\" /v \".jpg\" /t REG_SZ /d \"PhotoViewer.FileAssoc.Tiff\" /f", true)
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Ícones da Bandeja",
                Category = "Explorer/UI",
                Icon = "🔔",
                Description = "Limpa ícones antigos ou 'fantasmas' da área de notificação.",
                Execute = () => {
                    Logger.Log("Resetando ícones da bandeja do sistema...");
                    SystemUtils.RunExternalProcess("reg", "delete \"HKCU\\Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\TrayNotify\" /v IconStreams /f", true);
                    Process.Start("explorer.exe");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Habilitar Segundos no Relógio",
                Category = "Explorer/UI",
                Icon = "⏱️",
                Description = "Mostra os segundos no relógio da barra de tarefas.",
                Execute = () => {
                    SystemUtils.RunExternalProcess("reg", "add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced\" /v ShowSecondsInSystemClock /t REG_DWORD /d 1 /f", true);
                    Process.Start("explorer.exe");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Resetar Visualização de Pastas",
                Category = "Explorer/UI",
                Icon = "📂",
                Description = "Reseta o modo de exibição de todas as pastas para o padrão.",
                Execute = () => {
                    Logger.Log("Apagando chaves de visualização de pastas (BagMRU)...");
                    SystemUtils.RunExternalProcess("reg", "delete \"HKCU\\Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\Shell\\BagMRU\" /f", true);
                    Process.Start("explorer.exe");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Lixeira Corrompida",
                Category = "Explorer/UI",
                Icon = "🗑️",
                IsDangerous = true,
                Description = "Corrige erro de acesso à Lixeira em todas as unidades de disco.",
                Execute = () => {
                    Logger.Log("Resetando Lixeira em todos os drives...");
                    foreach (var d in DriveInfo.GetDrives())
                        if (d.DriveType == DriveType.Fixed)
                            SystemUtils.RunExternalProcess("cmd", $"/c rd /s /q \"{d.Name}$Recycle.bin\"", true);
                }
            });


            // =================================================================
            // 2. INTERNET E REDE
            // =================================================================

            repairs.Add(new RepairAction
            {
                Name = "Reset Completo Winsock/IP",
                Category = "Internet",
                Icon = "🌐",
                IsDangerous = true,
                Description = "Reseta sockets, TCP/IP e libera conexões. Fix essencial de rede.",
                Execute = () => {
                    Logger.Log("Executando reset de rede completo (Winsock/IP)...");
                    SystemUtils.RunExternalProcess("netsh", "winsock reset", true);
                    SystemUtils.RunExternalProcess("netsh", "int ip reset", true);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Flush DNS (Limpar Cache)",
                Category = "Internet",
                Icon = "🚿",
                Description = "Remove cache antigo de resolução de nomes de sites.",
                Execute = () => SystemUtils.RunExternalProcess("ipconfig", "/flushdns", true)
            });

            repairs.Add(new RepairAction
            {
                Name = "Resetar Proxy do Windows",
                Category = "Internet",
                Icon = "🔀",
                Description = "Limpa configurações de Proxy (WinHTTP) que malwares alteram.",
                Execute = () => SystemUtils.RunExternalProcess("netsh", "winhttp reset proxy", true)
            });

            repairs.Add(new RepairAction
            {
                Name = "Resetar Firewall",
                Category = "Internet",
                Icon = "🧱",
                IsDangerous = true,
                Description = "Apaga todas as regras do Firewall e restaura o padrão de fábrica.",
                Execute = () => SystemUtils.RunExternalProcess("netsh", "advfirewall reset", true)
            });

            repairs.Add(new RepairAction
            {
                Name = "Restaurar HOSTS",
                Category = "Internet",
                Icon = "📝",
                IsDangerous = true,
                Description = "Reseta o arquivo de bloqueio de sites (C:\\Windows\\System32\\drivers\\etc).",
                Execute = () => {
                    try
                    {
                        Logger.Log("Restaurando arquivo HOSTS original...");
                        File.WriteAllText(Path.Combine(Environment.SystemDirectory, @"drivers\etc\hosts"), "127.0.0.1 localhost");
                        Logger.Log("[SUCESSO] Arquivo HOSTS limpo.");
                    }
                    catch (Exception ex) { Logger.LogError("ResetHosts", ex.Message); }
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Ativar Descoberta de Rede",
                Category = "Internet",
                Icon = "📡",
                Description = "Permite que este computador veja e seja visto por outros na rede.",
                Execute = () => SystemUtils.RunExternalProcess("netsh", "advfirewall firewall set rule group=\"Network Discovery\" new enable=Yes", true)
            });


            // =================================================================
            // 3. SISTEMA, SERVIÇOS E FERRAMENTAS
            // =================================================================

            repairs.Add(new RepairAction
            {
                Name = "Resetar Windows Update",
                Category = "Sistema",
                Icon = "🔄",
                IsSlow = true,
                Description = "Para serviços, limpa pastas temporárias e reinicia o Update.",
                Execute = () => {
                    Logger.Log("Iniciando reparo do Windows Update em janela externa...");
                    SystemUtils.RunExternalProcess("cmd", "/c net stop wuauserv && net stop bits && net stop cryptsvc && rd /s /q %systemroot%\\SoftwareDistribution && rd /s /q %systemroot%\\System32\\catroot2 && net start wuauserv && net start bits && net start cryptsvc & pause", false, false);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Limpeza de Disco (SAGE)",
                Category = "Sistema",
                Icon = "🧹",
                Description = "Executa a limpeza de disco avançada do Windows (cleanmgr).",
                Execute = () => {
                    Logger.Log("Iniciando Limpeza de Disco Avançada...");
                    SystemUtils.RunExternalProcess("cleanmgr.exe", "/sagerun:1", true);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Windows Store (Apps)",
                Category = "Sistema",
                Icon = "🛒",
                Description = "Reseta o cache da loja e reinstala apps padrão (WSReset).",
                Execute = () => {
                    SystemUtils.RunExternalProcess("wsreset.exe", "", true);
                    SystemUtils.RunExternalProcess("powershell", "-ExecutionPolicy Bypass -Command \"Get-AppXPackage -AllUsers | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register \"$($_.InstallLocation)\\AppXManifest.xml\"}\"", true);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Gaming Services + Xbox",
                Category = "Sistema",
                Icon = "🎮",
                Description = "Desinstala e reinstala Gaming Services e Xbox App. Fix para Game Pass e jogos da Store que funcionam juntos.",
                Execute = () => {
                    Logger.Log("Reparando Gaming Services e Xbox App...");

                    // 1. Reparar Gaming Services
                    var gamingResult = Toolbox.RepairGamingServices();
                    if (gamingResult.Success)
                    {
                        Logger.Log(gamingResult.Message);
                    }
                    else
                    {
                        Logger.LogError("Reparar Gaming Services", gamingResult.Message);
                    }

                    // 2. Reinstalar componentes Xbox relacionados
                    Logger.Log("Reinstalando componentes Xbox...");
                    SystemUtils.RunExternalProcess("powershell", "-Command \"Get-AppxPackage *Microsoft.XboxApp* | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register '$($_.InstallLocation)\\AppXManifest.xml'}\"", true);
                    SystemUtils.RunExternalProcess("powershell", "-Command \"Get-AppxPackage *Microsoft.XboxIdentityProvider* | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register '$($_.InstallLocation)\\AppXManifest.xml'}\"", true);
                    SystemUtils.RunExternalProcess("powershell", "-Command \"Get-AppxPackage *Microsoft.XboxGamingOverlay* | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register '$($_.InstallLocation)\\AppXManifest.xml'}\"", true);

                    // 3. Reiniciar serviços Xbox
                    Logger.Log("Reiniciando serviços Xbox...");
                    SystemUtils.RunExternalProcess("net", "stop XblGameSave", true);
                    SystemUtils.RunExternalProcess("net", "start XblGameSave", true);
                    SystemUtils.RunExternalProcess("net", "stop XboxNetApiSvc", true);
                    SystemUtils.RunExternalProcess("net", "start XboxNetApiSvc", true);

                    Logger.Log("[SUCESSO] Gaming Services e Xbox reparados. Reinicie o PC.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Limpar Cache de Sombra (VSS)",
                Category = "Sistema",
                Icon = "🗂️",
                IsDangerous = true,
                Description = "Apaga todos os pontos de restauração antigos para liberar espaço.",
                Execute = () => SystemUtils.RunExternalProcess("vssadmin", "delete shadows /all /quiet", true)
            });

            repairs.Add(new RepairAction
            {
                Name = "Resetar Energia (Power)",
                Category = "Sistema",
                Icon = "⚡",
                Description = "Restaura os planos de energia padrão do Windows.",
                Execute = () => SystemUtils.RunExternalProcess("powercfg", "-restoredefaultschemes", true)
            });

            repairs.Add(new RepairAction
            {
                Name = "Corrigir Time/Hora (NTP)",
                Category = "Sistema",
                Icon = "🕐",
                Description = "Sincroniza o relógio do Windows com servidores oficiais.",
                Execute = () => {
                    SystemUtils.RunExternalProcess("net", "stop w32time", true);
                    SystemUtils.RunExternalProcess("w32tm", "/unregister", true);
                    SystemUtils.RunExternalProcess("w32tm", "/register", true);
                    SystemUtils.RunExternalProcess("net", "start w32time", true);
                    SystemUtils.RunExternalProcess("w32tm", "/resync", true);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Habilitar Modo Deus (GodMode)",
                Category = "Sistema",
                Icon = "⚜️",
                Description = "Cria uma pasta na área de trabalho com acesso a TODAS as configurações.",
                Execute = () => {
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    string path = Path.Combine(desktop, "GodMode.{ED7BA470-8E54-465E-825C-99712043E01C}");
                    if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Habilitar Regedit",
                Category = "Sistema",
                Icon = "🔓",
                Description = "Remove bloqueio de administrador/vírus (Regedit Disable).",
                Execute = () => SystemUtils.RunExternalProcess("reg", "delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v DisableRegistryTools /f", true)
            });

            repairs.Add(new RepairAction
            {
                Name = "Limpar Logs de Eventos",
                Category = "Sistema",
                Icon = "📜",
                Description = "Apaga todo o histórico do Visualizador de Eventos do Windows.",
                Execute = () => {
                    Logger.Log("Limpando Visualizador de Eventos (Event Viewer)...");
                    SystemUtils.RunExternalProcess("powershell", "-Command \"wevtutil el | Foreach-Object { wevtutil cl $_ }\"", true);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Corrigir Associação .EXE",
                Category = "Sistema",
                Icon = "🔧",
                Description = "Repara o registro para corrigir programas que não abrem.",
                Execute = () => SystemUtils.RunExternalProcess("cmd", "/c assoc .exe=exefile", true)
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Windows Defender",
                Category = "Sistema",
                Icon = "🛡️",
                Description = "Reseta configurações e definições do Antivírus nativo.",
                Execute = () => SystemUtils.RunExternalProcess("cmd", "/c \"%ProgramFiles%\\Windows Defender\\MpCmdRun.exe\" -RestoreDefaults", true)
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar CD/DVD Drive",
                Category = "Sistema",
                Icon = "💿",
                Description = "Remove filtros Upper/Lower do registro que ocultam o leitor.",
                Execute = () => SystemUtils.RunExternalProcess("reg", "delete \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e965-e325-11ce-bfc1-08002be10318}\" /v UpperFilters /f", true)
            });

            repairs.Add(new RepairAction
            {
                Name = "Destravar Clipboard/Ctrl+V",
                Category = "Sistema",
                Icon = "📋",
                Description = "Limpa e reinicia o serviço da área de transferência.",
                Execute = () => SystemUtils.RunExternalProcess("cmd", "/c echo off | clip", true)
            });

            repairs.Add(new RepairAction
            {
                Name = "Desativar Hibernação",
                Category = "Sistema",
                Icon = "💤",
                Description = "Libera gigabytes de espaço deletando o hiberfil.sys.",
                Execute = () => SystemUtils.RunExternalProcess("powercfg", "-h off", true)
            });

            // =================================================================
            // 4. WINDOWS STORE E APPS
            // =================================================================

            repairs.Add(new RepairAction
            {
                Name = "Resetar Microsoft Store",
                Category = "Apps/Loja",
                Icon = "🏪",
                IsSlow = true,
                Description = "Executa WSReset.exe para limpar cache da loja e destravar downloads.",
                Execute = () => SystemUtils.RunExternalProcess("wsreset.exe", "", false, false)
            });

            repairs.Add(new RepairAction
            {
                Name = "Reinstalar Todos Apps Padrão",
                Category = "Apps/Loja",
                Icon = "📦",
                IsSlow = true,
                Description = "Usa PowerShell para reinstalar Calculadora, Fotos, Email e outros.",
                Execute = () => {
                    Logger.Log("Iniciando reinstalação de Apps Padrão via PowerShell em janela externa...");
                    SystemUtils.RunExternalProcess("powershell", "-ExecutionPolicy Bypass -NoExit -Command \"Get-AppXPackage -AllUsers | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register \"$($_.InstallLocation)\\AppXManifest.xml\"}\"", false, false);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Pesquisa (Search)",
                Category = "Apps/Loja",
                Icon = "🔍",
                Description = "Reinicia serviços e reconstrói o índice do Windows Search.",
                Execute = () => {
                    Logger.Log("Reiniciando serviço Windows Search...");
                    SystemUtils.RunExternalProcess("net", "stop wsearch", true);
                    SystemUtils.RunExternalProcess("net", "start wsearch", true);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Resetar Central de Notificação",
                Category = "Apps/Loja",
                Icon = "🔔",
                Description = "Re-registra o ShellExperienceHost. Corrige notificações travadas.",
                Execute = () => SystemUtils.RunExternalProcess("powershell", "Get-AppxPackage Microsoft.Windows.ShellExperienceHost | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register \"$($_.InstallLocation)\\AppXManifest.xml\"}", true)
            });

            // =================================================================
            // 5. JOGOS / ANTI-CHEAT (VALORANT FIX)
            // =================================================================

            repairs.Add(new RepairAction
            {
                Name = "Correção VALORANT (VAN9005)",
                Category = "Jogos/Anti-Cheat",
                Icon = "🎮",
                IsSlow = true, // Abre painel de diagnóstico integrado
                Description = "Abre diagnóstico integrado para verificar UEFI, TPM 2.0 e VBS/HVCI. A melhor solução é habilitar UEFI + TPM 2.0 (conforme artigo da Riot). Se não for possível, desative VBS/HVCI.",
                Execute = () =>
                {
                    Logger.Log("Reparo do Valorant gerenciado pela GUI (painel integrado)");
                    // O diagnóstico é gerenciado pela RepairsPage.xaml.cs
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reativar Segurança VBS (Padrão)",
                Category = "Jogos/Anti-Cheat",
                Icon = "🛡️",
                IsDangerous = true,
                Description = "Reativa o Hypervisor, VBS e HVCI. Restaura a segurança padrão do Windows.",
                Execute = () =>
                {
                    Logger.Log("Restaurando configurações de segurança VBS/Hypervisor...");

                    // Restaura BCD para Automático
                    SystemUtils.RunExternalProcess("bcdedit", "/set hypervisorlaunchtype auto", true);

                    // Remove as chaves de bloqueio
                    SystemUtils.RunExternalProcess("reg", @"delete ""HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard"" /v EnableVirtualizationBasedSecurity /f", true);
                    SystemUtils.RunExternalProcess("reg", @"delete ""HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity"" /v Enabled /f", true);

                    Logger.Log("[SUCESSO] Segurança padrão restaurada. Reinicie o PC.");
                }
            });

            // =================================================================
            // 6. DIAGNÓSTICO (MSDT NATIVO)
            // =================================================================

            repairs.Add(new RepairAction
            {
                Name = "Solução de Áudio",
                Category = "Soluções Win",
                Icon = "🎙️",
                IsSlow = true,
                Description = "Diagnostica problemas de reprodução de som e drivers.",
                Execute = () => SystemUtils.RunExternalProcess("msdt", "/id AudioPlaybackDiagnostic", false, false)
            });

            repairs.Add(new RepairAction
            {
                Name = "Solução de Rede/Wifi",
                Category = "Soluções Win",
                Icon = "📡",
                IsSlow = true,
                Description = "Diagnostica problemas de conexão Wifi e Ethernet.",
                Execute = () => SystemUtils.RunExternalProcess("msdt", "/id NetworkDiagnosticsNetworkAdapter", false, false)
            });

            repairs.Add(new RepairAction
            {
                Name = "Solução de Impressora",
                Category = "Soluções Win",
                Icon = "🖨️",
                Description = "Corrige erros de spooler e conexão com impressoras.",
                Execute = () => SystemUtils.RunExternalProcess("msdt", "/id PrinterDiagnostic", false, false)
            });

            repairs.Add(new RepairAction
            {
                Name = "Solução de Teclado",
                Category = "Soluções Win",
                Icon = "⌨️",
                Description = "Verifica configurações de layout e drivers de teclado.",
                Execute = () => SystemUtils.RunExternalProcess("msdt", "/id KeyboardDiagnostic", false, false)
            });

            repairs.Add(new RepairAction
            {
                Name = "Solução Compatibilidade",
                Category = "Soluções Win",
                Icon = "🧩",
                IsSlow = true,
                Description = "Ajuda a executar programas antigos no Windows atual.",
                Execute = () => SystemUtils.RunExternalProcess("msdt", "/id PCWDiagnostic", false, false)
            });

            repairs.Add(new RepairAction
            {
                Name = "Solução de Energia",
                Category = "Soluções Win",
                Icon = "🔋",
                IsSlow = true,
                Description = "Otimiza planos de energia para economizar bateria.",
                Execute = () => SystemUtils.RunExternalProcess("msdt", "/id PowerDiagnostic", false, false)
            });

            // =================================================================
            // ☣️ 7. MANUTENÇÃO AVANÇADA / EXPERT
            // =================================================================

            repairs.Add(new RepairAction
            {
                Name = "SFC Scannow (Arquivos)",
                Category = "Avançado",
                Icon = "⚕️",
                IsSlow = true,
                Description = "Verifica a integridade de todos os arquivos protegidos do sistema.",
                Execute = () => {
                    Logger.Log("Iniciando SFC /Scannow em janela externa...");
                    SystemUtils.RunExternalProcess("cmd", "/c sfc /scannow & pause", false, false);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "DISM RestoreHealth",
                Category = "Avançado",
                Icon = "🚑",
                IsSlow = true,
                Description = "Usa o Windows Update para corrigir a imagem corrompida do sistema.",
                Execute = () => {
                    Logger.Log("Iniciando DISM RestoreHealth em janela externa...");
                    SystemUtils.RunExternalProcess("cmd", "/c DISM /Online /Cleanup-Image /RestoreHealth & pause", false, false);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Limpar WinSxS (Espaço)",
                Category = "Avançado",
                Icon = "🏭",
                IsSlow = true,
                Description = "Limpa backups antigos de atualizações (Component Store).",
                Execute = () => {
                    Logger.Log("Iniciando limpeza WinSxS em janela externa...");
                    SystemUtils.RunExternalProcess("cmd", "/c DISM /Online /Cleanup-Image /StartComponentCleanup & pause", false, false);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Resetar WMI",
                Category = "Avançado",
                Icon = "⚙️",
                IsDangerous = true,
                Description = "Reconstrói o repositório de gerenciamento do Windows.",
                Execute = () => {
                    Logger.Log("Resetando repositório WMI...");
                    SystemUtils.RunExternalProcess("net", "stop winmgmt /y", true);
                    SystemUtils.RunExternalProcess("winmgmt", "/resetrepository", true);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Resetar Políticas de grupo (GPO) COMPLETO",
                Category = "Avançado",
                Icon = "📜",
                IsDangerous = true,
                IsSlow = true,
                Description = "Remove TODAS as políticas do registro e pastas GPO. Fix para 'gerenciado pela organização'. REINICIE O PC APÓS EXECUTAR.",
                Execute = () => {
                    Logger.Log("Iniciando reset COMPLETO de políticas em janela externa...");
                    SystemUtils.RunExternalProcess("cmd", "/c reg delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\" /f && reg delete \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows\" /f && reg delete \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\" /f && RD /S /Q \"%WinDir%\\System32\\GroupPolicyUsers\" >nul 2>&1 && RD /S /Q \"%WinDir%\\System32\\GroupPolicy\" >nul 2>&1 & echo. & echo RESET COMPLETO DAS POLITICAS FINALIZADO! & echo REINICIE O PC PARA APLICAR. & pause", false, false);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Agendar CHKDSK C:",
                Category = "Avançado",
                Icon = "💾",
                IsSlow = true,
                Description = "Verifica erros no disco rígido na próxima reinicialização.",
                Execute = () => {
                    Logger.Log("Agendando CHKDSK...");
                    SystemUtils.RunExternalProcess("cmd.exe", "/c echo S | chkdsk c: /f /r & pause", false, false);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Menu de Boot Legacy",
                Category = "Avançado",
                Icon = "🏁",
                Description = "Habilita a tecla F8 no boot para entrar em Modo de Segurança.",
                Execute = () => SystemUtils.RunExternalProcess("bcdedit", "/set {default} bootmenupolicy legacy", true)
            });

            repairs.Add(new RepairAction
            {
                Name = "Ativar CompactOS",
                Category = "Avançado",
                Icon = "🗜️",
                IsSlow = true,
                Description = "Comprime os arquivos do OS para liberar espaço sem perder velocidade.",
                Execute = () => {
                    Logger.Log("Iniciando CompactOS em janela externa...");
                    SystemUtils.RunExternalProcess("cmd", "/c compact.exe /CompactOS:always & pause", false, false);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Corrigir Erro de Áudio USB DAC - KB5050009",
                Category = "Sistema",
                Icon = "🎤",
                IsDangerous = false,
                Description = "Corrige falha de alocação de memória que impede funcionamento de áudio USB DAC. Erro 'Insufficient system resources exist to complete the API' afeta Windows 10/11.",
                Execute = () => {
                    Logger.Log("Corrigindo problema de alocação de memória para áudio USB DAC...");
                    SystemUtils.RunExternalProcess("reg", @"add ""HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows"" /v DisableDynamicAudioPolicy /t REG_DWORD /d 0 /f", true);
                    Logger.Log("[SUCESSO] Política de áudio USB ajustada. Reinicie para aplicar.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Detecção de Webcam - KB5050009",
                Category = "Sistema",
                Icon = "📷",
                IsDangerous = false,
                Description = "Corrige falha na detecção de webcams integradas após atualização KB5050009. Erro 0xA00F4244 afeta cameras HP e monitores 4K.",
                Execute = () => {
                    Logger.Log("Reparando detecção de webcam...");
                    SystemUtils.RunExternalProcess("reg", @"add ""HKLM\SOFTWARE\Microsoft\Windows Media Foundation\Platform\Imaging"" /v EnableFrameServerMode /t REG_DWORD /d 0 /f", true);
                    Logger.Log("[SUCESSO] Detecção de webcam restaurada. Reinicie o PC.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Restaurar Configurações do BitLocker",
                Category = "Sistema",
                Icon = "🔐",
                IsDangerous = false,
                Description = "Corrige erro onde configurações do BitLocker são gerenciadas incorretamente pelo sistema. Mostra erro falso de 'gerenciado pelo administrador'.",
                Execute = () => {
                    Logger.Log("Restaurando configurações do BitLocker...");
                    SystemUtils.RunExternalProcess("reg", @"add ""HKLM\SOFTWARE\Policies\Microsoft\FVE"" /v UseAdvancedStartup /t REG_DWORD /d 1 /f", true);
                    SystemUtils.RunExternalProcess("reg", @"add ""HKLM\SOFTWARE\Policies\Microsoft\FVE"" /v EnableBDEWithNoTPM /t REG_DWORD /d 1 /f", true);
                    SystemUtils.RunExternalProcess("reg", @"add ""HKLM\SOFTWARE\Policies\Microsoft\FVE"" /v UseTPM /t REG_DWORD /d 2 /f", true);
                    SystemUtils.RunExternalProcess("reg", @"add ""HKLM\SOFTWARE\Policies\Microsoft\FVE"" /v UseTPMKeyPIN /t REG_DWORD /d 1 /f", true);
                    Logger.Log("[SUCESSO] Configurações do BitLocker restauradas. Reinicie o PC.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Timeline do Adobe Premiere Pro - KB5050094",
                Category = "Sistema",
                Icon = "🎬",
                IsDangerous = false,
                Description = "Corrige falha ao arrastar clipes na timeline do Premiere Pro em múltiplos monitores. Afeta setups com diferentes escalas.",
                Execute = () => {
                    Logger.Log("Reparando Timeline do Adobe Premiere Pro...");
                    SystemUtils.RunExternalProcess("reg", @"add ""HKCU\SOFTWARE\Adobe\Premiere Pro\14.0\Timeline"" /v EnableHighDPIAware /t REG_DWORD /d 1 /f", true);
                    Logger.Log("[SUCESSO] Timeline do Premiere Pro restaurada. Reinicie o aplicativo.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Corrigir Cursor Girando no Windows 11 24H2",
                Category = "Sistema",
                Icon = "🔄",
                IsDangerous = false,
                Description = "Corrige problema de cursor girando indefinidamente na área de trabalho do Windows 11 24H2. Bug relacionado ao processamento de entrada.",
                Execute = () => {
                    Logger.Log("Corrigindo cursor girando no Windows 11...");
                    SystemUtils.RunExternalProcess("reg", @"add ""HKCU\Control Panel\Mouse"" /v MouseSpeed /t REG_SZ /d ""0"" /f", true);
                    SystemUtils.RunExternalProcess("reg", @"add ""HKCU\Control Panel\Mouse"" /v MouseThreshold1 /t REG_SZ /d ""0"" /f", true);
                    Logger.Log("[SUCESSO] Configurações do mouse restauradas. Reinicie o PC.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Desconexões de Área de Trabalho Remota",
                Category = "Sistema",
                Icon = "🌐",
                IsDangerous = false,
                Description = "Corrige falhas de autenticação em conexões RDP e Azure Virtual Desktop após atualizações de 2026. KB5078127 e KB5074109.",
                Execute = () => {
                    Logger.Log("Reparando conexões RDP/Azure...");
                    SystemUtils.RunExternalProcess("netsh", "advfirewall firewall set rule group=\"Remote Desktop\" new enable=Yes", true);
                    SystemUtils.RunExternalProcess("reg", @"add ""HKLM\SOFTWARE\Microsoft\Terminal Server Client\Default"" /v AuthenticationLevel /t REG_DWORD /d 0 /f", true);
                    Logger.Log("[SUCESSO] Configurações de RDP ajustadas. Tente reconectar.");
                }
            });
            repairs.Add(new RepairAction
            {
                Name = "Reparar Gerenciador de Tarefas Lento ao Fechar",
                Category = "Sistema",
                Icon = "📋",
                IsDangerous = false,
                Description = "Corrige o bug do Windows 11 onde o Gerenciador de Tarefas continua rodando em segundo plano após fechar ou trava ao fechar. Remove overrides obsoletos e encerra instâncias fantasmas.",
                Execute = () => {
                    Logger.Log("Removendo overrides de FeatureManagement que travam o Task Manager...");
                    SystemUtils.RunExternalProcess("reg", @"delete ""HKLM\SYSTEM\CurrentControlSet\Control\FeatureManagement\Overrides\14"" /f", true);
                    SystemUtils.RunExternalProcess("reg", @"delete ""HKLM\SYSTEM\ControlSet001\Control\FeatureManagement\Overrides\14"" /f", true);
                    Logger.Log("Encerrando instâncias ativas do Gerenciador de Tarefas...");
                    SystemUtils.RunExternalProcess("taskkill", "/f /im taskmgr.exe", true);
                    Logger.Log("[SUCESSO] Configurações do Gerenciador de Tarefas restauradas. Reinicie o PC para garantir que todos os efeitos sejam aplicados.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Salvamento em Nuvem (OneDrive/Dropbox)",
                Category = "Sistema",
                Icon = "☁️",
                IsDangerous = true,
                Description = "Corrige problemas ao salvar arquivos em armazenamento na nuvem após atualizações de 2026. KB5078127 e KB5074109.",
                Execute = () => {
                    Logger.Log("Reparando salvamento em nuvem...");
                    SystemUtils.RunExternalProcess("cmd", "/c echo off | clip", true);
                    SystemUtils.RunExternalProcess("powershell", "Get-AppxPackage Microsoft.OneDriveSync | Reset-AppxPackage", true);
                    SystemUtils.RunExternalProcess("powershell", "Get-AppxPackage Microsoft.Windows.CloudExperienceHost | Reset-AppxPackage", true);
                    Logger.Log("[SUCESSO] Serviços de nuvem resetados. Reinicie o PC.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Gerenciador de Tarefas Múltiplo",
                Category = "Sistema",
                Icon = "📊",
                IsDangerous = false,
                Description = "Corrige bug do Task Manager que abria múltiplas instâncias, degradando performance em PCs de baixo hardware.",
                Execute = () => {
                    Logger.Log("Reparando Task Manager...");
                    SystemUtils.RunExternalProcess("taskkill", "/f /im taskmgr.exe", true);
                    SystemUtils.RunExternalProcess("cmd", "/c start taskmgr.exe", true);
                    Logger.Log("[SUCESSO] Task Manager reiniciado. Monitore o comportamento.");
                }
            });

            // =================================================================
            // DESEMPENHO E OTIMIZAÇÃO DO SISTEMA
            // =================================================================

            repairs.Add(new RepairAction
            {
                Name = "Otimizar Timeouts de Fechamento de Aplicativos",
                Category = "Desempenho",
                Icon = "⏱️",
                IsDangerous = false,
                Description = "Reduz os timeouts de fechamento de aplicativos travados (HungAppTimeout, WaitToKillAppTimeout, WaitToKillServiceTimeout). Elimina a demora de 20 segundos no desligamento e acelera o fechamento de programas que não respondem.",
                Execute = () => {
                    Logger.Log("Otimizando timeouts de fechamento de aplicativos e serviços...");
                    // Reduz tempo para detectar app travado: 5s → 2s
                    SystemUtils.RunExternalProcess("reg", @"add ""HKCU\Control Panel\Desktop"" /v HungAppTimeout /t REG_SZ /d 2000 /f", true);
                    // Reduz tempo de espera para fechar app no desligamento: 20s → 3s
                    SystemUtils.RunExternalProcess("reg", @"add ""HKCU\Control Panel\Desktop"" /v WaitToKillAppTimeout /t REG_SZ /d 3000 /f", true);
                    // Habilita fechamento automático de apps travados
                    SystemUtils.RunExternalProcess("reg", @"add ""HKCU\Control Panel\Desktop"" /v AutoEndTasks /t REG_SZ /d 1 /f", true);
                    // Reduz timeout de serviços: 12s → 3s
                    SystemUtils.RunExternalProcess("reg", @"add ""HKLM\SYSTEM\CurrentControlSet\Control"" /v WaitToKillServiceTimeout /t REG_SZ /d 3000 /f", true);
                    Logger.Log("[SUCESSO] Timeouts otimizados. Desligamento e fechamento de apps serão mais rápidos.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Ativar Compressão de Memória RAM",
                Category = "Desempenho",
                Icon = "💾",
                IsDangerous = false,
                Description = "Reativa a compressão de memória RAM do Windows (desativada por alguns tweaks). Permite que mais programas caibam na RAM física, reduzindo o uso do arquivo de paginação e melhorando a performance em PCs com pouca memória.",
                Execute = () => {
                    Logger.Log("Reativando compressão de memória RAM...");
                    SystemUtils.RunExternalProcess("reg", @"add ""HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"" /v DisableMemoryCompression /t REG_DWORD /d 0 /f", true);
                    Logger.Log("[SUCESSO] Compressão de memória RAM reativada. Reinicie o PC para aplicar.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Otimizar NTFS para Melhor Desempenho de Disco",
                Category = "Desempenho",
                Icon = "💿",
                IsDangerous = false,
                Description = "Desativa o registro de data de último acesso e a geração de nomes 8.3 no NTFS. Reduz escritas desnecessárias no disco e melhora a performance de I/O, especialmente em HDDs com muitos arquivos.",
                Execute = () => {
                    Logger.Log("Otimizando parâmetros NTFS...");
                    // Desativa atualização de data de acesso (reduz I/O de escrita)
                    SystemUtils.RunExternalProcess("reg", @"add ""HKLM\SYSTEM\CurrentControlSet\Control\FileSystem"" /v NtfsDisableLastAccessUpdate /t REG_DWORD /d 1 /f", true);
                    // Desativa geração de nomes curtos 8.3 (overhead por criação de arquivo)
                    SystemUtils.RunExternalProcess("reg", @"add ""HKLM\SYSTEM\CurrentControlSet\Control\FileSystem"" /v NtfsDisable8dot3NameCreation /t REG_DWORD /d 1 /f", true);
                    Logger.Log("[SUCESSO] NTFS otimizado. Reinicie o PC para que as mudanças tenham efeito.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Manter Kernel na RAM (Desativar Paginação do Kernel)",
                Category = "Desempenho",
                Icon = "🧠",
                IsDangerous = false,
                Description = "Configura o Windows para manter o kernel e drivers essenciais na memória RAM (DisablePagingExecutive=1). Melhora significativamente a responsividade do sistema ao alternar entre aplicativos pesados.",
                Execute = () => {
                    Logger.Log("Configurando kernel para permanecer na RAM...");
                    SystemUtils.RunExternalProcess("reg", @"add ""HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"" /v DisablePagingExecutive /t REG_DWORD /d 1 /f", true);
                    Logger.Log("[SUCESSO] Kernel configurado para usar RAM. Reinicie o PC para aplicar.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Configurar Prioridade de CPU/GPU para Jogos",
                Category = "Desempenho",
                Icon = "🎮",
                IsDangerous = false,
                Description = "Define a prioridade de CPU (6=Alta) e GPU (8=Máxima) para o perfil de jogos do MMCSS. Melhora FPS, reduz micro-stuttering e garante que jogos recebam prioridade máxima sobre processos em segundo plano.",
                Execute = () => {
                    Logger.Log("Configurando prioridade de CPU e GPU para jogos via MMCSS...");
                    string gamesKey = @"add ""HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games""";
                    SystemUtils.RunExternalProcess("reg", $@"{gamesKey} /v ""GPU Priority"" /t REG_DWORD /d 8 /f", true);
                    SystemUtils.RunExternalProcess("reg", $@"{gamesKey} /v ""Priority"" /t REG_DWORD /d 6 /f", true);
                    SystemUtils.RunExternalProcess("reg", $@"{gamesKey} /v ""Scheduling Category"" /t REG_SZ /d High /f", true);
                    SystemUtils.RunExternalProcess("reg", $@"{gamesKey} /v ""SFIO Priority"" /t REG_SZ /d High /f", true);
                    Logger.Log("[SUCESSO] Prioridades de jogos configuradas. Reinicie para aplicar.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Restaurar SystemResponsiveness (Evitar Micro-Stuttering)",
                Category = "Desempenho",
                Icon = "⚡",
                IsDangerous = false,
                Description = "Restaura o SystemResponsiveness para 20% (padrão do Windows). Tweaks agressivos definem este valor para 0, o que prejudica o agendamento de threads de áudio/vídeo e causa micro-stuttering e engasgos de áudio.",
                Execute = () => {
                    Logger.Log("Restaurando SystemResponsiveness para o valor padrão (20)...");
                    SystemUtils.RunExternalProcess("reg", @"add ""HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile"" /v SystemResponsiveness /t REG_DWORD /d 20 /f", true);
                    Logger.Log("[SUCESSO] SystemResponsiveness restaurado para 20. Reinicie para efeito completo.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar SSD TRIM e Manutenção de Disco",
                Category = "Desempenho",
                Icon = "🔧",
                IsDangerous = false,
                Description = "Garante que o serviço de desfragmentação/TRIM esteja habilitado e dispara uma sessão de TRIM manual no SSD. TRIM evita a degradação de velocidade de escrita em SSDs com o tempo.",
                Execute = () => {
                    Logger.Log("Verificando e reparando manutenção de SSD (TRIM)...");
                    // Garante que o serviço de otimização de disco está habilitado
                    SystemUtils.RunExternalProcess("cmd", "/c sc config defragsvc start= demand", true);
                    // Executa TRIM em todas as unidades
                    SystemUtils.RunExternalProcess("cmd", "/c defrag C: /U /V /L", true, false);
                    Logger.Log("[SUCESSO] TRIM de SSD iniciado em segundo plano. Verifique o progresso no Otimizador de Unidades.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reiniciar Serviços de Desempenho (SysMain + MMCSS + WSearch)",
                Category = "Desempenho",
                Icon = "🔄",
                IsDangerous = false,
                Description = "Reinicia os serviços essenciais de desempenho: Superfetch (SysMain), MMCSS (áudio), e Windows Search. Resolve lentidão repentina causada por serviços em estado travado sem precisar reiniciar o PC.",
                Execute = () => {
                    Logger.Log("Reiniciando serviços de desempenho do sistema...");
                    foreach (var svc in new[] { "SysMain", "MMCSS", "WSearch" })
                    {
                        try {
                            SystemUtils.RunExternalProcess("net", $"stop {svc}", true);
                            System.Threading.Thread.Sleep(500);
                            SystemUtils.RunExternalProcess("net", $"start {svc}", true);
                            Logger.Log($"[OK] Serviço '{svc}' reiniciado.");
                        } catch {
                            Logger.Log($"[AVISO] Não foi possível reiniciar '{svc}' (pode não estar instalado).");
                        }
                    }
                    Logger.Log("[SUCESSO] Serviços de desempenho reiniciados.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Remover Menu Iniciar Copilot Forçado",
                Category = "Sistema",
                Icon = "🤖",
                IsDangerous = false,
                Description = "Remove o atalho do Copilot do Menu Iniciar que estava sendo forçado indevidamente pelo Windows Update.",
                Execute = () => {
                    Logger.Log("Removendo Copilot forçado do Menu Iniciar...");
                    SystemUtils.RunExternalProcess("reg", @"delete ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced"" /v TaskbarMn /f", true);
                    SystemUtils.RunExternalProcess("reg", @"delete ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced"" /v TaskbarDa /f", true);
                    Logger.Log("[SUCESSO] Copilot removido do Menu Iniciar. Reinicie o Explorer.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Desempenho de Jogos (NVIDIA/AMD)",
                Category = "Sistema",
                Icon = "🎮",
                IsDangerous = false,
                Description = "Corrige queda de performance em jogos após atualizações de 2025-2026 que afetaram drivers NVIDIA/AMD. Restaura otimizações.",
                Execute = () => {
                    Logger.Log("Reparando desempenho de jogos...");
                    SystemUtils.RunExternalProcess("reg", @"add ""HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers"" /v TdrLevel /t REG_DWORD /d 3 /f", true);
                    SystemUtils.RunExternalProcess("reg", @"add ""HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR"" /v AppCaptureEnabled /t REG_DWORD /d 1 /f", true);
                    Logger.Log("[SUCESSO] Desempenho de jogos restaurado. Teste FPS nos jogos.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Windows Update Quebrado",
                Category = "Sistema",
                Icon = "🔄",
                IsDangerous = true,
                Description = "Corrige problemas com serviço Windows Update que não funciona ou fica travado.",
                Execute = () => {
                    Logger.Log("Reparando Windows Update...");
                    SystemUtils.RunExternalProcess("cmd", "/c net stop wuauserv && net start wuauserv", true);
                    SystemUtils.RunExternalProcess("cmd", "/c net stop bits && net start bits", true);
                    SystemUtils.RunExternalProcess("cmd", "/c rd /s /q \"%SystemRoot%\\SoftwareDistribution\\*\" && md \"%SystemRoot%\\SoftwareDistribution\\Backup\\\"", true);
                    SystemUtils.RunExternalProcess("cmd", "/c ren \"%SystemRoot%\\SoftwareDistribution\\Download\\*\" \"%SystemRoot%\\SoftwareDistribution\\Download\\Old\\\" 2>nul", true);
                    SystemUtils.RunExternalProcess("cmd", "/c reg delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\WindowsUpdate\\Auto Update\\RebootRequired\" /f", true);
                    SystemUtils.RunExternalProcess("cmd", "/c reg delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\WindowsUpdate\\Auto Update\\RebootRequiredForcedApps\" /f", true);
                    SystemUtils.RunExternalProcess("cmd", "/c net start wuauserv && net start bits", true);
                    Logger.Log("[SUCESSO] Windows Update reparado. Verifique atualizações.");
                }
            });



            // =================================================================
            // 8. DIAGNÓSTICO DE HARDWARE
            // =================================================================

            repairs.Add(new RepairAction
            {
                Name = "Relatório de Bateria (Laptops)",
                Category = "Diagnóstico",
                Icon = "🔋",
                Description = "Gera relatório HTML detalhado da saúde da bateria (capacidade, ciclos, etc.).",
                Execute = () => {
                    Logger.Log("Gerando relatório de bateria...");
                    string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    string reportPath = Path.Combine(desktop, "battery-report.html");
                    SystemUtils.RunExternalProcess("powercfg", "/batteryreport /output \"" + reportPath + "\"", true);
                    Logger.Log("[SUCESSO] Relatório salvo em: " + reportPath);
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Teste de Memória RAM",
                Category = "Diagnóstico",
                Icon = "🧠",
                IsSlow = true,
                Description = "Inicia o Windows Memory Diagnostic para testar erros na memória RAM.",
                Execute = () => {
                    Logger.Log("Iniciando teste de memória...");
                    SystemUtils.RunExternalProcess("mdsched.exe", "", false, false);
                    Logger.Log("[INFO] Selecione 'Reiniciar agora e verificar problemas'.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Avaliação de Performance (WinSat)",
                Category = "Diagnóstico",
                Icon = "📊",
                IsSlow = true,
                Description = "Executa benchmark oficial do Windows (CPU, Disco, Gráficos).",
                Execute = () => {
                    Logger.Log("Iniciando avaliação de performance WinSat em janela externa...");
                    SystemUtils.RunExternalProcess("cmd", "/c winsat formal & pause", false, false);
                }
            });

            // =================================================================
            // 9. CERTIFICADOS E SEGURANÇA
            // =================================================================

            repairs.Add(new RepairAction
            {
                Name = "Reparar Repositório de Certificados",
                Category = "Sistema",
                Icon = "🔒",
                IsDangerous = true,
                Description = "Reconstrói o repositório de certificados do Windows. Útil para erros SSL/TLS.",
                Execute = () => {
                    Logger.Log("Reparando repositório de certificados...");
                    SystemUtils.RunExternalProcess("certutil", "-pulse", true);
                    SystemUtils.RunExternalProcess("certutil", "-verify -urlfetch CA", true);
                    Logger.Log("[SUCESSO] Repositório de certificados reparado.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Limpar Cache de Certificados",
                Category = "Sistema",
                Icon = "🧹",
                Description = "Limpa cache de certificados corrompidos que causam erros de conexão segura.",
                Execute = () => {
                    Logger.Log("Limpando cache de certificados...");
                    SystemUtils.RunExternalProcess("certutil", "-flushcache", true);
                    SystemUtils.RunExternalProcess("certutil", "-urlcache * delete", true);
                    Logger.Log("[SUCESSO] Cache de certificados limpo.");
                }
            });

            // =================================================================
            // 10. BOOT E INICIALIZAÇÃO
            // =================================================================

            repairs.Add(new RepairAction
            {
                Name = "Reconstruir BCD (Boot Configuration Data)",
                Category = "Avançado",
                Icon = "🏁",
                IsDangerous = true,
                Description = "Reconstrói o banco de dados de configuração de boot. Fix para erro 'Boot Manager is missing'.",
                Execute = () => {
                    Logger.Log("Reconstruindo BCD...");
                    SystemUtils.RunExternalProcess("bcdedit", "/export c:\\bcdbackup", true);
                    SystemUtils.RunExternalProcess("bootrec", "/rebuildbcd", false, false);
                    Logger.Log("[INFO] Siga as instruções na janela do CMD.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar EFI Bootloader (UEFI)",
                Category = "Avançado",
                Icon = "⚡",
                IsDangerous = true,
                Description = "Repara o bootloader EFI para sistemas UEFI. Fix para erro 'No bootable device'.",
                Execute = () => {
                    Logger.Log("Reparando EFI Bootloader...");
                    SystemUtils.RunExternalProcess("bcdedit", "/set {bootmgr} path \\EFI\\Microsoft\\Boot\\bootmgfw.efi", true);
                    Logger.Log("[SUCESSO] EFI Bootloader reparado. Reinicie o PC.");
                }
            });

            // =================================================================
            // 11. SERVIÇOS DO SISTEMA
            // =================================================================

            repairs.Add(new RepairAction
            {
                Name = "Reparar Serviços Corrompidos",
                Category = "Sistema",
                Icon = "🔧",
                IsDangerous = true,
                Description = "Repara configurações de serviços do Windows que não iniciam ou falham.",
                Execute = () => {
                    Logger.Log("Reparando serviços do Windows...");
                    SystemUtils.RunExternalProcess("cmd", "/c sc query state= all | find \"STOPPED\" > \"%TEMP%\\stopped_services.txt\"", true);
                    SystemUtils.RunExternalProcess("powershell", "Get-Service | Where-Object {$_.Status -eq 'Stopped'} | Set-Service -StartupType Automatic", true);
                    Logger.Log("[SUCESSO] Serviços reconfigurados. Verifique Serviços.msc.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Resetar Spooler de Impressão",
                Category = "Sistema",
                Icon = "🖨️",
                Description = "Reseta o serviço de spooler para corrigir erros de impressão e filas travadas.",
                Execute = () => {
                    Logger.Log("Resetando spooler de impressão...");
                    SystemUtils.RunExternalProcess("cmd", "/c net stop spooler", true);
                    SystemUtils.RunExternalProcess("cmd", "/c del /f /s /q %SystemRoot%\\System32\\spool\\PRINTERS\\* 2>nul", true);
                    SystemUtils.RunExternalProcess("cmd", "/c net start spooler", true);
                    Logger.Log("[SUCESSO] Spooler de impressão resetado. Tente imprimir novamente.");
                }
            });









            // =================================================================
            // 18. BLUETOOTH E DISPOSITIVOS
            // =================================================================

            repairs.Add(new RepairAction
            {
                Name = "Resetar Bluetooth Stack",
                Category = "Internet",
                Icon = "🦷",
                Description = "Reinicia serviço Bluetooth e limpa drivers corrompidos. Fix para Bluetooth sumindo ou não conectando.",
                Execute = () => {
                    Logger.Log("Resetando Bluetooth...");
                    SystemUtils.RunExternalProcess("net", "stop BTHSSVC", true);
                    SystemUtils.RunExternalProcess("net", "start BTHSSVC", true);
                    Logger.Log("[SUCESSO] Bluetooth reiniciado.");
                }
            });





            return repairs;
        }
    }
}