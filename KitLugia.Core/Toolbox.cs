using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace KitLugia.Core
{
    [SupportedOSPlatform("windows")]
    public static partial class Toolbox
    {
        // --- Expressões Regulares Corrigidas para Máxima Compatibilidade ---
        // A funcionalidade é idêntica, mas esta forma evita erros de compilação
        // caso o gerador de código fonte do .NET 7+ não esteja ativo.

        internal static Regex PowerPlanNameRegex() => new Regex(@"\(([^)]+)\)");

        internal static Regex GuidRegex() => new Regex(@"([a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12})", RegexOptions.IgnoreCase);

        internal static Regex DriverSubKeyRegex() => new Regex(@"^\d{4}$");

        // Este arquivo continua sendo o ponto de entrada da classe parcial 'Toolbox'.
        // A lógica de cada funcionalidade está separada nos seus respectivos arquivos "Manager".
    }
}