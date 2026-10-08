using System;
using System.IO;
using System.Reflection;

// ===========================================================================
// appguard - o scan de residuos nunca pode varrer uma RAIZ AMPLA
// ===========================================================================
// O caso reportado pelo utilizador: ao desinstalar o OneDrive, o scanner
// pegava em "C:\Program Files".
//
// Causa: ScanLeftoverFiles fazia
//     results.Add(installLocation);
//     Directory.EnumerateFiles(installLocation, AllDirectories) -> results.Add(f)
// SEM NENHUMA GUARDA. Se o Uninstall key devolvesse "C:\Program Files\X.exe",
// o GetDirectoryName dava "C:\Program Files" e a varredura enumerava o
// Program Files INTEIRO, metendo na lista o programa de todas as outras apps.
// Pior: IsProhibitedLocation compara o caminho EXACTO, por isso bloqueava
// "C:\Program Files" mas deixava passar "C:\Program Files\Mozilla\firefox.exe".
//
// Este teste tem DOIS lados, e o segundo e' o mais importante:
//   BLOQUEAR  -> raizes de shell e filhos diretos da raiz da unidade
//   DEIXAR PASSAR -> as pastas de instalacao REAIS de uma app. Se a guarda
//                    fosse demasiado agressiva, nenhuma desinstalacao limpava
//                    nada e o Kit deixava de servir para nada.
// ===========================================================================

internal static class Program
{
    static int _falhas = 0;

    static bool Bloquear(string rotulo, string caminho, bool esperado)
    {
        bool real;
        try { real = (bool)Chamar(caminho); }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FALHA] {rotulo}: '{caminho}' lancou {ex.GetType().Name}");
            _falhas++;
            return false;
        }

        bool ok = real == esperado;
        if (!ok) _falhas++;
        string accao = real ? "BLOQUEADO" : "deixado passar";
        string devia = esperado ? "BLOQUEADO" : "passar";
        Console.WriteLine($"  [{(ok ? "OK  " : "FALHA")}] {rotulo,-34} {caminho,-46} -> {accao} (devia {devia})");
        return ok;
    }

    static MethodInfo _m;
    static MethodInfo _m2;

    static object Chamar(string caminho)
    {
        _m ??= typeof(KitLugia.Core.DeepUninstaller).GetMethod("IsTooBroadAsInstallRoot",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("IsTooBroadAsInstallRoot nao existe - o nome mudou?");
        return _m.Invoke(null, new object[] { caminho });
    }

    // Defesa em profundidade da fase de limpeza.
    static bool ChamarPai(string caminho)
    {
        _m2 ??= typeof(KitLugia.Core.DeepUninstaller).GetMethod("IsDirectlyInsideCriticalRoot",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("IsDirectlyInsideCriticalRoot nao existe - o nome mudou?");
        return (bool)_m2.Invoke(null, new object[] { caminho });
    }

    static bool BloquearPai(string rotulo, string caminho, bool esperado)
    {
        bool real;
        try { real = ChamarPai(caminho); }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FALHA] {rotulo}: '{caminho}' lancou {ex.GetType().Name}");
            _falhas++;
            return false;
        }
        bool ok = real == esperado;
        if (!ok) _falhas++;
        Console.WriteLine($"  [{(ok ? "OK  " : "FALHA")}] {rotulo,-34} {caminho,-46} -> {(real ? "BLOQUEADO" : "deixado passar")} (devia {(esperado ? "BLOQUEADO" : "passar")})");
        return ok;
    }

    static string Pf => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    static string Pf86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    static string Pd => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    static string Raiz => Path.GetPathRoot(Win) ?? "C:\\";

    static int Main()
    {
        Console.WriteLine("=== appguard - raizes amplas nunca podem virar 'pasta de instalacao' ===");
        Console.WriteLine($"  ProgramFiles = {Pf}");
        Console.WriteLine($"  LocalAppData = {Local}");
        Console.WriteLine();

        Console.WriteLine("--- 1. TEM de bloquear (raizes de shell / filhos da raiz da unidade) ---");
        Bloquear("raiz da unidade", Raiz, true);
        Bloquear("filho direto da raiz", Path.Combine(Raiz, "Users"), true);
        Bloquear("Program Files", Pf, true);
        Bloquear("Program Files (x86)", Pf86, true);
        Bloquear("ProgramData", Pd, true);
        Bloquear("Windows", Win, true);
        Bloquear("Common Files dentro de PF", Path.Combine(Pf, "Common Files"), true);
        Bloquear("AppData do utilizador", Roaming, true);
        Bloquear("LocalAppData", Local, true);
        Bloquear("Programs dentro do LocalAppData", Path.Combine(Local, "Programs"), true);
        Bloquear("vazio", "", true);
        Bloquear("so espacos", "   ", true);
        Bloquear("lixo", @"C:\nao\existe\..\..\..", true);

        Console.WriteLine();
        Console.WriteLine("--- 2. TEM de DEIXAR PASSAR (pastas reais de uma app) ---");
        Bloquear("OneDrive (o caso real)", Path.Combine(Pf, "Microsoft OneDrive"), false);
        Bloquear("OneDrive sem subpasta", Pf + @"\OneDrive", false);
        Bloquear("Firefox x86", Path.Combine(Pf86, "Mozilla Firefox"), false);
        Bloquear("KitLugia", Path.Combine(Pf, "KitLugia"), false);
        Bloquear("app no LocalAppData", Path.Combine(Local, "Spotify"), false);
        Bloquear("app no Roaming", Path.Combine(Roaming, "discord"), false);
        Bloquear("pasta solta na raiz", Path.Combine(Raiz, "Minhapasta"), false);
        Bloquear("pasta de vendor na raiz", Path.Combine(Raiz, "AMD"), false);

        Console.WriteLine();
        Console.WriteLine("--- 3. Defesa em profundidade na limpeza (pai direto = raiz de sistema) ---");
        BloquearPai("ficheiro solto no Program Files", Path.Combine(Pf, "somefile.dll"), true);
        BloquearPai("ficheiro solto no AppData", Path.Combine(Roaming, "desktop.ini"), true);
        BloquearPai("ficheiro solto no Windows", Path.Combine(Win, "foo.dll"), true);
        BloquearPai("raiz da unidade", Raiz, true);
        BloquearPai("exe dentro de app no PF", Path.Combine(Pf, "Mozilla", "firefox.exe"), false);
        BloquearPai("fich dentro de app no Local", Path.Combine(Local, "Spotify", "data.db"), false);

        Console.WriteLine();
        Console.WriteLine(_falhas == 0
            ? "TODOS OS TESTES PASSARAM"
            : $"{_falhas} VERIFICACAO(OES) FALHARAM");
        return _falhas;
    }
}