using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Media.Control;

namespace MacBridge;

/// <summary>
/// Media and volume control. Uses the Windows media session API (the same one behind the volume
/// flyout), which works with Spotify, the Media Player app, browsers and most other players.
/// Falls back to the media keys when no player has a session.
/// </summary>
sealed class MediaController
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);
    private GlobalSystemMediaTransportControlsSessionManager? manager;

    // Volume

    public (int Level, bool Muted) Volume() => AudioVolume.Get();

    public void SetVolume(int level) => AudioVolume.SetLevel(level);

    public void SetMuted(bool muted) => AudioVolume.SetMuted(muted);

    public void VolumeStep(int delta) => SetVolume(Volume().Level + Math.Clamp(delta, -25, 25));

    // Playback

    public void PlayPause() => Control(s => s.TryTogglePlayPauseAsync(), Native.VK_MEDIA_PLAY_PAUSE);
    public void Play() => Control(s => s.TryPlayAsync(), Native.VK_MEDIA_PLAY_PAUSE);
    public void Pause() => Control(s => s.TryPauseAsync(), Native.VK_MEDIA_PLAY_PAUSE);
    public void Next() => Control(s => s.TrySkipNextAsync(), Native.VK_MEDIA_NEXT_TRACK);
    public void Previous() => Control(s => s.TrySkipPreviousAsync(), Native.VK_MEDIA_PREV_TRACK);

    private void Control(Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> action, ushort key)
    {
        try
        {
            if (ActiveSession() is { } session && Wait(action(session).AsTask())) return;
        }
        catch (Exception e) when (e is TimeoutException or COMException or AggregateException)
        {
            Trace.WriteLine($"MacBridge: media session refused: {e.Message}");
        }
        PostMediaKey(key);
    }

    // Now playing

    public Dictionary<string, object> NowPlaying()
    {
        try
        {
            if (ActiveSession() is not { } session) return new() { ["state"] = "idle" };
            var app = AppName(session.SourceAppUserModelId);
            var properties = Wait(session.TryGetMediaPropertiesAsync().AsTask());
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();

            var state = playback.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
                _ => "idle",
            };
            var duration = Math.Max(0, (timeline.EndTime - timeline.StartTime).TotalSeconds);
            var position = timeline.Position.TotalSeconds;
            // Players only report the position now and then; extrapolate while playing.
            if (state == "playing" && timeline.LastUpdatedTime > DateTimeOffset.MinValue)
                position += Math.Max(0, (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds);
            if (duration > 0) position = Math.Min(position, duration);

            return new()
            {
                ["state"] = state,
                ["app"] = app,
                ["title"] = SystemControl.Clean(properties?.Title ?? "", 300),
                ["artist"] = SystemControl.Clean(properties?.Artist ?? "", 300),
                ["album"] = SystemControl.Clean(properties?.AlbumTitle ?? "", 300),
                ["duration"] = (int)duration,
                ["position"] = (int)Math.Max(0, position),
            };
        }
        catch (Exception e) when (e is TimeoutException or COMException or AggregateException or InvalidOperationException)
        {
            Trace.WriteLine($"MacBridge: now playing unavailable: {e.Message}");
            return new() { ["state"] = "idle", ["error"] = "unavailable" };
        }
    }

    /// <summary>A session that is playing; otherwise the one Windows considers current.</summary>
    private GlobalSystemMediaTransportControlsSession? ActiveSession()
    {
        var m = manager ??= Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask());
        var playing = m.GetSessions().FirstOrDefault(s =>
            s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
        return playing ?? m.GetCurrentSession() ?? m.GetSessions().FirstOrDefault();
    }

    private static readonly Dictionary<string, string> KnownApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Spotify"] = "Spotify",
        ["SpotifyAB.SpotifyMusic"] = "Spotify",
        ["Microsoft.ZuneMusic"] = "Media Player",
        ["chrome"] = "Chrome",
        ["msedge"] = "Edge",
        ["MSEdge"] = "Edge",
        ["firefox"] = "Firefox",
        ["vlc"] = "VLC",
        ["AppleInc.AppleMusicWin"] = "Apple Music",
        ["AppleInc.iTunes"] = "iTunes",
    };

    /// <summary>"Spotify.exe" or "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" → "Spotify".</summary>
    private static string AppName(string id)
    {
        var name = id.Split('!')[0].Split('_')[0];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return KnownApps.TryGetValue(name, out var known) ? known : SystemControl.Clean(name, 64);
    }

    private static T Wait<T>(Task<T> task)
    {
        if (!task.Wait(CallTimeout)) throw new TimeoutException("media session did not answer");
        return task.Result;
    }

    // Media keys

    /// <summary>For players without a media session. Windows needs no extra permission for this.</summary>
    private static void PostMediaKey(ushort key)
    {
        var inputs = new[]
        {
            KeyInput(key, Native.KEYEVENTF_EXTENDEDKEY),
            KeyInput(key, Native.KEYEVENTF_EXTENDEDKEY | Native.KEYEVENTF_KEYUP),
        };
        Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Native.INPUT>());
    }

    private static Native.INPUT KeyInput(ushort key, uint flags) => new()
    {
        Type = Native.INPUT_KEYBOARD,
        U = new Native.InputUnion { Keyboard = new Native.KEYBDINPUT { Vk = key, Flags = flags } },
    };
}
