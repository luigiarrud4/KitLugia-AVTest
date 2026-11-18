using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using KitLugia.Core;
using KitLugia.GUI.Helpers;

namespace KitLugia.GUI.Converters
{
    /// <summary>
    /// Ícone do item de inicialização para a coluna da GridStartup (ServicesPage).
    ///
    /// Ordem de tentativa:
    ///   1. StartupAppDetails.FilePath — executável resolvido pelo Core (variáveis expandidas + PATH);
    ///   2. StartupAppDetails.IconFilePath — candidata encontrada pelo Core na MESMA pasta do app
    ///      (.ico, .exe com o nome do app, .exe com o nome da pasta) quando o binário do item não serve;
    ///   3. ExePath cru do comando;
    ///   4. busca na pasta do comando (cobre a lista "fast", que não passa pelo enriquecimento);
    ///   5. ícone genérico do Windows — só em último caso.
    ///
    /// ProgramIconHelper já tem cache em memória + cache em disco PNG, então a conversão custa
    /// quase nada nas linhas já vistas; a virtualização do DataGrid só realiza as visíveis.
    /// </summary>
    public sealed class StartupIconConverter : IValueConverter
    {
        private static BitmapSource? _fallback;
        private static bool _fallbackLoaded;

        private static BitmapSource? Fallback
        {
            get
            {
                if (!_fallbackLoaded)
                {
                    _fallbackLoaded = true;
                    try { _fallback = ProgramIconHelper.GetGenericIcon(); }
                    catch { _fallback = null; }
                }
                return _fallback;
            }
        }

        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not StartupAppDetails app) return null;

            try
            {
                var icon = ProgramIconHelper.GetIconFromFiles(app.IconFilePath, app.FilePath, app.ExePath);
                if (icon != null) return icon;

                // Nada nas caminhos diretos: tenta a pasta do binário uma vez (cai no cache em disco
                // do ProgramIconHelper nas próximas vezes).
                string? pasta = PastaDoComando(app.FilePath, app.IconFilePath, app.ExePath);
                if (pasta != null)
                {
                    icon = ProgramIconHelper.GetIconFromDirectory(pasta);
                    if (icon != null) return icon;
                }

                return Fallback;
            }
            catch
            {
                return Fallback;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        /// <summary>Primeira pasta válida entre os caminhos conhecidos do item.</summary>
        private static string? PastaDoComando(params string?[] caminhos)
        {
            foreach (var c in caminhos)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                try
                {
                    string bruto = Environment.ExpandEnvironmentVariables(c.Trim().Trim('"', '\'', ' '));
                    int exeIdx = bruto.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                    if (exeIdx > 0) bruto = bruto[..(exeIdx + 4)].Trim('"', '\'', ' ');

                    string? dir = System.IO.Path.GetDirectoryName(bruto);
                    if (!string.IsNullOrWhiteSpace(dir) && System.IO.Directory.Exists(dir)) return dir;
                }
                catch { /* caminho inválido */ }
            }
            return null;
        }
    }
}
