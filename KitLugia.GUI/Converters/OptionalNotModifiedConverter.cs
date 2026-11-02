using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using KitLugia.Core;

namespace KitLugia.GUI.Converters
{
    /// <summary>
    /// OPCIONAL e MODIFICADO saem mutuamente exclusivos na tela do Integrity.
    ///
    /// POR QUE EXISTE: antes o selo "OPCIONAL" aparecia ao lado da categoria
    /// MESMO quando o item estava vermelho ("MODIFICADO"), fazendo o item
    /// vulnerável parecer apenas "opcional" e o botão virar "Desativar".
    /// Agora o selo OPCIONAL só aparece quando o item está no padrão seguro
    /// (OK). Se o item opcional foi alterado, o estado MODIFICADO fala por si
    /// só e o selo OPCIONAL some — eles nunca aparecem juntos.
    ///
    /// Valores de entrada (order importa): [0] IsOptional (bool), [1] Status (TweakStatus).
    /// </summary>
    public sealed class OptionalNotModifiedConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2) return Visibility.Collapsed;

            bool isOptional = values[0] is bool b && b;
            if (!isOptional) return Visibility.Collapsed;

            var status = values[1] as TweakStatus?;
            if (status == null) return Visibility.Collapsed;

            // OPCIONAL so aparece se o item NAO estiver modificado.
            if (status == TweakStatus.MODIFIED) return Visibility.Collapsed;

            return Visibility.Visible;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Monta o texto real do tooltip do botão de informação (ℹ️) de cada item
    /// de integridade. Substitui o MultiBinding quebrado que mostrava
    /// "System.Windows.Controls.TextBlock" cru ao passar o mouse.
    ///
    /// Recebe um ScannableTweak e devolve uma string pronta:
    ///   - a Description do check;
    ///   - o que o BOTÃO DE AÇÃO vai fazer exatamente (APLICAR / REVERTER /
    ///     CORRIGIR / Desativar), com chave, valor e destino da mudança;
    ///   - se PENDING, o motivo de aguardar reinício;
    ///   - se UNCONFIRMED, aviso de que não pôde ser confirmado.
    /// </summary>
    public sealed class TweakDescriptionTooltipConverter : IValueConverter
    {
        private const string Sep = "\n\n";

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var tweak = value as ScannableTweak;
            if (tweak == null) return string.Empty;

            var text = string.IsNullOrEmpty(tweak.Description)
                ? "Sem descrição disponível."
                : tweak.Description;

            text += Sep + BuildActionExplanation(tweak);

            if (!string.IsNullOrEmpty(tweak.UnconfirmedReason))
                text += Sep + "❔ NÃO CONFIRMADO — " + tweak.UnconfirmedReason + ".";

            if (tweak.Status == TweakStatus.PENDING)
                text += Sep + "⏳ AGUARDANDO REINÍCIO — " +
                        (tweak.PendingReason ?? "o Windows só aplica esta configuração na próxima inicialização") +
                        "." + "\nReinicie o PC para o check ser confirmado.";

            return text;
        }

        /// <summary>
        /// Explica, em texto, o que o botão de ação da linha vai fazer.
        /// O botão muda de rótulo conforme o estado (APLICAR / REVERTER /
        /// CORRIGIR / Desativar) e o usuário não tinha como saber o efeito
        /// real — só "REVERTER" não diz o que será restaurado.
        /// </summary>
        private static string BuildActionExplanation(ScannableTweak tweak)
        {
            bool isOptional = tweak.IsOptional;
            bool isModified = tweak.Status == TweakStatus.MODIFIED;

            string acao, efeito;

            if (isOptional && isModified)
            {
                acao = "🔧 REVERTER (este botão)";
                efeito = "Restaura o valor padrão do Windows neste item e desmarca a otimização. " +
                         "Nada mais é alterado. O item continua marcado como \"◇ OPCIONAL\" " +
                         "(itens opcionais não afetam o score de integridade).";
            }
            else if (isOptional)
            {
                acao = "🔧 CORRIGIR (este botão)";
                efeito = "Aplica a otimização opcional: o item passa a usar o valor configurado pelo KitLugia " +
                         "em vez do padrão do Windows. O item continua marcado como \"◇ OPCIONAL\" " +
                         "(não afeta o score) e o botão passa a mostrar REVERTER para desfazer.";
            }
            else if (isModified)
            {
                acao = "🔧 CORRIGIR (este botão)";
                efeito = "Grava o valor seguro/recomendado no item, desfazendo a alteração que deixou o sistema vulnerável. " +
                         "O check volta para \"✅ PROTEGIDO\".";
            }
            else
            {
                acao = "🔧 DESATIVAR (este botão)";
                efeito = "Grava o valor de otimização (o que deixa a proteção mais frouxa). " +
                         "O check passa a \"⚠️ MODIFICADO\" e o score de integridade cai. " +
                         "Use apenas se você quiser a otimização.";
            }

            var sb = new System.Text.StringBuilder();
            sb.Append(acao).Append(": ").Append(efeito);
            sb.Append(Sep).Append("Onde isso acontece:").Append('\n');
            sb.Append(DescribeTarget(tweak));

            // Ressalva real do Guardian: serviço desligado de propósito pelo
            // próprio KitLugia (toggle de telemetria/diagnóstico) NÃO é
            // reativado pelo Integrity — o reverter devolve a mensagem
            // "permanece desativado (intencional do KitLugia)".
            if (tweak.Type == TweakType.Service && isModified && isOptional)
            {
                sb.Append('\n').Append('\n')
                  .Append("ℹ️ Se este serviço foi desativado de propósito pelo KitLugia " +
                          "(toggles de telemetria/diagnóstico), o REVERTER mantém ele desativado — " +
                          "reative pelo toggle do Kit.");
            }

            return sb.ToString();
        }

        /// <summary>Descreve o alvo concreto da mudança (registro, serviço ou outro).</summary>
        private static string DescribeTarget(ScannableTweak tweak)
        {
            // PATH tem reparo DINAMICO: o Guardian re-diagnostica as entradas e
            // reconstrói a variável, não grava um valor fixo.
            if (tweak.IsPathItem)
            {
                return $"• Variável PATH: {tweak.KeyPath}" +
                       "\n• O reparo é dinâmico: duplicadas são removidas, inexistentes são " +
                       "descartadas e os caminhos essenciais do Windows (e os de programas " +
                       "instalados, como git/dotnet/npm) são garantidos." +
                       "\n• Reinicie o terminal para a variável recarregar.";
            }

            switch (tweak.Type)
            {
                case TweakType.Service:
                    return $"• Serviço do Windows: \"{tweak.ServiceName}\"" +
                           $"\n• Modo de inicialização — padrão do Windows: {Fmt(tweak.DefaultStartMode)}" +
                           $"\n• Modo de inicialização — valor da otimização: {Fmt(tweak.HarmfulStartMode)}" +
                           "\n• O serviço é reiniciado para assumir a nova configuração.";

                case TweakType.Bcd:
                    return $"• Configuração de boot (BCD): {tweak.ValueName}" +
                           $"\n• Valor — padrão do Windows: {Fmt(tweak.DefaultValue)}" +
                           $"\n• Valor — otimização/desativação: {Fmt(tweak.HarmfulValue)}" +
                           "\n• A alteração só entra em vigor no próximo boot.";

                case TweakType.PageFile:
                    return "• Configuração de memória (PagingFiles):" +
                           "\n• Padrão do Windows: página de troca gerenciada pelo sistema." +
                           "\n• Otimização: página de troca desativada (pode quebrar dumps e programas que exigem memória virtual).";

                case TweakType.Mouse:
                    return "• Configuração do mouse (HKCU\\Control Panel\\Mouse):" +
                           "\n• Padrão do Windows: aceleração e velocidade padrão." +
                           "\n• Otimização: aceleração desativada.";

                case TweakType.Registry:
                    return $"• Registro: {tweak.KeyPath}" +
                           $"\n• Valor: {tweak.ValueName}" +
                           $"\n• Valor — padrão do Windows: {Fmt(tweak.DefaultValue)}" +
                           $"\n• Valor — otimização/desativação: {Fmt(tweak.HarmfulValue)}";

                default:
                    return $"• Tipo de check: {tweak.Type}" +
                           $"\n• Alvo: {tweak.KeyPath ?? tweak.ValueName ?? tweak.Name}";
            }
        }

        private static string Fmt(object? v)
        {
            if (v == null) return "(padrão do Windows)";
            return v.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
