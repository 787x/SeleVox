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
    private AudioWorkbenchWindow? audioWorkbench;
    private Settings settings = new();
    private readonly SelectionCapture capture = new();
    private readonly SettingsStore store = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeleVox", "settings.json"));
    private bool exiting;
    private readonly AutomaticSelection automaticSelection = new();
    private AutomaticMouseMonitor? mouse;
    private Forms.ToolStripMenuItem? autoMenu, autoAskMenu, startupMenu;
    private long mouseEpoch;
    private readonly TextCaptureOperation ocrOperation = new();
    private readonly LocalOcr ocr = new();
    private readonly SemaphoreSlim ocrGate = new(1, 1);
    private OcrResultWindow? ocrResult;
    private QuestionSession? questionSession;
    private AiAnswerWindow? aiAnswer;
    private CancellationTokenSource? selectionAction;
    private long selectionEpoch;
    private AgentExecution? agentExecution;
    private AgentPipe? agentPipe;
    internal bool IsExiting => exiting;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, ".selevox-upgrade-pending")))
        {
            if (!SingleInstance.IsBackground(e.Args)) MessageBox.Show("SeleVox 安装未完成，请先重新运行上次目标版本的 Setup 修复安装；请保留安装目录中的恢复文件。");
            Shutdown(); return;
        }
        if (SingleInstance.MaintenanceInProgress())
        {
            if (!SingleInstance.IsBackground(e.Args)) MessageBox.Show("安装或卸载正在进行，请完成后再启动 SeleVox。");
            Shutdown(); return;
        }
        instance = new SingleInstance(showExisting: !SingleInstance.IsBackground(e.Args));
        if (!instance.IsFirst) { Shutdown(); return; }
        var loaded = store.Load(); settings = loaded.Settings;
        var speech = new SpeechAudio();
        reading = new ReadingSession(speech, speech);
        agentExecution = new(reading, speech, () => settings,
            action => Dispatcher.InvokeAsync(action).Task.Unwrap(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeleVox"),
            () => { InvalidateSelectionAction(); questionSession?.Cancel(); mouse?.CancelGesture(); ++mouseEpoch; automaticSelection.Invalidate(); });
        agentPipe = new(agentExecution);
        reading.Changed += agentExecution.PlaybackChanged;
        var chat = new ChatCompletionClient();
        questionSession = new QuestionSession(chat.GenerateAsync,
            (text, options) => reading.ReadSentenceAsync(text, options, CancellationToken.None), reading.Stop, reading.ReadSentenceAsync);
        settingsWindow = new MainWindow(settings, SaveSettings, () => reading.TogglePause(), StopReading, ExitApplication, AddForegroundApplicationAsync, CancelAutomaticReading);
        settingsWindow.LlmCredentialsChanged = () => { InvalidateSelectionAction(); questionSession.Cancel(); };
        settingsWindow.OpenAudioWorkbench = ShowAudioWorkbench;
        MainWindow = settingsWindow;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("设置", null, (_, _) => ShowSettings());
        menu.Items.Add("音频生成", null, (_, _) => ShowAudioWorkbench());
        menu.Items.Add("屏幕框选 OCR · Ctrl+Alt+O", null, (_, _) => StartOcr());
        autoMenu = new Forms.ToolStripMenuItem("Auto Speak · 自动选中朗读") { Checked = settings.AutoSpeak };
        autoMenu.Click += (_, _) =>
        {
            var error = SaveSettings(settings with { AutoSpeak = !settings.AutoSpeak });
            if (error is not null) settingsWindow.SetNotice(error);
        };
        menu.Items.Add(autoMenu);
        autoAskMenu = new Forms.ToolStripMenuItem("Auto Ask · 自动选中问 AI") { Checked = settings.AutoAsk };
        autoAskMenu.Click += (_, _) => { var error = SaveSettings(settings with { AutoAsk = !settings.AutoAsk }); if (error is not null) settingsWindow.SetNotice(error); };
        menu.Items.Add(autoAskMenu);
        startupMenu = new Forms.ToolStripMenuItem("登录 Windows 时自动启动") { Checked = settings.StartWithWindows };
        startupMenu.Click += (_, _) =>
        {
            var error = SaveSettings(settings with { StartWithWindows = !settings.StartWithWindows });
            if (error is not null) { settingsWindow.SetNotice(error); Notify(error); }
        };
        menu.Items.Add(startupMenu);
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
            audioWorkbench?.SetPlaybackStatus(status);
            if (status.State == ReadingState.Error && status.Notify) Notify(status.Message);
        });
        hotkeys = new Hotkeys();
        hotkeys.Pressed += (id, foreground) =>
        {
            switch (id)
            {
                case 1: _ = CaptureActionAsync(foreground, false); break;
                case 2: reading.TogglePause(); break;
                case 3: StopReading(); break;
                case 4: StartOcr(); break;
                case 5: _ = CaptureActionAsync(foreground, true); break;
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
        catch (Exception ex) { notices.Add(ex is StartupRegistrationException ? ex.Message : $"开机启动设置不可用（{ex.GetType().Name}）。"); }
        if (!SingleInstance.IsBackground(e.Args)) ShowSettings();
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
    private void ShowAudioWorkbench()
    {
        if (exiting) return;
        if (audioWorkbench is null)
        {
            var window = new AudioWorkbenchWindow(() => settings, ShowSettings, (audio, token) =>
            {
                InvalidateSelectionAction(); questionSession?.Cancel();
                mouse?.CancelGesture(); ++mouseEpoch; automaticSelection.Invalidate();
                return reading!.PlayAudioAsync(audio.Open, settings, token);
            }, () => reading?.TogglePause(), StopReading,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeleVox", "audio-workbench.json"));
            audioWorkbench = window;
            window.Closed += (_, _) => { if (ReferenceEquals(audioWorkbench, window)) audioWorkbench = null; };
        }
        audioWorkbench.Show(); audioWorkbench.WindowState = WindowState.Normal; audioWorkbench.Activate();
    }
    private string? SaveSettings(Settings updated)
    {
        try
        {
            // Startup-only saves must not cancel work or refresh unrelated unsaved controls.
            if (OnlyStartupChanged(settings, updated))
            {
                StartupRegistration.ApplyAndSave(updated.StartWithWindows, () => store.Save(updated));
                settings = updated;
                settingsWindow?.SetStartupSettings(settings.StartWithWindows);
                if (startupMenu is not null) startupMenu.Checked = settings.StartWithWindows;
                return null;
            }
            if (Settings.NormalizeServiceUrl(updated.LlmBaseUrl) != Settings.NormalizeServiceUrl(settings.LlmBaseUrl))
                updated = updated with { AllowAutomaticLlm = false, LlmAuthorizedBaseUrl = null };
            var invalidateAutomaticQuestion = !updated.AutoAsk || !updated.AllowsAutomaticLlm ||
                !(updated.Applications ?? []).SequenceEqual(settings.Applications ?? []) ||
                settings.AllowsAutomaticProvider && !updated.AllowsAutomaticProvider ||
                updated.Provider != settings.Provider || Settings.NormalizeServiceUrl(updated.LlmBaseUrl) != Settings.NormalizeServiceUrl(settings.LlmBaseUrl);
            var virtualRouteChanged = settings.VirtualMicrophoneEnabled &&
                (!updated.VirtualMicrophoneEnabled || updated.VirtualMicrophone != settings.VirtualMicrophone);
            StartupRegistration.ApplyAndSave(updated.StartWithWindows, () => store.Save(updated)); settings = updated;
            // Do not leave a previously enabled route injecting old audio after saving Disable.
            if (virtualRouteChanged) StopReading();
            InvalidateSelectionAction(); if (invalidateAutomaticQuestion) questionSession?.CancelAutomatic();
            CancelAutomaticReading();
            settingsWindow?.SetAutomaticSettings(settings);
            ocrResult?.RefreshProviderNotice();
            if (autoMenu is not null) autoMenu.Checked = settings.AutoSpeak;
            if (autoAskMenu is not null) autoAskMenu.Checked = settings.AutoAsk;
            if (startupMenu is not null) startupMenu.Checked = settings.StartWithWindows;
            var warning = ApplyAutomatic();
            if (warning is not null) settingsWindow?.SetAutomaticStatus(warning);
            return null;
        }
        catch (Exception ex)
        {
            settingsWindow?.SetStartupSettings(settings.StartWithWindows);
            return ex is StartupRegistrationException ? ex.Message : $"设置保存失败（{ex.GetType().Name}），请检查用户目录及开机启动权限。";
        }
    }
    internal static bool OnlyStartupChanged(Settings saved, Settings updated) =>
        (saved.Applications ?? []).SequenceEqual(updated.Applications ?? []) &&
        (saved.OutputDeviceIds ?? []).SequenceEqual(updated.OutputDeviceIds ?? []) &&
        (saved.MiMoOverallStyles ?? []).SequenceEqual(updated.MiMoOverallStyles ?? []) &&
        (saved.CustomPrompts ?? []).SequenceEqual(updated.CustomPrompts ?? []) &&
        saved == updated with { StartWithWindows = saved.StartWithWindows, Applications = saved.Applications,
            OutputDeviceIds = saved.OutputDeviceIds, MiMoOverallStyles = saved.MiMoOverallStyles, CustomPrompts = saved.CustomPrompts };
    private string? ApplyAutomatic()
    {
        if (!(settings.AutoSpeak || settings.AutoAsk) || ocrOperation.IsActive)
        {
            mouse?.Dispose(); mouse = null;
            settingsWindow?.SetAutomaticStatus("自动选区操作已关闭");
            return null;
        }
        if (mouse is not null) return null;
        try
        {
            mouse = new AutomaticMouseMonitor();
            mouse.Invalidated += () => { ++mouseEpoch; automaticSelection.Invalidate(); };
            mouse.Selected += (window, start, end) =>
            {
                var epoch = mouseEpoch;
                Dispatcher.BeginInvoke(() =>
                {
                    if (epoch == mouseEpoch && !exiting && !ocrOperation.IsActive && (settings.AutoSpeak || settings.AutoAsk)) _ = AutoReadAsync(window, start, end, epoch);
                });
            };
            settingsWindow?.SetAutomaticStatus("仅在启用的应用规则中自动操作（UIA；Auto Ask 优先）");
            return null;
        }
        catch (Exception ex) { return $"自动鼠标监听不可用（{ex.GetType().Name}），可继续使用手动热键。"; }
    }
    private async Task AutoReadAsync(nint window, System.Windows.Point start, System.Windows.Point end, long epoch)
    {
        // Process identity lookup runs outside the low-level hook and UI thread.
        var path = await Task.Run(() => ApplicationIdentity.FromWindow(window));
        if (epoch != mouseEpoch || exiting || ocrOperation.IsActive || !(settings.AllowsAutomatic(path) || settings.AllowsAutomaticAsk(path)) || Native.GetForegroundWindow() != window) return;
        var identity = path! + "|" + window;
        await automaticSelection.RequestAsync(identity, async token =>
        {
            var stable = await StableSelection.CaptureAsync((focus, sampleToken) =>
                capture.CaptureSnapshotAsync(window, sampleToken, start, end, focus), token);
            return stable.Text;
        }, () => epoch == mouseEpoch && !exiting && (settings.AllowsAutomatic(path) || settings.AllowsAutomaticAsk(path)) && Native.GetForegroundWindow() == window,
            text =>
            {
                settingsWindow?.SetAutomaticStatus("已获取有效选区");
                if (settings.AllowsAutomaticAsk(path)) { AskAi(text, true); }
                else if (settings.AllowsAutomatic(path) && settings.AllowsAutomaticProvider) { InvalidateSelectionAction(); questionSession?.Cancel(); _ = reading!.ReadTextAsync(text, settings); }
            },
            message => settingsWindow?.SetAutomaticStatus(message));
    }
    private void StopReading()
    {
        settingsWindow?.CancelVirtualMicrophoneTest();
        agentExecution?.CancelAll();
        audioWorkbench?.CancelGeneration();
        if (ocrOperation.IsActive) settingsWindow?.SetNotice("屏幕 OCR 已取消。");
        InvalidateSelectionAction(); questionSession?.Cancel();
        ocrOperation.Cancel(); mouse?.CancelGesture(); ++mouseEpoch; automaticSelection.Invalidate(); reading?.Stop();
    }
    private void InvalidateSelectionAction()
    { ++selectionEpoch; selectionAction?.Cancel(); selectionAction = null; }
    private async Task CaptureActionAsync(nint foreground, bool ask)
    {
        InvalidateSelectionAction();
        using var request = new CancellationTokenSource(); selectionAction = request; var epoch = selectionEpoch;
        mouse?.CancelGesture(); automaticSelection.Invalidate(); ++mouseEpoch;
        try
        {
            var text = await capture.CaptureTextAsync(foreground, request.Token);
            if (exiting || epoch != selectionEpoch || request.IsCancellationRequested) return;
            if (string.IsNullOrWhiteSpace(text.Text)) throw new UserFeedbackException("没有选中文字。");
            if (ask) AskAi(text.Text);
            else { questionSession?.Cancel(); _ = reading!.ReadCapturedTextAsync(text, settings); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!exiting && epoch == selectionEpoch)
            {
                var message = ex is UserFeedbackException ? ex.Message : "无法读取当前选区，请重试。";
                settingsWindow?.SetNotice(message); Notify(message);
            }
        }
        finally { if (ReferenceEquals(selectionAction, request)) selectionAction = null; }
    }
    private void AskAi(string text) => AskAi(text, false);
    private void AskAi(string text, bool automatic)
    {
        if (exiting) return;
        var started = false;
        try
        {
            if (ocrOperation.IsActive) throw new UserFeedbackException("请先完成或取消当前 OCR 框选，再问 AI。");
            if (automatic && (!settings.AutoAsk || !settings.AllowsAutomaticLlm)) { settingsWindow?.SetAutomaticStatus("Auto Ask 未获当前 LLM 地址上传授权，未发送。"); return; }
            if (!settings.HasValidLlmConfiguration) throw new UserFeedbackException("请在设置的 AI 问答区域保存合法地址和模型。");
            var key = AzureCredentials.Llm(settings.LlmBaseUrl).Read();
            ChatCompletionClient.Validate(text, settings, key);
            InvalidateSelectionAction(); mouse?.CancelGesture(); ++mouseEpoch; automaticSelection.Invalidate();
            var task = questionSession!.AskAsync(text, settings, key!, automatic);
            started = true;
            if (aiAnswer is null)
            {
                var window = new AiAnswerWindow(questionSession, () => settings, () => reading!.Status.Message,
                    () => { InvalidateSelectionAction(); questionSession.Cancel(); },
                    () => { InvalidateSelectionAction(); mouse?.CancelGesture(); ++mouseEpoch; automaticSelection.Invalidate(); _ = questionSession.ReadAnswerAsync(settings); },
                    text => AskAi(text));
                aiAnswer = window;
                window.Closed += (_, _) => { InvalidateSelectionAction(); if (ReferenceEquals(aiAnswer, window)) aiAnswer = null; };
            }
            aiAnswer.ShowAnswer(!automatic);
            _ = task;
        }
        catch (Exception ex)
        {
            if (started) questionSession?.Cancel();
            var message = ex is UserFeedbackException ? ex.Message : "无法开始 AI 问答，请检查凭据和配置。";
            aiAnswer?.SetNotice(message);
            settingsWindow?.SetNotice(message); Notify(message);
        }
    }
    private async void StartOcr()
    {
        if (exiting) return;
        InvalidateSelectionAction();
        // Suppress overlay gestures and invalidate pending UIA capture without stopping current speech.
        mouse?.Dispose(); mouse = null; ++mouseEpoch; automaticSelection.Invalidate();
        var language = settings.OcrLanguage;
        settingsWindow?.SetNotice("正在准备屏幕框选；旧 OCR 计算尚未结束时需稍候。Ctrl+Alt+X 可取消。");
        try
        {
            ocrResult?.HideForCapture();
            aiAnswer?.HideForCapture();
            await ocrOperation.RunAsync(async token =>
            {
                await ocrGate.WaitAsync(token);
                try
                {
                    var snapshots = await ScreenSnapshot.CaptureAsync(token);
                    System.Drawing.Bitmap image;
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        using var selection = new ScreenSelection(snapshots, token);
                        image = await selection.Result;
                    }
                    finally { foreach (var snapshot in snapshots) snapshot.Dispose(); }
                    using (image)
                    {
                        token.ThrowIfCancellationRequested();
                        settingsWindow?.SetNotice("正在离线识别屏幕文字… Ctrl+Alt+X 可取消。");
                        return await ocr.RecognizeAsync(image, language, token).ConfigureAwait(false);
                    }
                }
                finally { ocrGate.Release(); }
            }, text =>
            {
                if (exiting) return;
                if (ocrResult is null)
                {
                    var window = new OcrResultWindow(text, () => settings, result =>
                    {
                        mouse?.CancelGesture(); ++mouseEpoch; automaticSelection.Invalidate();
                        InvalidateSelectionAction(); questionSession?.Cancel();
                        return reading!.ReadCapturedTextAsync(result, settings);
                    }, text => AskAi(text));
                    ocrResult = window;
                    window.Closed += (_, _) => { if (ReferenceEquals(ocrResult, window)) ocrResult = null; };
                }
                PresentOcrResult(ocrResult, aiAnswer, text);
                settingsWindow?.SetNotice("OCR 已完成，复制或朗读请在结果窗口主动操作。");
            }, message => { if (!exiting) { settingsWindow?.SetNotice(message); Notify(message); } },
                () => { if (!exiting) settingsWindow?.SetNotice("屏幕 OCR 已取消。"); });
        }
        catch (Exception ex)
        {
            if (!exiting)
            {
                var message = ex is UserFeedbackException ? ex.Message : "无法准备屏幕 OCR，请检查桌面访问权限后重试。";
                settingsWindow?.SetNotice(message); Notify(message);
            }
        }
        finally
        {
            if (!exiting && !ocrOperation.IsActive)
            {
                // A superseded request leaves the prior result hidden for its successor.
                // Restore before reinstalling Auto Speak so focus changes cannot form gestures.
                aiAnswer?.RestoreAfterCapture();
                ocrResult?.RestoreAfterCapture();
                ++mouseEpoch; var error = ApplyAutomatic();
                if (error is not null) Notify(error);
            }
        }
    }
    internal static void PresentOcrResult(OcrResultWindow result, AiAnswerWindow? answer, CapturedText text)
    {
        // ShowActivated=false preserves focus, but Show still raises a restored window in z-order.
        // Restore the answer first so the new OCR result remains the last window shown/activated.
        answer?.RestoreAfterCapture();
        result.ShowResult(text);
    }
    private void CancelAutomaticReading()
    {
        mouse?.CancelGesture(); ++mouseEpoch; automaticSelection.Invalidate(true);
        reading?.CancelPendingAutomatic();
    }
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
        exiting = true;
        agentPipe?.Dispose(); agentExecution?.Dispose();
        audioWorkbench?.Close();
        exiting = true; InvalidateSelectionAction(); questionSession?.Dispose(); aiAnswer?.Close(); ocrOperation.Dispose(); ocrResult?.Close(); mouse?.Dispose(); automaticSelection.Dispose(); hotkeys?.Dispose(); reading?.Dispose();
        if (tray is not null) { tray.Visible = false; tray.ContextMenuStrip?.Dispose(); tray.Dispose(); }
        instance?.Dispose(); base.OnExit(e);
    }
}
