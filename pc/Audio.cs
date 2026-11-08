using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace PCMonitor;

/// <summary>Son : volume, coupure, micro et choix de la sortie audio.</summary>
public static class Audio
{
    static readonly object Gate = new object();

    static MMDevice Default(DataFlow flow)
    {
        var en = new MMDeviceEnumerator();
        try { return en.GetDefaultAudioEndpoint(flow, Role.Multimedia); }
        catch { return null; }
    }

    public static Dictionary<string, object> State()
    {
        lock (Gate)
        {
            var state = new Dictionary<string, object>();
            try
            {
                using var output = Default(DataFlow.Render);
                if (output != null)
                {
                    state["volume"] = (int)Math.Round(output.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
                    state["muted"] = output.AudioEndpointVolume.Mute;
                    state["output"] = output.ID;
                }
                using var mic = Default(DataFlow.Capture);
                state["mic"] = mic != null;
                state["mic_muted"] = mic?.AudioEndpointVolume.Mute ?? false;

                var en = new MMDeviceEnumerator();
                state["outputs"] = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                    .Select(d =>
                    {
                        try { return new Dictionary<string, object> { ["id"] = d.ID, ["name"] = d.FriendlyName }; }
                        finally { d.Dispose(); }
                    })
                    .Cast<object>()
                    .ToList();
            }
            catch (Exception e)
            {
                Log.Error("État du son", e);
            }
            return state;
        }
    }

    public static void SetVolume(int percent)
    {
        lock (Gate)
        {
            using var output = Default(DataFlow.Render);
            if (output == null) return;
            output.AudioEndpointVolume.MasterVolumeLevelScalar = Math.Clamp(percent, 0, 100) / 100f;
            if (percent > 0 && output.AudioEndpointVolume.Mute) output.AudioEndpointVolume.Mute = false;
        }
    }

    public static bool ToggleMute()
    {
        lock (Gate)
        {
            using var output = Default(DataFlow.Render);
            if (output == null) return false;
            output.AudioEndpointVolume.Mute = !output.AudioEndpointVolume.Mute;
            return output.AudioEndpointVolume.Mute;
        }
    }

    /// <summary>Coupe ou rétablit tous les micros. Renvoie true si le micro est maintenant coupé.</summary>
    public static bool ToggleMic()
    {
        lock (Gate)
        {
            using var mic = Default(DataFlow.Capture);
            bool mute = !(mic?.AudioEndpointVolume.Mute ?? false);
            var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                try { d.AudioEndpointVolume.Mute = mute; }
                catch { }
                finally { d.Dispose(); }
            }
            return mute;
        }
    }

    // ------------------------------------------------- sortie audio par défaut
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, int isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, int isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, int fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string device, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string device, int visible);
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigClient
    {
    }

    /// <summary>Fait de ce périphérique la sortie audio par défaut (multimédia et communications).</summary>
    public static bool SetOutput(string id)
    {
        lock (Gate)
        {
            try
            {
                var policy = (IPolicyConfig)new PolicyConfigClient();
                int r = 0;
                for (int role = 0; role < 3; role++) r |= policy.SetDefaultEndpoint(id, role);
                Marshal.ReleaseComObject(policy);
                return r == 0;
            }
            catch (Exception e)
            {
                Log.Error("Changement de sortie audio", e);
                return false;
            }
        }
    }
}
