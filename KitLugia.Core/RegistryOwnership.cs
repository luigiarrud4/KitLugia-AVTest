using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace KitLugia.Core
{
    [SuppressUnmanagedCodeSecurity]
    public static class RegistryOwnership
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool LookupPrivilegeValue([Optional] string? lpSystemName, string lpName, out long lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, [MarshalAs(UnmanagedType.Bool)] bool DisableAllPrivileges, ref TOKEN_PRIVILEGES NewState, int BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [DllImport("advapi32.dll", SetLastError = true, PreserveSig = true)]
        private static extern int RegOpenKeyEx(UIntPtr hKey, string subKey, uint ulOptions, int samDesired, out IntPtr phkResult);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern int RegCloseKey(IntPtr hKey);

        private const uint TOKEN_QUERY = 0x0008;
        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const int SE_PRIVILEGE_ENABLED = 0x2;

        private struct TOKEN_PRIVILEGES
        {
            public long Luid;
            public int Attributes;
        }

        /// <summary>
        /// Tenta tomar posse de uma chave de registro e conceder FullControl ao Administrador.
        /// </summary>
        public static bool ForceTakeOwnership(RegistryKey baseKey, string subKeyPath)
        {
            try
            {
                if (!EnableTakeOwnershipPrivilege())
                {
                    Logger.Log("[OWNERSHIP] Falha ao habilitar privilégio SeTakeOwnershipPrivilege");
                    return false;
                }

                using (RegistryKey? key = baseKey.OpenSubKey(subKeyPath,
                    RegistryRights.TakeOwnership | RegistryRights.ChangePermissions))
                {
                    if (key == null)
                    {
                        Logger.Log($"[OWNERSHIP] Não foi possível abrir: {subKeyPath}");
                        return false;
                    }

                    var security = key.GetAccessControl(
                        AccessControlSections.Owner | AccessControlSections.Access);

                    var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
                    security.SetOwner(adminSid);

                    var rule = new RegistryAccessRule(
                        adminSid,
                        RegistryRights.FullControl,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None,
                        AccessControlType.Allow
                    );
                    security.AddAccessRule(rule);

                    key.SetAccessControl(security);

                    Logger.Log($"[OWNERSHIP] Posse obtida com sucesso para: {subKeyPath}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[OWNERSHIP] Falha ao tomar posse de {subKeyPath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Normaliza o valor para tipos aceitos pelo registro.
        /// Converte bool → int, etc.
        /// </summary>
        public static object? NormalizeValue(object? value)
        {
            if (value is bool b)
                return b ? 1 : 0;
            return value;
        }

        /// <summary>
        /// Detecta automaticamente o RegistryValueKind correto baseado no tipo do valor.
        /// </summary>
        private static RegistryValueKind DetectValueKind(object? value)
        {
            if (value == null) return RegistryValueKind.String;

            return value switch
            {
                int or long or uint => RegistryValueKind.DWord,
                string[] => RegistryValueKind.MultiString,
                byte[] => RegistryValueKind.Binary,
                string s when s.Contains('%') && (s.Contains("%SystemRoot%") || s.Contains("%USERPROFILE%") || s.Contains("%PATH%"))
                    => RegistryValueKind.ExpandString,
                string => RegistryValueKind.String,
                bool => RegistryValueKind.DWord,
                _ => RegistryValueKind.String
            };
        }

        /// <summary>
        /// Tenta escrever um valor no registro com fallback de take-ownership e auto-detecção de tipo.
        /// NUNCA lança exceções para fora - sempre retorna true/false.
        /// </summary>
        public static bool TrySetValueWithOwnershipFallback(RegistryKey baseKey, string subKeyPath,
            string valueName, object? value, RegistryValueKind hintKind)
        {
            try
            {
                // Normalizar bool → int antes de qualquer operação
                value = NormalizeValue(value);
                
                // Auto-detectar o tipo correto baseado no valor real
                RegistryValueKind bestKind = DetectValueKind(value);
                // Se o hint não é DWord padrão, prefere o hint (confia no modelo)
                if (hintKind != RegistryValueKind.DWord && hintKind != RegistryValueKind.Unknown)
                    bestKind = hintKind;

                // Estratégia: tenta vários métodos em ordem, até um funcionar
                var strategies = new (RegistryValueKind? Kind, string Label)[]
                {
                    (bestKind, $"auto({bestKind})"),
                    (hintKind, $"hint({hintKind})"),
                    (null, "sem tipo"),
                };

                foreach (var (kind, label) in strategies)
                {
                    try
                    {
                        using (RegistryKey? key = baseKey.OpenSubKey(subKeyPath, true))
                        {
                            if (key == null)
                            {
                                // Tenta criar
                                using (var created = baseKey.CreateSubKey(subKeyPath, true))
                                {
                                    if (created == null) continue;
                                    if (value != null)
                                    {
                                        if (kind.HasValue) created.SetValue(valueName, value, kind.Value);
                                        else created.SetValue(valueName, value);
                                    }
                                    return true;
                                }
                            }

                            if (value == null)
                            {
                                if (key.GetValue(valueName) != null)
                                    key.DeleteValue(valueName, false);
                                return true;
                            }

                            if (kind.HasValue)
                                key.SetValue(valueName, value, kind.Value);
                            else
                                key.SetValue(valueName, value);

                            return true;
                        }
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // Sair do loop e tentar take-ownership
                        break;
                    }
                    catch (ArgumentException)
                    {
                        Logger.Log($"[OWNERSHIP] Estratégia '{label}' falhou, tentando próxima...");
                        continue;
                    }
                }

                // Se chegou aqui, todas as estratégias sem take-ownership falharam
                Logger.Log($"[OWNERSHIP] Acesso negado. Tentando tomar posse de: {subKeyPath}");
                if (ForceTakeOwnership(baseKey, subKeyPath))
                {
                    try
                    {
                        using (RegistryKey? key = baseKey.OpenSubKey(subKeyPath, true))
                        {
                            if (key == null) return false;
                            if (value != null)
                            {
                                // Após take ownership, tenta sem tipo (mais flexível)
                                key.SetValue(valueName, value);
                            }
                            else
                            {
                                if (key.GetValue(valueName) != null)
                                    key.DeleteValue(valueName, false);
                            }
                            Logger.Log($"[OWNERSHIP] Sucesso após take-ownership");
                            return true;
                        }
                    }
                    catch (Exception ex2)
                    {
                        Logger.Log($"[OWNERSHIP] Falha após take-ownership: {ex2.Message}");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[OWNERSHIP] Erro inesperado: {ex.Message}");
            }
            return false;
        }

        private static bool EnableTakeOwnershipPrivilege()
        {
            IntPtr tokenHandle = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(Process.GetCurrentProcess().Handle,
                    TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out tokenHandle))
                {
                    return false;
                }

                if (!LookupPrivilegeValue(null, "SeTakeOwnershipPrivilege", out long luid))
                {
                    return false;
                }

                var tp = new TOKEN_PRIVILEGES
                {
                    Luid = luid,
                    Attributes = SE_PRIVILEGE_ENABLED
                };

                return AdjustTokenPrivileges(tokenHandle, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
                return false;
            }
            finally
            {
                if (tokenHandle != IntPtr.Zero)
                    RegCloseKey(tokenHandle);
            }
        }
    }
}