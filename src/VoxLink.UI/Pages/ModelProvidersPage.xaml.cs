using System.ComponentModel;
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
            WhisperModelButtons.SelectedItem = Controller.Settings.WhisperModel;
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshState()
    {
        var usesPublicTranslation = !Controller.Settings.UseAiTranslation
            || Controller.Settings.TranslationBackend == TranslationBackend.PublicFree;
        TranslationCredentials.Visibility = usesPublicTranslation
            ? Visibility.Collapsed
            : Visibility.Visible;
        TranslationRefinementSwitch.Visibility = usesPublicTranslation
            ? Visibility.Collapsed
            : Visibility.Visible;
        TranslationRefinementPromptBox.Visibility = !usesPublicTranslation
            && Controller.Settings.EnableTranslationRefinement
                ? Visibility.Visible
                : Visibility.Collapsed;
        var usesCloudAsr = Controller.Settings.UseCloudAsr && Controller.Settings.UsesCloudAsr;
        AsrCloudCredentials.Visibility = usesCloudAsr
            ? Visibility.Visible
            : Visibility.Collapsed;
        AsrLocalWhisperWarning.Visibility = Controller.Settings.UseCloudAsr
            && !Controller.Settings.UsesCloudAsr
                ? Visibility.Visible
                : Visibility.Collapsed;
        LocalWhisperPanel.Visibility = !Controller.Settings.UseCloudAsr
            ? Visibility.Visible
            : Visibility.Collapsed;
        AsrProtocolBox.IsEnabled = Controller.Settings.AsrProvider == AsrProvider.Custom;
        SpeechCredentials.Visibility = Controller.Settings.UseRemoteSpeech
            ? Visibility.Visible
            : Visibility.Collapsed;
        TranslationDescription.Text = Controller.Settings.TranslationBackend switch
        {
            TranslationBackend.PublicFree => "在 AI 与语音页开启 AI 翻译后使用此服务。",
            TranslationBackend.DashScope => "DashScope 官方 OpenAI 兼容接口。",
            TranslationBackend.DeepSeek => "DeepSeek 官方接口。",
            TranslationBackend.OpenAiCompatible => "任意 OpenAI 兼容服务。",
            _ => "自定义服务地址、模型与请求头。"
        };
        AsrDescription.Text = Controller.Settings.UseCloudAsr
            ? "云端识别会按提供方协议上传音频。"
            : "本地 Whisper 识别，原始音频不会离开电脑。";
        SpeechDescription.Text = Controller.Settings.UseRemoteSpeech
            ? "远程语音失败时回退到本地语音。"
            : "在 AI 与语音页开启远程语音服务后使用此配置。";
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

    private async void PrepareModel_Click(object sender, RoutedEventArgs args) =>
        await Controller.PrepareModelAsync();

    private void WhisperModelButtons_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_loading && WhisperModelButtons.SelectedItem is string model)
        {
            Controller.Settings.WhisperModel = model;
        }
    }

    private void ProviderErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        Controller.DismissError();
}
