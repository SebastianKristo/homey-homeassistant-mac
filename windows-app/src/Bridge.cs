using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace MacBridge;

/// <summary>Routes HTTP requests, executes commands and pushes status to webhooks.</summary>
sealed class Bridge : IDisposable
{
    private readonly SystemStats stats = new();
    private readonly MediaController media = new();
    public Sensors Sensors { get; } = new();
    public EventLog Events { get; } = new();
    private readonly Notifier notifier;
    private readonly AudioPlayer audio = new();
    private readonly Config config = Config.Shared;
    private readonly object stateGate = new();
    private readonly NonceCache nonces = new();
    private readonly FailureLimiter limiter = new();
    private static readonly HttpClient PushClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(5),
    };
    private System.Threading.Timer? pushTimer;

    /// <summary>Most recent rejected request, shown in the menu to help with pairing problems.</summary>
    public (string Reason, string Host, DateTime Date)? LastAuthFailure
    {
        get { lock (failureGate) return storedAuthFailure; }
    }
    private (string Reason, string Host, DateTime Date)? storedAuthFailure;
    private readonly object failureGate = new();

    /// <summary>The complete list of things MacBridge can be asked to do. Anything else is rejected.</summary>
    public static readonly string[] Commands =
    {
        "play_pause", "play", "pause", "next", "previous",
        "set_volume", "volume_up", "volume_down", "mute", "unmute", "set_mute",
        "sleep", "display_sleep", "wake", "wake_display", "lock", "restart", "shutdown",
        "say", "notify", "play_audio", "stop_audio",
    };

    public Bridge()
    {
        notifier = new Notifier(Events);
        Sensors.OnStateChange = state =>
        {
            Events.Add("state_change", new() { ["state"] = state });
            _ = Task.Run(() => Push(Status()));
        };
    }

    // Status

    public Dictionary<string, object> Status()
    {
        lock (stateGate)
        {
            var volume = media.Volume();
            return new()
            {
                ["device"] = SystemControl.DeviceInfo(),
                ["stats"] = stats.Snapshot(),
                ["volume"] = volume.Level,
                ["muted"] = volume.Muted,
                ["media"] = media.NowPlaying(),
                ["sensors"] = Sensors.Snapshot(),
                ["events"] = Events.Recent,
                ["commands"] = Commands,
                ["power_commands"] = config.AllowPowerCommands,
                ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
        }
    }

    // HTTP

    public HttpResponse Handle(HttpRequest request)
    {
        if (request.Method == "GET" && request.Path == "/api/ping")
            return HttpResponse.Json(new Dictionary<string, object> { ["ok"] = true, ["protocol"] = "MacBridge-v1" });
        if (limiter.IsBlocked(request.RemoteHost))
            return HttpResponse.Error("too many failed attempts", 429);

        if (Authenticate(request, out var nonce) is { } failure)
        {
            limiter.RecordFailure(request.RemoteHost);
            lock (failureGate) storedAuthFailure = (failure, request.RemoteHost, DateTime.Now);
            Trace.WriteLine($"MacBridge: rejected request from {request.RemoteHost}: {failure}");
            return HttpResponse.Json(new Dictionary<string, object>
            {
                ["error"] = "unauthorized",
                ["reason"] = failure,
                ["protocol"] = "MacBridge-v1",
                ["server_time"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            }, 401);
        }

        HttpResponse response;
        try
        {
            response = Route(request);
        }
        catch (Exception e)
        {
            Trace.WriteLine($"MacBridge: request failed: {e}");
            response = HttpResponse.Error("internal error", 500);
        }
        response.Headers["X-MacBridge-Signature"] =
            Auth.Sign(Auth.ResponseMessage(nonce, response.Status, response.Body), config.Token);
        return response;
    }

    /// <summary>
    /// Verifies the request signature, timestamp and nonce. Returns null on success, otherwise a
    /// reason that is safe to show the client (says what was wrong, never anything about the token).
    /// </summary>
    private string? Authenticate(HttpRequest request, out string nonce)
    {
        nonce = "";
        if (!request.Headers.TryGetValue(Auth.TimestampHeader, out var timestamp)
            || !request.Headers.TryGetValue(Auth.NonceHeader, out var requestNonce)
            || !request.Headers.TryGetValue(Auth.SignatureHeader, out var signature))
            return "unsigned_request";
        if (!double.TryParse(timestamp, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !(Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - seconds) <= Auth.MaxSkewSeconds))
            return "clock_skew";
        if (!Auth.IsValidNonce(requestNonce)) return "invalid_nonce";
        var expected = Auth.Sign(
            Auth.RequestMessage(request.Method, request.Target, timestamp, requestNonce, request.Body), config.Token);
        if (!Auth.Matches(signature, expected)) return "wrong_token";
        if (!nonces.Insert(requestNonce)) return "replayed_request";
        nonce = requestNonce;
        return null;
    }

    private HttpResponse Route(HttpRequest request)
    {
        switch (request.Method, request.Path)
        {
            case ("GET", "/api/status"):
                return HttpResponse.Json(Status());
            case ("POST", "/api/command"):
                string command;
                JsonElement? value;
                try
                {
                    using var document = JsonDocument.Parse(request.Body);
                    if (document.RootElement.ValueKind != JsonValueKind.Object
                        || !document.RootElement.TryGetProperty("command", out var c) || c.ValueKind != JsonValueKind.String)
                        throw new JsonException();
                    command = c.GetString()!;
                    value = document.RootElement.TryGetProperty("value", out var v) ? v.Clone() : null;
                }
                catch (JsonException)
                {
                    return HttpResponse.Error("body must be JSON: {\"command\": ..., \"value\": ...}", 400);
                }
                try
                {
                    Execute(command, value, request.RemoteHost);
                }
                catch (CommandException e)
                {
                    return HttpResponse.Error(e.Message, e.Status);
                }
                // Give the system a moment, then return fresh state.
                Thread.Sleep(300);
                var state = Status();
                _ = Task.Run(() => Push(state));
                return HttpResponse.Json(new Dictionary<string, object> { ["ok"] = true, ["status"] = state });
            default:
                return HttpResponse.Error("not found", 404);
        }
    }

    private sealed class CommandException(string message, int status) : Exception(message)
    {
        public int Status { get; } = status;
    }

    public void Execute(string command, JsonElement? value, string requester)
    {
        if (!Commands.Contains(command)) throw new CommandException("unknown command", 400);
        Trace.WriteLine($"MacBridge command: {command}");
        switch (command)
        {
            case "play_pause": media.PlayPause(); break;
            case "play": media.Play(); break;
            case "pause": media.Pause(); break;
            case "next": media.Next(); break;
            case "previous": media.Previous(); break;
            case "set_volume":
                if (IntValue(value) is not { } level || level is < 0 or > 100)
                    throw new CommandException("value 0-100 required", 400);
                media.SetVolume(level);
                break;
            case "volume_up": media.VolumeStep(IntValue(value) ?? 6); break;
            case "volume_down": media.VolumeStep(-(IntValue(value) ?? 6)); break;
            case "mute": media.SetMuted(true); break;
            case "unmute": media.SetMuted(false); break;
            case "set_mute": media.SetMuted(BoolValue(value)); break;
            case "sleep": RunLater(SystemControl.Sleep); break;
            case "display_sleep": RunLater(SystemControl.DisplaySleep); break;
            case "wake":
            case "wake_display": SystemControl.WakeDisplay(); break;
            case "lock": SystemControl.Lock(); break;
            case "restart":
            case "shutdown":
                if (!config.AllowPowerCommands)
                    throw new CommandException("restart/shutdown is turned off in MacBridge", 403);
                RunLater(command == "restart" ? SystemControl.Restart : SystemControl.Shutdown);
                break;
            case "say":
                if (!config.AllowSpeechAndNotifications)
                    throw new CommandException("speech is turned off in MacBridge", 403);
                if (value is not { ValueKind: JsonValueKind.String } text || string.IsNullOrEmpty(text.GetString()))
                    throw new CommandException("text required", 400);
                SystemControl.Say(text.GetString()!);
                break;
            case "notify":
                if (!config.AllowSpeechAndNotifications)
                    throw new CommandException("notifications are turned off in MacBridge", 403);
                string message = "", title = "Home";
                JsonElement? actions = null;
                if (value is { ValueKind: JsonValueKind.String } plain) message = plain.GetString() ?? "";
                if (value is { ValueKind: JsonValueKind.Object } dict)
                {
                    if (dict.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) message = m.GetString() ?? "";
                    if (dict.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) title = t.GetString() ?? title;
                    if (dict.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                        && d.TryGetProperty("actions", out var nested)) actions = nested;
                    else if (dict.TryGetProperty("actions", out var direct)) actions = direct;
                }
                if (message.Length == 0) throw new CommandException("message required", 400);
                notifier.Show(title, message, Notifier.ParseActions(actions));
                break;
            case "play_audio":
                if (!config.AllowSpeechAndNotifications)
                    throw new CommandException("audio is turned off in MacBridge", 403);
                string? url = value switch
                {
                    { ValueKind: JsonValueKind.String } u => u.GetString(),
                    { ValueKind: JsonValueKind.Object } o when o.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String => u.GetString(),
                    _ => null,
                };
                if (string.IsNullOrEmpty(url)) throw new CommandException("url required", 400);
                double? volume = value is { ValueKind: JsonValueKind.Object } options
                    && options.TryGetProperty("volume", out var vol) && vol.TryGetDouble(out var level) ? level : null;
                try
                {
                    audio.Play(url, requester, volume);
                }
                catch (AudioPlayer.NotAllowedException)
                {
                    throw new CommandException("audio url must point to the computer that sent the request", 403);
                }
                catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException
                                              or System.Runtime.InteropServices.COMException)
                {
                    Trace.WriteLine($"MacBridge: play_audio failed: {e.Message}");
                    throw new CommandException("could not play audio", 422);
                }
                break;
            case "stop_audio":
                audio.Stop();
                break;
            default:
                throw new CommandException("unknown command", 400);
        }
    }

    /// <summary>Lets the HTTP response go out before the PC goes to sleep / powers off.</summary>
    private static void RunLater(Action action) =>
        Task.Delay(TimeSpan.FromSeconds(1)).ContinueWith(_ => action(), TaskScheduler.Default);

    private static int? IntValue(JsonElement? v)
    {
        if (v is { ValueKind: JsonValueKind.Number } n && n.TryGetDouble(out var d) && double.IsFinite(d) && Math.Abs(d) < 1_000)
            return (int)Math.Round(d, MidpointRounding.AwayFromZero);
        if (v is { ValueKind: JsonValueKind.String } s && int.TryParse(s.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i))
            return i;
        return null;
    }

    private static bool BoolValue(JsonElement? v) => v switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.String } s => s.GetString() is "true" or "1",
        _ => false,
    };

    // Push

    public void StartPushing()
    {
        pushTimer?.Dispose();
        var interval = TimeSpan.FromSeconds(config.PushIntervalSeconds);
        pushTimer = new System.Threading.Timer(_ =>
        {
            if (config.PushUrls.Length == 0) return;
            Push(Status());
        }, null, interval, interval);
    }

    /// <summary>Sends status to the configured webhooks, signed so the receiver can verify it came from this PC.</summary>
    private void Push(Dictionary<string, object> state)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(state);
        foreach (var url in config.PushUrls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) continue;
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var nonce = Auth.RandomNonce();
            var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Add("X-MacBridge-Timestamp", timestamp);
            request.Headers.Add("X-MacBridge-Nonce", nonce);
            request.Headers.Add("X-MacBridge-Signature", Auth.Sign(Auth.PushMessage(timestamp, nonce, body), config.Token));
            _ = PushClient.SendAsync(request).ContinueWith(t =>
            {
                if (t.IsFaulted) Trace.WriteLine($"MacBridge: push failed: {t.Exception?.GetBaseException().Message}");
                else t.Result.Dispose();
                request.Dispose();
            }, TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        pushTimer?.Dispose();
        Sensors.Dispose();
        audio.Dispose();
    }
}
