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
    internal bool IsExiting => exiting;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new SingleInstance();
        if (!instance.IsFirst) { Shutdown(); return; }
        var loaded = store.Load(); settings = loaded.Settings;
        reading = new ReadingSession(new SpeechAudio());
        settingsWindow = new MainWindow(settings, SaveSettings, () => reading.TogglePause(), () => reading.Stop(), ExitApplication);
        MainWindow = settingsWindow;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add("暂停 / 继续", null, (_, _) => reading.TogglePause());
        menu.Items.Add("停止", null, (_, _) => reading.Stop());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApplication());
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "SeleVox · 就绪", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => ShowSettings();
        reading.Changed += status => Dispatcher.BeginInvoke(() =>
        {
            if (exiting) return;
            settingsWindow.SetStatus(status); tray.Text = $"SeleVox · {StateName(status.State)}";
            if (status.State == ReadingState.Error) Notify(status.Message);
        });
        hotkeys = new Hotkeys();
        hotkeys.Pressed += (id, foreground) =>
        {
            switch (id)
            {
                case 1: _ = reading.ReadAsync(token => capture.CaptureAsync(foreground, token), settings); break;
                case 2: reading.TogglePause(); break;
                case 3: reading.Stop(); break;
            }
        };
        instance.Listen(() => Dispatcher.BeginInvoke(ShowSettings));
        var notices = new List<string>();
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
        try { StartupRegistration.Apply(updated.StartWithWindows); store.Save(updated); settings = updated; return null; }
        catch (Exception ex)
        {
            try { StartupRegistration.Apply(settings.StartWithWindows); } catch { /* Keep the original failure; never log provider text. */ }
            return $"设置保存失败（{ex.GetType().Name}），请检查用户目录及开机启动权限。";
        }
    }
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e) { exiting = true; base.OnSessionEnding(e); }
    private void ExitApplication() { if (exiting) return; exiting = true; Shutdown(); }
    protected override void OnExit(ExitEventArgs e)
    {
        exiting = true; hotkeys?.Dispose(); reading?.Dispose();
        if (tray is not null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        instance?.Dispose(); base.OnExit(e);
    }
}
