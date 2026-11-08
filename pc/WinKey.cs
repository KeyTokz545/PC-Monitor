// Blocage de la touche Windows pendant le jeu (évite de sortir du jeu par erreur) et raccourcis de capture.
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PCMonitor;

public static class WinKey
{
    const int WH_KEYBOARD_LL = 13;
    const uint WM_QUIT = 0x0012;
    const int LLKHF_INJECTED = 0x10;
    const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public int vkCode; public int scanCode; public int flags; public int time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string name);

    static readonly object Gate = new();
    static readonly HookProc Proc = Hook; // gardé en vie pour le ramasse-miettes
    static IntPtr hook;
    static uint hookThread;

    public static bool Enabled { get; private set; }

    static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // les touches simulées par PC Monitor (raccourcis de capture) passent
            if ((k.vkCode == VK_LWIN || k.vkCode == VK_RWIN) && (k.flags & LLKHF_INJECTED) == 0) return (IntPtr)1;
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    /// <summary>Active ou désactive le blocage. Le crochet tourne sur son propre fil pour ne jamais ralentir le clavier.</summary>
    public static bool Set(bool on)
    {
        lock (Gate)
        {
            if (on == Enabled) return true;
            if (on)
            {
                var ready = new ManualResetEventSlim();
                var t = new Thread(() =>
                {
                    hookThread = GetCurrentThreadId();
                    hook = SetWindowsHookEx(WH_KEYBOARD_LL, Proc, GetModuleHandle(null), 0);
                    var err = Marshal.GetLastWin32Error();
                    ready.Set();
                    if (hook == IntPtr.Zero)
                    {
                        Log.Info($"Blocage touche Windows impossible (erreur {err})");
                        return;
                    }
                    while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
                    UnhookWindowsHookEx(hook);
                    hook = IntPtr.Zero;
                }) { IsBackground = true, Name = "Touche Windows" };
                t.Start();
                ready.Wait(3000);
                Enabled = hook != IntPtr.Zero;
            }
            else
            {
                if (hookThread != 0) PostThreadMessage(hookThread, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                hookThread = 0;
                Enabled = false;
            }
            Log.Info("Touche Windows " + (Enabled ? "bloquée" : "débloquée"));
            return Enabled == on;
        }
    }

    // ---------------------------------------------------------------- captures
    [DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);

    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2;
    const byte VK_MENU = 0x12, VK_SNAPSHOT = 0x2C, VK_G = 0x47, VK_R = 0x52, VK_F1 = 0x70, VK_F9 = 0x78, VK_F10 = 0x79;

    static void Combo(params byte[] keys)
    {
        static uint Ext(byte k) => k is (byte)VK_LWIN or VK_SNAPSHOT ? KEYEVENTF_EXTENDEDKEY : 0;
        foreach (var k in keys)
        {
            keybd_event(k, 0, Ext(k), UIntPtr.Zero);
            Thread.Sleep(20);
        }
        for (int i = keys.Length - 1; i >= 0; i--)
        {
            keybd_event(keys[i], 0, Ext(keys[i]) | KEYEVENTF_KEYUP, UIntPtr.Zero);
            Thread.Sleep(20);
        }
    }

    /// <summary>Capture d'écran, clip des 30 dernières secondes ou enregistrement (Xbox Game Bar ou NVIDIA).</summary>
    public static (bool Ok, string Message) Capture(string what, string tool)
    {
        bool nvidia = tool == "nvidia";
        switch (what)
        {
            case "shot":
                if (nvidia) Combo(VK_MENU, VK_F1); else Combo((byte)VK_LWIN, VK_MENU, VK_SNAPSHOT);
                return (true, "Capture d'écran");
            case "clip":
                if (nvidia) Combo(VK_MENU, VK_F10); else Combo((byte)VK_LWIN, VK_MENU, VK_G);
                return (true, "Clip des derniers instants enregistré");
            case "rec":
                if (nvidia) Combo(VK_MENU, VK_F9); else Combo((byte)VK_LWIN, VK_MENU, VK_R);
                return (true, "Enregistrement démarré ou arrêté");
        }
        return (false, "Capture inconnue");
    }
}
