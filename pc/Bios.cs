// Lecture (sans risque) de la variable UEFI « Setup » du BIOS, pour savoir si Windows peut
// atteindre les verrous d'undervolt du GL704GW. Aucune écriture n'est faite ici : ce module
// sert uniquement à tester si le déblocage depuis Windows est possible avant d'aller plus loin.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PCMonitor;

public static class Bios
{
    // Variable Setup du BIOS AMI de l'ASUS GL704GW (vérifié dans le BIOS 312).
    const string SetupName = "Setup";
    const string SetupGuid = "{ec87d643-eba4-4bb5-a1e5-3f3e36b20da9}";

    // Adresses des verrous dans la variable Setup (extraites du BIOS 312, valeur par défaut 1 = activé).
    public const int OffCfgLock = 0x6ED;   // CFG Lock
    public const int OffOcLock  = 0x789;   // Overclocking Lock
    public const int OffVoltOff = 0x85B;   // Core Voltage Offset (undervolt natif)
    const int SetupSize = 5228;            // taille du varstore Setup

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern uint GetFirmwareEnvironmentVariableExW(string lpName, string lpGuid, byte[] pBuffer, uint nSize, out uint pdwAttribubtes);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool LookupPrivilegeValue(string host, string name, out long luid);

    // Pack=4 impératif : sinon le champ LUID (8 octets) serait aligné sur 8 et décalerait la structure.
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TokenPriv { public int Count; public uint LuidLow; public int LuidHigh; public uint Attr; }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPriv newState, int len, IntPtr prev, IntPtr prevLen);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr h);

    /// <summary>Donne au processus le privilège d'accès à l'environnement firmware (nécessite l'admin). Renvoie le code d'erreur Windows (0 = ok).</summary>
    static int EnablePrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 0x20 /*ADJUST*/ | 0x8 /*QUERY*/, out var token)) return Marshal.GetLastWin32Error();
        try
        {
            if (!LookupPrivilegeValue(null, "SeSystemEnvironmentPrivilege", out var luid)) return Marshal.GetLastWin32Error();
            var tp = new TokenPriv { Count = 1, LuidLow = (uint)luid, LuidHigh = (int)(luid >> 32), Attr = 0x2 /*ENABLED*/ };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            return Marshal.GetLastWin32Error(); // 0 = ok, 1300 = privilège absent (pas admin)
        }
        finally { CloseHandle(token); }
    }

    /// <summary>Lit la variable Setup. Renvoie l'octet lu ou null si Windows n'y a pas accès.</summary>
    static byte[] ReadSetup(out uint attributes, out int error)
    {
        attributes = 0; error = 0;
        var buf = new byte[8192];
        var n = GetFirmwareEnvironmentVariableExW(SetupName, SetupGuid, buf, (uint)buf.Length, out attributes);
        if (n == 0) { error = Marshal.GetLastWin32Error(); return null; }
        Array.Resize(ref buf, (int)n);
        return buf;
    }

    /// <summary>Test de lecture seule : Windows peut-il lire (et donc sans doute écrire) les verrous du BIOS ?</summary>
    public static Dictionary<string, object> Test()
    {
        var d = new Dictionary<string, object>();
        try
        {
            var priv = EnablePrivilege();
            if (priv == 1300)
            {
                // ERROR_NOT_ALL_ASSIGNED : le privilège n'est pas dans le jeton -> pas administrateur
                d["readable"] = false;
                d["reason"] = "PC Monitor n'a pas les droits administrateur. Ferme-le, puis relance-le en administrateur (clic droit sur PCMonitor.exe > Exécuter en tant qu'administrateur).";
                return d;
            }
            var data = ReadSetup(out var attr, out var err);
            if (data == null)
            {
                d["readable"] = false;
                // 1 = ERROR_INVALID_FUNCTION (BIOS refuse), 5 = refus, 203 = variable absente, 1314 = privilège non détenu
                d["reason"] = err switch
                {
                    1 => "Le BIOS n'autorise pas Windows à lire cette zone (variable réservée au démarrage). Le déblocage depuis Windows est impossible : il faudra la clé USB.",
                    5 => "Accès refusé par Windows (lance PC Monitor en administrateur).",
                    1314 => "PC Monitor n'a pas pu obtenir le droit d'accès au firmware. Relance-le en administrateur ; si ça persiste, il faudra la clé USB.",
                    203 => "Variable Setup introuvable à chaud : le déblocage depuis Windows est impossible, il faudra la clé USB.",
                    _ => $"Lecture impossible (code {err}) : le déblocage depuis Windows n'est pas possible, il faudra la clé USB.",
                };
                return d;
            }

            d["readable"] = true;
            d["size"] = data.Length;
            d["attr"] = attr;
            // 0x1 = non-volatile, 0x4 = accessible à l'exécution (runtime)
            bool nv = (attr & 0x1) != 0, rt = (attr & 0x4) != 0;
            int cfg = data.Length > OffCfgLock ? data[OffCfgLock] : -1;
            int oc  = data.Length > OffOcLock ? data[OffOcLock] : -1;
            int vo  = data.Length > OffVoltOff ? data[OffVoltOff] : -1;
            d["cfg_lock"] = cfg;
            d["oc_lock"] = oc;
            d["volt_offset"] = vo;
            d["locked"] = oc == 1 || cfg == 1;
            // écriture plausible si la variable est non-volatile et accessible à chaud
            bool writable = nv && rt && data.Length >= SetupSize;
            d["writable"] = writable;
            d["reason"] = writable
                ? (oc == 1 || cfg == 1
                    ? "Windows peut lire les verrous, et ils sont bien actifs. L'écriture depuis Windows est possible : je peux ajouter le bouton de déblocage."
                    : "Windows lit la zone et les verrous sont déjà désactivés. L'undervolt devrait fonctionner tel quel.")
                : "Windows peut lire la zone mais pas la réécrire en l'état : il faudra la clé USB pour débloquer.";
            return d;
        }
        catch (Exception e)
        {
            Log.Error("Test BIOS", e);
            d["readable"] = false;
            d["reason"] = "Test impossible (" + e.Message + ").";
            return d;
        }
    }
}
