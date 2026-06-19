using System;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    
    [SupportedOSPlatform("windows")]
    public static class OptimizationOrchestrator
    {
        public static async Task RunOptimizationAsync(OptimizationSettings settings, IProgress<string> progress)
        {
            await Task.Run(() =>
            {
                // Helper para enviar logs para a UI e para o Console Interno
                void Report(string msg)
                {
                    progress?.Report(msg);
                    Logger.Log(msg);
                }

                Report("=== APLICANDO OTIMIZAÇÃO DE PERFORMANCE ESSENCIAL ===");
                Thread.Sleep(300);

                // [1/5] REGISTRO E CACHE
                if (settings.ApplyRegistryTweaks)
                {
                    Report("[1/5] Aplicando Tweaks de Registro Seguros...");
                    SystemTweaks.ApplyAutoCacheTweak();
                    SystemTweaks.ApplyLastClickTweak();
                    SystemTweaks.ApplyBingTweak();
                    Report("   - Cache de CPU (L2/L3) e ajustes de Explorer configurados.");
                    Thread.Sleep(500);
                }

                // [2/5] PLANO DE ENERGIA
                if (settings.ApplyPowerPlan)
                {
                    Report("[2/5] Importando e Ativando Plano de Energia...");
                    var bitsumResult = Toolbox.ImportAndActivateBitsumPlan();
                    if (bitsumResult.Success)
                    {
                        Report("   - Plano 'Bitsum Highest Performance' ativado!");
                    }
                    else
                    {
                        Toolbox.UnlockAndActivateUltimatePerformance();
                        Report("   - Plano 'Desempenho Máximo' ativado (Fallback).");
                    }
                    Thread.Sleep(500);
                }

                // [3/5] AGENDAMENTO DE GPU
                if (settings.ApplyGamingOptimizations)
                {
                    Report("[3/5] Ativando Otimizações de Agendamento para Jogos...");
                    SystemTweaks.ApplyGamingOptimizations();
                    Report("   - Prioridade de Jogos e Agendamento de GPU ativados.");
                    Thread.Sleep(500);
                }

                // [4/5] BOOT VERBOSE
                if (settings.ApplyVerboseBoot)
                {
                    Report("[4/5] Ativando Mensagens Detalhadas de Boot...");
                    SystemTweaks.ApplyVerboseStatus();
                    Report("   - Mensagens detalhadas no boot ativadas.");
                    Thread.Sleep(500);
                }

                // [5/5] VRAM DEDICADA
                if (settings.ApplyVramTweak)
                {
                    Report("[5/5] Ajustando VRAM para a GPU Selecionada...");
                    try
                    {
                        if (string.IsNullOrEmpty(settings.TargetGpuRegPath))
                        {
                            SystemTweaks.ApplyAutomaticVramTweak();
                            Report("   - Ajuste de VRAM Automático aplicado.");
                        }
                        else
                        {
                            SystemTweaks.ApplyGpuVramTweak(settings.TargetGpuRegPath, settings.VramSizeMb);
                            Report($"   - VRAM ajustada para {settings.VramSizeMb} MB.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Report($"   - Falha no ajuste de VRAM: {ex.Message}");
                    }
                    Thread.Sleep(500);
                }

                // MODO EXTREME (VISUAIS)
                if (settings.UseExtremeProfile)
                {
                    Report("\n=== INICIANDO MÓDULO EXTREME (VISUAIS) ===");
                    Thread.Sleep(500);
                    Report("   > [VISUAL] Removendo animações, sombras e efeitos para Max FPS...");
                    SystemTweaks.ApplyExtremeVisuals();

                    if (SystemTweaks.IsExtremeVisualsApplied())
                    {
                        Report("   ✔ [SUCESSO] Efeitos visuais desativados e validados.");
                    }
                    else
                    {
                        Report("   ⚠ [AVISO] Talvez seja necessário reiniciar para aplicar visualmente.");
                    }
                    Report("--- MODO EXTREME APLICADO! ---");
                }

                Report("\n--- OTIMIZAÇÃO CONCLUÍDA ---");
                Report("Reinicie o computador para garantir que todos os tweaks entrem em vigor.");
            });
        }

        public static async Task RevertAllOptimizationsAsync(IProgress<string> progress)
        {
            await Task.Run(() =>
            {
                void Report(string msg) { progress?.Report(msg); Logger.Log(msg); }

                Report("=== REVERTENDO TODAS AS OTIMIZAÇÕES (VOLTANDO AO NORMAL) ===");
                
                Report("-> Restaurando VRAM original para todas as GPUs...");
                SystemTweaks.RevertVramTweaks();

                Report("-> Removendo Otimizações de Agendamento...");
                // Note: SystemTweaks.ApplyGamingOptimizations doesn't have a direct revert yet, 
                // but we can manually clear the keys if needed.
                SystemTweaks.RevertRegistryValue(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "GPU Priority");
                SystemTweaks.RevertRegistryValue(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games", "Priority");

                Report("-> Revertendo Tweaks de Registro e Cache...");
                SystemTweaks.RevertRegistryValue(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "SecondLevelDataCache");
                SystemTweaks.RevertRegistryValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LastActiveClick");
                SystemTweaks.RevertRegistryValue(@"HKEY_CURRENT_USER\Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions");

                Report("--- REVERSÃO CONCLUÍDA ---");
                Report("Reinicie o sistema para restaurar completamente o estado original.");
            });
        }
    }
}
