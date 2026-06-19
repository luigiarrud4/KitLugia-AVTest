using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.Versioning;

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
        private static List<GlobalSearchResult> _database = new();
        private static bool _isInitialized = false;
        private static readonly object _dbLock = new();

        public static void Initialize()
        {
            if (_isInitialized) return;
            lock (_dbLock) _database.Clear();

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
            AddNav("Config. Tray", "Monitor de RAM e bandeja do sistema.", "🔔", "TraySettings");
            AddNav("Diagnóstico", "Depuração e monitoramento interno.", "🔬", "Diagnostic");
            AddNav("Servidor Local", "Túneis e servidor local.", "🌍", "Server");
            AddNav("Mídia Bootável", "Criação de pendrive bootável.", "💿", "Rufus");
            AddNav("Stutter Detector", "Detector de travamentos e micro-stutters (QPC). Diagnóstico de áudio e DPC.", "🪄", "Stutter");
            AddNav("WinTune", "Centenas de otimizações avançadas do Windows (registry e sistema).", "🎯", "WinTune");
            AddNav("Todos os Tweaks", "Lista unificada de todos os tweaks disponíveis no KitLugia.", "⚙️", "AllTweaks");
            AddNav("Config. RAM Avançada", "Limpeza agressiva de memória e perfis de RAM.", "🧠", "AdvancedRamCleanSettings");

            // 2. REPAROS (Ações sem estado)
            try
            {
                var repairs = GeneralRepairManager.GetAllRepairs();
                foreach (var repair in repairs)
                {
                    _database.Add(new GlobalSearchResult
                    {
                        Title = repair.Name,
                        Description = $"Reparo: {repair.Description}",
                        Icon = string.IsNullOrEmpty(repair.Icon) ? "🔧" : repair.Icon,
                        ButtonText = "EXECUTAR",
                        Type = SearchResultType.Action,
                        ExecuteAction = () => { repair.Execute?.Invoke(); return (true, "Comando enviado."); }
                    });
                }
            }
            catch { }

            // 3. TWEAKS DE SEGURANÇA (GUARDIAN)
            try
            {
                var securityTweaks = Guardian.GetAllTweaksDefinition();
                foreach (var tweak in securityTweaks)
                {
                    _database.Add(new GlobalSearchResult
                    {
                        Title = tweak.Name,
                        Description = tweak.Description,
                        Icon = "🛡️",
                        Type = SearchResultType.Tweak,
                        IsToggle = true,
                        CheckState = () => {
                            try
                            {
                                var all = Guardian.GetHarmfulTweaksWithStatus();
                                return all.FirstOrDefault(t => t.Name == tweak.Name)?.Status == TweakStatus.MODIFIED;
                            }
                            catch { return false; }
                        },
                        ExecuteAction = () => Guardian.ToggleTweak(tweak)
                    });
                }
            }
            catch { }

            // 4. BLOATWARE (carregado em background)
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var bloatApps = SystemTweaks.GetBloatwareAppsStatus();
                    lock (_dbLock)
                    {
                        foreach (var app in bloatApps)
                        {
                            _database.Add(new GlobalSearchResult
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
                catch { }
            });

            // 5. TWEAKS ESPECÍFICOS
            AddToggle("Modo Jogo (Game Mode)", "Prioridade de GPU e afinidade.", "🎮",
                action: () => { SystemTweaks.ApplyGamingOptimizations(); return (true, "Aplicado."); },
                check: () => SystemTweaks.IsGamingOptimized()
            );
            AddToggle("Desativar MPO", "Corrige telas piscando (Multi-Plane Overlay).", "📺",
                action: () => SystemTweaks.ToggleMpo(),
                check: () => SystemTweaks.IsMpoDisabled()
            );
            AddToggle("Desativar VBS", "Aumenta FPS desativando virtualização.", "⚡",
                action: () => SystemTweaks.ToggleVbs(),
                check: () => !SystemTweaks.IsVbsEnabled()
            );
            AddToggle("Desativar Pesquisa Bing", "Remove sugestões web do Iniciar.", "🔍",
                action: () => {
                    if (SystemTweaks.IsBingDisabled())
                    { SystemTweaks.RevertRegistryValue(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions"); return (true, "Reativado."); }
                    else { SystemTweaks.ApplyBingTweak(); return (true, "Desativado."); }
                },
                check: () => SystemTweaks.IsBingDisabled()
            );

            AddAction("CompactOS", "Comprime o Windows para economizar espaço.", "🗜️", () => { Toolbox.CompactOS(); return (true, "Iniciado."); });
            AddAction("Limpar Shaders", "Limpa cache de shader da GPU.", "🧹", () => { Toolbox.CleanShaderCaches(); return (true, "Limpo."); });
            AddAction("Flush DNS", "Limpa cache de resolução DNS.", "🚿", () => Toolbox.FlushDnsCache());
            AddAction("Resetar Windows Update", "Corrige erro 0x800 de atualização.", "🔄", () => { var r = Toolbox.ResetWindowsUpdateComponents(); return (r.Success, "Resetado."); });
            AddAction("Limpeza Total", "Limpa temporários, cache e lixeira.", "🧹", () => { Toolbox.RunFullCleanup(); return (true, "Limpeza concluída."); });
            AddAction("Reparar Imagem (DISM)", "Corrige corrupção do sistema.", "🚑", () => { var r = SystemRepair.RunDismRestoreHealthAsync().GetAwaiter().GetResult(); return (r.Success, r.Output); });
            AddAction("Verificar Arquivos (SFC)", "Escaneia arquivos protegidos.", "⚕️", () => { var r = SystemRepair.RunSfcScanNowAsync().GetAwaiter().GetResult(); return (r.Success, r.Output); });

            _isInitialized = true;
        }

        private static void AddNav(string title, string desc, string icon, string tag)
        {
            _database.Add(new GlobalSearchResult { Title = title, Description = desc, Icon = icon, ButtonText = "IR PARA", Type = SearchResultType.Navigation, NavigationTag = tag });
        }

        private static void AddAction(string title, string desc, string icon, Func<(bool, string)> action)
        {
            _database.Add(new GlobalSearchResult { Title = title, Description = desc, Icon = icon, ButtonText = "EXECUTAR", Type = SearchResultType.Action, ExecuteAction = action });
        }

        private static void AddToggle(string title, string desc, string icon, Func<(bool, string)> action, Func<bool> check)
        {
            _database.Add(new GlobalSearchResult
            {
                Title = title,
                Description = desc,
                Icon = icon,
                IsToggle = true,
                Type = SearchResultType.Tweak,
                ExecuteAction = action,
                CheckState = check
            });
        }

        public static List<GlobalSearchResult> Search(string query)
        {
            if (!_isInitialized) Initialize();
            if (string.IsNullOrWhiteSpace(query)) return new List<GlobalSearchResult>(0);

            query = query.ToLower().Trim();
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Search com scoring
            var scored = new List<(GlobalSearchResult Item, int Score)>();

            lock (_dbLock)
            {
                foreach (var item in _database)
                {
                    int score = 0;
                    string titleLower = item.Title.ToLower();
                    string descLower = item.Description.ToLower();

                    foreach (var word in words)
                    {
                        if (titleLower == word) score += 100;
                        else if (titleLower.StartsWith(word)) score += 80;
                        else if (titleLower.Contains(" " + word)) score += 60;
                        else if (titleLower.Contains(word)) score += 40;
                        else if (descLower.StartsWith(word)) score += 30;
                        else if (descLower.Contains(word)) score += 15;
                        else { score = -1; break; }
                    }

                    if (score > 0)
                    {
                        // Bônus por tipo (navegação primeiro)
                        if (item.Type == SearchResultType.Navigation) score += 10;
                        else if (item.Type == SearchResultType.Tweak) score += 5;

                        item.Score = score;
                        scored.Add((item, score));
                    }
                }
            }

            return scored
                .OrderByDescending(s => s.Score)
                .ThenBy(s => s.Item.Title.Length)
                .Select(s => { s.Item.Score = s.Score; return s.Item; })
                .ToList();
        }
    }
}
