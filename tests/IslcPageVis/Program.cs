// IslcPageVis - instancia a TraySettingsPage REAL para provar que:
//  1) o XAML novo parseia (Border nao aceita 2 filhos, x:Name, estilos)
//  2) os controles novos existem e tem o tipo certo
//  3) UpdateIslcStatus() roda com leitura real e escreve no badge
// Sem TrayService nao ha tray: a pagina degrada com seguranca (o codigo
// checa tray == null), entao o badge nao preenche - o que verificamos aqui
// e o PARSE e a ligacao, nao o numero.
using System;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using KitLugia.GUI.Pages;

class Program
{
    [STAThread]
    static int Main()
    {
        int fail = 0, ok = 0;
        void Check(string what, bool cond, string detail = "")
        {
            if (cond) { ok++; Console.WriteLine($"  OK   {what}{(detail.Length > 0 ? "  (" + detail + ")" : "")}"); }
            else { fail++; Console.WriteLine($"  FAIL {what}{(detail.Length > 0 ? "  (" + detail + ")" : "")}"); }
        }

        Console.WriteLine("IslcPageVis - parse do XAML + ligacao dos controles");
        Console.WriteLine(new string('=', 62));

        var app = new Application();

        // A pagina usa StaticResource do App.xaml (AccentColor, SubtleCardStyle...).
        // Sem o Generic.xaml em Application.Resources o XAML rebenta - mesmo
        // quirk documentado no AGENTS.md. Carrega o dicionario do proprio
        // assembly compilado em vez de reimplementar os estilos.
        int mergedAntes = app.Resources.MergedDictionaries.Count;
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KitLugia.GUI;component/Themes/Generic.xaml", UriKind.Absolute)
            });
            Check("Generic.xaml carregado no Application.Resources",
                  app.Resources.MergedDictionaries.Count > mergedAntes,
                  $"{app.Resources.MergedDictionaries.Count} dicionario(s) mesclado(s)");
        }
        catch (Exception ex)
        {
            Check("Generic.xaml carregado no Application.Resources", false, ex.GetType().Name + ": " + ex.Message);
        }

        TraySettingsPage page = null;
        try
        {
            page = new TraySettingsPage();
            Check("TraySettingsPage instanciou (XAML parseou)", true);
        }
        catch (Exception ex)
        {
            Check("TraySettingsPage instanciou (XAML parseou)", false, ex.GetType().Name + ": " + ex.Message);
            Console.WriteLine(ex.ToString());
            return 1;
        }

        // Controles novos do painel ISLC
        var nomes = new[]
        {
            "TxtIslcThreshold", "TxtIslcFreeThreshold", "TxtIslcMinInterval",
            "ChkIslcRespectForeground", "PnlIslcSettings", "TxtIslcLastAction",
            "TxtIslcBadge", "IslcStatusBadge", "ChkStandbyClean"
        };
        foreach (var n in nomes)
        {
            var f = typeof(TraySettingsPage).GetField(n,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Check($"controle '{n}' existe", f != null, f?.FieldType.Name ?? "ausente");
        }

        // Tipos especificos
        var panel = Find(page, "PnlIslcSettings") as StackPanel;
        Check("PnlIslcSettings e StackPanel", panel != null);
        var badge = Find(page, "TxtIslcBadge") as TextBlock;
        Check("TxtIslcBadge e TextBlock", badge != null);

        // O painel de parametros comeca oculto (so aparece com o toggle ligado)
        Check("PnlIslcSettings inicia Collapsed",
              panel != null && panel.Visibility == Visibility.Collapsed,
              panel?.Visibility.ToString() ?? "-");

// Os 3 TextBoxes de parametro nao tem valor aqui: GetTrayService() devolve null
// neste harness (nao ha MainWindow), entao o LoadSettings retorna antes de
// escreve-los. Isso e o comportamento defensivo correto. O que vale testar
// e a VALIDACAO do LostFocus: com lixo digitado ele nao pode estourar nem
// gravar o lixo.
foreach (var n in new[] { "TxtIslcThreshold", "TxtIslcFreeThreshold", "TxtIslcMinInterval" })
{
    var tb = Find(page, n) as TextBox;
    Check($"{n} existe como TextBox", tb != null);

    string antes = tb?.Text ?? "";
    try
    {
        tb.Text = "abc"; // lixo proposital
        var h = typeof(TraySettingsPage).GetMethod(n + "_LostFocus",
            BindingFlags.NonPublic | BindingFlags.Instance);
        h?.Invoke(page, new object[] { tb, null });
        Check($"{n}_LostFocus aguenta entrada invalida sem excecao", true,
              $"'{antes}' -> '{tb.Text}' (tray=null, nada gravado)");
    }
    catch (Exception ex)
    {
        Check($"{n}_LostFocus aguenta entrada invalida sem excecao", false, ex.GetType().Name + ": " + ex.Message);
    }
}

        // UpdateIslcStatus com leitura real: sem TrayService ele retorna cedo,
        // mas nao pode estourar - e o badge nao pode virar lixo/null.
        try
        {
            typeof(TraySettingsPage).GetMethod("UpdateIslcStatus",
                BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(page, null);
            Check("UpdateIslcStatus() executou sem excecao", true);
            Check("TxtIslcBadge.Foreground nao e null apos Update",
                  badge?.Foreground != null);
        }
        catch (Exception ex)
        {
            Check("UpdateIslcStatus() executou sem excecao", false, ex.GetType().Name + ": " + ex.Message);
        }

        // ── CALCULO AUTOMATICO (pedido 24/10: o usuario nao digita mais nada) ──
        // Os tres limites saem da RAM instalada: standby = RAM/8, livre = RAM/16.
        Console.WriteLine("\n-- ISLC automatico --");
        var chkAuto = Find(page, "ChkIslcAuto") as CheckBox;
        Check("ChkIslcAuto existe (caixa 'Calcular automaticamente')", chkAuto != null,
              chkAuto?.IsChecked?.ToString() ?? "ausente");

        long ramMB = 0;
        try
        {
            var st = new KitLugia.GUI.Services.Win32Api.MEMORYSTATUSEX();
            if (KitLugia.GUI.Services.Win32Api.GlobalMemoryStatusEx(st))
                ramMB = (long)(st.ullTotalPhys / (1024 * 1024));
        }
        catch { }
        Check("RAM total lida para a formula", ramMB > 0, ramMB + " MB");

        long esperadoStandby = Math.Clamp(ramMB / 8, 1024, 16384);
        long esperadoLivre = Math.Clamp(ramMB / 16, 512, 4096);
        int esperadoIntervalo = ramMB <= 16 * 1024 ? 60 : ramMB <= 64 * 1024 ? 30 : 15;

        var svc = new KitLugia.GUI.Services.TrayIconService();
        Check("um TrayIconService novo ja nasce com o standby calculado",
              svc.IslcThresholdMB == esperadoStandby,
              $"{svc.IslcThresholdMB} MB (esperado {esperadoStandby} para {ramMB} MB de RAM)");
        Check("o limite de RAM livre tambem nasce calculado",
              svc.IslcFreeThresholdMB == esperadoLivre,
              $"{svc.IslcFreeThresholdMB} MB (esperado {esperadoLivre})");
        Check("o intervalo minimo tambem nasce calculado",
              svc.IslcMinIntervalSeconds == esperadoIntervalo,
              $"{svc.IslcMinIntervalSeconds}s (esperado {esperadoIntervalo}s)");
        Check("o modo automatico e' o padrao", svc.IslcAutoTune, svc.IslcAutoTune.ToString());

        // Valores antigos digitados a mao (ex.: 4065/1024, do log real) sao substituidos
        svc.IslcThresholdMB = 9999;
        svc.IslcFreeThresholdMB = 111;
        svc.IslcMinIntervalSeconds = 3;
        svc.ApplyIslcAutoTuning();
        Check("ApplyIslcAutoTuning recalcula os tres valores",
              svc.IslcThresholdMB == esperadoStandby && svc.IslcFreeThresholdMB == esperadoLivre &&
              svc.IslcMinIntervalSeconds == esperadoIntervalo,
              $"{svc.IslcThresholdMB}/{svc.IslcFreeThresholdMB}/{svc.IslcMinIntervalSeconds}");

        // Desligado = respeita o valor manual
        svc.IslcAutoTune = false;
        svc.IslcThresholdMB = 777;
        svc.ApplyIslcAutoTuning();
        Check("com o automatico DESLIGADO o valor manual e' respeitado", svc.IslcThresholdMB == 777,
              svc.IslcThresholdMB + " MB");
        svc.IslcAutoTune = true;

        // UI: os campos aparecem preenchidos e TRAVADOS no modo automatico
        try
        {
            svc.ApplyIslcAutoTuning();
            typeof(TraySettingsPage).GetMethod("RefreshIslcFields",
                BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(page, new object[] { svc });
            var t1 = Find(page, "TxtIslcThreshold") as TextBox;
            var t2 = Find(page, "TxtIslcFreeThreshold") as TextBox;
            var t3 = Find(page, "TxtIslcMinInterval") as TextBox;
            Check("os campos mostram os valores calculados",
                  t1?.Text == esperadoStandby.ToString(),
                  $"standby='{t1?.Text}' livre='{t2?.Text}' intervalo='{t3?.Text}'");
            Check("com o automatico ligado os campos ficam SOMENTE LEITURA",
                  t1?.IsReadOnly == true && t2?.IsReadOnly == true && t3?.IsReadOnly == true,
                  $"{t1?.IsReadOnly}/{t2?.IsReadOnly}/{t3?.IsReadOnly}");
        }
        catch (Exception ex)
        {
            Check("RefreshIslcFields executou sem excecao", false, ex.GetType().Name + ": " + ex.Message);
        }

        // Sem TrayService na janela, o toggle nao pode gravar nem estourar
        try
        {
            typeof(TraySettingsPage).GetMethod("ChkIslcAuto_Changed",
                BindingFlags.NonPublic | BindingFlags.Instance)?.Invoke(page, new object[] { chkAuto, null });
            Check("ChkIslcAuto_Changed aguenta tray ausente", true, "sem excecao");
        }
        catch (Exception ex)
        {
            Check("ChkIslcAuto_Changed aguenta tray ausente", false, ex.GetType().Name + ": " + ex.Message);
        }

        // Handlers do XAML existem no code-behind (Click/LostFocus nao pode estar orfao)
        foreach (var h in new[] { "TxtIslcThreshold_LostFocus", "TxtIslcFreeThreshold_LostFocus",
                                  "TxtIslcMinInterval_LostFocus", "ChkIslcRespectForeground_Changed",
                                  "ChkIslcAuto_Changed" })
        {
            var m = typeof(TraySettingsPage).GetMethod(h, BindingFlags.NonPublic | BindingFlags.Instance);
            Check($"handler {h} existe", m != null);
        }

        Console.WriteLine(new string('=', 62));
        Console.WriteLine($"RESULTADO: {ok} OK / {fail} FALHA");
        Environment.Exit(fail == 0 ? 0 : 1);
        return fail == 0 ? 0 : 1;
    }

    static object Find(object root, string name)
    {
        var f = root.GetType().GetField(name,
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        return f?.GetValue(root);
    }
}