using Microsoft.Win32;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static partial class Toolbox
    {
        /// <summary>
        /// Obtém uma lista de programas instalados no sistema, buscando em todos os locais comuns do registro.
        /// </summary>
        /// <returns>Uma lista de objetos 'InstalledProgram' ordenada alfabeticamente.</returns>
        public static List<InstalledProgram> GetInstalledPrograms()
        {

            // Típico: 50-200 programas instalados
            var programs = new List<InstalledProgram>(200);

            // Define os locais a serem verificados:
            // 1. Programas instalados para todos os usuários (64-bit e 32-bit em sistemas 64-bit)
            // 2. Programas instalados apenas para o usuário atual.
            var registryKeysToScan = new[]
            {
                (Root: Registry.LocalMachine, Path: @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Root: Registry.LocalMachine, Path: @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                (Root: Registry.CurrentUser, Path: @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
            };

            foreach (var (root, path) in registryKeysToScan)
            {
                try
                {
                    using var baseKey = root.OpenSubKey(path);
                    if (baseKey == null) continue;

                    foreach (var subKeyName in baseKey.GetSubKeyNames())
                    {
                        using var subKey = baseKey.OpenSubKey(subKeyName);
                        if (subKey == null) continue;

                        var displayName = subKey.GetValue("DisplayName") as string;
                        var systemComponent = subKey.GetValue("SystemComponent") as int?;

                        // Aplica filtros para obter uma lista limpa:
                        // - Precisa ter um nome de exibição.
                        // - Não pode ser um componente do sistema.
                        // - Não pode ser uma atualização do Windows (KB...).
                        // - Não pode já estar na lista (evita duplicatas).
                        if (!string.IsNullOrWhiteSpace(displayName) &&
                            systemComponent != 1 &&
                            !displayName.StartsWith("Update for") &&
                            !displayName.Contains("KB") &&
                            !programs.Any(p => p.Name.Equals(displayName, System.StringComparison.OrdinalIgnoreCase)))
                        {
                            programs.Add(new InstalledProgram(
                                displayName,
                                subKey.GetValue("Publisher") as string ?? "N/A",
                                subKey.GetValue("DisplayVersion") as string ?? "N/A"
                            ));
                        }
                    }
                }
                catch
                {
                    // Ignora erros de permissão ou chaves inacessíveis.
                }
            }

            // Retorna a lista final, ordenada pelo nome do programa.
            return programs.OrderBy(p => p.Name).ToList();
        }
    }
}