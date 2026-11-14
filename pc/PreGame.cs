// Bouton « Avant de jouer » : ferme les programmes choisis, vide la mémoire en cache de Windows
// (liste « en attente », source de saccades quand elle grossit), puis met la ventilation et le profil CPU choisis.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace PCMonitor;

public static class PreGame
{
    const int SystemMemoryListInformation = 80;
    const int MemoryPurgeStandbyList = 4;

    [DllImport("ntdll.dll")] static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
    [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returned);

    static readonly object Gate = new();

    public static (bool Ok, string Message) Run(AppConfig cfg, ModeService modes, CpuTuner cpu)
    {
        if (!Monitor.TryEnter(Gate)) return (false, "Déjà en cours…");
        try
        {
            var done = new List<string>();
            var s = cfg.PreGame;

            // 1. programmes à fermer : d'abord proprement (comme la croix), puis de force si demandé
            List<string> names;
            lock (AppConfig.Gate) names = s.Close.Select(GameProfile.Norm).Where(n => n.Length > 0).Distinct().ToList();
            if (names.Count > 0)
            {
                var procs = names.SelectMany(n => Process.GetProcessesByName(n)).ToList();
                var wanted = procs.Select(p => p.ProcessName.ToLowerInvariant()).Distinct().ToList();
                foreach (var p in procs)
                    try { if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow(); } catch { }
                var until = DateTime.Now.AddSeconds(3);
                while (DateTime.Now < until && procs.Any(Alive)) Thread.Sleep(200);
                if (s.Force)
                    foreach (var p in procs.Where(Alive))
                        try { p.Kill(true); } catch { }
                Thread.Sleep(300);
                var closed = wanted.Count(n => Process.GetProcessesByName(n).Length == 0);
                foreach (var p in procs) p.Dispose();
                if (wanted.Count > 0) done.Add(closed == 1 ? "1 appli fermée" : $"{closed} applis fermées");
            }

            // 2. mémoire en cache
            if (s.Ram)
            {
                var mb = PurgeStandby();
                if (mb >= 0) done.Add(mb >= 1024 ? $"{mb / 1024.0:0.0} Go de cache vidé" : $"{mb} Mo de cache vidé");
            }

            // 3. ventilation et profil CPU
            var labels = new List<string>();
            if (ModeDefs.Known(s.Mode))
            {
                if (s.Mode != cfg.LastMode) modes.Apply(s.Mode);
                labels.Add(cfg.ModeLabel(s.Mode));
            }
            if (cfg.Cpu.Enabled && ModeDefs.Known(s.Cpu))
            {
                if (s.Cpu != cfg.Cpu.Active) cpu.SetProfile(s.Cpu);
                labels.Add("CPU " + CpuTuner.ProfileLabel(s.Cpu));
            }
            if (labels.Count > 0) done.Add(string.Join(" + ", labels));

            Osd.Toggle("Avant de jouer", "Prêt à jouer", true);
            Log.Info("Avant de jouer : " + string.Join(", ", done));
            return (true, done.Count > 0 ? "Prêt à jouer : " + string.Join(", ", done) : "Prêt à jouer");
        }
        catch (Exception e)
        {
            Log.Error("Avant de jouer", e);
            return (false, "Avant de jouer : " + e.Message);
        }
        finally
        {
            Monitor.Exit(Gate);
        }
    }

    static bool Alive(Process p)
    {
        try { return !p.HasExited; } catch { return false; }
    }

    /// <summary>Taille de la liste « en attente » en Mo (pages gardées en cache par Windows).</summary>
    static long StandbyMb()
    {
        // ZeroPageCount, FreePageCount, ModifiedPageCount, ModifiedNoWritePageCount, BadPageCount,
        // PageCountByPriority[8], RepurposedPagesByPriority[8], ModifiedPageCountPageFile
        const int count = 5 + 8 + 8 + 1;
        var buf = Marshal.AllocHGlobal(count * IntPtr.Size);
        try
        {
            if (NtQuerySystemInformation(SystemMemoryListInformation, buf, count * IntPtr.Size, out _) != 0) return -1;
            long pages = 0;
            for (var i = 5; i < 13; i++) pages += Marshal.ReadIntPtr(buf, i * IntPtr.Size).ToInt64();
            return pages * Environment.SystemPageSize / (1024 * 1024);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>Vide la liste « en attente ». Renvoie les Mo libérés, ou -1 si Windows refuse.</summary>
    static long PurgeStandby()
    {
        Win.EnablePrivilege("SeProfileSingleProcessPrivilege");
        var before = StandbyMb();
        var cmd = MemoryPurgeStandbyList;
        var st = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int));
        if (st != 0)
        {
            Log.Info($"Vidage de la mémoire en cache refusé (0x{st:X8})");
            return -1;
        }
        var after = StandbyMb();
        return before >= 0 && after >= 0 ? Math.Max(0, before - after) : 0;
    }
}
