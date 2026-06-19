using System;

namespace KitLugia.Core
{
    // Classe estática simples para enviar mensagens do Core para a GUI
    public static class Logger
    {
        // Toggle para remover limite de 500 linhas do console
        public static bool DisableOutputLimit = false;
        
        // Controle de verbosidade para reduzir spam
        public static bool VerboseCheckLogs = false;
        
        // Evento que a GUI vai "escutar"
        public static event Action<string>? OnLogReceived;

        public static void Log(string message)
        {
            // Dispara o evento se houver alguém escutando
            OnLogReceived?.Invoke(message);
        }

        public static void LogProcess(string filename, string args)
        {
            OnLogReceived?.Invoke($"[EXEC] {filename} {args}");
        }

        public static void LogRegistry(string key, string value, object data)
        {
            OnLogReceived?.Invoke($"[REG] Setando '{value}' = '{data}' em {key}");
        }

        public static void LogError(string context, string error)
        {
            OnLogReceived?.Invoke($"[ERRO] ({context}): {error}");
        }
        
        // Comando para ativar/desativar o limite de linhas via console
        public static void ToggleOutputLimit()
        {
            DisableOutputLimit = !DisableOutputLimit;
            OnLogReceived?.Invoke(DisableOutputLimit ? 
                "🔓 LIMITE DE 500 LINHAS REMOVIDO - Logs completos serão capturados" : 
                "🔒 LIMITE DE 500 LINHAS ATIVADO - Logs serão truncados");
        }
        
        // Comando para controlar verbosidade de logs CHECK
        public static void ToggleVerboseCheck()
        {
            VerboseCheckLogs = !VerboseCheckLogs;
            OnLogReceived?.Invoke(VerboseCheckLogs ? 
                "📝 Logs CHECK detalhados ATIVADOS - Mostra todas as verificações" : 
                "📝 Logs CHECK detalhados DESATIVADOS - Mostra apenas erros e mudanças");
        }
    }
}