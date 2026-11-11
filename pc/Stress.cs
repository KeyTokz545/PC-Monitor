// Test de stabilité de l'undervolt : charge AVX/FMA sur tous les fils avec vérification des calculs,
// erreurs matérielles WHEA de Windows, températures et ralentissements, puis un verdict.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using System.Threading;

namespace PCMonitor;

public sealed class StressTest
{
    readonly AppConfig cfg;
    readonly Collector collector;
    readonly object gate = new();
    static readonly string Marker = Path.Combine(Log.Dir, "test-stabilite.json");

    CancellationTokenSource cts;
    Timer monitor;
    DateTime start;
    int duration;
    long errors, iterations;
    int whea, seconds, thermalSecs, powerSecs, clockSamples;
    float tempMax;
    double clockSum, powerSum;
    int powerSamples;
    string profile;
    int uvCore;
    Dictionary<string, object> result;

    public StressTest(AppConfig cfg, Collector collector)
    {
        this.cfg = cfg;
        this.collector = collector;
        CheckCrash();
    }

    public bool Running => cts != null;

    /// <summary>Un test était en cours quand le PC s'est arrêté : c'est un plantage (ou un redémarrage forcé).</summary>
    void CheckCrash()
    {
        try
        {
            if (!File.Exists(Marker)) return;
            var info = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Marker));
            File.Delete(Marker);
            result = new Dictionary<string, object>
            {
                ["verdict"] = "crash",
                ["title"] = "Le PC s'est arrêté pendant le test",
                ["detail"] = "Plantage ou redémarrage pendant le test : ce réglage n'est pas stable. Remonte l'undervolt de 10 à 20 mV (moins négatif) puis refais le test.",
                ["profile"] = info.TryGetValue("profile", out var p) ? p.GetString() : null,
                ["core"] = info.TryGetValue("core", out var c) ? c.GetInt32() : 0,
                ["at"] = info.TryGetValue("start", out var s) ? s.GetString() : null,
            };
            Log.Info("Test de stabilité interrompu par un arrêt du PC");
        }
        catch (Exception e)
        {
            Log.Error("Lecture du test de stabilité précédent", e);
        }
    }

    public (bool Ok, string Message) Start(int minutes, string activeProfile, int core)
    {
        lock (gate)
        {
            if (Running) return (false, "Un test est déjà en cours");
            minutes = Math.Clamp(minutes, 1, 60);
            duration = minutes * 60;
            start = DateTime.Now;
            errors = iterations = 0;
            whea = seconds = thermalSecs = powerSecs = clockSamples = powerSamples = 0;
            tempMax = 0;
            clockSum = powerSum = 0;
            profile = activeProfile;
            uvCore = core;
            result = null;
            try
            {
                File.WriteAllText(Marker, JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["profile"] = profile, ["core"] = core, ["start"] = start.ToString("dd/MM HH:mm"),
                }));
            }
            catch { }
            cts = new CancellationTokenSource();
            var token = cts.Token;
            Current = this;
            for (int i = 0; i < Environment.ProcessorCount; i++)
                new Thread(() => Work(token)) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Test stabilité" }.Start();
            monitor = new Timer(_ => Tick(), null, 1000, 1000);
            Log.Info($"Test de stabilité : {minutes} min, profil {CpuTuner.ProfileLabel(profile)}, undervolt {core} mV, {Environment.ProcessorCount} fils, AVX2/FMA {(Fma.IsSupported ? "oui" : "non")}");
            return (true, $"Test lancé pour {minutes} min : le PC va chauffer, c'est normal");
        }
    }

    public (bool Ok, string Message) Stop() => Finish(stoppedByUser: true);

    // ---------------------------------------------------------------- charge de calcul
    static void Work(CancellationToken token)
    {
        var a = new double[512];
        var b = new double[512];
        for (int i = 0; i < a.Length; i++)
        {
            a[i] = 1.0 + (i % 17) * 1e-7;
            b[i] = 1.0 - (i % 13) * 1e-7;
        }
        long reference = BitConverter.DoubleToInt64Bits(Kernel(a, b));
        while (!token.IsCancellationRequested)
        {
            var r = BitConverter.DoubleToInt64Bits(Kernel(a, b));
            if (r != reference) Interlocked.Increment(ref Current.errors);
            Interlocked.Increment(ref Current.iterations);
        }
    }

    /// <summary>Le test en cours, pour les fils de calcul.</summary>
    static StressTest Current;

    static double Kernel(double[] a, double[] b)
    {
        if (Fma.IsSupported && Avx.IsSupported)
        {
            var acc0 = Vector256<double>.Zero;
            var acc1 = Vector256<double>.Zero;
            var acc2 = Vector256<double>.Zero;
            var acc3 = Vector256<double>.Zero;
            ReadOnlySpan<Vector256<double>> va = MemoryMarshal.Cast<double, Vector256<double>>(a);
            ReadOnlySpan<Vector256<double>> vb = MemoryMarshal.Cast<double, Vector256<double>>(b);
            for (int rep = 0; rep < 400; rep++)
                for (int i = 0; i + 3 < va.Length; i += 4)
                {
                    acc0 = Fma.MultiplyAdd(va[i], vb[i], acc0);
                    acc1 = Fma.MultiplyAdd(va[i + 1], vb[i + 1], acc1);
                    acc2 = Fma.MultiplyAdd(va[i + 2], vb[i + 2], acc2);
                    acc3 = Fma.MultiplyAdd(va[i + 3], vb[i + 3], acc3);
                }
            var s = Avx.Add(Avx.Add(acc0, acc1), Avx.Add(acc2, acc3));
            return s.GetElement(0) + s.GetElement(1) + s.GetElement(2) + s.GetElement(3);
        }
        double sum = 0;
        for (int rep = 0; rep < 400; rep++)
            for (int i = 0; i < 512; i++)
                sum = Math.FusedMultiplyAdd(a[i], b[i], sum);
        return sum;
    }

    // ---------------------------------------------------------------- surveillance
    void Tick()
    {
        try
        {
            seconds = (int)(DateTime.Now - start).TotalSeconds;
            if (collector.CpuTemp is float t && t > tempMax) tempMax = t;
            if (collector.CpuPower is float p) { powerSum += p; powerSamples++; }
            if (Msr.Available)
            {
                if (Msr.Read(0x198, out var ps))
                {
                    var ratio = (ps >> 8) & 0xFF;
                    if (ratio > 0) { clockSum += ratio / 10.0; clockSamples++; }
                }
                if (Msr.Read(0x1B1, out var th))
                {
                    if ((th & 1) == 1) thermalSecs++;
                    if (((th >> 10) & 1) == 1) powerSecs++;
                }
            }
            if (seconds % 30 == 0) whea = CountWhea();
            if (seconds >= duration) Finish(stoppedByUser: false);
        }
        catch (Exception e)
        {
            Log.Error("Surveillance du test de stabilité", e);
        }
    }

    /// <summary>Erreurs matérielles corrigées ou non signalées par Windows depuis le début du test.</summary>
    int CountWhea()
    {
        try
        {
            var ms = (long)(DateTime.Now - start).TotalMilliseconds + 5000;
            var q = $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and TimeCreated[timediff(@SystemTime) <= {ms}]]]";
            var text = Win.Decode(Win.Run("wevtutil", $"qe System /q:\"{q}\" /f:text /c:200", 15000).Output) ?? "";
            int n = 0, i = 0;
            while ((i = text.IndexOf("Event[", i, StringComparison.Ordinal)) >= 0) { n++; i += 6; }
            return n;
        }
        catch
        {
            return whea;
        }
    }

    (bool, string) Finish(bool stoppedByUser)
    {
        CancellationTokenSource c;
        lock (gate)
        {
            if (cts == null) return (false, "Aucun test en cours");
            c = cts;
            cts = null;
        }
        c.Cancel();
        monitor?.Dispose();
        whea = CountWhea();
        try { File.Delete(Marker); } catch { }
        var full = seconds >= duration - 1;
        string verdict, title, detail;
        if (errors > 0 || whea > 0)
        {
            verdict = "bad";
            title = "Instable";
            detail = (errors > 0 ? $"{errors} erreur(s) de calcul" : "") + (errors > 0 && whea > 0 ? " et " : "") + (whea > 0 ? $"{whea} erreur(s) matérielle(s) signalée(s) par Windows" : "") +
                     ". Remonte l'undervolt de 10 à 20 mV (moins négatif) puis refais le test.";
        }
        else if (!full)
        {
            verdict = "partial";
            title = "Test arrêté avant la fin";
            detail = $"Aucune erreur pendant {Since(seconds)}, mais le test n'est pas allé au bout : relance-le pour être sûr.";
        }
        else
        {
            verdict = "ok";
            title = "Stable";
            detail = $"Aucune erreur en {Since(seconds)} de charge maximale. Tu peux garder ce réglage" +
                     (uvCore > -150 ? ", ou essayer 10 mV de plus si tu veux encore baisser la chauffe." : ".");
        }
        result = new Dictionary<string, object>
        {
            ["verdict"] = verdict,
            ["title"] = title,
            ["detail"] = detail,
            ["profile"] = profile,
            ["core"] = uvCore,
            ["seconds"] = seconds,
            ["errors"] = errors,
            ["whea"] = whea,
            ["temp_max"] = tempMax > 0 ? (object)Math.Round(tempMax) : null,
            ["clock_avg"] = clockSamples > 0 ? (object)Math.Round(clockSum / clockSamples, 2) : null,
            ["power_avg"] = powerSamples > 0 ? (object)Math.Round(powerSum / powerSamples) : null,
            ["thermal_s"] = thermalSecs,
            ["power_s"] = powerSecs,
            ["at"] = start.ToString("dd/MM HH:mm"),
        };
        Log.Info($"Test de stabilité terminé : {title} ({seconds} s, {errors} erreurs, {whea} WHEA, {tempMax:0} °C max)");
        return (true, stoppedByUser ? "Test arrêté" : $"Test terminé : {title}");
    }

    static string Since(int s) => s >= 60 ? $"{s / 60} min{(s % 60 > 0 ? $" {s % 60} s" : "")}" : $"{s} s";

    public Dictionary<string, object> State()
    {
        lock (gate)
        {
            if (Running)
                return new Dictionary<string, object>
                {
                    ["running"] = true,
                    ["seconds"] = seconds,
                    ["duration"] = duration,
                    ["errors"] = Interlocked.Read(ref errors),
                    ["whea"] = whea,
                    ["temp_max"] = tempMax > 0 ? (object)Math.Round(tempMax) : null,
                    ["clock"] = clockSamples > 0 ? (object)Math.Round(clockSum / clockSamples, 2) : null,
                    ["thermal_s"] = thermalSecs,
                    ["power_s"] = powerSecs,
                    ["profile"] = profile,
                    ["core"] = uvCore,
                };
            return new Dictionary<string, object> { ["running"] = false, ["result"] = result };
        }
    }

}
