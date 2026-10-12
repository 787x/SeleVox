using System.ComponentModel;
using System.Windows;
using SeleVox.Core;
namespace SeleVox.App;
public partial class MainWindow : Window
{
    private readonly Func<Settings, string?> save;
    private readonly Action pause, stop, exit;
    public string? VoiceWarning { get; }
    internal MainWindow(Settings settings, Func<Settings, string?> save, Action pause, Action stop, Action exit)
    {
        InitializeComponent(); this.save = save; this.pause = pause; this.stop = stop; this.exit = exit;
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
    protected override void OnClosing(CancelEventArgs e)
    {
        if (((App)System.Windows.Application.Current).IsExiting is false) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Voice.SelectedValue is not string voice) { SetNotice("请先选择可用的本地语音。"); return; }
        SetNotice(save(new(voice, Rate.Value, (float)Volume.Value, AutoStart.IsChecked == true)) ?? "设置已保存，下次朗读生效。");
    }
    private void Pause_Click(object sender, RoutedEventArgs e) => pause();
    private void Stop_Click(object sender, RoutedEventArgs e) => stop();
    private void Exit_Click(object sender, RoutedEventArgs e) => exit();
}
