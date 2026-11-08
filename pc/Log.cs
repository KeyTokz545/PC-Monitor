using System;
using System.IO;
using System.Text;

namespace PCMonitor;

/// <summary>Journal texte dans %AppData%\PCMonitor\journal.txt (utile pour le dépannage).</summary>
internal static class Log
{
    static readonly object Gate = new object();

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCMonitor");
    public static string FilePath => Path.Combine(Dir, "journal.txt");

    public static void Info(string msg) => Write("INFO", msg);

    public static void Error(string where, Exception e) => Write("ERREUR", where + " : " + (e?.ToString() ?? "?"));

    static void Write(string level, string msg)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1_000_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch
        {
            // le journal ne doit jamais faire planter l'appli
        }
    }
}
