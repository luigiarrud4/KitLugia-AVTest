using System.Windows;
// Resolve ambiguidade
using Application = System.Windows.Application;

namespace KitLugia.GUI.Helpers
{
    /// <summary>
    /// Helper estático para navegação padronizada entre páginas.
    /// </summary>
    public static class NavigationHelper
    {
        /// <summary>
        /// Navega para uma página específica usando PageType.
        /// </summary>
        public static void NavigateTo(PageType pageType)
        {
            if (Application.Current.MainWindow is MainWindow mw)
            {
                mw.NavigateToPage(pageType);
            }
        }

        /// <summary>
        /// Navega para uma página específica usando PageType com tabIndex.
        /// </summary>
        public static void NavigateTo(PageType pageType, int tabIndex)
        {
            if (Application.Current.MainWindow is MainWindow mw)
            {
                mw.NavigateToPage(pageType, tabIndex);
            }
        }

        /// <summary>
        /// Navega para uma página específica usando tag emoji (legado).
        /// </summary>
        public static void NavigateTo(string tag)
        {
            if (Application.Current.MainWindow is MainWindow mw)
            {
                mw.NavigateToPage(tag);
            }
        }
    }
}
