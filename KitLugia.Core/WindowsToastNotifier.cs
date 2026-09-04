using System;
using System.Runtime.Versioning;

namespace KitLugia.Core
{
    /// <summary>
    /// Envia notificações toast nativas do Windows (Action Center).
    /// Requer Windows 10+ e app registrado no SO.
    /// Fallback silencioso: se toast falhar, loga e retorna false.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static class WindowsToastNotifier
    {
        /// <summary>
        /// Mostra uma notificação toast nativa do Windows.
        /// </summary>
        /// <param name="title">Título da notificação</param>
        /// <param name="message">Corpo da mensagem</param>
        /// <returns>true se enviada com sucesso</returns>
        public static bool Show(string title, string message)
        {
            try
            {
                var toastXml = new Windows.Data.Xml.Dom.XmlDocument();
                toastXml.LoadXml($@"
<toast duration='long'>
    <visual>
        <binding template='ToastGeneric'>
            <text>{Esc(title)}</text>
            <text>{Esc(message)}</text>
        </binding>
    </visual>
    <audio src='ms-winsoundevent:Notification.Default'/>
</toast>");

                var notifier = GetNotifier();
                if (notifier == null)
                {
                    Logger.Log("⚠️ Toast notifier indisponível (app pode não estar registrado no SO)");
                    return false;
                }

                var toast = new Windows.UI.Notifications.ToastNotification(toastXml);
                toast.Tag = $"KitLugia_{DateTime.Now:yyyyMMdd_HHmm}";
                toast.Group = "KitLugiaUpdates";
                notifier.Show(toast);
                Logger.Log($"🔔 Toast Windows enviado: {title}");
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"⚠️ Falha ao enviar toast: {ex.Message}");
                return false;
            }
        }

        private static Windows.UI.Notifications.ToastNotifier? _notifier;

        private static Windows.UI.Notifications.ToastNotifier? GetNotifier()
        {
            if (_notifier != null) return _notifier;

            // Tentar 1: com AUMI do app
            try
            {
                _notifier = Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier("KitLugia");
                return _notifier;
            }
            catch { }

            // Tentar 2: sem AUMI (usa processo atual)
            try
            {
                _notifier = Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier();
                return _notifier;
            }
            catch (Exception ex)
            {
                Logger.Log($"⚠️ ToastNotifier init: {ex.Message}");
                return null;
            }
        }

        private static string Esc(string s) =>
            string.IsNullOrEmpty(s) ? "" :
            s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
             .Replace("\"", "&quot;").Replace("'", "&apos;");
    }
}
