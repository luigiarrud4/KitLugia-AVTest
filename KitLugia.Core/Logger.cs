using System;
using System.IO;

namespace KitLugia.Core
{
    public static class Logger
    {
        private static readonly string LogFilePath;
        private static readonly object LogLock = new();

        public static bool DisableOutputLimit = false;
        public static bool VerboseCheckLogs = false;
        public static event Action<string>? OnLogReceived;

        static Logger()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logDir = Path.Combine(appData, "KitLugia", "Logs");
            Directory.CreateDirectory(logDir);
            LogFilePath = Path.Combine(logDir, "KitLugia.log");
        }

        private static void WriteToFile(string level, string message)
        {
            try
            {
                lock (LogLock)
                {
                    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
                    File.AppendAllText(LogFilePath, line + Environment.NewLine);
                }
            }
            catch
            {
                // Se falhar ao escrever no arquivo, não podemos fazer nada
            }
        }

        public static void Log(string message)
        {
            WriteToFile("INFO", message);
            OnLogReceived?.Invoke(message);
        }

        public static void LogProcess(string filename, string args)
        {
            var msg = $"[EXEC] {filename} {args}";
            WriteToFile("EXEC", msg);
            OnLogReceived?.Invoke(msg);
        }

        public static void LogRegistry(string key, string value, object data)
        {
            var msg = $"[REG] Setando '{value}' = '{data}' em {key}";
            WriteToFile("REG", msg);
            OnLogReceived?.Invoke(msg);
        }

        public static void LogError(string context, string error)
        {
            var msg = $"[ERRO] ({context}): {error}";
            WriteToFile("ERROR", msg);
            OnLogReceived?.Invoke(msg);
        }

        public static void LogWarning(string context, string message)
        {
            var msg = $"[AVISO] ({context}): {message}";
            WriteToFile("WARN", msg);
            OnLogReceived?.Invoke(msg);
        }

        public static void ToggleOutputLimit()
        {
            DisableOutputLimit = !DisableOutputLimit;
            var msg = DisableOutputLimit
                ? "LIMITE DE 500 LINHAS REMOVIDO - Logs completos serao capturados"
                : "LIMITE DE 500 LINHAS ATIVADO - Logs serao truncados";
            WriteToFile("TOGGLE", msg);
            OnLogReceived?.Invoke(msg);
        }

        public static void ToggleVerboseCheck()
        {
            VerboseCheckLogs = !VerboseCheckLogs;
            var msg = VerboseCheckLogs
                ? "Logs CHECK detalhados ATIVADOS - Mostra todas as verificacoes"
                : "Logs CHECK detalhados DESATIVADOS - Mostra apenas erros e mudancas";
            WriteToFile("TOGGLE", msg);
            OnLogReceived?.Invoke(msg);
        }

        public static string GetLogPath()
        {
            return LogFilePath;
        }
    }
}