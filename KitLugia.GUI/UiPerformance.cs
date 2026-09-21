namespace KitLugia.GUI
{
    /// <summary>
    /// Chaves globais de performance da UI.
    /// PCs fracos (CPU antiga / SSD lento) travam na troca de abas por 3 motivos:
    /// 1) animacao de escala a cada navegacao (passada extra de layout + GPU),
    /// 2) EmptyWorkingSet SINCRONO no meio da troca (page-faults em disco lento),
    /// 3) GC.Collect + trim logo apos navegar (despagina a pagina que acabou de abrir).
    /// Centralizar aqui permite ligar/desligar sem varrer XAML.
    /// </summary>
    public static class UiPerformance
    {
        /// <summary>
        /// Quando false (padrao), MainFrame_Navigated nao anima nada: troca instantanea.
        /// A animacao de escala 0.98-&gt;1.0 custava um passe de layout + composicao
        /// concorrendo com a construcao da pagina nova.
        /// </summary>
        public static bool NavigationAnimationEnabled { get; set; } = false;

        /// <summary>
        /// Quanto tempo apos a navegacao o trim de RAM pode rodar (ms).
        /// Trocar de aba 2x rapido cancela o trim anterior: sem storm de trim.
        /// </summary>
        public static int PostNavTrimDelayMs { get; set; } = 4000;

        /// <summary>
        /// So devolve RAM se o WorkingSet estiver acima disso (MB).
        /// 90MB pegava uso normal e despaginava a pagina nova em SSD lento.
        /// </summary>
        public static long PostNavTrimThresholdMb { get; set; } = 140;
    }
}
