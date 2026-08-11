using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Controls;

public sealed partial class SpeechServiceDialogContent : UserControl
{
    private readonly AppController _controller;
    private readonly SpeechServiceMode _mode;
    private bool _loading = true;
    public SpeechServiceDialogContent(AppController controller)
    {
        _controller = controller;
        _mode = controller.Settings.SpeechServiceMode;
        InitializeComponent();

        NoConfigText.Visibility = _mode == SpeechServiceMode.SystemFallback
            ? Visibility.Visible
            : Visibility.Collapsed;
        KokoroPanel.Visibility = _mode == SpeechServiceMode.Kokoro
            ? Visibility.Visible
            : Visibility.Collapsed;
        RemotePanel.Visibility = _mode == SpeechServiceMode.Remote
            ? Visibility.Visible
            : Visibility.Collapsed;

        SpeakerBox.Value = controller.Settings.KokoroSpeakerId;
        SpeedBox.Value = controller.Settings.KokoroSpeed;
        ApiKeyBox.Password = controller.Settings.SpeechApiKey;
        VoiceBox.Text = controller.Settings.SpeechVoice;
        BaseUrlBox.Text = controller.Settings.SpeechBaseUrl;
        ModelBox.Text = controller.Settings.SpeechModel;
        SelectProtocol(controller.Settings.SpeechProtocol);
        HeaderEditor.Configure(controller, HeaderEditorTarget.Speech, commitImmediately: false);
        _loading = false;
    }

    public bool HasEditableSettings => _mode != SpeechServiceMode.SystemFallback;

    public bool Validate() => _mode != SpeechServiceMode.Remote || HeaderEditor.Validate();

    public void Commit()
    {
        if (_mode == SpeechServiceMode.Kokoro)
        {
            if (double.IsFinite(SpeakerBox.Value))
            {
                _controller.Settings.KokoroSpeakerId = (int)Math.Round(SpeakerBox.Value);
            }
            if (double.IsFinite(SpeedBox.Value))
            {
                _controller.Settings.KokoroSpeed = SpeedBox.Value;
            }
            return;
        }

        if (_mode != SpeechServiceMode.Remote)
        {
            return;
        }

        if (ProtocolBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<SpeechProtocol>(tag, out var protocol)
            && protocol != _controller.Settings.SpeechProtocol)
        {
            _controller.Settings.ApplySpeechProtocolDefaults(protocol);
        }
        _controller.Settings.SpeechApiKey = ApiKeyBox.Password;
        _controller.Settings.SpeechBaseUrl = BaseUrlBox.Text.Trim();
        _controller.Settings.SpeechModel = ModelBox.Text.Trim();
        _controller.Settings.SpeechVoice = VoiceBox.Text.Trim();
        HeaderEditor.Commit();
    }

    private void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || ProtocolBox.SelectedItem is not ComboBoxItem { Tag: string tag }
            || !Enum.TryParse<SpeechProtocol>(tag, out var protocol))
        {
            return;
        }

        (BaseUrlBox.Text, ModelBox.Text, VoiceBox.Text) = protocol switch
        {
            SpeechProtocol.DashScope => (
                "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation",
                "qwen3-tts-flash",
                "Cherry"),
            SpeechProtocol.MiMo => (
                "https://api.xiaomimimo.com/v1/chat/completions",
                "mimo-v2.5-tts",
                "mimo_default"),
            _ => ("http://localhost:8000/v1/audio/speech", "tts-1", "alloy")
        };
    }
    private void SelectProtocol(SpeechProtocol protocol)
    {
        foreach (var item in ProtocolBox.Items)
        {
            if (item is ComboBoxItem { Tag: string tag }
                && tag.Equals(protocol.ToString(), StringComparison.Ordinal))
            {
                ProtocolBox.SelectedItem = item;
                return;
            }
        }
    }
}
