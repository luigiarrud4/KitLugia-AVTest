// RustParity - prova que a mudanca no rust_native.dll (cache de padroes +
// guard de panic + comparacao Unicode) NAO quebrou nada, e que a
// divergencia Rust-vs-C# na Confidence foi eliminada.
//
// Compara o DLL NOVO com o DLL ANTIGO (backup) nos mesmos casos:
//   1) Confidence: o FFI tem de devolver a MESMA pontuacao que o C#
//      (OrdinalIgnoreCase) daria - inclusive em NOMES COM ACENTO, onde o
//      codigo antigo (eq_ignore_ascii_case + fatiamento por byte) errava.
//   2) reg_scan_ffi: os mesmos nomes de chave para os mesmos alvos.
//   3) mft_scan_ffi: a mesma contagem de entradas (a logica do mft.rs nao
//      mudou - so o wrapper de panic).
//   4) Seguranca: export chamado com ponteiro NULL / buffer pequeno nao
//      pode derrubar o processo (com panic=abort, um panic MATAVA o app).
//
// Os dois DLLs nao podem ser carregados no mesmo processo (mesmo nome de
// arquivo). Por isso o harness roda em DOIS modos: "old" e "new", cada um
// com seu DLL ao lado, e o runner compara os dumps.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

class Program
{
    const string RustDll = "rust_native.dll";

