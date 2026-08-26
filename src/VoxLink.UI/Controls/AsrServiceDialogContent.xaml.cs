using Microsoft.UI.Xaml.Controls;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Controls;

public sealed partial class AsrServiceDialogContent : UserControl
{
    private readonly AppController _controller;

    public AsrServiceDialogContent(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        VoxLink.UI.Infrastructure.ComboBoxPopupPlacer.Apply(this);
        ApiKeyBox.Password = controller.Settings.AsrApiKey;
        AllowUploadSwitch.IsOn = controller.Settings.AllowCloudAudioUpload;
        BaseUrlBox.Text = controller.Settings.AsrBaseUrl;
        ModelBox.Text = controller.Settings.AsrModel;
        AdvancedExpander.IsExpanded = controller.Settings.AsrProvider == AsrProvider.Custom;
        ProtocolBox.IsEnabled = controller.Settings.AsrProvider == AsrProvider.Custom;
        SelectProtocol(controller.Settings.AsrProtocol);
        HeaderEditor.Configure(controller, HeaderEditorTarget.Asr, commitImmediately: false);
    }

    public bool Validate() => HeaderEditor.Validate();

    public void Commit()
    {
        _controller.Settings.AsrApiKey = ApiKeyBox.Password;
        _controller.Settings.AllowCloudAudioUpload = AllowUploadSwitch.IsOn;
        _controller.Settings.AsrBaseUrl = BaseUrlBox.Text.Trim();
        _controller.Settings.AsrModel = ModelBox.Text.Trim();
        if (_controller.Settings.AsrProvider == AsrProvider.Custom
            && ProtocolBox.SelectedItem is ComboBoxItem { Tag: string tag }
            && Enum.TryParse<AsrProtocol>(tag, out var protocol))
        {
            _controller.Settings.AsrProtocol = protocol;
        }
        HeaderEditor.Commit();
    }

    private void SelectProtocol(AsrProtocol protocol)
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
