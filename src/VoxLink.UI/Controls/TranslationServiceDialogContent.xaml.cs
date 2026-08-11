using Microsoft.UI.Xaml.Controls;
using VoxLink.UI.Core.Models;
using VoxLink.UI.Core.ViewModels;

namespace VoxLink.UI.Controls;

public sealed partial class TranslationServiceDialogContent : UserControl
{
    private readonly AppController _controller;

    public TranslationServiceDialogContent(AppController controller)
    {
        _controller = controller;
        InitializeComponent();
        ApiKeyBox.Password = controller.Settings.TranslationApiKey;
        BaseUrlBox.Text = controller.Settings.TranslationBaseUrl;
        ModelBox.Text = controller.Settings.TranslationModel;
        RefinementSwitch.IsOn = controller.Settings.EnableTranslationRefinement;
        RefinementPromptBox.Text = controller.Settings.TranslationRefinementPrompt;
        AdvancedExpander.IsExpanded = controller.Settings.TranslationBackend is
            TranslationBackend.OpenAiCompatible or TranslationBackend.Custom;
        HeaderEditor.Configure(controller, HeaderEditorTarget.Translation, commitImmediately: false);
    }

    public bool Validate() => HeaderEditor.Validate();

    public void Commit()
    {
        _controller.Settings.TranslationApiKey = ApiKeyBox.Password;
        _controller.Settings.TranslationBaseUrl = BaseUrlBox.Text.Trim();
        _controller.Settings.TranslationModel = ModelBox.Text.Trim();
        _controller.Settings.EnableTranslationRefinement = RefinementSwitch.IsOn;
        _controller.Settings.TranslationRefinementPrompt = RefinementPromptBox.Text.Trim();
        HeaderEditor.Commit();
    }
}
