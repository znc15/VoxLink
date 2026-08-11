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
            TranslationBackend.PublicFree => "免密在线",
            TranslationBackend.LocalMiniCpm => LocalStatus(LocalModelIds.MiniCpm51BGguf),
            TranslationBackend.ManagedHyMt => LocalStatus(LocalModelIds.HyMt1518B),
            TranslationBackend.ManagedM2M100 => LocalStatus(LocalModelIds.M2M100418M),
            TranslationBackend.ManagedSmall100 => LocalStatus(LocalModelIds.Small100),
            _ => "云端文本服务"
        };

        AsrStatusText.Text = Controller.Settings.UseCloudAsr
            ? Controller.Settings.AllowCloudAudioUpload ? "云端 · 已授权上传" : "云端 · 等待上传授权"
            : LocalStatus(LocalModelIds.WhisperId(Controller.Settings.WhisperModel));

        SpeechStatusText.Text = Controller.Settings.SpeechServiceMode switch
        {
            SpeechServiceMode.Kokoro => LocalStatus(LocalModelIds.Kokoro82M),
            SpeechServiceMode.Remote => "远程语音",
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

    private async void ConfigureTranslation_Click(object sender, RoutedEventArgs args)
    {
        if (Controller.Settings.TranslationBackend is TranslationBackend.PublicFree
            or TranslationBackend.LocalMiniCpm)
        {
            await ShowSimpleDialogAsync(
                Controller.Settings.TranslationBackend == TranslationBackend.LocalMiniCpm
                    ? "本地翻译"
                    : "公共免密翻译",
                Controller.Settings.TranslationBackend == TranslationBackend.LocalMiniCpm
                    ? "模型在“本地模型”页管理，安装后会随 VoxLink 一起启动。"
                    : "无需 API Key 或其他配置。");
            return;
        }

        var content = new TranslationServiceDialogContent(Controller);
        if (await ShowSettingsDialogAsync(
                "翻译设置",
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
                "本地语音识别",
                "选择模型即可。未安装时，VoxLink 会在启动翻译前自动下载并校验。"
            );
            return;
        }

        var content = new AsrServiceDialogContent(Controller);
        if (await ShowSettingsDialogAsync(
                "语音识别设置",
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
        var result = await ShowSettingsDialogAsync(
            "语音合成设置",
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
