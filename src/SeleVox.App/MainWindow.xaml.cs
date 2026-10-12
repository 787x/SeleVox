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
    private readonly Action cancelAutomatic;
    private readonly Func<CancellationToken, Task<IReadOnlyList<EdgeVoiceChoice>>> queryEdge;
    private IReadOnlyList<EdgeVoiceChoice> edgeVoices = [];
    private string? edgeSelection;
    private bool filteringEdge;
    private bool refreshingOutputs;
    public string? VoiceWarning { get; }
    internal MainWindow(Settings settings, Func<Settings, string?> save, Action pause, Action stop, Action exit,
        Func<Task<string?>> addForeground, Action cancelAutomatic,
        Func<CancellationToken, Task<IReadOnlyList<EdgeVoiceChoice>>>? queryEdge = null)
    {
        InitializeComponent(); this.save = save; this.pause = pause; this.stop = stop; this.exit = exit;
        this.addForeground = addForeground; this.cancelAutomatic = cancelAutomatic; savedSettings = settings; SetAutomaticSettings(settings);
        this.queryEdge = queryEdge ?? new EdgeSpeech().GetVoicesAsync;
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
        AutomaticEdge.IsChecked = settings.AllowAutomaticEdge;
        edgeSelection = settings.EdgeVoice;
        EdgePitch.Value = settings.EdgePitchHz;
        EdgeLocale.Text = settings.EdgeLocaleFilter;
        FilterEdgeVoices();
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
        UpdateProviderSaveHint();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        voiceQuery?.Cancel();
        if (System.Windows.Application.Current is App { IsExiting: false }) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var provider = (TtsProvider)Provider.SelectedIndex;
        if (provider == TtsProvider.Windows && Voice.SelectedValue is not string) { SetNotice("请先选择可用的本地语音。"); return; }
        if (provider == TtsProvider.Azure && (!Settings.IsRegionValid(Region.Text.Trim()) || CloudVoice.SelectedValue is not string))
        { SetNotice("请填写 Region 并查询选择可用的 Azure 语音。"); return; }
        if (provider == TtsProvider.Edge && string.IsNullOrWhiteSpace(edgeSelection))
        { SetNotice("请查询并选择 Edge 在线语音。"); return; }
        if (refreshingOutputs) { SetNotice("正在刷新设备，请稍后保存。"); return; }
        var error = save(savedSettings with { VoiceId = Voice.SelectedValue as string, Rate = Rate.Value, Volume = (float)Volume.Value,
            Provider = provider, AzureRegion = provider == TtsProvider.Azure ? Region.Text.Trim() : savedSettings.AzureRegion,
            AzureVoice = provider == TtsProvider.Azure ? CloudVoice.SelectedValue as string : savedSettings.AzureVoice,
            AllowAutomaticCloud = AutomaticCloud.IsChecked == true,
            EdgeVoice = edgeSelection, EdgePitchHz = (int)EdgePitch.Value, EdgeLocaleFilter = EdgeLocale.Text.Trim(),
            AllowAutomaticEdge = AutomaticEdge.IsChecked == true,
            OutputDeviceIds = Outputs.SelectedItems.Cast<OutputChoice>().Select(d => d.Id).ToArray(),
            StartWithWindows = AutoStart.IsChecked == true });
        SetNotice(error ?? "设置已保存，下次朗读生效。");
        if (error is null) OutputSaveHint.Text = "设备选择已保存，用于下一次朗读。";
    }
    private void Outputs_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (refreshingOutputs || OutputSaveHint is null) return;
        var selected = Outputs.SelectedItems.Cast<OutputChoice>().Select(d => d.Id).ToArray();
        var saved = savedSettings.OutputDeviceIds ?? [];
        var same = (selected.Length == 0 ? [Settings.DefaultOutput] : selected).ToHashSet()
            .SetEquals(saved.Length == 0 ? [Settings.DefaultOutput] : saved);
        OutputSaveHint.Text = same ? "设备选择与已保存设置一致。" : "设备选择尚未保存：请点击下方“保存设置”，下一次朗读才会使用新设备。";
    }
    private void Provider_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LocalPanel is null || AzurePanel is null || EdgePanel is null) return;
        voiceQuery?.Cancel(); cancelAutomatic?.Invoke();
        LocalPanel.Visibility = Provider.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        AzurePanel.Visibility = Provider.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        EdgePanel.Visibility = Provider.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        UpdateProviderSaveHint();
    }
    private void UpdateProviderSaveHint()
    {
        if (savedSettings is null || ProviderSaveHint is null) return;
        var active = savedSettings.Provider switch
        {
            TtsProvider.Edge => "Edge 在线语音",
            TtsProvider.Azure => "Azure Speech",
            _ => "Windows 本地 TTS"
        };
        ProviderSaveHint.Text = $"当前朗读使用：{active}。" +
            (Provider.SelectedIndex >= 0 && Provider.SelectedIndex != (int)savedSettings.Provider
                ? " Provider 切换尚未保存；请选择语音，然后点击“保存设置”生效。"
                : " 语音、语速、音调和设备修改后请保存，下次朗读生效。");
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
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!query.IsCancellationRequested && ReferenceEquals(voiceQuery, query)) SetNotice(ex is UserFeedbackException ? ex.Message : "Azure 语音查询失败。"); }
        finally { if (ReferenceEquals(voiceQuery, query)) { voiceQuery = null; QueryVoices.IsEnabled = true; } }
    }
    private async void QueryEdgeVoices_Click(object sender, RoutedEventArgs e)
    {
        voiceQuery?.Cancel();
        using var query = new CancellationTokenSource(); voiceQuery = query;
        SetNotice("正在查询 Edge 在线语音…");
        try
        {
            var voices = await queryEdge(query.Token);
            if (query.IsCancellationRequested || !ReferenceEquals(voiceQuery, query) || Provider.SelectedIndex != 2) return;
            edgeVoices = voices; FilterEdgeVoices();
            SetNotice(voices.Count == 0 ? "Edge 没有返回可用语音，已保留原选择。" : "查询完成，请选择语音并保存；已保存但不在列表的语音会保留并标注。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!query.IsCancellationRequested && ReferenceEquals(voiceQuery, query))
                SetNotice((ex is UserFeedbackException ? ex.Message : "Edge 语音查询失败。") + " 已保留原语音选择。");
        }
        finally { if (ReferenceEquals(voiceQuery, query)) { voiceQuery = null; QueryVoices.IsEnabled = true; } }
    }
    private void EdgeLocale_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => FilterEdgeVoices();
    private void FilterEdgeVoices()
    {
        if (EdgeVoice is null || EdgeLocale is null) return;
        filteringEdge = true;
        try
        {
            var filter = EdgeLocale.Text.Trim();
            var voices = edgeVoices.Where(v => v.Locale.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (edgeSelection is { } id && voices.All(v => v.Id != id))
                voices.Insert(0, edgeVoices.FirstOrDefault(v => v.Id == id) ?? new(id, id + "（保留选择；尚未确认在线可用）", ""));
            EdgeVoice.ItemsSource = voices; EdgeVoice.SelectedValue = edgeSelection;
        }
        finally { filteringEdge = false; }
    }
    private void EdgeVoice_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!filteringEdge && EdgeVoice.SelectedValue is string id) edgeSelection = id;
    }
    private void AutomaticEdge_Click(object sender, RoutedEventArgs e)
    {
        SaveAutomatic(savedSettings with { AllowAutomaticEdge = AutomaticEdge.IsChecked == true });
        AutomaticEdge.IsChecked = savedSettings.AllowAutomaticEdge;
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
