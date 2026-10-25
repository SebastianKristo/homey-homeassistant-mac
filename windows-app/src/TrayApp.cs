using System.Diagnostics;
using System.Net.Sockets;

namespace MacBridge;

/// <summary>Tray (notification area) app: runs the HTTP API and shows connection details.</summary>
sealed class TrayApp : ApplicationContext
{
    private readonly Bridge bridge = new();
    private readonly Config config = Config.Shared;
    private readonly NotifyIcon trayIcon;
    private readonly ContextMenuStrip menu = new();
    private readonly SynchronizationContext ui;
    private HttpServer? server;
    private MdnsAdvertiser? advertiser;
    private System.Windows.Forms.Timer? clipboardClearTimer;
    private readonly DisplayWatcher displayWatcher;

    public TrayApp()
    {
        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        trayIcon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "MacBridge",
            ContextMenuStrip = menu,
            Visible = true,
        };
        // Refresh the menu each time it opens (e.g. to show the last rejected request).
        menu.Opening += (_, _) => Populate();
        Populate();
        SystemControl.ShowNotification = (title, message) => ui.Post(_ =>
            trayIcon.ShowBalloonTip(5000, title, message, ToolTipIcon.None), null);
        displayWatcher = new DisplayWatcher(bridge.Sensors.SetDisplayState);
        config.RefreshStartWithWindows();
        StartServer();
        bridge.StartPushing();
    }

    private static Icon LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } path && Icon.ExtractAssociatedIcon(path) is { } icon) return icon;
        }
        catch (Exception e) when (e is ArgumentException or IOException) { }
        return SystemIcons.Application;
    }

    private void StartServer()
    {
        advertiser?.Dispose();
        server?.Dispose();
        server = null;
        var next = new HttpServer(config.Port, bridge.Handle);
        try
        {
            next.Start();
        }
        catch (SocketException e)
        {
            Alert($"MacBridge could not open port {config.Port}: {e.Message}. Another program (or another MacBridge) may be using it. Quit it, or choose another port.");
            return;
        }
        server = next;
        var info = SystemControl.DeviceInfo();
        advertiser = new MdnsAdvertiser();
        advertiser.Start(SystemControl.ComputerName(), config.Port, new Dictionary<string, string>
        {
            ["id"] = SystemControl.DeviceId(),
            ["model"] = info["model"] as string ?? "",
            ["version"] = "2",
            ["platform"] = "windows",
        });
    }

    private void Populate()
    {
        menu.Items.Clear();
        menu.Items.Add(Label($"MacBridge {SystemControl.AppVersion} for Windows – port {config.Port}"));
        menu.Items.Add(Label($"Device ID: {SystemControl.DeviceId()}"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Copy token", CopyToken));
        menu.Items.Add(Item("Regenerate token", RegenerateToken));
        menu.Items.Add(Item("Change port…", ChangePort));
        menu.Items.Add(Item("Push URLs (webhooks)…", EditPushUrls));
        menu.Items.Add(Toggle("Allow restart/shutdown", config.AllowPowerCommands, () => config.AllowPowerCommands = !config.AllowPowerCommands));
        menu.Items.Add(Toggle("Allow speech and notifications", config.AllowSpeechAndNotifications,
            () => config.AllowSpeechAndNotifications = !config.AllowSpeechAndNotifications));
        menu.Items.Add(Toggle("Report Bluetooth devices", config.ReportBluetoothDevices,
            () => config.ReportBluetoothDevices = !config.ReportBluetoothDevices));
        menu.Items.Add(Item("Watched programs…", EditWatchedProcesses));
        menu.Items.Add(Item("Watched services…", EditWatchedServices));
        menu.Items.Add(Toggle("Start with Windows", config.StartWithWindows, () => config.StartWithWindows = !config.StartWithWindows));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Item("Quit", Quit));
        if (bridge.LastAuthFailure is { } failure)
        {
            var hint = failure.Reason switch
            {
                "unsigned_request" => "old Homey/HA app, update it",
                "wrong_token" => "wrong token",
                "clock_skew" => "clocks differ by more than 1 min",
                _ => failure.Reason,
            };
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(Label($"Last rejected: {failure.Host} at {failure.Date:t} ({hint})"));
        }
    }

    private static ToolStripMenuItem Label(string text) => new(text) { Enabled = false };

    private static ToolStripMenuItem Item(string text, Action action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        return item;
    }

    private static ToolStripMenuItem Toggle(string text, bool on, Action toggle)
    {
        var item = Item(text, toggle);
        item.Checked = on;
        return item;
    }

    /// <summary>
    /// Copies the token, kept out of clipboard history and cloud clipboard sync, and clears it
    /// again after a minute.
    /// </summary>
    private void CopyToken()
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, config.Token);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1, 0, 0, 0 }));
        data.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[] { 0, 0, 0, 0 }));
        data.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[] { 0, 0, 0, 0 }));
        try
        {
            Clipboard.SetDataObject(data, true, 5, 100);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            Alert("Could not copy the token: the clipboard is in use by another program. Try again.");
            return;
        }
        var sequence = Native.GetClipboardSequenceNumber();
        clipboardClearTimer?.Dispose();
        clipboardClearTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        clipboardClearTimer.Tick += (_, _) =>
        {
            clipboardClearTimer?.Dispose();
            clipboardClearTimer = null;
            if (Native.GetClipboardSequenceNumber() == sequence)
            {
                try { Clipboard.Clear(); } catch (System.Runtime.InteropServices.ExternalException) { }
            }
        };
        clipboardClearTimer.Start();
    }

    private void RegenerateToken()
    {
        config.RegenerateToken();
        CopyToken();
        Alert("New token copied to clipboard (cleared after 1 minute). Update Home Assistant and Homey.");
    }

    private void ChangePort()
    {
        if (InputDialog.Show("HTTP port", config.Port.ToString()) is not { } value) return;
        if (!int.TryParse(value.Trim(), out var port) || port is < 1 or > 65535)
        {
            Alert("The port must be a number from 1 to 65535.");
            return;
        }
        config.Port = port;
        StartServer();
    }

    private void EditPushUrls()
    {
        if (InputDialog.Show(
                $"Push URLs (http/https), comma separated. Status is POSTed every {config.PushIntervalSeconds}s, signed with the token.",
                string.Join(", ", config.PushUrls)) is not { } value)
            return;
        var urls = value.Split(',').Select(u => u.Trim()).Where(u => u.Length > 0).ToArray();
        var rejected = urls.Where(u => !Config.IsAllowedPushUrl(u)).ToArray();
        config.PushUrls = urls;
        if (rejected.Length > 0) Alert($"Ignored (must be http:// or https://): {string.Join(", ", rejected)}");
    }

    private void EditWatchedProcesses()
    {
        if (InputDialog.Show("Programs to report as running/not running (comma separated, max 20), e.g. steam, zoom, obs64",
                string.Join(", ", config.WatchedProcesses)) is not { } value) return;
        config.WatchedProcesses = value.Split(',');
    }

    private void EditWatchedServices()
    {
        if (InputDialog.Show("Windows services to report the state of (service names, comma separated, max 20), e.g. Spooler, wuauserv",
                string.Join(", ", config.WatchedServices)) is not { } value) return;
        config.WatchedServices = value.Split(',');
    }

    private void Quit()
    {
        trayIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            advertiser?.Dispose();
            server?.Dispose();
            displayWatcher.Dispose();
            bridge.Dispose();
            trayIcon.Dispose();
            menu.Dispose();
            clipboardClearTimer?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static void Alert(string text) =>
        MessageBox.Show(text, "MacBridge", MessageBoxButtons.OK, MessageBoxIcon.Information,
            MessageBoxDefaultButton.Button1, MessageBoxOptions.DefaultDesktopOnly);
}

/// <summary>Hidden window that receives display on/off notifications from Windows.</summary>
sealed class DisplayWatcher : NativeWindow, IDisposable
{
    private readonly Action<int> onChange;
    private IntPtr registration;

    public DisplayWatcher(Action<int> onChange)
    {
        this.onChange = onChange;
        CreateHandle(new CreateParams());
        registration = Native.RegisterPowerSettingNotification(Handle, ref Native.GUID_CONSOLE_DISPLAY_STATE, 0);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_POWERBROADCAST && m.WParam == Native.PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
        {
            // POWERBROADCAST_SETTING: GUID (16 bytes), DataLength (4), Data (DWORD for the display state).
            onChange(System.Runtime.InteropServices.Marshal.ReadInt32(m.LParam, 20));
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (registration != IntPtr.Zero) Native.UnregisterPowerSettingNotification(registration);
        registration = IntPtr.Zero;
        DestroyHandle();
    }
}
