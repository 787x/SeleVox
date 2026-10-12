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
    private readonly AzureCredentials credentials = new();
    private CancellationTokenSource? voiceQuery;
    private bool refreshingOutputs;
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
        Region.Text = settings.AzureRegion;
        if (settings.AzureVoice is { } cloudVoice)
        {
            CloudVoice.ItemsSource = new[] { new VoiceChoice(cloudVoice, cloudVoice + "（已保存；可查询区域列表）") };
            CloudVoice.SelectedValue = cloudVoice;
        }
        AutomaticCloud.IsChecked = settings.AllowAutomaticCloud;
        Provider.SelectedIndex = (int)settings.Provider;
        UpdateCredentialStatus();
        _ = RefreshOutputsAsync(settings.OutputDeviceIds ?? [Settings.DefaultOutput]);
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
        voiceQuery?.Cancel();
        if (((App)System.Windows.Application.Current).IsExiting is false) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var provider = Provider.SelectedIndex == 1 ? TtsProvider.Azure : TtsProvider.Windows;
        if (provider == TtsProvider.Windows && Voice.SelectedValue is not string) { SetNotice("请先选择可用的本地语音。"); return; }
        if (provider == TtsProvider.Azure && (!Settings.IsRegionValid(Region.Text.Trim()) || CloudVoice.SelectedValue is not string))
        { SetNotice("请填写 Region 并查询选择可用的 Azure 语音。"); return; }
        if (refreshingOutputs) { SetNotice("正在刷新设备，请稍后保存。"); return; }
        SetNotice(save(savedSettings with { VoiceId = Voice.SelectedValue as string, Rate = Rate.Value, Volume = (float)Volume.Value,
            Provider = provider, AzureRegion = provider == TtsProvider.Azure ? Region.Text.Trim() : savedSettings.AzureRegion,
            AzureVoice = provider == TtsProvider.Azure ? CloudVoice.SelectedValue as string : savedSettings.AzureVoice,
            AllowAutomaticCloud = AutomaticCloud.IsChecked == true,
            OutputDeviceIds = Outputs.SelectedItems.Cast<OutputChoice>().Select(d => d.Id).ToArray(),
            StartWithWindows = AutoStart.IsChecked == true }) ?? "设置已保存，下次朗读生效。");
    }
    private void Provider_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LocalPanel is null || AzurePanel is null) return;
        LocalPanel.Visibility = Provider.SelectedIndex == 1 ? Visibility.Collapsed : Visibility.Visible;
        AzurePanel.Visibility = Provider.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void AzureRegion_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        voiceQuery?.Cancel();
        if (CloudVoice is not null) CloudVoice.ItemsSource = null;
    }
    private void UpdateCredentialStatus()
    {
        try { CredentialStatus.Text = credentials.Read() is null ? "尚未保存 Key" : "已保存 Key（不显示内容）"; }
        catch (UserFeedbackException ex) { CredentialStatus.Text = ex.Message; }
    }
    private void SaveKey_Click(object sender, RoutedEventArgs e)
    {
        try { voiceQuery?.Cancel(); credentials.Write(AzureKey.Password.Trim()); AzureKey.Clear(); UpdateCredentialStatus(); SetNotice("Key 已安全保存。"); }
        catch (UserFeedbackException ex) { SetNotice(ex.Message); }
    }
    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        try { voiceQuery?.Cancel(); credentials.Clear(); AzureKey.Clear(); UpdateCredentialStatus(); SetNotice("Azure Key 已清除，本地朗读不受影响。"); }
        catch (UserFeedbackException ex) { SetNotice(ex.Message); }
    }
    private async void QueryVoices_Click(object sender, RoutedEventArgs e)
    {
        voiceQuery?.Cancel();
        using var query = new CancellationTokenSource(); voiceQuery = query;
        QueryVoices.IsEnabled = false; SetNotice("正在查询 Azure 区域语音…");
        var previous = CloudVoice.SelectedValue as string;
        try
        {
            var voices = await new AzureSpeech().GetVoicesAsync(Region.Text.Trim(), query.Token);
            query.Token.ThrowIfCancellationRequested();
            CloudVoice.ItemsSource = voices; CloudVoice.SelectedValue = previous;
            SetNotice(voices.Count == 0 ? "此区域没有可用语音。" : "查询完成，请选择语音并保存设置。");
        }
        catch (OperationCanceledException) { SetNotice("Azure 语音查询已取消。"); }
        catch (Exception ex) { SetNotice(ex is UserFeedbackException ? ex.Message : $"语音查询失败（{ex.GetType().Name}）。"); }
        finally { if (ReferenceEquals(voiceQuery, query)) voiceQuery = null; QueryVoices.IsEnabled = true; }
    }
    private async void RefreshOutputs_Click(object sender, RoutedEventArgs e) =>
        await RefreshOutputsAsync(Outputs.SelectedItems.Cast<OutputChoice>().Select(d => d.Id).ToArray());
    private async Task RefreshOutputsAsync(string[] selected)
    {
        if (refreshingOutputs) return;
        refreshingOutputs = true;
        try
        {
            var devices = (await Task.Run(AudioOutputs.GetDevices)).ToList();
            foreach (var id in selected.Except(devices.Select(d => d.Id))) devices.Add(new(id, "不可用（保留已选设备）：" + id));
            Outputs.ItemsSource = devices;
            foreach (var choice in devices.Where(d => selected.Contains(d.Id))) Outputs.SelectedItems.Add(choice);
        }
        catch (Exception ex) { SetNotice($"无法枚举音频设备（{ex.GetType().Name}），保留设备配置并请稍后刷新。");
            var devices = selected.Select(id => new OutputChoice(id, "未能查询：" + id)).ToArray();
            Outputs.ItemsSource = devices; foreach (var choice in devices) Outputs.SelectedItems.Add(choice);
        }
        finally { refreshingOutputs = false; }
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
