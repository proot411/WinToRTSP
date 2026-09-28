using System;
using System.Linq;
using System.Windows;
using WinToRTSP.Resources;

namespace WinToRTSP;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        IconGenerator.EnsureIconExists();

        bool startMinimized = e.Args.Any(a =>
            a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--headless", StringComparison.OrdinalIgnoreCase));

        var mainWindow = new MainWindow();

        if (startMinimized)
        {
            mainWindow.WindowState = WindowState.Minimized;
            mainWindow.Show();
            mainWindow.Hide();
        }
        else
        {
            mainWindow.Show();
        }
    }
}
