using System.Windows;
using System.Windows.Controls;
using SeleVox.Core;

namespace SeleVox.App;

public partial class MainWindow
{
    private CancellationTokenSource? ocrModelTest;
    private OcrModels? verifiedOcrModels;
    internal Func<OcrModels, CancellationToken, Task>? TestXiaomiOcr { get; init; }

    private void InitializeOcrSettings(Settings settings)
    {
        XiaomiModelPath.Text = settings.XiaomiModelPath ?? "";
        XiaomiProjectorPath.Text = settings.XiaomiProjectorPath ?? "";
        OcrEngineChoice.SelectedIndex = (int)settings.OcrEngine;
        IsVisibleChanged += (_, _) => { if (!IsVisible && ocrModelTest is not null) ResetOcrTest(); };
    }

    private OcrModels CandidateModels() => new(XiaomiModelPath.Text.Trim(), XiaomiProjectorPath.Text.Trim());

    private void OcrEngine_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (XiaomiModelsPanel is null || OcrLanguageChoice is null) return;
        var useXiaomi = OcrEngineChoice.SelectedIndex == (int)OcrEngine.XiaomiCuda;
        XiaomiModelsPanel.Visibility = useXiaomi ? Visibility.Visible : Visibility.Collapsed;
        OcrLanguageChoice.IsEnabled = !useXiaomi;
        if (!useXiaomi) ResetOcrTest();
    }

    private void OcrPath_Changed(object sender, TextChangedEventArgs e) => ResetOcrTest();

    private void ResetOcrTest()
    {
        verifiedOcrModels = null;
        ocrModelTest?.Cancel();
        ocrModelTest = null;
        if (OcrModelStatus is not null) OcrModelStatus.Text = "";
        if (TestOcrModel is not null) TestOcrModel.IsEnabled = true;
    }

    private void BrowseOcrModel_Click(object sender, RoutedEventArgs e) => BrowseOcrFile(XiaomiModelPath);
    private void BrowseOcrProjector_Click(object sender, RoutedEventArgs e) => BrowseOcrFile(XiaomiProjectorPath);

    private void BrowseOcrFile(TextBox target)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "GGUF 模型 (*.gguf)|*.gguf", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FileName;
    }

    private async void TestOcrModel_Click(object sender, RoutedEventArgs e)
    {
        if (ocrModelTest is not null || TestXiaomiOcr is null) return;
        using var test = new CancellationTokenSource();
        ocrModelTest = test;
        verifiedOcrModels = null;
        TestOcrModel.IsEnabled = false;
        OcrModelStatus.Text = "正在加载模型并识别测试图片…";
        var models = CandidateModels();
        try
        {
            await TestXiaomiOcr(models, test.Token);
            if (!ReferenceEquals(ocrModelTest, test)) return;
            verifiedOcrModels = models;
            OcrModelStatus.Text = "测试成功。点击底部“保存设置”启用。";
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(ocrModelTest, test))
                OcrModelStatus.Text = ex switch
                {
                    UserFeedbackException => ex.Message,
                    OperationCanceledException => "模型测试已取消。",
                    _ => "模型测试失败，请检查模型文件、CUDA 运行时和驱动。"
                };
        }
        finally
        {
            if (ReferenceEquals(ocrModelTest, test))
            {
                ocrModelTest = null;
                TestOcrModel.IsEnabled = true;
            }
        }
    }
}
