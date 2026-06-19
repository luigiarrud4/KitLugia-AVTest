using Microsoft.Win32; // Necessário para limpar registro da Nvidia
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Tasks;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static class DisplayManager
    {
        // --- API NATIVA ---
        [DllImport("gdi32.dll")] private static extern bool GetDeviceGammaRamp(IntPtr hdc, ref RAMP lpRamp);
        [DllImport("gdi32.dll")] private static extern bool SetDeviceGammaRamp(IntPtr hdc, ref RAMP lpRamp);
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct RAMP
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Red;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Green;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)] public ushort[] Blue;
        }

        public class ColorProfileData
        {
            public string ProfileName { get; set; } = "Default";
            public DateTime CreatedAt { get; set; }
            public ushort[] Red { get; set; } = new ushort[256];
            public ushort[] Green { get; set; } = new ushort[256];
            public ushort[] Blue { get; set; } = new ushort[256];
        }

        // --- MÉTODOS ---

        public static (bool Success, string Message) SaveColorProfile(string profileName, string filePath)
        {
            IntPtr hDC = IntPtr.Zero;
            try
            {
                hDC = GetDC(IntPtr.Zero);
                RAMP ramp = new RAMP { Red = new ushort[256], Green = new ushort[256], Blue = new ushort[256] };

                if (GetDeviceGammaRamp(hDC, ref ramp))
                {
                    var data = new ColorProfileData
                    {
                        ProfileName = profileName,
                        CreatedAt = DateTime.Now,
                        Red = ramp.Red,
                        Green = ramp.Green,
                        Blue = ramp.Blue
                    };
                    File.WriteAllText(filePath, JsonSerializer.Serialize(data));
                    return (true, "Perfil salvo com sucesso.");
                }
                return (false, "Falha ao ler cores da GPU.");
            }
            catch (Exception ex) { return (false, ex.Message); }
            finally { if (hDC != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hDC); }
        }

        public static (bool Success, string Message) RestoreColorProfile(string filePath)
        {
            IntPtr hDC = IntPtr.Zero;
            try
            {
                if (!File.Exists(filePath)) return (false, "Perfil não encontrado.");
                var data = JsonSerializer.Deserialize<ColorProfileData>(File.ReadAllText(filePath));
                if (data == null) return (false, "Perfil inválido.");

                RAMP ramp = new RAMP { Red = data.Red, Green = data.Green, Blue = data.Blue };
                hDC = GetDC(IntPtr.Zero);

                // Tenta aplicar 3 vezes em caso de falha momentânea
                bool success = false;
                for (int i = 0; i < 3; i++)
                {
                    if (SetDeviceGammaRamp(hDC, ref ramp)) { success = true; break; }
                    System.Threading.Thread.Sleep(50);
                }

                return (success, success ? "Cores restauradas." : "Driver bloqueou a restauração.");
            }
            catch (Exception ex) { return (false, ex.Message); }
            finally { if (hDC != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hDC); }
        }

        public static (bool Success, string Message) ResetColorProfileToDefault()
        {
            IntPtr hDC = IntPtr.Zero;
            try
            {
                RAMP ramp = new RAMP { Red = new ushort[256], Green = new ushort[256], Blue = new ushort[256] };
                for (int i = 0; i < 256; i++) { ushort v = (ushort)(i * 256); ramp.Red[i] = v; ramp.Green[i] = v; ramp.Blue[i] = v; }

                hDC = GetDC(IntPtr.Zero);
                SetDeviceGammaRamp(hDC, ref ramp);
                return (true, "Cores resetadas (Linear).");
            }
            catch { return (false, "Erro ao resetar."); }
            finally { if (hDC != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hDC); }
        }

        public static async Task<(bool Success, string Message)> FixColorConflict()
        {
            try
            {
                await Task.Run(() =>
                {
                    // 1. Tenta limpar chaves de persistência de cor da NVIDIA (Seguro: User Mode)
                    try
                    {
                        using var key = Registry.CurrentUser.OpenSubKey(@"Software\NVIDIA Corporation\Global\NVTweak", true);
                        if (key != null)
                        {
                            // Deleta valores que forçam cor na inicialização se existirem
                            key.DeleteValue("NvidiaColorCorrection", false);
                        }
                    }
                    catch { }

                    // 2. Para serviços conflitantes
                    SystemUtils.RunExternalProcess("cmd.exe", @"/c schtasks /Change /TN ""\Microsoft\Windows\WindowsColorSystem\Calibration Loader"" /Disable", true);
                    SystemUtils.RunExternalProcess("sc", "stop NvContainerLocalSystem", true);
                    System.Threading.Thread.Sleep(2000); // Espera 2s para garantir que o serviço morreu
                    SystemUtils.RunExternalProcess("sc", "start NvContainerLocalSystem", true);
                });

                return (true, "Serviços NVIDIA reiniciados e cache limpo.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static async Task<(bool Success, string Message)> RestartGraphicsDriver()
        {

            var gpuNames = SystemTweaks.GetAllGpuNames();
            var gpuName = gpuNames.FirstOrDefault(n => n.Contains("NVIDIA") || n.Contains("AMD"));
            
            if (string.IsNullOrEmpty(gpuName)) return (false, "GPU não detectada.");
            
            // Obtém PNPDeviceID de forma segura via registry
            string? regPath = SystemTweaks.FindGpuRegistryPathByDescription(gpuName);
            string pnpId = "";
            if (!string.IsNullOrEmpty(regPath))
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(regPath.Replace("HKEY_LOCAL_MACHINE\\", ""));
                    pnpId = key?.GetValue("MatchingDeviceId")?.ToString() ?? "";
                }
                catch { }
            }
            
            if (string.IsNullOrEmpty(pnpId)) return (false, "Não foi possível obter o ID da GPU.");

            try
            {
                // Executa o restart em uma Task separada para não travar a UI (mesmo com render software)
                await Task.Run(() =>
                {
                    string cmd = $"/c pnputil /disable-device \"{pnpId}\" && timeout /t 3 && pnputil /enable-device \"{pnpId}\"";
                    SystemUtils.RunExternalProcess("cmd.exe", cmd, hidden: true, waitForExit: true);
                });

                return (true, "Driver reiniciado com sucesso.");
            }
            catch (Exception ex) { return (false, ex.Message); }
        }

        public static void OpenNvidiaControlPanel()
        {
            try { Process.Start(new ProcessStartInfo("cmd", "/c start shell:AppsFolder\\NVIDIACorp.NVIDIAControlPanel_56jybvy8sckqj!NVIDIACorp.NVIDIAControlPanel") { CreateNoWindow = true }); } catch { }
        }

        public static void OpenWindowsColorManagement()
        {
            SystemUtils.RunExternalProcess("colorcpl.exe", "", hidden: false, waitForExit: false);
        }

        public static string GetCurrentResolutionInfo()
        {
            return $"{GetSystemMetrics(SM_CXSCREEN)}x{GetSystemMetrics(SM_CYSCREEN)}";
        }
    }
}