using System.Windows;
using Forms = System.Windows.Forms;
using SeleVox.Core;
namespace SeleVox.App;
public partial class App : System.Windows.Application
{
    private SingleInstance? instance;
    private Hotkeys? hotkeys;
    private Forms.NotifyIcon? tray;
    private ReadingSession? reading;
    private MainWindow? settingsWindow;
    private Settings settings = new();
    private readonly SelectionCapture capture = new();
    private readonly SettingsStore store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeleVox", "settings.json"));
    private bool exiting;
    private readonly AutomaticSelection automaticSelection = new();
    private AutomaticMouseMonitor? mouse;
    private Forms.ToolStripMenuItem? autoMenu;
    private long mouseEpoch;
    internal bool IsExiting => exiting;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new SingleInstance();
        if (!instance.IsFirst) { Shutdown(); return; }
        var loaded = store.Load(); settings = loaded.Settings;
        reading = new ReadingSession(new SpeechAudio());
        settingsWindow = new MainWindow(settings, SaveSettings, () => reading.TogglePause(), StopReading, ExitApplication, AddForegroundApplicationAsync);
        MainWindow = settingsWindow;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        autoMenu = new Forms.ToolStripMenuItem("Auto Speak · 自动选中朗读") { Checked = settings.AutoSpeak };
        autoMenu.Click += (_, _) =>
        {
            var error = SaveSettings(settings with { AutoSpeak = !settings.AutoSpeak });
            if (error is not null) settingsWindow.SetNotice(error);
        };
        menu.Items.Add(autoMenu);
        menu.Items.Add("暂停 / 继续", null, (_, _) => reading.TogglePause());
        menu.Items.Add("停止", null, (_, _) => StopReading());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "SeleVox · 就绪", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowSettings();
        reading.Changed += status => Dispatcher.BeginInvoke(() =>
        {
            if (exiting) return;
            settingsWindow.SetStatus(status); tray.Text = $"SeleVox · {StateName(status.State)}";
            if (status.State == ReadingState.Error && status.Notify) Notify(status.Message);
        });
        hotkeys = new Hotkeys();
        hotkeys.Pressed += (id, foreground) =>
        {
            switch (id)
            {
                case 1: automaticSelection.Invalidate(); ++mouseEpoch; _ = reading.ReadAsync(token => capture.CaptureAsync(foreground, token), settings); break;
                case 2: reading.TogglePause(); break;
                case 3: StopReading(); break;
            }
        };
        instance.Listen(() => Dispatcher.BeginInvoke(ShowSettings));
        var notices = new List<string>();
        var autoError = ApplyAutomatic();
        if (autoError is not null) notices.Add(autoError);
        if (loaded.Warning is not null) notices.Add(loaded.Warning);
        notices.AddRange(hotkeys.Conflicts);
        if (settingsWindow.VoiceWarning is not null) notices.Add(settingsWindow.VoiceWarning);
        try { StartupRegistration.Apply(settings.StartWithWindows); }
        catch (Exception ex) { notices.Add($"开机启动设置不可用（{ex.GetType().Name}）。"); }
        if (!e.Args.Contains("--background")) ShowSettings();
        if (notices.Count > 0) { var notice = string.Join(Environment.NewLine, notices); settingsWindow.SetNotice(notice); Notify(notice); }
    }
    private static string StateName(ReadingState state) => state switch
    {
        ReadingState.Synthesizing => "合成中", ReadingState.Playing => "播放中", ReadingState.Paused => "已暂停", ReadingState.Error => "出错", _ => "空闲"
    };
    private void Notify(string text)
    {
        if (tray is null) return;
        tray.BalloonTipTitle = "SeleVox"; tray.BalloonTipText = text; tray.ShowBalloonTip(3500);
    }
    private void ShowSettings()
    {
        if (exiting || settingsWindow is null) return;
        settingsWindow.Show(); settingsWindow.WindowState = WindowState.Normal; settingsWindow.Activate();
    }
    private string? SaveSettings(Settings updated)
    {
        try
        {
            StartupRegistration.Apply(updated.StartWithWindows); store.Save(updated); settings = updated;
            ++mouseEpoch; automaticSelection.Invalidate(true);
            reading?.CancelPendingAutomatic();
            settingsWindow?.SetAutomaticSettings(settings);
            if (autoMenu is not null) autoMenu.Checked = settings.AutoSpeak;
            var warning = ApplyAutomatic();
            if (warning is not null) settingsWindow?.SetAutomaticStatus(warning);
            return null;
        }
        catch (Exception ex)
        {
            try { StartupRegistration.Apply(settings.StartWithWindows); } catch { /* Keep the original failure; never log provider text. */ }
            return $"设置保存失败（{ex.GetType().Name}），请检查用户目录及开机启动权限。";
        }
    }
    private string? ApplyAutomatic()
    {
        if (!settings.AutoSpeak)
        {
            mouse?.Dispose(); mouse = null;
            settingsWindow?.SetAutomaticStatus("自动选中朗读已关闭");
            return null;
        }
        if (mouse is not null) return null;
        try
        {
            mouse = new AutomaticMouseMonitor();
            mouse.Invalidated += () => { ++mouseEpoch; automaticSelection.Invalidate(); };
            mouse.Selected += (window, point) =>
            {
                var epoch = mouseEpoch;
                Dispatcher.BeginInvoke(() =>
                {
                    if (epoch == mouseEpoch && !exiting && settings.AutoSpeak) _ = AutoReadAsync(window, point, epoch);
                });
            };
            settingsWindow?.SetAutomaticStatus("仅在启用的应用规则中自动朗读（UIA）");
            return null;
        }
        catch (Exception ex) { return $"自动鼠标监听不可用（{ex.GetType().Name}），可继续使用手动热键。"; }
    }
    private async Task AutoReadAsync(nint window, System.Windows.Point point, long epoch)
    {
        // Process identity lookup runs outside the low-level hook and UI thread.
        var path = await Task.Run(() => ApplicationIdentity.FromWindow(window));
        if (epoch != mouseEpoch || exiting || !settings.AllowsAutomatic(path) || Native.GetForegroundWindow() != window) return;
        var identity = path! + "|" + window;
        await automaticSelection.RequestAsync(identity, async token =>
        {
            await Task.Delay(180, token);
            var first = await capture.CaptureSnapshotAsync(window, token, point);
            await Task.Delay(80, token);
            var stable = await capture.CaptureSnapshotAsync(window, token, point, first.Focus);
            if (first.Text != stable.Text) throw new UserFeedbackException("选区仍在变化，已忽略此次自动朗读。");
            return stable.Text;
        }, () => epoch == mouseEpoch && !exiting && settings.AllowsAutomatic(path) && Native.GetForegroundWindow() == window,
            text => { settingsWindow?.SetAutomaticStatus("已获取有效选区"); _ = reading!.ReadTextAsync(text, settings); },
            message => settingsWindow?.SetAutomaticStatus(message));
    }
    private void StopReading() { ++mouseEpoch; automaticSelection.Invalidate(); reading?.Stop(); }
    private async Task<string?> AddForegroundApplicationAsync()
    {
        // Wait for the user to bring their target to the foreground; never activate it ourselves.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        nint window;
        do
        {
            await Task.Delay(100);
            if (exiting) return "应用正在退出。";
            window = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(window, out var process);
            if (window != 0 && process != 0 && process != Environment.ProcessId) break;
        } while (DateTime.UtcNow < deadline);
        var path = await Task.Run(() => ApplicationIdentity.FromWindow(window));
        if (exiting) return "应用正在退出。";
        if (path is null || Native.GetForegroundWindow() != window) return "无法可靠识别前台应用，请切换到目标窗口后重试。";
        var rules = (settings.Applications ?? []).Where(r => !string.Equals(r.ExecutablePath, path, StringComparison.OrdinalIgnoreCase))
            .Append(new ApplicationRule(path)).ToArray();
        return SaveSettings(settings with { Applications = rules });
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e) { exiting = true; base.OnSessionEnding(e); }
    private void ExitApplication() { if (exiting) return; exiting = true; Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        exiting = true; mouse?.Dispose(); automaticSelection.Dispose(); hotkeys?.Dispose(); reading?.Dispose();
        if (tray is not null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        instance?.Dispose(); base.OnExit(e);
    }
}
