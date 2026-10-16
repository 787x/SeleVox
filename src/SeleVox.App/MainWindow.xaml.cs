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
    internal Action? LlmCredentialsChanged { get; set; }
    internal Action? OpenAudioWorkbench { get; set; }
    private void AudioWorkbench_Click(object sender, RoutedEventArgs e) => OpenAudioWorkbench?.Invoke();
    private readonly Func<CancellationToken, Task<IReadOnlyList<EdgeVoiceChoice>>> queryEdge;
    private IReadOnlyList<EdgeVoiceChoice> edgeVoices = [];
    private string? edgeSelection;
    private bool filteringEdge;
    private bool refreshingOutputs;
    private MiMoStyleTags miMoStyles = new();
    private List<PromptPreset> customPrompts = [];
    private bool refreshingPrompts;
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
        StartupSilence.Value = settings.StartupSilenceMilliseconds;
        OcrLanguageChoice.SelectedIndex = (int)settings.OcrLanguage;
        Region.Text = settings.AzureRegion;
        if (settings.AzureVoice is { } cloudVoice)
        {
            CloudVoice.ItemsSource = new[] { new VoiceChoice(cloudVoice, cloudVoice + "（已保存；可查询区域列表）") };
            CloudVoice.SelectedValue = cloudVoice;
        }
        AutomaticCloud.IsChecked = settings.AllowAutomaticCloud;
        AutomaticEdge.IsChecked = settings.AllowAutomaticEdge;
        MiMoVoice.ItemsSource = Settings.MiMoVoices; MiMoVoice.SelectedItem = settings.MiMoVoice; MiMoModel.Text = settings.MiMoModel;
        MiMoStreaming.IsChecked = settings.MiMoStreaming;
        MiMoStyle.Text = settings.MiMoStyle; AutomaticMiMo.IsChecked = settings.AllowAutomaticMiMo;
        miMoStyles = new(settings.MiMoOverallStyles); RefreshMiMoStyles();
        OpenAiBaseUrl.Text = settings.OpenAiBaseUrl; OpenAiModel.Text = settings.OpenAiModel;
        OpenAiVoice.Text = settings.OpenAiVoice; OpenAiInstructions.Text = settings.OpenAiInstructions;
        LlmBaseUrl.Text = settings.LlmBaseUrl; LlmModel.Text = settings.LlmModel; LlmSystemPrompt.Text = settings.LlmSystemPrompt;
        LlmStreaming.IsChecked = settings.LlmStreaming; LlmAutoRead.IsChecked = settings.LlmAutoRead;
        LlmReadingStrategy.SelectedIndex = (int)settings.LlmReadingStrategy;
        ThinkingProtocolChoice.SelectedIndex = (int)settings.LlmThinkingProtocol;
        ThinkingOptionChoice.SelectedValue = settings.LlmThinking;
        customPrompts = (settings.CustomPrompts ?? []).ToList(); RefreshPrompts(settings.SelectedPromptId);
        UpdateLlmCredentialStatus();
        AutomaticOpenAi.IsChecked = settings.AllowAutomaticOpenAi && settings.OpenAiAuthorizedBaseUrl == Settings.NormalizeServiceUrl(settings.OpenAiBaseUrl);
        UpdateHttpCredentialStatus();
        edgeSelection = settings.EdgeVoice;
        EdgePitch.Value = settings.EdgePitchHz;
        EdgeLocale.Text = settings.EdgeLocaleFilter;
        FilterEdgeVoices();
        Provider.SelectedIndex = (int)settings.Provider;
        UpdateCredentialStatus();
        _ = RefreshOutputsAsync(settings.OutputDeviceIds ?? [Settings.DefaultOutput]);
        InitializeVirtualMicrophone(settings);
    }
    public void SetNotice(string message) => Notice.Text = message;
    public void SetStatus(Status status) { StatusText.Text = status.Message; if (status.State != ReadingState.Idle) virtualTest?.Cancel(); }
    public void SetAutomaticStatus(string message) => AutomaticStatus.Text = message;
    public void SetStartupSettings(bool enabled)
    {
        savedSettings = savedSettings with { StartWithWindows = enabled };
        AutoStart.IsChecked = enabled;
    }
    public void SetAutomaticSettings(Settings settings)
    {
        savedSettings = settings; AutoSpeak.IsChecked = settings.AutoSpeak; AutoAsk.IsChecked = settings.AutoAsk;
        SetStartupSettings(settings.StartWithWindows);
        if (virtualTimer is not null) UpdateVirtualStatus();
        diagnosticsWindow?.Refresh();
        AutomaticLlm.IsChecked = settings.AllowsAutomaticLlm;
        AutomaticLlmStatus.Text = $"自动上传：{(settings.AllowsAutomaticLlm ? "已授权" : "未授权")} · {Settings.NormalizeServiceUrl(settings.LlmBaseUrl)}（可能产生费用）";
        AutomaticMiMo.IsChecked = settings.AllowAutomaticMiMo;
        AutomaticOpenAi.IsChecked = settings.AllowAutomaticOpenAi && settings.OpenAiAuthorizedBaseUrl == Settings.NormalizeServiceUrl(settings.OpenAiBaseUrl);
        Rules.ItemsSource = settings.Applications ?? [];
        UpdateProviderSaveHint();
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        voiceQuery?.Cancel();
        virtualTest?.Cancel();
        diagnosticsWindow?.Close();
        if (System.Windows.Application.Current is App { IsExiting: false }) { e.Cancel = true; Hide(); }
        base.OnClosing(e);
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (VirtualMicEnabled.IsChecked == true && virtualRoute is not { RecordingVerified: true })
        { SetNotice("请先选择同一 VB-CABLE 的端点并通过录音验证，再启用。"); return; }
        var provider = (TtsProvider)Provider.SelectedIndex;
        if (provider == TtsProvider.Windows && Voice.SelectedValue is not string) { SetNotice("请先选择可用的本地语音。"); return; }
        if (provider == TtsProvider.Azure && (!Settings.IsRegionValid(Region.Text.Trim()) || CloudVoice.SelectedValue is not string))
        { SetNotice("请填写 Region 并查询选择可用的 Azure 语音。"); return; }
        if (provider == TtsProvider.Edge && string.IsNullOrWhiteSpace(edgeSelection))
        { SetNotice("请查询并选择 Edge 在线语音。"); return; }
        if (refreshingOutputs) { SetNotice("正在刷新设备，请稍后保存。"); return; }
        var address = Settings.NormalizeServiceUrl(OpenAiBaseUrl.Text);
        var llmAddress = Settings.NormalizeServiceUrl(LlmBaseUrl.Text);
        if (llmAddress is null) { SetNotice("AI 服务地址无效：远程服务须使用 HTTPS，本机回环可用 HTTP。"); return; }
        if (provider == TtsProvider.MiMo && string.IsNullOrWhiteSpace(MiMoModel.Text)) { SetNotice("请填写 MiMo 模型名称。"); return; }
        if (provider == TtsProvider.OpenAiCompatible && (address is null || string.IsNullOrWhiteSpace(OpenAiModel.Text) || string.IsNullOrWhiteSpace(OpenAiVoice.Text)))
        { SetNotice("请填写合法的服务 Base URL、Model 和 Voice。"); return; }
        if (provider == TtsProvider.OpenAiCompatible && OpenAiModel.Text.Trim() is "tts-1" or "tts-1-hd" && !string.IsNullOrWhiteSpace(OpenAiInstructions.Text))
        { SetNotice("tts-1 / tts-1-hd 不支持 Instructions，请清空或选择支持的模型。"); return; }
        var error = save(savedSettings with { VoiceId = Voice.SelectedValue as string, Rate = Rate.Value, Volume = (float)Volume.Value,
            StartupSilenceMilliseconds = (int)Math.Round(StartupSilence.Value),
            Provider = provider, AzureRegion = provider == TtsProvider.Azure ? Region.Text.Trim() : savedSettings.AzureRegion,
            AzureVoice = provider == TtsProvider.Azure ? CloudVoice.SelectedValue as string : savedSettings.AzureVoice,
            AllowAutomaticCloud = AutomaticCloud.IsChecked == true,
            EdgeVoice = edgeSelection, EdgePitchHz = (int)EdgePitch.Value, EdgeLocaleFilter = EdgeLocale.Text.Trim(),
            AllowAutomaticEdge = AutomaticEdge.IsChecked == true,
            MiMoVoice = provider == TtsProvider.MiMo ? MiMoVoice.SelectedItem as string ?? "冰糖" : savedSettings.MiMoVoice,
            MiMoStyle = provider == TtsProvider.MiMo ? MiMoStyle.Text : savedSettings.MiMoStyle,
            MiMoOverallStyles = miMoStyles.Selected.ToArray(),
            MiMoModel = provider == TtsProvider.MiMo ? MiMoModel.Text.Trim() : savedSettings.MiMoModel,
            MiMoStreaming = provider == TtsProvider.MiMo ? MiMoStreaming.IsChecked == true : savedSettings.MiMoStreaming,
            OpenAiBaseUrl = provider == TtsProvider.OpenAiCompatible ? address! : savedSettings.OpenAiBaseUrl,
            OpenAiModel = provider == TtsProvider.OpenAiCompatible ? OpenAiModel.Text.Trim() : savedSettings.OpenAiModel,
            OpenAiVoice = provider == TtsProvider.OpenAiCompatible ? OpenAiVoice.Text.Trim() : savedSettings.OpenAiVoice,
            OpenAiInstructions = provider == TtsProvider.OpenAiCompatible ? OpenAiInstructions.Text : savedSettings.OpenAiInstructions,
            AllowAutomaticOpenAi = savedSettings.AllowAutomaticOpenAi && (provider != TtsProvider.OpenAiCompatible || address == Settings.NormalizeServiceUrl(savedSettings.OpenAiBaseUrl)),
            OpenAiAuthorizedBaseUrl = provider != TtsProvider.OpenAiCompatible || address == Settings.NormalizeServiceUrl(savedSettings.OpenAiBaseUrl) ? savedSettings.OpenAiAuthorizedBaseUrl : null,
            OutputDeviceIds = Outputs.SelectedItems.Cast<OutputChoice>().Select(d => d.Id).ToArray(),
            VirtualMicrophoneEnabled = VirtualMicEnabled.IsChecked == true, VirtualMicrophone = virtualRoute,
            StartWithWindows = AutoStart.IsChecked == true, OcrLanguage = (OcrLanguage)OcrLanguageChoice.SelectedIndex,
            LlmBaseUrl = llmAddress, LlmModel = LlmModel.Text.Trim(), LlmSystemPrompt = LlmSystemPrompt.Text,
            LlmReadingStrategy = (AnswerReadingStrategy)LlmReadingStrategy.SelectedIndex,
            LlmThinkingProtocol = (ThinkingProtocol)ThinkingProtocolChoice.SelectedIndex,
            LlmThinking = ThinkingOptionChoice.SelectedValue is ThinkingOption thinking ? thinking : ThinkingOption.Default,
            CustomPrompts = customPrompts.ToArray(), SelectedPromptId = (PromptChoice.SelectedItem as PromptPreset)?.Id,
            AllowAutomaticLlm = savedSettings.AllowAutomaticLlm && llmAddress == Settings.NormalizeServiceUrl(savedSettings.LlmBaseUrl),
            LlmAuthorizedBaseUrl = llmAddress == Settings.NormalizeServiceUrl(savedSettings.LlmBaseUrl) ? savedSettings.LlmAuthorizedBaseUrl : null,
            LlmStreaming = LlmStreaming.IsChecked == true, LlmAutoRead = LlmAutoRead.IsChecked == true });
        SetNotice(error ?? "设置已保存，下次朗读生效。");
        if (error is null) OutputSaveHint.Text = "设备选择已保存，用于下一次朗读。";
    }
    private void ThinkingProtocol_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ThinkingOptionChoice is null) return;
        var protocol = (ThinkingProtocol)ThinkingProtocolChoice.SelectedIndex;
        var values = protocol switch
        {
            ThinkingProtocol.OpenAi => new[] { ThinkingOption.Default, ThinkingOption.Disabled, ThinkingOption.Low, ThinkingOption.Medium, ThinkingOption.High },
            ThinkingProtocol.MiMo => new[] { ThinkingOption.Default, ThinkingOption.Disabled, ThinkingOption.Enabled },
            _ => new[] { ThinkingOption.Default }
        };
        ThinkingOptionChoice.DisplayMemberPath = "Label"; ThinkingOptionChoice.SelectedValuePath = "Value";
        ThinkingOptionChoice.ItemsSource = values.Select(v => new { Value = v, Label = v switch {
            ThinkingOption.Default => "服务默认（不发送参数）", ThinkingOption.Disabled => "关闭（模型须支持）",
            ThinkingOption.Low => "低", ThinkingOption.Medium => "中", ThinkingOption.High => "高", _ => "开启思考" } }).ToArray();
        ThinkingOptionChoice.SelectedValue = ThinkingOption.Default;
    }
    private void RefreshPrompts(string? selected)
    {
        refreshingPrompts = true;
        try { PromptChoice.ItemsSource = PromptPresets.BuiltIn.Concat(customPrompts).ToArray();
            PromptChoice.SelectedItem = PromptChoice.Items.Cast<PromptPreset>().FirstOrDefault(p => p.Id == selected);
            PromptName.Text = (PromptChoice.SelectedItem as PromptPreset)?.Name ?? ""; }
        finally { refreshingPrompts = false; }
    }
    private void PromptChoice_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (refreshingPrompts || LlmSystemPrompt is null || PromptName is null) return;
        if (PromptChoice.SelectedItem is PromptPreset preset) { LlmSystemPrompt.Text = preset.Prompt; PromptName.Text = preset.Name; }
    }
    private void AddPrompt_Click(object sender, RoutedEventArgs e) => EditPrompt(false);
    private void UpdatePrompt_Click(object sender, RoutedEventArgs e) => EditPrompt(true);
    private void EditPrompt(bool update)
    {
        var old = PromptChoice.SelectedItem as PromptPreset;
        if (update && (old is null || !customPrompts.Any(p => p.Id == old.Id))) { SetNotice("内置预设请另存为自定义预设。"); return; }
        if (string.IsNullOrWhiteSpace(PromptName.Text) || !update && customPrompts.Count >= 50) { SetNotice("请填写名称；最多 50 个自定义预设。"); return; }
        var preset = new PromptPreset(update ? old!.Id : Guid.NewGuid().ToString("N"), PromptName.Text.Trim(), LlmSystemPrompt.Text);
        if (update) customPrompts.RemoveAll(p => p.Id == preset.Id);
        customPrompts.Add(preset); RefreshPrompts(preset.Id); SetNotice("预设修改尚未保存，请点击底部保存设置。");
    }
    private void DeletePrompt_Click(object sender, RoutedEventArgs e)
    {
        if (PromptChoice.SelectedItem is PromptPreset p && customPrompts.RemoveAll(x => x.Id == p.Id) > 0)
        { RefreshPrompts(null); SetNotice("预设删除尚未保存；当前 Prompt 原文保留。"); }
    }
    private void AutomaticLlm_Click(object sender, RoutedEventArgs e)
    {
        if (AutomaticLlm.IsChecked == true && Settings.NormalizeServiceUrl(LlmBaseUrl.Text) != Settings.NormalizeServiceUrl(savedSettings.LlmBaseUrl))
        { AutomaticLlm.IsChecked = savedSettings.AllowsAutomaticLlm; SetNotice("请先保存 LLM 地址，再明确授权当前服务。"); return; }
        SaveAutomatic(savedSettings with { AllowAutomaticLlm = AutomaticLlm.IsChecked == true,
            LlmAuthorizedBaseUrl = AutomaticLlm.IsChecked == true ? Settings.NormalizeServiceUrl(savedSettings.LlmBaseUrl) : null });
    }
    private void AutoAsk_Click(object sender, RoutedEventArgs e) => SaveAutomatic(savedSettings with { AutoAsk = AutoAsk.IsChecked == true });
    private void AskRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.CheckBox { DataContext: ApplicationRule rule } box)
            SaveAutomatic(savedSettings with { Applications = (savedSettings.Applications ?? []).Select(r =>
                r == rule ? r with { AutoAsk = box.IsChecked == true } : r).ToArray() });
    }
    private void LlmAddress_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (LlmKey is null || LlmCredentialStatus is null) return;
        LlmKey.Clear(); UpdateLlmCredentialStatus();
    }
    private void UpdateLlmCredentialStatus()
    {
        if (LlmCredentialStatus is null) return;
        try
        {
            LlmCredentialStatus.Text = Settings.NormalizeServiceUrl(LlmBaseUrl.Text) is null ? "地址无效。" :
                string.IsNullOrWhiteSpace(AzureCredentials.Llm(LlmBaseUrl.Text).Read()) ? "当前 LLM 地址未配置 Key。" : "当前 LLM 地址已配置 Key（与 TTS 独立）。";
        }
        catch (UserFeedbackException ex) { LlmCredentialStatus.Text = ex.Message; }
    }
    private void SaveLlmKey_Click(object sender, RoutedEventArgs e)
    {
        try { AzureCredentials.Llm(LlmBaseUrl.Text).Write(LlmKey.Password); LlmKey.Clear(); LlmCredentialsChanged?.Invoke(); diagnosticsWindow?.CredentialsChanged(); UpdateLlmCredentialStatus(); }
        catch (UserFeedbackException ex) { SetNotice(ex.Message); }
    }
    private void ClearLlmKey_Click(object sender, RoutedEventArgs e)
    {
        try { AzureCredentials.Llm(LlmBaseUrl.Text).Clear(); LlmKey.Clear(); LlmCredentialsChanged?.Invoke(); diagnosticsWindow?.CredentialsChanged(); UpdateLlmCredentialStatus(); }
        catch (UserFeedbackException ex) { SetNotice(ex.Message); }
    }
    private void RefreshMiMoStyles()
    {
        RefreshMiMoSelection(); RefreshMiMoPresets();
    }
    private void RefreshMiMoSelection()
    {
        if (MiMoSelectedStyles is null || MiMoPresetGroups is null) return;
        MiMoSelectedStyles.ItemsSource = miMoStyles.Selected.ToArray();
        var prefix = MiMoStyleTags.GetPrefix(miMoStyles.Selected);
        MiMoStylePrefix.Text = prefix.Length == 0 ? "实际前缀：无（保持原文）" : "实际前缀：" + prefix;
        MiMoStyleHint.Text = miMoStyles.Selected.Any(MiMoStyleTags.IsSinging)
            ? "唱歌为独立模式，适合中文歌词；普通文章未必能自然演唱。选择其他风格将退出唱歌模式。"
            : "按选择顺序组合，最多 16 项。修改后请点击底部“保存设置”。";
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
                { Content = name, Tag = name, IsChecked = miMoStyles.Selected.Contains(name),
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
            if (miMoStyles.Selected.Contains(name)) miMoStyles.Remove(name); else miMoStyles.Add(name);
            MiMoStyleError.Text = "";
            // Preserve the focused toggle for keyboard users while updating all selected states.
            RefreshMiMoSelection();
        }
        catch (UserFeedbackException ex) { MiMoStyleError.Text = ex.Message; }
        foreach (var row in MiMoPresetGroups.Children.OfType<System.Windows.Controls.WrapPanel>())
            foreach (var item in row.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>())
                item.IsChecked = miMoStyles.Selected.Contains((string)item.Tag);
    }
    private void AddMiMoStyle_Click(object sender, RoutedEventArgs e)
    {
        try { miMoStyles.Add(MiMoCustomStyle.Text); MiMoCustomStyle.Clear(); MiMoStyleError.Text = ""; RefreshMiMoStyles(); }
        catch (UserFeedbackException ex) { MiMoStyleError.Text = ex.Message; }
    }
    private void MiMoCustomStyle_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { AddMiMoStyle_Click(sender, e); e.Handled = true; }
    }
    private void RemoveMiMoStyle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: string name }) { miMoStyles.Remove(name); MiMoStyleError.Text = ""; RefreshMiMoStyles(); }
    }
    private void ClearMiMoStyles_Click(object sender, RoutedEventArgs e)
    { miMoStyles.Clear(); MiMoStyleError.Text = ""; RefreshMiMoStyles(); }
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
        if (LocalPanel is null || AzurePanel is null || EdgePanel is null || MiMoPanel is null || OpenAiPanel is null) return;
        voiceQuery?.Cancel(); cancelAutomatic?.Invoke();
        LocalPanel.Visibility = Provider.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        AzurePanel.Visibility = Provider.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        EdgePanel.Visibility = Provider.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        MiMoPanel.Visibility = Provider.SelectedIndex == 3 ? Visibility.Visible : Visibility.Collapsed;
        OpenAiPanel.Visibility = Provider.SelectedIndex == 4 ? Visibility.Visible : Visibility.Collapsed;
        UpdateProviderSaveHint();
    }
    private void UpdateProviderSaveHint()
    {
        if (savedSettings is null || ProviderSaveHint is null) return;
        var active = savedSettings.Provider switch
        {
            TtsProvider.Edge => "Edge 在线语音",
            TtsProvider.Azure => "Azure Speech",
            TtsProvider.MiMo => "MiMo TTS",
            TtsProvider.OpenAiCompatible => "OpenAI-Compatible TTS",
            _ => "Windows 本地 TTS"
        };
        ProviderSaveHint.Text = $"当前朗读使用：{active}。" +
            (Provider.SelectedIndex >= 0 && Provider.SelectedIndex != (int)savedSettings.Provider
                ? " Provider 切换尚未保存；请选择语音，然后点击“保存设置”生效。"
                : " 语音、语速、音调和设备修改后请保存，下次朗读生效。") +
            (OpenAiBaseUrl is not null && Settings.NormalizeServiceUrl(OpenAiBaseUrl.Text) != Settings.NormalizeServiceUrl(savedSettings.OpenAiBaseUrl)
                ? " 兼容服务地址尚未保存，仍使用已保存地址；请保存后重新授权。" : "");
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
        try { voiceQuery?.Cancel(); credentials.Write(AzureKey.Password.Trim()); diagnosticsWindow?.CredentialsChanged(); AzureKey.Clear(); UpdateCredentialStatus(); SetNotice("Key 已安全保存。"); }
        catch (UserFeedbackException ex) { SetNotice(ex.Message); }
    }
    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        try { voiceQuery?.Cancel(); credentials.Clear(); diagnosticsWindow?.CredentialsChanged(); AzureKey.Clear(); UpdateCredentialStatus(); SetNotice("Azure Key 已清除，本地朗读不受影响。"); }
        catch (UserFeedbackException ex) { SetNotice(ex.Message); }
    }
    private void UpdateHttpCredentialStatus()
    {
        if (MiMoCredentialStatus is null || OpenAiCredentialStatus is null) return;
        try { MiMoCredentialStatus.Text = AzureCredentials.MiMo().Read() is null ? "尚未保存 MiMo Key" : "已保存 MiMo Key（不显示内容）"; }
        catch (UserFeedbackException ex) { MiMoCredentialStatus.Text = ex.Message; }
        try { OpenAiCredentialStatus.Text = AzureCredentials.OpenAi(OpenAiBaseUrl.Text).Read() is null ? "此地址尚未保存 Key" : "此地址已保存 Key（不显示内容）"; }
        catch (UserFeedbackException ex) { OpenAiCredentialStatus.Text = ex.Message; }
    }
    private void SaveHttpKey_Click(object sender, RoutedEventArgs e) => ChangeHttpKey(sender, false);
    private void MiMoModel_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (MiMoStreaming is null || MiMoStreamingHint is null) return;
        var supported = MiMoModel.Text.Trim() == "mimo-v2.5-tts";
        MiMoStreaming.IsEnabled = supported;
        MiMoStreamingHint.Text = supported ? "保存后边合成边播放；关闭后完整合成为 WAV 再播放。流中断不会重试已播文本。" :
            "该自定义模型的流式协议尚未验证，将使用非流式 WAV 模式。";
    }
    private void ClearHttpKey_Click(object sender, RoutedEventArgs e) => ChangeHttpKey(sender, true);
    private void MiMoApiKey_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://platform.xiaomimimo.com/console/api-keys") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { SetNotice("无法打开默认浏览器，请手动访问小米 MiMo 控制台获取 API Key。"); }
        e.Handled = true;
    }
    private void ChangeHttpKey(object sender, bool clear)
    {
        var mimo = (sender as System.Windows.Controls.Button)?.Tag as string == "MiMo";
        var box = mimo ? MiMoKey : OpenAiKey;
        try
        {
            var store = mimo ? AzureCredentials.MiMo() : AzureCredentials.OpenAi(OpenAiBaseUrl.Text);
            if (clear) { cancelAutomatic(); store.Clear(); } else store.Write(box.Password.Trim());
            box.Clear(); diagnosticsWindow?.CredentialsChanged(); UpdateHttpCredentialStatus(); SetNotice(clear ? "此服务 Key 已清除。" : "此服务 Key 已安全保存；服务配置请点击底部保存设置。");
        }
        catch (UserFeedbackException ex) { SetNotice(ex.Message); }
    }
    private void OpenAiAddress_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (savedSettings is null || AutomaticOpenAi is null) return;
        var address = Settings.NormalizeServiceUrl(OpenAiBaseUrl.Text);
        if (address != Settings.NormalizeServiceUrl(savedSettings.OpenAiBaseUrl))
        {
            OpenAiKey.Clear(); AutomaticOpenAi.IsChecked = false;
            cancelAutomatic();
            if (savedSettings.AllowAutomaticOpenAi) SaveAutomatic(savedSettings with { AllowAutomaticOpenAi = false, OpenAiAuthorizedBaseUrl = null });
        }
        UpdateHttpCredentialStatus(); UpdateProviderSaveHint();
    }
    private void AutomaticHttp_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, AutomaticMiMo))
        { SaveAutomatic(savedSettings with { AllowAutomaticMiMo = AutomaticMiMo.IsChecked == true }); return; }
        var address = Settings.NormalizeServiceUrl(OpenAiBaseUrl.Text);
        if (address is null || address != Settings.NormalizeServiceUrl(savedSettings.OpenAiBaseUrl))
        { AutomaticOpenAi.IsChecked = false; SetNotice("服务地址尚未保存，请先保存设置，再明确开启该地址的自动授权。"); return; }
        SaveAutomatic(savedSettings with { AllowAutomaticOpenAi = AutomaticOpenAi.IsChecked == true,
            OpenAiAuthorizedBaseUrl = AutomaticOpenAi.IsChecked == true ? address : null });
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
    private void Stop_Click(object sender, RoutedEventArgs e) { virtualTest?.Cancel(); stop(); }
    private void Exit_Click(object sender, RoutedEventArgs e) => exit();
}
