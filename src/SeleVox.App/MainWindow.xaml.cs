using System.ComponentModel;
using System.Windows;
using SeleVox.Core;
namespace SeleVox.App;
public partial class MainWindow : Window
{
    private readonly Func<Settings, string?> save;
    private readonly Action pause, stop, exit;
    private readonly Func<Task<string?>> addForeground;
    private Settings savedSettings;
    public string? VoiceWarning { get; }
    internal MainWindow(Settings settings, Func<Settings, string?> save, Action pause, Action stop, Action exit,
        Func<Task<string?>> addForeground)
    {
        InitializeComponent(); this.save = save; this.pause = pause; this.stop = stop; this.exit = exit;
        this.addForeground = addForeground; savedSettings = settings; SetAutomaticSettings(settings);
        try
        {
            var voices = SpeechAudio.GetVoices(); Voice.ItemsSource = voices; Voice.SelectedValue = settings.VoiceId;
            if (settings.VoiceId is null && voices.Count > 0) Voice.SelectedValue = Windows.Media.SpeechSynthesis.SpeechSynthesizer.DefaultVoice.Id;
            if (voices.Count == 0) VoiceWarning = "没有可用的 Windows 本地语音，请在系统语言设置中安装语音。";
            else if (Voice.SelectedIndex < 0) VoiceWarning = "已保存的语音不可用，请重新选择并保存。";
        }
        catch (Exception ex) { VoiceWarning = $"无法枚举本地语音（{ex.GetType().Name}）。"; }
        Rate.Value = settings.Rate; Volume.Value = settings.Volume; AutoStart.IsChecked = settings.StartWithWindows;
    }
    public void SetNotice(string message) => Notice.Text = message;
    public void SetStatus(Status status) => StatusText.Text = status.Message;
    public void SetAutomaticStatus(string message) => AutomaticStatus.Text = message;
    public void SetAutomaticSettings(Settings settings)
    {
        savedSettings = settings; AutoSpeak.IsChecked = settings.AutoSpeak;
        Rules.ItemsSource = settings.Applications ?? [];
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        if (((App)System.Windows.Application.Current).IsExiting is false) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Voice.SelectedValue is not string voice) { SetNotice("请先选择可用的本地语音。"); return; }
        SetNotice(save(savedSettings with { VoiceId = voice, Rate = Rate.Value, Volume = (float)Volume.Value,
            StartWithWindows = AutoStart.IsChecked == true }) ?? "设置已保存，下次朗读生效。");
    }
    private void SaveAutomatic(Settings updated)
    {
        var error = save(updated);
        if (error is not null) { SetAutomaticSettings(savedSettings); SetNotice(error); }
        else SetNotice("自动朗读规则已保存，立即生效。");
    }
    private void AutoSpeak_Click(object sender, RoutedEventArgs e) => SaveAutomatic(savedSettings with { AutoSpeak = AutoSpeak.IsChecked == true });
    private void Rule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox { DataContext: ApplicationRule rule } box)
            SaveAutomatic(savedSettings with { Applications = (savedSettings.Applications ?? []).Select(r =>
                r == rule ? r with { AutoSpeak = box.IsChecked == true } : r).ToArray() });
    }
    private void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (Rules.SelectedItem is ApplicationRule rule)
            SaveAutomatic(savedSettings with { Applications = (savedSettings.Applications ?? []).Where(r => r != rule).ToArray() });
    }
    private async void AddApplication_Click(object sender, RoutedEventArgs e)
    {
        AddApplication.IsEnabled = false;
        SetAutomaticStatus("请在 10 秒内切换到目标应用，切换后立即添加；完成后不会抢回焦点。");
        try { SetAutomaticStatus(await addForeground() ?? "前台应用已添加并启用。"); }
        catch (Exception ex) { SetAutomaticStatus($"添加应用失败（{ex.GetType().Name}）。"); }
        finally { AddApplication.IsEnabled = true; }
    }
    private void Pause_Click(object sender, RoutedEventArgs e) => pause();
    private void Stop_Click(object sender, RoutedEventArgs e) => stop();
    private void Exit_Click(object sender, RoutedEventArgs e) => exit();
}
