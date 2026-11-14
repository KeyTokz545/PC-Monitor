using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace PCMonitor;

/// <summary>Petites fonctions Windows : clavier, alimentation, lancement de programmes.</summary>
public static class Win
{
    // ---------------------------------------------------------------- clavier
    [DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);

    public const byte VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    public const byte VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
    public const byte VK_MEDIA_NEXT = 0xB0, VK_MEDIA_PREV = 0xB1, VK_MEDIA_PLAY = 0xB3;
    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;

    /// <summary>Appuie sur une combinaison de touches (ex. Ctrl+Maj+Alt+F18).</summary>
    public static void Press(params byte[] keys)
    {
        foreach (var k in keys)
        {
            keybd_event(k, 0, 0, UIntPtr.Zero);
            Thread.Sleep(15);
        }
        for (int i = keys.Length - 1; i >= 0; i--)
        {
            keybd_event(keys[i], 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(15);
        }
    }

    /// <summary>Touche multimédia (volume, lecture…).</summary>
    public static void Media(byte vk)
    {
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    // ---------------------------------------------------------- alimentation
    [DllImport("user32.dll")]
    public static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool SetSuspendState([MarshalAs(UnmanagedType.U1)] bool hibernate,
        [MarshalAs(UnmanagedType.U1)] bool forceCritical, [MarshalAs(UnmanagedType.U1)] bool disableWakeEvent);

    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint Low; public int High; }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValue(string system, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES state, uint length, IntPtr previous, IntPtr returned);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    static void EnableShutdownPrivilege() => EnablePrivilege("SeShutdownPrivilege");

    /// <summary>Active un privilège du compte administrateur (désactivé par défaut dans le jeton).</summary>
    public static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x0028, out var token)) return false; // ADJUST_PRIVILEGES | QUERY
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 0x2 }; // SE_PRIVILEGE_ENABLED
            return AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero) && Marshal.GetLastWin32Error() == 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    public static void Sleep()
    {
        EnableShutdownPrivilege();
        if (!SetSuspendState(false, false, false))
            Run("rundll32.exe", "powrprof.dll,SetSuspendState 0,1,0", 3000);
    }

    /// <summary>Turbo Boost du processeur dans le plan d'alimentation Windows (secteur et batterie).</summary>
    public static void SetCpuBoost(int value)
    {
        const string sub = "54533251-82be-4824-96c1-47b60b740d00";   // SUB_PROCESSOR
        const string boost = "be337238-0d82-4146-a960-4f3749d470c7"; // PERFBOOSTMODE
        Run("powercfg", $"/attributes {sub} {boost} -ATTRIB_HIDE", 5000);
        Run("powercfg", $"/setacvalueindex SCHEME_CURRENT {sub} {boost} {value}", 5000);
        Run("powercfg", $"/setdcvalueindex SCHEME_CURRENT {sub} {boost} {value}", 5000);
        Run("powercfg", "/setactive SCHEME_CURRENT", 5000);
    }

    // ------------------------------------------------------------- processus
    /// <summary>Lance une commande sans fenêtre et renvoie son code de sortie et sa sortie brute.</summary>
    public static (int Code, byte[] Output) Run(string file, string args, int timeoutMs = 10000)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            using var ms = new MemoryStream();
            var copy = p.StandardOutput.BaseStream.CopyToAsync(ms);
            var err = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch { }
            }
            copy.Wait(2000);
            return (p.HasExited ? p.ExitCode : -1, ms.ToArray());
        }
        catch (Exception e)
        {
            Log.Error("Commande " + file, e);
            return (-1, Array.Empty<byte>());
        }
    }

    /// <summary>Texte de console : UTF-8 si valide, sinon page de code OEM (850 en France).</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return "";
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage).GetString(bytes); }
            catch { return Encoding.Latin1.GetString(bytes); }
        }
    }

    /// <summary>
    /// Ouvre un fichier, un raccourci ou un lien avec les droits normaux de l'utilisateur
    /// (PC Monitor tourne en administrateur, mais Steam ou Discord ne doivent pas).
    /// </summary>
    public static void OpenAsUser(string target)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = false });
    }

    static readonly Regex UriLike = new(@"^[a-z][a-z0-9+.\-]*:(?!\\)", RegexOptions.IgnoreCase);
    static readonly Regex ExeWithArgs = new(@"^""?([^""]+?\.exe)""?(?:\s+(.*))?$", RegexOptions.IgnoreCase);

    public static void OpenApp(string command)
    {
        var cmd = Environment.ExpandEnvironmentVariables((command ?? "").Trim());
        if (cmd.Length == 0) throw new InvalidOperationException("commande vide");
        if (File.Exists(cmd) || Directory.Exists(cmd) || UriLike.IsMatch(cmd))
        {
            OpenAsUser(cmd);
            return;
        }
        var m = ExeWithArgs.Match(cmd);
        if (m.Success && File.Exists(m.Groups[1].Value))
        {
            var exe = m.Groups[1].Value;
            var args = m.Groups[2].Value.Trim();
            OpenAsUser(args.Length == 0 ? exe : MakeShortcut(exe, args));
            return;
        }
        // nom court d'un programme de Windows (notepad, taskmgr…)
        Process.Start(new ProcessStartInfo(cmd) { UseShellExecute = true });
    }

    /// <summary>Crée (une fois) un raccourci .lnk pour lancer « programme + arguments » sans droits admin.</summary>
    static string MakeShortcut(string exe, string args)
    {
        var dir = Path.Combine(Log.Dir, "raccourcis");
        Directory.CreateDirectory(dir);
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(exe + "|" + args)))[..12];
        var path = Path.Combine(dir, hash + ".lnk");
        if (!File.Exists(path))
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(type);
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = exe;
            link.Arguments = args;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.Save();
        }
        return path;
    }

    public static void CloseApp(string processName)
    {
        Run("taskkill", $"/IM \"{processName}\" /T /F", 8000);
    }

    public static bool IsRunning(string exeName)
    {
        var name = Path.GetFileNameWithoutExtension(exeName);
        try { return Process.GetProcessesByName(name).Length > 0; }
        catch { return false; }
    }
}
