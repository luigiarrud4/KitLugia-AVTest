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
            // O segundo valor e' o proprio ScannableTweak (empacotado no MultiBinding).
            if (values == null || values.Length < 2) return string.Empty;

            var tweak = values[1] as KitLugia.Core.ScannableTweak;
            if (tweak == null) return string.Empty;


            if (!string.IsNullOrEmpty(tweak.UnconfirmedReason))
                return Sep + "❔ NÃO CONFIRMADO — " + tweak.UnconfirmedReason;

            if (tweak.Status == KitLugia.Core.TweakStatus.PENDING)
                return Sep + "⏳ AGUARDANDO REINÍCIO — " +
                       (tweak.PendingReason ?? "o Windows só aplica esta configuração na próxima inicialização") +
                       "." + Sep +
                       "Reinicie o PC para o check ser confirmado.";

            return string.Empty;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}