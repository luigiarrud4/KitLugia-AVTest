using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KitLugia.Core;

namespace AutoCleanTest
{
    /// <summary>
    /// Prova que a limpeza automatica so toca no que e' 100% confirmado.
    ///
    /// Este teste e' o guardiao do "apagar sem perguntar": se alguem afrouxar
    /// o limiar ou alargar uma regra, este ficheiro falha. Cada caso abaixo
    /// existe porque e' um erro REAL que ja aconteceu noutro sitio do codigo
    /// (pasta de outro programa apanhada por nome parecido, raiz de disco, etc).
    /// </summary>
    internal static class Program
    {
        static int _pass, _fail;
        static readonly List<string> _falhas = new();

        static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        static string RoamingAppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        static void Checar(string nome, bool ok, string detalhe = "")
        {
            if (ok) { _pass++; Console.WriteLine($"  OK   {nome}"); }
            else { _fail++; _falhas.Add($"{nome} {detalhe}"); Console.WriteLine($"  FALHA {nome}  {detalhe}"); }
        }

        static void Automatico(string nome, string app, string inst, string path, bool esperado)
        {
            int c = AutoCleanup.ConfiancaDe(app, inst, path);
            bool auto = AutoCleanup.EhAutomatico(app, inst, path);
            Checar(nome, auto == esperado, $"confianca={c} auto={auto} esperado={esperado} :: {path}");
        }

        static int Main()
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.WriteLine("== Limiar e escala ==");

            Checar("limiar e' 95 (muito alto)", AutoCleanup.LimiarAutomatico == 95,
                $"valor={AutoCleanup.LimiarAutomatico}");

            Checar("so pontuacao 100 passa o portao",
                AutoCleanup.EhAutomatico("Discord", null, LocalAppData + @"\Discord") &&
                !AutoCleanup.EhAutomatico("Discord", null, LocalAppData + @"\DiscordOld"),
                "100 passa, 60 nao");

            // ── 100: CONFIRMADO ────────────────────────────────────────────
            Console.WriteLine();
            Console.WriteLine("== Casos que DEVEM ser automaticos (100%) ==");

            Automatico("pasta com nome EXATO em LocalAppData",
                "Discord", null, LocalAppData + @"\Discord", true);

            Automatico("pasta com nome EXATO em Roaming",
                "VLC", null, RoamingAppData + @"\VLC", true);

            // um ficheiro DENTRO da pasta exata tambem e' dele
            Automatico("ficheiro dentro da pasta exata",
                "Discord", null, LocalAppData + @"\Discord\Cache\data.db", true);

            // subpasta profunda
            Automatico("subpasta profunda da pasta exata",
                "Spotify", null, LocalAppData + @"\Spotify\Storage\blob1", true);

            // dentro da propria instalacao
            Automatico("ficheiro dentro da instalacao do programa",
                "Notepad", @"C:\Program Files\Notepad", @"C:\Program Files\Notepad\app.exe", true);

            // nome com espaco e acento
            Automatico("nome com espaco e acento bate exato",
                "Audacity Portable", null, LocalAppData + @"\Audacity Portable", true);

            // diferenca de caixa nao conta
            Automatico("caixa diferente ainda e' o mesmo nome",
                "discord", null, LocalAppData + @"\Discord", true);

            // ── NUNCA automatico ────────────────────────────────────────────
            Console.WriteLine();
            Console.WriteLine("== Casos que NUNCA podem ser automaticos ==");

            // o erro classico: Discord vs DiscordPTB / DiscordCanary
            Automatico("outro programa com nome parecido (DiscordPTB)",
                "Discord", null, LocalAppData + @"\DiscordPTB", false);

            Automatico("outro programa com nome parecido (DiscordCanary)",
                "Discord", null, LocalAppData + @"\DiscordCanary", false);

            // prefixo inverso: pedir "Cord" nao pode apanhar Discord
            Automatico("prefixo do nome pedido nao e' o mesmo programa",
                "Cord", null, LocalAppData + @"\Discord", false);

            // AppData de outra app sem relacao
            Automatico("AppData de programa sem relacao de nome",
                "Discord", null, LocalAppData + @"\Spotify", false);

            // Installer / Temp sao partilhados
            Automatico("pasta Temp partilhada",
                "Discord", null, LocalAppData + @"\Temp\instalador.exe", false);

            // ProgramData e partilhado por muitas apps -> classico Moderate
            Automatico("ProgramData partilhado",
                "Discord", @"C:\ProgramData\Discord", @"C:\ProgramData\Discord", false);

