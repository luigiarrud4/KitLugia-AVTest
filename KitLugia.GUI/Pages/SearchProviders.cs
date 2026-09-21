using System;
using System.Collections.Generic;
using KitLugia.Core;

namespace KitLugia.GUI.Pages
{
    /// <summary>
    /// Registra no SearchEngine os catalogos que moram na GUI (sem o Core
    /// depender da GUI): AllTweaks (108 tweaks) e, no futuro, WinTune/ExmTweaks.
    /// Idempotente e thread-safe; os lambdas so executam sob demanda.
    /// </summary>
    internal static class SearchProviders
    {
        private static bool _registered;
        private static readonly object _gate = new();

        public static void EnsureRegistered()
        {
            if (_registered) return;
            lock (_gate)
            {
                if (_registered) return;
                _registered = true;
            }

            SearchEngine.RegisterProvider(AllTweaksProvider);
        }

        private static IReadOnlyList<GlobalSearchResult> AllTweaksProvider()
        {
            var list = new List<GlobalSearchResult>();
            var states = new List<(string, Func<bool>)>();
            try
            {
                foreach (var t in AllTweaksPage.DefineSystemTweaks())
                {
                    var tweak = t;
                    string key = "alltweak:" + tweak.Name;
                    states.Add((key, tweak.CheckState));
                    list.Add(new GlobalSearchResult
                    {
                        Title = tweak.Name,
                        Description = $"{tweak.Description} (Todos os Tweaks: {tweak.Category})",
                        Icon = "⚙️",
                        ButtonText = "ALTERNAR",
                        Type = SearchResultType.Tweak,
                        IsToggle = true,
                        StateKey = key,
                        ExecuteAction = () =>
                        {
                            bool active = false;
                            try { active = tweak.CheckState(); } catch { }
                            bool ok = active ? tweak.RevertAction() : tweak.ApplyAction();
                            SearchEngine.InvalidateStates();
                            return (ok, ok ? (active ? "Tweak revertido." : "Tweak aplicado.") : "Falhou.");
                        }
                    });
                }
                SearchEngine.RegisterStates(states);
            }
            catch { KitLugia.Core.Logger.LogWarning("Unknown", "Exception suppressed"); }
            return list;
        }
    }
}
