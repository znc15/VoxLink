using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoxLink.UI.Controls;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Pages;

public sealed partial class ModelProvidersPage : Page
{
    private bool _loading;

    public ModelProvidersPage()
    {
        InitializeComponent();
        Loaded += ModelProvidersPage_Loaded;
        Unloaded += ModelProvidersPage_Unloaded;
    }

    public AppController Controller => App.Controller;

    private void ModelProvidersPage_Loaded(object sender, RoutedEventArgs args)
    {
        Controller.PropertyChanged += Controller_PropertyChanged;
        LoadSelections();
        RefreshState();
    }

    private void ModelProvidersPage_Unloaded(object sender, RoutedEventArgs args) =>
        Controller.PropertyChanged -= Controller_PropertyChanged;

    private void Controller_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        LoadSelections();
        RefreshState();
    }

    private void LoadSelections()
    {
        _loading = true;
        try
        {
            SelectByTag(
                TranslationBackendBox,
                Controller.Settings.UseAiTranslation
                    ? Controller.Settings.TranslationBackend.ToString()
                    : TranslationBackend.PublicFree.ToString());
            SelectByTag(AsrProviderBox, CurrentAsrTag());
            SelectByTag(SpeechServiceBox, Controller.Settings.SpeechServiceMode.ToString());
        }
        finally
        {
            _loading = false;
        }
    }

    private string CurrentAsrTag()
    {
        if (Controller.Settings.UseCloudAsr
            && Controller.Settings.AsrProvider != AsrProvider.LocalWhisper)
        {
            return Controller.Settings.AsrProvider.ToString();
        }

        return Controller.Settings.WhisperModel.ToLowerInvariant() switch
        {
            "base" => "WhisperBase",
            "small" => "WhisperSmall",
            "large-v3-turbo" => "WhisperLargeV3Turbo",
            _ => "WhisperTiny"
        };
    }

    private void RefreshState()
    {
        var translation = Controller.Settings.UseAiTranslation
            ? Controller.Settings.TranslationBackend
            : TranslationBackend.PublicFree;
        TranslationStatusText.Text = translation switch
        {
            TranslationBackend.PublicFree => "免费在线",
            TranslationBackend.LocalMiniCpm => LocalStatus(LocalModelIds.MiniCpm51BGguf),
            TranslationBackend.LocalHyMtGguf => LocalStatus(LocalModelIds.HyMt15Gguf),
            _ => "云端 AI"
        };

        AsrStatusText.Text = Controller.Settings.UseCloudAsr
            ? Controller.Settings.AllowCloudAudioUpload ? "云端 · 已授权上传" : "云端 · 等待上传授权"
            : LocalStatus(LocalModelIds.WhisperId(Controller.Settings.WhisperModel));

        SpeechStatusText.Text = Controller.Settings.SpeechServiceMode switch
        {
            SpeechServiceMode.Kokoro => LocalStatus(LocalModelIds.Kokoro82M),
            SpeechServiceMode.Remote => "云端语音",
            _ => "系统语音"
        };

        UpdateLocalOption(HyMtGgufOption, "本地混元翻译 HY-MT1.5-1.8B（GGUF）", LocalModelIds.HyMt15Gguf);
        UpdateLocalOption(MiniCpmOption, "本地 MiniCPM5-1B", LocalModelIds.MiniCpm51BGguf);
        UpdateLocalOption(WhisperBaseOption, "Whisper base（推荐）", LocalModelIds.WhisperBase);
        UpdateLocalOption(WhisperSmallOption, "Whisper small（更准确）", LocalModelIds.WhisperSmall);
        UpdateLocalOption(WhisperLargeV3TurboOption, "Whisper large-v3-turbo（最准确）", LocalModelIds.WhisperLargeV3Turbo);
        UpdateLocalOption(KokoroOption, "本地 Kokoro-82M", LocalModelIds.Kokoro82M);
        var canSelect = !Controller.HasBusyLocalModels && !Controller.IsBusy;
        TranslationBackendBox.IsEnabled = canSelect;
        AsrProviderBox.IsEnabled = canSelect;
        SpeechServiceBox.IsEnabled = canSelect;
        ConfigureTranslationButton.IsEnabled = canSelect;
        ConfigureAsrButton.IsEnabled = canSelect;
        ConfigureSpeechButton.IsEnabled = canSelect;

        ProviderResultBar.Message = Controller.ModelServiceResultMessage ?? string.Empty;
        ProviderResultBar.IsOpen = !string.IsNullOrWhiteSpace(Controller.ModelServiceResultMessage);
        ProviderErrorBar.Message = Controller.ErrorMessage ?? string.Empty;
        ProviderErrorBar.IsOpen = !string.IsNullOrWhiteSpace(Controller.ErrorMessage);
        RestartInfoBar.IsOpen = Controller.NeedsSessionRestart;
    }

    private void UpdateLocalOption(ComboBoxItem option, string name, string modelId)
    {
        var installed = Controller.LocalModels.FirstOrDefault(model => model.Id == modelId)?.Installed == true;
        option.Content = $"{name} · {(installed ? "已安装" : "请先安装")}";
        option.IsEnabled = installed;
    }
    private string LocalStatus(string modelId) =>
        Controller.LocalModels.FirstOrDefault(model => model.Id == modelId)?.Installed == true
            ? "本地 · 已安装"
            : "本地 · 请先安装";

    private async void TranslationBackendBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || !TryReadTag(TranslationBackendBox, out var tag))
        {
            return;
        }

        if (tag is nameof(TranslationBackend.LocalMiniCpm) or nameof(TranslationBackend.LocalHyMtGguf))
        {
            var modelId = tag == nameof(TranslationBackend.LocalHyMtGguf)
                ? LocalModelIds.HyMt15Gguf
                : LocalModelIds.MiniCpm51BGguf;
            await Controller.InstallAndActivateLocalModelAsync(
                modelId,
                reportToModelService: true);
        }
        else if (Enum.TryParse<TranslationBackend>(tag, out var backend))
        {
            Controller.Settings.SelectTranslationBackend(backend);
        }
        LoadSelections();
        RefreshState();
    }

    private async void AsrProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || !TryReadTag(AsrProviderBox, out var tag))
        {
            return;
        }

        var localModelId = tag switch
        {
            "WhisperTiny" => LocalModelIds.WhisperTiny,
            "WhisperBase" => LocalModelIds.WhisperBase,
            "WhisperSmall" => LocalModelIds.WhisperSmall,
            "WhisperLargeV3Turbo" => LocalModelIds.WhisperLargeV3Turbo,
            _ => null
        };
        if (localModelId is not null)
        {
            await Controller.InstallAndActivateLocalModelAsync(
                localModelId,
                reportToModelService: true);
        }
        else if (Enum.TryParse<AsrProvider>(tag, out var provider))
        {
            Controller.Settings.SelectAsrProvider(provider);
        }
        LoadSelections();
        RefreshState();
    }

    private async void SpeechServiceBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || !TryReadTag(SpeechServiceBox, out var tag)
            || !Enum.TryParse<SpeechServiceMode>(tag, out var mode))
        {
            return;
        }

        if (mode == SpeechServiceMode.Kokoro)
        {
            await Controller.InstallAndActivateLocalModelAsync(
                LocalModelIds.Kokoro82M,
                reportToModelService: true);
        }
        else
        {
            Controller.Settings.SelectSpeechService(mode);
        }
        LoadSelections();
        RefreshState();
    }

    private async void ConfigureTranslation_Click(object sender, RoutedEventArgs args)
    {
        var backend = Controller.Settings.TranslationBackend;
        if (backend is TranslationBackend.LocalMiniCpm or TranslationBackend.LocalHyMtGguf)
        {
            await ShowSimpleDialogAsync(
                TranslationServiceLabel(backend),
                "本地模型装好就能用，不用填 Key 和地址；安装管理在「本地模型」页。");
            return;
        }

        if (backend == TranslationBackend.PublicFree)
        {
            await ShowSimpleDialogAsync(
                "公共免密翻译",
                "不用 API Key，也不用填任何配置。");
            return;
        }

        var content = new TranslationServiceDialogContent(Controller);
        if (await ShowSettingsDialogAsync(
                $"{TranslationServiceLabel(backend)} · 翻译设置",
                content,
                Controller.IsRunning ? "保存（重启后生效）" : "保存并测试",
                content.Validate) == ContentDialogResult.Primary)
        {
            content.Commit();
            if (Controller.IsRunning)
            {
                await Controller.SaveCommittedServiceSettingsAsync();
            }
            else
            {
                await Controller.TestTranslationAsync();
            }
        }
    }

    private async void ConfigureAsr_Click(object sender, RoutedEventArgs args)
    {
        if (!Controller.Settings.UseCloudAsr)
        {
            await ShowSimpleDialogAsync(
                TryReadTag(AsrProviderBox, out var localTag) ? AsrServiceLabel(localTag) : "本地语音识别",
                "选好模型就能用；没装的模型会在启动前自动下载。"
            );
            return;
        }

        var content = new AsrServiceDialogContent(Controller);
        var asrLabel = TryReadTag(AsrProviderBox, out var asrTag) ? AsrServiceLabel(asrTag) : "语音识别";
        if (await ShowSettingsDialogAsync(
                $"{asrLabel} · 语音识别设置",
                content,
                Controller.IsRunning ? "保存（重启后生效）" : "保存并校验",
                content.Validate) == ContentDialogResult.Primary)
        {
            content.Commit();
            if (Controller.IsRunning)
            {
                await Controller.SaveCommittedServiceSettingsAsync();
            }
            else
            {
                await Controller.PrepareModelAsync();
            }
        }
    }

    private async void ConfigureSpeech_Click(object sender, RoutedEventArgs args)
    {
        var content = new SpeechServiceDialogContent(Controller);
        var speechLabel = TryReadTag(SpeechServiceBox, out var speechTag) ? SpeechServiceLabel(speechTag) : "语音合成";
        var result = await ShowSettingsDialogAsync(
            $"{speechLabel} · 语音合成设置",
            content,
            content.HasEditableSettings
                ? Controller.IsRunning ? "保存（重启后生效）" : "保存并试听"
                : "关闭",
            content.Validate);
        if (result == ContentDialogResult.Primary && content.HasEditableSettings)
        {
            content.Commit();
            if (Controller.IsRunning)
            {
                await Controller.SaveCommittedServiceSettingsAsync();
            }
            else
            {
                await Controller.TestSpeechAsync();
            }
        }
    }

    private static string TranslationServiceLabel(TranslationBackend backend) => backend switch
    {
        TranslationBackend.PublicFree => "公共免密翻译",
        TranslationBackend.DashScope => "DashScope 通义千问",
        TranslationBackend.DeepSeek => "DeepSeek",
        TranslationBackend.OpenAiCompatible => "OpenAI 兼容",
        TranslationBackend.Custom => "自定义服务",
        TranslationBackend.LocalMiniCpm => "本地 MiniCPM5-1B",
        TranslationBackend.LocalHyMtGguf => "本地混元翻译 HY-MT1.5-1.8B（GGUF）",
        _ => "翻译"
    };

    private static string AsrServiceLabel(string tag) => tag switch
    {
        "WhisperTiny" => "Whisper tiny",
        "WhisperBase" => "Whisper base",
        "WhisperSmall" => "Whisper small",
        "WhisperLargeV3Turbo" => "Whisper large-v3-turbo",
        "Soniox" => "Soniox",
        "SiliconFlow" => "硅基流动",
        "MiMo" => "小米 MiMo",
        "OpenAiCompatible" => "OpenAI 兼容",
        "Custom" => "自定义服务",
        _ => "语音识别"
    };

    private static string SpeechServiceLabel(string tag) => tag switch
    {
        "SystemFallback" => "系统语音",
        "Kokoro" => "本地 Kokoro-82M",
        "Remote" => "远程语音服务",
        _ => "语音合成"
    };

    private void OpenLocalModels_Click(object sender, RoutedEventArgs args) => Controller.RequestLocalModels();

    private async Task<ContentDialogResult> ShowSettingsDialogAsync(
        string title,
        object content,
        string primaryText = "保存并测试",
        Func<bool>? validate = null)
    {
        if (XamlRoot is null)
        {
            return ContentDialogResult.None;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (validate is not null)
        {
            dialog.PrimaryButtonClick += (_, args) => args.Cancel = !validate();
        }
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
            return ContentDialogResult.None;
        }
    }

    private async Task ShowSimpleDialogAsync(string title, string message)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot
        };
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    private static void SelectByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem { Tag: string itemTag }
                && itemTag.Equals(tag, StringComparison.Ordinal))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
        comboBox.SelectedIndex = -1;
    }

    private static bool TryReadTag(ComboBox comboBox, out string tag)
    {
        if (comboBox.SelectedItem is ComboBoxItem { Tag: string value })
        {
            tag = value;
            return true;
        }
        tag = string.Empty;
        return false;
    }

    private void ProviderErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        Controller.DismissError();

    /// <summary>
    /// 强制下拉列表弹出在控件正下方。WinUI 3 ComboBox 的模板 Popup 偏移恒为 0，
    /// 实际按「选中项对齐控件」定位，选中项靠后时弹层整体翻到控件上方盖住界面；
    /// 展开动画结束后测量弹层实际位置，统一改按控件底边对齐。
    /// </summary>
    private async void ProviderBox_DropDownOpened(object? sender, object args)
    {
        if (sender is not ComboBox comboBox)
        {
            return;
        }

        var popup = FindTemplatePopup(comboBox);
        var content = popup?.Child as FrameworkElement;
        if (popup is null || content is null)
        {
            return;
        }

        try
        {
            // 等展开动画结束（约 200ms）再测量，动画期间弹层还在位移
            await Task.Delay(280);

            // 弹层内容顶部相对 ComboBox 顶部的距离（负值 = 翻到了上方）。
            // TransformToVisual 测的是渲染后位置，已含 VerticalOffset 效果，直接补差即可。
            var top = content.TransformToVisual(comboBox).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            var desired = comboBox.ActualHeight + 2;
            var delta = desired - top;
            if (Math.Abs(delta) > 0.5)
            {
                popup.VerticalOffset += delta;
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            // 元素已收起或已从树中移除时忽略
        }
    }

    private static Microsoft.UI.Xaml.Controls.Primitives.Popup? FindTemplatePopup(FrameworkElement root)
    {
        var count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i)
                is Microsoft.UI.Xaml.Controls.Primitives.Popup popup)
            {
                return popup;
            }

            if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i) is FrameworkElement child
                && FindTemplatePopup(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }
}
