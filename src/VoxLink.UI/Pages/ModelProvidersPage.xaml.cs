using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
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
            TranslationBackend.ManagedHyMt => LocalStatus(LocalModelIds.HyMt1518B),
            TranslationBackend.ManagedM2M100 => LocalStatus(LocalModelIds.M2M100418M),
            TranslationBackend.ManagedSmall100 => LocalStatus(LocalModelIds.Small100),
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

        UpdateLocalOption(MiniCpmOption, "本地 MiniCPM5-1B", LocalModelIds.MiniCpm51BGguf);
        UpdateLocalOption(ManagedHyMtOption, "本地混元翻译 HY-MT1.5-1.8B", LocalModelIds.HyMt1518B);
        UpdateLocalOption(ManagedM2M100Option, "本地 M2M-100 418M", LocalModelIds.M2M100418M);
        UpdateLocalOption(ManagedSmall100Option, "本地 SMaLL-100", LocalModelIds.Small100);
        UpdateLocalOption(WhisperBaseOption, "Whisper base（推荐）", LocalModelIds.WhisperBase);
        UpdateLocalOption(WhisperSmallOption, "Whisper small（更准确）", LocalModelIds.WhisperSmall);
        UpdateLocalOption(KokoroOption, "本地 Kokoro-82M", LocalModelIds.Kokoro82M);
        UpdateLocalOption(ManagedMossOption, "本地 MOSS 转写+说话人", LocalModelIds.MossTranscribeDiarize);
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

        if (tag == nameof(TranslationBackend.LocalMiniCpm))
        {
            await Controller.InstallAndActivateLocalModelAsync(
                LocalModelIds.MiniCpm51BGguf,
                reportToModelService: true);
        }
        else if (tag is nameof(TranslationBackend.ManagedHyMt)
                 or nameof(TranslationBackend.ManagedM2M100)
                 or nameof(TranslationBackend.ManagedSmall100))
        {
            var modelId = tag switch
            {
                nameof(TranslationBackend.ManagedHyMt) => LocalModelIds.HyMt1518B,
                nameof(TranslationBackend.ManagedM2M100) => LocalModelIds.M2M100418M,
                _ => LocalModelIds.Small100
            };
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
            "LocalManagedMoss" => LocalModelIds.MossTranscribeDiarize,
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

    private void TranslationBackendBox_DropDownOpened(object sender, object args) =>
        AlignDropdownBelowSelectionBar(TranslationBackendBox);

    private void AsrProviderBox_DropDownOpened(object sender, object args) =>
        AlignDropdownBelowSelectionBar(AsrProviderBox);

    private void SpeechServiceBox_DropDownOpened(object sender, object args) =>
        AlignDropdownBelowSelectionBar(SpeechServiceBox);

    private static void AlignDropdownBelowSelectionBar(ComboBox comboBox)
    {
        var popup = FindTemplatePopup(comboBox);
        if (popup is not null)
        {
            popup.VerticalOffset = comboBox.ActualHeight + 2;
        }
    }

    private static Popup? FindTemplatePopup(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is Popup popup)
            {
                return popup;
            }

            var nested = FindTemplatePopup(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }

    private async void ConfigureTranslation_Click(object sender, RoutedEventArgs args)
    {
        var backend = Controller.Settings.TranslationBackend;
        if (backend is TranslationBackend.LocalMiniCpm
            or TranslationBackend.ManagedHyMt
            or TranslationBackend.ManagedM2M100
            or TranslationBackend.ManagedSmall100)
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
        TranslationBackend.ManagedHyMt => "本地 HY-MT1.5",
        TranslationBackend.ManagedM2M100 => "本地 M2M-100",
        TranslationBackend.ManagedSmall100 => "本地 SMaLL-100",
        _ => "翻译"
    };

    private static string AsrServiceLabel(string tag) => tag switch
    {
        "WhisperTiny" => "Whisper tiny",
        "WhisperBase" => "Whisper base",
        "WhisperSmall" => "Whisper small",
        "LocalManagedMoss" => "本地 MOSS",
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
}
