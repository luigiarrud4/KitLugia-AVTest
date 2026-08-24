using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace KitLugia.Core
{
    public enum KernelDriverStartType
    {
        Boot = 0,
        System = 1,
        Auto = 2,
        Demand = 3,
        Disabled = 4
    }

    public enum KernelDriverType
    {
        Kernel = 1,
        FileSystem = 2
    }

    public class KernelDriverInfo : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); } }

        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public string StartName { get; set; } = "";
        public int StartValue { get; set; } = -1;
        public string TypeName { get; set; } = "";
        public int TypeValue { get; set; }
        public string ImagePath { get; set; } = "";
        public string ResolvedPath { get; set; } = "";
        public bool IsThirdParty { get; set; }
        public string Manufacturer { get; set; } = "";
        public string ParentSoftware { get; set; } = "";
        public string CompanyName { get; set; } = "";
        public string FileVersion { get; set; } = "";
        public bool FileExists { get; set; }

        // UI helpers
        public string RiskLevel
        {
            get
            {
                if (IsThirdParty && StartValue == 0) return "CRÍTICO";
                if (IsThirdParty && StartValue == 1) return "ALTO";
                if (IsThirdParty && StartValue == 2) return "MÉDIO";
                if (!IsThirdParty && StartValue <= 1) return "SISTEMA";
                return "BAIXO";
            }
        }

        public string RiskColor
        {
            get
            {
                if (IsThirdParty && StartValue == 0) return "#FF3333";
                if (IsThirdParty && StartValue == 1) return "#FF6F00";
                if (IsThirdParty && StartValue == 2) return "#FFD700";
                return "#4CAF50";
            }
        }

        public string StartIcon
        {
            get => StartValue switch
            {
                0 => "[!!!]",
                1 => "[!! ]",
                2 => "[!  ]",
                3 => "[   ]",
                4 => "[---]",
                _ => "[ ? ]"
            };
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    public static class KernelDriverManager
    {
        // Known third-party driver prefixes (file name without extension, lower)
        private static readonly HashSet<string> _knownThirdPartyNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "avgArDisk","avgbidsh","avgbuniv","avgElam","avgRvrt","avgVmm","avgArPot","avgbidsdriver","avgDrvBrg","avgKbd","avgMonFlt","avgNetHub","avgRdr","avgSnx","avgSP","avgStm",
            "Ld9BoxNetLwf","Ld9BoxSup","BstkDrv","BstkDrv_nxt","SbieDrv","vmx86","vmnetbridge","vmnetuserif","hcmon","vmci","vsock","tap0901","WinDivert","WinDivert64","wintun",
            "logi_joy_bus_enum","logi_joy_vir_hid","logi_joy_xlcore","steamxbox","hidgamemap","MSIO","MsIo64","cpuz","e1d","e1dexpress","MEIx64","TeeDriver","igovsd","scmbus",
            "BlueStacksDrv","BlueStacksDrv_nxt","BEDaisy","BEService","EasyAntiCheat","vgk","vgc","faceit","mhyprot"
        };

        private static readonly HashSet<string> _knownMicrosoftProviders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Microsoft Corporation", "Microsoft", "Microsoft Windows", "Microsoft Windows Hardware Compatibility Publisher"
        };

        public static List<KernelDriverInfo> GetKernelDrivers(bool includeDisabled = false)
        {
            var list = new List<KernelDriverInfo>(350);
            try
            {
                using var baseKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
                if (baseKey == null) return list;

                foreach (var svcName in baseKey.GetSubKeyNames())
                {
                    try
                    {
                        using var svcKey = baseKey.OpenSubKey(svcName);
                        if (svcKey == null) continue;

                        object? typeObj = svcKey.GetValue("Type");
                        if (typeObj == null) continue;
                        int typeVal = Convert.ToInt32(typeObj);
                        if (typeVal != 1 && typeVal != 2) continue; // só Kernel / FileSystem

                        object? startObj = svcKey.GetValue("Start");
                        int startVal = startObj != null ? Convert.ToInt32(startObj) : -1;
                        if (!includeDisabled && startVal == 4) continue;

                        string imagePath = svcKey.GetValue("ImagePath")?.ToString() ?? "";
                        string displayName = svcKey.GetValue("DisplayName")?.ToString() ?? "";

                        var info = new KernelDriverInfo
                        {
                            Name = svcName,
                            DisplayName = displayName,
                            StartValue = startVal,
                            StartName = GetStartName(startVal),
                            TypeValue = typeVal,
                            TypeName = typeVal == 1 ? "Kernel" : "FileSystem",
                            ImagePath = imagePath,
                        };

                        // Resolve path físico
                        info.ResolvedPath = ResolveDriverPath(imagePath);
                        info.FileExists = !string.IsNullOrEmpty(info.ResolvedPath) && File.Exists(info.ResolvedPath);

                        // ParentSoftware + CompanyName
                        info.ParentSoftware = ExtractParentSoftware(imagePath, svcName);
                        if (info.FileExists)
                        {
                            try
                            {
                                var fvi = FileVersionInfo.GetVersionInfo(info.ResolvedPath);
                                info.CompanyName = fvi.CompanyName ?? "";
                                info.FileVersion = fvi.FileVersion ?? "";
                            }
                            catch { }
                        }

                        // Determina se é de terceiros - lógica robusta (diferencia bem)
                        info.IsThirdParty = DetermineIsThirdParty(svcName, imagePath, info.ResolvedPath, info.CompanyName);
                        info.Manufacturer = info.IsThirdParty ? "Terceiros" : "Microsoft";

                        list.Add(info);
                    }
                    catch { continue; }
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("GetKernelDrivers", ex.Message);
            }

            return list.OrderBy(d => d.StartValue).ThenBy(d => d.IsThirdParty ? 1 : 0).ThenBy(d => d.Name).ToList();
        }

        public static string GetStartName(int v) => v switch
        {
            0 => "Boot",
            1 => "System",
            2 => "Auto",
            3 => "Demand",
            4 => "Disabled",
            _ => $"Unknown({v})"
        };

        private static string ResolveDriverPath(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return "";
            try
            {
                string p = imagePath.Trim().Trim('"', '\'');
                // Remove prefixos NT
                if (p.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                    p = p.Replace(@"\SystemRoot", Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);
                else if (p.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
                    p = p.Substring(4);
                else if (p.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase))
                    p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), p);
                else if (p.StartsWith(@"%SystemRoot%", StringComparison.OrdinalIgnoreCase))
                    p = Environment.ExpandEnvironmentVariables(p);

                p = Environment.ExpandEnvironmentVariables(p);
                // Se ainda relativo, tenta System32
                if (!Path.IsPathRooted(p) && p.Contains("drivers", StringComparison.OrdinalIgnoreCase))
                    p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", p.Replace("System32\\", "", StringComparison.OrdinalIgnoreCase).TrimStart('\\'));

                return p;
            }
            catch { return ""; }
        }

        private static string ExtractParentSoftware(string imagePath, string svcName)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return svcName;
            try
            {
                // Program Files\Vendor\...
                var m = System.Text.RegularExpressions.Regex.Match(imagePath, @"Program Files(?: \(x86\))?\\([^\\]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value;

                // system32\drivers\Foo.sys -> Foo.sys
                var m2 = System.Text.RegularExpressions.Regex.Match(imagePath, @"system32\\drivers\\([^\\]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m2.Success) return m2.Groups[1].Value;

                var m3 = System.Text.RegularExpressions.Regex.Match(imagePath, @"FileRepository\\[^\\]+\\([^\\]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m3.Success) return m3.Groups[1].Value;
            }
            catch { }
            return svcName;
        }

        private static bool DetermineIsThirdParty(string svcName, string imagePath, string resolvedPath, string companyName)
        {
            // 1. Lista curada tem prioridade - corrige falsos negativos como AVG
            if (_knownThirdPartyNames.Contains(svcName)) return true;
            string fileName = Path.GetFileNameWithoutExtension(resolvedPath);
            if (!string.IsNullOrEmpty(fileName) && _knownThirdPartyNames.Contains(fileName)) return true;

            // 2. CompanyName do arquivo é o critério mais confiável
            if (!string.IsNullOrWhiteSpace(companyName))
            {
                string lower = companyName.ToLowerInvariant();
                if (lower.Contains("microsoft")) return false;
                // Qualquer CompanyName não-Microsoft com arquivo existente é terceiro
                if (!string.IsNullOrWhiteSpace(companyName)) return true;
            }

            // 3. Caminho: Program Files = sempre terceiro (exceto lista Microsoft)
            if (!string.IsNullOrWhiteSpace(imagePath))
            {
                string lower = imagePath.ToLowerInvariant();
                if (lower.Contains(@"program files") || lower.Contains(@"programdata\") || lower.Contains(@"\??\"))
                    return true;

                // DriverStore: se o INF contiver palavras de vendor conhecido, mas genérico
                // Considera Microsoft se vier de DriverStore e não tem CompanyName e não está na lista -> Microsoft inbox
                if (lower.Contains(@"driverstore\filerepository"))
                {
                    // Inbox drivers do Windows tem Company Microsoft ou sem CompanyName mas path windows
                    // Se chegou aqui sem CompanyName e não é conhecido, assume Microsoft inbox (menos falso positivo)
                    if (string.IsNullOrWhiteSpace(companyName))
                    {
                        // Heurística: arquivos genéricos como acpipagr, compositebus -> Microsoft
                        // Mantém false (Microsoft) para não poluir
                        return false;
                    }
                }

                // system32\drivers sem CompanyName -> checa se é da lista conhecida, senão assume Microsoft
                // (evita marcar ndis.sys, ntfs.sys como terceiro)
                if (lower.Contains(@"system32\drivers\"))
                {
                    // Se não tem CompanyName e não está na lista, é Microsoft inbox
                    if (string.IsNullOrWhiteSpace(companyName)) return false;
                }
            }

            // 4. Fallback: se não conseguiu resolver arquivo, mas serviço não é conhecido Microsoft
            // Verifica DisplayName com @*.inf (Microsoft) - assume Microsoft
            return false;
        }

        public static (int total, int thirdParty, int boot, int system, int auto) GetSummary(List<KernelDriverInfo> list)
        {
            return (
                list.Count,
                list.Count(d => d.IsThirdParty),
                list.Count(d => d.StartValue == 0),
                list.Count(d => d.StartValue == 1),
                list.Count(d => d.StartValue == 2)
            );
        }
    }
}
