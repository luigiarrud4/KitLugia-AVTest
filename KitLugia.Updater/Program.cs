using System;
using System.Windows;

namespace KitLugia.Updater;

public class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var app = new Application();
        var window = new MainWindow(args);
        app.Run(window);
    }
}
