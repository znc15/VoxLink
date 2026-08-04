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
        LoadSettingsIntoControls();
        Controller.PropertyChanged += Controller_PropertyChanged;
        RefreshState();
    }

    private void ModelProvidersPage_Unloaded(object sender, RoutedEventArgs args) =>
        Controller.PropertyChanged -= Controller_PropertyChanged;

    private void Controller_PropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(AppController.Settings))
        {
            LoadSettingsIntoControls();
        }

        RefreshState();
    }

    private void LoadSettingsIntoControls()
    {
        _loading = true;
        try
        {
            Bindings.Update();
            TranslationBackendBox.SelectedIndex = (int)Controller.Settings.TranslationBackend;
            AsrProviderBox.SelectedIndex = (int)Controller.Settings.AsrProvider;
            AsrProtocolBox.SelectedIndex = (int)Controller.Settings.AsrProtocol;
            SpeechProtocolBox.SelectedIndex = (int)Controller.Settings.SpeechProtocol;
            TranslationApiKeyBox.Password = Controller.Settings.TranslationApiKey;
            AsrApiKeyBox.Password = Controller.Settings.AsrApiKey;
            SpeechApiKeyBox.Password = Controller.Settings.SpeechApiKey;
            TranslationHeaderEditor.Configure(Controller, HeaderEditorTarget.Translation);
            AsrHeaderEditor.Configure(Controller, HeaderEditorTarget.Asr);
            SpeechHeaderEditor.Configure(Controller, HeaderEditorTarget.Speech);
            TinyModelButton.IsChecked = Controller.Settings.WhisperModel == "tiny";
            BaseModelButton.IsChecked = Controller.Settings.WhisperModel == "base";
            SmallModelButton.IsChecked = Controller.Settings.WhisperModel == "small";
            KokoroSpeakerBox.Value = Controller.Settings.KokoroSpeakerId;
            KokoroSpeedBox.Value = Controller.Settings.KokoroSpeed;
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshState()
    {
        var usesPublicTranslation = Controller.Settings.TranslationBackend == TranslationBackend.PublicFree;
        var usesLocalTranslation = Controller.Settings.TranslationBackend == TranslationBackend.LocalMiniCpm;
        TranslationCredentials.Visibility = usesPublicTranslation || usesLocalTranslation
            ? Visibility.Collapsed
            : Visibility.Visible;
        TranslationRefinementSwitch.Visibility = usesPublicTranslation
            ? Visibility.Collapsed
            : Visibility.Visible;
        TranslationRefinementPromptBox.Visibility = !usesPublicTranslation
            && Controller.Settings.EnableTranslationRefinement
                ? Visibility.Visible
                : Visibility.Collapsed;
        AsrCloudCredentials.Visibility = Controller.Settings.UsesCloudAsr
            ? Visibility.Visible
            : Visibility.Collapsed;
        AsrLocalWhisperWarning.Visibility = Controller.Settings.UseCloudAsr
            && !Controller.Settings.UsesCloudAsr
                ? Visibility.Visible
                : Visibility.Collapsed;
        LocalWhisperPanel.Visibility = Visibility.Visible;
        var localWhisperEnabled = !Controller.Settings.UsesCloudAsr;
        LocalWhisperPanel.Opacity = localWhisperEnabled ? 1.0 : 0.45;
        PrepareModelButton.IsEnabled = localWhisperEnabled;
        TinyModelButton.IsEnabled = localWhisperEnabled;
        BaseModelButton.IsEnabled = localWhisperEnabled;
        SmallModelButton.IsEnabled = localWhisperEnabled;
        AsrProtocolBox.IsEnabled = Controller.Settings.AsrProvider == AsrProvider.Custom;
        var usesLocalKokoro = Controller.Settings.UseLocalKokoroTextToSpeech;
        LocalKokoroSettings.Visibility = usesLocalKokoro ? Visibility.Visible : Visibility.Collapsed;
        RemoteSpeechSwitch.IsEnabled = !usesLocalKokoro;
        SpeechCredentials.Visibility = !usesLocalKokoro && Controller.Settings.UseRemoteSpeech
            ? Visibility.Visible
            : Visibility.Collapsed;
        TranslationDescription.Text = Controller.Settings.TranslationBackend switch
        {
            TranslationBackend.PublicFree => "公共免密翻译；选择其他提供方后可配置并测试。",
            TranslationBackend.DashScope => "DashScope 官方 OpenAI 兼容接口，填写 API Key、模型等信息后可测试。",
            TranslationBackend.DeepSeek => "DeepSeek 官方接口，填写 API Key、模型等信息后可测试。",
            TranslationBackend.OpenAiCompatible => "任意 OpenAI 兼容服务，填写地址、模型与 API Key 后可测试。",
            TranslationBackend.LocalMiniCpm => "MiniCPM5-1B 在本机 CPU 上运行，不使用服务地址或 API Key；请先在下方模型目录安装。",
            _ => "自定义服务地址、模型、API Key 与请求头。"
        };
        AsrDescription.Text = Controller.Settings.UsesCloudAsr
            ? "选择云端提供方后显示所需配置；本地 Whisper 会同时置灰。"
            : "本地 Whisper 识别，原始音频不会离开电脑。";
        SpeechDescription.Text = usesLocalKokoro
            ? "Kokoro-82M 完全在本机生成语音；模型加载或生成失败时会直接报错，不会上传文字或静默切换在线服务。"
            : Controller.Settings.UseRemoteSpeech
                ? "使用所选远程语音服务，失败时回退本地系统语音。"
                : "使用 Edge、Google 或 Windows 系统语音作为普通语音输出兜底。";
        ModelProgressBar.Visibility = string.IsNullOrWhiteSpace(Controller.ModelStatus)
            ? Visibility.Collapsed
            : Visibility.Visible;
        ModelProgressText.Visibility = string.IsNullOrWhiteSpace(Controller.ModelStatus)
            ? Visibility.Collapsed
            : Visibility.Visible;
        ModelProgressBar.Value = Controller.ModelProgress;
        ModelProgressText.Text = string.IsNullOrWhiteSpace(Controller.ModelStatus)
            ? string.Empty
            : $"{Controller.ModelStatus} · {Controller.ModelProgress:P0}";
        ProviderErrorBar.Message = Controller.ErrorMessage ?? string.Empty;
        ProviderErrorBar.IsOpen = !string.IsNullOrWhiteSpace(Controller.ErrorMessage);
    }

    private void TranslationBackendBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || TranslationBackendBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<TranslationBackend>(tag, out var backend))
        {
            return;
        }

        Controller.Settings.ApplyTranslationBackendDefaults(backend);
        RefreshState();
    }

    private void AsrProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || AsrProviderBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<AsrProvider>(tag, out var provider))
        {
            return;
        }

        Controller.Settings.ApplyAsrProviderDefaults(provider);
        AsrProtocolBox.SelectedIndex = (int)Controller.Settings.AsrProtocol;
        RefreshState();
    }

    private void AsrProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || Controller.Settings.AsrProvider != AsrProvider.Custom
            || AsrProtocolBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<AsrProtocol>(tag, out var protocol))
        {
            return;
        }

        Controller.Settings.AsrProtocol = protocol;
        RefreshState();
    }

    private void TranslationRefinementSwitch_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_loading)
        {
            RefreshState();
        }
    }

    private void SpeechProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || SpeechProtocolBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<SpeechProtocol>(tag, out var protocol))
        {
            return;
        }

        Controller.Settings.ApplySpeechProtocolDefaults(protocol);
    }

    private void TranslationApiKeyBox_PasswordChanged(object sender, RoutedEventArgs args)
    {
        if (!_loading)
        {
            Controller.Settings.TranslationApiKey = TranslationApiKeyBox.Password;
        }
    }

    private void AsrApiKeyBox_PasswordChanged(object sender, RoutedEventArgs args)
    {
        if (!_loading)
        {
            Controller.Settings.AsrApiKey = AsrApiKeyBox.Password;
        }
    }

    private void SpeechApiKeyBox_PasswordChanged(object sender, RoutedEventArgs args)
    {
        if (!_loading)
        {
            Controller.Settings.SpeechApiKey = SpeechApiKeyBox.Password;
        }
    }

    private void LocalKokoroSwitch_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_loading)
        {
            Controller.NotifySettingsChanged();
            RefreshState();
        }
    }

    private void RemoteSpeechSwitch_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_loading)
        {
            Controller.NotifySettingsChanged();
            RefreshState();
        }
    }

    private void KokoroSpeakerBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_loading && double.IsFinite(args.NewValue))
        {
            Controller.Settings.KokoroSpeakerId = (int)Math.Round(args.NewValue);
        }
    }

    private void KokoroSpeedBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_loading && double.IsFinite(args.NewValue))
        {
            Controller.Settings.KokoroSpeed = args.NewValue;
        }
    }
    private async void PrepareModel_Click(object sender, RoutedEventArgs args) =>
        await Controller.PrepareModelAsync();

    private void WhisperModel_Checked(object sender, RoutedEventArgs args)
    {
        if (!_loading && sender is RadioButton { Tag: string model })
        {
            Controller.Settings.WhisperModel = model;
        }
    }

    private async void RefreshLocalModels_Click(object sender, RoutedEventArgs args) =>
        await Controller.RefreshLocalModelsAsync();

    private async void InstallLocalModel_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: LocalModelItem model })
        {
            return;
        }

        if (model.IsPartial)
        {
            await Controller.RetryLocalModelAsync(model.Id);
        }
        else
        {
            await Controller.InstallLocalModelAsync(model.Id);
        }
    }

    private async void RemoveLocalModel_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: LocalModelItem model } || XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = $"删除 {model.Name}？",
            Content = "模型文件将从本机删除。MiniCPM/Kokoro 正在使用时会阻止删除；Whisper 由独立安装器管理，删除前请先停止相关会话。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await Controller.RemoveLocalModelAsync(model.Id);
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine(exception);
        }
    }

    private async void OpenModelSource_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not HyperlinkButton { Tag: string url }
            || !TryCreateTrustedSourceUri(url, out var uri))
        {
            return;
        }

        await Windows.System.Launcher.LaunchUriAsync(uri);
    }

    private static bool TryCreateTrustedSourceUri(string value, out Uri uri)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            && candidate.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(candidate.UserInfo)
            && (IsHostOrSubdomain(candidate.IdnHost, "huggingface.co")
                || IsHostOrSubdomain(candidate.IdnHost, "github.com")))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static bool IsHostOrSubdomain(string host, string expected) =>
        host.Equals(expected, StringComparison.OrdinalIgnoreCase)
        || host.EndsWith('.' + expected, StringComparison.OrdinalIgnoreCase);
    private void ProviderErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        Controller.DismissError();
}
