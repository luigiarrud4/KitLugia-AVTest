using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace KitLugia.GUI.Logging
{
    /// <summary>
    /// Armazenamento do log desacoplado da UI.
    /// - Persistência completa em disco (todas as linhas, sem limite).
    /// - Anel em memória com teto rígido (MaxInMemoryLines) — a RAM nunca explode.
    /// - A UI consome somente o anel (virtualizado); "copiar tudo" lê o arquivo completo.
    /// </summary>
    public static class LogStore
    {
        // --- Configuração de limites ---
        // O anel em memória guarda no máximo estas linhas. O arquivo em disco guarda TUDO.
        // 20.000 linhas * ~150 bytes = ~3 MB de RAM — inofensivo, e a UI vê apenas ~30 por vez.
        public const int MaxInMemoryLines = 20000;
        // Rotação do arquivo em disco para não crescer para sempre.
        private const long MaxFileBytes = 64 * 1024 * 1024; // 64 MB

        private static readonly object _lock = new();
        private static readonly List<string> _ring = new(MaxInMemoryLines);
        private static readonly string _filePath;

        /// <summary>Contagem total de linhas já adicionadas desde o início da sessão.</summary>
        public static long TotalLines { get; private set; }

        static LogStore()
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KitLugia", "Logs");
            Directory.CreateDirectory(logDir);
            _filePath = Path.Combine(logDir, "KitLugiaConsole.log");

            // Sessão NOVA: o log do console começa zerado a cada inicialização do kit.
            // O arquivo antigo (e o .old rotacionado) não fazem sentido para a próxima
            // sessão — o usuário viu 132k linhas de "X" de testes antigos vazando no
            // "copiar tudo". Limpa os arquivos no boot e deixa só a sessão atual.
            ResetAllFiles();
        }

        /// <summary>Apaga o arquivo atual + o .old rotacionado (sem tocar o anel da UI).</summary>
        private static void ResetAllFiles()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(_filePath)) File.WriteAllText(_filePath, "");
                    var old = _filePath + ".old";
                    if (File.Exists(old)) File.Delete(old);
                }
                catch
                {
                }
            }
        }

        public static string FilePath => _filePath;

        /// <summary>Adiciona uma linha: vai para o anel e para o arquivo.</summary>
        public static void AppendLine(string line)
        {
            lock (_lock)
            {
                TotalLines++;
                _ring.Add(line);
                if (_ring.Count > MaxInMemoryLines)
                    _ring.RemoveAt(0);

                try
                {
                    var fi = new FileInfo(_filePath);
                    if (fi.Exists && fi.Length > MaxFileBytes)
                        RotateFile();
                    File.AppendAllText(_filePath, line + Environment.NewLine);
                }
                catch
                {
                    // Se o disco falhar, a sessão continua funcionando com o anel em memória.
                }
            }
        }

        /// <summary>Retorna as últimas N linhas do anel (nunca lê disco no caminho quente da UI).</summary>
        public static IReadOnlyList<string> GetRecent(int count)
        {
            lock (_lock)
            {
                if (count <= 0 || _ring.Count == 0) return Array.Empty<string>();
                int start = Math.Max(0, _ring.Count - count);
                var result = new List<string>(Math.Min(count, _ring.Count - start));
                for (int i = start; i < _ring.Count; i++)
                    result.Add(_ring[i]);
                return result;
            }
        }

        /// <summary>
        /// Retorna o log COMPLETO (anel atual + tudo que já foi persistido no arquivo),
        /// sem linha duplicada — a .NET/arquivo sobe o anel antigo se o arquivo foi rotacionado?
        /// Não: o arquivo contém tudo, o anel contém as últimas MaxInMemoryLines que JÁ estão
        /// no arquivo também. Para o texto completo basta ler o arquivo.
        /// </summary>
        public static string GetFullText()
        {
            lock (_lock)
            {
                return ReadAllTextUnsafe();
            }
        }

        private static string ReadAllTextUnsafe()
        {
            try
            {
                if (!File.Exists(_filePath)) return string.Empty;
                // Arquivo rotacionado (.old) vem ANTES do atual (ordem cronológica).
                var old = _filePath + ".old";
                if (File.Exists(old))
                    return File.ReadAllText(old) + "\n" + File.ReadAllText(_filePath);
                return File.ReadAllText(_filePath);
            }
            catch
            {
                return string.Join("\n", _ring);
            }
        }

        public static void Clear()
        {
            lock (_lock)
            {
                _ring.Clear();
                TotalLines = 0;
                try
                {
                    if (File.Exists(_filePath)) File.WriteAllText(_filePath, "");
                    // O .old (rotacao antiga) tambem deve sumir - senao "copiar tudo"
                    // continua lendo dezenas de milhares de linhas antigas.
                    var old = _filePath + ".old";
                    if (File.Exists(old)) File.Delete(old);
                }
                catch
                {
                }
            }
        }

        private static void RotateFile()
        {
            // Renomeia o atual para .old (sobrescreve) e começa um arquivo novo.
            var old = _filePath + ".old";
            try
            {
                if (File.Exists(old)) File.Delete(old);
                if (File.Exists(_filePath)) File.Move(_filePath, old);
            }
            catch
            {
            }
        }
    }
}