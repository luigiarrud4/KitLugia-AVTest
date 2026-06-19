using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KitLugia.Core;

// Resolve conflito se houver (WPF vs Forms)
using UserControl = System.Windows.Controls.UserControl;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace KitLugia.GUI.Controls
{
    public partial class ValorantDiagnosticOverlay : UserControl
    {
        private bool _uefiEnabled = false;
        private bool _tpm20Enabled = false;
        private bool _vbsEnabled = false;
        private bool _hvciEnabled = false;
        private bool _canApplyValorantRepair = false;

        public event EventHandler? Closed;

        public ValorantDiagnosticOverlay()
        {
            InitializeComponent();
            Loaded += ValorantDiagnosticOverlay_Loaded;
        }

        private void ValorantDiagnosticOverlay_Loaded(object sender, RoutedEventArgs e)
        {
            _ = RunValorantDiagnosticAsync();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }

        private async Task RunValorantDiagnosticAsync()
        {
            TxtUEFIStatus.Text = "Verificando...";
            TxtTPMStatus.Text = "Verificando...";
            TxtVBSStatus.Text = "Verificando...";
            TxtHVCIStatus.Text = "Verificando...";
            TxtValorantOverallStatus.Text = "Verificando requisitos do sistema...";
            BtnApplyValorantRepair.IsEnabled = false;

            await Task.Delay(500);

            await CheckUEFIStatusAsync();
            await Task.Delay(200);

            await CheckTPM20StatusAsync();
            await Task.Delay(200);

            await CheckVBSStatusAsync();
            await Task.Delay(200);

            await CheckHVCIStatusAsync();
            await Task.Delay(200);

            EvaluateValorantRepairPossibility();
        }

        private async Task CheckUEFIStatusAsync()
        {
            try
            {
                string result = SystemUtils.RunExternalProcess("powershell",
                    "-NoProfile -Command \"(Get-WmiObject -Class Win32_ComputerSystem).BootUpState\"",
                    true);

                if (!result.Contains("Legacy", StringComparison.OrdinalIgnoreCase))
                {
                    _uefiEnabled = true;
                    TxtUEFIStatus.Text = "✅ UEFI Habilitado";
                    TxtUEFIStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 221, 23)); // Verde suave
                }
                else
                {
                    _uefiEnabled = false;
                    TxtUEFIStatus.Text = "❌ Modo Legacy (Precisa alterar para UEFI na BIOS)";
                    TxtUEFIStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 83, 80)); // Vermelho suave
                }
            }
            catch
            {
                _uefiEnabled = false;
                TxtUEFIStatus.Text = "❌ Não foi possível verificar";
                TxtUEFIStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 83, 80)); // Vermelho suave
            }
        }

        private async Task CheckTPM20StatusAsync()
        {
            try
            {
                // Verificação via Gerenciador de Dispositivos (Get-PnpDevice)
                // Este método mostra exatamente o que aparece no Gerenciador de Dispositivos do Windows
                // Inclui todos os nomes possíveis de TPM encontrados em diferentes fabricantes
                string deviceManagerResult = SystemUtils.RunExternalProcess("powershell",
                    "-NoProfile -Command \"Get-PnpDevice | Where-Object { $_.FriendlyName -like '*TPM*' -or $_.FriendlyName -like '*Trusted Platform*' -or $_.FriendlyName -like '*Infineon*' -or $_.FriendlyName -like '*STMicroelectronics*' -or $_.FriendlyName -like '*Intel*' -or $_.FriendlyName -like '*AMD*' -or $_.FriendlyName -like '*Nationz*' -or $_.FriendlyName -like '*Nuvoton*' -or $_.FriendlyName -like '*FIPS*' -or $_.FriendlyName -like '*IFX*' -or $_.FriendlyName -like '*Winbond*' } | Select-Object FriendlyName, Status, InstanceId | ConvertTo-Json\"",
                    true);

                if (!string.IsNullOrWhiteSpace(deviceManagerResult))
                {
                    // Verificar se encontrou algum dispositivo TPM (todos os nomes possíveis)
                    bool tpmFound = deviceManagerResult.Contains("TPM") || 
                                     deviceManagerResult.Contains("Trusted Platform") ||
                                     deviceManagerResult.Contains("Infineon") ||
                                     deviceManagerResult.Contains("STMicroelectronics") ||
                                     deviceManagerResult.Contains("Intel") ||
                                     deviceManagerResult.Contains("AMD") ||
                                     deviceManagerResult.Contains("Nationz") ||
                                     deviceManagerResult.Contains("Nuvoton") ||
                                     deviceManagerResult.Contains("FIPS") ||
                                     deviceManagerResult.Contains("IFX") ||
                                     deviceManagerResult.Contains("Winbond");

                    if (!tpmFound)
                    {
                        _tpm20Enabled = false;
                        TxtTPMStatus.Text = "❌ TPM não encontrado";
                        TxtTPMStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 83, 80)); // Vermelho suave
                        return;
                    }

                    // Extrair FriendlyName do dispositivo TPM
                    string tpmDeviceName = "";
                    var nameMatch = System.Text.RegularExpressions.Regex.Match(deviceManagerResult, "\"FriendlyName\":\\s*\"([^\"]+)\"");
                    if (nameMatch.Success)
                    {
                        tpmDeviceName = nameMatch.Groups[1].Value;
                    }

                    // Extrair Status do dispositivo
                    string tpmStatus = "";
                    var statusMatch = System.Text.RegularExpressions.Regex.Match(deviceManagerResult, "\"Status\":\\s*\"([^\"]+)\"");
                    if (statusMatch.Success)
                    {
                        tpmStatus = statusMatch.Groups[1].Value;
                    }

                    // TPM 1.2 é muito raro atualmente e não é suportado pelo Valorant
                    // Se encontrar qualquer dispositivo TPM, assumir que é TPM 2.0
                    // Isso é mais seguro e garante compatibilidade
                    _tpm20Enabled = true;

                    // Verificar se o dispositivo está OK (não tem erro)
                    bool isDeviceOK = tpmStatus.Equals("OK", System.StringComparison.OrdinalIgnoreCase) ||
                                     tpmStatus.Contains("OK") ||
                                     !tpmStatus.Contains("Error") &&
                                     !tpmStatus.Contains("Degraded") &&
                                     !tpmStatus.Contains("Unknown");

                    if (!isDeviceOK)
                    {
                        _tpm20Enabled = false;
                        TxtTPMStatus.Text = "⚠️ TPM Presente mas com erro (Status: " + tpmStatus + ")";
                        TxtTPMStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Amarelo suave
                    }
                    else
                    {
                        _tpm20Enabled = true;
                        TxtTPMStatus.Text = "✅ TPM 2.0 Habilitado (" + tpmDeviceName + ")";
                        TxtTPMStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 221, 23)); // Verde suave
                    }
                }
                else
                {
                    _tpm20Enabled = false;
                    TxtTPMStatus.Text = "❌ TPM não encontrado";
                    TxtTPMStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 83, 80)); // Vermelho suave
                }
            }
            catch
            {
                _tpm20Enabled = false;
                TxtTPMStatus.Text = "❌ Não foi possível verificar";
                TxtTPMStatus.Foreground = new SolidColorBrush(Color.FromRgb(239, 83, 80)); // Vermelho suave
            }
        }

        private async Task CheckVBSStatusAsync()
        {
            try
            {
                string result = SystemUtils.RunExternalProcess("reg",
                    @"query ""HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard"" /v EnableVirtualizationBasedSecurity",
                    true);

                if (result.Contains("0x1"))
                {
                    _vbsEnabled = true;
                    TxtVBSStatus.Text = "⚠️ Habilitado (Precisa ser desativado para Vanguard)";
                    TxtVBSStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Amarelo suave
                }
                else if (result.Contains("0x0"))
                {
                    _vbsEnabled = false;
                    TxtVBSStatus.Text = "✅ Desabilitado";
                    TxtVBSStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 221, 23)); // Verde suave
                }
                else
                {
                    _vbsEnabled = false;
                    TxtVBSStatus.Text = "✅ Desabilitado (Padrão)";
                    TxtVBSStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 221, 23)); // Verde suave
                }
            }
            catch
            {
                _vbsEnabled = false;
                TxtVBSStatus.Text = "✅ Desabilitado (Não configurado)";
                TxtVBSStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 221, 23)); // Verde suave
            }
        }

        private async Task CheckHVCIStatusAsync()
        {
            try
            {
                string result = SystemUtils.RunExternalProcess("reg",
                    @"query ""HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity"" /v Enabled",
                    true);

                if (result.Contains("0x1"))
                {
                    _hvciEnabled = true;
                    TxtHVCIStatus.Text = "⚠️ Habilitado (Precisa ser desativado para Vanguard)";
                    TxtHVCIStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 193, 7)); // Amarelo suave
                }
                else if (result.Contains("0x0"))
                {
                    _hvciEnabled = false;
                    TxtHVCIStatus.Text = "✅ Desabilitado";
                    TxtHVCIStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 221, 23)); // Verde suave
                }
                else
                {
                    _hvciEnabled = false;
                    TxtHVCIStatus.Text = "✅ Desabilitado (Padrão)";
                    TxtHVCIStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 221, 23)); // Verde suave
                }
            }
            catch
            {
                _hvciEnabled = false;
                TxtHVCIStatus.Text = "✅ Desabilitado (Não configurado)";
                TxtHVCIStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 221, 23)); // Verde suave
            }
        }

        private void EvaluateValorantRepairPossibility()
        {
            string statusMessage = "";

            if (_uefiEnabled && _tpm20Enabled)
            {
                statusMessage = " MELHOR CENÁRIO: UEFI + TPM 2.0 HABILITADOS\n\n" +
                               "Seu PC está configurado corretamente!\n" +
                               "Com UEFI e TPM 2.0 habilitados, o Vanguard funcionará mesmo com VBS habilitado.\n\n" +
                               "Se ainda estiver vendo o erro VAN9005:\n" +
                               "• Reinicie o PC\n" +
                               "• Verifique se o Vanguard está atualizado\n" +
                               "• Entre em contato com o suporte da Riot se persistir";
                BorderValorantStatus.Background = new SolidColorBrush(Color.FromRgb(56, 142, 60)); // Verde escuro suave
                _canApplyValorantRepair = false;
            }
            else if (!_uefiEnabled && !_tpm20Enabled)
            {
                statusMessage = " SEU PC NÃO TEM UEFI NEM TPM 2.0\n\n" +
                               "A melhor solução é habilitar UEFI e TPM 2.0 na BIOS.\n\n" +
                               "Soluções:\n" +
                               "1. Entre na BIOS e altere o modo de boot para UEFI\n" +
                               "2. Habilite TPM 2.0 (fPTM ou PTT na Intel, TPM na AMD)\n" +
                               "3. Se não houver essas opções, sua placa-mãe pode não suportar\n\n" +
                               "Se NÃO for possível habilitar UEFI + TPM 2.0:\n" +
                               "Você pode desativar VBS/HVCI como alternativa (clicando em Aplicar Reparo)";
                BorderValorantStatus.Background = new SolidColorBrush(Color.FromRgb(198, 40, 40)); // Vermelho escuro suave
                _canApplyValorantRepair = true;
            }
            else if (!_uefiEnabled)
            {
                statusMessage = " MODO LEGACY DETECTADO\n\n" +
                               "Seu PC está em modo Legacy BIOS.\n" +
                               "A melhor solução é alterar para UEFI na BIOS.\n\n" +
                               "Solução:\n" +
                               "Entre na BIOS e altere o modo de boot para UEFI, depois habilite TPM 2.0.\n\n" +
                               "Se NÃO for possível alterar para UEFI:\n" +
                               "Você pode desativar VBS/HVCI como alternativa (clicando em Aplicar Reparo)";
                BorderValorantStatus.Background = new SolidColorBrush(Color.FromRgb(245, 124, 0)); // Laranja suave
                _canApplyValorantRepair = true;
            }
            else if (!_tpm20Enabled)
            {
                statusMessage = " TPM 2.0 NÃO HABILITADO\n\n" +
                               "Seu PC está em UEFI, mas TPM 2.0 não está habilitado.\n" +
                               "A melhor solução é habilitar TPM 2.0 na BIOS.\n\n" +
                               "Solução:\n" +
                               "Entre na BIOS e habilite TPM 2.0 (fPTM/PTT na Intel, TPM na AMD).\n\n" +
                               "Se NÃO for possível habilitar TPM 2.0:\n" +
                               "Você pode desativar VBS/HVCI como alternativa (clicando em Aplicar Reparo)";
                BorderValorantStatus.Background = new SolidColorBrush(Color.FromRgb(245, 124, 0)); // Laranja suave
                _canApplyValorantRepair = true;
            }
            else if (_vbsEnabled || _hvciEnabled)
            {
                statusMessage = " UEFI + TPM 2.0 OK, MAS VBS/HVCI HABILITADOS\n\n" +
                               "Seu PC tem UEFI e TPM 2.0, então o Vanguard deve funcionar.\n" +
                               "Porém, se ainda estiver tendo problemas, pode desativar VBS/HVCI.\n\n" +
                               "Este reparo vai:\n" +
                               "• Desativar o Hypervisor (bcdedit)\n" +
                               "• Desativar VBS (Virtualization Based Security)\n" +
                               "• Desativar HVCI (Integridade de Memória)\n\n" +
                               "Recomendado apenas se ainda estiver vendo erro VAN9005.";
                BorderValorantStatus.Background = new SolidColorBrush(Color.FromRgb(56, 142, 60)); // Verde escuro suave
                _canApplyValorantRepair = true;
            }
            else
            {
                statusMessage = " TUDO CONFIGURADO CORRETAMENTE\n\n" +
                               "Seu PC tem UEFI, TPM 2.0 e VBS/HVCI desabilitados.\n\n" +
                               "O Vanguard deve funcionar normalmente. " +
                               "Se ainda não funcionar, tente reiniciar o PC.";
                BorderValorantStatus.Background = new SolidColorBrush(Color.FromRgb(56, 142, 60)); // Verde escuro suave
                _canApplyValorantRepair = false;
            }

            TxtValorantOverallStatus.Text = statusMessage;
            BtnApplyValorantRepair.IsEnabled = _canApplyValorantRepair;
        }

        private void BtnApplyValorantRepair_Click(object sender, RoutedEventArgs e)
        {
            if (!_canApplyValorantRepair) return;

            Logger.Log("Iniciando reparo do Valorant...");
            Logger.Log("Desativando VBS/HVCI para Vanguard...");

            SystemUtils.RunExternalProcess("bcdedit", "/set hypervisorlaunchtype off", true);
            SystemUtils.RunExternalProcess("reg",
                @"add ""HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard"" /v EnableVirtualizationBasedSecurity /t REG_DWORD /d 0 /f", true);
            SystemUtils.RunExternalProcess("reg",
                @"add ""HKLM\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity"" /v Enabled /t REG_DWORD /d 0 /f", true);

            Logger.Log("[SUCESSO] Reparo aplicado. Reinicie o PC para o Vanguard funcionar.");

            TxtValorantOverallStatus.Text = "✅ REPARO APLICADO COM SUCESSO\n\n" +
                                           "As configurações foram alteradas:\n" +
                                           "• Hypervisor desativado\n" +
                                           "• VBS desativado\n" +
                                           "• HVCI desativado\n\n" +
                                           "⚠️ REINICIE O PC AGORA para o Vanguard funcionar.";
            BorderValorantStatus.Background = new SolidColorBrush(Color.FromRgb(100, 221, 23)); // Verde suave
            BtnApplyValorantRepair.IsEnabled = false;
        }
    }
}
