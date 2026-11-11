using System;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PCMonitor;

internal static class Program
{
    public const string Version = "2.6";

    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Global\PCMonitor-KeyTokz545", out bool first);
        if (!first)
        {
            MessageBox.Show("PC Monitor est déjà lancé : son icône est près de l'horloge.", "PC Monitor",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // page de code 850 de netsh
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (s, e) => Log.Error("Interface", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Erreur fatale", e.ExceptionObject as Exception);

        Log.Info($"Démarrage de PC Monitor {Version} ({string.Join(' ', args)})");
        Application.Run(new TrayApp(args));
        Log.Info("Arrêt de PC Monitor");
    }
}
