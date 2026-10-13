using System.Windows;
using System.Windows.Controls;
using SeleVox.Core;
using Microsoft.Win32;

namespace SeleVox.App;

public partial class AudioWorkbenchWindow : Window
{
    internal const string SampleText = "你好，这是 SeleVox 的默认试音。Hello, this is a SeleVox voice sample.";
    private readonly Func<Settings> getSettings;
    private readonly Action showSettings, pause, stop;
    private readonly Func<GeneratedAudio, CancellationToken, Task> play;
    private readonly AudioGeneration generator;
    private readonly string preferencesPath;
    private WorkbenchPreferences preferences;
    private CancellationTokenSource? generation, query;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? preview;
    private long epoch;
    private bool closed;
    private GeneratedAudio? result;
    private MiMoStyleTags styles;
    private IReadOnlyList<EdgeVoiceChoice> edgeVoices = [];
    private string? edgeSelection;

    internal AudioWorkbenchWindow(Func<Settings> getSettings, Action showSettings,
        Func<GeneratedAudio, CancellationToken, Task> play, Action pause, Action stop,
        string preferencesPath, ISpeechSynthesis? speech = null)
    {
        InitializeComponent(); this.getSettings = getSettings; this.showSettings = showSettings;
        this.play = play; this.pause = pause; this.stop = stop; this.preferencesPath = preferencesPath;
        generator = new(speech ?? new SpeechAudio());
        preferences = WorkbenchPreferences.Load(preferencesPath, getSettings());
        var s = preferences.Tts; styles = new(s.MiMoOverallStyles);
        Provider.ItemsSource = Enum.GetValues<TtsProvider>();
        try { LocalVoice.ItemsSource = SpeechAudio.GetVoices(); LocalVoice.SelectedValue = s.VoiceId;
            if (s.VoiceId is null) LocalVoice.SelectedValue = Windows.Media.SpeechSynthesis.SpeechSynthesizer.DefaultVoice.Id; }
        catch { GenerationStatus.Text = "无法枚举本地语音，请在 Windows 设置中安装语音。"; }
        Region.Text = s.AzureRegion;
        if (s.AzureVoice is { } azure) { AzureVoice.ItemsSource = new[] { new VoiceChoice(azure, azure) }; AzureVoice.SelectedValue = azure; }
        edgeSelection = s.EdgeVoice; Locale.Text = s.EdgeLocaleFilter; FilterEdge(); Pitch.Value = s.EdgePitchHz;
        MiMoModel.Text = s.MiMoModel; MiMoVoice.ItemsSource = Settings.MiMoVoices; MiMoVoice.SelectedItem = s.MiMoVoice; MiMoStyle.Text = s.MiMoStyle;
        RefreshMiMoStyles(); OpenAiModel.Text = s.OpenAiModel; OpenAiVoice.Text = s.OpenAiVoice; Instructions.Text = s.OpenAiInstructions;
        Rate.Value = s.Rate; Provider.SelectedItem = s.Provider;
        Width = Math.Clamp(preferences.Width, 620, 1600); Height = Math.Clamp(preferences.Height, 620, 1400);
        if (double.IsFinite(preferences.Left) && double.IsFinite(preferences.Top))
        {
            var bounds = new Rect(preferences.Left, preferences.Top, Width, Height);
            var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (Rect.Intersect(bounds, desktop) is { Width: >= 120, Height: >= 80 }) { Left = preferences.Left; Top = preferences.Top; }
        }
        Activated += (_, _) => RefreshServiceStatus();
        Closed += (_, _) => CloseWorkbench();
    }
    internal Settings Draft() => getSettings() with {
        Provider = Provider.SelectedItem is TtsProvider p ? p : TtsProvider.Windows,
        VoiceId = LocalVoice.SelectedValue as string, Rate = Rate.Value, AzureRegion = Region.Text.Trim(), AzureVoice = AzureVoice.SelectedValue as string,
        EdgeVoice = EdgeVoice.SelectedValue as string ?? edgeSelection, EdgeLocaleFilter = Locale.Text.Trim(), EdgePitchHz = (int)Math.Round(Pitch.Value),
        MiMoModel = MiMoModel.Text.Trim(), MiMoVoice = MiMoVoice.SelectedItem as string ?? "冰糖", MiMoStyle = MiMoStyle.Text,
        MiMoOverallStyles = styles.Selected.ToArray(), MiMoStreaming = false,
        OpenAiModel = OpenAiModel.Text.Trim(), OpenAiVoice = OpenAiVoice.Text.Trim(), OpenAiInstructions = Instructions.Text };
    // An unfinished field in another provider's panel must not block the selected provider.
    private Settings EffectiveDraft()
    {
        var d = Draft(); var s = getSettings() with { Provider = d.Provider, Rate = d.Rate };
        return d.Provider switch {
            TtsProvider.Windows => s with { VoiceId = d.VoiceId },
            TtsProvider.Azure => s with { AzureRegion = d.AzureRegion, AzureVoice = d.AzureVoice },
            TtsProvider.Edge => s with { EdgeVoice = d.EdgeVoice, EdgeLocaleFilter = d.EdgeLocaleFilter, EdgePitchHz = d.EdgePitchHz },
            TtsProvider.MiMo => s with { MiMoModel = d.MiMoModel, MiMoVoice = d.MiMoVoice, MiMoStyle = d.MiMoStyle, MiMoOverallStyles = d.MiMoOverallStyles, MiMoStreaming = false },
            _ => s with { OpenAiModel = d.OpenAiModel, OpenAiVoice = d.OpenAiVoice, OpenAiInstructions = d.OpenAiInstructions } };
    }
    private void Provider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (WindowsPanel is null) return;
        var p = Provider.SelectedItem is TtsProvider selected ? selected : TtsProvider.Windows;
        WindowsPanel.Visibility = p == TtsProvider.Windows ? Visibility.Visible : Visibility.Collapsed;
        AzurePanel.Visibility = p == TtsProvider.Azure ? Visibility.Visible : Visibility.Collapsed;
        EdgePanel.Visibility = p == TtsProvider.Edge ? Visibility.Visible : Visibility.Collapsed;
        MiMoPanel.Visibility = p == TtsProvider.MiMo ? Visibility.Visible : Visibility.Collapsed;
        OpenAiPanel.Visibility = p == TtsProvider.OpenAiCompatible ? Visibility.Visible : Visibility.Collapsed;
        RefreshServiceStatus();
    }
    private void RefreshServiceStatus()
    {
        var s = getSettings(); ServiceAddress.Text = s.OpenAiBaseUrl;
        if (CredentialStatus is null) return;
        try
        {
            var p = Provider.SelectedItem is TtsProvider selected ? selected : TtsProvider.Windows;
            var configured = p switch {
                TtsProvider.Azure => !string.IsNullOrWhiteSpace(new AzureCredentials().Read()),
                TtsProvider.MiMo => !string.IsNullOrWhiteSpace(AzureCredentials.MiMo().Read()),
                TtsProvider.OpenAiCompatible => !string.IsNullOrWhiteSpace(AzureCredentials.OpenAi(s.OpenAiBaseUrl).Read()), _ => true };
            CredentialStatus.Text = p is TtsProvider.Windows or TtsProvider.Edge ? "此 Provider 无需 API Key。" :
                configured ? "主设置已配置凭据；工作台不显示或存储 Key。" : "未配置凭据：请通过上方入口在主设置保存 Key。";
        }
        catch (UserFeedbackException ex) { CredentialStatus.Text = ex.Message; }
    }
    private void RefreshMiMoStyles()
    {
        RefreshMiMoSelection(); RefreshMiMoPresets();
    }
    private void RefreshMiMoSelection()
    {
        if (MiMoSelectedStyles is null || MiMoPresetGroups is null) return;
        MiMoSelectedStyles.ItemsSource = styles.Selected.ToArray();
        var prefix = MiMoStyleTags.GetPrefix(styles.Selected);
        MiMoStylePrefix.Text = prefix.Length == 0 ? "实际前缀：无（保持原文）" : "实际前缀：" + prefix;
        MiMoStyleHint.Text = styles.Selected.Any(MiMoStyleTags.IsSinging)
            ? "唱歌为独立模式，适合中文歌词；普通文章未必能自然演唱。选择其他风格将退出唱歌模式。"
            : "按选择顺序组合，最多 16 项。修改用于下一次生成；可保存为工作台默认参数。";
    }
    private void RefreshMiMoPresets()
    {
        if (MiMoPresetGroups is null || MiMoStyleSearch is null) return;
        MiMoPresetGroups.Children.Clear();
        foreach (var category in MiMoStylePresets.Search(MiMoStyleSearch.Text))
        {
            MiMoPresetGroups.Children.Add(new System.Windows.Controls.TextBlock
            { Text = category.Name, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 4) });
            var row = new System.Windows.Controls.WrapPanel();
            foreach (var name in category.Styles)
            {
                var button = new System.Windows.Controls.Primitives.ToggleButton
                { Content = name, Tag = name, IsChecked = styles.Selected.Contains(name),
                    Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 6) };
                button.Click += ToggleMiMoStyle_Click; row.Children.Add(button);
            }
            MiMoPresetGroups.Children.Add(row);
        }
        if (MiMoPresetGroups.Children.Count == 0)
            MiMoPresetGroups.Children.Add(new System.Windows.Controls.TextBlock { Text = "没有匹配的预设，可在下方添加自定义风格。" });
    }
    private void MiMoStyleSearch_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => RefreshMiMoPresets();
    private void ToggleMiMoStyle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { Tag: string name }) return;
        try
        {
            if (styles.Selected.Contains(name)) styles.Remove(name); else styles.Add(name);
            MiMoStyleError.Text = "";
            // Preserve the focused toggle for keyboard users while updating all selected states.
            RefreshMiMoSelection();
        }
        catch (UserFeedbackException ex) { MiMoStyleError.Text = ex.Message; }
        foreach (var row in MiMoPresetGroups.Children.OfType<System.Windows.Controls.WrapPanel>())
            foreach (var item in row.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>())
                item.IsChecked = styles.Selected.Contains((string)item.Tag);
    }
    private void AddMiMoStyle_Click(object sender, RoutedEventArgs e)
    {
        try { styles.Add(MiMoCustomStyle.Text); MiMoCustomStyle.Clear(); MiMoStyleError.Text = ""; RefreshMiMoStyles(); }
        catch (UserFeedbackException ex) { MiMoStyleError.Text = ex.Message; }
    }
    private void MiMoCustomStyle_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { AddMiMoStyle_Click(sender, e); e.Handled = true; }
    }
    private void RemoveMiMoStyle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string name }) { styles.Remove(name); MiMoStyleError.Text = ""; RefreshMiMoStyles(); }
    }
    private void ClearMiMoStyles_Click(object sender, RoutedEventArgs e)
    { styles.Clear(); MiMoStyleError.Text = ""; RefreshMiMoStyles(); }
    private void FilterEdge()
    {
        if (EdgeVoice is null) return;
        edgeSelection = EdgeVoice.SelectedValue as string ?? edgeSelection;
        var filtered = edgeVoices.Where(v => string.IsNullOrWhiteSpace(Locale.Text) || v.Locale.Contains(Locale.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (edgeSelection is { } id && !filtered.Any(v => v.Id == id)) filtered.Insert(0, new(id, id + "（当前选择）", ""));
        EdgeVoice.ItemsSource = filtered; EdgeVoice.SelectedValue = edgeSelection;
    }
    private void Locale_Changed(object sender, TextChangedEventArgs e) => FilterEdge();
    private async void AzureQuery_Click(object sender, RoutedEventArgs e) => await QueryAsync(true);
    private async void EdgeQuery_Click(object sender, RoutedEventArgs e) => await QueryAsync(false);
    private async Task QueryAsync(bool azure)
    {
        query?.Cancel(); using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); query = request;
        var region = Region.Text.Trim(); GenerationStatus.Text = "正在查询语音…";
        try
        {
            if (azure)
            {
                var voices = await Task.Run(() => new AzureSpeech().GetVoicesAsync(region, request.Token));
                if (closed || request.IsCancellationRequested || Region.Text.Trim() != region) return;
                var selected = AzureVoice.SelectedValue; AzureVoice.ItemsSource = voices; AzureVoice.SelectedValue = selected;
                if (AzureVoice.SelectedIndex < 0 && voices.Count > 0) AzureVoice.SelectedIndex = 0;
            }
            else { var voices = await new EdgeSpeech().GetVoicesAsync(request.Token);
                if (closed || request.IsCancellationRequested) return;
                edgeSelection = EdgeVoice.SelectedValue as string ?? edgeSelection; edgeVoices = voices; FilterEdge(); }
            GenerationStatus.Text = "语音列表已更新。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed && !request.IsCancellationRequested) Feedback(ex, "语音查询失败，请检查网络及主设置中的凭据。"); }
        finally { if (ReferenceEquals(query, request)) query = null; }
    }
    private async void Sample_Click(object sender, RoutedEventArgs e) => await GenerateAsync(true);
    private async void Generate_Click(object sender, RoutedEventArgs e) => await GenerateAsync(false);
    internal async Task GenerateAsync(bool sample)
    {
        generation?.Cancel(); var id = ++epoch;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); generation = request;
        var text = sample ? SampleText : InputText.Text;
        edgeSelection = EdgeVoice.SelectedValue as string ?? edgeSelection;
        var settings = EffectiveDraft(); GenerationStatus.Text = "正在合成完整音频…可取消；完成前保留上一次成功结果。";
        try
        {
            if (settings.Provider == TtsProvider.Windows && LocalVoice.SelectedValue is not string)
                throw new UserFeedbackException("请选择可用的 Windows 本地语音；缺失的语音请先在系统设置中安装。");
            var audio = await Task.Run(() => generator.GenerateAsync(text, settings, request.Token));
            if (closed || id != epoch || request.IsCancellationRequested) return;
            result = audio; ResultSummary.Text = audio.Description; Preview.IsEnabled = SaveAudio.IsEnabled = true;
            GenerationStatus.Text = sample ? "默认试音已生成；正在试听。" : "生成成功。试听和保存使用此结果，不重新合成。";
            if (sample) await PreviewAsync();
        }
        catch (OperationCanceledException) { if (!closed && id == epoch) GenerationStatus.Text = "生成已取消；上一次成功结果保留。"; }
        catch (Exception ex) { if (!closed && id == epoch) Feedback(ex, "合成失败，请检查参数、网络及主设置中的凭据。"); }
        finally { if (ReferenceEquals(generation, request)) generation = null; }
    }
    private async void Preview_Click(object sender, RoutedEventArgs e) => await PreviewAsync();
    internal async Task PreviewAsync()
    {
        if (result is not { } audio || closed) return;
        preview?.Cancel(); preview?.Dispose(); preview = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try { await play(audio, preview.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) Feedback(ex, "试听失败，请检查输出设备。"); }
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (result is not { } audio) return;
        var dialog = new SaveFileDialog { Filter = "WAVE 音频 (*.wav)|*.wav", DefaultExt = ".wav", AddExtension = true, FileName = "SeleVox.wav", OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true || closed) return;
        var exists = File.Exists(dialog.FileName);
        if (exists && MessageBox.Show(this, "文件已存在，是否覆盖？", "保存 WAV", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        SaveAudio.IsEnabled = false;
        try { await Task.Run(() => audio.SaveAsync(dialog.FileName, exists, lifetime.Token)); if (!closed) GenerationStatus.Text = "WAV 已保存（原始音频，不含播放起音补偿）。"; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) Feedback(ex, "保存失败；请检查目录权限或文件是否已存在。"); }
        finally { if (!closed) SaveAudio.IsEnabled = result is not null; }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelGeneration();
    internal void CancelGeneration() { ++epoch; generation?.Cancel(); GenerationStatus.Text = "生成已取消；上一次成功结果保留。"; }
    private void Pause_Click(object sender, RoutedEventArgs e) => pause();
    private void Stop_Click(object sender, RoutedEventArgs e) { CancelGeneration(); stop(); }
    private void Settings_Click(object sender, RoutedEventArgs e) => showSettings();
    private void Defaults_Click(object sender, RoutedEventArgs e)
    {
        try { edgeSelection = EdgeVoice.SelectedValue as string ?? edgeSelection; var draft = Draft();
            if (!draft.IsValid) throw new UserFeedbackException("参数无效，未保存默认值。");
            preferences = preferences with { Tts = WorkbenchPreferences.OnlyTts(draft) }; SavePreferences(); GenerationStatus.Text = "工作台默认参数已保存，日常朗读设置未改变。"; }
        catch (Exception ex) { Feedback(ex, "工作台参数保存失败。"); }
    }
    private void SavePreferences()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height) : RestoreBounds;
        (preferences with { Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height }).Save(preferencesPath);
    }
    internal void SetPlaybackStatus(Status status) { if (!closed) PlaybackStatus.Text = "统一播放：" + status.Message; }
    private void Feedback(Exception ex, string fallback) => GenerationStatus.Text = ex is UserFeedbackException ? ex.Message + "（凭据仅在主设置管理）" : fallback;
    private void CloseWorkbench()
    {
        closed = true; ++epoch; generation?.Cancel(); query?.Cancel(); lifetime.Cancel(); preview?.Dispose(); result = null;
        try { SavePreferences(); } catch { /* No content or key is logged. */ }
        lifetime.Dispose();
    }
}
