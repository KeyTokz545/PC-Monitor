using System;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace PCMonitor;

internal static class Program
{
    public const string Version = "2.13.3";

    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Global\PCMonitor-KeyTokz545", out bool first);
        // Passage de PCMonitor.exe à Keystone.exe : l'ancienne version encore lancée est fermée
        // pour que la nouvelle prenne sa place, sans rien avoir à faire à la main.
        if (!first && ReplaceOldVersion())
        {
            try { first = mutex.WaitOne(8000); }
            catch (AbandonedMutexException) { first = true; }
        }
        // relancé par une mise à jour : on attend que l'ancienne version ait fini de se fermer
        if (!first && WaitForPid(args))
        {
            try { first = mutex.WaitOne(15000); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            MessageBox.Show("Keystone est déjà lancé : son icône est près de l'horloge.", "Keystone",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); // page de code 850 de netsh
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (s, e) => Log.Error("Interface", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Log.Error("Erreur fatale", e.ExceptionObject as Exception);

        Log.Info($"Démarrage de Keystone {Version} ({string.Join(' ', args)})");
        Application.Run(new TrayApp(args));
        Log.Info("Arrêt de Keystone");
    }

    static bool WaitForPid(string[] args)
    {
        var i = Array.IndexOf(args, "--wait-pid");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            p.WaitForExit(15000);
        }
        catch { } // déjà fermée
        return true;
    }

    /// <summary>Ferme l'ancien PCMonitor.exe s'il tourne encore. Vrai si un processus a été fermé.</summary>
    static bool ReplaceOldVersion()
    {
        var closed = false;
        var me = Environment.ProcessId;
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("PCMonitor"))
        {
            try
            {
                if (p.Id == me) continue;
                p.Kill(true);
                p.WaitForExit(5000);
                closed = true;
            }
            catch { }
            finally { p.Dispose(); }
        }
        return closed;
    }
}
