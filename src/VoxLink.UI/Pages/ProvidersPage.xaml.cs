using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoxLink.UI.Controls;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Pages;

public sealed partial class ProvidersPage : Page
{
    private bool _loading;

    public ProvidersPage()
    {
        InitializeComponent();
        Loaded += ProvidersPage_Loaded;
        Unloaded += ProvidersPage_Unloaded;
    }

    public AppController Controller => App.Controller;

    private void ProvidersPage_Loaded(object sender, RoutedEventArgs args)
    {
        LoadSettingsIntoControls();
        Controller.PropertyChanged += Controller_PropertyChanged;
        RefreshState();
    }
    private void ProvidersPage_Unloaded(object sender, RoutedEventArgs args) =>
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
        _loading = false;
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
        AsrProtocolBox.IsEnabled = Controller.Settings.AsrProvider == AsrProvider.Custom;
        SpeechCredentials.Visibility = Controller.Settings.UseRemoteSpeech
            ? Visibility.Visible
            : Visibility.Collapsed;
        TranslationDescription.Text = Controller.Settings.TranslationBackend switch
        {
            TranslationBackend.PublicFree => "默认免密翻译，失败时自动切换公共服务。",
            TranslationBackend.DashScope => "通过 DashScope 官方 OpenAI 兼容接口翻译与生成。",
            TranslationBackend.DeepSeek => "通过 DeepSeek 官方接口翻译与生成。",
            TranslationBackend.OpenAiCompatible => "连接本地模型或任意 OpenAI 兼容服务。",
            _ => "使用自定义服务地址、模型与请求头。"
        };
        AsrDescription.Text = Controller.Settings.AsrProvider switch
        {
            AsrProvider.LocalWhisper => "本地 Whisper 识别，原始音频不离开电脑。",
            AsrProvider.DashScope => "持续 WebSocket 识别，按每个启用音源建立独立连接。",
            AsrProvider.Soniox => "持续 WebSocket 识别，可选云端 speaker ID。",
            AsrProvider.SiliconFlow => "按智能断句上传 WAV 片段到 OpenAI 兼容接口。",
            AsrProvider.MiMo => "按智能断句通过 input_audio 上传 WAV 片段。",
            AsrProvider.OpenAiCompatible => "按智能断句上传 WAV 片段到自建兼容服务。",
            _ => "按所选协议连接自定义语音识别服务。"
        };
        SpeechDescription.Text = Controller.Settings.UseRemoteSpeech
            ? "远程语音失败时仍会回退到 Edge、Google 和 Windows 本地语音。"
            : "使用 Edge、Google 与 Windows 本地语音回退，无需 API Key。";
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

    private void RemoteSpeechSwitch_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_loading)
        {
            RefreshState();
        }
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
    private async void PrepareAsr_Click(object sender, RoutedEventArgs args) =>
        await Controller.PrepareModelAsync();

    private async void TestTranslation_Click(object sender, RoutedEventArgs args) =>
        await Controller.TestTranslationAsync();
    private async void TestSpeech_Click(object sender, RoutedEventArgs args) =>
        await Controller.TestSpeechAsync();

    private void ProviderErrorBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) =>
        Controller.DismissError();
}
