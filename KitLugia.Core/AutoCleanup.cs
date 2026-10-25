using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace KitLugia.Core
{
    /// <summary>
    /// Decide o que pode ser apagado SOZINHO depois de desinstalar, sem
    /// perguntar nada ao utilizador.
    ///
    /// A exigencia e' ALTA de propenso: errar aqui apaga ficheiro de outro
    /// programa sem o utilizador ter aprovado. Por isso so entram duas situacoes
    /// que nao admitem duvida — e o resto vai para o review como sempre.
    ///
    /// Regra (nesta ordem, a primeira que casa ganha):
    ///   100  o caminho esta DENTRO da pasta de instalacao do proprio programa
    ///   100  a pasta tem EXATAMENTE o nome do programa em LocalAppData/Roaming
    ///         (%AppData%\Discord, %LocalAppData%\VLC — o nome bate inteiro)
    ///    60  nome COMECA com o do programa, ou outra pasta dentro do AppData
    ///    20  em AppData mas sem relacao de nome
    ///     0  fora do AppData e fora da instalacao (ProgramData, Windows,
    ///        Program Files partilhado...) — NUNCA automatico
    ///
    /// <see cref="LimiarAutomatico"/> e' 95 de proposito: acima do 100-exato
    /// nao ha nada. Se um dia alguem quiser afrouxar, mexer aqui e no
    /// comentario — e o numero aparece no log sempre que a limpeza automatica
    /// corre, para se saber o que estava em jogo.
    /// </summary>
    public static class AutoCleanup
    {
        /// <summary>So entra no automatico o que pontuar 100.</summary>
        public const int LimiarAutomatico = 95;

        public const int ConfiancaDentroDaInstalacao = 100;
        public const int ConfiancaPastaExataNoAppData = 100;
        public const int ConfiancaNomeParcial = 60;
        public const int ConfiancaAppDataSemNome = 20;

        /// <summary>
        /// Confianca (0-100) de que <paramref name="path"/> e residuo do
        /// programa <paramref name="displayName"/>. Compare sempre com
        /// <see cref="LimiarAutomatico"/> — nao chame esperando "acima de 60".
        /// </summary>
        public static int ConfiancaDe(string displayName, string? installLocation, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(displayName))
                return 0;

            string p = path.TrimEnd('\\', '/');
            if (p.Length < 3) return 0;

            // ── 1) dentro da propria instalacao do programa ────────────────
            // Vai PRIMEIRO de proposito: e' o sinal mais forte que existe (os
            // ficheiros sao literalmente do programa). Se fosse depois do guard
            // de areas sensiveis, uma instalacao em "C:\Program Files" — que e'
            // area sensivel — nunca passava, e o proprio programa ficava
            //imediatamente de fora.
            if (!string.IsNullOrWhiteSpace(installLocation))
            {
                string inst = installLocation.TrimEnd('\\', '/');
                // A localizacao tem de ser EXCLUSIVA do programa. Alguns
                // instaladores deixam os dados em "C:\ProgramData\<App>" ou em
                // "Common Files" — essas pastas sao partilhadas por N programas
                // e apagar as cegas e' exatamente o erro que nao podemos
                // cometer. O codigo do programa ("C:\Program Files\<App>") e'
                // exclusivo dele e continua a passar.
                if (inst.Length > 0 && LocalizacaoEhExclusiva(inst) &&
                    (p.Equals(inst, StringComparison.OrdinalIgnoreCase) ||
                     p.StartsWith(inst + "\\", StringComparison.OrdinalIgnoreCase)))
                    return ConfiancaDentroDaInstalacao;
            }

            // 2) NUNCA tocar em areas do sistema nem em pastas partilhadas.
            if (EhRaizCritica(p)) return 0;

            // 3) AppData do utilizador com o nome EXATO do programa
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            foreach (var raiz in new[] { local, roaming })
            {
                if (string.IsNullOrEmpty(raiz)) continue;
                string r = raiz.TrimEnd('\\', '/');
                if (!p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)) continue;

                string rel = p[r.Length..].TrimStart('\\', '/');
                if (rel.Length == 0) continue;

                string primeiraPasta = rel.Split('\\')[0];
                if (string.Equals(primeiraPasta, displayName, StringComparison.OrdinalIgnoreCase))
                    return ConfiancaPastaExataNoAppData;

                if (primeiraPasta.StartsWith(displayName, StringComparison.OrdinalIgnoreCase))
                    return ConfiancaNomeParcial;

                return ConfiancaAppDataSemNome;
            }

            // Fora do AppData e fora da instalacao: so o classificador antigo
            // (que olha o ProgramData) decide, e esse nunca e automatico.
            return 0;
        }

        /// <summary>Pode ser apagado sem perguntar?</summary>
        public static bool EhAutomatico(string displayName, string? installLocation, string? path)
            => ConfiancaDe(displayName, installLocation, path) >= LimiarAutomatico;

        /// <summary>Separa o que some sozinho do que ainda precisa de decisao.</summary>
        public static (List<string> automaticos, List<string> restantes) Separar(
            string displayName, string? installLocation, IEnumerable<string> paths)
        {
            var auto = new List<string>();
            var resto = new List<string>();
            foreach (var p in paths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                (EhAutomatico(displayName, installLocation, p) ? auto : resto).Add(p);
            }
            return (auto, resto);
        }

        /// <summary>
        /// A localizacao de instalacao pertence SO a este programa?
        /// <c>C:\Program Files\App</c> sim. <c>C:\ProgramData\App</c> e
        /// <c>...\Common Files\App</c> nao — essas sao partilhadas.
        /// </summary>
        static bool LocalizacaoEhExclusiva(string inst)
        {
            string[] partilhadas =
            {
                @"C:\ProgramData",
                @"C:\Program Files\Common Files",
                @"C:\Program Files (x86)\Common Files",
                @"C:\Windows",
            };
            foreach (var x in partilhadas)
                if (inst.Equals(x, StringComparison.OrdinalIgnoreCase) ||
                    inst.StartsWith(x + "\\", StringComparison.OrdinalIgnoreCase))
                    return false;
            return true;
        }

        static bool EhRaizCritica(string p)
        {
            // raiz de volume ("C:" ou "C:\") — nunca
            if (p.Length <= 3) return true;

            // Pastas partilhadas por VARIOS programas. Bloquear por prefixo e
            // o que impede o pior erro: "Discord" pedir e ir buscar a pasta do
            // vizinho. Nota: "C:\Users" NAO entra aqui de proposito — TODO o
            // AppData vive la dentro, e o ramo do AppData ja e' preciso (exige
            // nome EXATO). O perfil em si cai em 0 sozinho por nao estar em
            // AppData nem na instalacao.
            string[] partilhadasPorPrefixo =
            {
                @"C:\Windows",
                @"C:\Program Files",
                @"C:\Program Files (x86)",
                @"C:\Program Files\Common Files",
                @"C:\ProgramData",
            };
            foreach (var x in partilhadasPorPrefixo)
                if (p.Equals(x, StringComparison.OrdinalIgnoreCase) ||
                    p.StartsWith(x + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;

            //ocoisas que nao sao caminho absoluto de disco
            if (!p.Contains('\\')) return true;
            if (p.StartsWith(@"\\", StringComparison.Ordinal)) return true;

            return false;
        }
    }
}