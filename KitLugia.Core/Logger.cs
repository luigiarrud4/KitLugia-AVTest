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

        public static void LogWarning(string context, string message,
            [System.Runtime.CompilerServices.CallerFilePath] string? sourceFile = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int sourceLine = 0,
            [System.Runtime.CompilerServices.CallerMemberName] string? sourceMember = null)
        {
            // Anexa a LOCALIZACAO exata (arquivo:linha metodo) ao warning sem precisar
            // editar os ~600 call sites — o Caller* e preenchido pelo compilador.
            // Antes, "Exception suppressed" nao dizia DE ONDE veio; agora identifica
            // o catch que falhou (ex: SystemTweaks.cs:123 AplicarTweak).
            string detail = message;
            if (sourceFile != null && message.IndexOf("Exception suppressed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                detail = $"{message} [origem: {Path.GetFileName(sourceFile)}:{sourceLine} {sourceMember}]";
            }

            if (detail.Contains("Exception suppressed", StringComparison.OrdinalIgnoreCase) &&
                ShouldSuppressRepeated(context, detail))
            {
                return;
            }
            var fullMsg = $"[AVISO] ({context}): {detail}";
            WriteToFile("WARN", fullMsg);
            OnLogReceived?.Invoke(fullMsg);
        }

        // Rate limiter for the flood of "Exception suppressed" warnings produced by the
        // deep-scan loops (each inacessible file/folder logs individually, which can spike
        // hundreds of lines in a few seconds). Keeps the first occurrence, then logs a
        // summary every 100 repeats within the same 60s window instead of every item.
        private const int SuppressLogEvery = 100;
        private static readonly object SuppressLock = new();
        private static readonly Dictionary<string, (int Count, DateTime First)> SuppressedWarnings =
            new(StringComparer.OrdinalIgnoreCase);

        private static bool ShouldSuppressRepeated(string context, string message)
        {
            lock (SuppressLock)
            {
                var now = DateTime.UtcNow;
                string key = $"{context}|{message}";
                if (SuppressedWarnings.TryGetValue(key, out var entry))
                {
                    if ((now - entry.First).TotalSeconds > 60)
                    {
                        SuppressedWarnings[key] = (1, now);
                        return false;
                    }

                    int count = entry.Count + 1;
                    SuppressedWarnings[key] = (count, entry.First);
                    // Let one summary line through every N repeats; everything else is hidden.
                    return count % SuppressLogEvery != 0;
                }

                SuppressedWarnings.Add(key, (1, now));
                return false;
            }
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