using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SeleVox.Core;

namespace SeleVox.App;

public partial class AudioWorkbenchWindow
{
    private CloneLibrary library = null!;
    private bool creativeReady, assetBusy;
    private CancellationTokenSource? recording;
    private MiMoCreativeMode Mode => CreativeMode.SelectedValue is MiMoCreativeMode mode ? mode : MiMoCreativeMode.PresetVoice;
    private MiMoControl Control => CreativeControl.SelectedValue is MiMoControl control ? control : MiMoControl.NaturalLanguage;
    private sealed record ModeChoice(MiMoCreativeMode Id, string Label);
    private sealed record ControlChoice(MiMoControl Id, string Label);
    private static readonly ControlChoice[] Controls = [new(MiMoControl.NaturalLanguage, "自然语言控制"), new(MiMoControl.Director, "导演模式"),
        new(MiMoControl.StyleTags, "风格标签"), new(MiMoControl.AudioTags, "音频标签脚本"), new(MiMoControl.Singing, "唱歌")];

    private void InitializeCreative(Settings s)
    {
        library = new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(preferencesPath))!, "clone-samples"));
        CreativeMode.DisplayMemberPath = "Label"; CreativeMode.SelectedValuePath = "Id";
        CreativeMode.ItemsSource = new ModeChoice[] { new(MiMoCreativeMode.PresetVoice, "预置音色 · Preset Voice"), new(MiMoCreativeMode.VoiceDesign, "音色设计 · VoiceDesign"), new(MiMoCreativeMode.VoiceClone, "音色克隆 · VoiceClone") };
        CreativeMode.SelectedValue = s.MiMoModel switch { "mimo-v2.5-tts-voicedesign" => MiMoCreativeMode.VoiceDesign, "mimo-v2.5-tts-voiceclone" => MiMoCreativeMode.VoiceClone, _ => MiMoCreativeMode.PresetVoice };
        CreativeControl.DisplayMemberPath = "Label"; CreativeControl.SelectedValuePath = "Id";
        CreativeControl.ItemsSource = Mode == MiMoCreativeMode.PresetVoice ? Controls : Controls.Take(2).ToArray();
        CreativeControl.SelectedValue = MiMoControl.NaturalLanguage;
        VoiceDescription.Text = s.MiMoVoiceDescription; AutoSegment.IsChecked = s.MiMoAutoSegment;
        SegmentCharacters.Text = s.MiMoSegmentCharacters.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var tag in MiMoCreative.AudioTags)
        {
            var button = new Button { Content = tag, Tag = tag, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 6) };
            button.Click += InsertAudioTag_Click; AudioTagButtons.Children.Add(button);
        }
        creativeReady = true;
        InputText.TextChanged += (_, _) => MarkDraftChanged(); MiMoModel.TextChanged += (_, _) => { RefreshCreativePreview(); MarkDraftChanged(); };
        MiMoStyle.TextChanged += (_, _) => MarkDraftChanged(); VoiceDescription.TextChanged += (_, _) => MarkDraftChanged();
        Rate.ValueChanged += (_, _) => { RefreshCreativePreview(); MarkDraftChanged(); };
        RefreshCreativeLayout(); RefreshSamples();
    }
    private void MarkDraftChanged()
    {
        if (result is not null) GenerationStatus.Text = "输入已修改；右侧仍是上一次成功结果。点击生成语音更新。";
    }
    private void CreativeMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!creativeReady) return;
        MiMoModel.Text = MiMoCreative.Model(Mode);
        CreativeControl.ItemsSource = Mode == MiMoCreativeMode.PresetVoice ? Controls : Controls.Take(2).ToArray();
        CreativeControl.SelectedValue = MiMoControl.NaturalLanguage; RefreshCreativeLayout(); MarkDraftChanged();
    }
    private void CreativeControl_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!creativeReady) return;
        if (Mode == MiMoCreativeMode.PresetVoice && Control == MiMoControl.Singing) { styles.Add("唱歌"); RefreshMiMoStyles(); }
        RefreshCreativeLayout(); MarkDraftChanged();
    }
    private void RefreshCreativeLayout()
    {
        PresetPanel.Visibility = Mode == MiMoCreativeMode.PresetVoice ? Visibility.Visible : Visibility.Collapsed;
        DesignPanel.Visibility = Mode == MiMoCreativeMode.VoiceDesign ? Visibility.Visible : Visibility.Collapsed;
        ClonePanel.Visibility = Mode == MiMoCreativeMode.VoiceClone ? Visibility.Visible : Visibility.Collapsed;
        AssetNamePanel.Visibility = Mode == MiMoCreativeMode.PresetVoice ? Visibility.Collapsed : Visibility.Visible;
        MiMoModel.IsReadOnly = Mode != MiMoCreativeMode.PresetVoice;
        DirectorPanel.Visibility = Control == MiMoControl.Director ? Visibility.Visible : Visibility.Collapsed;
        NaturalPanel.Visibility = Control == MiMoControl.NaturalLanguage ? Visibility.Visible : Visibility.Collapsed;
        TagStylePanel.Visibility = Mode == MiMoCreativeMode.PresetVoice && Control is MiMoControl.NaturalLanguage or MiMoControl.StyleTags or MiMoControl.Singing ? Visibility.Visible : Visibility.Collapsed;
        AudioTagPanel.Visibility = Provider.SelectedItem is TtsProvider.MiMo && Control == MiMoControl.AudioTags ? Visibility.Visible : Visibility.Collapsed;
        RequestPreviewPanel.Visibility = Provider.SelectedItem is TtsProvider.MiMo ? Visibility.Visible : Visibility.Collapsed;
        CloneReusePanel.Visibility = Provider.SelectedItem is TtsProvider.MiMo ? Visibility.Visible : Visibility.Collapsed;
        RefreshCreativePreview();
    }
    private string CreativeStyle() => Control switch {
        MiMoControl.Director => MiMoCreative.Director(DirectorRole.Text, DirectorScene.Text, DirectorDirection.Text),
        MiMoControl.NaturalLanguage => MiMoStyle.Text, _ => "" };
    private string[] CreativeStyles() => Mode != MiMoCreativeMode.PresetVoice ? [] : Control switch {
        MiMoControl.Singing => ["唱歌"], MiMoControl.Director or MiMoControl.AudioTags => [], _ => styles?.Selected.ToArray() ?? [] };
    private void Creative_Changed(object sender, TextChangedEventArgs e) { RefreshCreativePreview(); MarkDraftChanged(); }
    private void Segment_Changed(object sender, RoutedEventArgs e) { RefreshCreativePreview(); MarkDraftChanged(); }
    private void RefreshCreativePreview()
    {
        if (!creativeReady || closed) return;
        RequestPreviewPanel.Visibility = Provider.SelectedItem is TtsProvider.MiMo ? Visibility.Visible : Visibility.Collapsed;
        AudioTagPanel.Visibility = Provider.SelectedItem is TtsProvider.MiMo && Control == MiMoControl.AudioTags ? Visibility.Visible : Visibility.Collapsed;
        CloneReusePanel.Visibility = Provider.SelectedItem is TtsProvider.MiMo ? Visibility.Visible : Visibility.Collapsed;
        if (Provider.SelectedItem is not TtsProvider.MiMo) return;
        try
        {
            var s = EffectiveDraft(); var parts = MiMoSpeech.SplitCreativeText(InputText.Text, s);
            var prefix = MiMoSpeech.CreativePrefix(s);
            var user = MiMoSpeech.GetStyle(s);
            RequestPreview.Text = $"user：{user}\n{parts.Count} 段 · 每段 ≤ {s.MiMoSegmentCharacters} 字符 / UTF-8 安全预算\n" +
                string.Join("\n\n", parts.Select((part, i) => $"assistant [{i + 1}]（{part.Length} 字符）：{prefix}{part}"));
        }
        catch (UserFeedbackException ex) { RequestPreview.Text = ex.Message; }
    }
    private void InsertAudioTag_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        var position = InputText.SelectionStart; var selected = InputText.SelectionLength; var insertion = "[" + tag + "]";
        InputText.Text = InputText.Text.Insert(position, insertion);
        InputText.Focus(); InputText.Select(position + insertion.Length, selected);
    }
    private void DirectorExample_Click(object sender, RoutedEventArgs e)
    { DirectorRole.Text = "温柔、沉稳的深夜电台主持人"; DirectorScene.Text = "向结束一天工作的听众道晚安"; DirectorDirection.Text = "语速稍慢，咬字清晰，句间自然停顿，带一点笑意。"; }
    private void RefreshSamples(Guid? selected = null)
    {
        if (closed) return;
        try { var id = selected ?? (CloneSamples.SelectedItem as CloneSample)?.Id; CloneSamples.ItemsSource = library.Load();
            CloneSamples.SelectedItem = ((IReadOnlyList<CloneSample>)CloneSamples.ItemsSource).FirstOrDefault(s => s.Id == id);
            if (CloneSamples.SelectedIndex < 0 && CloneSamples.Items.Count > 0) CloneSamples.SelectedIndex = 0; }
        catch (UserFeedbackException ex) { CloneStatus.Text = ex.Message; }
    }
    private void CloneSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        CloneDetails.Text = CloneSamples.SelectedItem is CloneSample s ? $"{s.Label}\n{s.FilePath}" : "未选择样本";
        MarkDraftChanged();
    }
    internal async Task SaveDesignSourceAsync(string name)
    {
        if (result is not { Configuration.Provider: TtsProvider.MiMo, Configuration.MiMoModel: "mimo-v2.5-tts-voicedesign" } audio)
            throw new UserFeedbackException("请先成功生成 VoiceDesign 音频。");
        using var source = audio.Open(); using var memory = new MemoryStream(); await source.CopyToAsync(memory, lifetime.Token);
        var sample = await Task.Run(() => library.AddAsync(name, CloneSource.VoiceDesign, new(memory.ToArray(), ".wav"), lifetime.Token));
        if (!closed) { RefreshSamples(sample.Id); GenerationStatus.Text = "设计结果已保存为本地克隆源；可切换 VoiceClone 直接使用。未再次请求 MiMo。"; }
    }
    private async void SaveCloneSource_Click(object sender, RoutedEventArgs e)
    {
        if (assetBusy || closed) return;
        assetBusy = true; SaveCloneSource.IsEnabled = false;
        try { await SaveDesignSourceAsync(SampleName.Text); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) Feedback(ex, "保存克隆源失败，请检查本地目录权限。"); }
        finally { assetBusy = false; if (!closed) SaveCloneSource.IsEnabled = result?.Configuration.MiMoModel == "mimo-v2.5-tts-voicedesign"; }
    }
    private async void UploadClone_Click(object sender, RoutedEventArgs e)
    {
        if (assetBusy || closed) return;
        var dialog = new OpenFileDialog { Filter = "克隆音频 (*.wav;*.mp3)|*.wav;*.mp3", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true || closed) return;
        var name = SampleName.Text; assetBusy = true;
        try { var sample = await Task.Run(async () => await library.AddAsync(name, CloneSource.Upload, await CloneAudio.ReadAsync(dialog.FileName, lifetime.Token), lifetime.Token));
            if (!closed) { RefreshSamples(sample.Id); CloneStatus.Text = "已复制到本地样本库；点击生成才发送。"; } }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) Feedback(ex, "导入失败，请检查文件和本地目录权限。"); }
        finally { assetBusy = false; }
    }
    private async void RecordClone_Click(object sender, RoutedEventArgs e)
    {
        if (recording is not null) { recording.Cancel(); return; }
        if (assetBusy || closed) return;
        assetBusy = true; var name = SampleName.Text;
        using var request = new CancellationTokenSource(); recording = request;
        RecordClone.Content = "结束并保存录音"; CloneStatus.Text = "正在录制系统默认麦克风；请录制 5–30 秒，30 秒自动结束。关闭窗口将丢弃录音。";
        try
        {
            var audio = await Task.Run(() => CloneRecording.RecordAsync(request.Token, lifetime.Token));
            var sample = await Task.Run(() => library.AddAsync(name, CloneSource.Recording, audio, lifetime.Token));
            if (!closed) { RefreshSamples(sample.Id); CloneStatus.Text = "录音已保存本地；点击生成才发送选中样本。"; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) Feedback(ex, "录音失败，请检查默认麦克风、Windows 麦克风权限和设备占用。"); }
        finally { recording = null; assetBusy = false; if (!closed) RecordClone.Content = "开始录音"; }
    }
    private async void PreviewClone_Click(object sender, RoutedEventArgs e)
    {
        if (CloneSamples.SelectedItem is not CloneSample sample || closed) return;
        preview?.Cancel(); preview?.Dispose(); using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); preview = request;
        try { var audio = await Task.Run(async () => (await CloneAudio.ReadAsync(sample.FilePath, request.Token)).Preview(request.Token));
            if (!closed && !request.IsCancellationRequested) await play(audio, request.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed && !request.IsCancellationRequested) Feedback(ex, "样本试听失败，请检查本地文件和输出设备。"); }
        finally { if (ReferenceEquals(preview, request)) preview = null; }
    }
    private async void DeleteClone_Click(object sender, RoutedEventArgs e)
    {
        if (CloneSamples.SelectedItem is not CloneSample sample || assetBusy || closed) return;
        assetBusy = true;
        try { await Task.Run(() => library.DeleteAsync(sample, lifetime.Token)); if (!closed) { RefreshSamples(); CloneStatus.Text = "已删除库内文件与元数据；上传原文件不受影响。"; } }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!closed) Feedback(ex, "删除失败，请检查文件是否被占用和目录权限。"); }
        finally { assetBusy = false; }
    }
}
