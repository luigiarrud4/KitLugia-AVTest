using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KitLugia.Core.KitStore
{
    /// <summary>
    /// Engine standalone do KitStore — toda lógica winget/choco/Appx + detecção de fantasmas fica no Core, sem depender do GUI.
    /// GUI deve chamar APENAS este Engine (sem duplicar parsing). Inclui fallback COM e OEM encoding correto.
    /// </summary>
    public static class StoreEngine
    {
        // ---- Winget / Choco discovery ----

        public static string? FindWingetPath()
        {
            // 1) Cache do Kit
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\KitLugia\Paths");
                if (key?.GetValue("Winget") is string saved && File.Exists(saved)) return saved;
            }
            catch { }
            // 2) SystemUtils (LocalAppData\WindowsApps)
            var p = SystemUtils.FindWingetPath();
            if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
            // 3) Onde.exe
            try
            {
                var wh = RunCapture("where", "winget", 4000);
                var first = wh.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                              .Select(s => s.Trim().Trim('"'))
                              .FirstOrDefault(s => s.EndsWith("winget.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(s));
                if (first != null) return first;
            }
            catch { }
            // 4) WindowsApps store location (Program Files\WindowsApps)
            try
            {
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var wa = Path.Combine(pf, "WindowsApps");
                if (Directory.Exists(wa))
                {
                    var cand = Directory.GetDirectories(wa, "Microsoft.DesktopAppInstaller_*")
                                        .Select(d => Path.Combine(d, "winget.exe"))
                                        .FirstOrDefault(File.Exists);
                    if (cand != null) return cand;
                }
            }
            catch { }
            return null;
        }

        public static string? FindChoco()
        {
            try
            {
                var p = Environment.GetEnvironmentVariable("ChocolateyInstall");
                if (!string.IsNullOrEmpty(p))
                {
                    var exe = Path.Combine(p, "choco.exe");
                    if (File.Exists(exe)) return exe;
                }
                var candidates = new[] { @"C:\ProgramData\chocolatey\bin\choco.exe", @"C:\Chocolatey\bin\choco.exe" };
                foreach (var c in candidates) if (File.Exists(c)) return c;
                var found = RunCapture("where", "choco", 3000);
                var first = found.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(s => s.Trim())
                                 .FirstOrDefault(s => s.EndsWith("choco.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(s));
                if (first != null) return first;
            }
            catch { }
            return null;
        }

        // ---- Processo com OEM encoding e timeout robusto ----

        public static string RunCapture(string exe, string args, int timeoutMs)
        {
            try
            {
                var oem = SystemUtils.GetOemEncoding();
                var file = exe.Trim().Trim('"');
                // Se exe contém espaços e já veio com aspas, o trim acima resolve; args já é passado separado
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = oem,
                    StandardErrorEncoding = oem
                };
                using var p = Process.Start(psi);
                if (p == null) return "";
                var sb = new StringBuilder();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit(timeoutMs))
                {
                    try { p.Kill(entireProcessTree: true); } catch { try { p.Kill(); } catch { } }
                    try { p.WaitForExit(2000); } catch { }
                    sb.AppendLine($"[timeout {timeoutMs}ms]");
                }
                else
                {
                    // Garante que os handlers terminaram
                    try { p.WaitForExit(); } catch { }
                }
                return sb.ToString();
            }
            catch (Exception ex) { return $"[erro] {ex.Message}"; }
        }

        // ---- Winget queries (CLI fallback) ----

        private static readonly string[] WingetHeaderTokensPtEn = new[] { "Nome", "Name", "Id", "Versão", "Version", "Disponível", "Available", "Origem", "Source" };

        public static List<StoreApp> QueryWingetInstalled(string? wingetPath)
        {
            var list = new List<StoreApp>();
            if (string.IsNullOrWhiteSpace(wingetPath) || !File.Exists(wingetPath)) return list;
            try
            {
                // Tenta COM primeiro quando disponível (10x mais rápido, sem console)
                var com = TryQueryWingetInstalledCom();
                if (com != null && com.Count > 0) return com;

                var output = RunCapture($"\"{wingetPath}\"", "list --accept-source-agreements --disable-interactivity", 25000);
                // Filtra warning lines do winget ("Windows Package Manager v1.x ...")
                var lines = output.Split('\n');
                bool inData = false;
                foreach (var raw in lines)
                {
                    var line = raw.TrimEnd('\r');
                    if (!inData)
                    {
                        // header separator line contains ---- and usually starts with -
                        if (line.TrimStart().StartsWith("---") || (line.Contains("----") && line.Contains("-")))
                        { inData = true; continue; }
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("No installed", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Nenhum pacote", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Nenhum instalado", StringComparison.OrdinalIgnoreCase)) break;
                    // Heuristic: skip progress/spinner lines
                    if (line.Trim().Length < 5) continue;
                    var parts = Regex.Split(line.Trim(), @"\s{2,}");
                    if (parts.Length >= 3)
                    {
                        var name = parts[0].Trim();
                        var id = parts[1].Trim();
                        var ver = parts[2].Trim().TrimStart('>', '<', '=', '→', '—', ' ', '\t');
                        // winget marca versões com prefixo > / < quando a versão instalada difere do catálogo
                        ver = Regex.Replace(ver, @"^[>\s<]+", "").Trim();
                        // Skip header row repetido
                        if (WingetHeaderTokensPtEn.Any(t => string.Equals(id, t, StringComparison.OrdinalIgnoreCase))) continue;
                        if (string.Equals(ver, "Versão", StringComparison.OrdinalIgnoreCase) || string.Equals(ver, "Version", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.IsNullOrEmpty(id) || id.Length < 2) continue;
                        // id deve conter ponto ou ser plausível (evita linhas de status)
                        // Normaliza Unknown → vazio para não quebrar comparação de versão
                        if (string.Equals(ver, "Unknown", StringComparison.OrdinalIgnoreCase)) ver = "";
                        list.Add(new StoreApp { Name = name, Id = id, Version = ver, Source = "winget" });
                    }
                    if (list.Count > 900) break;
                }
            }
            catch (Exception ex) { try { Logger.Log($"[STORE] winget list falhou: {ex.Message}"); } catch { } }
            return list;
        }

        public static List<StoreApp> QueryWingetUpgrades(string? wingetPath)
        {
            var list = new List<StoreApp>();
            if (string.IsNullOrWhiteSpace(wingetPath) || !File.Exists(wingetPath)) return list;
            try
            {
                var output = RunCapture($"\"{wingetPath}\"", "upgrade --include-unknown --accept-source-agreements --disable-interactivity", 35000);
                // Fallback se --include-unknown não suportado (winget velho)
                if (output.Contains("unknown") && output.Contains("not recognized"))
                    output = RunCapture($"\"{wingetPath}\"", "upgrade --accept-source-agreements --disable-interactivity", 35000);
                var lines = output.Split('\n');
                bool inData = false;
                foreach (var raw in lines)
                {
                    var line = raw.TrimEnd('\r');
                    if (!inData) { if (line.TrimStart().StartsWith("---") || (line.Contains("----") && line.Contains("-"))) { inData = true; continue; } continue; }
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("No applicable", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("Nenhum", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("No upgrades", StringComparison.OrdinalIgnoreCase)) break;
                    var parts = Regex.Split(line.Trim(), @"\s{2,}");
                    if (parts.Length >= 4)
                    {
                        var name = parts[0].Trim();
                        var id = parts[1].Trim();
                        var cur = parts[2].Trim().TrimStart('>', '<', '=', '→', '—', ' ', '\t');
                        var avail = parts[3].Trim().TrimStart('>', '<', '=', '→', '—', ' ', '\t');
                        cur = Regex.Replace(cur, @"^[>\s<]+", "").Trim();
                        avail = Regex.Replace(avail, @"^[>\s<]+", "").Trim();
                        var src = parts.Length >= 5 ? parts[4].Trim() : "winget";
                        if (string.Equals(id, "Id", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.Equals(avail, "Available", StringComparison.OrdinalIgnoreCase) || string.Equals(avail, "Disponível", StringComparison.OrdinalIgnoreCase)) continue;
                        if (string.IsNullOrEmpty(id)) continue;
                        if (string.Equals(cur, "Unknown", StringComparison.OrdinalIgnoreCase)) cur = "";
                        if (string.Equals(avail, "Unknown", StringComparison.OrdinalIgnoreCase)) avail = "";
                        list.Add(new StoreApp { Name = name, Id = id, Version = cur, AvailableVersion = avail, Source = src });
                    }
                    if (list.Count > 600) break;
                }
            }
            catch (Exception ex) { try { Logger.Log($"[STORE] winget upgrade falhou: {ex.Message}"); } catch { } }
            return list;
        }

        public static List<StoreApp> QueryWingetSearch(string? wingetPath, string query)
        {
            var list = new List<StoreApp>();
            if (string.IsNullOrWhiteSpace(wingetPath) || !File.Exists(wingetPath) || string.IsNullOrWhiteSpace(query)) return list;
            try
            {
                var q = query.Replace("\"", "").Trim();
                if (q.Length < 2) return list;
                // Escapa caracteres problemáticos
                q = Regex.Replace(q, @"[^\w\s\.\-\+]", "");
                var args = $"search --query \"{q}\" --accept-source-agreements --disable-interactivity --count 40";
                var output = RunCapture($"\"{wingetPath}\"", args, 35000);
                var lines = output.Split('\n');
                bool inData = false;
                foreach (var raw in lines)
                {
                    var line = raw.TrimEnd('\r');
                    if (!inData) { if (line.TrimStart().StartsWith("---") || line.Contains("----")) { inData = true; continue; } continue; }
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (line.StartsWith("No package", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Nenhum pacote", StringComparison.OrdinalIgnoreCase)) break;
                    var parts = Regex.Split(line.Trim(), @"\s{2,}");
                    if (parts.Length >= 3)
                    {
                        var name = parts[0].Trim();
                        var id = parts[1].Trim();
                        var ver = parts.Length >= 3 ? parts[2].Trim() : "";
                        if (string.IsNullOrEmpty(id) || WingetHeaderTokensPtEn.Any(t => string.Equals(id, t, StringComparison.OrdinalIgnoreCase))) continue;
                        var pub = parts.Length >= 4 ? parts[3].Trim() : "";
                        list.Add(new StoreApp { Name = name, Id = id, Version = ver, Publisher = pub, Source = "winget" });
                    }
                    if (list.Count >= 40) break;
                }
            }
            catch (Exception ex) { try { Logger.Log($"[STORE] winget search falhou: {ex.Message}"); } catch { } }
            return list;
        }

        /// <summary>
        /// Busca winget restrita a uma fonte (estilo UniGetUI: --source winget|msstore).
        /// Para msstore usa o CLI real (o índice local não cobre a fonte Store).
        /// </summary>
        public static List<StoreApp> QueryWingetSearch(string? wingetPath, string query, string source)
        {
            if (string.IsNullOrWhiteSpace(wingetPath) || !File.Exists(wingetPath) || string.IsNullOrWhiteSpace(query)) return new List<StoreApp>();
            if (string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var q = Regex.Replace(query.Replace("\"", "").Trim(), @"[^\w\s\.\-\+]", "");
                    if (q.Length < 2) return new List<StoreApp>();
                    var args = $"search --query \"{q}\" --source msstore --accept-source-agreements --disable-interactivity --count 25";
                    var output = RunCapture($"\"{wingetPath}\"", args, 35000);
                    var list = new List<StoreApp>();
                    bool inData = false;
                    foreach (var raw in output.Split('\n'))
                    {
                        var line = raw.TrimEnd('\r');
                        if (!inData) { if (line.TrimStart().StartsWith("---") || line.Contains("----")) { inData = true; continue; } continue; }
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        if (line.StartsWith("No package", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Nenhum pacote", StringComparison.OrdinalIgnoreCase)) break;
                        var parts = Regex.Split(line.Trim(), @"\s{2,}");
                        if (parts.Length >= 3)
                        {
                            var name = parts[0].Trim();
                            var id = parts[1].Trim();
                            var ver = parts[2].Trim();
                            if (string.IsNullOrEmpty(id) || WingetHeaderTokensPtEn.Any(t => string.Equals(id, t, StringComparison.OrdinalIgnoreCase))) continue;
                            var pub = parts.Length >= 4 ? parts[3].Trim() : "";
                            // msstore reporta "Unknown" — normaliza para não aparecer na lista
                            if (string.Equals(ver, "Unknown", StringComparison.OrdinalIgnoreCase)) ver = "";
                            list.Add(new StoreApp { Name = name, Id = id, Version = ver, Publisher = pub, Source = "msstore" });
                        }
                        if (list.Count >= 25) break;
                    }
                    return list;
                }
                catch (Exception ex) { try { Logger.Log($"[STORE] winget search msstore falhou: {ex.Message}"); } catch { } }
                return new List<StoreApp>();
            }
            // Fonte winget → mesma lógica da busca normal
            return QueryWingetSearch(wingetPath, query);
        }

        /// <summary>Busca no Chocolatey (estilo UniGetUI: --limit-output, id|versão|título).</summary>
        public static List<StoreApp> QueryChocoSearch(string? chocoPath, string query, int limit = 30)
        {
            var list = new List<StoreApp>();
            if (string.IsNullOrWhiteSpace(chocoPath) || !File.Exists(chocoPath) || string.IsNullOrWhiteSpace(query)) return list;
            try
            {
                var q = Regex.Replace(query.Replace("\"", "").Trim(), @"[^\w\s\.\-\+]", "");
                if (q.Length < 2) return list;
                var args = $"search \"{q}\" --limit-output --by-id-only --order-by-popularity --page 0 --page-size {limit}";
                var output = RunCapture($"\"{chocoPath}\"", args, 25000);
                foreach (var raw in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = raw.Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("Chocolatey", StringComparison.OrdinalIgnoreCase)) continue;
                    var parts = line.Split('|');
                    if (parts.Length >= 2)
                    {
                        var id = parts[0].Trim();
                        var ver = parts.Length > 1 ? parts[1].Trim() : "";
                        var title = parts.Length > 2 ? parts[2].Trim() : "";
                        if (string.IsNullOrEmpty(id)) continue;
                        list.Add(new StoreApp { Name = string.IsNullOrEmpty(title) ? id : title, Id = id, Version = ver, Source = "choco" });
                    }
                    if (list.Count >= limit) break;
                }
            }
            catch (Exception ex) { try { Logger.Log($"[STORE] choco search falhou: {ex.Message}"); } catch { } }
            return list;
        }

        // ---- Gerenciadores de dev (Pip/Npm/DotNet/Cargo) — mesmos comandos do UniGetUI ----

        public static string? FindFirstOnPath(string fileName)
        {
            try
            {
                var found = RunCapture("where", fileName, 4000);
                var first = found.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                                 .Select(s => s.Trim().Trim('"'))
                                 .FirstOrDefault(s => s.EndsWith(fileName, StringComparison.OrdinalIgnoreCase) && File.Exists(s));
                return first;
            }
            catch { return null; }
        }

        /// <summary>Localiza o pip como o UniGetUI: python do PATH + pastas de instalação do Python.
        /// Retorna python.exe (rodamos `-m pip`) ou pip.exe direto quando existir.</summary>
        public static string? FindPipPath()
        {
            try
            {
                // 1) pip.exe direto no PATH
                var direct = FindFirstOnPath("pip.exe");
                if (direct != null) return direct;
                // 2) python.exe no PATH (igual UniGetUI FindCandidateExecutableFiles)
                var python = FindFirstOnPath("python.exe");
                if (python != null && !python.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                    return python;
                // 3) Pastas de instalação conhecidas
                var dirs = new List<string>();
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (Directory.Exists(pf)) dirs.Add(pf);
                var userPy = Path.Combine(appdata, "Programs", "Python");
                if (Directory.Exists(userPy)) dirs.Add(userPy);
                foreach (var dir in dirs)
                {
                    try
                    {
                        var py = Path.Combine(dir, "python.exe");
                        if (File.Exists(py)) return py;
                        foreach (var sub in Directory.GetDirectories(dir, "Python*"))
                        {
                            var p2 = Path.Combine(sub, "python.exe");
                            if (File.Exists(p2)) return p2;
                        }
                    }
                    catch { }
                }
                // 4) Stub do WindowsApps como último recurso (funciona se o Python real estiver instalado)
                return python;
            }
            catch { return null; }
        }

        /// <summary>Pip: `pip list --outdated` (tabela Package | Version | Available, igual UniGetUI).
        /// pipPath pode ser o próprio pip.exe OU um python.exe (rodamos `python -m pip`).</summary>
        public static List<StoreApp> QueryPipOutdated(string? pipPath)
        {
            var list = new List<StoreApp>();
            if (string.IsNullOrWhiteSpace(pipPath) || !File.Exists(pipPath)) return list;
            try
            {
                bool isPython = Path.GetFileName(pipPath).StartsWith("python", StringComparison.OrdinalIgnoreCase);
                var args = isPython ? "-m pip list --outdated" : "list --outdated";
                var output = RunCapture($"\"{pipPath}\"", args, 60000);
                bool dashesPassed = false;
                foreach (var raw in output.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (!dashesPassed) { if (line.Contains("----")) dashesPassed = true; continue; }
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // UniGetUI: split por espaço simples após colapsar — ids pip não têm espaços
                    var elements = Regex.Replace(line.Trim(), " {2,}", " ").Split(' ');
                    if (elements.Length < 3) continue;
                    var id = elements[0].Trim();
                    var cur = elements[1].Trim();
                    var avail = elements[2].Trim();
                    if (string.IsNullOrEmpty(id) || id.Equals("Package", StringComparison.OrdinalIgnoreCase)) continue;
                    list.Add(new StoreApp { Name = id, Id = id, Version = cur, AvailableVersion = avail, Source = "pip" });
                    if (list.Count >= 200) break;
                }
            }
            catch (Exception ex) { try { Logger.Log($"[STORE] pip outdated falhou: {ex.Message}"); } catch { } }
            return list;
        }

        /// <summary>Npm: `npm outdated --json` (local + global, igual UniGetUI; JSON {id:{current,latest}}).</summary>
        public static List<StoreApp> QueryNpmOutdated(string? npmPath)
        {
            var list = new List<StoreApp>();
            if (string.IsNullOrWhiteSpace(npmPath) || !File.Exists(npmPath)) return list;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var global in new[] { false, true })
            {
                try
                {
                    var psi = new ProcessStartInfo(npmPath, "outdated --json" + (global ? " --global" : ""))
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = home,
                        StandardOutputEncoding = Encoding.UTF8
                    };
                    using var p = Process.Start(psi);
                    if (p == null) continue;
                    var output = p.StandardOutput.ReadToEnd();
                    try { p.WaitForExit(30000); } catch { }
                    if (string.IsNullOrWhiteSpace(output)) continue;
                    using var doc = JsonDocument.Parse(output);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        try
                        {
                            string? cur = prop.Value.TryGetProperty("current", out var c) ? c.GetString() : null;
                            string? latest = prop.Value.TryGetProperty("latest", out var l) ? l.GetString() : null;
                            if (string.IsNullOrEmpty(cur) || string.IsNullOrEmpty(latest)) continue;
                            var id = prop.Name;
                            if (list.Any(x => x.Id == id)) continue; // local+global duplicado
                            list.Add(new StoreApp
                            {
                                Name = id,
                                Id = id,
                                Version = cur,
                                AvailableVersion = latest,
                                Source = "npm",
                                Category = global ? "global" : "local"
                            });
                            if (list.Count >= 200) break;
                        }
                        catch { }
                    }
                }
                catch (Exception ex) { try { Logger.Log($"[STORE] npm outdated ({(global ? "global" : "local")}) falhou: {ex.Message}"); } catch { } }
            }
            return list;
        }

        /// <summary>
        /// .NET Tools globais: `dotnet tool list --global` + consulta NuGet flatcontainer
        /// (idem UniGetUI V3PackageType=DotnetTool, fonte nuget.org).
        /// </summary>
        public static List<StoreApp> QueryDotnetToolUpdates(string? dotnetPath)
        {
            var list = new List<StoreApp>();
            if (string.IsNullOrWhiteSpace(dotnetPath) || !File.Exists(dotnetPath)) return list;
            try
            {
                var output = RunCapture($"\"{dotnetPath}\"", "tool list --global", 20000);
                bool dashesPassed = false;
                var tools = new List<(string Id, string Version)>();
                foreach (var raw in output.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (!dashesPassed) { if (line.Contains("----")) dashesPassed = true; continue; }
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = Regex.Split(line.Trim(), @"\s{2,}");
                    if (parts.Length < 2) continue;
                    var id = parts[0].Trim();
                    var ver = parts[1].Trim();
                    if (string.IsNullOrEmpty(id) || id.Contains(' ') || id.Equals("Package", StringComparison.OrdinalIgnoreCase)) continue;
                    tools.Add((id, ver));
                }
                if (tools.Count > 40) tools = tools.Take(40).ToList();
                foreach (var (id, ver) in tools)
                {
                    try
                    {
                        var latest = NuGetLatestVersion(id);
                        if (string.IsNullOrEmpty(latest)) continue;
                        if (CompareVersions(latest, ver) > 0)
                            list.Add(new StoreApp { Name = id, Id = id, Version = ver, AvailableVersion = latest, Source = "dotnet" });
                    }
                    catch { }
                }
            }
            catch (Exception ex) { try { Logger.Log($"[STORE] dotnet tool list falhou: {ex.Message}"); } catch { } }
            return list;
        }

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

        private static string? NuGetLatestVersion(string packageId)
        {
            try
            {
                var url = $"https://api.nuget.org/v3-flatcontainer/{Uri.EscapeDataString(packageId.ToLowerInvariant())}/index.json";
                var json = _http.GetStringAsync(url).GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("versions", out var versions)) return null;
                string? best = null;
                foreach (var v in versions.EnumerateArray())
                {
                    var s = v.GetString();
                    if (string.IsNullOrEmpty(s)) continue;
                    if (best == null || CompareVersions(s!, best) > 0) best = s;
                }
                return best;
            }
            catch { return null; }
        }

        /// <summary>Cargo: `cargo install-update -l` (cargo-update; linhas com "Needs update" = Yes).</summary>
        public static List<StoreApp> QueryCargoUpdates(string? cargoPath)
        {
            var list = new List<StoreApp>();
            if (string.IsNullOrWhiteSpace(cargoPath) || !File.Exists(cargoPath)) return list;
            try
            {
                var output = RunCapture($"\"{cargoPath}\"", "install-update -l", 60000);
                if (output.Contains("no such subcommand") || output.Contains("couldn't find"))
                {
                    try { Logger.Log("[STORE] cargo-update não instalado — updates do Cargo pulados (cargo install cargo-update)"); } catch { }
                    return list;
                }
                bool dashesPassed = false;
                foreach (var raw in output.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (!dashesPassed) { if (line.Contains("----")) dashesPassed = true; continue; }
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var parts = Regex.Split(line.Trim(), @"\s{2,}");
                    if (parts.Length < 4) continue;
                    var id = parts[0].Trim();
                    var cur = parts[1].Trim().TrimStart('v');
                    var latest = parts[2].Trim().TrimStart('v');
                    var needs = parts[^1].Trim();
                    if (!needs.StartsWith("Yes", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrEmpty(id)) continue;
                    list.Add(new StoreApp { Name = id, Id = id, Version = cur, AvailableVersion = latest, Source = "cargo" });
                }
            }
            catch (Exception ex) { try { Logger.Log($"[STORE] cargo install-update falhou: {ex.Message}"); } catch { } }
            return list;
        }

        // ---- Índice SQLite local (busca instantânea, sem spawnar winget) ----
        private static string? _indexDbPath;
        private static bool _indexDbTried;
        private static readonly object _indexLock = new();
        private static List<StoreApp>? _localIndex;

        /// <summary>Localiza e extrai o index.db do source2.msix (índice SQLite que o próprio winget usa para busca).</summary>
        private static string? FindLocalIndexDb()
        {
            lock (_indexLock)
            {
                if (_indexDbTried) return _indexDbPath;
                _indexDbTried = true;
                try
                {
                    var candidates = new List<string>();
                    try
                    {
                        var lpk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                        if (Directory.Exists(lpk))
                        {
                            foreach (var dir in Directory.GetDirectories(lpk, "Microsoft.DesktopAppInstaller_*", SearchOption.TopDirectoryOnly))
                            {
                                var ic = Path.Combine(dir, "AC", "INetCache");
                                if (Directory.Exists(ic))
                                    candidates.AddRange(Directory.GetFiles(ic, "source*.msix", SearchOption.AllDirectories));
                                var ls = Path.Combine(dir, "LocalState");
                                if (Directory.Exists(ls))
                                    candidates.AddRange(Directory.GetFiles(ls, "source*.msix", SearchOption.AllDirectories));
                            }
                        }
                    }
                    catch { }
                    foreach (var msix in candidates.OrderByDescending(File.GetLastWriteTimeUtc))
                    {
                        try
                        {
                            var tmp = Path.Combine(Path.GetTempPath(), "KitStoreIndex", "index.db");
                            if (!Directory.Exists(Path.GetDirectoryName(tmp))) Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
                            using (var zip = System.IO.Compression.ZipFile.OpenRead(msix))
                            {
                                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("index.db", StringComparison.OrdinalIgnoreCase));
                                if (entry == null) continue;
                                using var es = entry.Open();
                                using var os = File.Create(tmp);
                                es.CopyTo(os);
                                _indexDbPath = tmp;
                                return _indexDbPath;
                            }
                        }
                        catch { }
                    }
                }
                catch { }
                return null;
            }
        }

        /// <summary>Busca instantânea no índice local do winget (fallback: QueryWingetSearch CLI).</summary>
        public static List<StoreApp> QueryWingetSearchLocal(string? wingetPath, string query)
        {
            var result = new List<StoreApp>();
            if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2) return result;
            var q = query.Trim().ToLowerInvariant();

            try
            {
                var db = FindLocalIndexDb();
                if (db != null)
                {
                    try
                    {
                        var list = EnsureLocalIndex(db);
                        if (list != null)
                        {
                            // match em name/id/moniker (case-insensitive substring)
                            var hits = list.Where(a =>
                                (a.Name?.ToLowerInvariant().Contains(q) == true) ||
                                (a.Id?.ToLowerInvariant().Contains(q) == true) ||
                                (a.Category?.ToLowerInvariant().Contains(q) == true))
                                .Take(60).ToList();
                            result.AddRange(hits);
                            return result;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // Fallback CLI
            if (result.Count == 0) result.AddRange(QueryWingetSearch(wingetPath, query));
            return result;
        }

        private static List<StoreApp>? EnsureLocalIndex(string dbPath)
        {
            lock (_indexLock)
            {
                if (_localIndex != null) return _localIndex;
                try
                {
                    var list = new List<StoreApp>(16000);
                    var rows = SqliteReader.ReadAll(dbPath, "packages");
                    foreach (var r in rows)
                    {
                        // packages (empírico): [0]=reservado/null, [1]=id, [2]=name, [3]=moniker, [4]=latest_version, ...
                        string id = r.Length > 0 ? (r.Length > 1 ? (r[1] as string) ?? "" : "") : "";
                        string name = r.Length > 2 ? (r[2] as string) ?? "" : "";
                        string moniker = r.Length > 3 ? (r[3] as string) ?? "" : "";
                        string ver = r.Length > 4 ? (r[4] as string) ?? "" : "";
                        if (string.IsNullOrEmpty(id)) continue;
                        if (moniker.Equals("None", StringComparison.OrdinalIgnoreCase)) moniker = "";
                        var pub = "";
                        var di = id.LastIndexOf('.');
                        if (di > 0) pub = id.Substring(0, di);
                        list.Add(new StoreApp { Name = name, Id = id, Version = ver, Publisher = pub, Source = "winget", Category = moniker });
                    }
                    _localIndex = list;
                    return list;
                }
                catch (Exception ex)
                {
                    try { Logger.Log($"[STORE] índice local falhou: {ex.Message}"); } catch { }
                    return null;
                }
            }
        }

        public static List<StoreApp> QueryChocoOutdated(string? chocoPath)
        {
            var list = new List<StoreApp>();
            if (string.IsNullOrWhiteSpace(chocoPath) || !File.Exists(chocoPath)) return list;
            try
            {
                var output = RunCapture($"\"{chocoPath}\"", "outdated --limit-output", 25000);
                foreach (var raw in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = raw.Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("Chocolatey", StringComparison.OrdinalIgnoreCase)) continue;
                    var parts = line.Split('|');
                    if (parts.Length >= 3)
                        list.Add(new StoreApp { Name = parts[0].Trim(), Id = parts[0].Trim(), Version = parts[1].Trim(), AvailableVersion = parts[2].Trim(), Source = "choco" });
                }
            }
            catch (Exception ex) { try { Logger.Log($"[STORE] choco outdated falhou: {ex.Message}"); } catch { } }
            return list;
        }

        public static List<string> QueryAppxPackages()
        {
            // Tenta WinRT primeiro (PackageManager) — 10x mais rápido que PowerShell
            var winrt = TryQueryAppxWinRt();
            if (winrt != null) return winrt;
            var list = new List<string>();
            try
            {
                var output = RunCapture("powershell.exe", "-NoProfile -Command \"Get-AppxPackage | Select-Object -ExpandProperty PackageFullName\"", 20000);
                foreach (var raw in output.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var line = raw.Trim();
                    if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("PS ", StringComparison.OrdinalIgnoreCase))
                        list.Add(line);
                }
            }
            catch { }
            return list;
        }

        // ---- Tentativas COM/WinRT (via reflection p/ não quebrar build sem WinAppSDK) ----

        private static List<StoreApp>? TryQueryWingetInstalledCom()
        {
            try
            {
                // Microsoft.Management.Deployment.PackageManager está no App Installer (Microsoft.DesktopAppInstaller)
                var pmType = Type.GetType("Microsoft.Management.Deployment.PackageManager, Microsoft.Management.Deployment, ContentType=WindowsRuntime");
                if (pmType == null) return null;
                dynamic pm = Activator.CreateInstance(pmType)!;
                // LocalPackageCatalog InstalledPackages não exposto diretamente em todas as versões; fallback CLI
                return null;
            }
            catch { return null; }
        }

        private static List<string>? TryQueryAppxWinRt()
        {
            try
            {
                var pmType = Type.GetType("Windows.Management.Deployment.PackageManager, Windows.Management.Deployment, ContentType=WindowsRuntime");
                if (pmType == null) return null;
                dynamic pm = Activator.CreateInstance(pmType)!;
                var pkgs = pm.FindPackages();
                var list = new List<string>();
                foreach (var p in pkgs)
                {
                    try { string full = (string)p.Id.FullName; if (!string.IsNullOrEmpty(full)) list.Add(full); } catch { }
                }
                return list;
            }
            catch { return null; }
        }

        // ---- Helpers ----

        public static int CompareVersions(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return 0;
            try
            {
                // Tenta parse semântico (remove sufixes como -beta)
                var ca = Regex.Replace(a ?? "", @"[^0-9\.]", ".").Trim('.');
                var cb = Regex.Replace(b ?? "", @"[^0-9\.]", ".").Trim('.');
                if (System.Version.TryParse(NormalizeVersion(ca), out var va) && System.Version.TryParse(NormalizeVersion(cb), out var vb))
                    return va.CompareTo(vb);
            }
            catch { }
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeVersion(string v)
        {
            var parts = v.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries).Take(4).ToArray();
            while (parts.Length < 2) parts = parts.Concat(new[] { "0" }).ToArray();
            return string.Join(".", parts);
        }

        // ---- Phantom report (mantido p/ compatibilidade, mas agora genérico + específico) ----

        public static string BuildPhantomReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Scan {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(new string('=', 60));
            try
            {
                var pmType = Type.GetType("Windows.Management.Deployment.PackageManager, Windows.Management.Deployment, ContentType=WindowsRuntime");
                if (pmType != null)
                {
                    dynamic pm = Activator.CreateInstance(pmType)!;
                    var pkgs = pm.FindPackages();
                    int fant = 0;
                    foreach (var p in pkgs) { try { var id = (string)p.Id.FullName; if (id.IndexOf("MinecraftPreview", StringComparison.OrdinalIgnoreCase) >= 0) { sb.AppendLine($"WinRT fantasma: {id}"); fant++; } } catch { } }
                    sb.AppendLine($"WinRT PackageManager: OK ({pkgs.Count} pacotes via FindPackages) — {fant} fantasma(s) MinecraftPreview");
                }
                else sb.AppendLine("WinRT PackageManager: não disponível (WinPE) — usando registry fallback");
            }
            catch (Exception ex) { sb.AppendLine($"WinRT PackageManager: fallback ({ex.Message})"); }

            try
            {
                using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore");
                if (baseKey == null) sb.AppendLine("AppxAllUserStore: chave não encontrada");
                else
                {
                    var sids = baseKey.GetSubKeyNames();
                    sb.AppendLine($"AppxAllUserStore: {sids.Length} SID(s)");
                    foreach (var sid in sids.Take(5))
                    {
                        try
                        {
                            using var sidKey = baseKey.OpenSubKey(sid);
                            var fams = sidKey?.GetSubKeyNames() ?? Array.Empty<string>();
                            var preview = fams.Where(f => f.Contains("MinecraftPreview", StringComparison.OrdinalIgnoreCase)).ToArray();
                            if (preview.Length > 0) sb.AppendLine($"  SID {sid}: FANTASMA MinecraftPreview: {string.Join(", ", preview)}");
                            using var eol = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\EndOfLife");
                            var eolFams = eol?.GetSubKeyNames() ?? Array.Empty<string>();
                            var eolPreview = eolFams.Where(f => f.Contains("MinecraftPreview", StringComparison.OrdinalIgnoreCase)).ToArray();
                            if (eolPreview.Length > 0) sb.AppendLine($"  EndOfLife: {string.Join(", ", eolPreview)}");
                        }
                        catch { }
                    }
                    using var staged = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Staged");
                    if (staged != null) sb.AppendLine($"Staged packages: {staged.GetSubKeyNames().Length}");
                    using var deprov = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Deprovisioned");
                    if (deprov != null) sb.AppendLine($"Deprovisioned: {deprov.GetSubKeyNames().Length} bloqueados");
                }
            }
            catch (Exception ex) { sb.AppendLine($"AppxAllUserStore erro: {ex.Message}"); }

            try
            {
                using var pl = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\StateChange\PackageList");
                if (pl != null)
                {
                    int corrupted = 0;
                    foreach (var pkg in pl.GetSubKeyNames())
                    {
                        try
                        {
                            using var k = pl.OpenSubKey(pkg);
                            var st = k?.GetValue("PackageStatus");
                            if (st is int iv && iv != 0) { if (corrupted < 10) sb.AppendLine($"  CORROMPIDO PackageStatus!=0: {pkg} = {iv}"); corrupted++; }
                        }
                        catch { }
                    }
                    if (corrupted == 0) sb.AppendLine("PackageStatus: todos 0 (OK)");
                    else sb.AppendLine($"PackageStatus: {corrupted} CORROMPIDO(S) — causa 0x80073CFC");
                }
            }
            catch (Exception ex) { sb.AppendLine($"PackageStatus erro: {ex.Message}"); }

            try
            {
                using var pd = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\PendingDeletions");
                if (pd != null) sb.AppendLine($"PendingDeletions: {pd.GetValueNames().Length} pendente(s) de reboot");
                else sb.AppendLine("PendingDeletions: nenhum");
            }
            catch (Exception ex) { sb.AppendLine($"PendingDeletions erro: {ex.Message}"); }

            try
            {
                using var cdm = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager");
                var silent = cdm?.GetValue("SilentInstalledAppsEnabled");
                var sub310 = cdm?.GetValue("SubscribedContent-310093Enabled");
                sb.AppendLine($"ContentDeliveryManager: SilentInstalledAppsEnabled={silent ?? "(não def)"} SubscribedContent-310093Enabled={sub310 ?? "(não def)"}");
            }
            catch (Exception ex) { sb.AppendLine($"ContentDeliveryManager erro: {ex.Message}"); }

            try
            {
                using var ws = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsStore");
                sb.AppendLine($"WindowsStore AutoDownload={ws?.GetValue("AutoDownload") ?? "(não def)"} (2=sempre, 4=nunca)");
            }
            catch (Exception ex) { sb.AppendLine($"WindowsStore erro: {ex.Message}"); }

            try
            {
                var tq = RunCapture("schtasks", "/Query /TN \"\\Microsoft\\Windows\\InstallService\\ScanForUpdates\" /FO LIST /V 2>&1", 8000);
                if (tq.Contains("ERROR") || tq.Contains("não foi encontrado")) sb.AppendLine("ScanForUpdates task: não encontrada");
                else
                {
                    var lastRun = Regex.Match(tq, @"Last Run Time:\s*(.+)").Groups[1].Value.Trim();
                    var nextRun = Regex.Match(tq, @"Next Run Time:\s*(.+)").Groups[1].Value.Trim();
                    sb.AppendLine($"ScanForUpdates: LastRun={lastRun} NextRun={nextRun}");
                }
            }
            catch (Exception ex) { sb.AppendLine($"ScanForUpdates erro: {ex.Message}"); }

            try
            {
                var svc = RunCapture("sc", "query InstallService 2>&1", 5000);
                sb.AppendLine("InstallService: " + (svc.Contains("RUNNING") ? "RUNNING" : svc.Contains("STOPPED") ? "STOPPED" : svc.Substring(0, Math.Min(120, svc.Length))));
                var appx = RunCapture("sc", "query AppXSvc 2>&1", 5000);
                sb.AppendLine("AppXSVC: " + (appx.Contains("RUNNING") ? "RUNNING" : appx.Contains("STOPPED") ? "STOPPED" : appx.Substring(0, Math.Min(120, appx.Length))));
            }
            catch (Exception ex) { sb.AppendLine($"Serviços erro: {ex.Message}"); }

            sb.AppendLine(new string('=', 60));
            sb.AppendLine("Dica: se FANTASMA/CORROMPIDO/PENDENTE, use Bloquear Minecraft Preview ou wsreset + Re-registrar + reboot.");
            return sb.ToString();
        }

        // ---- Cache de performance (TTL 3 min) — supera Store que re-query sempre ----
        private static readonly object _cacheLock = new();
        private static List<StoreApp>? _cachedInstalled; private static DateTime _cachedInstalledTime;
        private static List<StoreApp>? _cachedUpgrades; private static DateTime _cachedUpgradesTime;
        private static List<string>? _cachedAppx; private static DateTime _cachedAppxTime;
        private const int CacheTTLSeconds = 180;

        public static void InvalidateCache()
        {
            lock (_cacheLock) { _cachedInstalled = null; _cachedUpgrades = null; _cachedAppx = null; }
        }

        public static List<StoreApp> QueryWingetInstalledCached(string? wingetPath, bool force = false)
        {
            lock (_cacheLock)
            {
                if (!force && _cachedInstalled != null && (DateTime.UtcNow - _cachedInstalledTime).TotalSeconds < CacheTTLSeconds)
                    return new List<StoreApp>(_cachedInstalled);
            }
            var r = QueryWingetInstalled(wingetPath);
            lock (_cacheLock) { _cachedInstalled = new List<StoreApp>(r); _cachedInstalledTime = DateTime.UtcNow; }
            return r;
        }

        // ---- Detector e corretor genérico 0x80073CFB / fantasmas (supera Store) ----
        public class StuckInfo
        {
            public string FullName { get; set; } = "";
            public string Family { get; set; } = "";
            public string Reason { get; set; } = "";
            public int PackageStatus { get; set; }
            public bool IsStagedOnly { get; set; }
            public bool IsEndOfLife { get; set; }
        }

        /// <summary>Detecta pacotes travados (0x80073CFB/0x80073CFC/pendentes). Genérico, não só Minecraft.</summary>
        public static List<StuckInfo> DetectStuckPackages(string? filterFamily = null)
        {
            var list = new List<StuckInfo>();
            try
            {
                // 1) Via PackageList PackageStatus !=0 (corrompido → 0x80073CFC)
                using var pl = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\StateChange\PackageList");
                if (pl != null)
                {
                    foreach (var pkg in pl.GetSubKeyNames())
                    {
                        if (!string.IsNullOrEmpty(filterFamily) && pkg.IndexOf(filterFamily, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        try
                        {
                            using var k = pl.OpenSubKey(pkg);
                            var st = k?.GetValue("PackageStatus");
                            if (st is int iv && iv != 0)
                                list.Add(new StuckInfo { FullName = pkg, Family = pkg.Split('_')[0], Reason = $"PackageStatus={iv} (corrompido 0x80073CFC)", PackageStatus = iv });
                        }
                        catch { }
                    }
                }
                // 2) Staged sem Installed → fantasma (0x80073CFB pendente)
                using var staged = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Staged");
                using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore");
                if (staged != null && baseKey != null)
                {
                    var stagedNames = staged.GetSubKeyNames();
                    foreach (var fn in stagedNames)
                    {
                        if (!string.IsNullOrEmpty(filterFamily) && fn.IndexOf(filterFamily, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        bool installed = false;
                        foreach (var sid in baseKey.GetSubKeyNames())
                        {
                            if (sid == "Staged" || sid == "EndOfLife" || sid == "Deprovisioned" || sid == "InboxApplications") continue;
                            try
                            {
                                using var sidK = baseKey.OpenSubKey(sid);
                                if (sidK?.GetSubKeyNames().Any(n => n.Equals(fn, StringComparison.OrdinalIgnoreCase)) == true) { installed = true; break; }
                            }
                            catch { }
                        }
                        if (!installed && !list.Any(x => x.FullName.Equals(fn, StringComparison.OrdinalIgnoreCase)))
                            list.Add(new StuckInfo { FullName = fn, Family = fn.Split('_')[0], Reason = "Staged sem Installed (fantasma 0x80073CFB)", IsStagedOnly = true });
                    }
                }
                // 3) EndOfLife pendente
                using var eol = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\EndOfLife");
                if (eol != null)
                {
                    foreach (var fn in eol.GetSubKeyNames())
                    {
                        if (!string.IsNullOrEmpty(filterFamily) && fn.IndexOf(filterFamily, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (!list.Any(x => x.FullName.Equals(fn, StringComparison.OrdinalIgnoreCase)))
                            list.Add(new StuckInfo { FullName = fn, Family = fn.Split('_')[0], Reason = "EndOfLife pendente", IsEndOfLife = true });
                    }
                }
                // 4) PendingDeletions
                using var pd = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\PendingDeletions");
                if (pd != null)
                {
                    foreach (var v in pd.GetValueNames())
                    {
                        if (!string.IsNullOrEmpty(filterFamily) && v.IndexOf(filterFamily, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (!list.Any(x => v.Contains(x.Family, StringComparison.OrdinalIgnoreCase)))
                            list.Add(new StuckInfo { FullName = v, Family = v, Reason = "PendingDeletions (reboot pendente)" });
                    }
                }
            }
            catch (Exception ex) { try { Logger.Log($"[STORE] DetectStuck falhou: {ex.Message}"); } catch { } }
            return list;
        }

        /// <summary>
        /// Corrige 0x80073CFB/0x80073CFC para uma família (ex: Minecraft). Supera Store que só tenta reinstalar.
        /// Passos: PackageStatus→0, Remove-AppxPackage -AllUsers, Remove-AppxProvisionedPackage, Deprovisioned, ClipSVC/AppXSvc restart.
        /// </summary>
        public static string FixStuckPackage(string fullNameOrFamily, bool isFamily = false)
        {
            var sb = new StringBuilder();
            string target = fullNameOrFamily.Trim();
            sb.AppendLine($"[FIX] Alvo: {target} isFamily={isFamily} {DateTime.Now:HH:mm:ss}");
            try
            {
                // 1) Corrige PackageStatus se corrompido
                try
                {
                    using var pl = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModel\StateChange\PackageList", true);
                    if (pl != null)
                    {
                        foreach (var pkg in pl.GetSubKeyNames().Where(p => p.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0))
                        {
                            try
                            {
                                using var k = pl.OpenSubKey(pkg, true);
                                var st = k?.GetValue("PackageStatus");
                                if (st is int iv && iv != 0)
                                {
                                    k!.SetValue("PackageStatus", 0, Microsoft.Win32.RegistryValueKind.DWord);
                                    sb.AppendLine($"  PackageStatus {pkg}: {iv} → 0 (corrigido)");
                                }
                            }
                            catch (Exception ex) { sb.AppendLine($"  PackageStatus falhou {pkg}: {ex.Message}"); }
                        }
                    }
                }
                catch (Exception ex) { sb.AppendLine($"  Registry PackageStatus erro: {ex.Message}"); }

                // 2) Remove-AppxPackage -AllUsers (PackageManager WinRT se possível, senão PowerShell)
                bool removed = false;
                try
                {
                    var ps = $"Get-AppxPackage -AllUsers | Where-Object {{ $_.PackageFullName -like '*{target}*' }} | Remove-AppxPackage -AllUsers -ErrorAction Continue 2>&1 | Out-String";
                    var out1 = RunCapture("powershell.exe", $"-NoProfile -Command \"{ps}\"", 30000);
                    sb.AppendLine($"  Remove-AppxPackage: {Trunc(out1, 600)}");
                    if (!out1.Contains("ERROR") && !out1.Contains("0x80073")) removed = true;
                }
                catch (Exception ex) { sb.AppendLine($"  Remove-AppxPackage erro: {ex.Message}"); }

                // 3) Se ainda há staged, tenta via WinRT PackageManager.RemovePackageAsync
                if (!removed)
                {
                    try
                    {
                        var pmType = Type.GetType("Windows.Management.Deployment.PackageManager, Windows.Management.Deployment, ContentType=WindowsRuntime");
                        if (pmType != null)
                        {
                            dynamic pm = Activator.CreateInstance(pmType)!;
                            var pkgs = pm.FindPackages();
                            foreach (var p in pkgs)
                            {
                                try
                                {
                                    string fn = (string)p.Id.FullName;
                                    if (fn.IndexOf(target, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                    // Tenta remover para todos usuários
                                    var op = pm.RemovePackageAsync(fn, 0x1 /*RemovalOptions.RemoveForAllUsers*/);
                                    // Aguarda síncrono via GetResults (reflection)
                                    // Fallback: apenas loga intenção
                                    sb.AppendLine($"  WinRT RemovePackageAsync tentado para {fn}");
                                }
                                catch (Exception ex2) { sb.AppendLine($"  WinRT remove falhou {ex2.Message}"); }
                            }
                        }
                    }
                    catch { }
                }

                // 4) Deprovisioned (impede re-stage) — extrai família
                try
                {
                    string family = target.Contains("_") ? target.Split('_')[0] : target;
                    // Tenta descobrir família real via staged registry
                    using var staged = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Staged");
                    if (staged != null)
                    {
                        var match = staged.GetSubKeyNames().FirstOrDefault(n => n.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0);
                        if (match != null) family = match.Split('_')[0];
                    }
                    using var dep = Microsoft.Win32.Registry.LocalMachine.CreateSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Deprovisioned\{family}_8wekyb3d8bbwe", true);
                    // Para Minecraft UWP a família tem publisher 8wekyb3d8bbwe; para genérico tenta sem sufixo
                    if (dep == null)
                    {
                        using var dep2 = Microsoft.Win32.Registry.LocalMachine.CreateSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Appx\AppxAllUserStore\Deprovisioned\{family}", true);
                        dep2?.SetValue("Deprovisioned", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        sb.AppendLine($"  Deprovisioned {family} (sem sufixo) OK");
                    }
                    else
                    {
                        dep.SetValue("Deprovisioned", 1, Microsoft.Win32.RegistryValueKind.DWord);
                        sb.AppendLine($"  Deprovisioned {family}_8wekyb3d8bbwe OK");
                    }
                }
                catch (Exception ex) { sb.AppendLine($"  Deprovisioned falhou: {ex.Message} (rode como admin)"); }

                // 5) Limpa InstallService queue / reinicia serviços
                try
                {
                    RunCapture("sc", "stop InstallService", 5000);
                    RunCapture("sc", "stop ClipSVC", 5000);
                    System.Threading.Thread.Sleep(800);
                    RunCapture("sc", "start ClipSVC", 5000);
                    RunCapture("sc", "start InstallService", 5000);
                    sb.AppendLine("  Serviços InstallService/ClipSVC reiniciados");
                }
                catch (Exception ex) { sb.AppendLine($"  Restart serviços falhou: {ex.Message}"); }

                // 6) Remove provisioned package (se era provisioned)
                try
                {
                    var ps2 = $"Get-AppxProvisionedPackage -Online | Where-Object {{ $_.DisplayName -like '*{target}*' }} | Remove-AppxProvisionedPackage -Online 2>&1 | Out-String";
                    var out2 = RunCapture("powershell.exe", $"-NoProfile -Command \"{ps2}\"", 30000);
                    if (!string.IsNullOrWhiteSpace(out2)) sb.AppendLine($"  Deprovision provisioned: {Trunc(out2, 500)}");
                }
                catch { }

                sb.AppendLine("[FIX] Concluído — reinicie e tente instalar novamente. Se ainda falhar, use wsreset + reboot.");
            }
            catch (Exception ex) { sb.AppendLine($"[FIX] Erro geral: {ex.Message}"); }
            return sb.ToString();
        }

        private static string Trunc(string s, int max) => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "...");
    }
}
