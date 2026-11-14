// Pourquoi le processeur ou la carte graphique baisse sa fréquence (bridage) :
// processeur par ses registres « raisons de limitation » (comme ThrottleStop), carte NVIDIA par NVML.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace PCMonitor;

public static class Throttle
{
    const uint MSR_CORE_PERF_LIMIT_REASONS = 0x64F, IA32_PACKAGE_THERM_STATUS = 0x1B1;
    static int msrMode = -1; // -1 à tester, 0 indisponible, 1 = 0x64F, 2 = 0x1B1

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HandleFn(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ReasonsFn(IntPtr device, out ulong reasons);
    static bool nvTried;
    static ReasonsFn nvReasons;
    static IntPtr nvDevice;

    /// <summary>Codes : thermal (chauffe), power (limite de puissance), prochot (signal d'alerte externe),
    /// current (courant / alimentation), hw (alimentation de la carte graphique).</summary>
    public static Dictionary<string, object> Read(float? cpuTemp)
    {
        var cpu = new List<string>();
        var gpu = new List<string>();
        try { ReadCpu(cpu, cpuTemp); } catch { msrMode = 0; }
        try { ReadGpu(gpu); } catch { nvReasons = null; }
        return new Dictionary<string, object> { ["cpu"] = cpu, ["gpu"] = gpu };
    }

    static void ReadCpu(List<string> list, float? temp)
    {
        if (msrMode != 0 && Msr.Available)
        {
            if ((msrMode is -1 or 1) && Msr.Read(MSR_CORE_PERF_LIMIT_REASONS, out var v))
            {
                msrMode = 1;
                if ((v & 0x1) != 0) list.Add("prochot");
                if ((v & 0x2) != 0) list.Add("thermal");
                if ((v & ((1UL << 10) | (1UL << 11))) != 0) list.Add("power");
                if ((v & ((1UL << 6) | (1UL << 7) | (1UL << 8))) != 0) list.Add("current");
                return;
            }
            if ((msrMode is -1 or 2) && Msr.Read(IA32_PACKAGE_THERM_STATUS, out var t))
            {
                msrMode = 2;
                if ((t & 0x1) != 0) list.Add("thermal");
                if ((t & (1UL << 10)) != 0) list.Add("power");
                return;
            }
            msrMode = 0;
        }
        // sans accès aux registres : on se fie à la température (seuil d'origine 100 °C)
        if (temp >= 98) list.Add("thermal");
    }

    static void ReadGpu(List<string> list)
    {
        if (!nvTried)
        {
            nvTried = true;
            var paths = new[]
            {
                "nvml.dll",
                Path.Combine(Environment.SystemDirectory, "nvml.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
            };
            foreach (var p in paths)
            {
                if (!NativeLibrary.TryLoad(p, out var lib)) continue;
                try
                {
                    var init = Marshal.GetDelegateForFunctionPointer<InitFn>(NativeLibrary.GetExport(lib, "nvmlInit_v2"));
                    var handle = Marshal.GetDelegateForFunctionPointer<HandleFn>(NativeLibrary.GetExport(lib, "nvmlDeviceGetHandleByIndex_v2"));
                    var reasons = NativeLibrary.TryGetExport(lib, "nvmlDeviceGetCurrentClocksEventReasons", out var fn)
                        ? fn : NativeLibrary.GetExport(lib, "nvmlDeviceGetCurrentClocksThrottleReasons");
                    if (init() != 0 || handle(0, out nvDevice) != 0) continue;
                    nvReasons = Marshal.GetDelegateForFunctionPointer<ReasonsFn>(reasons);
                    Log.Info("Bridage GPU : NVML prêt (" + p + ")");
                    break;
                }
                catch (Exception e) { Log.Info("NVML : " + e.Message); }
            }
        }
        if (nvReasons == null || nvReasons(nvDevice, out var r) != 0) return;
        if ((r & 0x4) != 0) list.Add("power");                 // limite de puissance du pilote
        if ((r & (0x20 | 0x40)) != 0) list.Add("thermal");     // chauffe (logicielle ou matérielle)
        if ((r & (0x8 | 0x80)) != 0) list.Add("hw");           // ralentissement matériel / frein d'alimentation
    }
}
