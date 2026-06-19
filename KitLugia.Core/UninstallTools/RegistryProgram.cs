using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Win32;

namespace KitLugia.Core.UninstallTools
{
    /// <summary>
    /// Modelo de dados para programas instalados via Registry
    /// Inspirado no ApplicationUninstallerEntry do BCU
    /// </summary>
    public class RegistryProgram
    {
        public string DisplayName { get; set; } = "";
        public string DisplayVersion { get; set; } = "";
        public string Publisher { get; set; } = "";
        public string InstallLocation { get; set; } = "";
        public string UninstallString { get; set; } = "";
        public string QuietUninstallString { get; set; } = "";
        public string InstallDate { get; set; } = "";
        public string EstimatedSize { get; set; } = "";
        public string DisplayIcon { get; set; } = "";
        public string AboutUrl { get; set; } = "";
        public bool IsProtected { get; set; }
        public bool IsSystemComponent { get; set; }
        public bool Is64Bit { get; set; }
        public string RegistryPath { get; set; } = "";
        public string RegistryKeyName { get; set; } = "";
    }

    /// <summary>
    /// Factory simplificado para detectar programas instalados via Registry
    /// Inspirado no RegistryFactory do BCU
    /// </summary>
    public class RegistryProgramFactory
    {
        private static readonly string RegUninstallersKeyDirect =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        private static readonly string RegUninstallersKeyWow =
            @"SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

        /// <summary>
        /// Obtém lista de programas instalados via Registry
        /// </summary>
        public static List<RegistryProgram> GetInstalledPrograms()
        {
            var programs = new List<RegistryProgram>();

            try
            {
                // Obtém chaves de registro de 32-bit e 64-bit
                var registryKeys = GetParentRegistryKeys();

                foreach (var kvp in registryKeys)
                {
                    if (kvp.Key == null) continue;

                    try
                    {
                        var subKeyNames = kvp.Key.GetSubKeyNames();
                        foreach (var subKeyName in subKeyNames)
                        {
                            try
                            {
                                var subKey = kvp.Key.OpenSubKey(subKeyName);
                                if (subKey == null) continue;

                                var program = CreateFromRegistry(subKey, kvp.Value);
                                if (program != null && !string.IsNullOrEmpty(program.DisplayName))
                                {
                                    programs.Add(program);
                                }

                                subKey.Close();
                            }
                            catch (UnauthorizedAccessException)
                            {
                                // Ignora chaves sem permissão de acesso
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"Erro ao ler chave {subKeyName}: {ex.Message}");
                            }
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Ignora chaves sem permissão de acesso
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Erro ao obter sub-chaves: {ex.Message}");
                    }

                    kvp.Key.Close();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Erro ao obter programas: {ex.Message}");
            }

            return programs;
        }

        private static RegistryProgram? CreateFromRegistry(RegistryKey uninstallerKey, bool is64Bit)
        {
            if (uninstallerKey == null) return null;

            var program = new RegistryProgram
            {
                DisplayName = GetStringSafe(uninstallerKey, "DisplayName") ?? "",
                DisplayVersion = GetStringSafe(uninstallerKey, "DisplayVersion") ?? "",
                Publisher = GetStringSafe(uninstallerKey, "Publisher") ?? "",
                InstallLocation = GetStringSafe(uninstallerKey, "InstallLocation") ?? "",
                UninstallString = GetStringSafe(uninstallerKey, "UninstallString") ?? "",
                QuietUninstallString = GetStringSafe(uninstallerKey, "QuietUninstallString") ?? "",
                InstallDate = GetStringSafe(uninstallerKey, "InstallDate") ?? "",
                EstimatedSize = GetStringSafe(uninstallerKey, "EstimatedSize") ?? "",
                DisplayIcon = GetStringSafe(uninstallerKey, "DisplayIcon") ?? "",
                AboutUrl = GetAboutUrl(uninstallerKey) ?? "",
                IsProtected = GetIntSafe(uninstallerKey, "NoRemove") != 0,
                IsSystemComponent = GetIntSafe(uninstallerKey, "SystemComponent") != 0,
                Is64Bit = is64Bit,
                RegistryPath = uninstallerKey.Name,
                RegistryKeyName = uninstallerKey.Name
            };

            // Se não tiver DisplayName, ignora
            if (string.IsNullOrEmpty(program.DisplayName))
            {
                // Tenta usar Publisher como fallback
                if (!string.IsNullOrEmpty(program.Publisher))
                {
                    program.DisplayName = program.Publisher;
                }
                else
                {
                    return null;
                }
            }

            return program;
        }

        private static string? GetStringSafe(RegistryKey key, string name)
        {
            try
            {
                var value = key.GetValue(name);
                return value?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static int GetIntSafe(RegistryKey key, string name)
        {
            try
            {
                var value = key.GetValue(name);
                if (value == null) return 0;
                return Convert.ToInt32(value);
            }
            catch
            {
                return 0;
            }
        }

        private static string? GetAboutUrl(RegistryKey uninstallerKey)
        {
            var urlSources = new[] { "URLInfoAbout", "URLUpdateInfo", "HelpLink" };
            foreach (var urlSource in urlSources)
            {
                var url = GetStringSafe(uninstallerKey, urlSource);
                if (!string.IsNullOrEmpty(url) && url.Contains('.'))
                    return url;
            }
            return null;
        }

        private static List<KeyValuePair<RegistryKey, bool>> GetParentRegistryKeys()
        {
            var keysToCheck = new List<KeyValuePair<RegistryKey, bool>>();

            var hklm = Registry.LocalMachine;
            var hkcu = Registry.CurrentUser;

            bool is64Bit = Environment.Is64BitOperatingSystem;

            if (is64Bit)
            {
                // 64-bit system - check both 32-bit and 64-bit keys
                AddKeyIfValid(keysToCheck, hklm, RegUninstallersKeyDirect, true);
                AddKeyIfValid(keysToCheck, hkcu, RegUninstallersKeyDirect, true);
                AddKeyIfValid(keysToCheck, hklm, RegUninstallersKeyWow, false);
                AddKeyIfValid(keysToCheck, hkcu, RegUninstallersKeyWow, false);
            }
            else
            {
                // 32-bit system - only check 32-bit keys
                AddKeyIfValid(keysToCheck, hklm, RegUninstallersKeyDirect, false);
                AddKeyIfValid(keysToCheck, hkcu, RegUninstallersKeyDirect, false);
            }

            return keysToCheck;
        }

        private static void AddKeyIfValid(List<KeyValuePair<RegistryKey, bool>> list, RegistryKey baseKey, string subKeyName, bool is64Bit)
        {
            try
            {
                var key = baseKey.OpenSubKey(subKeyName);
                if (key != null)
                {
                    list.Add(new KeyValuePair<RegistryKey, bool>(key, is64Bit));
                }
            }
            catch
            {
                // Ignora chaves que não podem ser abertas
            }
        }
    }
}
