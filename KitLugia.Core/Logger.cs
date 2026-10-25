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

        /// <summary>
        /// Log que so fala quando o ESTADO MUDA (o resto e heartbeat).
        /// Uso: rotinas que rodam a cada ciclo e reprintem quase a mesma linha
        /// (RAM Limiter a cada 500 ms, [DISK] a cada varredura). Sem isto o log
        /// vira hundreds de linhas e o que importa (erro real) se perde no meio.
        ///
        /// - 1a chamada sempre passa;
        /// - depois so passa se o texto mudar (ou a ASSINATURA, se houver)
        ///   OU se passou o intervalo de heartbeat;
        /// - a contagem e incrementada sempre e anexada no heartbeat ("x17"), para
        ///   nao se perder que a rotina esteve ativa.
        ///
        /// ASSINATURA (opcional): o que realmente caracteriza o ESTADO. Sem ela
        /// compara o texto inteiro - e ai uma linha com numeros variaveis (MB,
        /// cpu, faults) muda SEMPRE e o LogOnce deixa vazar todas. Exemplo real
        /// do kit: "RAM Limiter: opera ... 3581 MB ... 3398 MB ... 1398 MB" - o
        /// texto muda a cada ciclo, mas o estado (mesmo processo, mesma acao) e
        /// o mesmo. Com a assinatura, a linha so reemite quando a acao muda ou
        /// no heartbeat.
        ///
        /// MIN_CHANGE_INTERVAL (opcional): mesmo quando o estado muda, so
        /// reporta a mudanca se passou este tempo desde a ultima linha. Existe
        /// porque alguns estados OSCILAM: o RAM Limiter alterna entre
        /// "esvaziado" e "teto" a cada 5 s conforme o app ganha/perde o foco -
        /// cada transicao e uma "mudanca" e o log vira 4 linhas/min de
        /// flip-flop. Com o intervalo, a mudanca e reportada quando o estado
        /// ESTABILIZA (ou no heartbeat), e o log fica legivel.
        /// </summary>
        public static void LogOnce(string key, string message, int heartbeatSeconds = 120,
            string? signature = null, int minChangeIntervalSeconds = 0)
        {
            lock (StateLock)
            {
                var now = DateTime.UtcNow;
                string compare = signature ?? message;
                if (StateLogLast.TryGetValue(key, out var last))
                {
                    int count = last.Count + 1;
                    bool mudou = !string.Equals(last.Text, compare, StringComparison.Ordinal);
                    bool heartbeat = (now - last.At).TotalSeconds >= Math.Max(5, heartbeatSeconds);
                    bool passouMin = minChangeIntervalSeconds <= 0 ||
                        (now - last.At).TotalSeconds >= minChangeIntervalSeconds;

                    if (mudou && !passouMin)
                    {
                        // Mudou, mas rapido demais: guarda o estado novo e NAO loga.
                        // `At` NAO e mexido (e' o horario da ultima LINHA) para o
                        // heartbeat nao reiniciar a cada supressao.
                        StateLogLast[key] = (count, last.At, compare, last.First);
                        return;
                    }

                    if (!mudou && !heartbeat)
                    {
                        StateLogLast[key] = (count, now, compare, last.At);
                        return;
                    }

                    string saida = mudou
                        ? message
                        : $"{message}  (x{count} em {(now - last.At).TotalMinutes:F0} min sem mudar)";
                    StateLogLast[key] = (1, now, compare, now);
                    WriteToFile("INFO", saida);
                    OnLogReceived?.Invoke(saida);
                    return;
                }

                StateLogLast[key] = (1, now, compare, now);
                WriteToFile("INFO", message);
                OnLogReceived?.Invoke(message);
            }
        }

        /// <summary>Limpa o estado de uma chave (usar quando a rotina desliga).</summary>
        public static void ResetLogState(string key)
        {
            lock (StateLock)
            {
                StateLogLast.Remove(key);
            }
        }

        private static readonly object StateLock = new();
        private static readonly Dictionary<string, (int Count, DateTime At, string Text, DateTime First)>
            StateLogLast = new(StringComparer.Ordinal);

        public static void LogProcess(string filename, string args)
        {
            var msg = $"[EXEC] {filename} {args}";
            WriteToFile("EXEC", msg);
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
    }
}