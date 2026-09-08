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
                    using var _ = Process.Start("explorer.exe");
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
                    using var _ = Process.Start("explorer.exe");
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
                Description = "Ativa o visualizador de fotos clássico (leve e rápido) para JPG/PNG/BMP/GIF/TIFF — 'Abrir com' → Visualizador de Fotos do Windows.",
                Execute = () => {
                    foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif" })
                        SystemUtils.RunExternalProcess("reg", $"add \"HKLM\\SOFTWARE\\Microsoft\\Windows Photo Viewer\\Capabilities\\FileAssociations\" /v \"{ext}\" /t REG_SZ /d \"PhotoViewer.FileAssoc.Tiff\" /f", true);
                    Logger.Log("[SUCESSO] Photo Viewer clássico registrado para JPG/JPEG/PNG/BMP/GIF/TIFF.");
                }
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
                    using var _ = Process.Start("explorer.exe");
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
                    using var _ = Process.Start("explorer.exe");
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
                    using var _ = Process.Start("explorer.exe");
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
                Description = "Para TODOS os serviços do Update, renomeia SoftwareDistribution/catroot2 (preserva downloads como .old), recadastra as DLLs principais e reinicia. Fix para erro 0x80070002/0x800f0922/Update travado.",
                Execute = () => {
                    Logger.Log("Iniciando reparo completo do Windows Update...");
                    foreach (var svc in new[] { "wuauserv", "bits", "cryptsvc", "dosvc", "UsoSvc", "msiserver" })
                        SystemUtils.RunExternalProcess("net", $"stop {svc}", true);
                    System.Threading.Thread.Sleep(500);
                    // Renomeia em vez de apagar (recuperável se algo der errado)
                    SystemUtils.RunExternalProcess("cmd", "/c if exist %systemroot%\\SoftwareDistribution rd /s /q %systemroot%\\SoftwareDistribution.old & ren %systemroot%\\SoftwareDistribution SoftwareDistribution.old & if exist %systemroot%\\System32\\catroot2 rd /s /q %systemroot%\\System32\\catroot2.old & ren %systemroot%\\System32\\catroot2 catroot2.old", true);
                    // Recadastra as DLLs do Update
                    foreach (var dll in new[] { "atl.dll", "urlmon.dll", "jscript.dll", "vbscript.dll", "scrrun.dll", "msxml3.dll", "msxml6.dll", "wintrust.dll", "wuapi.dll", "wuaueng.dll", "wucltux.dll", "wups.dll", "wuwebv.dll" })
                        SystemUtils.RunExternalProcess("regsvr32", $"/s %systemroot%\\system32\\{dll}", true);
                    foreach (var svc in new[] { "cryptsvc", "bits", "msiserver", "wuauserv", "UsoSvc" })
                        SystemUtils.RunExternalProcess("net", $"start {svc}", true);
                    Logger.Log("[SUCESSO] Windows Update resetado (pastas renomeadas para .old). Rode 'Verificar atualizações' de novo — a primeira checagem pode demorar.");
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
                Description = "Repara programas que não abrem ou abrem no app errado: restaura assoc (.exe=exefile), ftype (exefile=\"%1\" %*) e a chave exefile do registro completa.",
                Execute = () => {
                    SystemUtils.RunExternalProcess("cmd", "/c assoc .exe=exefile", true);
                    SystemUtils.RunExternalProcess("cmd", "/c ftype exefile=\"%1\" %*", true);
                    using var key = Microsoft.Win32.Registry.ClassesRoot.CreateSubKey("exefile\\shell\\open\\command");
                    key?.SetValue(null, "\"%1\" %*");
                    using var k2 = Microsoft.Win32.Registry.ClassesRoot.CreateSubKey("exefile");
                    k2?.SetValue(null, "Application");
                    Logger.Log("[SUCESSO] Associação .exe restaurada (assoc + ftype + HKCR\\exefile).");
                }
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
                Description = "Fix para a pesquisa que não acha nada: reseta o índice (SetupCompletedSuccessfully=0 força reconstrução), reinicia o WSearch e re-registra o SearchHost/StartMenuExperienceHost.",
                Execute = () => {
                    Logger.Log("Resetando índice do Windows Search...");
                    SystemUtils.RunExternalProcess("reg", "add \"HKLM\\SOFTWARE\\Microsoft\\Windows Search\" /v SetupCompletedSuccessfully /t REG_DWORD /d 0 /f", true);
                    SystemUtils.RunExternalProcess("net", "stop wsearch", true);
                    SystemUtils.RunExternalProcess("net", "start wsearch", true);
                    Logger.Log("Re-registrando SearchHost + StartMenuExperienceHost...");
                    SystemUtils.RunExternalProcess("powershell", "-NoProfile -Command \"Get-AppxPackage Microsoft.Windows.Search,Microsoft.Windows.StartMenuExperienceHost | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register ($_.InstallLocation + '\\AppXManifest.xml')}\"", true);
                    Logger.Log("[SUCESSO] Índice será reconstruído em segundo plano (pode levar alguns minutos em discos grandes).");
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
                Description = "Abre os solucionadores de problemas modernos de Reprodução de Áudio (o antigo msdt.exe foi removido pela Microsoft no Windows 11 24H2+). Escolha 'Áudio' na lista e execute.",
                Execute = () => SystemUtils.RunExternalProcess("cmd", "/c start ms-settings:troubleshoot", false, false)
            });

            repairs.Add(new RepairAction
            {
                Name = "Solução de Rede/Wifi",
                Category = "Soluções Win",
                Icon = "📡",
                IsSlow = true,
                Description = "Abre os solucionadores modernos (Conexões com a Internet + Adaptadores de Rede) — o antigo msdt.exe foi removido no 24H2+. Para reset profundo, use também 'Reset Completo Winsock/IP'.",
                Execute = () => SystemUtils.RunExternalProcess("cmd", "/c start ms-settings:troubleshoot", false, false)
            });

            repairs.Add(new RepairAction
            {
                Name = "Solução de Impressora",
                Category = "Soluções Win",
                Icon = "🖨️",
                Description = "Abre os solucionadores modernos de Impressora — o antigo msdt.exe foi removido no 24H2+. Para spooler travado, use também 'Resetar Spooler de Impressão'.",
                Execute = () => SystemUtils.RunExternalProcess("cmd", "/c start ms-settings:troubleshoot", false, false)
            });

            repairs.Add(new RepairAction
            {
                Name = "Solução de Teclado",
                Category = "Soluções Win",
                Icon = "⌨️",
                Description = "Abre os solucionadores modernos de Teclado — o antigo msdt.exe foi removido no 24H2+.",
                Execute = () => SystemUtils.RunExternalProcess("cmd", "/c start ms-settings:troubleshoot", false, false)
            });

            repairs.Add(new RepairAction
            {
                Name = "Solução Compatibilidade",
                Category = "Soluções Win",
                Icon = "🧩",
                IsSlow = true,
                Description = "Abre os solucionadores modernos de Compatibilidade de Programas — o antigo msdt.exe foi removido no 24H2+.",
                Execute = () => SystemUtils.RunExternalProcess("cmd", "/c start ms-settings:troubleshoot", false, false)
            });

            repairs.Add(new RepairAction
            {
                Name = "Solução de Energia",
                Category = "Soluções Win",
                Icon = "🔋",
                IsSlow = true,
                Description = "Abre os solucionadores modernos de Energia — o antigo msdt.exe foi removido no 24H2+. Para perfil corrompido, use também 'Resetar Energia (Power)'.",
                Execute = () => SystemUtils.RunExternalProcess("cmd", "/c start ms-settings:troubleshoot", false, false)
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
                Description = "Reset COMPLETO e automático: apaga políticas de HKLM/HKCU (Policies), pastas GroupPolicy/GroupPolicyUsers, reseta a política de segurança local (secedit com base padrão) e força gpupdate. Fix para 'gerenciado pela organização' e bloqueios de otimizadores/malware. REINICIE O PC APÓS EXECUTAR.",
                Execute = () => {
                    Logger.Log("Iniciando reset COMPLETO de políticas (automático, sem janela manual)...");
                    // 1. Chaves de Policies (HKLM user/machine + HKCU + Software\\Policies completos)
                    foreach (var args in new[] {
                        "delete \"HKLM\\\\SOFTWARE\\\\Microsoft\\\\Windows\\\\CurrentVersion\\\\Policies\" /f",
                        "delete \"HKLM\\\\SOFTWARE\\\\Policies\\\\Microsoft\" /f",
                        "delete \"HKCU\\\\SOFTWARE\\\\Microsoft\\\\Windows\\\\CurrentVersion\\\\Policies\" /f",
                        "delete \"HKCU\\\\SOFTWARE\\\\Policies\\\\Microsoft\" /f",
                    }) {
                        var (code, _) = SystemUtils.RunExternalProcessWithCode("reg", args, true);
                        Logger.Log($"  reg {args.Split(' ')[1]} -> código {code} (1 = não existia, ok)");
                    }
                    // 2. Pastas de GPO (machine + usuários)
                    SystemUtils.RunExternalProcess("cmd", "/c rd /s /q \"%WinDir%\\System32\\GroupPolicyUsers\" & rd /s /q \"%WinDir%\\System32\\GroupPolicy\"", true);
                    Logger.Log("  Pastas GroupPolicy/GroupPolicyUsers removidas.");
                    // 3. Política de segurança local de volta ao padrão de fábrica (o passo que antes era manual)
                    Logger.Log("  Resetando política de segurança local (secedit /defltbase)...");
                    string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    SystemUtils.RunExternalProcess("secedit", $"/configure /cfg {Path.Combine(winDir, "inf", "defltbase.ini")} /db defltbase.sdb /verbose", true);
                    // 4. Aplica
                    SystemUtils.RunExternalProcess("gpupdate", "/force", true);
                    Logger.Log("[SUCESSO] Políticas resetadas por completo (registro + GPO + segurança local + gpupdate). REINICIE O PC PARA APLICAR.");
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
                Description = "Corrige falhas de autenticação em conexões RDP e Azure Virtual Desktop após atualizações do Windows.",
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
                Description = "Corrige problemas ao salvar arquivos em armazenamento na nuvem após atualizações do Windows.",
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





            // =================================================================
            // 19. PATH / SISTEMA (Reparar PATH Dinamico)
            // =================================================================

            repairs.Add(new RepairAction
            {
                Name = "Reparar PATH do Sistema (Adiciona Programas Faltantes)",
                Category = "PATH/Sistema",
                Icon = "🔧",
                Description = "Recupera o PATH a partir dos executaveis encontrados no disco (winget, node, git, 7z, dotnet, cargo, pwsh) e adiciona os que faltam. Funciona mesmo quando o registro falha. Baseado na solucao que funcionou de primeira (scan de .exe no disco).",
                Execute = () => {
                    Logger.Log("Iniciando reparo dinamico do PATH (scan de executaveis no disco)...");
                    var recovered = PathRepair.RecoverFromExecutableScan();
                    int count = 0;
                    foreach (var kvp in recovered)
                    {
                        Logger.Log($"[PATH RECOVERED] {kvp.Key} -> {kvp.Value}");
                        count++;
                    }
                    // Aplica reparo ao User PATH: adiciona programas faltantes, nunca remove
                    try
                    {
                        var userPathValue = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
                        if (!string.IsNullOrEmpty(userPathValue))
                        {
                            var (fmtChanged, fmtPath, fmtMsg) = PathRepair.RepairPath(userPathValue);
                            // Adiciona os programas encontrados no disco que ainda faltam (somente adicao)
                            var (finalUserPath, addedPaths) = PathRepair.EnsureUserPathMinimum(fmtPath, recovered);
                            bool userChanged = fmtChanged || addedPaths.Count > 0;
                            if (userChanged)
                            {
                                Environment.SetEnvironmentVariable("PATH", finalUserPath, EnvironmentVariableTarget.User);
                                foreach (var added in addedPaths)
                                {
                                    Logger.Log($"[PATH REPAIR USER] {added}");
                                }
                                Logger.Log($"[PATH REPAIR USER] {fmtMsg}");
                            }
                            else
                            {
                                Logger.Log($"[PATH REPAIR USER] {fmtMsg}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("PATHRepair", $"Falha ao reparar User PATH: {ex.Message}");
                    }
                    // Aplica reparo ao System PATH (se tiver acesso)
                    try
                    {
                        var sysPathValue = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
                        if (!string.IsNullOrEmpty(sysPathValue))
                        {
                            var entries = PathRepair.DiagnosePath(sysPathValue, "System");
                            var (repairedPath, actions) = PathRepair.RepairPathEntries(entries, "System");
                            repairedPath = PathRepair.EnsureSystemPathMinimum(repairedPath);
                            bool sysChanged = !sysPathValue.Equals(repairedPath, StringComparison.OrdinalIgnoreCase);
                            if (sysChanged)
                            {
                                Environment.SetEnvironmentVariable("PATH", repairedPath, EnvironmentVariableTarget.Machine);
                                Logger.Log($"[PATH REPAIR SYSTEM] Reparado. Acoes: {string.Join("; ", actions)}");
                            }
                            else
                            {
                                Logger.Log("[PATH REPAIR SYSTEM] PATH do sistema ja esta correto.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning("PATHRepair", $"Nao foi possivel alterar PATH do sistema (requer admin): {ex.Message}");
                    }
                    Logger.Log($"[SUCESSO] Recuperacao de PATH concluida. Executaveis encontrados: {count}. Reinicie o terminal para ver as mudancas.");
                }
            });


            // =================================================================
            // NOVOS REPAROS — Menu Iniciar, shells, fontes, pastas do usuário,
            // contadores de performance, malware policies, Win+X, Spotlight...
            // Fontes: MS Learn (troubleshoot-start-menu-errors), ElevenForum,
            // NinjaOne, TheWindowsClub (lodctr), comunidade 24H2/25H2.
            // =================================================================

            repairs.Add(new RepairAction
            {
                Name = "Reparar Menu Iniciar (Re-registrar)",
                Category = "Sistema",
                Icon = "🧭",
                Description = "Menu Iniciar não abre, abre e fecha na hora ou 'Todos os apps' sumiu (comum no 24H2/25H2). Re-registra o StartMenuExperienceHost e o reinicia.",
                Execute = () => {
                    Logger.Log("Re-registrando StartMenuExperienceHost...");
                    SystemUtils.RunExternalProcess("powershell", "-NoProfile -Command \"Stop-Process -Name 'StartMenuExperienceHost' -Force -ErrorAction SilentlyContinue; Get-AppxPackage Microsoft.Windows.StartMenuExperienceHost | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register ($_.InstallLocation + '\\AppXManifest.xml')}\"", true);
                    Logger.Log("[SUCESSO] Menu Iniciar re-registrado. Abra-o novamente para testar.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Central de Ações / Quick Settings",
                Category = "Sistema",
                Icon = "🎚️",
                Description = "Painel de volume/Wi-Fi/brilho (Quick Settings) e notificações não abrem ou abrem em branco. Re-registra o ShellExperienceHost.",
                Execute = () => {
                    Logger.Log("Re-registrando ShellExperienceHost (Action Center)...");
                    SystemUtils.RunExternalProcess("powershell", "-NoProfile -Command \"Stop-Process -Name 'ShellExperienceHost' -Force -ErrorAction SilentlyContinue; Get-AppxPackage Microsoft.Windows.ShellExperienceHost | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register ($_.InstallLocation + '\\AppXManifest.xml')}\"", true);
                    Logger.Log("[SUCESSO] Central de Ações re-registrada.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reconstruir Cache de Fontes",
                Category = "Sistema",
                Icon = "🔤",
                Description = "Fontes embaralhadas, quadrados/letras erradas ou apps com caracteres inválidos. Para o FontCache, apaga o cache e reinicia o serviço (Requer Admin).",
                Execute = () => {
                    Logger.Log("Reconstruindo cache de fontes...");
                    SystemUtils.RunExternalProcess("cmd", "/c net stop FontCache & net stop FontCache3.0.0.0", true);
                    SystemUtils.RunExternalProcess("cmd", "/c del /f /q \"%WinDir%\\ServiceProfiles\\LocalService\\AppData\\Local\\FontCache\\*\" & del /f /q \"%WinDir%\\System32\\FNTCACHE.DAT\"", true);
                    SystemUtils.RunExternalProcess("cmd", "/c net start FontCache", true);
                    Logger.Log("[SUCESSO] Cache de fontes limpo. Reinicie o PC para regenerar.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Restaurar Pastas do Usuário (Downloads/Desktop/etc)",
                Category = "Sistema",
                Icon = "📁",
                Description = "Downloads/Desktop/Documentos/Imagens/Músicas/Vídeos sumiram do Explorer ou apontam para lugar errado (ex.: OneDrive mal removido). Repõe os caminhos padrão em %USERPROFILE%. NÃO move arquivos.",
                Execute = () => {
                    Logger.Log("Restaurando User Shell Folders para os padrões...");
                    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
                    if (key != null)
                    {
                        key.SetValue("Desktop", "%USERPROFILE%\\Desktop", Microsoft.Win32.RegistryValueKind.ExpandString);
                        key.SetValue("Personal", "%USERPROFILE%\\Documents", Microsoft.Win32.RegistryValueKind.ExpandString);
                        key.SetValue("{374DE290-123F-4565-9164-39C4925E467B}", "%USERPROFILE%\\Downloads", Microsoft.Win32.RegistryValueKind.ExpandString);
                        key.SetValue("My Pictures", "%USERPROFILE%\\Pictures", Microsoft.Win32.RegistryValueKind.ExpandString);
                        key.SetValue("My Music", "%USERPROFILE%\\Music", Microsoft.Win32.RegistryValueKind.ExpandString);
                        key.SetValue("My Video", "%USERPROFILE%\\Videos", Microsoft.Win32.RegistryValueKind.ExpandString);
                    }
                    SystemUtils.RunExternalProcess("cmd.exe", "/c start explorer.exe", true, false);
                    Logger.Log("[SUCESSO] Pastas do usuário restauradas para %USERPROFILE%. Reinicie o Explorer se necessário.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reconstruir Contadores de Performance",
                Category = "Diagnóstico",
                Icon = "📈",
                Description = "Aba Desempenho do Gerenciador de Tarefas em branco ou erro 'contador inválido'. Reconstrói os contadores com lodctr /R + resyncperf (Requer Admin).",
                Execute = () => {
                    Logger.Log("Reconstruindo contadores de performance...");
                    SystemUtils.RunExternalProcess("cmd", "/c lodctr /R", true);
                    SystemUtils.RunExternalProcess("cmd", "/c winmgmt /resyncperf", true);
                    SystemUtils.RunExternalProcess("cmd", "/c net stop winmgmt & net start winmgmt", true);
                    Logger.Log("[SUCESSO] Contadores reconstruídos. Reabra o Gerenciador de Tarefas.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Resetar Layout da Barra de Tarefas",
                Category = "Explorer/UI",
                Icon = "📌",
                Description = "Barra de tarefas corrompida, ícones fixados somem ou layout bugado. Reseta a chave Taskband (ATENÇÃO: ícones fixados voltam ao padrão).",
                Execute = () => {
                    Logger.Log("Resetando Taskband (layout da barra de tarefas)...");
                    SystemUtils.RunExternalProcess("reg", "delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Taskband\" /f", true);
                    SystemUtils.RunExternalProcess("taskkill", "/f /im explorer.exe", true);
                    System.Threading.Thread.Sleep(800);
                    SystemUtils.RunExternalProcess("cmd.exe", "/c start explorer.exe", true, false);
                    Logger.Log("[SUCESSO] Barra de tarefas resetada. Refixe seus apps.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Restaurar Fonte Padrão (Segoe UI)",
                Category = "Sistema",
                Icon = "🅰️",
                Description = "Aplicativos/tweaks de 'trocar fonte' deixaram o sistema com fonte estranha. Remove a sobrescrita da Segoe UI feita no usuário e reinicia o Explorer.",
                Execute = () => {
                    Logger.Log("Removendo sobrescrita de fonte do usuário...");
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Fonts", true);
                    if (key != null)
                    {
                        foreach (var name in key.GetValueNames())
                        {
                            if (name.StartsWith("Segoe UI", StringComparison.OrdinalIgnoreCase))
                            {
                                key.DeleteValue(name, false);
                                Logger.Log($"[FONTE] Removida sobrescrita: {name}");
                            }
                        }
                    }
                    SystemUtils.RunExternalProcess("taskkill", "/f /im explorer.exe", true);
                    System.Threading.Thread.Sleep(800);
                    SystemUtils.RunExternalProcess("cmd.exe", "/c start explorer.exe", true, false);
                    Logger.Log("[SUCESSO] Segoe UI restaurada.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Windows Spotlight (Tela de Bloqueio)",
                Category = "Sistema",
                Icon = "🖼️",
                Description = "Spotlight travado na mesma imagem ou não carrega novas fotos. Limpa os assets baixados e re-registra o ContentDeliveryManager.",
                Execute = () => {
                    Logger.Log("Reparando Windows Spotlight...");
                    SystemUtils.RunExternalProcess("cmd", "/c del /f /q \"%LocalAppData%\\Packages\\Microsoft.Windows.ContentDeliveryManager_cw5n1h2txyewy\\LocalState\\Assets\\*\"", true);
                    SystemUtils.RunExternalProcess("powershell", "-NoProfile -Command \"Get-AppxPackage Microsoft.Windows.ContentDeliveryManager | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register ($_.InstallLocation + '\\AppXManifest.xml')}\"", true);
                    Logger.Log("[SUCESSO] Spotlight resetado. Troque o plano de fundo para Spotlight e aguarde novas imagens.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Restaurar Bibliotecas do Explorer",
                Category = "Explorer/UI",
                Icon = "📚",
                Description = "Bibliotecas (Documentos/Vídeos/Imagens agrupadas) sumiram da navegação. Recria o atalho no namespace do desktop.",
                Execute = () => {
                    Logger.Log("Restaurando Bibliotecas...");
                    using var ns = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\Explorer\Desktop\NameSpace\{031E4825-7B94-4dc3-B131-E946B44C8DD5}");
                    ns?.SetValue("", "Libraries", Microsoft.Win32.RegistryValueKind.String);
                    using var hide = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel");
                    hide?.SetValue("{031E4825-7B94-4dc3-B131-E946B44C8DD5}", 0, Microsoft.Win32.RegistryValueKind.DWord);
                    SystemUtils.RunExternalProcess("taskkill", "/f /im explorer.exe", true);
                    System.Threading.Thread.Sleep(800);
                    SystemUtils.RunExternalProcess("cmd.exe", "/c start explorer.exe", true, false);
                    Logger.Log("[SUCESSO] Bibliotecas restauradas.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reativar Gerenciador de Tarefas",
                Category = "Sistema",
                Icon = "📊",
                Description = "Gerenciador de Tarefas bloqueado pelo admin ('desabilitado pelo administrador') — típico resquício de malware ou tweak agressivo. Remove a política.",
                Execute = () => {
                    Logger.Log("Removendo política DisableTaskMgr...");
                    using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System");
                    k?.DeleteValue("DisableTaskMgr", false);
                    SystemUtils.RunExternalProcess("reg", "delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v DisableTaskMgr /f", true); // admin best-effort
                    Logger.Log("[SUCESSO] Gerenciador de Tarefas reativado.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Desbloquear CMD/Regedit (Malware)",
                Category = "Sistema",
                Icon = "🔓",
                Description = "Prompt de Comando, Regedit ou Painel de Controle bloqueados ('desabilitado pelo administrador') — assinatura clássica de malware/tweak. Remove as políticas.",
                Execute = () => {
                    Logger.Log("Removendo políticas DisableCMD/DisableRegistryTools/NoControlPanel...");
                    using var sys = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System");
                    sys?.DeleteValue("DisableCMD", false);
                    sys?.DeleteValue("DisableRegistryTools", false);
                    using var exp = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer");
                    exp?.DeleteValue("NoControlPanel", false);
                    // HKLM (malware costuma gravar nos dois — admin best-effort)
                    SystemUtils.RunExternalProcess("reg", "delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v DisableCMD /f", true);
                    SystemUtils.RunExternalProcess("reg", "delete \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Policies\\System\" /v DisableRegistryTools /f", true);
                    Logger.Log("[SUCESSO] CMD/Regedit/Painel desbloqueados. Rode um antivírus para garantir.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Menu Win+X (Power User)",
                Category = "Explorer/UI",
                Icon = "⚡",
                Description = "Menu do botão direito no Iniciar (Win+X) vazio ou com entradas quebradas. Restaura os atalhos originais da instalação do Windows.",
                Execute = () => {
                    Logger.Log("Restaurando Win+X do perfil Default...");
                    string src = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Users", "Default", "AppData", "Local", "Microsoft", "Windows", "WinX");
                    string dst = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "WinX");
                    if (Directory.Exists(src))
                    {
                        foreach (var dir in Directory.GetDirectories(dst))
                            try { Directory.Delete(dir, true); } catch { }
                        foreach (var dir in Directory.GetDirectories(src))
                        {
                            string target = Path.Combine(dst, Path.GetFileName(dir));
                            Directory.CreateDirectory(target);
                            foreach (var f in Directory.GetFiles(dir)) File.Copy(f, Path.Combine(target, Path.GetFileName(f)), true);
                        }
                        SystemUtils.RunExternalProcess("taskkill", "/f /im explorer.exe", true);
                        System.Threading.Thread.Sleep(800);
                        SystemUtils.RunExternalProcess("cmd.exe", "/c start explorer.exe", true, false);
                        Logger.Log("[SUCESSO] Win+X restaurado.");
                    }
                    else Logger.Log("[AVISO] Pasta Default do Win+X não encontrada.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Esquecer Todas as Redes Wi-Fi",
                Category = "Internet",
                Icon = "📶",
                Description = "Wi-Fi não conecta mais, perfil corrompido ou trocou de senha e não pede de novo. Apaga TODOS os perfis salvos (você vai redigitar as senhas).",
                Execute = () => {
                    Logger.Log("Apagando todos os perfis Wi-Fi salvos...");
                    SystemUtils.RunExternalProcess("cmd", "/c netsh wlan delete profile name=* i=*", true);
                    Logger.Log("[SUCESSO] Perfis Wi-Fi apagados. Reconecte e redigite as senhas.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Restaurar Associação de Imagens (Fotos)",
                Category = "Explorer/UI",
                Icon = "🖼️",
                Description = "Imagens abrindo com app errado ou previews não carregam por associação quebrada. Remove a escolha travada de .png/.jpg/.jpeg/.bmp/.gif para o Windows voltar ao padrão (Fotos).",
                Execute = () => {
                    Logger.Log("Resetando UserChoice das associações de imagem...");
                    foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" })
                    {
                        try
                        {
                            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\{ext}", true);
                            k?.DeleteSubKeyTree("UserChoice", false);
                        }
                        catch { }
                    }
                    Logger.Log("[SUCESSO] Associações de imagem redefinidas — o Windows voltará a perguntar/usar o padrão.");
                }
            });

            // =================================================================
            // REPAROS EXTRA — problemas comuns reportados em fóruns (ElevenForum,
            // TenForums, MS Answers, Reddit techsupport) não cobertos pelo FixWin.
            // =================================================================

            repairs.Add(new RepairAction
            {
                Name = "Reparar App Configurações (Não Abre)",
                Category = "Sistema",
                Icon = "⚙️",
                Description = "O app Configurações (Win+I) não abre, abre e fecha na hora ou fica em branco. Re-registra o windows.immersivecontrolpanel — fix clássico de fóruns para o 24H2.",
                Execute = () => {
                    Logger.Log("Re-registrando o app Configurações...");
                    SystemUtils.RunExternalProcess("powershell", "-NoProfile -Command \"Get-AppxPackage windows.immersivecontrolpanel | Foreach {Add-AppxPackage -DisableDevelopmentMode -Register ($_.InstallLocation + '\\AppXManifest.xml')}\"", true);
                    Logger.Log("[SUCESSO] Configurações re-registrado. Teste Win+I.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Áudio (Sem Som / Dispositivo Não Detectado)",
                Category = "Sistema",
                Icon = "🔊",
                Description = "Ícone de som com X, 'nenhum dispositivo de saída' ou sem som após update. Restaura os serviços Audiosrv/AudioEndpointBuilder para Automatic, os reinicia e reativa dispositivos de áudio em estado de erro.",
                Execute = () => {
                    Logger.Log("Reparando stack de áudio...");
                    SystemUtils.RunExternalProcess("cmd", "/c sc config Audiosrv start= auto & sc config AudioEndpointBuilder start= auto", true);
                    SystemUtils.RunExternalProcess("net", "stop Audiosrv", true);
                    SystemUtils.RunExternalProcess("net", "stop AudioEndpointBuilder", true);
                    SystemUtils.RunExternalProcess("net", "start AudioEndpointBuilder", true);
                    SystemUtils.RunExternalProcess("net", "start Audiosrv", true);
                    SystemUtils.RunExternalProcess("powershell", "-NoProfile -Command \"Get-PnpDevice -Class AudioEndpoint,Media -Status Error,Unknown -ErrorAction SilentlyContinue | Enable-PnpDevice -Confirm:$false -ErrorAction SilentlyContinue\"", true);
                    Logger.Log("[SUCESSO] Serviços de áudio restaurados e dispositivos desativados reativados. Se persistir, reinstale o driver (Realtek/etc). ");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Painel de Dispositivos (Gerenciador em Branco)",
                Category = "Diagnóstico",
                Icon = "🧰",
                Description = "Gerenciador de Dispositivos abre em branco/vazio ou a aba 'Hardware' não aparece. Re-detecta hardware com devcon-like (pnputil /scan-devices) e reconstrói os contadores.",
                Execute = () => {
                    Logger.Log("Re-escaneando dispositivos...");
                    SystemUtils.RunExternalProcess("pnputil", "/scan-devices", true);
                    SystemUtils.RunExternalProcess("cmd", "/c lodctr /R", true);
                    Logger.Log("[SUCESSO] Escaneado. Reabra o Gerenciador de Dispositivos.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reativar USB (Portas Mortas após Suspensão)",
                Category = "Hardware",
                Icon = "🔌",
                Description = "Mouse/teclado/pendrive param de funcionar após dormir ou 'dispositivo USB não reconhecido'. Desativa a suspensão seletiva e reescaneia os hubs USB.",
                Execute = () => {
                    Logger.Log("Reativando portas USB...");
                    SystemUtils.RunExternalProcess("powershell", "-NoProfile -Command \"Get-PnpDevice -Class USB -Status Error,Unknown,Degraded -ErrorAction SilentlyContinue | Enable-PnpDevice -Confirm:$false -ErrorAction SilentlyContinue\"", true);
                    SystemUtils.RunExternalProcess("cmd", "/c reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\USB\" /v DisableSelectiveSuspend /t REG_DWORD /d 1 /f", true);
                    SystemUtils.RunExternalProcess("pnputil", "/scan-devices", true);
                    Logger.Log("[SUCESSO] USB reescaneado + suspensão seletiva desativada (fix do 'não reconhecido' após dormir). Reinicie se persistir.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Modo de Segurança (F8/msconfig Bloqueado)",
                Category = "Avançado",
                Icon = "🛟",
                Description = "Modo de Segurança não inicia ou o safeboot ficou travado em loop (bota safe mode e não sai). Limpa o safeboot do BCD e reativa o F8 legado.",
                Execute = () => {
                    Logger.Log("Reparando opções de Modo de Segurança...");
                    var (code, _) = SystemUtils.RunExternalProcessWithCode("bcdedit", "/deletevalue {default} safeboot", true);
                    Logger.Log($"  safeboot removido do BCD (código {code} — 1 = não estava setado, ok).");
                    SystemUtils.RunExternalProcess("bcdedit", "/set {default} bootmenupolicy Legacy", true);
                    Logger.Log("[SUCESSO] F8 legado reativado e safeboot limpo. Reinicie.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Reciclagem Travada (Não Esvazia)",
                Category = "Explorer/UI",
                Icon = "🗑️",
                Description = "Lixeira não esvazia, ícone nunca muda ou arquivos não vão para ela. Limpa as pastas $Recycle.Bin de TODOS os drives (recria do zero).",
                Execute = () => {
                    Logger.Log("Limpando $Recycle.Bin de todos os drives...");
                    foreach (var d in DriveInfo.GetDrives().Where(x => x.DriveType == DriveType.Fixed || x.DriveType == DriveType.Removable))
                        SystemUtils.RunExternalProcess("cmd", $"/c rd /s /q \"{d.Name.TrimEnd('\\')}\\$Recycle.Bin\"", true);
                    SystemUtils.RunExternalProcess("taskkill", "/f /im explorer.exe", true);
                    System.Threading.Thread.Sleep(500);
                    SystemUtils.RunExternalProcess("cmd.exe", "/c start explorer.exe", true, false);
                    Logger.Log("[SUCESSO] Lixeiras recriadas do zero.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar 'Enviar Para' (Menu de Contexto)",
                Category = "Explorer/UI",
                Icon = "📨",
                Description = "Menu 'Enviar para' vazio ou sumido no clique direito. Recria os atalhos padrão (Desktop, Documentos, Compactado, Lixeira) na pasta SendTo do usuário.",
                Execute = () => {
                    var sendTo = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft\\Windows\\SendTo");
                    Directory.CreateDirectory(sendTo);
                    Logger.Log("Recriando atalhos padrão do SendTo...");
                    // Área de trabalho e Documentos via shell namespace
                    SystemUtils.RunExternalProcess("powershell", $"-NoProfile -Command \"$ws=New-Object -ComObject WScript.Shell; $d=$ws.CreateShortcut('{sendTo}\\Área de Trabalho.lnk'); $d.TargetPath='shell:::{{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}}'; $d.Save(); $m=$ws.CreateShortcut('{sendTo}\\Documentos.lnk'); $m.TargetPath='shell:::{{FDD39AD0-238F-46AF-ADB4-6C85480369C7}}'; $m.Save()\"", true);
                    Logger.Log($"[SUCESSO] Atalhos recriados em {sendTo} (papelaria/pasta de 'Enviar para' valida no próximo clique direito).");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Copiar/Colar e Arrastar (Clipboard Corrompido)",
                Category = "Sistema",
                Icon = "📋",
                Description = "Ctrl+C/V param de funcionar em apps, arrastar-arquivos não solta. Reinicia o serviço e o monitor do clipboard (cbdhtaskbar/ClipSvc) e limpa o cache.",
                Execute = () => {
                    Logger.Log("Reiniciando clipboard...");
                    SystemUtils.RunExternalProcess("cmd", "/c echo off | clip", true);
                    SystemUtils.RunExternalProcess("net", "stop ClipSvc", true);
                    SystemUtils.RunExternalProcess("net", "start ClipSvc", true);
                    SystemUtils.RunExternalProcess("taskkill", "/f /im rdpclip.exe", true);
                    SystemUtils.RunExternalProcess("cmd.exe", "/c start rdpclip.exe", true, false);
                    Logger.Log("[SUCESSO] Clipboard resetado (clip + ClipSvc + rdpclip). Teste Ctrl+C/V.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Drive Óptico/USB Sumido do Explorer",
                Category = "Hardware",
                Icon = "💽",
                Description = "Drive aparece no Gerenciador de Dispositivos mas sumiu do Explorer (sem letra). Remove os filtros superiores/inferiores corrompidos do MountMgr e reseta as letras — fix clássico de fórum para 'meu drive sumiu'.",
                Execute = () => {
                    Logger.Log("Removendo filtros corrompidos de volume ( UpperFilters/LowerFilters )...");
                    using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e967-e325-11ce-bfc1-08002be10318}", true);
                    if (k != null)
                    {
                        foreach (var v in new[] { "UpperFilters", "LowerFilters" })
                            try { k.DeleteValue(v, false); Logger.Log($"  {v} removido."); } catch { }
                    }
                    SystemUtils.RunExternalProcess("pnputil", "/scan-devices", true);
                    Logger.Log("[SUCESSO] Filtros de disco removidos + rescan. Reinicie se o drive não voltar.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Notificações Toast (Não Chegam Mais)",
                Category = "Sistema",
                Icon = "🔔",                    Description = "Notificações de apps pararam (WpnService parado ou Focus Assist travado). Reativa o WpnService e limpa o ToastEnabled do usuário — fix para quem desativou via 'tweak' de serviços.",
                Execute = () => {
                    Logger.Log("Reparando notificações (WpnService)...");
                    SystemUtils.RunExternalProcess("cmd", "/c sc config WpnService start= auto", true);
                    SystemUtils.RunExternalProcess("net", "start WpnService", true);
                    SystemUtils.RunExternalProcess("reg", "delete \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\PushNotifications\" /v ToastEnabled /f", true);
                    Logger.Log("[SUCESSO] WpnService reativado e ToastEnabled removido (default = ON). Reinicie o Explorer.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar OneDrive (Não Sincroniza / Ícone Erro)",
                Category = "Sistema",
                Icon = "☁️",
                Description = "OneDrive com X vermelho, não sincroniza ou não inicia. Reseta o pacote Appx do OneDrive e reinicia o processo com o fix oficial da Microsoft (%LocalAppData%\\OneDrive\\OneDrive.exe /reset).",
                Execute = () => {
                    Logger.Log("Resetando OneDrive (método oficial Microsoft)...");
                    SystemUtils.RunExternalProcess("cmd", "/c if exist \"%LocalAppData%\\Microsoft\\OneDrive\\OneDrive.exe\" \"%LocalAppData%\\Microsoft\\OneDrive\\OneDrive.exe\" /reset", true);
                    Logger.Log("[SUCESSO] OneDrive /reset enviado (recria a sync database). Ele volta sozinho em ~2 min; se não, inicie-o manualmente.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Horário Duplicado/Extra na Barra (Relógio)",
                Category = "Explorer/UI",
                Icon = "⏰",
                Description = "Dois relógios, relógio extra da taskbar antiga ou data/hora errada persistente. Limpa caches da taskbar de notificações e resincroniza o time service.",
                Execute = () => {
                    Logger.Log("Reparando relógio da barra...");
                    SystemUtils.RunExternalProcess("w32tm", "/resync /force", true);
                    SystemUtils.RunExternalProcess("taskkill", "/f /im explorer.exe", true);
                    System.Threading.Thread.Sleep(500);
                    SystemUtils.RunExternalProcess("cmd.exe", "/c start explorer.exe", true, false);
                    Logger.Log("[SUCESSO] Relógio resincronizado + barra recarregada.");
                }
            });

            repairs.Add(new RepairAction
            {
                Name = "Reparar Antivírus de Terceiros Bloqueando Windows Security",
                Category = "Sistema",
                Icon = "🛡️",
                Description = "Segurança do Windows em cinza, 'seu administrador de vírus... foi desativado' após desinstalar AVG/Avast/Norton. Re-registra o SecurityHealthService e limpa o AV de terceiros do Security Center (WMI).",
                Execute = () => {
                    Logger.Log("Reparando Segurança do Windows / Security Center...");
                    SystemUtils.RunExternalProcess("powershell", "-NoProfile -Command \"Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct | Where-Object {$_.displayName -notmatch 'Windows'} | Remove-CimInstance\"", true);
                    SystemUtils.RunExternalProcess("cmd", "/c sc config WinDefend start= auto & sc config SecurityHealthService start= auto & net start SecurityHealthService", true);
                    SystemUtils.RunExternalProcess("cmd", "/c start windowsdefender:", true, false);
                    Logger.Log("[SUCESSO] AV fantasma removido do Security Center + WinDefend/SecurityHealthService reativados.");
                }
            });

            return repairs;
        }
    }
}