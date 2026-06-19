using System.Windows.Controls;

namespace KitLugia.GUI.Extensions
{
    /// <summary>
    /// Métodos de extensão para ScrollViewer
    /// </summary>
    public static class ScrollViewerExtensions
    {
        /// <summary>
        /// Rola o ScrollViewer até o final
        /// </summary>
        public static void ScrollToBottom(this ScrollViewer scrollViewer)
        {
            if (scrollViewer == null) return;
            
            scrollViewer.ScrollToEnd();
        }
    }
}
