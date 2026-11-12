using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PCMonitor;

/// <summary>
/// Carte graphique NVIDIA : verrouillage de fréquence (nvidia-smi) et décalage de courbe
/// fréquence/tension (NVAPI). Le décalage cœur positif combiné à un plafond de fréquence
/// donne un undervolt : la fréquence plafonnée est atteinte à une tension plus basse.
/// </summary>
public static class Gpu
{
    static bool? supported;
    static int maxClock = -1;

    public static bool Supported => supported ??= Detect();

    static bool Detect()
    {
        var (code, output) = Win.Run("nvidia-smi", "--query-gpu=name --format=csv,noheader", 8000);
        var ok = code == 0 && Win.Decode(output).Trim().Length > 0;
        Log.Info("nvidia-smi : " + (ok ? Win.Decode(output).Trim() : "indisponible"));
        return ok;
    }

    /// <summary>Fréquence maximale d'usine du GPU (MHz), pour borner le réglage.</summary>
    public static int MaxClock()
    {
        if (maxClock > 0 || !Supported) return maxClock;
        var (code, output) = Win.Run("nvidia-smi", "--query-gpu=clocks.max.graphics --format=csv,noheader,nounits", 8000);
        if (code == 0 && int.TryParse(Win.Decode(output).Trim().Split('\n')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 300)
            maxClock = v;
        return maxClock;
    }

    /// <summary>Bride la fréquence (MHz) ou rend la main au pilote (mhz &lt;= 0). Renvoie (réussi, message).</summary>
    public static (bool Ok, string Message) SetLimit(int mhz)
    {
        if (!Supported) return (false, "Carte graphique NVIDIA introuvable (nvidia-smi)");
        var args = mhz > 0 ? $"-lgc 0,{mhz}" : "-rgc";
        var (code, output) = Win.Run("nvidia-smi", args, 15000);
        var text = Win.Decode(output).Trim();
        Log.Info($"nvidia-smi {args} : {code} {text}");
        if (code != 0)
            return (false, text.Length > 0 && text.Length < 160 ? "NVIDIA : " + text : "Le pilote NVIDIA a refusé le réglage");
        return (true, mhz > 0 ? $"GPU limité à {mhz} MHz" : "GPU en fréquence normale");
    }

    // ---------------------------------------------------------------- NVAPI (décalage de courbe)

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    static extern IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumGpusDelegate([Out] IntPtr[] handles, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int Pstates20Delegate(IntPtr gpu, byte[] info);

    const uint ID_INIT = 0x0150E828, ID_ENUM = 0xE5AC921F, ID_GET_P20 = 0x6FF81213, ID_SET_P20 = 0x0F4DAE6B;
    // Domaines d'horloge publics NVAPI
    const uint DOMAIN_GPU = 0, DOMAIN_MEM = 4;
    // Taille du NV_GPU_PERF_PSTATES20_INFO : V2 (avec section OV) et V1 (sans)
    const int SIZE_V2 = 7416, SIZE_V1 = 7316;

    static bool nvInit, nvTried;
    static IntPtr nvGpu = IntPtr.Zero;
    static Pstates20Delegate setP20, getP20;
    public static string NvError { get; private set; }

    /// <summary>Le pilote NVIDIA autorise-t-il les décalages de fréquence (NVAPI dispo + GPU trouvé) ?</summary>
    public static bool TuneSupported
    {
        get { EnsureNvapi(); return nvInit && nvGpu != IntPtr.Zero && setP20 != null; }
    }

    static T Fn<T>(uint id) where T : Delegate
    {
        var p = QueryInterface(id);
        return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(p);
    }

    static void EnsureNvapi()
    {
        if (nvTried) return;
        nvTried = true;
        try
        {
            var init = Fn<InitDelegate>(ID_INIT);
            if (init == null) { NvError = "NVAPI indisponible"; return; }
            var r = init();
            if (r != 0) { NvError = $"NVAPI init {r}"; return; }
            nvInit = true;
            var enumGpus = Fn<EnumGpusDelegate>(ID_ENUM);
            setP20 = Fn<Pstates20Delegate>(ID_SET_P20);
            getP20 = Fn<Pstates20Delegate>(ID_GET_P20);
            if (enumGpus == null || setP20 == null) { NvError = "NVAPI incomplet"; return; }
            var handles = new IntPtr[64];
            if (enumGpus(handles, out var count) == 0 && count > 0) nvGpu = handles[0];
            else NvError = "Aucun GPU NVIDIA via NVAPI";
            Log.Info($"NVAPI : init ok, {count} GPU, réglage {(nvGpu != IntPtr.Zero ? "dispo" : "indispo")}");
        }
        catch (DllNotFoundException) { NvError = "Pilote NVIDIA sans NVAPI (nvapi64.dll absent)"; }
        catch (Exception e) { NvError = "NVAPI : " + e.Message; Log.Error("NVAPI init", e); }
    }

    static uint Ver(int size, int v) => (uint)(size | (v << 16));

    /// <summary>Construit un NV_GPU_PERF_PSTATES20_INFO réglant le décalage d'un domaine sur le P-state P0.</summary>
    static byte[] BuildOffset(int size, int v, uint domain, int offsetKHz)
    {
        var b = new byte[size];
        void U32(int off, uint val) => BitConverter.GetBytes(val).CopyTo(b, off);
        void S32(int off, int val) => BitConverter.GetBytes(val).CopyTo(b, off);
        U32(0, Ver(size, v));   // version
        U32(8, 1);              // numPstates
        U32(12, 1);             // numClocks
        U32(16, 0);             // numBaseVoltages
        // pstates[0] @ 20 : pstateId=P0(0), bIsEditable=0
        U32(20, 0);
        // clocks[0] @ 28 : domainId, typeId=0 (single), bIsEditable=0, freqDelta.value @40
        U32(28, domain);
        U32(32, 0);
        S32(40, offsetKHz);
        return b;
    }

    static int SetOne(uint domain, int offsetMHz)
    {
        var r = setP20(nvGpu, BuildOffset(SIZE_V2, 2, domain, offsetMHz * 1000));
        if (r == -190 /* INCOMPATIBLE_STRUCT_VERSION */)
            r = setP20(nvGpu, BuildOffset(SIZE_V1, 1, domain, offsetMHz * 1000));
        return r;
    }

    /// <summary>Applique les décalages de fréquence cœur et mémoire (MHz). Renvoie (réussi, message).</summary>
    public static (bool Ok, string Message) SetOffsets(int coreMhz, int memMhz)
    {
        EnsureNvapi();
        if (!TuneSupported) return (false, NvError ?? "Décalage GPU indisponible");
        try
        {
            var rc = SetOne(DOMAIN_GPU, coreMhz);
            var rm = SetOne(DOMAIN_MEM, memMhz);
            Log.Info($"NVAPI décalage : cœur {coreMhz:+#;-#;0} MHz (r={rc}), mémoire {memMhz:+#;-#;0} MHz (r={rm})");
            if (rc != 0 || rm != 0)
                return (false, $"Le pilote NVIDIA a refusé le décalage (codes {rc}/{rm})");
            return (true, $"Décalage appliqué : cœur {coreMhz:+#;-#;0} MHz, mémoire {memMhz:+#;-#;0} MHz");
        }
        catch (Exception e)
        {
            Log.Error("NVAPI décalage", e);
            return (false, "Erreur NVAPI : " + e.Message);
        }
    }

    /// <summary>Remet les décalages à zéro.</summary>
    public static (bool Ok, string Message) ClearOffsets() => SetOffsets(0, 0);
}
