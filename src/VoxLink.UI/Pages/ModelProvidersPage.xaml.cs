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
            TinyModelButton.IsChecked = Controller.Settings.WhisperModel == "tiny";
            BaseModelButton.IsChecked = Controller.Settings.WhisperModel == "base";
            SmallModelButton.IsChecked = Controller.Settings.WhisperModel == "small";
        }
        finally
        {
            _loading = false;
        }
    }

    private void RefreshState()
    {
        var usesPublicTranslation = Controller.Settings.TranslationBackend == TranslationBackend.PublicFree;
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
        SpeechCredentials.Visibility = Visibility.Visible;
        TranslationDescription.Text = Controller.Settings.TranslationBackend switch
        {
            TranslationBackend.PublicFree => "公共免密翻译；选择其他提供方后可配置并测试。",
            TranslationBackend.DashScope => "DashScope 官方 OpenAI 兼容接口，填写 API Key、模型等信息后可测试。",
            TranslationBackend.DeepSeek => "DeepSeek 官方接口，填写 API Key、模型等信息后可测试。",
            TranslationBackend.OpenAiCompatible => "任意 OpenAI 兼容服务，填写地址、模型与 API Key 后可测试。",
            _ => "自定义服务地址、模型、API Key 与请求头。"
        };
        AsrDescription.Text = Controller.Settings.UsesCloudAsr
            ? "选择云端提供方后显示所需配置；本地 Whisper 会同时置灰。"
            : "本地 Whisper 识别，原始音频不会离开电脑。";
        SpeechDescription.Text = "填写远程语音服务配置后即可试听；开启后实际朗读使用远程服务，失败时回退本地语音。";
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

    private void WhisperModel_Checked(object sender, RoutedEventArgs args)
    {
        if (!_loading && sender is RadioButton { Tag: string model })
        {
            Controller.Settings.WhisperModel = model;
        }
    }

    private void ProviderErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        Controller.DismissError();
}
