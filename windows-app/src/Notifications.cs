using System.Diagnostics;
using System.Text.Json;
using Microsoft.Toolkit.Uwp.Notifications;

namespace MacBridge;

/// <summary>
/// Things that happened on the PC that Home Assistant / Homey should react to (a notification
/// button was clicked, the PC went to sleep, …). Kept in memory, newest last.
/// </summary>
sealed class EventLog
{
    private readonly object gate = new();
    private readonly List<Dictionary<string, object>> events = new();
    private long nextId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // unique across restarts

    public void Add(string type, Dictionary<string, object>? data = null)
    {
        lock (gate)
        {
            var e = data is null ? new Dictionary<string, object>() : new Dictionary<string, object>(data);
            e["id"] = nextId++;
            e["type"] = type;
            e["at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            events.Add(e);
            if (events.Count > 50) events.RemoveRange(0, events.Count - 50);
        }
    }

    public List<Dictionary<string, object>> Recent
    {
        get { lock (gate) return events.ToList(); }
    }
}

/// <summary>
/// Windows toast notifications with optional buttons. A clicked button becomes a
/// "notification_action" event; a button may open an https/http link, but only when the user clicks it.
/// </summary>
sealed class Notifier
{
    private readonly EventLog events;
    private readonly object gate = new();
    private readonly Dictionary<string, Uri> links = new();

    public sealed record Action(string Id, string Title, Uri? Uri);

    public Notifier(EventLog events)
    {
        this.events = events;
        ToastNotificationManagerCompat.OnActivated += OnActivated;
    }

    /// <summary>Parses <c>[{"action": "...", "title": "...", "uri": "https://..."}]</c>. At most 4 buttons; only http(s) links.</summary>
    public static List<Action> ParseActions(JsonElement? value)
    {
        var result = new List<Action>();
        if (value is not { ValueKind: JsonValueKind.Array } list) return result;
        foreach (var item in list.EnumerateArray().Take(4))
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("action", out var a) || a.ValueKind != JsonValueKind.String) continue;
            var id = SystemControl.Clean(a.GetString() ?? "", 64);
            if (id.Length == 0) continue;
            var title = item.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? SystemControl.Clean(t.GetString() ?? "", 40) : "";
            Uri? uri = null;
            if (item.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String
                && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var parsed)
                && (parsed.Scheme == Uri.UriSchemeHttps || parsed.Scheme == Uri.UriSchemeHttp))
                uri = parsed;
            result.Add(new Action(id, title.Length > 0 ? title : id, uri));
        }
        return result;
    }

    public void Show(string title, string message, IReadOnlyList<Action> actions)
    {
        var id = Guid.NewGuid().ToString("N");
        title = SystemControl.Clean(title, 100);
        var builder = new ToastContentBuilder()
            .AddArgument("mb", id)
            .AddArgument("title", title)
            .AddText(title)
            .AddText(SystemControl.Clean(message, 500));
        lock (gate)
        {
            if (links.Count > 80) links.Clear();
            foreach (var action in actions)
            {
                if (action.Uri is not null) links[$"{id}|{action.Id}"] = action.Uri;
                builder.AddButton(new ToastButton().SetContent(action.Title).AddArgument("action", action.Id));
            }
        }
        try
        {
            builder.Show();
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Trace.WriteLine($"MacBridge: toast failed ({e.Message}), using tray balloon");
            SystemControl.Notify(title, message);
        }
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        if (!args.TryGetValue("action", out var action) || !args.TryGetValue("mb", out var id)) return;
        args.TryGetValue("title", out var title);
        Uri? link;
        lock (gate) links.TryGetValue($"{id}|{action}", out link);
        if (link is not null)
        {
            // Opened by Windows' default browser for http(s) only; nothing else is started.
            try { Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception ex) { Trace.WriteLine($"MacBridge: could not open link: {ex.Message}"); }
        }
        events.Add("notification_action", new() { ["action"] = action, ["title"] = title ?? "" });
    }
}
