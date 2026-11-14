// Touches dédiées : Pause/Attn et Impr. écran, seules (sans combinaison), changent un réglage
// et l'annonce s'affiche à l'écran. Les combinaisons (Win + Impr. écran, Alt + Impr. écran…) restent normales.
using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PCMonitor;

public static class HotKeys
{
    const int WH_KEYBOARD_LL = 13;
    const uint WM_QUIT = 0x0012;
    const int LLKHF_INJECTED = 0x10;
    const int VK_PAUSE = 0x13, VK_SNAPSHOT = 0x2C;
    public static readonly string[] Choices = { "none", "mode", "cpu", "mic", "pregame" };

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT { public int vkCode; public int scanCode; public int flags; public int time; public IntPtr extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);

    static readonly object Gate = new();
    static readonly HookProc Proc = Hook; // gardé en vie pour le ramasse-miettes
    static IntPtr hook;
    static uint hookThread;
    static readonly bool[] held = new bool[2], passed = new bool[2];
    static AppConfig cfg;
    static ModeService modes;
    static CpuTuner cpu;
    static Asus asus;

    /// <summary>Lance « Avant de jouer » (fourni par TrayApp).</summary>
    public static Func<(bool Ok, string Message)> PreGame { get; set; }

    public static void Init(AppConfig c, ModeService m, CpuTuner p, Asus a)
    {
        cfg = c;
        modes = m;
        cpu = p;
        asus = a;
        Apply();
    }

    static string Action(int vk) => vk == VK_PAUSE ? cfg?.Keys?.Pause : cfg?.Keys?.Print;

    /// <summary>Le crochet clavier ne tourne que si au moins une touche a une action.</summary>
    public static void Apply()
    {
        if (cfg == null) return;
        var need = Choices.Skip(1).Contains(cfg.Keys.Pause) || Choices.Skip(1).Contains(cfg.Keys.Print);
        Set(need);
    }

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    static bool Modifiers() => Down(0x11) || Down(0x12) || Down(0x5B) || Down(0x5C) || Down(0x10); // Ctrl, Alt, Win, Maj

    static IntPtr Hook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((k.vkCode == VK_PAUSE || k.vkCode == VK_SNAPSHOT) && (k.flags & LLKHF_INJECTED) == 0)
            {
                var what = Action(k.vkCode);
                var i = k.vkCode == VK_PAUSE ? 0 : 1;
                var msg = (int)wParam;
                var down = msg == 0x100 || msg == 0x104;
                // combinaison (Win + Impr. écran…) : la touche garde son rôle normal, appui et relâchement
                if (down && !held[i]) passed[i] = Modifiers();
                var mine = what != null && what != "none" && !passed[i];
                if (!down && passed[i]) passed[i] = false;
                if (mine)
                {
                    if (down)
                    {
                        if (!held[i]) Task.Run(() => Run(what)); // pas de répétition si la touche reste enfoncée
                        held[i] = true;
                    }
                    else
                    {
                        // Impr. écran n'envoie parfois que le relâchement
                        if (!held[i]) Task.Run(() => Run(what));
                        held[i] = false;
                    }
                    return (IntPtr)1; // la touche ne fait rien d'autre
                }
            }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    static string Next(string[] list, string current)
    {
        var i = Array.IndexOf(list, current);
        return list[(i + 1) % list.Length];
    }

    static void Run(string what)
    {
        try
        {
            switch (what)
            {
                case "mode":
                {
                    var list = cfg.Modes.Where(ModeDefs.Known).ToArray();
                    if (list.Length == 0) return;
                    var next = Next(list, cfg.LastMode);
                    if (modes.Apply(next).Ok) Osd.Mode(next);
                    break;
                }
                case "cpu":
                {
                    if (!cfg.Cpu.Enabled) { Osd.Toggle("Profil CPU", "Non activé", false); return; }
                    var next = Next(ModeDefs.Order, cfg.Cpu.Active);
                    if (cpu.SetProfile(next).Ok) Osd.CpuProfile(next);
                    break;
                }
                case "mic":
                {
                    var muted = Audio.ToggleMic();
                    if (asus.Connected) asus.Set(Asus.MicMuteLed, muted ? 1 : 0);
                    Osd.Toggle("Micro", muted ? "Coupé" : "Activé", !muted, muted ? Osd.Red : Osd.Green);
                    break;
                }
                case "pregame":
                    PreGame?.Invoke();
                    break;
            }
        }
        catch (Exception e)
        {
            Log.Error("Touche dédiée", e);
        }
    }

    static void Set(bool on)
    {
        lock (Gate)
        {
            var enabled = hook != IntPtr.Zero;
            if (on == enabled) return;
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
                        Log.Info($"Touches dédiées indisponibles (erreur {err})");
                        return;
                    }
                    while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
                    UnhookWindowsHookEx(hook);
                    hook = IntPtr.Zero;
                }) { IsBackground = true, Name = "Touches dédiées" };
                t.Start();
                ready.Wait(3000);
            }
            else
            {
                if (hookThread != 0) PostThreadMessage(hookThread, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                hookThread = 0;
            }
        }
    }
}
