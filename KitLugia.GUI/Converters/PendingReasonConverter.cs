using System;
using System.Globalization;
using System.Windows.Data;

namespace KitLugia.GUI.Converters
{
    /// <summary>
    /// Monta a segunda parte do tooltip do item de Integridade: o motivo do
    /// estado especial (PENDING).
    ///
    /// POR QUE EXISTE: o tooltip mostrava só a Description, e um item amarelo
    /// "AGUARDANDO REINÍCIO" sem explicar por quê parece bug. Aqui o texto diz
    /// o que está acontecendo de fato:
    ///   PENDING      -> o que falta (reiniciar)
    ///   fora desses  -> string vazia (o tooltip fica igual ao de antes)
    /// </summary>
    public sealed class PendingReasonConverter : IMultiValueConverter
    {
        private const string Sep = "\n\n";

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            {
                // O primeiro valor e' a Description; o segundo e' o proprio ScannableTweak.
                if (values == null || values.Length < 2) return string.Empty;

                var description = values[0] as string ?? string.Empty;
                var tweak = values[1] as KitLugia.Core.ScannableTweak;

                string pending = string.Empty;
                if (tweak != null)
                {
                    if (!string.IsNullOrEmpty(tweak.UnconfirmedReason))
                        pending = Sep + "❔ NÃO CONFIRMADO — " + tweak.UnconfirmedReason;
                    else if (tweak.Status == KitLugia.Core.TweakStatus.PENDING)
                        pending = Sep + "⏳ AGUARDANDO REINÍCIO — " +
                               (tweak.PendingReason ?? "o Windows só aplica esta configuração na próxima inicialização") +
                               "." + Sep +
                               "Reinicie o PC para o check ser confirmado.";
                }

                return description + pending;
            }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}