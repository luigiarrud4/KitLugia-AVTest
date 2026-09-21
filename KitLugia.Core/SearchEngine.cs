using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;

namespace KitLugia.Core
{
    public enum SearchResultType { Navigation, Action, Tweak, Service }

    public class GlobalSearchResult : INotifyPropertyChanged
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string Icon { get; set; } = "🔍";
        public string ButtonText { get; set; } = "ABRIR";
        public SearchResultType Type { get; set; } = SearchResultType.Navigation;
        public Func<(bool Success, string Message)>? ExecuteAction { get; set; }
        public string? NavigationTag { get; set; }
        public int Score { get; set; }

        /// <summary>
        /// Chave no cache de estados do SearchEngine ("guardian:...", "privacy:...",
        /// "alltweak:..."). Null = item sem estado (navegacao/acao).
        /// </summary>
        public string? StateKey { get; set; }

        public string TypeLabel => Type switch
        {
            SearchResultType.Navigation => "PÁGINA",
            SearchResultType.Tweak => "TWEAK",
            SearchResultType.Action => "AÇÃO",
            SearchResultType.Service => "SERVIÇO",
            _ => ""
        };

        public bool IsToggle { get; set; } = false;

        private bool _isActive = false;
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    OnPropertyChanged(nameof(IsActive));
                }
            }
        }

        public Func<bool>? CheckState { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    [SupportedOSPlatform("windows")]
    public static class SearchEngine
    {
        private sealed class Entry
        {
            public GlobalSearchResult Item = new();
            public string TitleNorm = "";
            public string DescNorm = "";
        }

        private static readonly List<Entry> _index = new();
        private static readonly List<Func<IReadOnlyList<GlobalSearchResult>>> _providers = new();
        private static bool _isInitialized = false;
        private static readonly object _dbLock = new();

        // --- Cache de estados (evita 1 scan Guardian por tecla digitada) ---
        private static readonly object _stateLock = new();
        private static Dictionary<string, bool> _stateCache = new();
        private static DateTime _stateTime = DateTime.MinValue;
        private static readonly List<(string Key, Func<bool> Check)> _stateResolvers = new();
        private static bool _refreshing = false;
        public static TimeSpan StateTtl { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Normaliza para busca: minusculas + sem acentos (pt-BR: "otimizacao"
        /// acha "otimização"). Calculado 1x na indexacao, zero alocacao por tecla.
        /// </summary>
        public static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string lower = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(lower.Length);
            foreach (char c in lower)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        /// <summary>
        /// Provedores externos (ex: AllTweaks da GUI) registram itens sem o Core
        /// depender da GUI. Chamado 1x via bootstrap antes do primeiro Search.
        /// </summary>
        public static void RegisterProvider(Func<IReadOnlyList<GlobalSearchResult>> provider)
        {
            lock (_dbLock) _providers.Add(provider);
        }

        /// <summary>
        /// Resolvedores de estado avaliados 1x por refresh do cache (cada um em
        /// try/catch isolado: 1 tweak problemático nunca quebra os outros).
        /// </summary>
        public static void RegisterStates(IEnumerable<(string Key, Func<bool> Check)> resolvers)
        {
            lock (_stateLock)
            {
                foreach (var r in resolvers)
                {
                    if (!_stateResolvers.Any(x => x.Key == r.Key))
                        _stateResolvers.Add(r);
                }
            }
        }

        public static void Initialize()
        {
            if (_isInitialized) return;
            lock (_dbLock)
            {
                if (_isInitialized) return;
                _index.Clear();

                BuildCoreDatabase();

                foreach (var provider in _providers)
                {
                    try
                    {
                        foreach (var item in provider())
                            AddItem(item);
                    }
                    catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
                }

                _isInitialized = true;
            }
        }

        private static void AddItem(GlobalSearchResult item)
        {
            _index.Add(new Entry
            {
                Item = item,
                TitleNorm = Fold(item.Title),
                DescNorm = Fold(item.Description)
            });
        }

        private static void AddNav(string title, string desc, string icon, string tag)
        {
            AddItem(new GlobalSearchResult { Title = title, Description = desc, Icon = icon, ButtonText = "IR PARA", Type = SearchResultType.Navigation, NavigationTag = tag });
        }

        private static void AddAction(string title, string desc, string icon, Func<(bool, string)> action)
        {
            AddItem(new GlobalSearchResult { Title = title, Description = desc, Icon = icon, ButtonText = "EXECUTAR", Type = SearchResultType.Action, ExecuteAction = action });
        }

        private static void AddToggle(string title, string desc, string icon, string stateKey, Func<(bool, string)> action, Func<bool> check)
        {
            var item = new GlobalSearchResult
            {
                Title = title,
                Description = desc,
                Icon = icon,
                IsToggle = true,
                Type = SearchResultType.Tweak,
                StateKey = stateKey,
                ExecuteAction = action,
                CheckState = check
            };
            AddItem(item);
            // Sem RegisterStates aqui: Guardian/privacidade/extras sao cobertos
            // direto pelo refresh do cache. Registrar o proprio check (que chama
            // QueryState) causaria recursao infinita durante o refresh.
            // Providers externos registram seus resolvers explicitamente.
        }

        private static void BuildCoreDatabase()
        {
            // 1. NAVEGAÇÃO (TODAS AS PÁGINAS)
            AddNav("Dashboard", "Visão geral do hardware e sistema.", "🏠", "Dashboard");
            AddNav("Desempenho", "Tweaks e otimizações de desempenho.", "⚡", "Tweaks");
            AddNav("Aplicativos", "Gerenciar apps e programas instalados.", "📱", "Apps");
            AddNav("Armazenamento", "Limpeza de disco e arquivos temporários.", "💿", "Storage");
            AddNav("Gerenciar Discos", "Particionamento e formatação.", "💽", "Partitions");
            AddNav("Rede / DNS", "Configurações de latência e DNS.", "🌐", "Network");
            AddNav("Jogos", "Otimizações gaming e GameBoost.", "🎮", "Games");
            AddNav("Drivers", "Atualização e backup de drivers.", "💾", "Drivers");
            AddNav("Serviços", "Gerenciador de serviços e startup.", "🛡️", "Services");
            AddNav("Reparos AIO", "Ferramentas de reparo do sistema.", "🔧", "Repairs");
            AddNav("Integridade", "Scanner de segurança e vulnerabilidades.", "🧰", "Integrity");
            AddNav("Tela", "Calibragem de cores e resolução.", "🖥️", "Screen");
            AddNav("Ferramentas", "Planos de energia e utilitários.", "🛠️", "Tools");
            AddNav("GameBoost Pro", "Otimização inteligente para jogos.", "🚀", "GameBoost");
            AddNav("Winboot", "Criação de mídia de instalação Windows.", "💻", "Winboot");
            AddNav("Ferramentas Avançadas", "ISO Editor, Winboot, Partições.", "🔨", "AdvancedTools");
            AddNav("ISO Editor", "Editar e personalizar ISOs do Windows.", "📀", "IsoEditor");
            AddNav("Segurança", "Firewall, Defender e proteções.", "🔐", "Security");
            AddNav("Privacidade", "Configurações de privacidade e telemetria.", "🔒", "Privacy");
            AddNav("Ativação", "Status e ativação do Windows.", "🔑", "Activation");
            AddNav("Atualizações", "Verificar e instalar atualizações.", "🔄", "Update");
            AddNav("Windows Update", "Pausar, canal e controle de updates.", "🔄", "WindowsUpdate");
            AddNav("Config. Tray", "Monitor de RAM e bandeja do sistema.", "🔔", "TraySettings");
            AddNav("Diagnóstico", "Depuração e monitoramento interno.", "🔬", "Diagnostic");
            AddNav("Servidor Local", "Túneis e servidor local.", "🌍", "Server");
            AddNav("Mídia Bootável", "Criação de pendrive bootável.", "💿", "Rufus");
            AddNav("Stutter Detector", "Detector de travamentos e micro-stutters (QPC). Diagnóstico de áudio e DPC.", "🪄", "Stutter");
            AddNav("WinTune", "Centenas de otimizações avançadas do Windows (registry e sistema).", "🎯", "WinTune");
            AddNav("Exm Tweaks", "Tweaks experimentais do Windows.", "🧪", "ExmTweaks");
            AddNav("Todos os Tweaks", "Lista unificada de todos os tweaks disponíveis no KitLugia.", "⚙️", "AllTweaks");
            AddNav("Config. RAM Avançada", "Limpeza agressiva de memória e perfis de RAM.", "🧠", "AdvancedRamCleanSettings");
            AddNav("WinPE / Boot", "Preparar WinPE, shrink e boot instalador.", "💿", "WinpeTools");
            AddNav("Reinstalar Preservando", "Fresh install aplicando a imagem direto no disco sem perder dados.", "♻️", "ReinstallPreserve");
            AddNav("Shrink de Partição", "Reduzir partição via WinPE.", "🗜️", "Shrink");
            AddNav("Instalação Rápida", "Instalador rápido de programas.", "📦", "QuickInstall");
            AddNav("Menu de Contexto", "Personalizar menu de botão direito do Explorer.", "📋", "ContextMenu");
            AddNav("Force Stop Unlock", "Destravar e deletar arquivos/pastas em uso.", "🔓", "ForceStopUnlock");
            AddNav("Kit Store", "Loja de aplicativos (janela separada).", "🏪", "StoreRemake");

            // 2. REPAROS (Ações sem estado)
            try
            {
                var repairs = GeneralRepairManager.GetAllRepairs();
                foreach (var repair in repairs)
                {
                    var r = repair;
                    AddItem(new GlobalSearchResult
                    {
                        Title = r.Name,
                        Description = $"Reparo: {r.Description}",
                        Icon = string.IsNullOrEmpty(r.Icon) ? "🔧" : r.Icon,
                        ButtonText = "EXECUTAR",
                        Type = SearchResultType.Action,
                        ExecuteAction = () => { r.Execute?.Invoke(); return (true, "Comando enviado."); }
                    });
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // 3. TWEAKS DE SEGURANÇA (GUARDIAN) — CheckState via cache (1 scan por TTL,
            // nunca 1 scan por item: o lambda antigo fazia GetHarmfulTweaksWithStatus
            // inteiro a cada Invoke).
            try
            {
                var securityTweaks = Guardian.GetAllTweaksDefinition();
                foreach (var tweak in securityTweaks)
                {
                    // Itens de PATH sao consolidados em UM resultado abaixo.
                    if (tweak.IsPathItem) continue;

                    var t = tweak;
                    string key = "guardian:" + t.Name;
                    AddToggle(t.Name, t.Description, "🛡️", key,
                        action: () => { var r = Guardian.ToggleTweak(t); InvalidateStates(); return r; },
                        check: () => QueryState(key));
                }

                AddToggle("PATH do Sistema e do Usuário (Corrigir tudo)",
                    "Remove duplicatas e pastas inexistentes e adiciona programas instalados fora do PATH (winget, git, node, dotnet, npm, 7-Zip, cargo...). Um clique resolve tudo.",
                    "📁", "guardian:PATH",
                    action: () => { var r = Guardian.RepairAllPathsOnce(); InvalidateStates(); return (r.Changed, r.Summary); },
                    check: () => QueryState("guardian:PATH"));
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // 4. PRIVACIDADE (OOShutUp, 160 settings) — estados via cache compartilhado.
            try
            {
                foreach (var setting in OOShutUpManager.GetPrivacySettings())
                {
                    var s = setting;
                    string key = "privacy:" + s.Name;
                    AddToggle(s.Name, $"{s.Description} (Privacidade: {s.Category})", "🔒", key,
                        action: () =>
                        {
                            bool applied = false;
                            try { applied = OOShutUpManager.IsPrivacySettingApplied(s); } catch { }
                            bool ok = applied ? OOShutUpManager.RevertPrivacySetting(s) : OOShutUpManager.ApplyPrivacySetting(s);
                            InvalidateStates();
                            return (ok, ok ? (applied ? "Proteção revertida." : "Proteção aplicada.") : "Falhou.");
                        },
                        check: () => QueryState(key));
                }
            }
            catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

            // 5. BLOATWARE (carregado em background)
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var bloatApps = SystemTweaks.GetBloatwareAppsStatus();
                    lock (_dbLock)
                    {
                        foreach (var app in bloatApps)
                        {
                            AddItem(new GlobalSearchResult
                            {
                                Title = $"Remover {app.DisplayName}",
                                Description = "Desinstalar aplicativo nativo do Windows.",
                                Icon = "🗑️",
                                ButtonText = "REMOVER",
                                Type = SearchResultType.Action,
                                ExecuteAction = () => SystemTweaks.RemoveBloatwareApp(app.PackageName)
                            });
                        }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }
            });

            // 6. TWEAKS ESPECÍFICOS
            AddToggle("Modo Jogo (Game Mode)", "Prioridade de GPU e afinidade.", "🎮", "extra:gamemode",
                action: () => { SystemTweaks.ApplyGamingOptimizations(); InvalidateStates(); return (true, "Aplicado."); },
                check: () => QueryState("extra:gamemode")
            );
            AddToggle("Desativar MPO", "Corrige telas piscando (Multi-Plane Overlay).", "📺", "extra:mpo",
                action: () => { var r = SystemTweaks.ToggleMpo(); InvalidateStates(); return r; },
                check: () => QueryState("extra:mpo")
            );
            AddToggle("Desativar VBS", "Aumenta FPS desativando virtualização.", "⚡", "extra:vbs",
                action: () => { var r = SystemTweaks.ToggleVbs(); InvalidateStates(); return r; },
                check: () => QueryState("extra:vbs")
            );
            AddToggle("Desativar Pesquisa Bing", "Remove sugestões web do Iniciar.", "🔍", "extra:bing",
                action: () => {
                    if (SystemTweaks.IsBingDisabled())
                    { SystemTweaks.RevertRegistryValue(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions"); InvalidateStates(); return (true, "Reativado."); }
                    else { SystemTweaks.ApplyBingTweak(); InvalidateStates(); return (true, "Desativado."); }
                },
                check: () => QueryState("extra:bing")
            );

            AddAction("CompactOS", "Comprime o Windows para economizar espaço.", "🗜️", () => { Toolbox.CompactOS(); return (true, "Iniciado."); });
            AddAction("Limpar Shaders", "Limpa cache de shader da GPU.", "🧹", () => { Toolbox.CleanShaderCaches(); return (true, "Limpo."); });
            AddAction("Flush DNS", "Limpa cache de resolução DNS.", "🚿", () => Toolbox.FlushDnsCache());
            AddAction("Resetar Windows Update", "Corrige erro 0x800 de atualização.", "🔄", () => { var r = Toolbox.ResetWindowsUpdateComponents(); return (r.Success, "Resetado."); });
            AddAction("Limpeza Total", "Limpa temporários, cache e lixeira.", "🧹", () => { Toolbox.RunFullCleanup(); return (true, "Limpeza concluída."); });
            AddAction("Reparar Imagem (DISM)", "Corrige corrupção do sistema.", "🚑", () => { var r = System.Threading.Tasks.Task.Run(() => SystemRepair.RunDismRestoreHealthAsync()).GetAwaiter().GetResult(); return (r.Success, r.Output); });
            AddAction("Verificar Arquivos (SFC)", "Escaneia arquivos protegidos.", "⚕️", () => { var r = System.Threading.Tasks.Task.Run(() => SystemRepair.RunSfcScanNowAsync()).GetAwaiter().GetResult(); return (r.Success, r.Output); });
        }

        /// <summary>
        /// Devolve os estados com TTL: 1 scan Guardian + 1 passada de privacidade +
        /// resolvedores registrados, no maximo 1x a cada StateTtl — nunca 1 scan
        /// por tecla digitada ou por item.
        /// </summary>
        public static Dictionary<string, bool> GetStates()
        {
            lock (_stateLock)
            {
                if ((DateTime.Now - _stateTime) < StateTtl && _stateCache.Count > 0)
                    return new Dictionary<string, bool>(_stateCache);
                // Refresh aninhado (resolver chamando QueryState no meio do
                // refresh): devolve o ultimo conhecido em vez de recursar.
                if (_refreshing)
                    return new Dictionary<string, bool>(_stateCache);
                _refreshing = true;
            }

            var fresh = new Dictionary<string, bool>(StringComparer.Ordinal);
            try
            {
                // Guardian em 1 passada so
                try
                {
                    foreach (var t in Guardian.GetHarmfulTweaksWithStatus())
                        fresh["guardian:" + t.Name] = t.Status == TweakStatus.MODIFIED;
                    fresh["guardian:PATH"] = fresh.Any(kvp =>
                        kvp.Key.StartsWith("guardian:", StringComparison.Ordinal) &&
                        kvp.Key.Contains("PATH", StringComparison.OrdinalIgnoreCase) &&
                        kvp.Value);
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                // Privacidade em 1 passada so
                try
                {
                    foreach (var s in OOShutUpManager.GetPrivacySettings())
                    {
                        try { fresh["privacy:" + s.Name] = OOShutUpManager.IsPrivacySettingApplied(s); }
                        catch { }
                    }
                }
                catch { Logger.LogWarning("Unknown", "Exception suppressed"); }

                // Extras especificos (leituras pontuais baratas)
                try
                {
                    fresh["extra:gamemode"] = SystemTweaks.IsGamingOptimized();
                    try { fresh["extra:mpo"] = SystemTweaks.IsMpoDisabled(); } catch { }
                    try { fresh["extra:vbs"] = !SystemTweaks.IsVbsEnabled(); } catch { }
                    try { fresh["extra:bing"] = SystemTweaks.IsBingDisabled(); } catch { }
                }
                catch { }

                // Resolvedores registrados por providers (AllTweaks etc.), cada um
                // em try/catch isolado e FORA do lock (podem chamar QueryState).
                List<(string Key, Func<bool> Check)> resolvers;
                lock (_stateLock) { resolvers = new List<(string, Func<bool>)>(_stateResolvers); }
                foreach (var (key, check) in resolvers)
                {
                    try { fresh[key] = check(); }
                    catch { }
                }
            }
            finally
            {
                lock (_stateLock)
                {
                    _stateCache = fresh;
                    _stateTime = DateTime.Now;
                    _refreshing = false;
                }
            }
            return new Dictionary<string, bool>(fresh);
        }

        public static bool QueryState(string key)
        {
            lock (_stateLock)
            {
                // Monitor e reentrante na mesma thread: o refresh pode rodar aqui
                // dentro com seguranca quando o cache expirou.
                if ((DateTime.Now - _stateTime) >= StateTtl || _stateCache.Count == 0)
                {
                    var fresh = GetStates();
                    return fresh.TryGetValue(key, out bool v) && v;
                }
                return _stateCache.TryGetValue(key, out bool val) && val;
            }
        }

        public static void InvalidateStates()
        {
            lock (_stateLock) { _stateTime = DateTime.MinValue; }
        }

        private static int ScoreEntry(string titleNorm, string descNorm, string[] words)
        {
            int score = 0;
            foreach (var word in words)
            {
                int w;
                if (titleNorm == word) w = 100;
                else if (titleNorm.StartsWith(word, StringComparison.Ordinal)) w = 80;
                else if (titleNorm.Contains(" " + word, StringComparison.Ordinal)) w = 60;
                else if (titleNorm.Contains(word, StringComparison.Ordinal)) w = 40;
                else if (descNorm.StartsWith(word, StringComparison.Ordinal)) w = 30;
                else if (descNorm.Contains(word, StringComparison.Ordinal)) w = 15;
                else return -1; // AND: toda palavra precisa casar em algum campo
                score += w;
            }
            return score;
        }

        public static List<GlobalSearchResult> Search(string query, int maxResults = int.MaxValue)
        {
            if (!_isInitialized) Initialize();
            if (string.IsNullOrWhiteSpace(query)) return new List<GlobalSearchResult>(0);

            string[] words = Fold(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return new List<GlobalSearchResult>(0);

            var scored = new List<(GlobalSearchResult Item, int Score)>();

            lock (_dbLock)
            {
                foreach (var e in _index)
                {
                    int score = ScoreEntry(e.TitleNorm, e.DescNorm, words);
                    if (score <= 0) continue;

                    if (e.Item.Type == SearchResultType.Navigation) score += 10;
                    else if (e.Item.Type == SearchResultType.Tweak) score += 5;

                    e.Item.Score = score;
                    scored.Add((e.Item, score));
                }
            }

            return scored
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Item.Title.Length)
                .Take(maxResults)
                .Select(s => { s.Item.Score = s.Score; return s.Item; })
                .ToList();
        }

        /// <summary>
        /// Atalho do popup live: top-N instantaneo, sem checagem de estado.
        /// </summary>
        public static List<GlobalSearchResult> SearchTop(string query, int maxResults = 8)
            => Search(query, maxResults);
    }
}
