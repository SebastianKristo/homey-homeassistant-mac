using System.Runtime.InteropServices;

namespace MacBridge;

/// <summary>Master volume of the default playback device (Core Audio, IAudioEndpointVolume).</summary>
static class AudioVolume
{
    private const int eRender = 0, eMultimedia = 1, CLSCTX_ALL = 23;
    private static readonly object Gate = new();

    public static (int Level, bool Muted) Get() => Use(v =>
    {
        Check(v.GetMasterVolumeLevelScalar(out var level));
        Check(v.GetMute(out var muted));
        return ((int)Math.Round(level * 100), muted);
    }, (0, false));

    public static void SetLevel(int level) => Use(v =>
    {
        var context = Guid.Empty;
        Check(v.SetMasterVolumeLevelScalar(Math.Clamp(level, 0, 100) / 100f, ref context));
        return true;
    }, false);

    public static void SetMuted(bool muted) => Use(v =>
    {
        var context = Guid.Empty;
        Check(v.SetMute(muted, ref context));
        return true;
    }, false);

    /// <summary>Opens the current default device each time, so switching speakers/headphones just works.</summary>
    private static T Use<T>(Func<IAudioEndpointVolume, T> action, T fallback)
    {
        lock (Gate)
        {
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            object? endpoint = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                Check(enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out device));
                var iid = typeof(IAudioEndpointVolume).GUID;
                Check(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out endpoint));
                return action((IAudioEndpointVolume)endpoint);
            }
            catch (Exception e) when (e is COMException or InvalidCastException)
            {
                System.Diagnostics.Trace.WriteLine($"MacBridge: volume unavailable: {e.Message}");
                return fallback; // e.g. no audio device
            }
            finally
            {
                if (endpoint is not null) Marshal.ReleaseComObject(endpoint);
                if (device is not null) Marshal.ReleaseComObject(device);
                if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
            }
        }
    }

    private static void Check(int hresult)
    {
        if (hresult < 0) throw new COMException("Core Audio call failed", hresult);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