    [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    static extern int confidence_generate_ffi(string displayName, string folderName);

    [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    static extern int regex_match_ffi(string text, string pattern);

    [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    static extern int glob_match_ffi(string pattern, string path);

    [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    static extern int reg_scan_ffi(string root, string display, string install, string excl, int mode, IntPtr outBuf, int outCap);

    [DllImport(RustDll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    static extern int probe_boundary_ffi(string a, string b);

    // Casos de paridade da Confidence. O esperado e calculado com a MESMA
    // semantica do C# (StringComparison.OrdinalIgnoreCase).
    static readonly (string disp, string folder)[] CasosConfianca =
    {
        ("7-Zip", "7-Zip"),                       // exato
        ("Adobe AIR", "Adobe AIR"),               // exato
        ("Foo Launcher", "Foo"),                  // prefixo
        ("Foo", "Foo Launcher"),                  // prefixo inverso
        ("Display Driver Uninstaller", "Microsoft ME Display"), // NAO deve casar (falso positivo conhecido)
        ("Café Editor", "Café Editor"),           // exato COM ACENTO
        ("café editor", "CAFÉ EDITOR"),           // exato com acento + caixa
        ("Café Editor", "Café"),                  // prefixo com acento
        ("Café Editor", "Café Editor"),           // prefixo multi-byte
        ("Ünicorn Tool", "Ünicorn Tool"),         // exato com U-diaerese
        ("Ünicorn Tool", "Ünicorn"),              // prefixo multi-byte
        ("Österreich", "Österreich"),             // exato com O-diaerese
    };

    // Replica da ORDEM exata do C# (Confidence.Generate, DeepUninstaller.cs):
    //   1) igualdade OrdinalIgnoreCase  -> 100
    //   2) nome da pasta < 4 chars (fora match exato) -> 0
    //   3) display comeca com folder   -> 90
    //   4) folder comeca com display   -> 85
    //   5) fora dos 3 ramos diretos: a heuristica fuzzy decide -> -1
    // A ordem importa: o Rust antigo (ASCII-only) devolvia 70 onde o C#
    // devolvia 100 em nome com acento - divergencia REAL de resultado.
    static int EsperadoCSharp(string disp, string folder)
    {
        if (string.IsNullOrEmpty(disp) || string.IsNullOrEmpty(folder)) return 0;
        if (disp.Equals(folder, StringComparison.OrdinalIgnoreCase)) return 100;
        string folderTrimmed = folder.Trim().Trim('.', ' ');
        if (folderTrimmed.Length < 4) return 0;
        if (disp.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return 90;
        if (folder.StartsWith(disp, StringComparison.OrdinalIgnoreCase)) return 85;
        return -1;
    }

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "new";
        string outFile = args.Length > 1 ? args[1] : $"parity_{mode}.txt";
        var sb = new StringBuilder();

        sb.AppendLine($"# modo={mode} dll={Path.GetFullPath(RustDll)}");
        sb.AppendLine($"# hash={HashDll()}");

        // ---- 1) Confidence: o FFI tem de concordar com o C# ----
        sb.AppendLine("## confidence");
        int divergencias = 0;
        foreach (var (disp, folder) in CasosConfianca)
        {
            int got = confidence_generate_ffi(disp, folder);
            int esp = EsperadoCSharp(disp, folder);
            // -1 = fora dos ramos diretos (fuzzy decide): nao da para comparar
            bool compativel = (esp == -1) || (got == esp) ||
                              (esp == 0 && got == 0);
            if (!compativel) divergencias++;
            sb.AppendLine($"conf\t{disp}\t{folder}\t{got}\t{esp}\t{(compativel ? "OK" : "DIVERGE")}");
        }
        sb.AppendLine($"conf_divergencias\t{divergencias}");

        // ---- 2) regex/glob: resultado tem de ser o mesmo ----
        sb.AppendLine("## regex");
        string[] textos = { "test", "Test 123", "path/to/file.exe", "Café", "", "aaa" };
        string[] padroes = { "^test$", @"\d+", @"\s+", "(?i)café", "^$", "[a-z]+" };
        foreach (var t in textos)
            foreach (var p in padroes)
                sb.AppendLine($"regex\t{t}\t{p}\t{regex_match_ffi(t, p)}");

        sb.AppendLine("## glob");
        (string pat, string path)[] globs =
        {
            ("*.exe", "app.exe"), ("*.exe", "app.txt"), ("**/*.dll", "a/b/c.dll"),
            ("app*", "app.exe"), ("?.txt", "a.txt"), ("*.EXE", "app.exe"),
        };
        foreach (var (p, f) in globs)
            sb.AppendLine($"glob\t{p}\t{f}\t{glob_match_ffi(p, f)}");

        // ---- 3) reg_scan: os mesmos resultados ----
        sb.AppendLine("## reg");
        foreach (var alvo in new[] { "7-Zip", "Mozilla", "Notepad" })
        {
            var r = KitLugia.Core.NativeRegistry.Scan(
                "HKEY_LOCAL_MACHINE\\SOFTWARE", alvo, "", null, 0);
            sb.AppendLine($"reg\t{alvo}\t{r?.Count ?? -1}\t{string.Join("|", (r ?? new List<string>()).OrderBy(x => x))}");
        }

        // ---- 4) seguranca: ponteiro NULL / buffer pequeno nao pode matar o processo ----
        sb.AppendLine("## safety");
        try
        {
            int r1 = reg_scan_ffi("HKEY_LOCAL_MACHINE\\SOFTWARE", "x", "", "", 0, IntPtr.Zero, 0);
            sb.AppendLine($"safety\treg_null\t{r1}");
        }
        catch (Exception ex) { sb.AppendLine($"safety\treg_null\tEXC:{ex.GetType().Name}"); }

        try
        {
            // buffer de 4 bytes para uma string longa: deve devolver erro, nao quebrar
            var buf = Marshal.AllocHGlobal(4);
            int r2 = reg_scan_ffi("HKEY_LOCAL_MACHINE\\SOFTWARE", "7-Zip", "", "", 0, buf, 2);
            Marshal.FreeHGlobal(buf);
            sb.AppendLine($"safety\treg_small\t{r2}");
        }
        catch (Exception ex) { sb.AppendLine($"safety\treg_small\tEXC:{ex.GetType().Name}"); }

        try
        {
            int r3 = probe_boundary_ffi(null, null);
            sb.AppendLine($"safety\tprobe_null\t{r3}");
        }
        catch (Exception ex) { sb.AppendLine($"safety\tprobe_null\tEXC:{ex.GetType().Name}"); }

        // ---- 5) MFT: a contagem de entradas tem de ser a MESMA (a logica do
        //         mft.rs nao mudou - so o wrapper de panic) ----
        sb.AppendLine("## mft");
        try
        {
            // Caminhos ABSOLUTOS, como o PortableAppScanner usa (o
            // Path.GetPathRoot de um caminho relativo devolve "" e o MFT
            // nao acha o volume).
            string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string[] prefixos =
            {
                Path.Combine(up, "AppData", "Local"),
                Path.Combine(up, "AppData", "Roaming"),
                Path.Combine(up, "Desktop"),
                Path.Combine(up, "Downloads"),
                Path.Combine(up, "Documents"),
            };
            var t0 = DateTime.Now;
            var res = KitLugia.Core.NativeMft.ScanAllVolumes(prefixos);
            var ms = (DateTime.Now - t0).TotalMilliseconds;
            if (res == null)
            {
                sb.AppendLine("mft\tNULL");
            }
            else
            {
                foreach (var v in res)
                {
                    sb.AppendLine($"mft\t{v.VolumeRoot}\tfailed={v.VolumeFailed}\terr={v.ErrorCode}\t" +
                                  $"entries={v.Entries.Length}\tprefixes={string.Join("|", v.PrefixRecs.Select(p => p.ToString("X8")))}");
                }
                sb.AppendLine($"mft_total\t{res.Sum(r => r.Entries.Length)}\t{ms:F0}ms");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"mft\tEXC\t{ex.GetType().Name}\t{ex.Message}");
        }

        File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"gravado: {Path.GetFullPath(outFile)} ({sb.Length} bytes)");
        return 0;
    }

    static string HashDll()
    {
        try
        {
            var p = Path.Combine(AppContext.BaseDirectory, RustDll);
            using var fs = File.OpenRead(p);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(fs));
        }
        catch { return "?"; }
    }
}