            // areas do sistema: nunca
            Automatico("Windows\\System32",
                "Discord", null, @"C:\Windows\System32\drivers\etc\hosts", false);

            Automatico("a propria pasta Windows",
                "Discord", null, @"C:\Windows", false);

            Automatico("Program Files partilhado",
                "Discord", null, @"C:\Program Files\Common Files\Discord", false);

            // raiz de disco: o teste que existia no Force Stop
            Automatico("raiz de unidade",
                "Discord", null, @"C:", false);

            Automatico("raiz de unidade com barra",
                "Discord", null, @"C:\", false);

            Automatico("pasta Users",
                "Discord", null, @"C:\Users", false);

            // entradas invalidas
            Automatico("string vazia", "Discord", null, "", false);
            Automatico("null", "Discord", null, null!, false);
            Automatico("nome do programa vazio", "", null, LocalAppData + @"\Discord", false);

            // instalacao de OUTRO programa nao pode ser apagada
            Automatico("instalacao de outro programa",
                "Discord", @"C:\Program Files\Discord", @"C:\Program Files\Spotify\blob", false);

            // a propria pasta de instalacao (exacta, sem barra) entra
            Automatico("pasta de instalacao do programa em si",
                "Notepad", @"C:\Program Files\Notepad", @"C:\Program Files\Notepad", true);

            // ── Separar() ──────────────────────────────────────────────────
            Console.WriteLine();
            Console.WriteLine("== Separar(): o que vai e o que fica ==");

            var entrada = new List<string>
            {
                LocalAppData + @"\Discord",                    // auto
                LocalAppData + @"\Discord\Cache\x.bin",        // auto (dentro da exacta)
                LocalAppData + @"\DiscordPTB",                 // fica
                LocalAppData + @"\Steam",                      // fica
                @"C:\ProgramData\Discord",                      // fica
                @"C:\Windows\System32\x.dll",                   // fica
            };
            var (auto, resto) = AutoCleanup.Separar("Discord", null, entrada);

            Checar("separou 2 automaticos", auto.Count == 2, $"obtidos={auto.Count}: {string.Join(" | ", auto)}");
            Checar("restaram 4 para decisao", resto.Count == 4, $"obtidos={resto.Count}");

            // Invariante real: NADA que reached o limiar pode ter ficado em "resto",
            // e nada que ficou em "resto" pode ter pontuacao >= limiar. Isto
            // vale para qualquer caminho, sem depender de nome/sufixo.
            Checar("nenhum automatico ficou no resto",
                resto.All(p => AutoCleanup.ConfiancaDe("Discord", null, p) < AutoCleanup.LimiarAutomatico),
                string.Join(" | ", resto.Where(p => AutoCleanup.ConfiancaDe("Discord", null, p) >= AutoCleanup.LimiarAutomatico)));

            // nenhum item com duvida escaped para o lado automatico
            Checar("nenhum item dudoso foi automatico",
                auto.All(p => AutoCleanup.ConfiancaDe("Discord", null, p) >= AutoCleanup.LimiarAutomatico),
                string.Join(" | ", auto));

            // a pasta EXATA tem de ter ido para o automatico (e nao para o resto)
            Checar("a pasta exacta foi parar ao automatico",
                auto.Any(p => p.Equals(LocalAppData + @"\Discord", StringComparison.OrdinalIgnoreCase)),
                string.Join(" | ", auto));

            // lista vazia / null nao rebenta
            var (a0, r0) = AutoCleanup.Separar("Discord", null, new List<string>());
            Checar("lista vazia devolve vazio", a0.Count == 0 && r0.Count == 0);
            var (a1, r1) = AutoCleanup.Separar("Discord", null, null!);
            Checar("null devolve vazio", a1.Count == 0 && r1.Count == 0);

            // ── coerencia com a configuracao ───────────────────────────────
            Console.WriteLine();
            Console.WriteLine("== Coerencia com as preferencias ==");

            DeepUninstallSettings.AutoCleanConfirmed = false;
            Checar("desligar o automatico persiste", !DeepUninstallSettings.AutoCleanConfirmed);
            DeepUninstallSettings.AutoCleanConfirmed = true;
            Checar("religar o automatico persiste", DeepUninstallSettings.AutoCleanConfirmed);

            Console.WriteLine();
            Console.WriteLine(new string('=', 62));
            Console.WriteLine($"RESULTADO: {_pass} OK / {_fail} FALHA");
            if (_fail > 0)
            {
                Console.WriteLine();
                foreach (var f in _falhas) Console.WriteLine("  - " + f);
            }
            Console.WriteLine(new string('=', 62));
            return _fail == 0 ? 0 : 1;
        }
    }
}