using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace KitLugia.Core.UninstallTools
{
    /// <summary>
    /// Modelo de dados para programas instalados via Registry
    /// Inspirado no ApplicationUninstallerEntry do BCU
    /// </summary>
    public class RegistryProgram
    {
        /// <summary>
        /// Marcado quando o registro aponta para uma pasta que NAO existe mais no disco
        /// (app desinstalado, pasta movida ou instalador limpo). A UI mostra isso em vez
        /// de um "-" mudo: sem data e sem tamanho porque o arquivo nao esta la.
        /// </summary>
        public bool MissingOnDisk { get; set; }

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

            // Se não tiver DisplayName, tenta outros campos
            if (string.IsNullOrEmpty(program.DisplayName))
            {
                program.DisplayName = GetStringSafe(uninstallerKey, "AppName")
                                   ?? GetStringSafe(uninstallerKey, "Title")
                                   ?? program.Publisher;

                if (string.IsNullOrEmpty(program.DisplayName))
                {
                    return null;
                }
            }

            EnrichProgramMetadataFromRegistry(program, uninstallerKey);

            return program;
        }

        /// <summary>
        /// Enriquecimento SOMENTE REGISTRO — roda dentro do scan e precisa ser instantâneo:
        /// nenhum File.Exists/Directory.Exists/FileVersionInfo aqui (tudo que toca disco
        /// ficou em EnrichProgramMetadataFromDisk, executado em background pela AppsPage).
        /// Completa, de valores alternativos do próprio registro: InstallLocation, versão,
        /// fabricante, tamanho estimado e data de instalação.
        /// </summary>
        private static void EnrichProgramMetadataFromRegistry(RegistryProgram program, RegistryKey uninstallerKey)
        {
            try
            {
                // 1. InstallLocation: fallbacks que são pura leitura de registro. A validação
                //    de existência no disco fica para a fase background (EnrichProgramMetadataFromDisk).
                if (string.IsNullOrWhiteSpace(program.InstallLocation))
                {
                    var innoPath = GetStringSafe(uninstallerKey, "Inno Setup: App Path");
                    if (!string.IsNullOrWhiteSpace(innoPath))
                    {
                        program.InstallLocation = innoPath.Trim();
                    }
                    else
                    {
                        var src = GetStringSafe(uninstallerKey, "InstallSource");
                        if (!string.IsNullOrWhiteSpace(src) &&
                            !src.Contains("Windows\\Installer", StringComparison.OrdinalIgnoreCase) &&
                            !src.Contains("Temp", StringComparison.OrdinalIgnoreCase))
                        {
                            program.InstallLocation = src.Trim();
                        }
                    }
                }

                // 2. Versão de campos alternativos do registro
                if (string.IsNullOrWhiteSpace(program.DisplayVersion))
                {
                    var ver = GetStringSafe(uninstallerKey, "Version")
                           ?? GetStringSafe(uninstallerKey, "ProductVersion")
                           ?? GetStringSafe(uninstallerKey, "DisplayVersion_Legacy");
                    if (!string.IsNullOrWhiteSpace(ver)) program.DisplayVersion = ver;
                }
                if (string.IsNullOrWhiteSpace(program.Publisher))
                {
                    var pub = GetStringSafe(uninstallerKey, "RegCompany")
                           ?? GetStringSafe(uninstallerKey, "Contact")
                           ?? GetStringSafe(uninstallerKey, "AuthorizedCDFPrefix");
                    if (!string.IsNullOrWhiteSpace(pub)) program.Publisher = pub;
                }

                // 3. Tamanho estimado de campos alternativos do registro
                if (string.IsNullOrWhiteSpace(program.EstimatedSize) || program.EstimatedSize == "0")
                {
                    int sz = GetIntSafe(uninstallerKey, "EstimatedSize");
                    if (sz <= 0) sz = GetIntSafe(uninstallerKey, "InstalledSize");
                    if (sz <= 0) sz = GetIntSafe(uninstallerKey, "Size");
                    if (sz > 0)
                        program.EstimatedSize = sz.ToString(CultureInfo.InvariantCulture);
                }

                // 4. Data de instalação de campos alternativos do registro
                if (string.IsNullOrWhiteSpace(program.InstallDate))
                {
                    var id = GetStringSafe(uninstallerKey, "InstallDate") ?? GetStringSafe(uninstallerKey, "Date");
                    if (!string.IsNullOrWhiteSpace(id)) program.InstallDate = id;
                }
            }
            catch { }
        }

        /// <summary>
        /// Enriquecimento que TOCA DISCO (resolução de InstallLocation via DisplayIcon/
        /// UninstallString, versão/fabricante via FileVersionInfo, data via timestamp).
        /// NUNCA chamar dentro do scan: cada app pode custar dezenas de I/Os e o
        /// carregamento instantâneo da lista viraria lento. Roda em background com a
        /// lista já na tela.
        /// </summary>
        public static void EnrichProgramMetadataFromDisk(RegistryProgram program)
        {
            try
            {
                // Registro orfao: aponta para uma pasta que nao existe mais. Sem data e
                // sem tamanho porque o conteudo nao esta no disco — a UI sinaliza isso.
                if (!string.IsNullOrWhiteSpace(program.InstallLocation) && !Directory.Exists(program.InstallLocation))
                    program.MissingOnDisk = true;

                // 0. Produtos MSI (UninstallString "MsiExec.exe /X{GUID}" sem caminho):
                //    o Windows Installer resolve o InstallLocation real via msi.dll.
                TryResolveMsiInstallLocation(program);

                // 1. Resolver InstallLocation se estiver vazio ou apontando para pasta inexistente
                if (string.IsNullOrWhiteSpace(program.InstallLocation) || !Directory.Exists(program.InstallLocation))
                {
                    // a) DisplayIcon: usa a PASTA do icone mesmo quando o .ico em si nao
                    //    existe (instaladores removem o .ico mas a pasta fica). Sem isso o
                    //    app caia no UninstallString e ficava apontando para o cache do
                    //    instalador (C:\Windows\Installer\...), que nao serve nem de
                    //    pasta de instalacao nem de pasta medivel.
                    if (!string.IsNullOrWhiteSpace(program.DisplayIcon))
                    {
                        string iconPath = CleanPathString(program.DisplayIcon);
                        if (iconPath.Length > 0)
                        {
                            var dir = Path.GetDirectoryName(iconPath);
                            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !IsWindowsInstallerCache(dir))
                                program.InstallLocation = dir;
                        }
                    }
                    // b) UninstallString
                    if (string.IsNullOrWhiteSpace(program.InstallLocation) && !string.IsNullOrWhiteSpace(program.UninstallString))
                    {
                        string? exe = ExtractExecutablePath(program.UninstallString);
                        if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                        {
                            var dir = Path.GetDirectoryName(exe);
                            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir) && !IsWindowsInstallerCache(dir))
                                program.InstallLocation = dir;
                        }
                    }
                }

                // 2. Tentar obter executável representativo para extração de versão e fabricante
                string? sampleExe = null;
                if (!string.IsNullOrWhiteSpace(program.DisplayIcon))
                {
                    string iconClean = CleanPathString(program.DisplayIcon);
                    if (iconClean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(iconClean))
                        sampleExe = iconClean;
                }
                if (sampleExe == null && !string.IsNullOrWhiteSpace(program.UninstallString))
                {
                    string? uExe = ExtractExecutablePath(program.UninstallString);
                    if (!string.IsNullOrEmpty(uExe) && File.Exists(uExe))
                        sampleExe = uExe;
                }
                if (sampleExe == null && !string.IsNullOrWhiteSpace(program.InstallLocation) && Directory.Exists(program.InstallLocation))
                {
                    try
                    {
                        var exes = Directory.GetFiles(program.InstallLocation, "*.exe", new EnumerationOptions
                        {
                            IgnoreInaccessible = true,
                            RecurseSubdirectories = false,
                            AttributesToSkip = 0 // legado NÃO pulava .exe ocultos/de sistema
                        });
                        if (exes.Length > 0)
                            sampleExe = exes[0];
                    }
                    catch { }
                }

                // 3. Versão/fabricante que continuarem faltando: extrai do binário via FileVersionInfo
                if (sampleExe != null && (string.IsNullOrWhiteSpace(program.DisplayVersion) || string.IsNullOrWhiteSpace(program.Publisher)))
                {
                    try
                    {
                        var vi = FileVersionInfo.GetVersionInfo(sampleExe);
                        if (string.IsNullOrWhiteSpace(program.DisplayVersion))
                        {
                            string v = vi.FileVersion ?? vi.ProductVersion ?? "";
                            if (!string.IsNullOrWhiteSpace(v)) program.DisplayVersion = v.Trim();
                        }
                        if (string.IsNullOrWhiteSpace(program.Publisher))
                        {
                            string c = vi.CompanyName ?? "";
                            if (!string.IsNullOrWhiteSpace(c)) program.Publisher = c.Trim();
                        }
                    }
                    catch { }
                }

                // 4. Data de instalação faltante: timestamp de criação da pasta ou do exe
                if (string.IsNullOrWhiteSpace(program.InstallDate))
                {
                    if (!string.IsNullOrWhiteSpace(program.InstallLocation) && Directory.Exists(program.InstallLocation))
                    {
                        try
                        {
                            var dt = Directory.GetCreationTime(program.InstallLocation);
                            if (dt.Year >= 2000 && dt <= DateTime.Now.AddDays(1))
                                program.InstallDate = dt.ToString("yyyyMMdd");
                        }
                        catch { }
                    }
                    else if (sampleExe != null && File.Exists(sampleExe))
                    {
                        try
                        {
                            var dt = File.GetCreationTime(sampleExe);
                            if (dt.Year >= 2000 && dt <= DateTime.Now.AddDays(1))
                                program.InstallDate = dt.ToString("yyyyMMdd");
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        public static string CleanPathString(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string s = raw.Trim().Trim('"');
            if (s.Contains(','))
            {
                s = s.Split(',')[0].Trim().Trim('"');
            }
            return s;
        }

        public static string? ExtractExecutablePath(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim();
            if (s.StartsWith("\""))
            {
                int endQuote = s.IndexOf('"', 1);
                if (endQuote > 1)
                {
                    string candidate = s.Substring(1, endQuote - 1);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            int exeIdx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIdx >= 0)
            {
                string candidate = s.Substring(0, exeIdx + 4).Trim('"', ' ');
                if (File.Exists(candidate)) return candidate;
            }
            if (s.Contains(" "))
            {
                string candidate = s.Split(new[] { ' ' }, 2)[0].Trim('"', ' ');
                if (File.Exists(candidate)) return candidate;
            }
            if (File.Exists(s.Trim('"', ' '))) return s.Trim('"', ' ');
            return null;
        }

        private static string? GetStringSafe(RegistryKey key, string name)
        {
            try
            {
                var value = key.GetValue(name);
                return value?.ToString();
            }
            // Sem log: chave sem permissao ou valor corrompido acontece em dezenas de
            // entradas do registro e virava spam de "[AVISO] Exception suppressed" no
            // console do kit. O campo que falhou simplesmente fica vazio.
            catch { return null; }
        }

        private static int GetIntSafe(RegistryKey key, string name)
        {
            try
            {
                var value = key.GetValue(name);
                if (value == null) return 0;
                // Instaladores escrevem lixo em valores DWORD (ex.: "1,5MB", strings).
                // Convert.ToInt32 estourava FormatException aqui e o catch abaixo
                // virava spam de "[AVISO] Exception suppressed" no log (RegistryProgram.cs:385).
                if (value is int i) return i;
                if (value is long l) return l > int.MaxValue ? int.MaxValue : (int)l;
                return int.TryParse(value.ToString()?.Trim(), out var parsed) ? parsed : 0;
            }
            catch { return 0; }
        }

        // ===== Resolucao de InstallLocation para entradas MSI =====
        // Entradas Windows Installer guardam o produto como GUID (UninstallString
        // "MsiExec.exe /X{GUID}" ou RegistryKeyName "{GUID}") e NAO tem InstallLocation.
        // A MUI API do Windows Installer resolve o caminho real de instalacao.
        // Importa-se sob demanda para NAO carregar msi.dll no startup do kit.

        [DllImport("msi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int MsiGetProductInfoW(string szProduct, string szProperty, StringBuilder lpValueBuf, ref int pcchValueBuf);

        [DllImport("msi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint MsiLocateComponentW(string szComponent, StringBuilder lpPathBuf, ref int pcchPathBuf);

        private static bool LooksLikeMsiGuid(string? s)
            => !string.IsNullOrWhiteSpace(s) && s.Trim().Length == 38 && s.Trim()[0] == '{' && s.Trim()[^1] == '}';

        /// <summary>
        /// Resolve o InstallLocation real de produtos Windows Installer (MSI) cujo
        /// registro nao declara caminho. Chamado APENAS da fase background de
        /// enriquecimento (toca disco via msi.dll) — nunca no scan so-registro.
        /// </summary>
        public static void TryResolveMsiInstallLocation(RegistryProgram program)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(program.InstallLocation) && Directory.Exists(program.InstallLocation))
                    return;

                // 1. GUID do produto: UninstallString "MsiExec.exe /X{GUID}" ou RegistryKeyName.
                string? guid = null;
                if (!string.IsNullOrWhiteSpace(program.UninstallString))
                {
                    int idx = program.UninstallString.IndexOf('{');
                    int end = idx >= 0 ? program.UninstallString.IndexOf('}', idx) : -1;
                    if (idx >= 0 && end > idx && end - idx + 1 == 38)
                        guid = program.UninstallString.Substring(idx, 38);
                }
                guid ??= LooksLikeMsiGuid(program.RegistryKeyName) ? program.RegistryKeyName.Trim() : null;
                if (guid == null) return;

                var buf = new StringBuilder(512);
                int len = buf.Capacity;
                if (MsiGetProductInfoW(guid, "InstallLocation", buf, ref len) == 0 && len > 0 && buf.Length > 0)
                {
                    string loc = buf.ToString();
                    if (Directory.Exists(loc))
                    {
                        program.InstallLocation = loc;
                        return;
                    }
                }

                // 2. Fallback: pega um componente do produto e localiza o arquivo dele.
                //    (sem enumeracao de componentes aqui — custo/beneficio baixo para o
                //    caso comum; o caminho do produto ja resolve a maioria)
            }
            catch { }
        }

        /// <summary>
        /// C:\Windows\Installer (e InstallShield Installation Information) guardam o
        /// CACHE do instalador, nao os arquivos do app. Usar essa pasta como
        /// InstallLocation produz tamanho/data sem sentido e impede medir o app real.
        /// </summary>
        private static bool IsWindowsInstallerCache(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try
            {
                string d = Path.GetFullPath(dir);
                return d.StartsWith(@"C:\Windows\Installer", StringComparison.OrdinalIgnoreCase) ||
                       d.StartsWith(@"C:\WINDOWS\Installer", StringComparison.OrdinalIgnoreCase) ||
                       d.Contains(@"\InstallShield Installation Information", StringComparison.OrdinalIgnoreCase) ||
                       d.Contains(@"\Windows\Installer\", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// FASE FINAL de completude: apps que terminaram o enriquecimento ainda sem
        /// InstallLocation entram aqui. Procura a pasta de instalação pelo nome do app
        /// nas raízes padrão (Program Files, Program Files (x86), ProgramData,
        /// %LOCALAPPDATA%\Programs, %APPDATA%). Só aceita a pasta se ela existir E
        /// contiver ao menos um .exe na raiz — evita associar o app à pasta errada.
        /// Devolve true se preencheu a localização.
        /// </summary>
        public static bool TryResolveMissingInstallLocation(RegistryProgram program)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(program.InstallLocation)) return false;

                string alvo = NormalizeFolderKey(program.DisplayName);
                if (alvo.Length < 3) return false;

                foreach (var raiz in CommonInstallRoots())
                {
                    if (!Directory.Exists(raiz)) continue;

                    // 2 niveis: raiz (ex.: "Program Files\Razer Synapse") e subpasta de
                    // vendor (ex.: "Program Files\Razer\Razer Synapse").
                    foreach (var pasta in EnumerateCandidateFolders(raiz))
                    {
                        string nome = Path.GetFileName(pasta);
                        if (NormalizeFolderKey(nome) != alvo) continue;

                        // A pasta precisa SER do app: contem um executavel na raiz.
                        string exe = FindExecutableIn(pasta);
                        if (exe == null) continue;

                        program.InstallLocation = pasta;
                        if (string.IsNullOrWhiteSpace(program.DisplayIcon))
                        {
                            try { program.DisplayIcon = exe; } catch { }
                        }
                        return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        private static IEnumerable<string> EnumerateCandidateFolders(string raiz)
        {
            // 2 NÍVEIS, como sempre: raiz (ex.: "Program Files\Razer Synapse") e
            // subpasta de vendor (ex.: "Program Files\Razer\Razer Synapse").
            // A ordem e a profundidade importam: TryResolveMissingInstallLocation fica
            // com a PRIMEIRA pasta cujo nome bate e que tem .exe na raiz. Uma versão
            // recursiva aqui fazia a resolução cair em subpastas mais fundas (ou falhar
            // por pular .exe ocultos/de sistema) e apps perdiam a data de instalação.
            // IgnoreInaccessible apenas elimina as exceções do debugger — os resultados
            // ficam idênticos aos do GetDirectories legado (que não pulava nada).
            var opts = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                AttributesToSkip = 0 // legado NÃO pulava hidden/system/reparse
            };

            string[] subpastas;
            try { subpastas = Directory.GetDirectories(raiz, "*", opts); }
            catch { yield break; }

            foreach (var pasta in subpastas)
                yield return pasta;

            // Um nivel abaixo: a pasta do app costuma estar dentro da pasta do vendor.
            foreach (var pasta in subpastas)
            {
                string[] netas;
                try { netas = Directory.GetDirectories(pasta, "*", opts); }
                catch { continue; }
                foreach (var neta in netas)
                    yield return neta;
            }
        }

        /// <summary>
        /// Chave de comparacao de nomes de pasta: maiusculas, sem acentos e sem
        /// pontuacao ("Razer Synapse (x64)" e "razer_synapse" viram a mesma chave).
        /// </summary>
        private static string NormalizeFolderKey(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (char c in raw.Trim())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToUpperInvariant(c));
            }
            // Versoes no fim do nome ("RustDesk 1.4.6") nao fazem parte da chave.
            string s = sb.ToString();
            while (s.Length > 0 && char.IsDigit(s[^1])) s = s[..^1];
            return s.TrimEnd('X', 'B');
        }

        private static string? FindExecutableIn(string folder)
        {
            try
            {
                var exes = Directory.GetFiles(folder, "*.exe", new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = false,
                    AttributesToSkip = 0 // legado NÃO pulava .exe ocultos/de sistema
                });
                return exes.Length > 0 ? exes[0] : null;
            }
            catch { return null; }
        }

        private static IEnumerable<string> CommonInstallRoots()
        {
            var roots = new List<string>();
            void Add(string? p)
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                try
                {
                    string full = Path.GetFullPath(p);
                    if (!roots.Contains(full, StringComparer.OrdinalIgnoreCase)) roots.Add(full);
                }
                catch { }
            }

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            Add(pf);
            Add(pf86);
            Add(pd);
            if (!string.IsNullOrWhiteSpace(local))
            {
                Add(Path.Combine(local, "Programs"));
                Add(local);
            }
            if (!string.IsNullOrWhiteSpace(roaming)) Add(roaming);
            return roots;
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
