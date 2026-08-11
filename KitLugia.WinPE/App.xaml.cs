using System.Windows;
using Application = System.Windows.Application;

namespace KitLugia.WinPE
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            if (WinPEDetector.IsWinPE())
                KitLugia.Core.Logger.Log("KitLugia.WinPE iniciado em ambiente WinPE/Windows");
            else
                KitLugia.Core.Logger.Log("KitLugia.WinPE iniciado em Windows normal (modo compatibilidade)");
        }
    }
}
