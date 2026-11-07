namespace KitLugia.GUI.Controls
{
    // Define os tipos de notificação disponíveis globalmente para a GUI
    public enum NotificationType
    {
        Info,
        Success,
        Error,
        // Neutro/cinza: ação informativa sem veredito (ex.: criar ponto de restauração).
        // Não é verde nem vermelho — só informa que o processo rodou.
        Neutral
    }
}